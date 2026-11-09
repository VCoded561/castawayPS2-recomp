using System.Text;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class CallTreeCommand
{
    private class FunctionSummary
    {
        public uint Addr { get; set; }
        public List<uint> Callees { get; set; } = new();
        public List<string> Strings { get; set; } = new();
        public List<uint> Syscalls { get; set; } = new();
        public List<int> A0Offsets { get; set; } = new();
    }

    public static int Run(string[] args)
    {
        uint? addr = null;
        int depth = 3;
        bool callersUp = false;
        string? elfPath = null;
        int maxLines = 80;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--addr" || arg == "-a") && i + 1 < args.Length)
            {
                addr = InspectFuncCommand.ParseHex(args[++i]);
            }
            else if (arg == "--depth" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out depth);
            }
            else if (arg == "--up")
            {
                callersUp = true;
            }
            else if (arg == "--elf" && i + 1 < args.Length)
            {
                elfPath = args[++i];
            }
            else if (arg == "--max-lines" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out maxLines);
            }
            else if (!arg.StartsWith("-") && addr == null)
            {
                addr = InspectFuncCommand.ParseHex(arg);
            }
        }

        if (addr == null)
        {
            Console.WriteLine("Error: Missing --addr <hex_address>");
            return 1;
        }

        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);

        uint rootAddr = addr.Value;
        List<string> lines = new();
        lines.Add($"=== Call Tree for sub_{rootAddr:X8} (Depth: {depth}, Direction: {(callersUp ? "UP (Callers)" : "DOWN (Callees)")}) ===");

        if (callersUp)
        {
            // Build inverted caller graph
            WalkCallersUp(reader, rootAddr, depth, lines, new HashSet<uint>());
        }
        else
        {
            // Walk callees down
            WalkCalleesDown(reader, rootAddr, 0, depth, lines, new HashSet<uint>());
        }

        foreach (var l in lines.Take(maxLines))
        {
            Console.WriteLine(l);
        }

        if (lines.Count > maxLines)
        {
            Console.WriteLine($"... [Truncated {lines.Count - maxLines} lines. Narrow with --depth]");
        }

        return 0;
    }

    private static void WalkCalleesDown(Ps2ElfReader reader, uint funcAddr, int currentDepth, int maxDepth, List<string> lines, HashSet<uint> visited)
    {
        string indent = new string(' ', currentDepth * 3);
        var summary = SummarizeFunction(reader, funcAddr);

        string annotations = "";
        if (summary.Strings.Count > 0)
            annotations += $" [str: {string.Join(", ", summary.Strings.Take(2))}]";
        if (summary.Syscalls.Count > 0)
            annotations += $" [syscall: {string.Join(", ", summary.Syscalls.Select(s => $"0x{s:X2}"))}]";
        if (summary.A0Offsets.Count > 0)
            annotations += $" [a0_offsets: {string.Join(", ", summary.A0Offsets.Take(3).Select(o => $"+0x{o:X}"))}]";

        bool cycle = visited.Contains(funcAddr);
        string cycleMark = cycle ? " (RECURSION CYCLE)" : "";
        lines.Add($"{indent}├── sub_{funcAddr:X8}{annotations}{cycleMark}");

        if (cycle || currentDepth >= maxDepth)
            return;

        visited.Add(funcAddr);

        foreach (var callee in summary.Callees.Distinct())
        {
            WalkCalleesDown(reader, callee, currentDepth + 1, maxDepth, lines, new HashSet<uint>(visited));
        }
    }

    private static void WalkCallersUp(Ps2ElfReader reader, uint targetAddr, int maxDepth, List<string> lines, HashSet<uint> visited)
    {
        var textSection = reader.TextSection;
        if (textSection == null) return;

        // Scan .text for JALs targeting targetAddr
        List<uint> callers = new();
        uint start = textSection.VAddr;
        uint end = textSection.VAddr + textSection.Size;

        uint currentFunc = start;
        for (uint v = start; v < end; v += 4)
        {
            uint word = reader.ReadUInt32(v);
            var insn = R5900Decoder.Decode(word, v);

            if (insn.Op == R5900Op.Jal && insn.JumpTarget == targetAddr)
            {
                callers.Add(currentFunc);
            }

            if (insn.IsReturn)
            {
                v += 4;
                currentFunc = v + 4;
            }
        }

        lines.Add($"Callers of sub_{targetAddr:X8} ({callers.Count} found):");
        foreach (var caller in callers.Distinct())
        {
            var summary = SummarizeFunction(reader, caller);
            string extra = summary.Strings.Count > 0 ? $" [str: {summary.Strings[0]}]" : "";
            lines.Add($"  └── Caller sub_{caller:X8}{extra}");
        }
    }

    private static FunctionSummary SummarizeFunction(Ps2ElfReader reader, uint funcAddr)
    {
        var summary = new FunctionSummary { Addr = funcAddr };
        if (!reader.IsVAddrValid(funcAddr)) return summary;

        uint[] luiRegs = new uint[32];
        bool[] luiValid = new bool[32];

        uint curr = funcAddr;
        int count = 0;
        int maxInsns = 200;

        while (reader.IsVAddrValid(curr) && count < maxInsns)
        {
            uint word = reader.ReadUInt32(curr);
            var insn = R5900Decoder.Decode(word, curr);

            if (insn.Op == R5900Op.Jal)
            {
                if (reader.IsInText(insn.JumpTarget))
                    summary.Callees.Add(insn.JumpTarget);
            }

            if (insn.Op == R5900Op.Syscall)
            {
                // lookback for $v1
                if (curr >= funcAddr + 4)
                {
                    var prev = R5900Decoder.Decode(reader.ReadUInt32(curr - 4), curr - 4);
                    if (prev.Rt == 3 && prev.Op == R5900Op.Addiu)
                        summary.Syscalls.Add((uint)prev.Simm);
                }
            }

            // Track offsets accessed off $a0 (register 4)
            if ((insn.IsLoad || insn.IsStore) && insn.Rs == 4)
            {
                summary.A0Offsets.Add(insn.Simm);
            }

            // LUI string references
            if (insn.Op == R5900Op.Lui)
            {
                luiRegs[insn.Rt] = (uint)insn.Uimm << 16;
                luiValid[insn.Rt] = true;
            }
            else if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Ori || insn.IsLoad) && luiValid[insn.Rs])
            {
                uint eff = insn.Op == R5900Op.Ori ? (luiRegs[insn.Rs] | insn.Uimm) : (uint)(luiRegs[insn.Rs] + insn.Simm);
                if (reader.IsInRodata(eff))
                {
                    string s = reader.ReadAsciiZ(eff, 32);
                    if (!string.IsNullOrEmpty(s) && s.Length >= 3 && s.All(c => c >= 32 && c <= 126))
                    {
                        summary.Strings.Add($"\"{s}\"");
                    }
                }
                if (insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Ori)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
            }

            if (insn.IsReturn)
            {
                break;
            }

            curr += 4;
            count++;
        }

        return summary;
    }
}
