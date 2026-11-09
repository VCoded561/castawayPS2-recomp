using System.Buffers.Binary;
using System.Text;

namespace Castaway.ElfTools.Elf;

public sealed class ElfProgramHeader
{
    public uint Type { get; set; }
    public uint FileOffset { get; set; }
    public uint VAddr { get; set; }
    public uint PAddr { get; set; }
    public uint FileSize { get; set; }
    public uint MemSize { get; set; }
    public uint Flags { get; set; }
    public uint Align { get; set; }

    public bool IsLoad => Type == 1; // PT_LOAD
    public bool ContainsVAddr(uint vaddr) => vaddr >= VAddr && vaddr < VAddr + FileSize;
}

public sealed class ElfSection
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Type { get; set; }
    public uint Flags { get; set; }
    public uint VAddr { get; set; }
    public uint FileOffset { get; set; }
    public uint Size { get; set; }
    public uint Link { get; set; }
    public uint Info { get; set; }
    public uint AddrAlign { get; set; }
    public uint EntSize { get; set; }

    public bool IsNoBits => Type == 8; // SHT_NOBITS (.bss)
    public bool ContainsVAddr(uint vaddr) => vaddr >= VAddr && vaddr < VAddr + Size;
    public bool IsExecutable => (Flags & 0x4) != 0;
}

public sealed class Ps2ElfReader
{
    private readonly byte[] _rawBytes;

    public string FilePath { get; }
    public uint EntryPoint { get; private set; }
    public List<ElfProgramHeader> ProgramHeaders { get; } = new();
    public List<ElfSection> Sections { get; } = new();

    public ElfSection? TextSection { get; private set; }
    public ElfSection? RodataSection { get; private set; }
    public ElfSection? DataSection { get; private set; }
    public ElfSection? BssSection { get; private set; }

    public Ps2ElfReader(string filePath)
    {
        FilePath = filePath;
        _rawBytes = File.ReadAllBytes(filePath);
        Parse();
    }

    public static string FindDefaultElfPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string[] relCandidates =
        {
            Path.Combine("Data", "DVD", "SLUS_216.64"),
            Path.Combine("Game Data", "DVD", "SLUS_216.64")
        };

        // Check starting from current directory and AppContext.BaseDirectory, walking up parent directories
        string?[] startDirs = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

