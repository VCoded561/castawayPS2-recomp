using System.Buffers.Binary;
using System.Text;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class FindXrefsCommand
{
    public static int Run(string[] args)
    {
        string? targetStr = null;
        string? searchString = null;
        float? searchFloat = null;
        uint? searchImm = null;
        string? elfPath = null;
        int maxLines = 80;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--target" || arg == "-t") && i + 1 < args.Length)
            {
                targetStr = args[++i];
            }
            else if ((arg == "--string" || arg == "-s") && i + 1 < args.Length)
            {
                searchString = args[++i];
            }
            else if ((arg == "--float" || arg == "-f") && i + 1 < args.Length)
            {
                if (float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out float f))
                    searchFloat = f;
            }
            else if (arg == "--imm" && i + 1 < args.Length)
            {
                searchImm = InspectFuncCommand.ParseHex(args[++i]);
            }
            else if (arg == "--mmio" && i + 1 < args.Length)
            {
                targetStr = args[++i];
            }
            else if (arg == "--elf" && i + 1 < args.Length)
            {
                elfPath = args[++i];
            }
            else if (arg == "--max-lines" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out maxLines);
            }
        }

        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);

        List<uint> targetsToFind = new();

        // If searching for string, scan .rodata and .data for string occurrences
        if (!string.IsNullOrEmpty(searchString))
        {
            Console.WriteLine($"Scanning for string \"{searchString}\" in .rodata / .data...");
            var stringVAddrs = FindStringOccurrences(reader, searchString);
            Console.WriteLine($"Found {stringVAddrs.Count} string match(es): {string.Join(", ", stringVAddrs.Select(v => $"0x{v:X8}"))}");
            targetsToFind.AddRange(stringVAddrs);
        }

        if (targetStr != null)
        {
            targetsToFind.Add(InspectFuncCommand.ParseHex(targetStr));
        }

        if (searchFloat.HasValue)
        {
            uint floatBits = BitConverter.SingleToUInt32Bits(searchFloat.Value);
            Console.WriteLine($"Scanning for float {searchFloat.Value} (0x{floatBits:X8})...");
            targetsToFind.Add(floatBits);
        }

        if (searchImm.HasValue)
        {
            targetsToFind.Add(searchImm.Value);
        }

        if (targetsToFind.Count == 0)
        {
            Console.WriteLine("Error: Please provide --string, --target, --float, or --imm.");
            return 1;
        }

        HashSet<uint> targetSet = new(targetsToFind);
        List<string> results = new();

        var textSection = reader.TextSection;
        if (textSection == null)
        {
            Console.WriteLine("Error: No .text section found in ELF.");
            return 1;
        }

        // Also scan .rodata and .data for pointers matching targets
        foreach (var sec in reader.Sections.Where(s => s.Name == ".rodata" || s.Name == ".data"))
        {
            for (uint v = sec.VAddr; v + 4 <= sec.VAddr + sec.Size; v += 4)
            {
                uint val = reader.ReadUInt32(v);
                if (targetSet.Contains(val))
                {
                    results.Add($"[DATA PTR] {sec.Name} @ 0x{v:X8} -> points to 0x{val:X8}");
                }
            }
        }

        // Linear sweep of .text with stateful 32-register LUI tracking
        uint startVAddr = textSection.VAddr;
        uint endVAddr = textSection.VAddr + textSection.Size;

        uint[] luiRegs = new uint[32];
        bool[] luiValid = new bool[32];
        uint currentFuncStart = startVAddr;
        bool clearAfterDelaySlot = false;

        for (uint v = startVAddr; v < endVAddr; v += 4)
        {
            uint word = reader.ReadUInt32(v);
            var insn = R5900Decoder.Decode(word, v);

            // J / JAL direct call checks
            if (insn.Op == R5900Op.Jal || insn.Op == R5900Op.J)
            {
                if (targetSet.Contains(insn.JumpTarget))
                {
                    results.Add($"[CALL] sub_{currentFuncStart:X8} + 0x{v - currentFuncStart:X} @ 0x{v:X8}: {insn}");
                }
            }

            // LUI tracking
            if (insn.Op == R5900Op.Lui)
            {
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = (uint)insn.Uimm << 16;
                    luiValid[insn.Rt] = true;
                }
                if (targetSet.Contains(luiRegs[insn.Rt]))
                {
                    results.Add($"[LUI] sub_{currentFuncStart:X8} + 0x{v - currentFuncStart:X} @ 0x{v:X8}: {insn} (0x{luiRegs[insn.Rt]:X8})");
                }
            }
            else if ((insn.Op == R5900Op.Daddu || insn.Op == R5900Op.Addu || insn.Op == R5900Op.Or ||
                      insn.Op == R5900Op.Dadd || insn.Op == R5900Op.Add) &&
                     insn.Rd != 0 && (insn.Rt == 0 || insn.Rs == 0))
            {
                uint src = insn.Rt == 0 ? insn.Rs : insn.Rt;
                if (src != 0 && luiValid[src])
                {
                    luiRegs[insn.Rd] = luiRegs[src];
                    luiValid[insn.Rd] = true;
                }
                else
                {
                    luiValid[insn.Rd] = false;
                }
            }
            else if ((insn.Op == R5900Op.Addiu || insn.Op == R5900Op.Addi || insn.Op == R5900Op.Daddiu) && luiValid[insn.Rs])
            {
                uint eff = (uint)(luiRegs[insn.Rs] + insn.Simm);
                if (targetSet.Contains(eff))
                {
                    results.Add($"[XREF] sub_{currentFuncStart:X8} + 0x{v - currentFuncStart:X} @ 0x{v:X8}: {insn} -> 0x{eff:X8}");
                }
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
            }
            else if (insn.Op == R5900Op.Ori && luiValid[insn.Rs])
            {
                uint eff = luiRegs[insn.Rs] | insn.Uimm;
                if (targetSet.Contains(eff))
                {
                    results.Add($"[XREF] sub_{currentFuncStart:X8} + 0x{v - currentFuncStart:X} @ 0x{v:X8}: {insn} -> 0x{eff:X8}");
                }
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
            }
            else if ((insn.IsLoad || insn.IsStore) && luiValid[insn.Rs])
            {
                uint eff = (uint)(luiRegs[insn.Rs] + insn.Simm);
                if (targetSet.Contains(eff))
                {
                    results.Add($"[MEM] sub_{currentFuncStart:X8} + 0x{v - currentFuncStart:X} @ 0x{v:X8}: {insn} -> 0x{eff:X8}");
                }
                uint written = insn.WrittenGpr;
                if (written != 0 && luiValid[written])
                {
                    luiValid[written] = false;
                }
            }
            else
            {
                // Invalidate destination register if overwritten
                uint written = insn.WrittenGpr;
                if (written != 0 && luiValid[written])
                {
                    luiValid[written] = false;
                }
            }

            // Reset register state after the delay slot of function returns (JR $ra)
            if (clearAfterDelaySlot)
            {
                Array.Clear(luiValid, 0, 32);
                currentFuncStart = v + 4;
                clearAfterDelaySlot = false;
            }
            else if (insn.IsReturn)
            {
                clearAfterDelaySlot = true;
            }
        }

        Console.WriteLine($"Found {results.Count} cross reference(s):");
        foreach (var r in results.Take(maxLines))
        {
            Console.WriteLine(r);
        }

        if (results.Count > maxLines)
        {
            Console.WriteLine($"... [Truncated {results.Count - maxLines} lines. Narrow your query]");
        }

        return 0;
    }

    private static List<uint> FindStringOccurrences(Ps2ElfReader reader, string search)
    {
        List<uint> found = new();
        byte[] pattern = Encoding.ASCII.GetBytes(search);
        byte[] raw = reader.GetRawBytes();

        foreach (var sec in reader.Sections.Where(s => s.Name == ".rodata" || s.Name == ".data" || s.Name == ".text"))
        {
            int start = (int)sec.FileOffset;
            int len = (int)sec.Size;
            int end = start + len - pattern.Length;

            for (int i = start; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (raw[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    uint vaddr = sec.VAddr + (uint)(i - start);
                    found.Add(vaddr);
                }
            }
        }

        return found;
    }
}
