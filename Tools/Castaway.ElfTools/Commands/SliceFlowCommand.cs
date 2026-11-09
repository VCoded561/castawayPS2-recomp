using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class SliceFlowCommand
{
    private class FieldAccess
    {
        public uint FuncAddr { get; set; }
        public uint Pc { get; set; }
        public int Offset { get; set; }
        public string AccessType { get; set; } = string.Empty; // Read or Write
        public string InferredType { get; set; } = string.Empty; // int32, float, byte, uint128, etc.
        public string Instruction { get; set; } = string.Empty;
    }

    public static int Run(string[] args)
    {
        uint? addr = null;
        string regName = "a0";
        int depth = 3;
        string? elfPath = null;
        int maxLines = 80;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--addr" || arg == "-a") && i + 1 < args.Length)
            {
                addr = InspectFuncCommand.ParseHex(args[++i]);
            }
            else if ((arg == "--reg" || arg == "-r") && i + 1 < args.Length)
            {
                regName = args[++i];
            }
            else if (arg == "--depth" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out depth);
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

        uint initialReg = ParseRegister(regName);
        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);

        List<FieldAccess> accesses = new();
        HashSet<uint> visitedFuncs = new();

        SliceFunction(reader, addr.Value, initialReg, 0, depth, accesses, visitedFuncs);

        List<string> lines = new();
        lines.Add($"=== Slicing Pointer Flow from sub_{addr.Value:X8} (${regName}) [Depth: {depth}] ===");
        lines.Add($"Total field accesses discovered: {accesses.Count}");
        lines.Add("");

        // Group by byte offset
        var grouped = accesses.GroupBy(a => a.Offset).OrderBy(g => g.Key);
        lines.Add("Offset  | Type       | RW    | Callsite / Instruction");
        lines.Add("--------|------------|-------|----------------------------------------------");

        foreach (var group in grouped)
        {
            int off = group.Key;
            var sample = group.First();
            string rw = group.Any(x => x.AccessType == "Write") ? (group.Any(x => x.AccessType == "Read") ? "R/W" : "W  ") : "R  ";
            lines.Add($"+0x{off:X4}  | {sample.InferredType,-10} | {rw}   | sub_{sample.FuncAddr:X8} @ 0x{sample.Pc:X8}: {sample.Instruction}");
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

    private static void SliceFunction(Ps2ElfReader reader, uint funcAddr, uint trackReg, int currentDepth, int maxDepth, List<FieldAccess> accesses, HashSet<uint> visited)
    {
        if (currentDepth > maxDepth || visited.Contains(funcAddr) || !reader.IsVAddrValid(funcAddr))
            return;

        visited.Add(funcAddr);

        // Taint set: registers holding pointer
        bool[] tainted = new bool[32];
        tainted[trackReg] = true;

        uint curr = funcAddr;
        int count = 0;
        int maxInsns = 250;

        while (reader.IsVAddrValid(curr) && count < maxInsns)
        {
            uint word = reader.ReadUInt32(curr);
            var insn = R5900Decoder.Decode(word, curr);

            // 1. Check if instruction accesses memory off a tainted register
            if (insn.IsLoad || insn.IsStore)
            {
                if (tainted[insn.Rs])
                {
                    string rw = insn.IsStore ? "Write" : "Read";
                    string inferredType = InferType(insn.Op);
                    accesses.Add(new FieldAccess
                    {
                        FuncAddr = funcAddr,
                        Pc = curr,
                        Offset = insn.Simm,
                        AccessType = rw,
                        InferredType = inferredType,
                        Instruction = insn.ToString()
                    });
                }
            }

            // 2. Propagate taint: move $rt, $rs (or addu, daddu with $zero)
            if ((insn.Op == R5900Op.Addu || insn.Op == R5900Op.Daddu || insn.Op == R5900Op.Or) && insn.Rt == 0)
            {
                if (tainted[insn.Rs])
                {
                    if (insn.Rd != 0) tainted[insn.Rd] = true;
                }
                else if (insn.Rd != 0 && tainted[insn.Rd])
                {
                    tainted[insn.Rd] = false;
                }
            }
            else if (insn.Op == R5900Op.Jal)
            {
                // If a tainted register was loaded into $a0-$a3 (4..7), recursively slice callee
                for (uint argReg = 4; argReg <= 7; argReg++)
                {
                    if (tainted[argReg] && reader.IsInText(insn.JumpTarget))
                    {
                        SliceFunction(reader, insn.JumpTarget, argReg, currentDepth + 1, maxDepth, accesses, visited);
                    }
                }
            }
            else
            {
                // Invalidate tainted register if it's overwritten by something else
                if (insn.Rd != 0 && tainted[insn.Rd])
                {
                    tainted[insn.Rd] = false;
                }
                if (insn.Rt != 0 && !insn.IsStore && tainted[insn.Rt])
                {
                    tainted[insn.Rt] = false;
                }
            }

            if (insn.IsReturn)
                break;

            curr += 4;
            count++;
        }
    }

    private static string InferType(R5900Op op) => op switch
    {
        R5900Op.Lb or R5900Op.Lbu or R5900Op.Sb => "byte",
        R5900Op.Lh or R5900Op.Lhu or R5900Op.Sh => "int16",
        R5900Op.Lw or R5900Op.Lwu or R5900Op.Sw => "int32 / ptr",
        R5900Op.Ld or R5900Op.Sd => "int64",
        R5900Op.Lq or R5900Op.Sq => "uint128 (SIMD)",
        R5900Op.Lwc1 or R5900Op.Swc1 => "float",
        R5900Op.Lqc2 or R5900Op.Sqc2 => "Vector4 (VU0)",
        _ => "unknown"
    };

    private static uint ParseRegister(string reg)
    {
        reg = reg.Trim().ToLowerInvariant().TrimStart('$');
        return reg switch
        {
            "zero" or "r0" or "0" => 0,
            "at" or "r1" or "1" => 1,
            "v0" or "r2" or "2" => 2,
            "v1" or "r3" or "3" => 3,
            "a0" or "r4" or "4" => 4,
            "a1" or "r5" or "5" => 5,
            "a2" or "r6" or "6" => 6,
            "a3" or "r7" or "7" => 7,
            "t0" or "r8" or "8" => 8,
            "t1" or "r9" or "9" => 9,
            "t2" or "r10" or "10" => 10,
            "t3" or "r11" or "11" => 11,
            "t4" or "r12" or "12" => 12,
            "t5" or "r13" or "13" => 13,
            "t6" or "r14" or "14" => 14,
            "t7" or "r15" or "15" => 15,
            "s0" or "r16" or "16" => 16,
            "s1" or "r17" or "17" => 17,
            "s2" or "r18" or "18" => 18,
            "s3" or "r19" or "19" => 19,
            "s4" or "r20" or "20" => 20,
            "s5" or "r21" or "21" => 21,
            "s6" or "r22" or "22" => 22,
            "s7" or "r23" or "23" => 23,
            "t8" or "r24" or "24" => 24,
            "t9" or "r25" or "25" => 25,
            "k0" or "r26" or "26" => 26,
            "k1" or "r27" or "27" => 27,
            "gp" or "r28" or "28" => 28,
            "sp" or "r29" or "29" => 29,
            "fp" or "s8" or "r30" or "30" => 30,
            "ra" or "r31" or "31" => 31,
            _ => 4 // default to a0
        };
    }
}
