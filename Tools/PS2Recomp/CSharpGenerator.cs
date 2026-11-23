using System.Globalization;
using System.Runtime.Intrinsics;
using System.Text;
using Castaway.ElfTools.Elf;

namespace PS2Recomp;

public sealed class FunctionMeta
{
    public uint Start { get; set; }
    public uint End { get; set; }
    public string Name { get; set; } = "";
    public bool IsLeaf { get; set; }
    public bool UsesFpu { get; set; }
    public bool UsesVu0 { get; set; }
    public bool UsesMmi { get; set; }
    public long? SyscallId { get; set; }
}

public sealed class JumpTableMeta
{
    public uint JrVaddr { get; set; }
    public uint TableBase { get; set; }
    public int Entries { get; set; }
    public List<uint> Targets { get; } = new();
}

public sealed class CSharpGenerator
{
    private readonly Ps2ElfReader _elf;
    private readonly List<FunctionMeta> _functions = new();
    private readonly HashSet<uint> _functionStarts = new();
    private readonly Dictionary<uint, JumpTableMeta> _jumpTablesByJr = new();
    private readonly Dictionary<uint, FuncSummary> _summaries = new();

    private sealed class FuncSummary
    {
        public uint ReadGpr;
        public uint WriteGpr;
        public uint MustDefGpr;
        public uint ReadFpr;
        public uint WriteFpr;
        public uint MustDefFpr;
        public bool ReadFAcc;
        public bool WriteFAcc;
        public bool MustDefFAcc;
        public bool ReadFcc;
        public bool WriteFcc;
        public bool MustDefFcc;
        public bool HasUnknownCallee;
        public bool HasBackwardBranch;
        public int InsnCount;
        public readonly List<uint> DirectCallees = new();
    }

    // All addresses hooked anywhere in Castaway.Runtime, Castaway.Overrides, Castaway.Host, or Tests.
    // Any hooked function is treated conservatively (full flush/reload on call, never aggressively inlined).
    private static readonly HashSet<uint> KnownHookedAddresses = new()
    {
        0x00100000u, 0x00264268u, 0x0026CEE8u, 0x0026CFF0u, 0x0026F2B8u, 0x0026F330u, 0x0026F3A8u,
        0x00270F08u, 0x00271608u, 0x00272000u, 0x002737C8u, 0x00277070u, 0x002933B8u, 0x0029C048u,
        0x0029C1D8u, 0x0029C358u, 0x0029C760u, 0x0029C9E0u, 0x002A6230u, 0x002BB5B8u, 0x002C3C38u,
        0x002C7AE0u, 0x002C8660u, 0x002FE368u, 0x002FE518u, 0x002FE5C8u, 0x002FE6E0u, 0x00301A48u,
        0x00302478u, 0x00302838u, 0x00306610u, 0x00306690u, 0x00306820u, 0x00306BD8u, 0x003442A8u,
        0x00349A68u, 0x00349B98u, 0x0034EBE0u, 0x003611D8u, 0x00361460u, 0x00362F58u, 0x00367848u,
        0x00381968u, 0x0038A860u, 0x003A3D68u, 0x003ABA60u, 0x003D8428u, 0x003D8510u, 0x003DF2F0u,
        0x003E1ED0u, 0x003E1F98u, 0x003E2020u, 0x003E2940u, 0x003E3DA0u, 0x003E4090u, 0x003E4610u,
        0x003E46E0u, 0x00417158u, 0x00417A88u, 0x00419900u, 0x00427E40u, 0x00428AE8u, 0x0042EAE0u,
        0x0042EEB8u, 0x0042F6C8u, 0x004327E0u, 0x00432BB0u, 0x00433F40u, 0x00435490u, 0x004622B8u,
        0x00469298u, 0x00476EC0u, 0x00477040u, 0x004793F8u, 0x004798A0u, 0x00483150u, 0x004832E0u,
        0x00485178u, 0x00485A38u, 0x00485C18u, 0x00485E18u, 0x00486278u, 0x00486C80u, 0x00486F10u,
        0x00487088u, 0x004872C8u, 0x00489390u, 0x004893E0u, 0x00489650u, 0x00489760u, 0x00489888u,
        0x00489AF0u, 0x00489B88u, 0x0048AE28u
    };

    public IReadOnlyList<FunctionMeta> Functions => _functions;
    public IReadOnlyDictionary<uint, JumpTableMeta> JumpTables => _jumpTablesByJr;

    public CSharpGenerator(Ps2ElfReader elf, string tomlPath)
    {
        _elf = elf;
        ParseToml(tomlPath);
    }

    private void ParseToml(string tomlPath)
    {
        using var reader = new StreamReader(tomlPath);
        string? line;
        string currentSection = "";
        FunctionMeta? curFunc = null;
        JumpTableMeta? curJt = null;

        while ((line = reader.ReadLine()) != null)
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
                continue;

            if (span.SequenceEqual("[[functions]]"))
            {
                if (curFunc != null)
                {
                    _functions.Add(curFunc);
                    _functionStarts.Add(curFunc.Start);
                }
                curFunc = new FunctionMeta();
                curJt = null;
                currentSection = "functions";
                continue;
            }
            if (span.SequenceEqual("[[jump_tables]]"))
            {
                if (curJt != null)
                    _jumpTablesByJr[curJt.JrVaddr] = curJt;
                curJt = new JumpTableMeta();
                curFunc = null;
                currentSection = "jump_tables";
                continue;
            }
            if (span[0] == '[')
            {
                if (curFunc != null)
                {
                    _functions.Add(curFunc);
                    _functionStarts.Add(curFunc.Start);
                    curFunc = null;
                }
                if (curJt != null)
                {
                    _jumpTablesByJr[curJt.JrVaddr] = curJt;
                    curJt = null;
                }
                currentSection = "";
                continue;
            }

            int eqIdx = span.IndexOf('=');
            if (eqIdx < 0) continue;

            ReadOnlySpan<char> key = span[..eqIdx].Trim();
            ReadOnlySpan<char> val = span[(eqIdx + 1)..].Trim();

            if (currentSection == "functions" && curFunc != null)
            {
                if (key.SequenceEqual("start"))
                    curFunc.Start = ParseHex(val);
                else if (key.SequenceEqual("end"))
                    curFunc.End = ParseHex(val);
                else if (key.SequenceEqual("name"))
                    curFunc.Name = Unquote(val);
                else if (key.SequenceEqual("is_leaf"))
                    curFunc.IsLeaf = val.SequenceEqual("true");
                else if (key.SequenceEqual("uses_fpu"))
                    curFunc.UsesFpu = val.SequenceEqual("true");
                else if (key.SequenceEqual("uses_vu0"))
                    curFunc.UsesVu0 = val.SequenceEqual("true");
                else if (key.SequenceEqual("uses_mmi"))
                    curFunc.UsesMmi = val.SequenceEqual("true");
                else if (key.SequenceEqual("syscall_id") && long.TryParse(val, CultureInfo.InvariantCulture, out long scId))
                    curFunc.SyscallId = scId;
            }
            else if (currentSection == "jump_tables" && curJt != null)
            {
                if (key.SequenceEqual("jr_vaddr"))
                    curJt.JrVaddr = ParseHex(val);
                else if (key.SequenceEqual("table_base"))
                    curJt.TableBase = ParseHex(val);
                else if (key.SequenceEqual("entries"))
                    curJt.Entries = int.Parse(val, CultureInfo.InvariantCulture);
                else if (key.SequenceEqual("targets"))
                    ParseHexArray(val, curJt.Targets);
            }
        }

