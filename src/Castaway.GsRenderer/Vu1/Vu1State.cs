using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
    public long TotalXgKickCallbackTicks { get; set; }
    public long TotalProgramsExecuted { get; set; }
    public bool EnableProfiling { get; set; }

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
        Q = val;
        PendingQ = val;
        HasPendingQ = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QueueP(float val, int latency)
    {
        P = val;
        PendingP = val;
        HasPendingP = false;
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
    public void WriteAccNoFlags(Vector128<float> newVal, uint destMask)
        => ACC = Ps2Vu0.BlendMask(ACC, newVal, destMask);

    private static readonly Vector128<uint> ExpMaskVec = Vector128.Create(Ps2Float.ExpMask);
    private static readonly Vector128<uint> SignMaskVec = Vector128.Create(Ps2Float.SignMask);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMacAndStatusFlags(Vector128<float> res, uint destMask)
    {
        Vector128<uint> rBits = res.AsUInt32();
        Vector128<uint> exp = rBits & ExpMaskVec;
        Vector128<uint> isZero = Vector128.Equals(exp, Vector128<uint>.Zero);
        Vector128<uint> sign = Vector128.AndNot(rBits, isZero) & SignMaskVec;

        uint zeroBits = ((uint)(0xF7B3D591E6A2C480UL >> ((int)isZero.AsSingle().ExtractMostSignificantBits() << 2))) & destMask;
        uint signBits = ((uint)(0xF7B3D591E6A2C480UL >> ((int)sign.AsSingle().ExtractMostSignificantBits() << 2))) & destMask;

        ushort mac = (ushort)(zeroBits | (signBits << 4));
        MacFlags = mac;

        ushort st = (ushort)(StatusFlags & 0xFC0u); // Keep sticky flags
        if (zeroBits != 0) st |= 0x041; // Z / ZS
        if (signBits != 0) st |= 0x082; // S / SS
        StatusFlags = st;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ComputeMacFlagsOnly(Vector128<float> res, uint destMask)
    {
        Vector128<uint> rBits = res.AsUInt32();
        Vector128<uint> exp = rBits & ExpMaskVec;
        Vector128<uint> isZero = Vector128.Equals(exp, Vector128<uint>.Zero);
        Vector128<uint> sign = Vector128.AndNot(rBits, isZero) & SignMaskVec;

        uint zeroBits = ((uint)(0xF7B3D591E6A2C480UL >> ((int)isZero.AsSingle().ExtractMostSignificantBits() << 2))) & destMask;
        uint signBits = ((uint)(0xF7B3D591E6A2C480UL >> ((int)sign.AsSingle().ExtractMostSignificantBits() << 2))) & destMask;

        return (ushort)(zeroBits | (signBits << 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ComputeMacFlagsW(Vector128<float> res)
    {
        uint bits = res.AsUInt32().GetElement(3);
        return (bits & 0x7F800000u) == 0u ? (ushort)0x01 : (ushort)((bits >> 27) & 0x10u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMacFlagsOnly(Vector128<float> res, uint destMask)
    {
        MacFlags = ComputeMacFlagsOnly(res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> LoadQuadword(int qwordAddr)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        return Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DataMem), byteAddr));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreQuadword(int qwordAddr, Vector128<float> val, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        ref Vector128<float> dst = ref Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DataMem), byteAddr));
        dst = destMask == 0xFu ? val : Ps2Vu0.BlendMask(dst, val, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort LoadInt16(int qwordAddr, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        int lane = (destMask & 8u) != 0 ? 0 : (destMask & 4u) != 0 ? 1 : (destMask & 2u) != 0 ? 2 : 3;
        return Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DataMem), byteAddr + (lane << 2)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StoreInt16(int qwordAddr, ushort val, uint destMask)
    {
        int byteAddr = (qwordAddr & 0x3FF) << 4;
        ref byte dm = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DataMem), byteAddr);
        uint word = val;
        if ((destMask & 8u) != 0) Unsafe.WriteUnaligned(ref dm, word);
        if ((destMask & 4u) != 0) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, 4), word);
        if ((destMask & 2u) != 0) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, 8), word);
        if ((destMask & 1u) != 0) Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, 12), word);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecClip(Vector128<float> fs, Vector128<float> ft)
    {
        Vector128<uint> su = fs.AsUInt32();
        Vector128<uint> nonZeroExp = Vector128.GreaterThan(su & ExpMaskVec, Vector128<uint>.Zero);
        Vector128<int> s = (su & nonZeroExp).AsInt32();
        Vector128<int> negS = ((su ^ SignMaskVec) & nonZeroExp).AsInt32();

        uint rawW = ft.AsUInt32().GetElement(3);
        int value = (rawW & 0x7F800000u) != 0u ? (int)(rawW & 0x7FFFFFFFu) : 0;
        Vector128<int> valVec = Vector128.Create(value);

        uint posMask = Vector128.GreaterThan(s, valVec).AsSingle().ExtractMostSignificantBits();
        uint negMask = Vector128.GreaterThan(negS, valVec).AsSingle().ExtractMostSignificantBits();

        // Map lane 0 -> bit 0/1, lane 1 -> bit 2/3, lane 2 -> bit 4/5
        uint flags = ((posMask & 1u) | ((posMask & 2u) << 1) | ((posMask & 4u) << 2))
                   | (((negMask & 1u) << 1) | ((negMask & 2u) << 2) | ((negMask & 4u) << 3));

        ClipFlags = ((ClipFlags << 6) | flags) & 0x00FFFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float VuDivFast(float a, float b)
    {
        uint ub = BitConverter.SingleToUInt32Bits(b);
        if (((ub & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return VuDivSlow(a, b, ub);
        float r = a / b;
        uint ur = BitConverter.SingleToUInt32Bits(r);
        if (((ur & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return ClampVuResultSlow(ur);
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float VuDivSlow(float a, float b, uint ub)
    {
        uint expB = ub & Ps2Float.ExpMask;
        if (expB == 0u)
        {
            uint ua = BitConverter.SingleToUInt32Bits(a);
            return BitConverter.UInt32BitsToSingle(((ua ^ ub) & Ps2Float.SignMask) | Ps2Float.IeeeMaxNormalBits);
        }
        b = BitConverter.UInt32BitsToSingle((ub & Ps2Float.SignMask) | Ps2Float.IeeeMaxNormalBits);
        float r = a / b;
        uint ur = BitConverter.SingleToUInt32Bits(r);
        if (((ur & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return ClampVuResultSlow(ur);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float VuRsqrtFast(float a, float b)
    {
        uint ub = BitConverter.SingleToUInt32Bits(b);
        if (((ub & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return VuRsqrtSlow(a, b, ub);
        float r = a / MathF.Sqrt(MathF.Abs(b));
        uint ur = BitConverter.SingleToUInt32Bits(r);
        if (((ur & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return ClampVuResultSlow(ur);
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float VuRsqrtSlow(float a, float b, uint ub)
    {
        uint expB = ub & Ps2Float.ExpMask;
        if (expB == 0u)
        {
            uint ua = BitConverter.SingleToUInt32Bits(a);
            return (ua & Ps2Float.ExpMask) == 0u
                ? BitConverter.UInt32BitsToSingle(ua & Ps2Float.SignMask)
                : BitConverter.UInt32BitsToSingle((ua & Ps2Float.SignMask) | Ps2Float.IeeeMaxNormalBits);
        }
        b = BitConverter.UInt32BitsToSingle(Ps2Float.IeeeMaxNormalBits);
        float r = a / MathF.Sqrt(b);
        uint ur = BitConverter.SingleToUInt32Bits(r);
        if (((ur & Ps2Float.ExpMask) - 0x00800000u) >= 0x7F000000u)
            return ClampVuResultSlow(ur);
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float ClampVuResultSlow(uint ur)
    {
        if ((ur & Ps2Float.ExpMask) == Ps2Float.ExpMask)
            return BitConverter.UInt32BitsToSingle((ur & Ps2Float.SignMask) | Ps2Float.IeeeMaxNormalBits);
        return BitConverter.UInt32BitsToSingle(ur & Ps2Float.SignMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecItof(Vector128<float> fs, int shift)
    {
        Vector128<float> f = Vector128.ConvertToSingle(fs.AsInt32());
        return shift == 0 ? f : (f * Vector128.Create(1.0f / (1 << shift)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecFtoi0(Vector128<float> fs)
    {
        if (Sse2.IsSupported)
        {
            Vector128<int> ints = Sse2.ConvertToVector128Int32WithTruncation(fs);
            Vector128<int> isIndef = Vector128.Equals(ints, Vector128.Create(int.MinValue));
            Vector128<int> posOverflow = Vector128.AndNot(isIndef, Vector128.ShiftRightArithmetic(fs.AsInt32(), 31));
            return (ints + posOverflow).AsSingle();
        }
        return ExecFtoiSlow(fs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecFtoi(Vector128<float> fs, int shift)
    {
        Vector128<float> scaled = shift == 0 ? fs : (fs * Vector128.Create((float)(1 << shift)));
        if (Sse2.IsSupported)
        {
            Vector128<int> ints = Sse2.ConvertToVector128Int32WithTruncation(scaled);
            Vector128<int> isIndef = Vector128.Equals(ints, Vector128.Create(int.MinValue));
            Vector128<int> posOverflow = Vector128.AndNot(isIndef, Vector128.ShiftRightArithmetic(scaled.AsInt32(), 31));
            return (ints + posOverflow).AsSingle();
        }
        return ExecFtoiSlow(scaled);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<float> ExecFtoiSlow(Vector128<float> scaled)
    {
        return Vector128.Create(
            Ps2Float.CvtWS(scaled.GetElement(0)),
            Ps2Float.CvtWS(scaled.GetElement(1)),
            Ps2Float.CvtWS(scaled.GetElement(2)),
            Ps2Float.CvtWS(scaled.GetElement(3))).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecAbs(Vector128<float> fs)
        => (fs.AsUInt32() & Vector128.Create(0x7FFFFFFFu)).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecMr32(Vector128<float> fs)
        => Vector128.Shuffle(fs, Vector128.Create(1, 2, 3, 0));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecOpmula(Vector128<float> fs, Vector128<float> ft, bool updateFlags = true)
    {
        var fsYzx = Vector128.Shuffle(fs, Vector128.Create(1, 2, 0, 3));
        var ftZxy = Vector128.Shuffle(ft, Vector128.Create(2, 0, 1, 3));
        var prod = Ps2Float.VecMul(fsYzx, ftZxy);
        if (updateFlags) WriteAcc(prod, 0xEu);
        else WriteAccNoFlags(prod, 0xEu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecOpmula(ref Vector128<float> acc, Vector128<float> fs, Vector128<float> ft, bool updateFlags = true)
    {
        var fsYzx = Vector128.Shuffle(fs, Vector128.Create(1, 2, 0, 3));
        var ftZxy = Vector128.Shuffle(ft, Vector128.Create(2, 0, 1, 3));
        var prod = Ps2Float.VecMul(fsYzx, ftZxy);
        if (updateFlags) UpdateMacAndStatusFlags(prod, 0xEu);
        acc = Ps2Vu0.BlendMask(acc, prod, 0xEu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<float> ExecOpmsub(Vector128<float> fs, Vector128<float> ft)
        => ExecOpmsub(ACC, fs, ft);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecOpmsub(Vector128<float> acc, Vector128<float> fs, Vector128<float> ft)
    {
        var fsYzx = Vector128.Shuffle(fs, Vector128.Create(1, 2, 0, 3));
        var ftZxy = Vector128.Shuffle(ft, Vector128.Create(2, 0, 1, 3));
        return Ps2Float.VecSub(acc, Ps2Float.VecMul(fsYzx, ftZxy));
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecOpmulaFast(ref Vector128<float> acc, Vector128<float> fs, Vector128<float> ft, bool updateFlags = true)
    {
        var fsYzx = Vector128.Shuffle(fs, Vector128.Create(1, 2, 0, 3));
        var ftZxy = Vector128.Shuffle(ft, Vector128.Create(2, 0, 1, 3));
        var prod = fsYzx * ftZxy;
        if (updateFlags) UpdateMacAndStatusFlags(prod, 0xEu);
        acc = Ps2Vu0.BlendMask(acc, prod, 0xEu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ExecOpmsubFast(Vector128<float> acc, Vector128<float> fs, Vector128<float> ft)
    {
        var fsYzx = Vector128.Shuffle(fs, Vector128.Create(1, 2, 0, 3));
        var ftZxy = Vector128.Shuffle(ft, Vector128.Create(2, 0, 1, 3));
        return acc - (fsYzx * ftZxy);
    }

    /// <summary>
    /// Executes an XGKICK from VU1 DataMem at quadword address VI[isReg] &amp; 0x3FF,
    /// walking GIFTags until EOP and forwarding the complete packet to GIF Path 1.
    /// </summary>
    public void ExecXgKick(int isReg) => ExecXgKickAddr(ReadVi(isReg));

    /// <summary>
    /// Executes an XGKICK from VU1 DataMem at quadword address startQwordRaw &amp; 0x3FF.
    /// </summary>
    public void ExecXgKickAddr(ushort startQwordRaw)
    {
        TotalXgKicksExecuted++;
        if (OnXgKick == null)
            return;

        int startQword = startQwordRaw & 0x3FF;
        int curQword = startQword;
        int totalQwords = 0;
        const int maxQwords = 1024;
        ref byte dmBase = ref MemoryMarshal.GetArrayDataReference(DataMem);

        // Calculate total quadwords in the GIF packet chain up to EOP
        while (totalQwords < maxQwords)
        {
            int tagByteOff = (curQword & 0x3FF) << 4;
            ulong tagLo = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dmBase, tagByteOff));

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
        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
        bool sampling = RuntimeDispatcher.SamplingActive;
        int prevPhase = sampling ? RuntimeDispatcher.ActiveProfilePhase : 0;
        if (sampling) RuntimeDispatcher.ActiveProfilePhase = 3;
        try
        {
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
        finally
        {
            if (sampling) RuntimeDispatcher.ActiveProfilePhase = prevPhase;
        }

        if (t0 != 0)
            TotalXgKickCallbackTicks += Stopwatch.GetTimestamp() - t0;
    }
}
