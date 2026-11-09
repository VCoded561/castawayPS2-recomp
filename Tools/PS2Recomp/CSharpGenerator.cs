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

        sb.AppendLine($"    // {func.Name} (0x{start:X8} - 0x{end:X8})");
        sb.AppendLine($"    public static unsafe void sub_{start:X8}(CpuContext* ctx, byte* ram)");
        sb.AppendLine("    {");
        sb.AppendLine($"        if (RuntimeDispatcher.CheckEntry(0x{start:X8}u, ctx, ram)) return;");

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

                EmitBranchOrJumpWithDelaySlot(sb, func, insn, delayInsn, delayHasLabel);
                i++;
            }
            else
            {
                EmitSingleInstruction(sb, insn, "        ");
            }
        }

        bool endsWithUnconditionalJump = false;
        if (insnCount >= 2)
        {
            R5900Insn penult = insns[insnCount - 2];
            if (penult.Op is R5900Op.Jr or R5900Op.J)
                endsWithUnconditionalJump = true;
        }
        if (insnCount >= 1)
        {
            R5900Insn last = insns[insnCount - 1];
            if (last.Op is R5900Op.Jr or R5900Op.J or R5900Op.Eret)
                endsWithUnconditionalJump = true;
        }

        if (!endsWithUnconditionalJump && _functionStarts.Contains(end))
        {
            sb.AppendLine($"        sub_{end:X8}(ctx, ram);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private void EmitBranchOrJumpWithDelaySlot(
        StringBuilder sb,
        FunctionMeta func,
        R5900Insn insn,
        R5900Insn delayInsn,
        bool delayHasLabel)
    {
        uint pc = insn.Pc;
        uint returnAddr = pc + 8u;

        // 1. Unconditional J / JAL
        if (insn.Op is R5900Op.J or R5900Op.Jal)
        {
            if (insn.Op == R5900Op.Jal)
            {
                sb.AppendLine($"        ctx->SetU64(31, 0x{returnAddr:X8}UL);");
            }
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ");

            uint target = insn.JumpTarget;
            if (insn.Op == R5900Op.Jal)
            {
                EmitDirectOrDispatchedCall(sb, target, "        ");
            }
            else
            {
                EmitDirectOrDispatchedJump(sb, func, target, "        ");
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
                EmitSingleInstruction(sb, delayInsn, "        ");
                sb.AppendLine("        RuntimeDispatcher.OnReturn(ctx, ctx->GetU32(31));");
                sb.AppendLine("        return;");
                return;
            }

            if (delayHasLabel)
            {
                sb.AppendLine($"        __target_{pc:X8} = ctx->GetU32({insn.Rs});");
            }
            else
            {
                sb.AppendLine($"        uint __target_{pc:X8} = ctx->GetU32({insn.Rs});");
            }
            if (insn.Op == R5900Op.Jalr && insn.Rd != 0)
            {
                sb.AppendLine($"        ctx->SetU64({insn.Rd}, 0x{returnAddr:X8}UL);");
            }
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ");

            if (insn.Op == R5900Op.Jalr)
            {
                sb.AppendLine($"        RuntimeDispatcher.CallIndirect(__target_{pc:X8}, ctx, ram);");
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
                        sb.AppendLine($"            case 0x{target:X8}u: sub_{target:X8}(ctx, ram); return;");
                    }
                    else
                    {
                        sb.AppendLine($"            case 0x{target:X8}u: RuntimeDispatcher.Jump(0x{target:X8}u, ctx, ram); return;");
                    }
                }
                sb.AppendLine($"            default: RuntimeDispatcher.JumpIndirect(__target_{pc:X8}, ctx, ram); return;");
                sb.AppendLine("        }");
            }
            else
            {
                sb.AppendLine($"        RuntimeDispatcher.JumpIndirect(__target_{pc:X8}, ctx, ram);");
                sb.AppendLine("        return;");
            }
            return;
        }

        // 3. Conditional Branches (Standard & Branch Likely)
        string condExpr = GetBranchCondition(insn);
        string condVar = $"__cond_{pc:X8}";

        if (insn.Op is R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall)
        {
            sb.AppendLine($"        ctx->SetU64(31, 0x{returnAddr:X8}UL);");
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
                EmitSingleInstruction(sb, delayInsn, "        ");
                sb.AppendLine($"        if ({condVar})");
                sb.AppendLine("        {");
                sb.AppendLine($"            {condVar} = false;");
                EmitBranchTargetAction(sb, func, insn, branchTarget, "            ");
                sb.AppendLine("        }");
                sb.AppendLine($"    L_skip_{pc:X8}: ;");
            }
            else
            {
                sb.AppendLine($"        if ({condVar})");
                sb.AppendLine("        {");
                EmitSingleInstruction(sb, delayInsn, "            ");
                EmitBranchTargetAction(sb, func, insn, branchTarget, "            ");
                sb.AppendLine("        }");
            }
        }
        else
        {
            if (delayHasLabel)
            {
                sb.AppendLine($"    L_{delayInsn.Pc:X8}: ;");
            }
            EmitSingleInstruction(sb, delayInsn, "        ");
            sb.AppendLine($"        if ({condVar})");
            sb.AppendLine("        {");
            if (delayHasLabel)
            {
                sb.AppendLine($"            {condVar} = false;");
            }
            EmitBranchTargetAction(sb, func, insn, branchTarget, "            ");
            sb.AppendLine("        }");
        }
    }

    private void EmitBranchTargetAction(
        StringBuilder sb,
        FunctionMeta func,
        R5900Insn insn,
        uint target,
        string indent)
    {
        if (insn.Op is R5900Op.Bltzal or R5900Op.Bgezal or R5900Op.Bltzall or R5900Op.Bgezall)
        {
            if (target < func.Start || target >= func.End)
            {
                EmitDirectOrDispatchedCall(sb, target, indent);
                return;
            }
        }

        EmitDirectOrDispatchedJump(sb, func, target, indent);
    }

    private void EmitDirectOrDispatchedCall(StringBuilder sb, uint target, string indent)
    {
        if (_functionStarts.Contains(target))
        {
            sb.AppendLine($"{indent}sub_{target:X8}(ctx, ram);");
        }
        else
        {
            sb.AppendLine($"{indent}RuntimeDispatcher.Call(0x{target:X8}u, ctx, ram);");
        }
    }

    private void EmitDirectOrDispatchedJump(StringBuilder sb, FunctionMeta func, uint target, string indent)
    {
        if (target >= func.Start && target < func.End)
        {
            sb.AppendLine($"{indent}goto L_{target:X8};");
        }
        else if (_functionStarts.Contains(target))
        {
            sb.AppendLine($"{indent}sub_{target:X8}(ctx, ram);");
            sb.AppendLine($"{indent}return;");
        }
        else
        {
            sb.AppendLine($"{indent}RuntimeDispatcher.Jump(0x{target:X8}u, ctx, ram);");
            sb.AppendLine($"{indent}return;");
        }
    }

    private static string GetBranchCondition(R5900Insn insn)
    {
        uint rs = insn.Rs;
        uint rt = insn.Rt;

        return insn.Op switch
        {
            R5900Op.Beq or R5900Op.Beql =>
                rs == rt ? "true" : $"(ctx->GetU64({rs}) == ctx->GetU64({rt}))",
            R5900Op.Bne or R5900Op.Bnel =>
                rs == rt ? "false" : $"(ctx->GetU64({rs}) != ctx->GetU64({rt}))",
            R5900Op.Blez or R5900Op.Blezl =>
                rs == 0 ? "true" : $"(ctx->GetS64({rs}) <= 0L)",
            R5900Op.Bgtz or R5900Op.Bgtzl =>
                rs == 0 ? "false" : $"(ctx->GetS64({rs}) > 0L)",
            R5900Op.Bltz or R5900Op.Bltzl or R5900Op.Bltzal or R5900Op.Bltzall =>
                rs == 0 ? "false" : $"(ctx->GetS64({rs}) < 0L)",
            R5900Op.Bgez or R5900Op.Bgezl or R5900Op.Bgezal or R5900Op.Bgezall =>
                rs == 0 ? "true" : $"(ctx->GetS64({rs}) >= 0L)",
            R5900Op.Bc1f or R5900Op.Bc1fl => "!ctx->Cop1Condition",
            R5900Op.Bc1t or R5900Op.Bc1tl => "ctx->Cop1Condition",
            R5900Op.Bc0f or R5900Op.Bc0fl => "!ctx->Cop0Condition",
            R5900Op.Bc0t or R5900Op.Bc0tl => "ctx->Cop0Condition",
            R5900Op.Bc2f or R5900Op.Bc2fl => "(Ps2Vu0.Cfc2(ctx, 29) == 0u)",
            R5900Op.Bc2t or R5900Op.Bc2tl => "(Ps2Vu0.Cfc2(ctx, 29) != 0u)",
            _ => "false"
        };
    }

    public static void EmitSingleInstruction(StringBuilder sb, R5900Insn insn, string indent)
    {
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
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, (int)(ctx->GetU32({rt}) << {(int)sa}));");
                break;
            case R5900Op.Srl:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, (int)(ctx->GetU32({rt}) >> {(int)sa}));");
                break;
            case R5900Op.Sra:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, ctx->GetS32({rt}) >> {(int)sa});");
                break;
            case R5900Op.Sllv:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, (int)(ctx->GetU32({rt}) << (int)(ctx->GetU32({rs}) & 31u)));");
                break;
            case R5900Op.Srlv:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, (int)(ctx->GetU32({rt}) >> (int)(ctx->GetU32({rs}) & 31u)));");
                break;
            case R5900Op.Srav:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, ctx->GetS32({rt}) >> (int)(ctx->GetU32({rs}) & 31u));");
                break;

            case R5900Op.Dsll:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) << {(int)sa});");
                break;
            case R5900Op.Dsrl:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) >> {(int)sa});");
                break;
            case R5900Op.Dsra:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS64({rd}, ctx->GetS64({rt}) >> {(int)sa});");
                break;
            case R5900Op.Dsll32:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) << {(int)(sa + 32u)});");
                break;
            case R5900Op.Dsrl32:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) >> {(int)(sa + 32u)});");
                break;
            case R5900Op.Dsra32:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS64({rd}, ctx->GetS64({rt}) >> {(int)(sa + 32u)});");
                break;
            case R5900Op.Dsllv:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) << (int)(ctx->GetU32({rs}) & 63u));");
                break;
            case R5900Op.Dsrlv:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}) >> (int)(ctx->GetU32({rs}) & 63u));");
                break;
            case R5900Op.Dsrav:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS64({rd}, ctx->GetS64({rt}) >> (int)(ctx->GetU32({rs}) & 63u));");
                break;

            case R5900Op.Movz:
                if (rd != 0)
                    sb.AppendLine($"{indent}if (ctx->GetU64({rt}) == 0UL) ctx->SetU64({rd}, ctx->GetU64({rs}));");
                break;
            case R5900Op.Movn:
                if (rd != 0)
                    sb.AppendLine($"{indent}if (ctx->GetU64({rt}) != 0UL) ctx->SetU64({rd}, ctx->GetU64({rs}));");
                break;

            case R5900Op.Add:
            case R5900Op.Addu:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, unchecked(ctx->GetS32({rs}) + ctx->GetS32({rt})));");
                break;
            case R5900Op.Sub:
            case R5900Op.Subu:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rd}, unchecked(ctx->GetS32({rs}) - ctx->GetS32({rt})));");
                break;
            case R5900Op.Dadd:
            case R5900Op.Daddu:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, unchecked(ctx->GetU64({rs}) + ctx->GetU64({rt})));");
                break;
            case R5900Op.Dsub:
            case R5900Op.Dsubu:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, unchecked(ctx->GetU64({rs}) - ctx->GetU64({rt})));");
                break;

            case R5900Op.Addi:
            case R5900Op.Addiu:
                if (rt != 0)
                {
                    if (rs == 0)
                        sb.AppendLine($"{indent}ctx->SetS32({rt}, {simm});");
                    else
                        sb.AppendLine($"{indent}ctx->SetS32({rt}, unchecked(ctx->GetS32({rs}) + ({simm})));");
                }
                break;
            case R5900Op.Daddi:
            case R5900Op.Daddiu:
                if (rt != 0)
                {
                    if (rs == 0)
                        sb.AppendLine($"{indent}ctx->SetS64({rt}, {simm}L);");
                    else
                        sb.AppendLine($"{indent}ctx->SetS64({rt}, unchecked(ctx->GetS64({rs}) + ({simm}L)));");
                }
                break;

            case R5900Op.And:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rs}) & ctx->GetU64({rt}));");
                break;
            case R5900Op.Or:
                if (rd != 0)
                {
                    if (rs == 0 && rt == 0)
                        sb.AppendLine($"{indent}ctx->SetU64({rd}, 0UL);");
                    else if (rt == 0)
                        sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rs}));");
                    else if (rs == 0)
                        sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rt}));");
                    else
                        sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rs}) | ctx->GetU64({rt}));");
                }
                break;
            case R5900Op.Xor:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rs}) ^ ctx->GetU64({rt}));");
                break;
            case R5900Op.Nor:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ~(ctx->GetU64({rs}) | ctx->GetU64({rt})));");
                break;

            case R5900Op.Andi:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, ctx->GetU64({rs}) & 0x{uimm:X4}UL);");
                break;
            case R5900Op.Ori:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, ctx->GetU64({rs}) | 0x{uimm:X4}UL);");
                break;
            case R5900Op.Xori:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, ctx->GetU64({rs}) ^ 0x{uimm:X4}UL);");
                break;
            case R5900Op.Lui:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, unchecked((int)0x{((uint)uimm << 16):X8}u));");
                break;

            case R5900Op.Slt:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetS64({rs}) < ctx->GetS64({rt}) ? 1UL : 0UL);");
                break;
            case R5900Op.Sltu:
                if (rd != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->GetU64({rs}) < ctx->GetU64({rt}) ? 1UL : 0UL);");
                break;
            case R5900Op.Slti:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, ctx->GetS64({rs}) < ({simm}L) ? 1UL : 0UL);");
                break;
            case R5900Op.Sltiu:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, ctx->GetU64({rs}) < unchecked((ulong)(long)({simm})) ? 1UL : 0UL);");
                break;

            // ====================================================================
            // Multiply / Divide & HI/LO/SA
            // ====================================================================
            case R5900Op.Mult:
                sb.AppendLine($"{indent}{{ long __p = (long)ctx->GetS32({rs}) * (long)ctx->GetS32({rt}); ctx->LO = (ulong)(long)(int)__p; ctx->HI = (ulong)(long)(int)(__p >> 32); {(rd != 0 ? $"ctx->SetS32({rd}, (int)__p);" : "")} }}");
                break;
            case R5900Op.Multu:
                sb.AppendLine($"{indent}{{ ulong __p = (ulong)ctx->GetU32({rs}) * (ulong)ctx->GetU32({rt}); ctx->LO = (ulong)(long)(int)(uint)__p; ctx->HI = (ulong)(long)(int)(uint)(__p >> 32); {(rd != 0 ? $"ctx->SetS32({rd}, (int)(uint)__p);" : "")} }}");
                break;
            case R5900Op.Div:
                sb.AppendLine($"{indent}{{ int __a = ctx->GetS32({rs}), __b = ctx->GetS32({rt}); if (__b == 0) {{ ctx->LO = __a < 0 ? 1UL : ulong.MaxValue; ctx->HI = (ulong)(long)__a; }} else if (__a == int.MinValue && __b == -1) {{ ctx->LO = unchecked((ulong)(long)int.MinValue); ctx->HI = 0UL; }} else {{ ctx->LO = (ulong)(long)(__a / __b); ctx->HI = (ulong)(long)(__a % __b); }} }}");
                break;
            case R5900Op.Divu:
                sb.AppendLine($"{indent}{{ uint __a = ctx->GetU32({rs}), __b = ctx->GetU32({rt}); if (__b == 0) {{ ctx->LO = ulong.MaxValue; ctx->HI = (ulong)(long)(int)__a; }} else {{ ctx->LO = (ulong)(long)(int)(__a / __b); ctx->HI = (ulong)(long)(int)(__a % __b); }} }}");
                break;

            case R5900Op.Mfhi:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->HI);");
                break;
            case R5900Op.Mthi:
                sb.AppendLine($"{indent}ctx->HI = ctx->GetU64({rs});");
                break;
            case R5900Op.Mflo:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->LO);");
                break;
            case R5900Op.Mtlo:
                sb.AppendLine($"{indent}ctx->LO = ctx->GetU64({rs});");
                break;
            case R5900Op.Mfsa:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->SA);");
                break;
            case R5900Op.Mtsa:
                sb.AppendLine($"{indent}ctx->SA = ctx->GetU32({rs}) & 15u;");
                break;
            case R5900Op.Mtsab:
                sb.AppendLine($"{indent}ctx->SA = ((ctx->GetU32({rs}) ^ (uint)(ushort)({simm})) & 15u) * 8u;");
                break;
            case R5900Op.Mtsah:
                sb.AppendLine($"{indent}ctx->SA = ((ctx->GetU32({rs}) ^ (uint)(ushort)({simm})) & 7u) * 16u;");
                break;

            // ====================================================================
            // Memory Loads & Stores (32 MB Mask + Scratchpad + Unaligned Helpers)
            // ====================================================================
            case R5900Op.Lb:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (sbyte)Ps2Memory.Read8(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read8(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lbu:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetZ32({rt}, Ps2Memory.Read8(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read8(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lh:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (short)Ps2Memory.Read16(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read16(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lhu:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetZ32({rt}, Ps2Memory.Read16(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read16(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lw:
            case R5900Op.Ll:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (int)Ps2Memory.Read32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lwu:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetZ32({rt}, Ps2Memory.Read32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Ld:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetU64({rt}, Ps2Memory.Read64(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                else
                    sb.AppendLine($"{indent}_ = Ps2Memory.Read64(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lq:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetVec({rt}, Ps2Memory.Read128(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                break;

            case R5900Op.Lwl:
                sb.AppendLine($"{indent}Ps2Memory.LoadWordLeft(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Lwr:
                sb.AppendLine($"{indent}Ps2Memory.LoadWordRight(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Ldl:
                sb.AppendLine($"{indent}Ps2Memory.LoadDoubleLeft(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Ldr:
                sb.AppendLine($"{indent}Ps2Memory.LoadDoubleRight(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;

            case R5900Op.Sb:
                sb.AppendLine($"{indent}Ps2Memory.Write8(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), (byte)ctx->GetU32({rt}));");
                break;
            case R5900Op.Sh:
                sb.AppendLine($"{indent}Ps2Memory.Write16(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), (ushort)ctx->GetU32({rt}));");
                break;
            case R5900Op.Sw:
                sb.AppendLine($"{indent}Ps2Memory.Write32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->GetU32({rt}));");
                break;
            case R5900Op.Sc:
                sb.AppendLine($"{indent}Ps2Memory.Write32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->GetU32({rt}));");
                if (rt != 0) sb.AppendLine($"{indent}ctx->SetU64({rt}, 1UL);");
                break;
            case R5900Op.Sd:
                sb.AppendLine($"{indent}Ps2Memory.Write64(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->GetU64({rt}));");
                break;
            case R5900Op.Sq:
                sb.AppendLine($"{indent}Ps2Memory.Write128(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->GetVec({rt}));");
                break;

            case R5900Op.Swl:
                sb.AppendLine($"{indent}Ps2Memory.StoreWordLeft(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Swr:
                sb.AppendLine($"{indent}Ps2Memory.StoreWordRight(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Sdl:
                sb.AppendLine($"{indent}Ps2Memory.StoreDoubleLeft(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;
            case R5900Op.Sdr:
                sb.AppendLine($"{indent}Ps2Memory.StoreDoubleRight(ctx, ram, {(int)rt}, unchecked(ctx->GetU32({rs}) + (uint)({simm})));");
                break;

            case R5900Op.Lwc1:
                sb.AppendLine($"{indent}ctx->SetFpuBits({ft}, Ps2Memory.Read32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))));");
                break;
            case R5900Op.Swc1:
                sb.AppendLine($"{indent}Ps2Memory.Write32(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->GetFpuBits({ft}));");
                break;
            case R5900Op.Lqc2:
                if (ft != 0)
                    sb.AppendLine($"{indent}ctx->VF[{ft}] = Ps2Memory.Read128(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm}))).AsSingle();");
                break;
            case R5900Op.Sqc2:
                sb.AppendLine($"{indent}Ps2Memory.Write128(ctx, ram, unchecked(ctx->GetU32({rs}) + (uint)({simm})), ctx->ReadVf({ft}).AsUInt32());");
                break;

            // ====================================================================
            // System, Traps, Cache & COP0
            // ====================================================================
            case R5900Op.Syscall:
                sb.AppendLine($"{indent}RuntimeDispatcher.Syscall(ctx, ram, 0x{((insn.Word >> 6) & 0xFFFFFu):X5}u);");
                break;
            case R5900Op.Break:
                sb.AppendLine($"{indent}RuntimeDispatcher.Break(ctx, ram, 0x{((insn.Word >> 6) & 0xFFFFFu):X5}u);");
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
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (int)ctx->Cop0[{rd}]);");
                break;
            case R5900Op.Mtc0:
                sb.AppendLine($"{indent}ctx->Cop0[{rd}] = ctx->GetU32({rt});");
                break;

            case R5900Op.Tge:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) >= ctx->GetS64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tgeu:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) >= ctx->GetU64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tlt:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) < ctx->GetS64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tltu:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) < ctx->GetU64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Teq:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) == ctx->GetU64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tne:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) != ctx->GetU64({rt})) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tgei:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) >= ({simm}L)) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tgeiu:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) >= unchecked((ulong)(long)({simm}))) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tlti:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) < ({simm}L)) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tltiu:
                sb.AppendLine($"{indent}if (ctx->GetU64({rs}) < unchecked((ulong)(long)({simm}))) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Teqi:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) == ({simm}L)) RuntimeDispatcher.Trap(ctx, ram);");
                break;
            case R5900Op.Tnei:
                sb.AppendLine($"{indent}if (ctx->GetS64({rs}) != ({simm}L)) RuntimeDispatcher.Trap(ctx, ram);");
                break;

            // ====================================================================
            // COP1 (FPU — Clamped PS2 Float & Truncate Round-Toward-Zero)
            // ====================================================================
            case R5900Op.Mfc1:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (int)ctx->GetFpuBits({fs}));");
                break;
            case R5900Op.Mtc1:
                sb.AppendLine($"{indent}ctx->SetFpuBits({fs}, ctx->GetU32({rt}));");
                break;
            case R5900Op.Cfc1:
                if (rt != 0)
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (int){(fs == 0 ? "ctx->Fcr0" : "ctx->Fcr31")});");
                break;
            case R5900Op.Ctc1:
                if (fs == 31)
                    sb.AppendLine($"{indent}ctx->Fcr31 = ctx->GetU32({rt});");
                break;

            case R5900Op.AddS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Add(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.SubS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Sub(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.MulS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.DivS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Div(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.SqrtS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Sqrt(ctx->F[{ft}]);");
                break;
            case R5900Op.RsqrtS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Rsqrt(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.AbsS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Abs(ctx->F[{fs}]);");
                break;
            case R5900Op.MovS:
                sb.AppendLine($"{indent}ctx->SetFpuBits({fd}, ctx->GetFpuBits({fs}));");
                break;
            case R5900Op.NegS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Neg(ctx->F[{fs}]);");
                break;
            case R5900Op.AddaS:
                sb.AppendLine($"{indent}ctx->FAcc = Ps2Float.Add(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.SubaS:
                sb.AppendLine($"{indent}ctx->FAcc = Ps2Float.Sub(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.MulaS:
                sb.AppendLine($"{indent}ctx->FAcc = Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.MaddS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Add(ctx->FAcc, Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]));");
                break;
            case R5900Op.MsubS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Sub(ctx->FAcc, Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]));");
                break;
            case R5900Op.MaddaS:
                sb.AppendLine($"{indent}ctx->FAcc = Ps2Float.Add(ctx->FAcc, Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]));");
                break;
            case R5900Op.MsubaS:
                sb.AppendLine($"{indent}ctx->FAcc = Ps2Float.Sub(ctx->FAcc, Ps2Float.Mul(ctx->F[{fs}], ctx->F[{ft}]));");
                break;
            case R5900Op.CvtWS:
            case R5900Op.TruncWS:
            case R5900Op.RoundWS:
            case R5900Op.CeilWS:
            case R5900Op.FloorWS:
                sb.AppendLine($"{indent}ctx->SetFpuBits({fd}, (uint)Ps2Float.CvtWS(ctx->F[{fs}]));");
                break;
            case R5900Op.CvtSW:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.CvtSW((int)ctx->GetFpuBits({fs}));");
                break;
            case R5900Op.MaxS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Max(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.MinS:
                sb.AppendLine($"{indent}ctx->F[{fd}] = Ps2Float.Min(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.CF:
            case R5900Op.CSf:
            case R5900Op.CUn:
            case R5900Op.CNgle:
                sb.AppendLine($"{indent}ctx->Cop1Condition = false;");
                break;
            case R5900Op.CEq:
            case R5900Op.CUeq:
            case R5900Op.CSeq:
            case R5900Op.CNgl:
                sb.AppendLine($"{indent}ctx->Cop1Condition = Ps2Float.CompareEq(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.COlt:
            case R5900Op.CUlt:
            case R5900Op.CLt:
            case R5900Op.CNge:
                sb.AppendLine($"{indent}ctx->Cop1Condition = Ps2Float.CompareLt(ctx->F[{fs}], ctx->F[{ft}]);");
                break;
            case R5900Op.COle:
            case R5900Op.CUle:
            case R5900Op.CLe:
            case R5900Op.CNgt:
                sb.AppendLine($"{indent}ctx->Cop1Condition = Ps2Float.CompareLe(ctx->F[{fs}], ctx->F[{ft}]);");
                break;

            // ====================================================================
            // Multimedia Instructions (MMI — 128-bit Emotion Engine SIMD)
            // ====================================================================
            case R5900Op.Madd:
                sb.AppendLine($"{indent}Ps2Mmi.Madd(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Maddu:
                sb.AppendLine($"{indent}Ps2Mmi.Maddu(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Msub:
                sb.AppendLine($"{indent}Ps2Mmi.Msub(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Msubu:
                sb.AppendLine($"{indent}Ps2Mmi.Msubu(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Madd1:
                sb.AppendLine($"{indent}Ps2Mmi.Madd1(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Maddu1:
                sb.AppendLine($"{indent}Ps2Mmi.Maddu1(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Mult1:
                sb.AppendLine($"{indent}Ps2Mmi.Mult1(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Multu1:
                sb.AppendLine($"{indent}Ps2Mmi.Multu1(ctx, {(int)rd}, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Div1:
                sb.AppendLine($"{indent}Ps2Mmi.Div1(ctx, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Divu1:
                sb.AppendLine($"{indent}Ps2Mmi.Divu1(ctx, {(int)rs}, {(int)rt});");
                break;
            case R5900Op.Mfhi1:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->HI1);");
                break;
            case R5900Op.Mthi1:
                sb.AppendLine($"{indent}ctx->HI1 = ctx->GetU64({rs});");
                break;
            case R5900Op.Mflo1:
                if (rd != 0) sb.AppendLine($"{indent}ctx->SetU64({rd}, ctx->LO1);");
                break;
            case R5900Op.Mtlo1:
                sb.AppendLine($"{indent}ctx->LO1 = ctx->GetU64({rs});");
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
                    sb.AppendLine($"{indent}ctx->SetS32({rt}, (int)Ps2Vu0.Cfc2(ctx, {(int)fs}));");
                break;
            case R5900Op.Ctc2:
                sb.AppendLine($"{indent}Ps2Vu0.Ctc2(ctx, {(int)fs}, ctx->GetU32({rt}));");
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
                sb.AppendLine($"{indent}RuntimeDispatcher.Vcallms(ctx, ram, 0x{(((insn.Word >> 6) & 0x7FFFu) << 3):X4}u);");
                break;
            case R5900Op.Vcallmsr:
                sb.AppendLine($"{indent}RuntimeDispatcher.Vcallmsr(ctx, ram, {(int)fs});");
                break;

            case R5900Op.Jal:
                sb.AppendLine($"{indent}ctx->SetU64(31, 0x{(insn.Pc + 8u):X8}UL);");
                sb.AppendLine($"{indent}RuntimeDispatcher.Call(0x{insn.JumpTarget:X8}u, ctx, ram);");
                break;
            case R5900Op.J:
                sb.AppendLine($"{indent}RuntimeDispatcher.Jump(0x{insn.JumpTarget:X8}u, ctx, ram); return;");
                break;
            case R5900Op.Jr:
                if (insn.IsReturn)
                    sb.AppendLine($"{indent}RuntimeDispatcher.OnReturn(ctx, ctx->GetU32(31)); return;");
                else
                    sb.AppendLine($"{indent}RuntimeDispatcher.JumpIndirect(ctx->GetU32({rs}), ctx, ram); return;");
                break;
            case R5900Op.Jalr:
                sb.AppendLine($"{indent}{{ uint __t = ctx->GetU32({rs});");
                if (rd != 0)
                    sb.AppendLine($"{indent}  ctx->SetU64({rd}, 0x{(insn.Pc + 8u):X8}UL);");
                sb.AppendLine($"{indent}  RuntimeDispatcher.CallIndirect(__t, ctx, ram); }}");
                break;

            default:
                sb.AppendLine($"{indent}// Unhandled opcode {insn.Op} (0x{insn.Word:X8}) at 0x{insn.Pc:X8}");
                break;
        }
    }
}
