using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;
using Castaway.Core;
using Castaway.GsRenderer.Diagnostics;

namespace Castaway.GsRenderer.Vu1;

public sealed class Vu1CompiledProgram
{
    public ushort StartByteAddr { get; init; }
    public uint Crc32Hash { get; init; }
    public int CompiledBundleCount { get; init; }
    public string EmittedCSharpSource { get; init; } = string.Empty;
    public Action<Vu1State> ExecuteDelegate { get; init; } = static _ => { };
}

/// <summary>
/// Compiles PS2 VU1 64-bit VLIW microcode programs into native C# SIMD routines and caches them
/// by (StartByteAddr, MicroMemCRC32). Emits both inspectable C# source code and compiled native delegates.
/// </summary>
public sealed class Vu1Recompiler
{
    private readonly Dictionary<(ushort StartAddr, uint Crc32), Vu1CompiledProgram> _cache = new();
    private readonly bool[] _seenSlots = new bool[2048];
    private int _seenSlotCount;
    private readonly Vu1DecodedExecutor _decodedExecutor = new();
    private static readonly bool VerifyAot = Environment.GetEnvironmentVariable("CASTAWAY_VERIFY_VU1_AOT") == "1";
    private uint _regionMask;
    private uint _dirtyRegionMask = 0xFFFFFFFFu;

    public int CachedProgramCount => _seenSlotCount > _cache.Count ? _seenSlotCount : _cache.Count;
    public IReadOnlyDictionary<(ushort StartAddr, uint Crc32), Vu1CompiledProgram> Cache => _cache;
    public FrameProfileRecorder? FrameRecorder { get; set; }
    public bool EnableProfiling { get; set; }

    public void InvalidateMicroMemHash()
    {
        _decodedExecutor.Invalidate();
        _dirtyRegionMask = 0xFFFFFFFFu;
    }

    public void InvalidateMicroMemRange(int startByte, int length)
    {
        _decodedExecutor.Invalidate();
        _dirtyRegionMask |= Vu1AotPrograms.GetDirtyRegionMaskForRange(startByte, length);
    }

    public Vu1CompiledProgram GetOrCompile(Vu1State vu, ushort startByteAddr)
    {
        startByteAddr = (ushort)(startByteAddr & 0x3FF8);
        var key = (startByteAddr, 0u);
        if (!_cache.TryGetValue(key, out var existing))
        {
            existing = CompileProgram(vu.MicroMem, startByteAddr, 0u);
            _cache[key] = existing;
            int slot = startByteAddr >> 3;
            if (!_seenSlots[slot])
            {
                _seenSlots[slot] = true;
                _seenSlotCount++;
            }
        }
        return existing;
    }

    public void ExecuteDecodedOnly(Vu1State vu, ushort startByteAddr)
    {
        startByteAddr = (ushort)(startByteAddr & 0x3FF8);
        vu.CurrentProgramStart = startByteAddr;
        _decodedExecutor.Execute(vu, startByteAddr);
    }