        foreach (var start in startDirs)
        {
            if (string.IsNullOrEmpty(start)) continue;
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                foreach (var rel in relCandidates)
                {
                    string full = Path.Combine(dir.FullName, rel);
                    if (File.Exists(full))
                        return Path.GetFullPath(full);
                }
                dir = dir.Parent;
            }
        }

        // Try searching working directory
        var found = Directory.GetFiles(".", "SLUS_216.64", SearchOption.AllDirectories).FirstOrDefault();
        if (found != null)
            return Path.GetFullPath(found);

        throw new FileNotFoundException("Could not find SLUS_216.64. Please specify with --elf <path>.");
    }

    private void Parse()
    {
        ReadOnlySpan<byte> span = _rawBytes;
        if (span.Length < 52)
            throw new InvalidDataException("File is too small to be a valid ELF32.");

        // Magic \x7F ELF
        if (span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
            throw new InvalidDataException("Invalid ELF magic header.");

        byte elfClass = span[4]; // 1 = 32-bit
        byte elfData = span[5];  // 1 = 2's complement, little endian
        if (elfClass != 1 || elfData != 1)
            throw new InvalidDataException($"Unsupported ELF class {elfClass} or data encoding {elfData}.");

        EntryPoint = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x18, 4));
        uint phOff = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x1C, 4));
        uint shOff = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x20, 4));
        ushort phEntSize = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x2A, 2));
        ushort phNum = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x2C, 2));
        ushort shEntSize = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x2E, 2));
        ushort shNum = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x30, 2));
        ushort shStrNdx = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x32, 2));

        // Parse Program Headers
        for (int i = 0; i < phNum; i++)
        {
            int offset = (int)(phOff + i * phEntSize);
            if (offset + 32 > span.Length) break;

            var ph = new ElfProgramHeader
            {
                Type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4)),
                FileOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4)),
                VAddr = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 8, 4)),
                PAddr = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 12, 4)),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 16, 4)),
                MemSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 20, 4)),
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 24, 4)),
                Align = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 28, 4)),
            };
            ProgramHeaders.Add(ph);
        }

        // Parse Section Headers
        if (shOff > 0 && shNum > 0 && shOff + shNum * shEntSize <= span.Length)
        {
            // First pass: create section headers
            for (int i = 0; i < shNum; i++)
            {
                int offset = (int)(shOff + i * shEntSize);
                var sec = new ElfSection
                {
                    Index = i,
                    Type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4)),
                    Flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 8, 4)),
                    VAddr = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 12, 4)),
                    FileOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 16, 4)),
                    Size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 20, 4)),
                    Link = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 24, 4)),
                    Info = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 28, 4)),
                    AddrAlign = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 32, 4)),
                    EntSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 36, 4)),
                };
                Sections.Add(sec);
            }

            // Read Section Names from shstrtab
            if (shStrNdx < Sections.Count)
            {
                var strSec = Sections[shStrNdx];
                int strOff = (int)strSec.FileOffset;
                int strSize = (int)strSec.Size;

                if (strOff + strSize <= span.Length)
                {
                    for (int i = 0; i < Sections.Count; i++)
                    {
                        int offset = (int)(shOff + i * shEntSize);
                        uint nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                        if (strOff + nameIdx < span.Length)
                        {
                            Sections[i].Name = ReadAsciiZAt((int)(strOff + nameIdx));
                        }
                    }
                }
            }

            TextSection = Sections.FirstOrDefault(s => s.Name == ".text");
            RodataSection = Sections.FirstOrDefault(s => s.Name == ".rodata");
            DataSection = Sections.FirstOrDefault(s => s.Name == ".data");
            BssSection = Sections.FirstOrDefault(s => s.Name == ".bss");
        }
    }

    private string ReadAsciiZAt(int offset)
    {
        if (offset >= _rawBytes.Length) return string.Empty;
        int end = offset;
        while (end < _rawBytes.Length && _rawBytes[end] != 0)
            end++;
        return Encoding.ASCII.GetString(_rawBytes, offset, end - offset);
    }

    public static uint NormalizeAddress(uint vaddr)
    {
        // PS2 KSEG0/KSEG1 mapping to physical EE RAM (0x00000000..0x01FFFFFF)
        if (vaddr >= 0x80000000 && vaddr < 0xC0000000)
            return vaddr & 0x1FFFFFFFu;
        return vaddr;
    }

    public bool TryVAddrToFileOffset(uint vaddr, out uint fileOffset)
    {
        uint norm = NormalizeAddress(vaddr);

        // First check Program Headers (PT_LOAD)
        foreach (var ph in ProgramHeaders)
        {
            if (ph.IsLoad && ph.ContainsVAddr(norm))
            {
                fileOffset = ph.FileOffset + (norm - ph.VAddr);
                return fileOffset < _rawBytes.Length;
            }
        }

        // Next check Section Headers (non-NOBITS)
        foreach (var sec in Sections)
        {
            if (!sec.IsNoBits && sec.ContainsVAddr(norm))
            {
                fileOffset = sec.FileOffset + (norm - sec.VAddr);
                return fileOffset < _rawBytes.Length;
            }
        }

        fileOffset = 0;
        return false;
    }

    public uint VAddrToFileOffset(uint vaddr)
    {
        if (TryVAddrToFileOffset(vaddr, out uint offset))
            return offset;
        throw new ArgumentOutOfRangeException(nameof(vaddr), $"Virtual address 0x{vaddr:X8} does not map to any file section/segment.");
    }

    public bool IsVAddrValid(uint vaddr) => TryVAddrToFileOffset(vaddr, out _);

    public bool IsInText(uint vaddr)
    {
        uint norm = NormalizeAddress(vaddr);
        if (TextSection != null && TextSection.ContainsVAddr(norm)) return true;
        var sec = FindSectionContaining(norm);
        return sec != null && sec.IsExecutable && sec.Name != ".vutext";
    }

    public bool IsInRodata(uint vaddr)
    {
        uint norm = NormalizeAddress(vaddr);
        return RodataSection != null && RodataSection.ContainsVAddr(norm);
    }

    public bool IsInData(uint vaddr)
    {
        uint norm = NormalizeAddress(vaddr);
        return DataSection != null && DataSection.ContainsVAddr(norm);
    }

    public ElfSection? FindSection(string name) => Sections.FirstOrDefault(s => s.Name == name);

    public ElfSection? FindSectionContaining(uint vaddr)
    {
        uint norm = NormalizeAddress(vaddr);
        return Sections.FirstOrDefault(s => s.ContainsVAddr(norm));
    }

    public uint ReadUInt32(uint vaddr)
    {
        uint fileOffset = VAddrToFileOffset(vaddr);
        return BinaryPrimitives.ReadUInt32LittleEndian(_rawBytes.AsSpan((int)fileOffset, 4));
    }

    public ushort ReadUInt16(uint vaddr)
    {
        uint fileOffset = VAddrToFileOffset(vaddr);
        return BinaryPrimitives.ReadUInt16LittleEndian(_rawBytes.AsSpan((int)fileOffset, 2));
    }

    public byte ReadByte(uint vaddr)
    {
        uint fileOffset = VAddrToFileOffset(vaddr);
        return _rawBytes[fileOffset];
    }

    public ReadOnlySpan<byte> ReadSpan(uint vaddr, int count)
    {
        uint fileOffset = VAddrToFileOffset(vaddr);
        int available = Math.Min(count, _rawBytes.Length - (int)fileOffset);
        if (available <= 0) return ReadOnlySpan<byte>.Empty;
        return _rawBytes.AsSpan((int)fileOffset, available);
    }

    public string ReadAsciiZ(uint vaddr, int maxLen = 256)
    {
        if (!TryVAddrToFileOffset(vaddr, out uint fileOffset))
            return string.Empty;

        int end = (int)fileOffset;
        int max = Math.Min(_rawBytes.Length, end + maxLen);
        while (end < max && _rawBytes[end] != 0)
            end++;

        return Encoding.ASCII.GetString(_rawBytes, (int)fileOffset, end - (int)fileOffset);
    }

    public byte[] GetRawBytes() => _rawBytes;
}
