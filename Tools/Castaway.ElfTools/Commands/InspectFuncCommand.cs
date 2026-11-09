using System.Text;
using System.Text.RegularExpressions;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class InspectFuncCommand
{
    public static int Run(string[] args)
    {
        uint? addr = null;
        int count = 45;
        bool withCSharp = false;
        string? elfPath = null;
        int maxLines = 200;
        bool exact = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--addr" || arg == "-a") && i + 1 < args.Length)
            {
                addr = ParseHex(args[++i]);
            }
            else if (arg == "--exact")
            {
                exact = true;
            }
            else if (arg == "--count" && i + 1 < args.Length)
            {
                int.TryParse(args[++i], out count);
            }
            else if (arg == "--with-csharp")
            {
                withCSharp = true;
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
                addr = ParseHex(arg);
            }
        }

        if (addr == null)
        {
            Console.WriteLine("Error: Missing --addr <hex_address>");
            return 1;
        }

        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);

        uint targetAddr = addr.Value;
        uint startAddr = targetAddr;
        uint? endAddr = null;

        // Try to read function bounds from castaway_functions.toml if present
        if (!exact && File.Exists("castaway_functions.toml"))
        {
            var bounds = TryFindFunctionBounds("castaway_functions.toml", targetAddr);
            if (bounds.HasValue)
            {
                startAddr = bounds.Value.start;
                endAddr = bounds.Value.end;
            }
        }

        List<string> lines = new();
        lines.Add($"--- Inspecting function at 0x{startAddr:X8} (ELF: {Path.GetFileName(resolvedElf)}) ---");

        uint[] luiRegs = new uint[32];
        bool[] luiValid = new bool[32];
        uint curr = startAddr;
        int insnCount = 0;
        bool inDelaySlot = false;
        bool stopAfterDelaySlot = false;

        while (reader.IsVAddrValid(curr))
        {
            if (endAddr.HasValue && curr >= endAddr.Value) break;
            if (!endAddr.HasValue && insnCount >= count && !inDelaySlot) break;

            uint word = reader.ReadUInt32(curr);
            var insn = R5900Decoder.Decode(word, curr);

            // Stateful LUI tracking
            string comment = "";
            if (insn.Op == R5900Op.Lui)
            {
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = (uint)insn.Uimm << 16;
                    luiValid[insn.Rt] = true;
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
                comment = ResolveAddressComment(reader, eff);
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
            }
            else if (insn.Op == R5900Op.Ori && luiValid[insn.Rs])
            {
                uint eff = luiRegs[insn.Rs] | insn.Uimm;
                comment = ResolveAddressComment(reader, eff);
                if (insn.Rt != 0)
                {
                    luiRegs[insn.Rt] = eff;
                    luiValid[insn.Rt] = true;
                }
            }
            else if (insn.IsLoad || insn.IsStore)
            {
                if (luiValid[insn.Rs])
                {
                    uint eff = (uint)(luiRegs[insn.Rs] + insn.Simm);
                    comment = ResolveAddressComment(reader, eff);
                }
                uint written = insn.WrittenGpr;
                if (written != 0 && luiValid[written])
                {
                    luiValid[written] = false;
                }
            }
            else
            {
                uint written = insn.WrittenGpr;
                if (written != 0 && luiValid[written])
                {
                    luiValid[written] = false;
                }
            }

            string flags = "";
            if (inDelaySlot) flags += " [DELAY SLOT]";
            if (insn.IsBranchLikely) flags += " [BRANCH LIKELY - annul on false]";
            if (insn.Op == R5900Op.Jal) flags += $" [CALL -> 0x{insn.JumpTarget:X8}]";

            string line = $"0x{curr:X8}: {word:X8}   {insn,-32} {flags} {comment}".TrimEnd();
            lines.Add(line);

            if (stopAfterDelaySlot)
                break;

            if (inDelaySlot)
            {
                inDelaySlot = false;
                if (!endAddr.HasValue && insnCount >= count)
                    break;
            }
            else if (insn.HasDelaySlot)
            {
                inDelaySlot = true;
                if (!endAddr.HasValue && (insn.IsReturn || (insn.Flow == FlowType.Stop)))
                {
                    stopAfterDelaySlot = true;
                }
            }

            curr += 4;
            insnCount++;
        }

        if (withCSharp)
        {
            lines.Add("");
            lines.Add($"--- Emitted C# Method: sub_{startAddr:X8} ---");
            uint shardBase = startAddr & 0xFFFFC000u;
            string shardFile = FindShardFile(shardBase);

            if (File.Exists(shardFile))
            {
                var csLines = ExtractCSharpFunction(shardFile, startAddr);
                lines.AddRange(csLines);
            }
            else
            {
                lines.Add($"// [C# source not yet generated in {shardFile}]");
            }
        }

        foreach (var l in lines.Take(maxLines))
        {
            Console.WriteLine(l);
        }

        if (lines.Count > maxLines)
        {
            Console.WriteLine($"... [Truncated {lines.Count - maxLines} lines. Use --max-lines to see more]");
        }

        return 0;
    }

    private static (uint start, uint end)? TryFindFunctionBounds(string tomlPath, uint vaddr)
    {
        try
        {
            uint currentStart = 0;
            uint currentEnd = 0;
            bool insideFunc = false;

            foreach (var line in File.ReadLines(tomlPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[[functions]]"))
                {
                    if (insideFunc && vaddr >= currentStart && vaddr < currentEnd)
                        return (currentStart, currentEnd);
                    insideFunc = true;
                    currentStart = 0;
                    currentEnd = 0;
                }
                else if (insideFunc)
                {
                    if (trimmed.StartsWith("start ="))
                    {
                        currentStart = ParseHex(trimmed.Split('=')[1].Trim());
                    }
                    else if (trimmed.StartsWith("end ="))
                    {
                        currentEnd = ParseHex(trimmed.Split('=')[1].Trim());
                    }
                }
            }

            if (insideFunc && vaddr >= currentStart && vaddr < currentEnd)
                return (currentStart, currentEnd);
        }
        catch
        {
            // Ignore TOML read errors
        }

        return null;
    }

    private static string FindShardFile(uint shardBase)
    {
        string rel = Path.Combine("src", "Castaway.Generated", $"Funcs_{shardBase:X8}.cs");
        if (File.Exists(rel)) return rel;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return rel;
    }

    private static List<string> ExtractCSharpFunction(string filePath, uint funcAddr)
    {
        List<string> result = new();
        string targetSig = $"void sub_{funcAddr:X8}(";
        bool capturing = false;
        int braceDepth = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (!capturing)
            {
                if (line.Contains(targetSig))
                {
                    capturing = true;
                    result.Add(line);
                    braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                }
            }
            else
            {
                result.Add(line);
                braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                if (braceDepth <= 0 && result.Count > 1)
                    break;
            }
        }

        return result;
    }

    private static string ResolveAddressComment(Ps2ElfReader reader, uint eff)
    {
        if (reader.IsInRodata(eff) || reader.IsInData(eff))
        {
            string str = reader.ReadAsciiZ(eff, 40);
            if (!string.IsNullOrEmpty(str) && str.All(c => c >= 32 && c <= 126))
            {
                return $"; -> 0x{eff:X8} (\"{str}\")";
            }
            return $"; -> 0x{eff:X8}";
        }
        if (reader.IsInText(eff))
        {
            return $"; -> 0x{eff:X8} <sub_{eff:X8}>";
        }
        if (eff >= 0x10000000 && eff < 0x12002000)
        {
            return $"; -> 0x{eff:X8} [MMIO]";
        }
        return $"; -> 0x{eff:X8}";
    }

    public static uint ParseHex(string s)
    {
        s = s.Trim().Trim('"', '\'');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return Convert.ToUInt32(s, 16);
    }
}
