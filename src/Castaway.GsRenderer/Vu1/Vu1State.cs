using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Castaway.Core;

namespace Castaway.GsRenderer.Vu1;

/// <summary>
/// Complete Sony PS2 Vector Unit 1 (VU1) architectural state, 16 KiB MicroMem, 16 KiB DataMem,
/// and SIMD execution primitives used by compiled AOT/dynamic VU1 routines.
/// </summary>
public sealed class Vu1State
{
    public const int MicroMemSize = 16 * 1024;
    public const int DataMemSize = 16 * 1024;
    public const int MicroMemMask = MicroMemSize - 1;
    public const int DataMemMask = DataMemSize - 1;

    public readonly Vector128<float>[] VF = new Vector128<float>[32];
    public readonly ushort[] VI = new ushort[16];

    public Vector128<float> ACC;
    public float Q = 1.0f;
    public float P = 0.0f;
    public float I = 0.0f;
    public uint R = 0x3F800000u;

    public ushort MacFlags;
    public ushort StatusFlags;
    public uint ClipFlags;

    public ushort Top;
    public ushort Itop;
    public ushort Tpc;
    public ushort CurrentProgramStart;

    public int LastWrittenViReg;
    public ushort LastWrittenViOldVal;
    public int LastWrittenAnyViReg;

    // Background FDIV (Q) & EFU (P) pipelines
    public bool HasPendingQ;
    public float PendingQ = 1.0f;
    public int PendingQReadyCycle;

    public bool HasPendingP;
    public float PendingP;
    public int PendingPReadyCycle;

    public int CurrentCycle;

    public byte[] MicroMem { get; }
    public byte[] DataMem { get; }

    /// <summary>
    /// Callback invoked when XGKICK dispatches a GIF packet from VU1 DataMem (GIF Path 1).
    /// </summary>
    public Action<ReadOnlySpan<byte>>? OnXgKick { get; set; }

    public long TotalInstructionsExecuted { get; set; }
    public long TotalXgKicksExecuted { get; private set; }
    public long TotalExecutionTicks { get; set; }
    public long TotalProgramsExecuted { get; set; }

    public Vu1State(byte[]? microMem = null, byte[]? dataMem = null)
    {
        MicroMem = microMem ?? new byte[MicroMemSize];
        DataMem = dataMem ?? new byte[DataMemSize];
        Reset();
    }