        if (curFunc != null)
        {
            _functions.Add(curFunc);
            _functionStarts.Add(curFunc.Start);
        }
        if (curJt != null)
        {
            _jumpTablesByJr[curJt.JrVaddr] = curJt;
        }
    }

    private static uint ParseHex(ReadOnlySpan<char> raw)
    {
        raw = raw.Trim('"');
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        return uint.Parse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static string Unquote(ReadOnlySpan<char> raw) => raw.Trim('"').ToString();

    private static void ParseHexArray(ReadOnlySpan<char> raw, List<uint> list)
    {
        raw = raw.Trim();
        if (raw.StartsWith("[")) raw = raw[1..];
        if (raw.EndsWith("]")) raw = raw[..^1];

        while (!raw.IsEmpty)
        {
            int comma = raw.IndexOf(',');
            ReadOnlySpan<char> token = comma >= 0 ? raw[..comma] : raw;
            token = token.Trim();
            if (!token.IsEmpty)
                list.Add(ParseHex(token));
            if (comma < 0) break;
            raw = raw[(comma + 1)..];
        }
    }

    public int GenerateAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        BuildInterproceduralSummaries();

        foreach (string existing in Directory.EnumerateFiles(outputDir, "Funcs_*.cs"))
        {
            File.Delete(existing);
        }

        // Group functions by 16 KiB shard: startAddr & 0xFFFFC000u
        var shards = _functions
            .OrderBy(f => f.Start)
            .GroupBy(f => f.Start & 0xFFFFC000u)
            .ToList();

        Parallel.ForEach(shards, shard =>
        {
            uint shardBase = shard.Key;
            string shardPath = Path.Combine(outputDir, $"Funcs_{shardBase:X8}.cs");
            var sb = new StringBuilder(64 * 1024);

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("// Castaway Static Recompiler — SLUS_216.64");
            sb.AppendLine($"// Shard 0x{shardBase:X8} .. 0x{(shardBase + 0x3FFFu):X8}");
            sb.AppendLine("#pragma warning disable CS0162 // Unreachable code detected");
            sb.AppendLine("#pragma warning disable CS0164 // This label has not been referenced");
            sb.AppendLine("#pragma warning disable CS0219 // Variable is assigned but its value is never used");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Runtime.CompilerServices;");
            sb.AppendLine("using System.Runtime.Intrinsics;");
            sb.AppendLine("using Castaway.Core;");
            sb.AppendLine();
            sb.AppendLine("namespace Castaway.Generated;");
            sb.AppendLine();
            sb.AppendLine("public static unsafe partial class RecompiledFunctions");
            sb.AppendLine("{");

            foreach (FunctionMeta func in shard)
            {
                EmitFunction(sb, func);
            }

            sb.AppendLine("}");
            File.WriteAllText(shardPath, sb.ToString());
        });

        EmitFunctionTable(outputDir);
        return shards.Count;
    }

    private void EmitFunctionTable(string outputDir)
    {
        string path = Path.Combine(outputDir, "FunctionTable.cs");
        var sb = new StringBuilder(_functions.Count * 64 + 1024);

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Castaway Static Recompiler — SLUS_216.64 Master Function Table");
        sb.AppendLine("using Castaway.Core;");
        sb.AppendLine();
        sb.AppendLine("namespace Castaway.Generated;");
        sb.AppendLine();
        sb.AppendLine("public static unsafe partial class RecompiledFunctions");
        sb.AppendLine("{");
        sb.AppendLine($"    public const int TotalFunctionCount = {_functions.Count};");
        sb.AppendLine("    public const uint EntryPointAddress = 0x00100008u;");
        sb.AppendLine();
        sb.AppendLine("    public static void RegisterAll()");
        sb.AppendLine("    {");

        foreach (FunctionMeta func in _functions.OrderBy(f => f.Start))
        {
            sb.AppendLine($"        RuntimeDispatcher.RegisterFunction(0x{func.Start:X8}u, &sub_{func.Start:X8});");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString());
    }

    public void EmitFunction(StringBuilder sb, FunctionMeta func)
    {
        uint start = func.Start;
        uint end = func.End > start ? func.End : start + 4u;
        int insnCount = (int)((end - start) >> 2);

        var insns = new List<R5900Insn>(insnCount + 1);
        for (int i = 0; i < insnCount; i++)
        {
            uint pc = start + (uint)(i * 4);
            uint word = _elf.IsVAddrValid(pc) ? _elf.ReadUInt32(pc) : 0u;
            insns.Add(R5900Decoder.Decode(word, pc));
        }

        if (insns.Count > 0 && insns[^1].HasDelaySlot)
        {
            uint delayPc = end;
            uint word = _elf.IsVAddrValid(delayPc) ? _elf.ReadUInt32(delayPc) : 0u;
            insns.Add(R5900Decoder.Decode(word, delayPc));
        }

        var labels = new HashSet<uint>();
        for (int i = 0; i < insnCount; i++)
        {
            R5900Insn insn = insns[i];
            if (insn.IsBranch)
            {
                uint target = insn.BranchTarget;
                if (target >= start && target < end)
                    labels.Add(target);
            }
            else if (insn.Op == R5900Op.J)
            {
                uint target = insn.JumpTarget;
                if (target >= start && target < end)
                    labels.Add(target);
            }
            else if (insn.Op == R5900Op.Jr && _jumpTablesByJr.TryGetValue(insn.Pc, out JumpTableMeta? jt))
            {
                foreach (uint target in jt.Targets)
                {
                    if (target >= start && target < end)
                        labels.Add(target);
                }
            }
        }

        var promo = AnalyzeRegisters(insns, start);
        ComputeEntryDeadRegs(insns, insnCount, start, labels, out uint deadEntryGpr, out uint deadEntryFpr, out bool deadEntryFAcc, out bool deadEntryFcc);

        bool inlineHint = _summaries.TryGetValue(start, out var selfSummary)
            && selfSummary.DirectCallees.Count == 0
            && !selfSummary.HasUnknownCallee
            && !selfSummary.HasBackwardBranch
            && insnCount <= 12;

        sb.AppendLine($"    // {func.Name} (0x{start:X8} - 0x{end:X8})");
        if (inlineHint)
            sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine($"    public static unsafe void sub_{start:X8}(CpuContext* ctx, byte* ram)");
        sb.AppendLine("    {");
        sb.AppendLine($"        if (RuntimeDispatcher.CheckEntry(0x{start:X8}u, ctx, ram)) return;");

        if (promo.HasAny)
        {
            EmitLoadPromotedEntry(sb, in promo, deadEntryGpr, deadEntryFpr, deadEntryFAcc, deadEntryFcc, "        ");
        }

        for (int i = 0; i < insnCount; i++)
        {
            if (insns[i].HasDelaySlot && labels.Contains(insns[i].Pc + 4u))
            {
                if (insns[i].IsBranch)
                {
                    sb.AppendLine($"        bool __cond_{insns[i].Pc:X8} = false;");
                }
                else if (insns[i].Op is R5900Op.Jr or R5900Op.Jalr)
                {
                    sb.AppendLine($"        uint __target_{insns[i].Pc:X8} = 0u;");
                }
            }
        }

        if (labels.Contains(start))
        {
            sb.AppendLine($"    L_{start:X8}: ;");
        }

        for (int i = 0; i < insnCount; i++)
        {
            R5900Insn insn = insns[i];
            if (insn.Pc != start && labels.Contains(insn.Pc))
            {
                sb.AppendLine($"    L_{insn.Pc:X8}: ;");
            }

            if (insn.HasDelaySlot)
            {
                R5900Insn delayInsn = (i + 1 < insns.Count)
                    ? insns[i + 1]
                    : new R5900Insn(insn.Pc + 4u, 0u, R5900Op.Sll);
                bool delayHasLabel = labels.Contains(delayInsn.Pc);

                EmitBranchOrJumpWithDelaySlot(sb, func, insn, delayInsn, delayHasLabel, in promo);
                i++;
            }
            else if (i + 1 < insnCount && !labels.Contains(insns[i + 1].Pc) && !insns[i + 1].HasDelaySlot &&
                     TryEmitUnalignedPair(sb, insn, insns[i + 1], "        ", in promo))
            {
                i++;
            }
            else
            {
                EmitSingleInstruction(sb, insn, "        ", in promo);
            }
        }

        int effCount = insnCount;
        while (effCount > 0 && insns[effCount - 1].Word == 0u && !labels.Contains(insns[effCount - 1].Pc))
            effCount--;

        bool endsWithUnconditionalJump = false;
        if (effCount >= 2)
        {
            R5900Insn penult = insns[effCount - 2];
            if (penult.Op is R5900Op.Jr or R5900Op.J)
                endsWithUnconditionalJump = true;
        }
        if (effCount >= 1)
        {
            R5900Insn last = insns[effCount - 1];
            if (last.Op is R5900Op.Jr or R5900Op.J or R5900Op.Eret)
                endsWithUnconditionalJump = true;
        }

        if (!endsWithUnconditionalJump)
        {
            EmitFlushPromoted(sb, in promo, "        ");
            if (_functionStarts.Contains(end))
            {
                sb.AppendLine($"        sub_{end:X8}(ctx, ram);");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    public struct RegPromoState
    {
        public uint PromotedGpr;
        public uint WrittenGpr;
        public uint PromotedFpr;
        public uint WrittenFpr;
        public bool PromoteFAcc;
        public bool WrittenFAcc;
        public bool PromoteFcc;
        public bool WrittenFcc;
        public bool UsesScratchpad;

        public readonly bool HasAny => PromotedGpr != 0 || PromotedFpr != 0 || PromoteFAcc || PromoteFcc;
        public readonly bool HasAnyWritten => WrittenGpr != 0 || WrittenFpr != 0 || WrittenFAcc || WrittenFcc;
        public readonly bool IsGpr(uint r) => r != 0 && (PromotedGpr & (1u << (int)r)) != 0;
        public readonly bool IsFpr(uint f) => (PromotedFpr & (1u << (int)f)) != 0;
    }

    private RegPromoState AnalyzeRegisters(List<R5900Insn> insns, uint funcStart)
    {
        int n = insns.Count;
        int[] loopDelta = new int[n + 2];
        for (int i = 0; i < n; i++)
        {
            R5900Insn insn = insns[i];
            uint target = 0u;
            if (insn.IsBranch) target = insn.BranchTarget;
            else if (insn.Op == R5900Op.J) target = insn.JumpTarget;
            if (target >= funcStart && target <= insn.Pc)
            {
                int tIdx = (int)((target - funcStart) >> 2);
                if (tIdx >= 0 && tIdx <= i)
                {
                    loopDelta[tIdx]++;
                    loopDelta[Math.Min(i + 2, n)]--;
                }
            }
        }

        Span<int> gprUsesCold = stackalloc int[32];
        Span<int> gprUsesHot = stackalloc int[32];
        Span<int> fprUsesCold = stackalloc int[32];
        Span<int> fprUsesHot = stackalloc int[32];
        Span<int> gprCallCold = stackalloc int[32];
        Span<int> gprCallHot = stackalloc int[32];
        Span<int> fprCallCold = stackalloc int[32];
        Span<int> fprCallHot = stackalloc int[32];
        int fAccCallCold = 0, fAccCallHot = 0;
        int fccCallCold = 0, fccCallHot = 0;
        uint writeGpr = 0;
        uint nonPromotableGpr = 1u; // $zero never promoted
        uint writeFpr = 0;
        int fAccUsesCold = 0, fAccUsesHot = 0;
        bool writesFAcc = false;
        int fccUsesCold = 0, fccUsesHot = 0;
        bool writesFcc = false;
        bool usesFcr31 = false;
        bool usesScratchpad = funcStart >= 0x0041C000u && funcStart < 0x00424000u;
        int callCountCold = 0, callCountHot = 0;

        int loopDepth = 0;
        for (int i = 0; i < n; i++)
        {
            loopDepth += loopDelta[i];
            R5900Insn insn = insns[i];
            if (insn.Word == 0u) continue;
            bool inLoop = loopDepth > 0;
            Span<int> gprUses = inLoop ? gprUsesHot : gprUsesCold;
            Span<int> fprUses = inLoop ? fprUsesHot : fprUsesCold;
            Span<int> gprCallTouch = inLoop ? gprCallHot : gprCallCold;
            Span<int> fprCallTouch = inLoop ? fprCallHot : fprCallCold;
            ref int fAccCallTouch = ref (inLoop ? ref fAccCallHot : ref fAccCallCold);
            ref int fccCallTouch = ref (inLoop ? ref fccCallHot : ref fccCallCold);
            ref int fAccUses = ref (inLoop ? ref fAccUsesHot : ref fAccUsesCold);
            ref int fccUses = ref (inLoop ? ref fccUsesHot : ref fccUsesCold);
            ref int callCount = ref (inLoop ? ref callCountHot : ref callCountCold);
            uint rs = insn.Rs, rt = insn.Rt, rd = insn.Rd;
            uint fs = insn.Fs, ft = insn.Ft, fd = insn.Fd;

            switch (insn.Op)
            {
                case R5900Op.Beq:
                case R5900Op.Beql:
                case R5900Op.Bne:
                case R5900Op.Bnel:
                    if (rs != rt) { gprUses[(int)rs]++; gprUses[(int)rt]++; }
                    break;
                case R5900Op.Blez:
                case R5900Op.Blezl:
                case R5900Op.Bgtz:
                case R5900Op.Bgtzl:
                case R5900Op.Bltz:
                case R5900Op.Bltzl:
                case R5900Op.Bgez:
                case R5900Op.Bgezl:
                    gprUses[(int)rs]++;
                    break;
                case R5900Op.Bltzal:
                case R5900Op.Bgezal:
                case R5900Op.Bltzall:
                case R5900Op.Bgezall:
                    gprUses[(int)rs]++;
                    gprUses[31]++;
                    writeGpr |= 1u << 31;
                    callCount++;
                    break;
                case R5900Op.Bc1f:
                case R5900Op.Bc1fl:
                case R5900Op.Bc1t:
                case R5900Op.Bc1tl:
                    fccUses++;
                    break;
                case R5900Op.Jal:
                    gprUses[31]++;
                    writeGpr |= 1u << 31;
                    if (_summaries.TryGetValue(insn.JumpTarget, out var jalCallee) && !jalCallee.HasUnknownCallee)
                    {
                        uint cg = jalCallee.ReadGpr | jalCallee.WriteGpr | (1u << 31);
                        for (int r = 1; r < 32; r++)
                            if ((cg & (1u << r)) != 0) gprCallTouch[r]++;
                        uint cf = jalCallee.ReadFpr | jalCallee.WriteFpr;
                        for (int f = 0; f < 32; f++)
                            if ((cf & (1u << f)) != 0) fprCallTouch[f]++;
                        if (jalCallee.ReadFAcc || jalCallee.WriteFAcc) fAccCallTouch++;
                        if (jalCallee.ReadFcc || jalCallee.WriteFcc) fccCallTouch++;
                    }
                    else
                    {
                        callCount++;
                    }
                    break;
                case R5900Op.Jr:
                    gprUses[(int)rs]++;
                    break;
                case R5900Op.Jalr:
                    gprUses[(int)rs]++;
                    if (rd != 0) { gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    callCount++;
                    break;

                case R5900Op.Sll:
                case R5900Op.Srl:
                case R5900Op.Sra:
                case R5900Op.Dsll:
                case R5900Op.Dsrl:
                case R5900Op.Dsra:
                case R5900Op.Dsll32:
                case R5900Op.Dsrl32:
                case R5900Op.Dsra32:
                    if (rd != 0) { gprUses[(int)rt]++; gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    break;

                case R5900Op.Sllv:
                case R5900Op.Srlv:
                case R5900Op.Srav:
                case R5900Op.Dsllv:
                case R5900Op.Dsrlv:
                case R5900Op.Dsrav:
                case R5900Op.Movz:
                case R5900Op.Movn:
                case R5900Op.Add:
                case R5900Op.Addu:
                case R5900Op.Sub:
                case R5900Op.Subu:
                case R5900Op.Dadd:
                case R5900Op.Daddu:
                case R5900Op.Dsub:
                case R5900Op.Dsubu:
                case R5900Op.And:
                case R5900Op.Or:
                case R5900Op.Xor:
                case R5900Op.Nor:
                case R5900Op.Slt:
                case R5900Op.Sltu:
                    if (rd != 0) { gprUses[(int)rs]++; gprUses[(int)rt]++; gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    break;

                case R5900Op.Addi:
                case R5900Op.Addiu:
                case R5900Op.Daddi:
                case R5900Op.Daddiu:
                case R5900Op.Andi:
                case R5900Op.Ori:
                case R5900Op.Xori:
                case R5900Op.Slti:
                case R5900Op.Sltiu:
                    if (rt != 0) { gprUses[(int)rs]++; gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;

                case R5900Op.Lui:
                    if (insn.Uimm == 0x7000u) usesScratchpad = true;
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;

                case R5900Op.Mult:
                case R5900Op.Multu:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    if (rd != 0) { gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    break;
                case R5900Op.Div:
                case R5900Op.Divu:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    break;
                case R5900Op.Mfhi:
                case R5900Op.Mflo:
                case R5900Op.Mfsa:
                case R5900Op.Mfhi1:
                case R5900Op.Mflo1:
                    if (rd != 0) { gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    break;
                case R5900Op.Mthi:
                case R5900Op.Mtlo:
                case R5900Op.Mtsa:
                case R5900Op.Mtsab:
                case R5900Op.Mtsah:
                case R5900Op.Mthi1:
                case R5900Op.Mtlo1:
                    gprUses[(int)rs]++;
                    break;

                case R5900Op.Lb:
                case R5900Op.Lbu:
                case R5900Op.Lh:
                case R5900Op.Lhu:
                case R5900Op.Lw:
                case R5900Op.Ll:
                case R5900Op.Lwu:
                case R5900Op.Ld:
                    gprUses[(int)rs]++;
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;
                case R5900Op.Lq:
                    gprUses[(int)rs]++;
                    if (rt != 0)
                    {
                        if (rs != 29) gprUses[(int)rt]++;
                        writeGpr |= 1u << (int)rt;
                    }
                    break;
                case R5900Op.Lwl:
                case R5900Op.Lwr:
                case R5900Op.Ldl:
                case R5900Op.Ldr:
                    gprUses[(int)rs]++;
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;
                case R5900Op.Sq:
                    gprUses[(int)rs]++;
                    if (rs != 29) gprUses[(int)rt]++;
                    break;
                case R5900Op.Swl:
                case R5900Op.Swr:
                case R5900Op.Sdl:
                case R5900Op.Sdr:
                    gprUses[(int)rs]++;
                    gprUses[(int)rt]++;
                    break;
                case R5900Op.Sb:
                case R5900Op.Sh:
                case R5900Op.Sw:
                case R5900Op.Sd:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    break;
                case R5900Op.Sc:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    if (rt != 0) writeGpr |= 1u << (int)rt;
                    break;
                case R5900Op.Lwc1:
                    gprUses[(int)rs]++;
                    fprUses[(int)ft]++; writeFpr |= 1u << (int)ft;
                    break;
                case R5900Op.Swc1:
                    gprUses[(int)rs]++;
                    fprUses[(int)ft]++;
                    break;
                case R5900Op.Lqc2:
                case R5900Op.Sqc2:
                    gprUses[(int)rs]++;
                    break;

                case R5900Op.Syscall:
                case R5900Op.Break:
                case R5900Op.Vcallms:
                case R5900Op.Vcallmsr:
                    callCount++;
                    break;
                case R5900Op.Mfc0:
                case R5900Op.Cfc2:
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;
                case R5900Op.Mtc0:
                case R5900Op.Ctc2:
                    gprUses[(int)rt]++;
                    break;
                case R5900Op.Tge:
                case R5900Op.Tgeu:
                case R5900Op.Tlt:
                case R5900Op.Tltu:
                case R5900Op.Teq:
                case R5900Op.Tne:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    callCount++;
                    break;
                case R5900Op.Tgei:
                case R5900Op.Tgeiu:
                case R5900Op.Tlti:
                case R5900Op.Tltiu:
                case R5900Op.Teqi:
                case R5900Op.Tnei:
                    gprUses[(int)rs]++;
                    callCount++;
                    break;

                // COP1
                case R5900Op.Mfc1:
                    fprUses[(int)fs]++;
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;
                case R5900Op.Mtc1:
                    gprUses[(int)rt]++;
                    fprUses[(int)fs]++; writeFpr |= 1u << (int)fs;
                    break;
                case R5900Op.Cfc1:
                    usesFcr31 = true;
                    if (rt != 0) { gprUses[(int)rt]++; writeGpr |= 1u << (int)rt; }
                    break;
                case R5900Op.Ctc1:
                    usesFcr31 = true;
                    gprUses[(int)rt]++;
                    break;
                case R5900Op.AddS:
                case R5900Op.SubS:
                case R5900Op.MulS:
                case R5900Op.DivS:
                case R5900Op.RsqrtS:
                case R5900Op.MaxS:
                case R5900Op.MinS:
                    fprUses[(int)fs]++; fprUses[(int)ft]++; fprUses[(int)fd]++; writeFpr |= 1u << (int)fd;
                    break;
                case R5900Op.SqrtS:
                    fprUses[(int)ft]++; fprUses[(int)fd]++; writeFpr |= 1u << (int)fd;
                    break;
                case R5900Op.AbsS:
                case R5900Op.MovS:
                case R5900Op.NegS:
                case R5900Op.CvtWS:
                case R5900Op.TruncWS:
                case R5900Op.RoundWS:
                case R5900Op.CeilWS:
                case R5900Op.FloorWS:
                case R5900Op.CvtSW:
                    fprUses[(int)fs]++; fprUses[(int)fd]++; writeFpr |= 1u << (int)fd;
                    break;
                case R5900Op.AddaS:
                case R5900Op.SubaS:
                case R5900Op.MulaS:
                    fprUses[(int)fs]++; fprUses[(int)ft]++; fAccUses++; writesFAcc = true;
                    break;
                case R5900Op.MaddS:
                case R5900Op.MsubS:
                    fprUses[(int)fs]++; fprUses[(int)ft]++; fAccUses++; fprUses[(int)fd]++; writeFpr |= 1u << (int)fd;
                    break;
                case R5900Op.MaddaS:
                case R5900Op.MsubaS:
                    fprUses[(int)fs]++; fprUses[(int)ft]++; fAccUses += 2; writesFAcc = true;
                    break;
                case R5900Op.CF:
                case R5900Op.CSf:
                case R5900Op.CUn:
                case R5900Op.CNgle:
                    fccUses++; writesFcc = true;
                    break;
                case R5900Op.CEq:
                case R5900Op.CUeq:
                case R5900Op.CSeq:
                case R5900Op.CNgl:
                case R5900Op.COlt:
                case R5900Op.CUlt:
                case R5900Op.CLt:
                case R5900Op.CNge:
                case R5900Op.COle:
                case R5900Op.CUle:
                case R5900Op.CLe:
                case R5900Op.CNgt:
                    fprUses[(int)fs]++; fprUses[(int)ft]++; fccUses++; writesFcc = true;
                    break;

                // Scalar Pipeline 0/1 multiply/divide (promotable!)
                case R5900Op.Madd:
                case R5900Op.Maddu:
                case R5900Op.Msub:
                case R5900Op.Msubu:
                case R5900Op.Madd1:
                case R5900Op.Maddu1:
                case R5900Op.Mult1:
                case R5900Op.Multu1:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    if (rd != 0) { gprUses[(int)rd]++; writeGpr |= 1u << (int)rd; }
                    break;
                case R5900Op.Div1:
                case R5900Op.Divu1:
                    gprUses[(int)rs]++; gprUses[(int)rt]++;
                    break;

                // MMI & 128-bit COP2 transfers (mark touched GPRs non-promotable)
                case R5900Op.Pmultw:
                case R5900Op.Pmultuw:
                case R5900Op.Pmaddw:
                case R5900Op.Pmadduw:
                case R5900Op.Pmsubw:
                case R5900Op.Pmulth:
                case R5900Op.Pmaddh:
                case R5900Op.Pmsubh:
                case R5900Op.Phmadh:
                case R5900Op.Phmsbh:
                case R5900Op.Psllvw:
                case R5900Op.Psrlvw:
                case R5900Op.Psravw:
                case R5900Op.Paddw:
                case R5900Op.Psubw:
                case R5900Op.Paddh:
                case R5900Op.Psubh:
                case R5900Op.Paddb:
                case R5900Op.Psubb:
                case R5900Op.Paddsw:
                case R5900Op.Psubsw:
                case R5900Op.Paddsh:
                case R5900Op.Psubsh:
                case R5900Op.Paddsb:
                case R5900Op.Psubsb:
                case R5900Op.Padduw:
                case R5900Op.Psubuw:
                case R5900Op.Padduh:
                case R5900Op.Psubuh:
                case R5900Op.Paddub:
                case R5900Op.Psubub:
                case R5900Op.Pcgtw:
                case R5900Op.Pcgth:
                case R5900Op.Pcgtb:
                case R5900Op.Pceqw:
                case R5900Op.Pceqh:
                case R5900Op.Pceqb:
                case R5900Op.Pmaxw:
                case R5900Op.Pmaxh:
                case R5900Op.Pminw:
                case R5900Op.Pminh:
                case R5900Op.Padsbh:
                case R5900Op.Pextlw:
                case R5900Op.Pextuw:
                case R5900Op.Pextlh:
                case R5900Op.Pextuh:
                case R5900Op.Pextlb:
                case R5900Op.Pextub:
                case R5900Op.Ppacw:
                case R5900Op.Ppach:
                case R5900Op.Ppacb:
                case R5900Op.Pinth:
                case R5900Op.Pinteh:
                case R5900Op.Pcpyld:
                case R5900Op.Pcpyud:
                case R5900Op.Qfsrv:
                case R5900Op.Pand:
                case R5900Op.Por:
                case R5900Op.Pxor:
                case R5900Op.Pnor:
                    nonPromotableGpr |= (1u << (int)rd) | (1u << (int)rs) | (1u << (int)rt);
                    break;
                case R5900Op.Pdivw:
                case R5900Op.Pdivuw:
                case R5900Op.Pdivbw:
                    nonPromotableGpr |= (1u << (int)rs) | (1u << (int)rt);
                    break;
                case R5900Op.Plzcw:
                    nonPromotableGpr |= (1u << (int)rd) | (1u << (int)rs);
                    break;
                case R5900Op.Pmfhl:
                case R5900Op.Pmfhi:
                case R5900Op.Pmflo:
                    nonPromotableGpr |= 1u << (int)rd;
                    break;
                case R5900Op.Pmthl:
                case R5900Op.Pmthi:
                case R5900Op.Pmtlo:
                    nonPromotableGpr |= 1u << (int)rs;
                    break;
                case R5900Op.Psllh:
                case R5900Op.Psrlh:
                case R5900Op.Psrah:
                case R5900Op.Psllw:
                case R5900Op.Psrlw:
                case R5900Op.Psraw:
                case R5900Op.Pabsw:
                case R5900Op.Pabsh:
                case R5900Op.Pext5:
                case R5900Op.Ppac5:
                case R5900Op.Pcpyh:
                case R5900Op.Pexeh:
                case R5900Op.Pexch:
                case R5900Op.Prevh:
                case R5900Op.Pexew:
                case R5900Op.Pexcw:
                case R5900Op.Prot3w:
                    nonPromotableGpr |= (1u << (int)rd) | (1u << (int)rt);
                    break;
                case R5900Op.Qmfc2:
                case R5900Op.Qmtc2:
                    nonPromotableGpr |= 1u << (int)rt;
                    break;
            }
        }

        var state = new RegPromoState { UsesScratchpad = usesScratchpad };
        for (int r = 1; r < 32; r++)
        {
            if ((nonPromotableGpr & (1u << r)) != 0) continue;
            int effCalls = (callCountCold + gprCallCold[r]) + ((callCountHot + gprCallHot[r]) << 2);
            int weightedUses = gprUsesCold[r] + (gprUsesHot[r] << 2);
            int minUses = effCalls == 0 ? 1 : (effCalls <= 2 ? 3 : Math.Min(10, effCalls + 2));
            if (weightedUses >= minUses)
                state.PromotedGpr |= 1u << r;
        }
        state.WrittenGpr = writeGpr & state.PromotedGpr;

        for (int f = 0; f < 32; f++)
        {
            int effCalls = (callCountCold + fprCallCold[f]) + ((callCountHot + fprCallHot[f]) << 2);
            int weightedUses = fprUsesCold[f] + (fprUsesHot[f] << 2);
            int minUses = effCalls == 0 ? 1 : (effCalls <= 2 ? 3 : Math.Min(10, effCalls + 2));
            if (weightedUses >= minUses)
                state.PromotedFpr |= 1u << f;
        }
        state.WrittenFpr = writeFpr & state.PromotedFpr;

        int fAccCalls = (callCountCold + fAccCallCold) + ((callCountHot + fAccCallHot) << 2);
        int fAccMinUses = fAccCalls == 0 ? 1 : (fAccCalls <= 2 ? 3 : Math.Min(10, fAccCalls + 2));
        if (fAccUsesCold + (fAccUsesHot << 2) >= fAccMinUses)
        {
            state.PromoteFAcc = true;
            state.WrittenFAcc = writesFAcc;
        }
        int fccCalls = (callCountCold + fccCallCold) + ((callCountHot + fccCallHot) << 2);
        int fccMinUses = fccCalls == 0 ? 1 : (fccCalls <= 2 ? 3 : Math.Min(10, fccCalls + 2));
        if (!usesFcr31 && fccUsesCold + (fccUsesHot << 2) >= fccMinUses)
        {
            state.PromoteFcc = true;
            state.WrittenFcc = writesFcc;
        }

        return state;
    }

    private static void EmitLoadPromoted(StringBuilder sb, in RegPromoState p, string indent, bool declare)
    {
        EmitLoadPromotedMasked(sb, p.PromotedGpr, p.PromotedFpr, p.PromoteFAcc, p.PromoteFcc, indent, declare);
    }

    private static void EmitLoadPromotedEntry(
        StringBuilder sb, in RegPromoState p,
        uint deadGpr, uint deadFpr, bool deadFAcc, bool deadFcc,
        string indent)
    {
        uint g = p.PromotedGpr;
        for (int r = 1; r < 32; r++)
        {
            uint bit = 1u << r;
            if ((g & bit) != 0)
            {
                if ((deadGpr & bit) != 0)
                    sb.AppendLine($"{indent}ulong r{r} = 0UL;");
                else
                    sb.AppendLine($"{indent}ulong r{r} = ctx->GetU64({r});");
            }
        }
        uint fm = p.PromotedFpr;
        for (int f = 0; f < 32; f++)
        {
            uint bit = 1u << f;
            if ((fm & bit) != 0)
            {
                if ((deadFpr & bit) != 0)
                    sb.AppendLine($"{indent}float f{f} = 0f;");
                else
                    sb.AppendLine($"{indent}float f{f} = ctx->F[{f}];");
            }
        }
        if (p.PromoteFAcc)
            sb.AppendLine(deadFAcc ? $"{indent}float fAcc = 0f;" : $"{indent}float fAcc = ctx->FAcc;");
        if (p.PromoteFcc)
            sb.AppendLine(deadFcc ? $"{indent}bool fcc = false;" : $"{indent}bool fcc = ctx->Cop1Condition;");
    }

    private static void EmitLoadPromotedMasked(
        StringBuilder sb, uint gprMask, uint fprMask, bool loadFAcc, bool loadFcc, string indent, bool declare)
    {
        for (int r = 1; r < 32; r++)
        {
            if ((gprMask & (1u << r)) != 0)
                sb.AppendLine($"{indent}{(declare ? "ulong " : "")}r{r} = ctx->GetU64({r});");
        }
        for (int f = 0; f < 32; f++)
        {
            if ((fprMask & (1u << f)) != 0)
                sb.AppendLine($"{indent}{(declare ? "float " : "")}f{f} = ctx->F[{f}];");
        }
        if (loadFAcc)
            sb.AppendLine($"{indent}{(declare ? "float " : "")}fAcc = ctx->FAcc;");
        if (loadFcc)
            sb.AppendLine($"{indent}{(declare ? "bool " : "")}fcc = ctx->Cop1Condition;");
    }

    private static void EmitFlushPromoted(StringBuilder sb, in RegPromoState p, string indent)
    {
        if (!p.HasAnyWritten) return;
        EmitFlushPromotedMasked(sb, p.WrittenGpr, p.WrittenFpr, p.WrittenFAcc, p.WrittenFcc, indent);
    }

    private static void EmitFlushPromotedMasked(
        StringBuilder sb, uint gprMask, uint fprMask, bool flushFAcc, bool flushFcc, string indent)
    {
        for (int r = 1; r < 32; r++)
        {
            if ((gprMask & (1u << r)) != 0)
                sb.AppendLine($"{indent}ctx->SetU64({r}, r{r});");
        }
        for (int f = 0; f < 32; f++)
        {
            if ((fprMask & (1u << f)) != 0)
                sb.AppendLine($"{indent}ctx->F[{f}] = f{f};");
        }
        if (flushFAcc)
            sb.AppendLine($"{indent}ctx->FAcc = fAcc;");
        if (flushFcc)
            sb.AppendLine($"{indent}ctx->Cop1Condition = fcc;");
    }

    private void BuildInterproceduralSummaries()
    {
        _summaries.Clear();

        foreach (FunctionMeta func in _functions)
        {
            uint start = func.Start;
            uint end = func.End > start ? func.End : start + 4u;
            int insnCount = (int)((end - start) >> 2);
            var sum = new FuncSummary { InsnCount = insnCount };
            if (KnownHookedAddresses.Contains(start))
                sum.HasUnknownCallee = true;

            var insns = new List<R5900Insn>(insnCount + 1);
            var labels = new HashSet<uint>();
            for (int i = 0; i < insnCount; i++)
            {
                uint pc = start + (uint)(i * 4);
                uint word = _elf.IsVAddrValid(pc) ? _elf.ReadUInt32(pc) : 0u;
                R5900Insn insn = R5900Decoder.Decode(word, pc);
                insns.Add(insn);
                if (insn.IsBranch)
                {
                    uint t = insn.BranchTarget;
                    if (t >= start && t < end) labels.Add(t);
                }
                else if (insn.Op == R5900Op.J)
                {
                    uint t = insn.JumpTarget;
                    if (t >= start && t < end) labels.Add(t);
                }
                else if (insn.Op == R5900Op.Jr && _jumpTablesByJr.TryGetValue(insn.Pc, out var jt))
                {
                    foreach (uint t in jt.Targets)
                        if (t >= start && t < end) labels.Add(t);
                }
                AccumulateInsnSummary(sum, insn, start, end);
            }
            if (insnCount > 0 && insns[^1].HasDelaySlot)
            {
                uint delayPc = end;
                uint word = _elf.IsVAddrValid(delayPc) ? _elf.ReadUInt32(delayPc) : 0u;
                R5900Insn delayInsn = R5900Decoder.Decode(word, delayPc);
                insns.Add(delayInsn);
                AccumulateInsnSummary(sum, delayInsn, start, end);
            }

            ComputeEntryDeadRegs(insns, insnCount, start, labels,
                out sum.MustDefGpr, out sum.MustDefFpr, out sum.MustDefFAcc, out sum.MustDefFcc);

            // Strip trailing unlabeled padding NOPs before checking fallthrough
            int effCount = insnCount;
            while (effCount > 0 && insns[effCount - 1].Word == 0u && !labels.Contains(insns[effCount - 1].Pc))
                effCount--;

            bool endsUncond = false;
            if (effCount >= 2 && insns[effCount - 2].Op is R5900Op.Jr or R5900Op.J)
                endsUncond = true;
            if (effCount >= 1 && insns[effCount - 1].Op is R5900Op.Jr or R5900Op.J or R5900Op.Eret)
                endsUncond = true;
            if (!endsUncond)
            {
                if (_functionStarts.Contains(end))
                    sum.DirectCallees.Add(end);
                else
                    sum.HasUnknownCallee = true;
            }

            _summaries[start] = sum;
        }

        // Fixed-point propagation across direct call/tail-jump edges
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var kv in _summaries)
            {
                FuncSummary s = kv.Value;
                if (s.HasUnknownCallee || s.DirectCallees.Count == 0) continue;

                foreach (uint calleeAddr in s.DirectCallees)
                {
                    if (!_summaries.TryGetValue(calleeAddr, out var c) || c.HasUnknownCallee)
                    {
                        s.HasUnknownCallee = true;
                        changed = true;
                        break;
                    }
                    uint newRg = s.ReadGpr | c.ReadGpr;
                    uint newWg = s.WriteGpr | c.WriteGpr;
                    uint newRf = s.ReadFpr | c.ReadFpr;
                    uint newWf = s.WriteFpr | c.WriteFpr;
                    bool newRFA = s.ReadFAcc || c.ReadFAcc;
                    bool newWFA = s.WriteFAcc || c.WriteFAcc;
                    bool newRFc = s.ReadFcc || c.ReadFcc;
                    bool newWFc = s.WriteFcc || c.WriteFcc;
                    if (newRg != s.ReadGpr || newWg != s.WriteGpr ||
                        newRf != s.ReadFpr || newWf != s.WriteFpr ||
                        newRFA != s.ReadFAcc || newWFA != s.WriteFAcc ||
                        newRFc != s.ReadFcc || newWFc != s.WriteFcc)
                    {
                        s.ReadGpr = newRg; s.WriteGpr = newWg;
                        s.ReadFpr = newRf; s.WriteFpr = newWf;
                        s.ReadFAcc = newRFA; s.WriteFAcc = newWFA;
                        s.ReadFcc = newRFc; s.WriteFcc = newWFc;
                        changed = true;
                    }
                }
            }
        }
    }

    private void AccumulateInsnSummary(FuncSummary sum, R5900Insn insn, uint start, uint end)
    {
        if (insn.Word == 0u) return;

        if (insn.IsBranch)
        {
            uint target = insn.BranchTarget;
            if (target <= insn.Pc && target >= start && target < end)
                sum.HasBackwardBranch = true;
            if (target < start || target >= end)
            {
                if (_functionStarts.Contains(target))
                    sum.DirectCallees.Add(target);
                else
                    sum.HasUnknownCallee = true;
            }
        }
        else if (insn.Op == R5900Op.J)
        {
            uint target = insn.JumpTarget;
            if (target <= insn.Pc && target >= start && target < end)
                sum.HasBackwardBranch = true;
            if (target < start || target >= end)
            {
                if (_functionStarts.Contains(target))
                    sum.DirectCallees.Add(target);
                else
                    sum.HasUnknownCallee = true;
            }
        }
        else if (insn.Op == R5900Op.Jal)
        {
            uint target = insn.JumpTarget;
            if (_functionStarts.Contains(target))
                sum.DirectCallees.Add(target);
            else
                sum.HasUnknownCallee = true;
        }
        else if (insn.Op == R5900Op.Jr)
        {
            if (insn.Rs != 31)
            {
                if (_jumpTablesByJr.TryGetValue(insn.Pc, out var jt))
                {
                    foreach (uint t in jt.Targets)
                    {
                        if (t <= insn.Pc && t >= start && t < end)
                            sum.HasBackwardBranch = true;
                        if (t < start || t >= end)
                        {
                            if (_functionStarts.Contains(t))
                                sum.DirectCallees.Add(t);
                            else
                                sum.HasUnknownCallee = true;
                        }
                    }
                }
                else
                {
                    sum.HasUnknownCallee = true;
                }
            }
        }
        else if (insn.Op == R5900Op.Lui && (insn.Uimm == 0x1000u || insn.Uimm == 0x1200u || insn.Uimm == 0x1100u))
        {
            // Conservative on hardware MMIO/DMA/VU registers
            sum.HasUnknownCallee = true;
        }

        GetInsnRegEffects(insn, out uint rg, out uint wg, out uint rf, out uint wf,
            out bool rfa, out bool wfa, out bool rfc, out bool wfc, out bool unknown);
        sum.ReadGpr |= rg;
        sum.WriteGpr |= wg;
        sum.ReadFpr |= rf;
        sum.WriteFpr |= wf;
        sum.ReadFAcc |= rfa;
        sum.WriteFAcc |= wfa;
        sum.ReadFcc |= rfc;
        sum.WriteFcc |= wfc;
        if (unknown) sum.HasUnknownCallee = true;
    }

    private static void ComputeEntryDeadRegs(
        List<R5900Insn> insns, int insnCount, uint start, HashSet<uint> labels,
        out uint deadGpr, out uint deadFpr, out bool deadFAcc, out bool deadFcc)
    {
        deadGpr = 0; deadFpr = 0; deadFAcc = false; deadFcc = false;
        if (labels.Contains(start)) return;

        uint seenUseGpr = 0, seenDefGpr = 0;
        uint seenUseFpr = 0, seenDefFpr = 0;
        bool seenUseFAcc = false, seenDefFAcc = false;
        bool seenUseFcc = false, seenDefFcc = false;

        for (int i = 0; i < insnCount; i++)
        {
            R5900Insn insn = insns[i];
            if (i > 0 && labels.Contains(insn.Pc)) break;

            if (insn.HasDelaySlot)
            {
                // Only include a straight-line jr $ra + its delay slot before stopping
                if (insn.Op == R5900Op.Jr && insn.Rs == 31 && i + 1 < insns.Count && !labels.Contains(insns[i + 1].Pc))
                {
                    seenUseGpr |= 1u << 31;
                    R5900Insn ds = insns[i + 1];
                    GetInsnRegEffects(ds, out uint rg, out uint wg, out uint rf, out uint wf,
                        out bool rfa, out bool wfa, out bool rfc, out bool wfc, out bool unk);
                    if (!unk)
                    {
                        seenUseGpr |= rg & ~seenDefGpr;
                        seenDefGpr |= wg & ~seenUseGpr;
                        seenUseFpr |= rf & ~seenDefFpr;
                        seenDefFpr |= wf & ~seenUseFpr;
                        if (rfa && !seenDefFAcc) seenUseFAcc = true;
                        if (wfa && !seenUseFAcc) seenDefFAcc = true;
                        if (rfc && !seenDefFcc) seenUseFcc = true;
                        if (wfc && !seenUseFcc) seenDefFcc = true;
                    }
                }
                break;
            }

            if (insn.Word == 0u) continue;
            GetInsnRegEffects(insn, out uint rG, out uint wG, out uint rF, out uint wF,
                out bool rFA, out bool wFA, out bool rFc, out bool wFc, out bool unknown);
            if (unknown) break;

            // Partial writes (Movz/Movn/Lwl/Lwr/Ldl/Ldr) have the reg in BOTH rG and wG,
            // so seenUseGpr catches it before seenDefGpr!
            seenUseGpr |= rG & ~seenDefGpr;
            seenDefGpr |= wG & ~seenUseGpr;
            seenUseFpr |= rF & ~seenDefFpr;
            seenDefFpr |= wF & ~seenUseFpr;
            if (rFA && !seenDefFAcc) seenUseFAcc = true;
            if (wFA && !seenUseFAcc) seenDefFAcc = true;
            if (rFc && !seenDefFcc) seenUseFcc = true;
            if (wFc && !seenUseFcc) seenDefFcc = true;
        }

        deadGpr = seenDefGpr & ~1u;
        deadFpr = seenDefFpr;
        deadFAcc = seenDefFAcc;
        deadFcc = seenDefFcc;
    }

    private static void GetInsnRegEffects(
        R5900Insn insn,
        out uint readGpr, out uint writeGpr,
        out uint readFpr, out uint writeFpr,
        out bool readFAcc, out bool writeFAcc,
        out bool readFcc, out bool writeFcc,
        out bool unknownEffect)
    {
        readGpr = 0; writeGpr = 0; readFpr = 0; writeFpr = 0;
        readFAcc = false; writeFAcc = false; readFcc = false; writeFcc = false;
        unknownEffect = false;
        if (insn.Word == 0u) return;

        uint rs = insn.Rs, rt = insn.Rt, rd = insn.Rd;
        uint fs = insn.Fs, ft = insn.Ft, fd = insn.Fd;
        uint bitRs = rs != 0 ? (1u << (int)rs) : 0u;
        uint bitRt = rt != 0 ? (1u << (int)rt) : 0u;
        uint bitRd = rd != 0 ? (1u << (int)rd) : 0u;

        switch (insn.Op)
        {
            case R5900Op.Beq:
            case R5900Op.Beql:
            case R5900Op.Bne:
            case R5900Op.Bnel:
                if (rs != rt) readGpr = bitRs | bitRt;
                break;
            case R5900Op.Blez:
            case R5900Op.Blezl:
            case R5900Op.Bgtz:
            case R5900Op.Bgtzl:
            case R5900Op.Bltz:
            case R5900Op.Bltzl:
            case R5900Op.Bgez:
            case R5900Op.Bgezl:
                readGpr = bitRs;
                break;
            case R5900Op.Bltzal:
            case R5900Op.Bgezal:
            case R5900Op.Bltzall:
            case R5900Op.Bgezall:
                readGpr = bitRs;
                writeGpr = 1u << 31;
                break;
            case R5900Op.Bc1f:
            case R5900Op.Bc1fl:
            case R5900Op.Bc1t:
            case R5900Op.Bc1tl:
                readFcc = true;
                break;
            case R5900Op.J:
                break;
            case R5900Op.Jal:
                writeGpr = 1u << 31;
                break;
            case R5900Op.Jr:
                readGpr = bitRs;
                break;
            case R5900Op.Jalr:
                readGpr = bitRs;
                writeGpr = bitRd;
                unknownEffect = true;
                break;

            case R5900Op.Sll:
            case R5900Op.Srl:
            case R5900Op.Sra:
            case R5900Op.Dsll:
            case R5900Op.Dsrl:
            case R5900Op.Dsra:
            case R5900Op.Dsll32:
            case R5900Op.Dsrl32:
            case R5900Op.Dsra32:
                if (rd != 0) { readGpr = bitRt; writeGpr = bitRd; }
                break;

            case R5900Op.Movz:
            case R5900Op.Movn:
                if (rd != 0) { readGpr = bitRs | bitRt | bitRd; writeGpr = bitRd; }
                break;

            case R5900Op.Sllv:
            case R5900Op.Srlv:
            case R5900Op.Srav:
            case R5900Op.Dsllv:
            case R5900Op.Dsrlv:
            case R5900Op.Dsrav:
            case R5900Op.Add:
            case R5900Op.Addu:
            case R5900Op.Sub:
            case R5900Op.Subu:
            case R5900Op.Dadd:
            case R5900Op.Daddu:
            case R5900Op.Dsub:
            case R5900Op.Dsubu:
            case R5900Op.And:
            case R5900Op.Or:
            case R5900Op.Xor:
            case R5900Op.Nor:
            case R5900Op.Slt:
            case R5900Op.Sltu:
                if (rd != 0) { readGpr = bitRs | bitRt; writeGpr = bitRd; }
                break;

            case R5900Op.Addi:
            case R5900Op.Addiu:
            case R5900Op.Daddi:
            case R5900Op.Daddiu:
            case R5900Op.Andi:
            case R5900Op.Ori:
            case R5900Op.Xori:
            case R5900Op.Slti:
            case R5900Op.Sltiu:
                if (rt != 0) { readGpr = bitRs; writeGpr = bitRt; }
                break;

            case R5900Op.Lui:
                writeGpr = bitRt;
                break;

            case R5900Op.Mult:
            case R5900Op.Multu:
            case R5900Op.Mult1:
            case R5900Op.Multu1:
            case R5900Op.Madd:
            case R5900Op.Maddu:
            case R5900Op.Msub:
            case R5900Op.Msubu:
            case R5900Op.Madd1:
            case R5900Op.Maddu1:
                readGpr = bitRs | bitRt;
                writeGpr = bitRd;
                break;
            case R5900Op.Div:
            case R5900Op.Divu:
            case R5900Op.Div1:
            case R5900Op.Divu1:
                readGpr = bitRs | bitRt;
                break;
            case R5900Op.Mfhi:
            case R5900Op.Mflo:
            case R5900Op.Mfsa:
            case R5900Op.Mfhi1:
            case R5900Op.Mflo1:
                writeGpr = bitRd;
                break;
            case R5900Op.Mthi:
            case R5900Op.Mtlo:
            case R5900Op.Mtsa:
            case R5900Op.Mtsab:
            case R5900Op.Mtsah:
            case R5900Op.Mthi1:
            case R5900Op.Mtlo1:
                readGpr = bitRs;
                break;

            case R5900Op.Lb:
            case R5900Op.Lbu:
            case R5900Op.Lh:
            case R5900Op.Lhu:
            case R5900Op.Lw:
            case R5900Op.Ll:
            case R5900Op.Lwu:
            case R5900Op.Ld:
            case R5900Op.Lq:
                readGpr = bitRs;
                writeGpr = bitRt;
                break;
            case R5900Op.Lwl:
            case R5900Op.Lwr:
            case R5900Op.Ldl:
            case R5900Op.Ldr:
                readGpr = bitRs | bitRt; // partial merge depends on previous rt
                writeGpr = bitRt;
                break;
            case R5900Op.Sb:
            case R5900Op.Sh:
            case R5900Op.Sw:
            case R5900Op.Sd:
            case R5900Op.Sq:
            case R5900Op.Swl:
            case R5900Op.Swr:
            case R5900Op.Sdl:
            case R5900Op.Sdr:
                readGpr = bitRs | bitRt;
                break;
            case R5900Op.Sc:
                readGpr = bitRs | bitRt;
                writeGpr = bitRt;
                break;
            case R5900Op.Lwc1:
                readGpr = bitRs;
                writeFpr = 1u << (int)ft;
                break;
            case R5900Op.Swc1:
                readGpr = bitRs;
                readFpr = 1u << (int)ft;
                break;
            case R5900Op.Lqc2:
            case R5900Op.Sqc2:
                readGpr = bitRs;
                break;

            case R5900Op.Mfc0:
            case R5900Op.Cfc2:
            case R5900Op.Qmfc2:
                writeGpr = bitRt;
                break;
            case R5900Op.Mtc0:
            case R5900Op.Ctc2:
            case R5900Op.Qmtc2:
                readGpr = bitRt;
                break;

            case R5900Op.Mfc1:
                readFpr = 1u << (int)fs;
                writeGpr = bitRt;
                break;
            case R5900Op.Mtc1:
                readGpr = bitRt;
                writeFpr = 1u << (int)fs;
                break;
            case R5900Op.Cfc1:
                readFcc = true;
                writeGpr = bitRt;
                break;
            case R5900Op.Ctc1:
                readGpr = bitRt;
                writeFcc = true;
                break;

            case R5900Op.AddS:
            case R5900Op.SubS:
            case R5900Op.MulS:
            case R5900Op.DivS:
            case R5900Op.RsqrtS:
            case R5900Op.MaxS:
            case R5900Op.MinS:
                readFpr = (1u << (int)fs) | (1u << (int)ft);
                writeFpr = 1u << (int)fd;
                break;
            case R5900Op.SqrtS:
                readFpr = 1u << (int)ft;
                writeFpr = 1u << (int)fd;
                break;
            case R5900Op.AbsS:
            case R5900Op.MovS:
            case R5900Op.NegS:
            case R5900Op.CvtWS:
            case R5900Op.TruncWS:
            case R5900Op.RoundWS:
            case R5900Op.CeilWS:
            case R5900Op.FloorWS:
            case R5900Op.CvtSW:
                readFpr = 1u << (int)fs;
                writeFpr = 1u << (int)fd;
                break;
            case R5900Op.AddaS:
            case R5900Op.SubaS:
            case R5900Op.MulaS:
                readFpr = (1u << (int)fs) | (1u << (int)ft);
                writeFAcc = true;
                break;
            case R5900Op.MaddS:
            case R5900Op.MsubS:
                readFpr = (1u << (int)fs) | (1u << (int)ft);
                readFAcc = true;
                writeFpr = 1u << (int)fd;
                break;
            case R5900Op.MaddaS:
            case R5900Op.MsubaS:
                readFpr = (1u << (int)fs) | (1u << (int)ft);
                readFAcc = true;
                writeFAcc = true;
                break;
            case R5900Op.CF:
            case R5900Op.CSf:
            case R5900Op.CUn:
            case R5900Op.CNgle:
                writeFcc = true;
                break;
            case R5900Op.CEq:
            case R5900Op.CUeq:
            case R5900Op.CSeq:
            case R5900Op.CNgl:
            case R5900Op.COlt:
            case R5900Op.CUlt:
            case R5900Op.CLt:
            case R5900Op.CNge:
            case R5900Op.COle:
            case R5900Op.CUle:
            case R5900Op.CLe:
            case R5900Op.CNgt:
                readFpr = (1u << (int)fs) | (1u << (int)ft);
                writeFcc = true;
                break;

            case R5900Op.Pref:
            case R5900Op.Sync:
            case R5900Op.Cache:
                break;

            // MMI 3-operand (rd, rs, rt)
            case R5900Op.Pmultw:
            case R5900Op.Pmultuw:
            case R5900Op.Pmaddw:
            case R5900Op.Pmadduw:
            case R5900Op.Pmsubw:
            case R5900Op.Pmulth:
            case R5900Op.Pmaddh:
            case R5900Op.Pmsubh:
            case R5900Op.Phmadh:
            case R5900Op.Phmsbh:
            case R5900Op.Psllvw:
            case R5900Op.Psrlvw:
            case R5900Op.Psravw:
            case R5900Op.Paddw:
            case R5900Op.Psubw:
            case R5900Op.Paddh:
            case R5900Op.Psubh:
            case R5900Op.Paddb:
            case R5900Op.Psubb:
            case R5900Op.Paddsw:
            case R5900Op.Psubsw:
            case R5900Op.Paddsh:
            case R5900Op.Psubsh:
            case R5900Op.Paddsb:
            case R5900Op.Psubsb:
            case R5900Op.Padduw:
            case R5900Op.Psubuw:
            case R5900Op.Padduh:
            case R5900Op.Psubuh:
            case R5900Op.Paddub:
            case R5900Op.Psubub:
            case R5900Op.Pmaxw:
            case R5900Op.Pminw:
            case R5900Op.Pmaxh:
            case R5900Op.Pminh:
            case R5900Op.Pceqw:
            case R5900Op.Pcgtw:
            case R5900Op.Pceqh:
            case R5900Op.Pcgth:
            case R5900Op.Pceqb:
            case R5900Op.Pcgtb:
            case R5900Op.Pand:
            case R5900Op.Por:
            case R5900Op.Pxor:
            case R5900Op.Pnor:
            case R5900Op.Pextlw:
            case R5900Op.Pextuw:
            case R5900Op.Pextlh:
            case R5900Op.Pextuh:
            case R5900Op.Pextlb:
            case R5900Op.Pextub:
            case R5900Op.Ppacw:
            case R5900Op.Ppach:
            case R5900Op.Ppacb:
            case R5900Op.Pcpyld:
            case R5900Op.Pcpyud:
            case R5900Op.Pinth:
            case R5900Op.Pinteh:
            case R5900Op.Pdivw:
            case R5900Op.Pdivuw:
            case R5900Op.Pdivbw:
            case R5900Op.Plzcw:
                readGpr = bitRs | bitRt;
                writeGpr = bitRd;
                break;

            // MMI 2-operand (rd, rt)
            case R5900Op.Psllh:
            case R5900Op.Psrlh:
            case R5900Op.Psrah:
            case R5900Op.Psllw:
            case R5900Op.Psrlw:
            case R5900Op.Psraw:
            case R5900Op.Pabsw:
            case R5900Op.Pabsh:
            case R5900Op.Pext5:
            case R5900Op.Ppac5:
            case R5900Op.Pcpyh:
            case R5900Op.Pexeh:
            case R5900Op.Pexch:
            case R5900Op.Prevh:
            case R5900Op.Pexew:
            case R5900Op.Pexcw:
            case R5900Op.Prot3w:
                readGpr = bitRt;
                writeGpr = bitRd;
                break;

            case R5900Op.Pmfhl:
                writeGpr = bitRd;
                break;
            case R5900Op.Pmthl:
                readGpr = bitRs;
                break;

            // Pure COP2/VU0 macro arithmetic (touches only VU0 VF/VI, no EE GPR/FPR)
            case R5900Op.Vadd:
            case R5900Op.Vsub:
            case R5900Op.Vmul:
            case R5900Op.Vaddbc:
            case R5900Op.Vsubbc:
            case R5900Op.Vmulbc:
            case R5900Op.Vaddq:
            case R5900Op.Vsubq:
            case R5900Op.Vmulq:
            case R5900Op.Vaddi:
            case R5900Op.Vsubi:
            case R5900Op.Vmuli:
            case R5900Op.Vmadd:
            case R5900Op.Vmsub:
            case R5900Op.Vmaddbc:
            case R5900Op.Vmsubbc:
            case R5900Op.Vmaddq:
            case R5900Op.Vmsubq:
            case R5900Op.Vmaddi:
            case R5900Op.Vmsubi:
            case R5900Op.Vmax:
            case R5900Op.Vmini:
            case R5900Op.Vmaxbc:
            case R5900Op.Vminibc:
            case R5900Op.Vmaxi:
            case R5900Op.Vminii:
            case R5900Op.Vadda:
            case R5900Op.Vsuba:
            case R5900Op.Vmula:
            case R5900Op.Vaddabc:
            case R5900Op.Vsubabc:
            case R5900Op.Vmulabc:
            case R5900Op.Vaddaq:
            case R5900Op.Vsubaq:
            case R5900Op.Vmulaq:
            case R5900Op.Vaddai:
            case R5900Op.Vsubai:
            case R5900Op.Vmulai:
            case R5900Op.Vmadda:
            case R5900Op.Vmsuba:
            case R5900Op.Vmaddabc:
            case R5900Op.Vmsubabc:
            case R5900Op.Vmaddaq:
            case R5900Op.Vmsubaq:
            case R5900Op.Vmaddai:
            case R5900Op.Vmsubai:
            case R5900Op.Vopmula:
            case R5900Op.Vopmsub:
            case R5900Op.Vitof0:
            case R5900Op.Vitof4:
            case R5900Op.Vitof12:
            case R5900Op.Vitof15:
            case R5900Op.Vftoi0:
            case R5900Op.Vftoi4:
            case R5900Op.Vftoi12:
            case R5900Op.Vftoi15:
            case R5900Op.Vabs:
            case R5900Op.Vmove:
            case R5900Op.Vmr32:
            case R5900Op.Vclip:
            case R5900Op.Vdiv:
            case R5900Op.Vsqrt:
            case R5900Op.Vrsqrt:
            case R5900Op.Vwaitq:
            case R5900Op.Vnop:
            case R5900Op.Vmtir:
            case R5900Op.Vmfir:
            case R5900Op.Vlqi:
            case R5900Op.Vsqi:
            case R5900Op.Vlqd:
            case R5900Op.Vsqd:
            case R5900Op.Vilwr:
            case R5900Op.Viswr:
            case R5900Op.Viadd:
            case R5900Op.Visub:
            case R5900Op.Viaddi:
            case R5900Op.Viand:
            case R5900Op.Vior:
            case R5900Op.Vrinit:
            case R5900Op.Vrget:
            case R5900Op.Vrnext:
            case R5900Op.Vrxor:
                break;

            default:
                unknownEffect = true;
                break;
        }
    }

    private static bool TryEmitUnalignedPair(
        StringBuilder sb, R5900Insn a, R5900Insn b, string indent, in RegPromoState p)
    {
        if (a.Rs != b.Rs || a.Rt != b.Rt) return false;
        uint rs = a.Rs, rt = a.Rt;

        // LDL/LDR pair (64-bit unaligned load)
        if ((a.Op == R5900Op.Ldl && b.Op == R5900Op.Ldr && a.Simm == b.Simm + 7) ||
            (a.Op == R5900Op.Ldr && b.Op == R5900Op.Ldl && b.Simm == a.Simm + 7))
        {
            if (rt == 0 || rt == rs) return false;
            short loSimm = a.Op == R5900Op.Ldr ? a.Simm : b.Simm;
            sb.AppendLine($"{indent}{SetGprU64(rt, $"Ps2Memory.Read64Unaligned(ctx, ram, {AddrExpr(rs, loSimm, in p)})", in p)}");
            return true;
        }

        // SDL/SDR pair (64-bit unaligned store)
        if ((a.Op == R5900Op.Sdl && b.Op == R5900Op.Sdr && a.Simm == b.Simm + 7) ||
            (a.Op == R5900Op.Sdr && b.Op == R5900Op.Sdl && b.Simm == a.Simm + 7))
        {
            short loSimm = a.Op == R5900Op.Sdr ? a.Simm : b.Simm;
            sb.AppendLine($"{indent}Ps2Memory.Write64Unaligned(ctx, ram, {AddrExpr(rs, loSimm, in p)}, {GprU64(rt, in p)});");
            return true;
        }

        // LWL/LWR pair (32-bit unaligned load, sign-extended to 64-bit)
        if ((a.Op == R5900Op.Lwl && b.Op == R5900Op.Lwr && a.Simm == b.Simm + 3) ||
            (a.Op == R5900Op.Lwr && b.Op == R5900Op.Lwl && b.Simm == a.Simm + 3))
        {
            if (rt == 0 || rt == rs) return false;
            short loSimm = a.Op == R5900Op.Lwr ? a.Simm : b.Simm;
            sb.AppendLine($"{indent}{SetGprS32(rt, $"(int)Ps2Memory.Read32Unaligned(ctx, ram, {AddrExpr(rs, loSimm, in p)})", in p)}");
            return true;
        }

        // SWL/SWR pair (32-bit unaligned store)
        if ((a.Op == R5900Op.Swl && b.Op == R5900Op.Swr && a.Simm == b.Simm + 3) ||
            (a.Op == R5900Op.Swr && b.Op == R5900Op.Swl && b.Simm == a.Simm + 3))
        {
            short loSimm = a.Op == R5900Op.Swr ? a.Simm : b.Simm;
            sb.AppendLine($"{indent}Ps2Memory.Write32Unaligned(ctx, ram, {AddrExpr(rs, loSimm, in p)}, {GprU32(rt, in p)});");
            return true;
        }

        return false;
    }

    private static string GprU32(uint r, in RegPromoState p) => r == 0 ? "0u" : (p.IsGpr(r) ? $"(uint)r{r}" : $"ctx->GetU32({r})");
    private static string GprS32(uint r, in RegPromoState p) => r == 0 ? "0" : (p.IsGpr(r) ? $"(int)r{r}" : $"ctx->GetS32({r})");
    private static string GprU64(uint r, in RegPromoState p) => r == 0 ? "0UL" : (p.IsGpr(r) ? $"r{r}" : $"ctx->GetU64({r})");
    private static string GprS64(uint r, in RegPromoState p) => r == 0 ? "0L" : (p.IsGpr(r) ? $"(long)r{r}" : $"ctx->GetS64({r})");
    private static string Fpr(uint f, in RegPromoState p) => p.IsFpr(f) ? $"f{f}" : $"ctx->F[{f}]";
    private static string FprBits(uint f, in RegPromoState p) => p.IsFpr(f) ? $"BitConverter.SingleToUInt32Bits(f{f})" : $"ctx->GetFpuBits({f})";
    private static string FAccExpr(in RegPromoState p) => p.PromoteFAcc ? "fAcc" : "ctx->FAcc";
    private static string FccExpr(in RegPromoState p) => p.PromoteFcc ? "fcc" : "ctx->Cop1Condition";
    private static string AddrExpr(uint rs, short simm, in RegPromoState p)
        => simm == 0 ? GprU32(rs, in p) : $"unchecked({GprU32(rs, in p)} + (uint)({simm}))";

    private static string SetGprS32(uint r, string intExpr, in RegPromoState p)
    {
        if (r == 0) return "";
        if (!p.IsGpr(r)) return $"ctx->SetS32({r}, {intExpr});";
        string s = $"r{r} = unchecked((ulong)(long)({intExpr}));";
        if (r is 28 or 29) s += $" ctx->SetU64({r}, r{r});";
        return s;
    }

    private static string SetGprZ32(uint r, string uintExpr, in RegPromoState p)
    {
        if (r == 0) return "";
        if (!p.IsGpr(r)) return $"ctx->SetZ32({r}, {uintExpr});";
        string s = $"r{r} = (ulong)({uintExpr});";
        if (r is 28 or 29) s += $" ctx->SetU64({r}, r{r});";
        return s;
    }

    private static string SetGprU64(uint r, string ulongExpr, in RegPromoState p)
    {
        if (r == 0) return "";
        if (!p.IsGpr(r)) return $"ctx->SetU64({r}, {ulongExpr});";
        string s = $"r{r} = {ulongExpr};";
        if (r is 28 or 29) s += $" ctx->SetU64({r}, r{r});";
        return s;
    }

    private static string SetGprS64(uint r, string longExpr, in RegPromoState p)
    {
        if (r == 0) return "";
        if (!p.IsGpr(r)) return $"ctx->SetS64({r}, {longExpr});";
        string s = $"r{r} = unchecked((ulong)({longExpr}));";
        if (r is 28 or 29) s += $" ctx->SetU64({r}, r{r});";
        return s;
    }

    private static string SetFprBits(uint f, string uintExpr, in RegPromoState p)
        => p.IsFpr(f) ? $"f{f} = BitConverter.UInt32BitsToSingle({uintExpr});" : $"ctx->SetFpuBits({f}, {uintExpr});";

    private void EmitBranchOrJumpWithDelaySlot(
        StringBuilder sb,
        FunctionMeta func,
        R5900Insn insn,
        R5900Insn delayInsn,
        bool delayHasLabel,
        in RegPromoState promo)
    {
        uint pc = insn.Pc;
        uint returnAddr = pc + 8u;

        // 1. Unconditional J / JAL
        if (insn.Op is R5900Op.J or R5900Op.Jal)
        {
            if (insn.Op == R5900Op.Jal)
            {
                sb.AppendLine($"        {SetGprU64(31, $"0x{returnAddr:X8}UL", in promo)}");
            }
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ", in promo);

            uint target = insn.JumpTarget;
            if (insn.Op == R5900Op.Jal)
            {
                EmitDirectOrDispatchedCall(sb, target, "        ", in promo);
            }
            else
            {
                EmitDirectOrDispatchedJump(sb, func, target, "        ", in promo);
            }
            return;
        }

        // 2. Register jumps: JR / JALR
        if (insn.Op is R5900Op.Jr or R5900Op.Jalr)
        {
            if (insn.IsReturn && !_jumpTablesByJr.ContainsKey(pc))
            {
                if (delayHasLabel)
                {
                    sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
                }
                EmitSingleInstruction(sb, delayInsn, "        ", in promo);
                EmitFlushPromoted(sb, in promo, "        ");
                sb.AppendLine($"        RuntimeDispatcher.OnReturn(ctx, {GprU32(31, in promo)});");
                sb.AppendLine("        return;");
                return;
            }

            if (delayHasLabel)
            {
                sb.AppendLine($"        __target_{pc:X8} = {GprU32(insn.Rs, in promo)};");
            }
            else
            {
                sb.AppendLine($"        uint __target_{pc:X8} = {GprU32(insn.Rs, in promo)};");
            }
            if (insn.Op == R5900Op.Jalr && insn.Rd != 0)
            {
                sb.AppendLine($"        {SetGprU64(insn.Rd, $"0x{returnAddr:X8}UL", in promo)}");
            }
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ", in promo);

            if (insn.Op == R5900Op.Jalr)
            {
                EmitFlushPromoted(sb, in promo, "        ");
                sb.AppendLine($"        RuntimeDispatcher.CallIndirect(__target_{pc:X8}, ctx, ram);");
                if (promo.HasAny) EmitLoadPromoted(sb, in promo, "        ", declare: false);
            }
            else if (_jumpTablesByJr.TryGetValue(pc, out JumpTableMeta? jt))
            {
                sb.AppendLine($"        switch (__target_{pc:X8})");
                sb.AppendLine("        {");
                foreach (uint target in jt.Targets.Distinct())
                {
                    if (target >= func.Start && target < func.End)
                    {
                        sb.AppendLine($"            case 0x{target:X8}u: goto L_{target:X8};");
                    }
                    else if (_functionStarts.Contains(target))
                    {
                        sb.AppendLine($"            case 0x{target:X8}u:");
                        sb.AppendLine("            {");
                        EmitFlushPromoted(sb, in promo, "                ");
                        sb.AppendLine($"                sub_{target:X8}(ctx, ram);");
                        sb.AppendLine("                return;");
                        sb.AppendLine("            }");
                    }
                    else
                    {
                        sb.AppendLine($"            case 0x{target:X8}u:");
                        sb.AppendLine("            {");
                        EmitFlushPromoted(sb, in promo, "                ");
                        sb.AppendLine($"                RuntimeDispatcher.Jump(0x{target:X8}u, ctx, ram);");
                        sb.AppendLine("                return;");
                        sb.AppendLine("            }");
                    }
                }
                sb.AppendLine("            default:");
                sb.AppendLine("            {");
                EmitFlushPromoted(sb, in promo, "                ");
                sb.AppendLine($"                RuntimeDispatcher.JumpIndirect(__target_{pc:X8}, ctx, ram);");
                sb.AppendLine("                return;");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }
            else
            {
                EmitFlushPromoted(sb, in promo, "        ");
                sb.AppendLine($"        RuntimeDispatcher.JumpIndirect(__target_{pc:X8}, ctx, ram);");
                sb.AppendLine("        return;");
            }
            return;
        }

        // 3. Conditional Branches (Standard & Branch Likely)
        string condExpr = GetBranchCondition(insn, in promo);
        string condVar = $"__cond_{pc:X8}";

        if (insn.Op is R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall)
        {
            sb.AppendLine($"        {SetGprU64(31, $"0x{returnAddr:X8}UL", in promo)}");
        }

        if (delayHasLabel)
        {
            sb.AppendLine($"        {condVar} = {condExpr};");
        }
        else
        {
            sb.AppendLine($"        bool {condVar} = {condExpr};");
        }

        uint branchTarget = insn.BranchTarget;
        if (insn.IsBranchLikely)
        {
            if (delayHasLabel)
            {
                sb.AppendLine($"        if (!{condVar}) goto L_skip_{pc:X8};");
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
                EmitSingleInstruction(sb, delayInsn, "        ", in promo);
                sb.AppendLine($"        if ({condVar})");
                sb.AppendLine("        {");
                sb.AppendLine($"            {condVar} = false;");
                EmitBranchTargetAction(sb, func, insn, branchTarget, "            ", in promo);
                sb.AppendLine("        }");
                sb.AppendLine($"    L_skip_{pc:X8}: ;");
            }
            else
            {
                sb.AppendLine($"        if ({condVar})");
                sb.AppendLine("        {");
                EmitSingleInstruction(sb, delayInsn, "            ", in promo);
                EmitBranchTargetAction(sb, func, insn, branchTarget, "            ", in promo);
                sb.AppendLine("        }");
            }
        }
        else
        {
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ", in promo);
            sb.AppendLine($"        if ({condVar})");
            sb.AppendLine("        {");
            if (delayHasLabel)
            {
                sb.AppendLine($"            {condVar} = false;");
            }
            EmitBranchTargetAction(sb, func, insn, branchTarget, "            ", in promo);
            sb.AppendLine("        }");
        }
    }

    private void EmitBranchTargetAction(
        StringBuilder sb,
        FunctionMeta func,
        R5900Insn insn,
        uint target,
        string indent,
        in RegPromoState promo)
    {
        if (insn.Op is R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall)
        {
            if (target < func.Start || target >= func.End)
            {
                EmitDirectOrDispatchedCall(sb, target, indent, in promo);
                return;
            }
        }

        EmitDirectOrDispatchedJump(sb, func, target, indent, in promo);
    }

    private void EmitDirectOrDispatchedCall(StringBuilder sb, uint target, string indent, in RegPromoState promo)
    {
        if (_functionStarts.Contains(target))
        {
            if (_summaries.TryGetValue(target, out var callee) && !callee.HasUnknownCallee)
            {
                uint touchGpr = ((callee.ReadGpr | callee.WriteGpr) & ~callee.MustDefGpr) | (1u << 31);
                uint touchFpr = (callee.ReadFpr | callee.WriteFpr) & ~callee.MustDefFpr;
                bool touchFAcc = (callee.ReadFAcc || callee.WriteFAcc) && !callee.MustDefFAcc;
                bool touchFcc = (callee.ReadFcc || callee.WriteFcc) && !callee.MustDefFcc;
                EmitFlushPromotedMasked(
                    sb,
                    promo.WrittenGpr & touchGpr,
                    promo.WrittenFpr & touchFpr,
                    promo.WrittenFAcc && touchFAcc,
                    promo.WrittenFcc && touchFcc,
                    indent);
                sb.AppendLine($"{indent}sub_{target:X8}(ctx, ram);");
                EmitLoadPromotedMasked(
                    sb,
                    promo.PromotedGpr & callee.WriteGpr,
                    promo.PromotedFpr & callee.WriteFpr,
                    promo.PromoteFAcc && callee.WriteFAcc,
                    promo.PromoteFcc && callee.WriteFcc,
                    indent,
                    declare: false);
                return;
            }
            EmitFlushPromoted(sb, in promo, indent);
            sb.AppendLine($"{indent}sub_{target:X8}(ctx, ram);");
        }
        else
        {
            EmitFlushPromoted(sb, in promo, indent);
            sb.AppendLine($"{indent}RuntimeDispatcher.Call(0x{target:X8}u, ctx, ram);");
        }
        if (promo.HasAny) EmitLoadPromoted(sb, in promo, indent, declare: false);
    }

    private void EmitDirectOrDispatchedJump(StringBuilder sb, FunctionMeta func, uint target, string indent, in RegPromoState promo)
    {
        if (target >= func.Start && target < func.End)
        {
            sb.AppendLine($"{indent}goto L_{target:X8};");
        }
        else if (_functionStarts.Contains(target))
        {
            EmitFlushPromoted(sb, in promo, indent);
            sb.AppendLine($"{indent}sub_{target:X8}(ctx, ram);");
            sb.AppendLine($"{indent}return;");
        }
        else
        {
            EmitFlushPromoted(sb, in promo, indent);
            sb.AppendLine($"{indent}RuntimeDispatcher.Jump(0x{target:X8}u, ctx, ram);");
            sb.AppendLine($"{indent}return;");
        }
    }

    private static string GetBranchCondition(R5900Insn insn, in RegPromoState p)
    {
        uint rs = insn.Rs;
        uint rt = insn.Rt;

        return insn.Op switch
        {
            R5900Op.Beq or R5900Op.Beql =>
                rs == rt ? "true" : $"({GprU64(rs, in p)} == {GprU64(rt, in p)})",
            R5900Op.Bne or R5900Op.Bnel =>
                rs == rt ? "false" : $"({GprU64(rs, in p)} != {GprU64(rt, in p)})",
            R5900Op.Blez or R5900Op.Blezl =>
                rs == 0 ? "true" : $"({GprS64(rs, in p)} <= 0L)",
            R5900Op.Bgtz or R5900Op.Bgtzl =>
                rs == 0 ? "false" : $"({GprS64(rs, in p)} > 0L)",
            R5900Op.Bltz or R5900Op.Bltzl or R5900Op.Bltzal or R5900Op.Bltzall =>
                rs == 0 ? "false" : $"({GprS64(rs, in p)} < 0L)",
            R5900Op.Bgez or R5900Op.Bgezl or R5900Op.Bgezal or R5900Op.Bgezall =>
                rs == 0 ? "true" : $"({GprS64(rs, in p)} >= 0L)",
            R5900Op.Bc1f or R5900Op.Bc1fl => $"!{FccExpr(in p)}",
            R5900Op.Bc1t or R5900Op.Bc1tl => FccExpr(in p),
            R5900Op.Bc0f or R5900Op.Bc0fl => "!ctx->Cop0Condition",
            R5900Op.Bc0t or R5900Op.Bc0tl => "ctx->Cop0Condition",
            R5900Op.Bc2f or R5900Op.Bc2fl => "(Ps2Vu0.Cfc2(ctx, 29) == 0u)",
            R5900Op.Bc2t or R5900Op.Bc2tl => "(Ps2Vu0.Cfc2(ctx, 29) != 0u)",
            _ => "false"
        };
    }

    public static void EmitSingleInstruction(StringBuilder sb, R5900Insn insn, string indent)
    {
        RegPromoState none = default;
        EmitSingleInstruction(sb, insn, indent, in none);
    }

    public static void EmitSingleInstruction(StringBuilder sb, R5900Insn insn, string indent, in RegPromoState p)
    {
        string spr = p.UsesScratchpad ? "Spr" : "";
        if (insn.Word == 0u)
        {
            return; // NOP
        }

        uint rs = insn.Rs;
        uint rt = insn.Rt;
        uint rd = insn.Rd;
        uint sa = insn.Sa;
        uint fs = insn.Fs;
        uint ft = insn.Ft;
        uint fd = insn.Fd;
        short simm = insn.Simm;
        ushort uimm = insn.Uimm;
        uint dest = insn.Dest;
        uint bc = insn.Bc;

        switch (insn.Op)
        {
            // ====================================================================
            // Shifts & Standard Integer ALU
            // ====================================================================
            case R5900Op.Sll:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"(int)({GprU32(rt, in p)} << {(int)sa})", in p)}");
                break;
            case R5900Op.Srl:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"(int)({GprU32(rt, in p)} >> {(int)sa})", in p)}");
                break;
            case R5900Op.Sra:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"{GprS32(rt, in p)} >> {(int)sa}", in p)}");
                break;
            case R5900Op.Sllv:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"(int)({GprU32(rt, in p)} << (int)({GprU32(rs, in p)} & 31u))", in p)}");
                break;
            case R5900Op.Srlv:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"(int)({GprU32(rt, in p)} >> (int)({GprU32(rs, in p)} & 31u))", in p)}");
                break;
            case R5900Op.Srav:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"{GprS32(rt, in p)} >> (int)({GprU32(rs, in p)} & 31u)", in p)}");
                break;

            case R5900Op.Dsll:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} << {(int)sa}", in p)}");
                break;
            case R5900Op.Dsrl:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} >> {(int)sa}", in p)}");
                break;
            case R5900Op.Dsra:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS64(rd, $"{GprS64(rt, in p)} >> {(int)sa}", in p)}");
                break;
            case R5900Op.Dsll32:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} << {(int)(sa + 32u)}", in p)}");
                break;
            case R5900Op.Dsrl32:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} >> {(int)(sa + 32u)}", in p)}");
                break;
            case R5900Op.Dsra32:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS64(rd, $"{GprS64(rt, in p)} >> {(int)(sa + 32u)}", in p)}");
                break;
            case R5900Op.Dsllv:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} << (int)({GprU32(rs, in p)} & 63u)", in p)}");
                break;
            case R5900Op.Dsrlv:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rt, in p)} >> (int)({GprU32(rs, in p)} & 63u)", in p)}");
                break;
            case R5900Op.Dsrav:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS64(rd, $"{GprS64(rt, in p)} >> (int)({GprU32(rs, in p)} & 63u)", in p)}");
                break;

            case R5900Op.Movz:
                if (rd != 0)
                    sb.AppendLine($"{indent}if ({GprU64(rt, in p)} == 0UL) {{ {SetGprU64(rd, GprU64(rs, in p), in p)} }}");
                break;
            case R5900Op.Movn:
                if (rd != 0)
                    sb.AppendLine($"{indent}if ({GprU64(rt, in p)} != 0UL) {{ {SetGprU64(rd, GprU64(rs, in p), in p)} }}");
                break;

            case R5900Op.Add:
            case R5900Op.Addu:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"unchecked({GprS32(rs, in p)} + {GprS32(rt, in p)})", in p)}");
                break;
            case R5900Op.Sub:
            case R5900Op.Subu:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rd, $"unchecked({GprS32(rs, in p)} - {GprS32(rt, in p)})", in p)}");
                break;
            case R5900Op.Dadd:
            case R5900Op.Daddu:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"unchecked({GprU64(rs, in p)} + {GprU64(rt, in p)})", in p)}");
                break;
            case R5900Op.Dsub:
            case R5900Op.Dsubu:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"unchecked({GprU64(rs, in p)} - {GprU64(rt, in p)})", in p)}");
                break;

            case R5900Op.Addi:
            case R5900Op.Addiu:
                if (rt != 0)
                {
                    if (rs == 0)
                        sb.AppendLine($"{indent}{SetGprS32(rt, $"{simm}", in p)}");
                    else
                        sb.AppendLine($"{indent}{SetGprS32(rt, $"unchecked({GprS32(rs, in p)} + ({simm}))", in p)}");
                }
                break;
            case R5900Op.Daddi:
            case R5900Op.Daddiu:
                if (rt != 0)
                {
                    if (rs == 0)
                        sb.AppendLine($"{indent}{SetGprS64(rt, $"{simm}L", in p)}");
                    else
                        sb.AppendLine($"{indent}{SetGprS64(rt, $"unchecked({GprS64(rs, in p)} + ({simm}L))", in p)}");
                }
                break;

            case R5900Op.And:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rs, in p)} & {GprU64(rt, in p)}", in p)}");
                break;
            case R5900Op.Or:
                if (rd != 0)
                {
                    if (rs == 0 && rt == 0)
                        sb.AppendLine($"{indent}{SetGprU64(rd, "0UL", in p)}");
                    else if (rt == 0)
                        sb.AppendLine($"{indent}{SetGprU64(rd, GprU64(rs, in p), in p)}");
                    else if (rs == 0)
                        sb.AppendLine($"{indent}{SetGprU64(rd, GprU64(rt, in p), in p)}");
                    else
                        sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rs, in p)} | {GprU64(rt, in p)}", in p)}");
                }
                break;
            case R5900Op.Xor:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rs, in p)} ^ {GprU64(rt, in p)}", in p)}");
                break;
            case R5900Op.Nor:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"~({GprU64(rs, in p)} | {GprU64(rt, in p)})", in p)}");
                break;

            case R5900Op.Andi:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"{GprU64(rs, in p)} & 0x{uimm:X4}UL", in p)}");
                break;
            case R5900Op.Ori:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"{GprU64(rs, in p)} | 0x{uimm:X4}UL", in p)}");
                break;
            case R5900Op.Xori:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"{GprU64(rs, in p)} ^ 0x{uimm:X4}UL", in p)}");
                break;
            case R5900Op.Lui:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"unchecked((int)0x{((uint)uimm << 16):X8}u)", in p)}");
                break;

            case R5900Op.Slt:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprS64(rs, in p)} < {GprS64(rt, in p)} ? 1UL : 0UL", in p)}");
                break;
            case R5900Op.Sltu:
                if (rd != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rd, $"{GprU64(rs, in p)} < {GprU64(rt, in p)} ? 1UL : 0UL", in p)}");
                break;
            case R5900Op.Slti:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"{GprS64(rs, in p)} < ({simm}L) ? 1UL : 0UL", in p)}");
                break;
            case R5900Op.Sltiu:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"{GprU64(rs, in p)} < unchecked((ulong)(long)({simm})) ? 1UL : 0UL", in p)}");
                break;

            // ====================================================================
            // Multiply / Divide & HI/LO/SA
            // ====================================================================
            case R5900Op.Mult:
                sb.AppendLine($"{indent}{{ long __p = (long){GprS32(rs, in p)} * (long){GprS32(rt, in p)}; ctx->LO = (ulong)(long)(int)__p; ctx->HI = (ulong)(long)(int)(__p >> 32); {(rd != 0 ? SetGprS32(rd, "(int)__p", in p) : "")} }}");
                break;
            case R5900Op.Multu:
                sb.AppendLine($"{indent}{{ ulong __p = (ulong){GprU32(rs, in p)} * (ulong){GprU32(rt, in p)}; ctx->LO = (ulong)(long)(int)(uint)__p; ctx->HI = (ulong)(long)(int)(uint)(__p >> 32); {(rd != 0 ? SetGprS32(rd, "(int)(uint)__p", in p) : "")} }}");
                break;
            case R5900Op.Div:
                sb.AppendLine($"{indent}{{ int __a = {GprS32(rs, in p)}, __b = {GprS32(rt, in p)}; if (__b == 0) {{ ctx->LO = __a < 0 ? 1UL : ulong.MaxValue; ctx->HI = (ulong)(long)__a; }} else if (__a == int.MinValue && __b == -1) {{ ctx->LO = unchecked((ulong)(long)int.MinValue); ctx->HI = 0UL; }} else {{ ctx->LO = (ulong)(long)(__a / __b); ctx->HI = (ulong)(long)(__a % __b); }} }}");
                break;
            case R5900Op.Divu:
                sb.AppendLine($"{indent}{{ uint __a = {GprU32(rs, in p)}, __b = {GprU32(rt, in p)}; if (__b == 0) {{ ctx->LO = ulong.MaxValue; ctx->HI = (ulong)(long)(int)__a; }} else {{ ctx->LO = (ulong)(long)(int)(__a / __b); ctx->HI = (ulong)(long)(int)(__a % __b); }} }}");
                break;

            case R5900Op.Mfhi:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprU64(rd, "ctx->HI", in p)}");
                break;
            case R5900Op.Mthi:
                sb.AppendLine($"{indent}ctx->HI = {GprU64(rs, in p)};");
                break;
            case R5900Op.Mflo:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprU64(rd, "ctx->LO", in p)}");
                break;
            case R5900Op.Mtlo:
                sb.AppendLine($"{indent}ctx->LO = {GprU64(rs, in p)};");
                break;
            case R5900Op.Mfsa:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprU64(rd, "ctx->SA", in p)}");
                break;
            case R5900Op.Mtsa:
                sb.AppendLine($"{indent}ctx->SA = {GprU32(rs, in p)} & 15u;");
                break;
            case R5900Op.Mtsab:
                sb.AppendLine($"{indent}ctx->SA = (({GprU32(rs, in p)} ^ (uint)(ushort)({simm})) & 15u) * 8u;");
                break;
            case R5900Op.Mtsah:
                sb.AppendLine($"{indent}ctx->SA = (({GprU32(rs, in p)} ^ (uint)(ushort)({simm})) & 7u) * 16u;");
                break;

            // ====================================================================
            // Memory Loads & Stores (32 MB Mask + Scratchpad + Unaligned Helpers)
            // ====================================================================
            case R5900Op.Lb:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(sbyte)Ps2Memory.Read8{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read8{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lbu:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprZ32(rt, $"Ps2Memory.Read8{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read8{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lh:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(short)Ps2Memory.Read16{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read16{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lhu:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprZ32(rt, $"Ps2Memory.Read16{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read16{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lw:
            case R5900Op.Ll:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(int)Ps2Memory.Read32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lwu:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprZ32(rt, $"Ps2Memory.Read32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Ld:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprU64(rt, $"Ps2Memory.Read64{spr}(ctx, ram, {AddrExpr(rs, simm, in p)})", in p)}");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read64{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lq:
                if (rt != 0)
                {
                    if (p.IsGpr(rt))
                        sb.AppendLine($"{indent}{{ var __lq = Ps2Memory.Read128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}); ctx->SetVec({rt}, __lq); r{rt} = __lq.AsUInt64().GetElement(0); }}");
                    else
                        sb.AppendLine($"{indent}ctx->SetVec({rt}, Ps2Memory.Read128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}));");
                }
                break;

            case R5900Op.Lwl:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.LoadWordLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)}); r{rt} = ctx->GetU64({rt});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.LoadWordLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Lwr:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.LoadWordRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)}); r{rt} = ctx->GetU64({rt});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.LoadWordRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Ldl:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.LoadDoubleLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)}); r{rt} = ctx->GetU64({rt});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.LoadDoubleLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Ldr:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.LoadDoubleRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)}); r{rt} = ctx->GetU64({rt});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.LoadDoubleRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;

            case R5900Op.Sb:
                sb.AppendLine($"{indent}Ps2Memory.Write8{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, (byte){GprU32(rt, in p)});");
                break;
            case R5900Op.Sh:
                sb.AppendLine($"{indent}Ps2Memory.Write16{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, (ushort){GprU32(rt, in p)});");
                break;
            case R5900Op.Sw:
                sb.AppendLine($"{indent}Ps2Memory.Write32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Sc:
                sb.AppendLine($"{indent}Ps2Memory.Write32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, {GprU32(rt, in p)});");
                if (rt != 0) sb.AppendLine($"{indent}{SetGprU64(rt, "1UL", in p)}");
                break;
            case R5900Op.Sd:
                sb.AppendLine($"{indent}Ps2Memory.Write64{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, {GprU64(rt, in p)});");
                break;
            case R5900Op.Sq:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.Write128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, ctx->GetVec({rt}));");
                else
                    sb.AppendLine($"{indent}Ps2Memory.Write128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, ctx->GetVec({rt}));");
                break;

            case R5900Op.Swl:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.StoreWordLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.StoreWordLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Swr:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.StoreWordRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.StoreWordRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Sdl:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.StoreDoubleLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.StoreDoubleLeft(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Sdr:
                if (p.IsGpr(rt))
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, r{rt}); Ps2Memory.StoreDoubleRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                else
                    sb.AppendLine($"{indent}Ps2Memory.StoreDoubleRight(ctx, ram, {(int)rt}, {AddrExpr(rs, simm, in p)});");
                break;

            case R5900Op.Lwc1:
                sb.AppendLine($"{indent}{Fpr(ft, in p)} = Ps2Memory.ReadF32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)});");
                break;
            case R5900Op.Swc1:
                sb.AppendLine($"{indent}Ps2Memory.WriteF32{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.Lqc2:
                if (ft != 0)
                    sb.AppendLine($"{indent}ctx->VF[{ft}] = Ps2Memory.Read128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}).AsSingle();");
                break;
            case R5900Op.Sqc2:
                sb.AppendLine($"{indent}Ps2Memory.Write128{spr}(ctx, ram, {AddrExpr(rs, simm, in p)}, ctx->ReadVf({ft}).AsUInt32());");
                break;

            // ====================================================================
            // System, Traps, Cache & COP0
            // ====================================================================
            case R5900Op.Syscall:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Syscall(ctx, ram, 0x{((insn.Word >> 6) & 0xFFFFFu):X5}u);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent, declare: false);
                break;
            case R5900Op.Break:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Break(ctx, ram, 0x{((insn.Word >> 6) & 0xFFFFFu):X5}u);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent, declare: false);
                break;
            case R5900Op.Sync:
            case R5900Op.Cache:
            case R5900Op.Pref:
            case R5900Op.Tlbr:
            case R5900Op.Tlbwi:
            case R5900Op.Tlbwr:
            case R5900Op.Tlbp:
                break;
            case R5900Op.Eret:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Eret(ctx); return;");
                break;
            case R5900Op.Ei:
                sb.AppendLine($"{indent}ctx->Cop0[12] |= 0x10000u;");
                break;
            case R5900Op.Di:
                sb.AppendLine($"{indent}ctx->Cop0[12] &= ~0x10000u;");
                break;
            case R5900Op.Mfc0:
                if (rt != 0)
                {
                    if (rd == 9)
                        sb.AppendLine($"{indent}{SetGprS32(rt, "(int)ctx->ReadCop0Count()", in p)}");
                    else
                        sb.AppendLine($"{indent}{SetGprS32(rt, $"(int)ctx->Cop0[{rd}]", in p)}");
                }
                break;
            case R5900Op.Mtc0:
                if (rd == 9)
                    sb.AppendLine($"{indent}ctx->WriteCop0Count({GprU32(rt, in p)});");
                else
                    sb.AppendLine($"{indent}ctx->Cop0[{rd}] = {GprU32(rt, in p)};");
                break;

            case R5900Op.Tge:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} >= {GprS64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tgeu:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} >= {GprU64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tlt:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} < {GprS64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tltu:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} < {GprU64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Teq:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} == {GprU64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tne:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} != {GprU64(rt, in p)}) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tgei:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} >= ({simm}L)) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tgeiu:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} >= unchecked((ulong)(long)({simm}))) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tlti:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} < ({simm}L)) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tltiu:
                sb.AppendLine($"{indent}if ({GprU64(rs, in p)} < unchecked((ulong)(long)({simm}))) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Teqi:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} == ({simm}L)) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;
            case R5900Op.Tnei:
                sb.AppendLine($"{indent}if ({GprS64(rs, in p)} != ({simm}L)) {{");
                EmitFlushPromoted(sb, in p, indent + "    ");
                sb.AppendLine($"{indent}    RuntimeDispatcher.Trap(ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "    ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;

            // ====================================================================
            // COP1 (FPU — Clamped PS2 Float & Truncate Round-Toward-Zero)
            // ====================================================================
            case R5900Op.Mfc1:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(int){FprBits(fs, in p)}", in p)}");
                break;
            case R5900Op.Mtc1:
                sb.AppendLine($"{indent}{SetFprBits(fs, GprU32(rt, in p), in p)}");
                break;
            case R5900Op.Cfc1:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(int){(fs == 0 ? "ctx->Fcr0" : "ctx->Fcr31")}", in p)}");
                break;
            case R5900Op.Ctc1:
                if (fs == 31)
                    sb.AppendLine($"{indent}ctx->Fcr31 = {GprU32(rt, in p)};");
                break;

            case R5900Op.AddS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Add({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.SubS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Sub({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.MulS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.DivS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Div({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.SqrtS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Sqrt({Fpr(ft, in p)});");
                break;
            case R5900Op.RsqrtS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Rsqrt({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.AbsS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Abs({Fpr(fs, in p)});");
                break;
            case R5900Op.MovS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = {Fpr(fs, in p)};");
                break;
            case R5900Op.NegS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Neg({Fpr(fs, in p)});");
                break;
            case R5900Op.AddaS:
                sb.AppendLine($"{indent}{FAccExpr(in p)} = Ps2Float.Add({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.SubaS:
                sb.AppendLine($"{indent}{FAccExpr(in p)} = Ps2Float.Sub({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.MulaS:
                sb.AppendLine($"{indent}{FAccExpr(in p)} = Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.MaddS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Add({FAccExpr(in p)}, Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)}));");
                break;
            case R5900Op.MsubS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Sub({FAccExpr(in p)}, Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)}));");
                break;
            case R5900Op.MaddaS:
                sb.AppendLine($"{indent}{FAccExpr(in p)} = Ps2Float.Add({FAccExpr(in p)}, Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)}));");
                break;
            case R5900Op.MsubaS:
                sb.AppendLine($"{indent}{FAccExpr(in p)} = Ps2Float.Sub({FAccExpr(in p)}, Ps2Float.Mul({Fpr(fs, in p)}, {Fpr(ft, in p)}));");
                break;
            case R5900Op.CvtWS:
            case R5900Op.TruncWS:
            case R5900Op.RoundWS:
            case R5900Op.CeilWS:
            case R5900Op.FloorWS:
                sb.AppendLine($"{indent}{SetFprBits(fd, $"(uint)Ps2Float.CvtWS({Fpr(fs, in p)})", in p)}");
                break;
            case R5900Op.CvtSW:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.CvtSW((int){FprBits(fs, in p)});");
                break;
            case R5900Op.MaxS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Max({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.MinS:
                sb.AppendLine($"{indent}{Fpr(fd, in p)} = Ps2Float.Min({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.CF:
            case R5900Op.CSf:
            case R5900Op.CUn:
            case R5900Op.CNgle:
                sb.AppendLine($"{indent}{FccExpr(in p)} = false;");
                break;
            case R5900Op.CEq:
            case R5900Op.CUeq:
            case R5900Op.CSeq:
            case R5900Op.CNgl:
                sb.AppendLine($"{indent}{FccExpr(in p)} = Ps2Float.CompareEq({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.COlt:
            case R5900Op.CUlt:
            case R5900Op.CLt:
            case R5900Op.CNge:
                sb.AppendLine($"{indent}{FccExpr(in p)} = Ps2Float.CompareLt({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;
            case R5900Op.COle:
            case R5900Op.CUle:
            case R5900Op.CLe:
            case R5900Op.CNgt:
                sb.AppendLine($"{indent}{FccExpr(in p)} = Ps2Float.CompareLe({Fpr(fs, in p)}, {Fpr(ft, in p)});");
                break;

            // ====================================================================
            // Multimedia Instructions (MMI — 128-bit Emotion Engine SIMD)
            // ====================================================================
            case R5900Op.Madd:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.MaddVal(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.MaddVal(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)});");
                break;
            case R5900Op.Maddu:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.MadduVal(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.MadduVal(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Msub:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.MsubVal(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.MsubVal(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)});");
                break;
            case R5900Op.Msubu:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.MsubuVal(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.MsubuVal(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Madd1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.Madd1Val(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.Madd1Val(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)});");
                break;
            case R5900Op.Maddu1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.Maddu1Val(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.Maddu1Val(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Mult1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.Mult1Val(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.Mult1Val(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)});");
                break;
            case R5900Op.Multu1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprS32(rd, $"Ps2Mmi.Multu1Val(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)})", in p)}");
                else sb.AppendLine($"{indent}_ = Ps2Mmi.Multu1Val(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Div1:
                sb.AppendLine($"{indent}Ps2Mmi.Div1Val(ctx, {GprS32(rs, in p)}, {GprS32(rt, in p)});");
                break;
            case R5900Op.Divu1:
                sb.AppendLine($"{indent}Ps2Mmi.Divu1Val(ctx, {GprU32(rs, in p)}, {GprU32(rt, in p)});");
                break;
            case R5900Op.Mfhi1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprU64(rd, "ctx->HI1", in p)}");
                break;
            case R5900Op.Mthi1:
                sb.AppendLine($"{indent}ctx->HI1 = {GprU64(rs, in p)};");
                break;
            case R5900Op.Mflo1:
                if (rd != 0) sb.AppendLine($"{indent}{SetGprU64(rd, "ctx->LO1", in p)}");
                break;
            case R5900Op.Mtlo1:
                sb.AppendLine($"{indent}ctx->LO1 = {GprU64(rs, in p)};");
                break;
            case R5900Op.Plzcw:
                sb.AppendLine($"{indent}Ps2Mmi.Plzcw(ctx, {(int)rd}, {(int)rs});");
                break;
            case R5900Op.Pmfhl:
                sb.AppendLine($"{indent}Ps2Mmi.Pmfhl(ctx, {(int)rd}, {sa}u);");
                break;
            case R5900Op.Pmthl:
                sb.AppendLine($"{indent}Ps2Mmi.Pmthl(ctx, {(int)rs});");
                break;
            case R5900Op.Psllh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psllh(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psrlh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psrlh(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psrah:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psrah(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psllw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psllw(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psrlw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psrlw(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psraw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psraw(ctx->GetVec({rt}), {(int)sa}));");
                break;
            case R5900Op.Psllvw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psllvw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psrlvw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psrlvw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psravw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psravw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddsw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddsw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubsw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubsw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddsh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddsh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubsh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubsh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddsb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddsb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubsb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubsb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Padduw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Padduw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubuw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubuw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Padduh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Padduh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubuh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubuh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Paddub:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Paddub(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Psubub:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Psubub(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcgtw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcgtw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcgth:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcgth(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcgtb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcgtb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pceqw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pceqw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pceqh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pceqh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pceqb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pceqb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pmaxw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pmaxw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pmaxh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pmaxh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pminw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pminw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pminh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pminh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pabsw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pabsw(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pabsh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pabsh(ctx->GetVec({rt})));");
                break;
            case R5900Op.Padsbh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Padsbh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextlw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextlw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextuw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextuw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextlh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextlh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextuh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextuh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextlb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextlb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pextub:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pextub(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pext5:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pext5(ctx->GetVec({rt})));");
                break;
            case R5900Op.Ppacw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Ppacw(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Ppach:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Ppach(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Ppacb:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Ppacb(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Ppac5:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Ppac5(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pinth:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pinth(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pinteh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pinteh(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcpyld:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcpyld(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcpyud:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcpyud(ctx->GetVec({rs}), ctx->GetVec({rt})));");
                break;
            case R5900Op.Pcpyh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pcpyh(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pexeh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pexeh(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pexch:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pexch(ctx->GetVec({rt})));");
                break;
            case R5900Op.Prevh:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Prevh(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pexew:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pexew(ctx->GetVec({rt})));");
                break;
            case R5900Op.Pexcw:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Pexcw(ctx->GetVec({rt})));");
                break;
            case R5900Op.Prot3w:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Prot3w(ctx->GetVec({rt})));");
                break;
            case R5900Op.Qfsrv:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, Ps2Mmi.Qfsrv(ctx->GetVec({rs}), ctx->GetVec({rt}), ctx->SA));");
                break;
            case R5900Op.Pand:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, ctx->GetVec({rs}) & ctx->GetVec({rt}));");
                break;
            case R5900Op.Por:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, ctx->GetVec({rs}) | ctx->GetVec({rt}));");
                break;
            case R5900Op.Pxor:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, ctx->GetVec({rs}) ^ ctx->GetVec({rt}));");
                break;
            case R5900Op.Pnor:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetVec({rd}, ~(ctx->GetVec({rs}) | ctx->GetVec({rt})));");
                break;
            case R5900Op.Pmfhi:
                sb.AppendLine($"{indent}Ps2Mmi.Pmfhi(ctx, {(int)rd});");
                break;
            case R5900Op.Pmflo:
                sb.AppendLine($"{indent}Ps2Mmi.Pmflo(ctx, {(int)rd});");
                break;
            case R5900Op.Pmthi:
                sb.AppendLine($"{indent}Ps2Mmi.Pmthi(ctx, {(int)rs});");
                break;
            case R5900Op.Pmtlo:
                sb.AppendLine($"{indent}Ps2Mmi.Pmtlo(ctx, {(int)rs});");
                break;
            case R5900Op.Pmultw:
                sb.AppendLine($"{indent}Ps2Mmi.Pmultw(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmultuw:
                sb.AppendLine($"{indent}Ps2Mmi.Pmultuw(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmaddw:
                sb.AppendLine($"{indent}Ps2Mmi.Pmaddw(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmadduw:
                sb.AppendLine($"{indent}Ps2Mmi.Pmadduw(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmsubw:
                sb.AppendLine($"{indent}Ps2Mmi.Pmsubw(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pdivw:
                sb.AppendLine($"{indent}Ps2Mmi.Pdivw(ctx, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pdivuw:
                sb.AppendLine($"{indent}Ps2Mmi.Pdivuw(ctx, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pdivbw:
                sb.AppendLine($"{indent}Ps2Mmi.Pdivbw(ctx, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmulth:
                sb.AppendLine($"{indent}Ps2Mmi.Pmulth(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmaddh:
                sb.AppendLine($"{indent}Ps2Mmi.Pmaddh(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Pmsubh:
                sb.AppendLine($"{indent}Ps2Mmi.Pmsubh(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Phmadh:
                sb.AppendLine($"{indent}Ps2Mmi.Phmadh(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Phmsbh:
                sb.AppendLine($"{indent}Ps2Mmi.Phmsbh(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;

            // ====================================================================
            // COP2 (VU0 Macro Mode SIMD)
            // ====================================================================
            case R5900Op.Qmfc2:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetVec({rt}, ctx->ReadVf({fs}).AsUInt32());");
                break;
            case R5900Op.Qmtc2:
                if (fs != 0)
                    sb.AppendLine($"{indent}ctx->VF[{fs}] = ctx->GetVec({rt}).AsSingle();");
                break;
            case R5900Op.Cfc2:
                if (rt != 0)
                    sb.AppendLine($"{indent}{SetGprS32(rt, $"(int)Ps2Vu0.Cfc2(ctx, {(int)fs})", in p)}");
                break;
            case R5900Op.Ctc2:
                sb.AppendLine($"{indent}Ps2Vu0.Ctc2(ctx, {(int)fs}, {GprU32(rt, in p)});");
                break;

            case R5900Op.Vadd:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vsub:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vmul:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vmax:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMax(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vmini:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMin(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;

            case R5900Op.Vaddbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vsubbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vmulbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vmaxbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMax(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vminibc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMin(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;

            case R5900Op.Vaddq:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vsubq:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vmulq:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vaddi:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vsubi:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vmuli:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vmaxi:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMax(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vminii:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Vu0.VecMin(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;

            case R5900Op.Vmadd:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft}))), {dest}u);");
                break;
            case R5900Op.Vmsub:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft}))), {dest}u);");
                break;
            case R5900Op.Vmaddbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u))), {dest}u);");
                break;
            case R5900Op.Vmsubbc:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u))), {dest}u);");
                break;
            case R5900Op.Vmaddq:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q))), {dest}u);");
                break;
            case R5900Op.Vmsubq:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q))), {dest}u);");
                break;
            case R5900Op.Vmaddi:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I))), {dest}u);");
                break;
            case R5900Op.Vmsubi:
                sb.AppendLine($"{indent}Ps2Vu0.SetVf(ctx, {(int)fd}, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I))), {dest}u);");
                break;

            case R5900Op.Vadda:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vsuba:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vmula:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft})), {dest}u);");
                break;
            case R5900Op.Vaddabc:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vsubabc:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vmulabc:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u)), {dest}u);");
                break;
            case R5900Op.Vaddaq:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vsubaq:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vmulaq:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q)), {dest}u);");
                break;
            case R5900Op.Vaddai:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vsubai:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;
            case R5900Op.Vmulai:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I)), {dest}u);");
                break;

            case R5900Op.Vmadda:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft}))), {dest}u);");
                break;
            case R5900Op.Vmsuba:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), ctx->ReadVf({ft}))), {dest}u);");
                break;
            case R5900Op.Vmaddabc:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u))), {dest}u);");
                break;
            case R5900Op.Vmsubabc:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Ps2Vu0.BroadcastBc(ctx->ReadVf({ft}), {bc}u))), {dest}u);");
                break;
            case R5900Op.Vmaddaq:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q))), {dest}u);");
                break;
            case R5900Op.Vmsubaq:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0Q))), {dest}u);");
                break;
            case R5900Op.Vmaddai:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecAdd(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I))), {dest}u);");
                break;
            case R5900Op.Vmsubai:
                sb.AppendLine($"{indent}Ps2Vu0.SetAcc(ctx, Ps2Float.VecSub(ctx->Vu0Acc, Ps2Float.VecMul(ctx->ReadVf({fs}), Vector128.Create(ctx->Vu0I))), {dest}u);");
                break;

            case R5900Op.Vopmula:
                sb.AppendLine($"{indent}Ps2Vu0.Vopmula(ctx, {(int)fs}, {(int)ft}, {dest}u);");
                break;
            case R5900Op.Vopmsub:
                sb.AppendLine($"{indent}Ps2Vu0.Vopmsub(ctx, {(int)fd}, {(int)fs}, {(int)ft}, {dest}u);");
                break;

            case R5900Op.Vitof0:
                sb.AppendLine($"{indent}Ps2Vu0.Vitof(ctx, {(int)ft}, {(int)fs}, 0, {dest}u);");
                break;
            case R5900Op.Vitof4:
                sb.AppendLine($"{indent}Ps2Vu0.Vitof(ctx, {(int)ft}, {(int)fs}, 4, {dest}u);");
                break;
            case R5900Op.Vitof12:
                sb.AppendLine($"{indent}Ps2Vu0.Vitof(ctx, {(int)ft}, {(int)fs}, 12, {dest}u);");
                break;
            case R5900Op.Vitof15:
                sb.AppendLine($"{indent}Ps2Vu0.Vitof(ctx, {(int)ft}, {(int)fs}, 15, {dest}u);");
                break;

            case R5900Op.Vftoi0:
                sb.AppendLine($"{indent}Ps2Vu0.Vftoi(ctx, {(int)ft}, {(int)fs}, 0, {dest}u);");
                break;
            case R5900Op.Vftoi4:
                sb.AppendLine($"{indent}Ps2Vu0.Vftoi(ctx, {(int)ft}, {(int)fs}, 4, {dest}u);");
                break;
            case R5900Op.Vftoi12:
                sb.AppendLine($"{indent}Ps2Vu0.Vftoi(ctx, {(int)ft}, {(int)fs}, 12, {dest}u);");
                break;
            case R5900Op.Vftoi15:
                sb.AppendLine($"{indent}Ps2Vu0.Vftoi(ctx, {(int)ft}, {(int)fs}, 15, {dest}u);");
                break;

            case R5900Op.Vabs:
                sb.AppendLine($"{indent}Ps2Vu0.Vabs(ctx, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vmove:
                sb.AppendLine($"{indent}Ps2Vu0.Vmove(ctx, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vmr32:
                sb.AppendLine($"{indent}Ps2Vu0.Vmr32(ctx, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vclip:
                sb.AppendLine($"{indent}Ps2Vu0.Vclip(ctx, {(int)fs}, {(int)ft});");
                break;
            case R5900Op.Vdiv:
                sb.AppendLine($"{indent}Ps2Vu0.Vdiv(ctx, {(int)fs}, {insn.Fsf}u, {(int)ft}, {insn.Ftf}u);");
                break;
            case R5900Op.Vsqrt:
                sb.AppendLine($"{indent}Ps2Vu0.Vsqrt(ctx, {(int)ft}, {insn.Ftf}u);");
                break;
            case R5900Op.Vrsqrt:
                sb.AppendLine($"{indent}Ps2Vu0.Vrsqrt(ctx, {(int)fs}, {insn.Fsf}u, {(int)ft}, {insn.Ftf}u);");
                break;
            case R5900Op.Vwaitq:
            case R5900Op.Vnop:
                break;
            case R5900Op.Vmtir:
                sb.AppendLine($"{indent}Ps2Vu0.Vmtir(ctx, {(int)ft}, {(int)fs}, {insn.Fsf}u);");
                break;
            case R5900Op.Vmfir:
                sb.AppendLine($"{indent}Ps2Vu0.Vmfir(ctx, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vlqi:
                sb.AppendLine($"{indent}Ps2Vu0.Vlqi(ctx, ram, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vsqi:
                sb.AppendLine($"{indent}Ps2Vu0.Vsqi(ctx, ram, {(int)fs}, {(int)ft}, {dest}u);");
                break;
            case R5900Op.Vlqd:
                sb.AppendLine($"{indent}Ps2Vu0.Vlqd(ctx, ram, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Vsqd:
                sb.AppendLine($"{indent}Ps2Vu0.Vsqd(ctx, ram, {(int)fs}, {(int)ft}, {dest}u);");
                break;
            case R5900Op.Vilwr:
                sb.AppendLine($"{indent}Ps2Vu0.Vilwr(ctx, ram, {(int)ft}, {(int)fs}, {dest}u);");
                break;
            case R5900Op.Viswr:
                sb.AppendLine($"{indent}Ps2Vu0.Viswr(ctx, ram, {(int)ft}, {(int)fs}, {dest}u);");
                break;

            case R5900Op.Viadd:
                sb.AppendLine($"{indent}Ps2Vu0.SetVi(ctx, {(int)fd}, (ushort)(ctx->ReadVi({fs}) + ctx->ReadVi({ft})));");
                break;
            case R5900Op.Visub:
                sb.AppendLine($"{indent}Ps2Vu0.SetVi(ctx, {(int)fd}, (ushort)(ctx->ReadVi({fs}) - ctx->ReadVi({ft})));");
                break;
            case R5900Op.Viaddi:
                {
                    int imm5 = (int)((insn.Word >> 6) & 0x1Fu);
                    if ((imm5 & 0x10) != 0) imm5 |= ~0x1F;
                    sb.AppendLine($"{indent}Ps2Vu0.SetVi(ctx, {(int)ft}, (ushort)(ctx->ReadVi({fs}) + ({imm5})));");
                }
                break;
            case R5900Op.Viand:
                sb.AppendLine($"{indent}Ps2Vu0.SetVi(ctx, {(int)fd}, (ushort)(ctx->ReadVi({fs}) & ctx->ReadVi({ft})));");
                break;
            case R5900Op.Vior:
                sb.AppendLine($"{indent}Ps2Vu0.SetVi(ctx, {(int)fd}, (ushort)(ctx->ReadVi({fs}) | ctx->ReadVi({ft})));");
                break;

            case R5900Op.Vrinit:
                sb.AppendLine($"{indent}Ps2Vu0.Vrinit(ctx, {(int)fs}, {insn.Fsf}u);");
                break;
            case R5900Op.Vrget:
                sb.AppendLine($"{indent}Ps2Vu0.Vrget(ctx, {(int)ft}, {dest}u);");
                break;
            case R5900Op.Vrnext:
                sb.AppendLine($"{indent}Ps2Vu0.Vrnext(ctx, {(int)ft}, {dest}u);");
                break;
            case R5900Op.Vrxor:
                sb.AppendLine($"{indent}Ps2Vu0.Vrxor(ctx, {(int)fs}, {insn.Fsf}u);");
                break;
            case R5900Op.Vcallms:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Vcallms(ctx, ram, 0x{(((insn.Word >> 6) & 0x7FFFu) << 3):X4}u);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent, declare: false);
                break;
            case R5900Op.Vcallmsr:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Vcallmsr(ctx, ram, {(int)fs});");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent, declare: false);
                break;

            case R5900Op.Jal:
                sb.AppendLine($"{indent}{SetGprU64(31, $"0x{(insn.Pc + 8u):X8}UL", in p)}");
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Call(0x{insn.JumpTarget:X8}u, ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent, declare: false);
                break;
            case R5900Op.J:
                EmitFlushPromoted(sb, in p, indent);
                sb.AppendLine($"{indent}RuntimeDispatcher.Jump(0x{insn.JumpTarget:X8}u, ctx, ram); return;");
                break;
            case R5900Op.Jr:
                EmitFlushPromoted(sb, in p, indent);
                if (insn.IsReturn)
                    sb.AppendLine($"{indent}RuntimeDispatcher.OnReturn(ctx, {GprU32(31, in p)}); return;");
                else
                    sb.AppendLine($"{indent}RuntimeDispatcher.JumpIndirect({GprU32(rs, in p)}, ctx, ram); return;");
                break;
            case R5900Op.Jalr:
                sb.AppendLine($"{indent}{{ uint __t = {GprU32(rs, in p)};");
                if (rd != 0)
                    sb.AppendLine($"{indent}  {SetGprU64(rd, $"0x{(insn.Pc + 8u):X8}UL", in p)}");
                EmitFlushPromoted(sb, in p, indent + "  ");
                sb.AppendLine($"{indent}  RuntimeDispatcher.CallIndirect(__t, ctx, ram);");
                if (p.HasAny) EmitLoadPromoted(sb, in p, indent + "  ", declare: false);
                sb.AppendLine($"{indent}}}");
                break;

            default:
                sb.AppendLine($"{indent}// Unhandled opcode {insn.Op} (0x{insn.Word:X8}) at 0x{insn.Pc:X8}");
                break;
        }
    }
}