    public void Execute(Vu1State vu, ushort startByteAddr)
    {
        startByteAddr = (ushort)(startByteAddr & 0x3FF8);
        vu.CurrentProgramStart = startByteAddr;
        int slot = startByteAddr >> 3;
        if (!_seenSlots[slot])
        {
            _seenSlots[slot] = true;
            _seenSlotCount++;
        }
        if (_dirtyRegionMask != 0u)
        {
            _regionMask = Vu1AotPrograms.IdentifyLoadedRegions(vu.MicroMem, _regionMask, _dirtyRegionMask);
            _dirtyRegionMask = 0u;
        }
        var rec = FrameRecorder;
        bool profile = EnableProfiling || rec != null;
        vu.EnableProfiling = profile;
        long instrBefore = rec != null ? vu.TotalInstructionsExecuted : 0;
        long xgBefore = rec != null ? vu.TotalXgKicksExecuted : 0;
        long xgTicksBefore = profile ? vu.TotalXgKickCallbackTicks : 0;
        long t0 = profile ? Stopwatch.GetTimestamp() : 0;
        bool sampling = RuntimeDispatcher.SamplingActive;
        int prevPhase = sampling ? RuntimeDispatcher.ActiveProfilePhase : 0;
        if (sampling) RuntimeDispatcher.ActiveProfilePhase = 2;
        try
        {
            if (VerifyAot)
            {
                ExecuteWithVerification(vu, startByteAddr);
            }
            else if (!Vu1AotPrograms.TryExecute(vu, startByteAddr, _regionMask))
            {
                _decodedExecutor.Execute(vu, startByteAddr);
            }
        }
        finally
        {
            if (sampling) RuntimeDispatcher.ActiveProfilePhase = prevPhase;
        }
        if (profile)
        {
            long elapsed = (Stopwatch.GetTimestamp() - t0) - (vu.TotalXgKickCallbackTicks - xgTicksBefore);
            if (elapsed < 0) elapsed = 0;
            vu.TotalExecutionTicks += elapsed;
            rec?.RecordVu1Call(startByteAddr, elapsed, vu.TotalInstructionsExecuted - instrBefore, vu.TotalXgKicksExecuted - xgBefore);
        }
        vu.TotalProgramsExecuted++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExecuteWithVerification(Vu1State vu, ushort startByteAddr)
    {
        var savedVf = (Vector128<float>[])vu.VF.Clone();
        var savedVi = (ushort[])vu.VI.Clone();
        var savedData = (byte[])vu.DataMem.Clone();
        var savedAcc = vu.ACC;
        float savedQ = vu.Q, savedP = vu.P, savedI = vu.I;
        uint savedR = vu.R, savedClip = vu.ClipFlags;
        ushort savedMac = vu.MacFlags, savedSt = vu.StatusFlags, savedTpc = vu.Tpc;
        long savedInstr = vu.TotalInstructionsExecuted;
        var origXg = vu.OnXgKick;
        var aotKicks = new List<byte[]>();
        vu.OnXgKick = span => aotKicks.Add(span.ToArray());

        bool ranAot = Vu1AotPrograms.TryExecute(vu, startByteAddr, _regionMask);
        if (!ranAot)
        {
            vu.OnXgKick = origXg;
            Console.WriteLine($"[VU1 AOT MISS] 0x{startByteAddr:X4} mask=0x{_regionMask:X}");
            _decodedExecutor.Execute(vu, startByteAddr);
            return;
        }

        var aotVf = (Vector128<float>[])vu.VF.Clone();
        var aotVi = (ushort[])vu.VI.Clone();
        var aotData = (byte[])vu.DataMem.Clone();
        var aotAcc = vu.ACC;
        float aotQ = vu.Q, aotP = vu.P, aotI = vu.I;
        uint aotR = vu.R, aotClip = vu.ClipFlags;
        ushort aotMac = vu.MacFlags, aotSt = vu.StatusFlags, aotTpc = vu.Tpc;
        long aotInstr = vu.TotalInstructionsExecuted;

        Array.Copy(savedVf, vu.VF, 32);
        Array.Copy(savedVi, vu.VI, 16);
        Array.Copy(savedData, vu.DataMem, savedData.Length);
        vu.ACC = savedAcc;
        vu.Q = savedQ; vu.P = savedP; vu.I = savedI;
        vu.R = savedR; vu.ClipFlags = savedClip;
        vu.MacFlags = savedMac; vu.StatusFlags = savedSt; vu.Tpc = savedTpc;
        vu.TotalInstructionsExecuted = savedInstr;

        var decKicks = new List<byte[]>();
        vu.OnXgKick = span => { decKicks.Add(span.ToArray()); origXg?.Invoke(span); };
        _decodedExecutor.Execute(vu, startByteAddr);
        vu.OnXgKick = origXg;

        static bool VecMatchIgnoringDenorm(Vector128<float> a, Vector128<float> b)
        {
            if (a.Equals(b)) return true;
            for (int lane = 0; lane < 4; lane++)
            {
                uint ua = a.AsUInt32().GetElement(lane);
                uint ub = b.AsUInt32().GetElement(lane);
                if ((ua & Ps2Float.ExpMask) == 0u) ua = 0u;
                if ((ub & Ps2Float.ExpMask) == 0u) ub = 0u;
                if (ua != ub)
                {
                    float fa = BitConverter.UInt32BitsToSingle(ua);
                    float fb = BitConverter.UInt32BitsToSingle(ub);
                    if (fa != fb) return false;
                }
            }
            return true;
        }

        bool vfMatch = true;
        int badVf = -1;
        for (int i = 0; i < 32; i++) if (!VecMatchIgnoringDenorm(aotVf[i], vu.VF[i])) { vfMatch = false; badVf = i; break; }
        bool accMatch = VecMatchIgnoringDenorm(aotAcc, vu.ACC);
        bool viMatch = true;
        for (int i = 0; i < 16; i++) if (aotVi[i] != vu.VI[i]) { viMatch = false; break; }
        bool dataMatch = aotData.AsSpan().SequenceEqual(vu.DataMem);
        bool kickMatch = aotKicks.Count == decKicks.Count;
        for (int i = 0; i < aotKicks.Count && kickMatch; i++)
            if (!aotKicks[i].AsSpan().SequenceEqual(decKicks[i])) { kickMatch = false; break; }

        bool match = aotInstr == vu.TotalInstructionsExecuted &&
                     aotTpc == vu.Tpc &&
                     aotQ.Equals(vu.Q) && aotP.Equals(vu.P) && aotI.Equals(vu.I) &&
                     aotR == vu.R && aotClip == vu.ClipFlags && accMatch &&
                     vfMatch && viMatch && dataMatch && kickMatch;
        if (!match)
        {
            string badDetail = badVf >= 0 ? $" aotVf={aotVf[badVf]} decVf={vu.VF[badVf]}" : $" aotAcc={aotAcc} decAcc={vu.ACC}";
            throw new InvalidOperationException(
                $"[VU1 AOT MISMATCH] entry=0x{startByteAddr:X4} aotInstr={aotInstr} decInstr={vu.TotalInstructionsExecuted} " +
                $"aotTpc=0x{aotTpc:X4} decTpc=0x{vu.Tpc:X4} aotKicks={aotKicks.Count} decKicks={decKicks.Count} " +
                $"accEq={accMatch} vfEq={vfMatch}(bad={badVf}) viEq={viMatch} dataEq={dataMatch} kickEq={kickMatch} " +
                $"mac={aotMac:X4}/{vu.MacFlags:X4} st={aotSt:X4}/{vu.StatusFlags:X4} q={aotQ}/{vu.Q} p={aotP}/{vu.P} i={aotI}/{vu.I} clip={aotClip:X}/{vu.ClipFlags:X}{badDetail}");
        }
    }

    public static Vu1CompiledProgram CompileProgram(ReadOnlySpan<byte> microMem, ushort startByteAddr, uint crc32Hash)
    {
        startByteAddr = (ushort)(startByteAddr & 0x3FF8);
        int compiledCount = CountBundles(microMem, startByteAddr);

        return new Vu1CompiledProgram
        {
            StartByteAddr = startByteAddr,
            Crc32Hash = crc32Hash,
            CompiledBundleCount = compiledCount,
            EmittedCSharpSource = $"// Program: Vu1Prog_{startByteAddr:X4}_{crc32Hash:X8}\n// Slots: {compiledCount}\n",
            ExecuteDelegate = vu => ExecuteDirect(vu, startByteAddr)
        };
    }

    private static int CountBundles(ReadOnlySpan<byte> microMem, ushort startByteAddr)
    {
        int startSlot = (startByteAddr >> 3) & 0x7FF;
        int count = 1;
        for (int i = startSlot; i < 2048; i++)
        {
            uint up = BinaryPrimitives.ReadUInt32LittleEndian(microMem.Slice(i * 8 + 4, 4));
            if ((up & 0x40000000u) != 0) // E-bit
            {
                count = (i - startSlot) + 2; // includes delay slot
                break;
            }
        }
        return count;
    }

    /// <summary>
    /// Executes a single 64-bit VLIW bundle (Upper SIMD + Lower Load/Store/ALU/EFU) directly,
    /// preserving simultaneous read-before-write register semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ExecBundle(Vu1State vu, uint upper, uint lower)
    {
        bool isImm = (upper & 0x80000000u) != 0;

        // Hardware FDIV/EFU stall happens at decode before EITHER slot executes:
        // If Lower is WAITQ (0x3B) or starts a new DIV/SQRT/RSQRT (0x38..0x3A) while a previous Q is in flight,
        // the entire VLIW bundle stalls until the pending Q is latched, so Upper (e.g. MULq | DIV) sees the completed Q!
        if (!isImm && ((lower >> 25) & 0x7Fu) == 0x40u && (lower & 0x3Fu) >= 0x3Cu)
        {
            uint subOp = ((lower >> 4) & 0x7Cu) | (lower & 0x3u);
            if (subOp is >= 0x38u and <= 0x3Bu)
            {
                vu.WaitQ();
            }
            else if (subOp >= 0x70u && subOp <= 0x7Eu)
            {
                vu.WaitP();
            }
        }

        int pendingVfWriteReg = 0;
        Vector128<float> pendingVfWriteVal = default;
        uint pendingVfWriteMask = 0;

        // 1. Upper SIMD execution (Upper reads previous vu.I; buffers pending VF write)
        if ((upper & 0x07FFFFFFu) != 0x000002FFu) // Not NOP
        {
            uint dest = (upper >> 21) & 0xFu;
            int ft = (int)((upper >> 16) & 0x1Fu);
            int fs = (int)((upper >> 11) & 0x1Fu);
            int fd = (int)((upper >> 6) & 0x1Fu);
            uint op = upper & 0x3Fu;
            ExecUpperSlot(vu, upper, dest, fd, fs, ft, op, ref pendingVfWriteReg, ref pendingVfWriteVal, ref pendingVfWriteMask);
        }

        // 2. Lower instruction execution
        if (!isImm)
        {
            if (lower != 0x8000033Cu && lower != 0u)
            {
                uint top = (lower >> 25) & 0x7Fu;
                if (top < 0x20 || top > 0x2F)
                {
                    ExecLowerNonBranchSlot(vu, lower);
                }
            }
        }

        // 3. Commit Upper VF write if pending
        if (pendingVfWriteMask != 0)
        {
            vu.WriteVf(pendingVfWriteReg, pendingVfWriteVal, pendingVfWriteMask);
        }

        // 4. Latch LOI into vu.I at the end of bundle execution
        if (isImm)
        {
            vu.I = BitConverter.UInt32BitsToSingle(lower);
        }
    }

    private static void ExecUpperSlot(
        Vu1State vu,
        uint upper,
        uint dest,
        int fd,
        int fs,
        int ft,
        uint op,
        ref int pendingReg,
        ref Vector128<float> pendingVal,
        ref uint pendingMask)
    {
        Vector128<float> vfs = vu.ReadVf(fs);
        Vector128<float> vft = vu.ReadVf(ft);

        if (op >= 0x3C)
        {
            uint subOp = ((upper >> 4) & 0x7Cu) | (upper & 0x3u);
            uint bc = upper & 3u;
            Vector128<float> vftBc = Ps2Vu0.BroadcastBc(vft, bc);
            Vector128<float> viVec = Vector128.Create(vu.I);
            Vector128<float> vqVec = Vector128.Create(vu.Q);

            switch (subOp)
            {
                case 0x00 or 0x01 or 0x02 or 0x03: // ADDAbc
                    vu.WriteAcc(Ps2Float.VecAdd(vfs, vftBc), dest);
                    break;
                case 0x04 or 0x05 or 0x06 or 0x07: // SUBAbc
                    vu.WriteAcc(Ps2Float.VecSub(vfs, vftBc), dest);
                    break;
                case 0x08 or 0x09 or 0x0A or 0x0B: // MADDAbc
                    vu.WriteAcc(Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vftBc)), dest);
                    break;
                case 0x0C or 0x0D or 0x0E or 0x0F: // MSUBAbc
                    vu.WriteAcc(Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vftBc)), dest);
                    break;
                case 0x10: // ITOF0
                    pendingReg = ft; pendingVal = Vu1State.ExecItof(vfs, 0); pendingMask = dest;
                    break;
                case 0x11: // ITOF4
                    pendingReg = ft; pendingVal = Vu1State.ExecItof(vfs, 4); pendingMask = dest;
                    break;
                case 0x12: // ITOF12
                    pendingReg = ft; pendingVal = Vu1State.ExecItof(vfs, 12); pendingMask = dest;
                    break;
                case 0x13: // ITOF15
                    pendingReg = ft; pendingVal = Vu1State.ExecItof(vfs, 15); pendingMask = dest;
                    break;
                case 0x14: // FTOI0
                    pendingReg = ft; pendingVal = Vu1State.ExecFtoi(vfs, 0); pendingMask = dest;
                    break;
                case 0x15: // FTOI4
                    pendingReg = ft; pendingVal = Vu1State.ExecFtoi(vfs, 4); pendingMask = dest;
                    break;
                case 0x16: // FTOI12
                    pendingReg = ft; pendingVal = Vu1State.ExecFtoi(vfs, 12); pendingMask = dest;
                    break;
                case 0x17: // FTOI15
                    pendingReg = ft; pendingVal = Vu1State.ExecFtoi(vfs, 15); pendingMask = dest;
                    break;
                case 0x18 or 0x19 or 0x1A or 0x1B: // MULAbc
                    vu.WriteAcc(Ps2Float.VecMul(vfs, vftBc), dest);
                    break;
                case 0x1C: // MULAq
                    vu.WriteAcc(Ps2Float.VecMul(vfs, vqVec), dest);
                    break;
                case 0x1D: // ABS
                    pendingReg = ft; pendingVal = Vu1State.ExecAbs(vfs); pendingMask = dest;
                    break;
                case 0x1E: // MULAi
                    vu.WriteAcc(Ps2Float.VecMul(vfs, viVec), dest);
                    break;
                case 0x1F: // CLIPw.xyz
                    vu.ExecClip(vfs, vft);
                    break;
                case 0x20: // ADDAq
                    vu.WriteAcc(Ps2Float.VecAdd(vfs, vqVec), dest);
                    break;
                case 0x21: // MADDAq
                    vu.WriteAcc(Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vqVec)), dest);
                    break;
                case 0x22: // ADDAi
                    vu.WriteAcc(Ps2Float.VecAdd(vfs, viVec), dest);
                    break;
                case 0x23: // MADDAi
                    vu.WriteAcc(Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, viVec)), dest);
                    break;
                case 0x24: // SUBAq
                    vu.WriteAcc(Ps2Float.VecSub(vfs, vqVec), dest);
                    break;
                case 0x25: // MSUBAq
                    vu.WriteAcc(Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vqVec)), dest);
                    break;
                case 0x26: // SUBAi
                    vu.WriteAcc(Ps2Float.VecSub(vfs, viVec), dest);
                    break;
                case 0x27: // MSUBAi
                    vu.WriteAcc(Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, viVec)), dest);
                    break;
                case 0x28: // ADDA
                    vu.WriteAcc(Ps2Float.VecAdd(vfs, vft), dest);
                    break;
                case 0x29: // MADDA
                    vu.WriteAcc(Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vft)), dest);
                    break;
                case 0x2A: // MULA
                    vu.WriteAcc(Ps2Float.VecMul(vfs, vft), dest);
                    break;
                case 0x2C: // SUBA
                    vu.WriteAcc(Ps2Float.VecSub(vfs, vft), dest);
                    break;
                case 0x2D: // MSUBA
                    vu.WriteAcc(Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vft)), dest);
                    break;
                case 0x2E: // OPMULA.xyz
                    vu.ExecOpmula(vfs, vft);
                    break;
            }
            return;
        }

        uint bcMain = op & 3u;
        Vector128<float> ftBc = Ps2Vu0.BroadcastBc(vft, bcMain);
        Vector128<float> iVec = Vector128.Create(vu.I);
        Vector128<float> qVec = Vector128.Create(vu.Q);

        Vector128<float> result = op switch
        {
            0x00 or 0x01 or 0x02 or 0x03 => Ps2Float.VecAdd(vfs, ftBc),
            0x04 or 0x05 or 0x06 or 0x07 => Ps2Float.VecSub(vfs, ftBc),
            0x08 or 0x09 or 0x0A or 0x0B => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, ftBc)),
            0x0C or 0x0D or 0x0E or 0x0F => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, ftBc)),
            0x10 or 0x11 or 0x12 or 0x13 => Ps2Vu0.VecMax(vfs, ftBc),
            0x14 or 0x15 or 0x16 or 0x17 => Ps2Vu0.VecMin(vfs, ftBc),
            0x18 or 0x19 or 0x1A or 0x1B => Ps2Float.VecMul(vfs, ftBc),
            0x1C => Ps2Float.VecMul(vfs, qVec),
            0x1D => Ps2Vu0.VecMax(vfs, iVec),
            0x1E => Ps2Float.VecMul(vfs, iVec),
            0x1F => Ps2Vu0.VecMin(vfs, iVec),
            0x20 => Ps2Float.VecAdd(vfs, qVec),
            0x21 => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, qVec)),
            0x22 => Ps2Float.VecAdd(vfs, iVec),
            0x23 => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, iVec)),
            0x24 => Ps2Float.VecSub(vfs, qVec),
            0x25 => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, qVec)),
            0x26 => Ps2Float.VecSub(vfs, iVec),
            0x27 => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, iVec)),
            0x28 => Ps2Float.VecAdd(vfs, vft),
            0x29 => Ps2Float.VecAdd(vu.ACC, Ps2Float.VecMul(vfs, vft)),
            0x2A => Ps2Float.VecMul(vfs, vft),
            0x2B => Ps2Vu0.VecMax(vfs, vft),
            0x2C => Ps2Float.VecSub(vfs, vft),
            0x2D => Ps2Float.VecSub(vu.ACC, Ps2Float.VecMul(vfs, vft)),
            0x2E => vu.ExecOpmsub(vfs, vft),
            0x2F => Ps2Vu0.VecMin(vfs, vft),
            _ => vfs
        };

        // Even when fd == 0, MAC/Status flags are updated by arithmetic instructions
        vu.UpdateMacAndStatusFlags(result, op == 0x2E ? 0xEu : dest);
        if (fd != 0)
        {
            pendingReg = fd;
            pendingVal = result;
            pendingMask = op == 0x2E ? 0xEu : dest;
        }
    }

    private static void ExecLowerNonBranchSlot(Vu1State vu, uint lower)
    {
        uint top = (lower >> 25) & 0x7Fu;
        uint dest = (lower >> 21) & 0xFu;
        int it = (int)((lower >> 16) & 0x1Fu);
        int isReg = (int)((lower >> 11) & 0x1Fu);
        int id = (int)((lower >> 6) & 0x1Fu);
        int imm11 = SignExtend11((int)(lower & 0x7FFu));
        ushort uimm15 = (ushort)((((lower >> 21) & 0xFu) << 11) | (lower & 0x7FFu));

        switch (top)
        {
            case 0x00: // LQ
            {
                int qaddr = (vu.ReadViSigned(isReg) + imm11) & 0x3FF;
                vu.WriteVfNoFlags(it, vu.LoadQuadword(qaddr), dest);
                break;
            }
            case 0x01: // SQ
            {
                int qaddr = (vu.ReadViSigned(it) + imm11) & 0x3FF;
                vu.StoreQuadword(qaddr, vu.ReadVf(isReg), dest);
                break;
            }
            case 0x04: // ILW
            {
                int qaddr = (vu.ReadViSigned(isReg) + imm11) & 0x3FF;
                vu.WriteVi(it, vu.LoadInt16(qaddr, dest));
                break;
            }
            case 0x05: // ISW
            {
                int qaddr = (vu.ReadViSigned(isReg) + imm11) & 0x3FF;
                vu.StoreInt16(qaddr, vu.ReadVi(it), dest);
                break;
            }
            case 0x08: // IADDIU
                vu.WriteVi(it, (ushort)(vu.ReadVi(isReg) + uimm15));
                break;
            case 0x09: // ISUBIU
                vu.WriteVi(it, (ushort)(vu.ReadVi(isReg) - uimm15));
                break;
            case 0x10: // FCEQ
                vu.WriteVi(1, (ushort)((vu.ClipFlags & 0xFFFFFFu) == (lower & 0xFFFFFFu) ? 1 : 0));
                break;
            case 0x11: // FCSET
                vu.ClipFlags = lower & 0xFFFFFFu;
                break;
            case 0x12: // FCAND
                vu.WriteVi(1, (ushort)(((vu.ClipFlags & (lower & 0xFFFFFFu)) != 0) ? 1 : 0));
                break;
            case 0x13: // FCOR
                vu.WriteVi(1, (ushort)((((vu.ClipFlags | (lower & 0xFFFFFFu)) & 0xFFFFFFu) == 0xFFFFFFu) ? 1 : 0));
                break;
            case 0x14: // FSSET
                vu.StatusFlags = (ushort)((vu.StatusFlags & 0x03Fu) | (lower & 0xFC0u));
                break;
            case 0x16: // FSAND
                vu.WriteVi(it, (ushort)(vu.StatusFlags & (lower & 0xFFFu)));
                break;
            case 0x17: // FSOR
                vu.WriteVi(it, (ushort)((vu.StatusFlags | (lower & 0xFFFu)) & 0xFFFu));
                break;
            case 0x18: // FMEQ
                vu.WriteVi(it, (ushort)(vu.MacFlags == vu.ReadVi(isReg) ? 1 : 0));
                break;
            case 0x1A: // FMAND
                vu.WriteVi(it, (ushort)(vu.MacFlags & vu.ReadVi(isReg)));
                break;
            case 0x1B: // FMOR
                vu.WriteVi(it, (ushort)(vu.MacFlags | vu.ReadVi(isReg)));
                break;
            case 0x1C: // FCGET
                vu.WriteVi(it, (ushort)(vu.ClipFlags & 0xFFFu));
                break;
            case 0x40:
                ExecLower40(vu, lower, dest, id, isReg, it);
                break;
        }
    }

    private static void ExecLower40(Vu1State vu, uint lower, uint dest, int id, int isReg, int it)
    {
        uint op = lower & 0x3Fu;
        if (op < 0x3C)
        {
            int imm5 = SignExtend5(id);
            switch (op)
            {
                case 0x30: // IADD
                    vu.WriteVi(id, (ushort)(vu.ReadVi(isReg) + vu.ReadVi(it)));
                    break;
                case 0x31: // ISUB
                    vu.WriteVi(id, (ushort)(vu.ReadVi(isReg) - vu.ReadVi(it)));
                    break;
                case 0x32: // IADDI
                    vu.WriteVi(it, (ushort)(vu.ReadVi(isReg) + imm5));
                    break;
                case 0x34: // IAND
                    vu.WriteVi(id, (ushort)(vu.ReadVi(isReg) & vu.ReadVi(it)));
                    break;
                case 0x35: // IOR
                    vu.WriteVi(id, (ushort)(vu.ReadVi(isReg) | vu.ReadVi(it)));
                    break;
            }
            return;
        }

        uint subOp = ((lower >> 4) & 0x7Cu) | (lower & 0x3u);
        int fsf = (int)((lower >> 21) & 0x3u);
        int ftf = (int)((lower >> 23) & 0x3u);

        switch (subOp)
        {
            case 0x30: // MOVE
                vu.WriteVfNoFlags(it, vu.ReadVf(isReg), dest);
                break;
            case 0x31: // MR32
                vu.WriteVfNoFlags(it, Vu1State.ExecMr32(vu.ReadVf(isReg)), dest);
                break;
            case 0x34: // LQI
            {
                ushort idx = vu.ReadVi(isReg);
                vu.WriteVfNoFlags(it, vu.LoadQuadword(idx & 0x3FF), dest);
                vu.WriteVi(isReg, (ushort)((idx + 1) & 0x3FF));
                break;
            }
            case 0x35: // SQI
            {
                ushort idx = vu.ReadVi(it);
                vu.StoreQuadword(idx & 0x3FF, vu.ReadVf(isReg), dest);
                vu.WriteVi(it, (ushort)((idx + 1) & 0x3FF));
                break;
            }
            case 0x36: // LQD
            {
                ushort idx = (ushort)((vu.ReadVi(isReg) - 1) & 0x3FF);
                vu.WriteVi(isReg, idx);
                vu.WriteVfNoFlags(it, vu.LoadQuadword(idx), dest);
                break;
            }
            case 0x37: // SQD
            {
                ushort idx = (ushort)((vu.ReadVi(it) - 1) & 0x3FF);
                vu.WriteVi(it, idx);
                vu.StoreQuadword(idx, vu.ReadVf(isReg), dest);
                break;
            }
            case 0x38: // DIV
                vu.QueueQ(Ps2Float.Div(vu.ReadVf(isReg).GetElement(fsf), vu.ReadVf(it).GetElement(ftf)), 7);
                break;
            case 0x39: // SQRT
                vu.QueueQ(Ps2Float.Sqrt(vu.ReadVf(it).GetElement(ftf)), 7);
                break;
            case 0x3A: // RSQRT
                vu.QueueQ(Ps2Float.Rsqrt(vu.ReadVf(isReg).GetElement(fsf), vu.ReadVf(it).GetElement(ftf)), 13);
                break;
            case 0x3B: // WAITQ
                vu.WaitQ();
                break;
            case 0x3C: // MTIR
                vu.WriteVi(it, (ushort)vu.ReadVf(isReg).AsUInt32().GetElement(fsf));
                break;
            case 0x3D: // MFIR
                vu.WriteVfNoFlags(it, Vector128.Create((int)vu.ReadViSigned(isReg)).AsSingle(), dest);
                break;
            case 0x3E: // ILWR
                vu.WriteVi(it, vu.LoadInt16(vu.ReadVi(isReg) & 0x3FF, dest));
                break;
            case 0x3F: // ISWR
                vu.StoreInt16(vu.ReadVi(isReg) & 0x3FF, vu.ReadVi(it), dest);
                break;
            case 0x40: // RNEXT
                vu.WriteVfNoFlags(it, vu.ExecRnext(), dest);
                break;
            case 0x41: // RGET
                vu.WriteVfNoFlags(it, vu.ExecRget(), dest);
                break;
            case 0x42: // RINIT
                vu.ExecRinit(vu.ReadVf(isReg), fsf);
                break;
            case 0x43: // RXOR
                vu.ExecRxor(vu.ReadVf(isReg), fsf);
                break;
            case 0x64: // MFP
                vu.WriteVfNoFlags(it, Vector128.Create(vu.P), dest);
                break;
            case 0x68: // XTOP
                vu.WriteVi(it, vu.Top);
                break;
            case 0x69: // XITOP
                vu.WriteVi(it, vu.Itop);
                break;
            case 0x6C: // XGKICK
                vu.ExecXgKick(isReg);
                break;
            case 0x70: // ESADD
            {
                var v = vu.ReadVf(isReg);
                vu.QueueP(v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2), 11);
                break;
            }
            case 0x71: // ERSADD
            {
                var v = vu.ReadVf(isReg);
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                vu.QueueP(Ps2Float.Div(1.0f, sumSq), 18);
                break;
            }
            case 0x72: // ELENG
            {
                var v = vu.ReadVf(isReg);
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                vu.QueueP(Ps2Float.Sqrt(sumSq), 18);
                break;
            }
            case 0x73: // ERLENG
            {
                var v = vu.ReadVf(isReg);
                float sumSq = v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2);
                vu.QueueP(Ps2Float.Rsqrt(1.0f, sumSq), 24);
                break;
            }
            case 0x74: // EATANxy
            {
                var v = vu.ReadVf(isReg);
                vu.QueueP(MathF.Atan2(v.GetElement(1), v.GetElement(0)), 54);
                break;
            }
            case 0x75: // EATANxz
            {
                var v = vu.ReadVf(isReg);
                vu.QueueP(MathF.Atan2(v.GetElement(2), v.GetElement(0)), 54);
                break;
            }
            case 0x76: // ESUM
            {
                var v = vu.ReadVf(isReg);
                vu.QueueP(v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3), 12);
                break;
            }
            case 0x78: // ERCPR
                vu.QueueP(Ps2Float.Div(1.0f, vu.ReadVf(isReg).GetElement(fsf)), 12);
                break;
            case 0x79: // ESQRT
                vu.QueueP(Ps2Float.Sqrt(vu.ReadVf(isReg).GetElement(fsf)), 12);
                break;
            case 0x7A: // ERSQRT
                vu.QueueP(Ps2Float.Rsqrt(1.0f, vu.ReadVf(isReg).GetElement(fsf)), 18);
                break;
            case 0x7B: // WAITP
                vu.WaitP();
                break;
            case 0x7C: // ESIN
                vu.QueueP(MathF.Sin(vu.ReadVf(isReg).GetElement(fsf)), 29);
                break;
            case 0x7D: // EATAN
                vu.QueueP(MathF.Atan(vu.ReadVf(isReg).GetElement(fsf)), 54);
                break;
            case 0x7E: // EEXP
                vu.QueueP(MathF.Exp(-vu.ReadVf(isReg).GetElement(fsf)), 44);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (uint upper, uint lower) ReadSlot(byte[] microMem, int slot)
    {
        int offset = (slot & 0x7FF) << 3;
        ulong raw = Unsafe.ReadUnaligned<ulong>(ref microMem[offset]);
        return ((uint)(raw >> 32), (uint)raw);
    }

    public static void ExecuteDirect(Vu1State vu, ushort startByteAddr)
    {
        byte[] microMem = vu.MicroMem;
        int curSlot = (startByteAddr >> 3) & 0x7FF;
        int maxSteps = 65536;

        vu.FlushPipelines();
        vu.CurrentCycle = 0;

        while (maxSteps-- > 0)
        {
            var (up, lo) = ReadSlot(microMem, curSlot);
            bool isImm = (up & 0x80000000u) != 0;
            bool isE = (up & 0x40000000u) != 0;

            // Evaluate branch condition before delay slot
            bool branchTaken = false;
            int branchTargetSlot = 0;
            int linkReg = 0;
            ushort linkVal = 0;

            if (!isImm)
            {
                uint top = (lo >> 25) & 0x7Fu;
                if (top >= 0x20 && top <= 0x2F)
                {
                    int it = (int)((lo >> 16) & 0x1Fu);
                    int isReg = (int)((lo >> 11) & 0x1Fu);
                    int imm11 = SignExtend11((int)(lo & 0x7FFu));
                    int relTarget = (curSlot + 1 + imm11) & 0x7FF;

                    switch (top)
                    {
                        case 0x20: // B
                            branchTaken = true;
                            branchTargetSlot = relTarget;
                            break;
                        case 0x21: // BAL
                            branchTaken = true;
                            branchTargetSlot = relTarget;
                            linkReg = it;
                            linkVal = (ushort)((curSlot + 2) & 0x7FF);
                            break;
                        case 0x24: // JR
                            branchTaken = true;
                            branchTargetSlot = vu.ReadViForBranch(isReg) & 0x7FF;
                            break;
                        case 0x25: // JALR
                            branchTaken = true;
                            branchTargetSlot = vu.ReadViForBranch(isReg) & 0x7FF;
                            linkReg = it;
                            linkVal = (ushort)((curSlot + 2) & 0x7FF);
                            break;
                        case 0x28: // IBEQ
                            branchTaken = vu.ReadViForBranch(it) == vu.ReadViForBranch(isReg);
                            branchTargetSlot = relTarget;
                            break;
                        case 0x29: // IBNE
                            branchTaken = vu.ReadViForBranch(it) != vu.ReadViForBranch(isReg);
                            branchTargetSlot = relTarget;
                            break;
                        case 0x2C: // IBLTZ
                            branchTaken = vu.ReadViSignedForBranch(isReg) < 0;
                            branchTargetSlot = relTarget;
                            break;
                        case 0x2D: // IBGTZ
                            branchTaken = vu.ReadViSignedForBranch(isReg) > 0;
                            branchTargetSlot = relTarget;
                            break;
                        case 0x2E: // IBLEZ
                            branchTaken = vu.ReadViSignedForBranch(isReg) <= 0;
                            branchTargetSlot = relTarget;
                            break;
                        case 0x2F: // IBGEZ
                            branchTaken = vu.ReadViSignedForBranch(isReg) >= 0;
                            branchTargetSlot = relTarget;
                            break;
                    }
                }
            }

            // Execute current bundle directly
            vu.AdvanceCycle();
            vu.ResetViHazard();
            ExecBundle(vu, up, lo);
            vu.TotalInstructionsExecuted++;

            int delaySlot = (curSlot + 1) & 0x7FF;

            // Handle E-bit (End of Microprogram): execute delay slot at curSlot + 1 and stop
            if (isE)
            {
                var (delayUp, delayLo) = ReadSlot(microMem, delaySlot);
                vu.AdvanceCycle();
                vu.ResetViHazard();
                vu.LastWrittenAnyViReg = 0;
                ExecBundle(vu, delayUp, delayLo);
                vu.TotalInstructionsExecuted++;
                if (linkReg != 0 && vu.LastWrittenAnyViReg != (linkReg & 15))
                {
                    vu.VI[linkReg & 15] = linkVal;
                }
                vu.FlushPipelines();
                vu.Tpc = (ushort)(branchTaken ? (branchTargetSlot << 3) : (((delaySlot + 1) & 0x7FF) << 3));
                break;
            }

            // Handle branch delay slot
            if (!isImm && (((lo >> 25) & 0x7Fu) is >= 0x20 and <= 0x2F))
            {
                var (delayUp, delayLo) = ReadSlot(microMem, delaySlot);
                bool delayIsE = (delayUp & 0x40000000u) != 0;
                vu.AdvanceCycle();
                vu.ResetViHazard();
                vu.LastWrittenAnyViReg = 0;
                ExecBundle(vu, delayUp, delayLo);
                vu.TotalInstructionsExecuted++;

                if (linkReg != 0 && vu.LastWrittenAnyViReg != (linkReg & 15))
                {
                    vu.VI[linkReg & 15] = linkVal;
                }

                if (delayIsE)
                {
                    vu.FlushPipelines();
                    vu.Tpc = (ushort)(branchTaken ? (branchTargetSlot << 3) : (((delaySlot + 1) & 0x7FF) << 3));
                    break;
                }

                curSlot = branchTaken ? branchTargetSlot : ((delaySlot + 1) & 0x7FF);
                continue;
            }

            curSlot = delaySlot;
        }

        vu.FlushPipelines();
    }

    private static void EmitBundleCSharpComment(StringBuilder sb, int slot, uint upper, uint lower)
    {
        bool isImm = (upper & 0x80000000u) != 0;
        bool isE = (upper & 0x40000000u) != 0;
        uint dest = (upper >> 21) & 0xFu;
        uint ft = (upper >> 16) & 0x1Fu;
        uint fs = (upper >> 11) & 0x1Fu;
        uint fd = (upper >> 6) & 0x1Fu;
        uint op = upper & 0x3Fu;

        sb.Append($"    // [{slot:D4} @ 0x{slot * 8:X4}] Upper=0x{upper:X8} Lower=0x{lower:X8}");
        if (isE) sb.Append(" [E]");
        if (isImm) sb.Append($" [LOI={BitConverter.UInt32BitsToSingle(lower):G9}]");
        sb.AppendLine();

        if ((upper & 0x07FFFFFFu) != 0x000002FFu)
        {
            sb.AppendLine($"    ExecUpper(vu, 0x{upper:X8}u, 0x{dest:X}u, {fd}, {fs}, {ft}, 0x{op:X2}u);");
        }
        if (!isImm && lower != 0x8000033Cu && lower != 0u)
        {
            sb.AppendLine($"    ExecLower(vu, 0x{lower:X8}u);");
        }
    }

    private static readonly uint[] Crc32Table = GenerateCrc32Table();

    private static uint[] GenerateCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xFF];
        }
        return ~crc;
    }

    private static int SignExtend11(int v) => (v & 0x400) != 0 ? (v | ~0x7FF) : (v & 0x7FF);
    private static int SignExtend5(int v) => (v & 0x10) != 0 ? (v | ~0x1F) : (v & 0x1F);
}