    public void Reset()
    {
        Array.Clear(VF);
        VF[0] = Vector128.Create(0.0f, 0.0f, 0.0f, 1.0f);
        Array.Clear(VI);
        ACC = Vector128<float>.Zero;
        Q = 1.0f;
        P = 0.0f;
        I = 0.0f;
        R = 0x3F800000u;
        MacFlags = 0;
        StatusFlags = 0;
        ClipFlags = 0;
        Top = 0;
        Itop = 0;
        Tpc = 0;
        LastWrittenViReg = 0;
        LastWrittenViOldVal = 0;
        LastWrittenAnyViReg = 0;
        HasPendingQ = false;
        PendingQ = 1.0f;
        PendingQReadyCycle = 0;
        HasPendingP = false;
        PendingP = 0.0f;
        PendingPReadyCycle = 0;
        CurrentCycle = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceCycle()
    {
        CurrentCycle++;
        if (HasPendingQ && CurrentCycle >= PendingQReadyCycle)
        {
            Q = PendingQ;
            HasPendingQ = false;
        }
        if (HasPendingP && CurrentCycle >= PendingPReadyCycle)
        {
            P = PendingP;
            HasPendingP = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WaitQ()
    {
        if (HasPendingQ)
        {
            Q = PendingQ;
            HasPendingQ = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WaitP()
    {
        if (HasPendingP)
        {
            P = PendingP;
            HasPendingP = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QueueQ(float val, int latency)
    {
        if (HasPendingQ)
        {
            Q = PendingQ;
            CurrentCycle = Math.Max(CurrentCycle, PendingQReadyCycle);
        }
        PendingQ = val;
        PendingQReadyCycle = CurrentCycle + latency;
        HasPendingQ = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QueueP(float val, int latency)
    {
        if (HasPendingP)
        {
            P = PendingP;
            CurrentCycle = Math.Max(CurrentCycle, PendingPReadyCycle);
        }
        PendingP = val;
        PendingPReadyCycle = CurrentCycle + latency;
        HasPendingP = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FlushPipelines()
    {
        WaitQ();
        WaitP();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetViHazard()
    {
        LastWrittenViReg = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> ReadVf(int reg)
        => reg == 0 ? Vector128.Create(0.0f, 0.0f, 0.0f, 1.0f) : VF[reg & 31];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadVi(int reg)
        => reg == 0 ? (ushort)0 : VI[reg & 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadViSigned(int reg)
        => reg == 0 ? (short)0 : (short)VI[reg & 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadViForBranch(int reg)
        => reg == 0 ? (ushort)0 : VI[reg & 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadViSignedForBranch(int reg)
        => reg == 0 ? (short)0 : (short)VI[reg & 15];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVi(int reg, ushort val)
    {
        int r = reg & 15;
        if (r != 0)
        {
            LastWrittenAnyViReg = r;
            VI[r] = val;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteViWithHazard(int reg, ushort val) => WriteVi(reg, val);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVf(int reg, Vector128<float> newVal, uint destMask)
    {
        UpdateMacAndStatusFlags(newVal, destMask);
        if ((reg & 31) == 0)
            return;
        VF[reg & 31] = Ps2Vu0.BlendMask(VF[reg & 31], newVal, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVfNoFlags(int reg, Vector128<float> newVal, uint destMask)
    {
        if ((reg & 31) == 0)
            return;
        VF[reg & 31] = Ps2Vu0.BlendMask(VF[reg & 31], newVal, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteAcc(Vector128<float> newVal, uint destMask)
    {
        UpdateMacAndStatusFlags(newVal, destMask);
        ACC = Ps2Vu0.BlendMask(ACC, newVal, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMacAndStatusFlags(Vector128<float> res, uint destMask)
    {
        ushort mac = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            uint laneBit = (uint)(1 << (3 - lane));
            if ((destMask & laneBit) == 0)
                continue;

            uint bits = res.AsUInt32().GetElement(lane);
            uint exp = bits & Ps2Float.ExpMask;
            uint mag = bits & Ps2Float.MagMask;

            // Zero flag (bits 0..3: W=bit 0, Z=bit 1, Y=bit 2, X=bit 3)
            if (mag == 0u || exp == 0u)
                mac |= (ushort)laneBit;

            // Sign flag (bits 4..7)
            if ((bits & Ps2Float.SignMask) != 0 && exp != 0u)
                mac |= (ushort)(laneBit << 4);
        }

        MacFlags = mac;

        ushort st = (ushort)(StatusFlags & 0xFC0u); // Keep sticky flags
        if ((mac & 0x000Fu) != 0) st |= 0x041; // Z / ZS
        if ((mac & 0x00F0u) != 0) st |= 0x082; // S / SS
        StatusFlags = st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> LoadQuadword(int qwordAddr)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        return Unsafe.ReadUnaligned<Vector128<float>>(ref DataMem[byteAddr]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreQuadword(int qwordAddr, Vector128<float> val, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        var oldVal = Unsafe.ReadUnaligned<Vector128<float>>(ref DataMem[byteAddr]);
        var merged = Ps2Vu0.BlendMask(oldVal, val, destMask);
        Unsafe.WriteUnaligned(ref DataMem[byteAddr], merged);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort LoadInt16(int qwordAddr, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        int lane = (destMask & 8u) != 0 ? 0 : (destMask & 4u) != 0 ? 1 : (destMask & 2u) != 0 ? 2 : 3;
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(DataMem.AsSpan(byteAddr + lane * 4, 4));
        return (ushort)word;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreInt16(int qwordAddr, ushort val, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        uint word = val;
        for (int lane = 0; lane < 4; lane++)
        {
            if ((destMask & (1u << (3 - lane))) != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(DataMem.AsSpan(byteAddr + lane * 4, 4), word);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecClip(Vector128<float> fs, Vector128<float> ft)
    {
        var s = fs.AsInt32();
        var t = ft.AsInt32();
        int value = t.GetElement(3);
        value = (value & 0x7F800000) != 0 ? (value & 0x7FFFFFFF) : 0x007FFFFF;

        uint flags = 0u;
        if ((s.GetElement(0) ^ 0) > value) flags |= 0x01u;
        if (unchecked((int)((uint)s.GetElement(0) ^ 0x80000000u)) > value) flags |= 0x02u;
        if ((s.GetElement(1) ^ 0) > value) flags |= 0x04u;
        if (unchecked((int)((uint)s.GetElement(1) ^ 0x80000000u)) > value) flags |= 0x08u;
        if ((s.GetElement(2) ^ 0) > value) flags |= 0x10u;
        if (unchecked((int)((uint)s.GetElement(2) ^ 0x80000000u)) > value) flags |= 0x20u;

        ClipFlags = ((ClipFlags << 6) | flags) & 0x00FFFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecItof(Vector128<float> fs, int shift)
    {
        var s = fs.AsInt32();
        float scale = shift == 0 ? 1.0f : (1.0f / (1 << shift));
        return Vector128.Create(
            s.GetElement(0) * scale,
            s.GetElement(1) * scale,
            s.GetElement(2) * scale,
            s.GetElement(3) * scale);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecFtoi(Vector128<float> fs, int shift)
    {
        float scale = shift == 0 ? 1.0f : (float)(1 << shift);
        return Vector128.Create(
            Ps2Float.CvtWS(fs.GetElement(0) * scale),
            Ps2Float.CvtWS(fs.GetElement(1) * scale),
            Ps2Float.CvtWS(fs.GetElement(2) * scale),
            Ps2Float.CvtWS(fs.GetElement(3) * scale)).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecAbs(Vector128<float> fs)
        => (fs.AsUInt32() & Vector128.Create(0x7FFFFFFFu)).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecMr32(Vector128<float> fs)
        => Vector128.Create(fs.GetElement(1), fs.GetElement(2), fs.GetElement(3), fs.GetElement(0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecOpmula(Vector128<float> fs, Vector128<float> ft)
    {
        var fsYzx = Vector128.Create(fs.GetElement(1), fs.GetElement(2), fs.GetElement(0), fs.GetElement(3));
        var ftZxy = Vector128.Create(ft.GetElement(2), ft.GetElement(0), ft.GetElement(1), ft.GetElement(3));
        WriteAcc(Ps2Float.VecMul(fsYzx, ftZxy), 0xEu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> ExecOpmsub(Vector128<float> fs, Vector128<float> ft)
    {
        var fsYzx = Vector128.Create(fs.GetElement(1), fs.GetElement(2), fs.GetElement(0), fs.GetElement(3));
        var ftZxy = Vector128.Create(ft.GetElement(2), ft.GetElement(0), ft.GetElement(1), ft.GetElement(3));
        var prod = Ps2Float.VecMul(fsYzx, ftZxy);
        return Ps2Float.VecSub(ACC, prod);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecRinit(Vector128<float> fs, int fsf)
    {
        uint bits = fs.AsUInt32().GetElement(fsf & 3);
        R = 0x3F800000u | (bits & 0x007FFFFFu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> ExecRget()
        => Vector128.Create(BitConverter.UInt32BitsToSingle(R));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> ExecRnext()
    {
        uint x = (R >> 4) & 1u;
        uint y = (R >> 22) & 1u;
        R = 0x3F800000u | (((R << 1) ^ (x ^ y)) & 0x007FFFFFu);
        return Vector128.Create(BitConverter.UInt32BitsToSingle(R));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecRxor(Vector128<float> fs, int fsf)
    {
        uint bits = fs.AsUInt32().GetElement(fsf & 3) & 0x007FFFFFu;
        R = 0x3F800000u | ((R ^ bits) & 0x007FFFFFu);
    }

    /// <summary>
    /// Executes an XGKICK from VU1 DataMem at quadword address VI[isReg] &amp; 0x3FF,
    /// walking GIFTags until EOP and forwarding the complete packet to GIF Path 1.
    /// </summary>
    public void ExecXgKick(int isReg)
    {
        TotalXgKicksExecuted++;
        if (OnXgKick == null)
            return;

        int startQword = ReadVi(isReg) & 0x3FF;
        int curQword = startQword;
        int totalQwords = 0;
        const int maxQwords = 1024;

        // Calculate total quadwords in the GIF packet chain up to EOP
        while (totalQwords < maxQwords)
        {
            int tagByteOff = (curQword & 0x3FF) << 4;
            ulong tagLo = BinaryPrimitives.ReadUInt64LittleEndian(DataMem.AsSpan(tagByteOff, 8));

            int nloop = (int)(tagLo & 0x7FFFul);
            bool eop = ((tagLo >> 15) & 1ul) != 0;
            int flg = (int)((tagLo >> 58) & 0x3ul);
            int nreg = (int)((tagLo >> 60) & 0xFul);
            if (nreg == 0) nreg = 16;

            int payloadQwords = flg switch
            {
                0 => nloop * nreg,             // PACKED
                1 => (nloop * nreg + 1) >> 1,  // REGLIST
                _ => nloop                     // IMAGE
            };

            int packetStep = 1 + payloadQwords;
            totalQwords += packetStep;
            curQword = (curQword + packetStep) & 0x3FF;

            if (eop)
                break;
        }

        if (totalQwords <= 0)
            return;

        totalQwords = Math.Min(totalQwords, maxQwords);
        int byteCount = totalQwords << 4;

        if (startQword + totalQwords <= 1024)
        {
            OnXgKick(DataMem.AsSpan(startQword << 4, byteCount));
        }
        else
        {
            byte[] wrapped = new byte[byteCount];
            for (int q = 0; q < totalQwords; q++)
            {
                int srcOff = ((startQword + q) & 0x3FF) << 4;
                Buffer.BlockCopy(DataMem, srcOff, wrapped, q << 4, 16);
            }
            OnXgKick(wrapped);
        }
    }
}
