using System.Buffers.Binary;
using System.Text.Json;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public class Vu1ProgramManifestItem
{
    public string Name { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string VAddr { get; set; } = string.Empty;
    public int VuDestByteAddr { get; set; }
    public int SizeBytes { get; set; }
    public int InstructionCount { get; set; }
    public string Crc32 { get; set; } = string.Empty;
    public List<string> DisassemblySample { get; set; } = new();
}

public static class VifVu1ScannerCommand
{
    private static bool _fullDisasm = false;

    public static int Run(string[] args)
    {
        string outFile = "vu1_programs.json";
        string? elfPath = null;
        int maxLines = 80;
        _fullDisasm = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--full")
            {
                _fullDisasm = true;
            }
            else if ((arg == "--out" || arg == "-o") && i + 1 < args.Length)
            {
                outFile = args[++i];
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

        List<Vu1ProgramManifestItem> programs = new();
        List<string> summaryLines = new();
        summaryLines.Add($"=== Scanning {Path.GetFileName(resolvedElf)} for VU1 Microprograms ===");

        // 1. Scan .vutext for DMA tags + VIF MPG (0x4A) packets
        var vutext = reader.FindSection(".vutext");
        if (vutext != null && vutext.Size > 0 && !vutext.IsNoBits)
        {
            ScanSectionForMpg(reader, vutext, programs, summaryLines, isVuText: true);
        }

        // 2. Scan .data and .rodata for validated VIF MPG packets (addr < 0x0800 and valid VU1 opcodes)
        foreach (var sec in reader.Sections.Where(s => (s.Name == ".data" || s.Name == ".rodata") && !s.IsNoBits))
        {
            ScanSectionForMpg(reader, sec, programs, summaryLines, isVuText: false);
        }

        summaryLines.Add($"Total VU1 MPG Microprogram Blocks cataloged: {programs.Count}");

        // Save JSON manifest
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(programs, options);
            File.WriteAllText(outFile, json);
            summaryLines.Add($"Saved VU1 microprogram manifest to {outFile}");
        }
        catch (Exception ex)
        {
            summaryLines.Add($"Failed to write {outFile}: {ex.Message}");
        }

        foreach (var l in summaryLines.Take(maxLines))
        {
            Console.WriteLine(l);
        }

        return 0;
    }

    private static void ScanSectionForMpg(
        Ps2ElfReader reader,
        ElfSection sec,
        List<Vu1ProgramManifestItem> programs,
        List<string> summaryLines,
        bool isVuText)
    {
        byte[] raw = reader.GetRawBytes();
        int start = (int)sec.FileOffset;
        int end = Math.Min(raw.Length, start + (int)sec.Size) - 8;

        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);

            // VIF MPG command is 0x4A (or 0xCA with interrupt bit).
            // VU1 MicroMem is 16 KiB = 2048 64-bit instructions, so valid destination address in 64-bit units is < 2048 (0x0800).
            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800)
                continue;

            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)sec.Size)
                continue;

            var payload = raw.AsSpan(i + 4, mpgBytes);
            if (!isVuText && !LooksLikeValidVu1Microcode(payload))
                continue;

            uint vaddr = sec.VAddr + (uint)(i + 4 - start);
            int vuDestByteAddr = addr * 8;
            string name = $"vu1_mpg_0x{vaddr:X8}_dst_{vuDestByteAddr:X4}";
            var item = ProcessVuBlock(payload, name, sec.Name, vaddr, vuDestByteAddr);
            programs.Add(item);
            summaryLines.Add($"[VIF MPG] {name,-38} @ 0x{vaddr:X8} -> VU1[0x{vuDestByteAddr:X4}] ({mpgBytes,4} B, {insnCount,3} insns, CRC: {item.Crc32})");
            i += 4 + mpgBytes - 4;
        }
    }

    private static bool LooksLikeValidVu1Microcode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) return false;
        int validCount = 0;
        int total = payload.Length / 8;
        for (int i = 0; i < total; i++)
        {
            uint lower = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i * 8, 4));
            uint upper = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i * 8 + 4, 4));
            if (upper == 0 && lower == 0)
                return false;
            // Check if upper is NOP (0x000002FF) or has valid opcode bits, or E-bit (0x40000000), or lower is NOP (0x8000033C)
            if ((upper & 0x07FFFFFFu) == 0x000002FFu || lower == 0x8000033Cu || (upper & 0x40000000u) != 0)
                validCount++;
        }
        return validCount >= 2;
    }

    private static Vu1ProgramManifestItem ProcessVuBlock(
        ReadOnlySpan<byte> span,
        string name,
        string sectionName,
        uint vaddr,
        int vuDestByteAddr)
    {
        uint crc = Crc32.Compute(span);
        int insnCount = span.Length / 8;

        var item = new Vu1ProgramManifestItem
        {
            Name = name,
            Section = sectionName,
            VAddr = $"0x{vaddr:X8}",
            VuDestByteAddr = vuDestByteAddr,
            SizeBytes = span.Length,
            InstructionCount = insnCount,
            Crc32 = $"0x{crc:X8}"
        };

        int sampleCount = _fullDisasm ? insnCount : Math.Min(6, insnCount);
        for (int i = 0; i < sampleCount; i++)
        {
            uint lower = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i * 8, 4));
            uint upper = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i * 8 + 4, 4));
            item.DisassemblySample.Add(Vu1Disassembler.FormatBundle(vuDestByteAddr / 8 + i, upper, lower));
        }

        return item;
    }
}
