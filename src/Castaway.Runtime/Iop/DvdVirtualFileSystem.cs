using System.Buffers.Binary;
using System.Text;

namespace Castaway.Runtime;

public sealed class DvdFileEntry
{
    public required string HostPath { get; init; }
    public required string VirtualPath { get; init; }
    public required uint StartLba { get; init; }
    public required long SizeBytes { get; init; }
    public uint SectorCount => (uint)((SizeBytes + DvdVirtualFileSystem.SectorSize - 1) / DvdVirtualFileSystem.SectorSize);
    public uint EndLba => StartLba + Math.Max(1u, SectorCount);
}

public sealed unsafe class DvdVirtualFileSystem : IDisposable
{
    public const int SectorSize = 2048;
    public const uint BaseLba = 256;

    private readonly string _dvdRootPath;
    private readonly Dictionary<string, DvdFileEntry> _filesByVirtualPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DvdFileEntry> _sortedByLba = new();
    private readonly Dictionary<int, OpenFileHandle> _openHandles = new();
    private readonly Dictionary<uint, int> _eaSlotToFd = new();
    private byte[]? _spu2Ram;
    private int _nextFd = 3; // 0=stdin, 1=stdout, 2=stderr

    public void AttachSpu2Ram(byte[]? spu2Ram) => _spu2Ram = spu2Ram;

    private sealed class OpenFileHandle : IDisposable
    {
        public required FileStream Stream { get; init; }
        public required DvdFileEntry Entry { get; init; }

        public void Dispose() => Stream.Dispose();
    }

    public string DvdRootPath => _dvdRootPath;
    public IReadOnlyList<DvdFileEntry> Entries => _sortedByLba;

    public DvdVirtualFileSystem(string? dvdRootPath = null)
    {
        _dvdRootPath = ResolveDvdRootPath(dvdRootPath);
        BuildCatalog();
    }

    public static string ResolveDvdRootPath(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && Directory.Exists(customPath))
            return Path.GetFullPath(customPath);

        string cur = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(cur, "Data", "DVD");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            var parent = Directory.GetParent(cur);
            if (parent == null) break;
            cur = parent.FullName;
        }

        string cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "Data", "DVD");
        return Path.GetFullPath(cwdCandidate);
    }

    private void BuildCatalog()
    {
        _filesByVirtualPath.Clear();
        _sortedByLba.Clear();

        if (!Directory.Exists(_dvdRootPath))
            return;

        uint currentLba = BaseLba;
        var allFiles = Directory.GetFiles(_dvdRootPath, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (string fullPath in allFiles)
        {
            var info = new FileInfo(fullPath);
            string rel = Path.GetRelativePath(_dvdRootPath, fullPath)
                .Replace('\\', '/')
                .TrimStart('/')
                .ToUpperInvariant();

            var entry = new DvdFileEntry
            {
                HostPath = fullPath,
                VirtualPath = rel,
                StartLba = currentLba,
                SizeBytes = info.Length
            };

            _filesByVirtualPath[rel] = entry;
            _sortedByLba.Add(entry);

            // Advance LBA with 4-sector alignment padding between files
            currentLba = entry.EndLba + 4;
        }
    }

    public static string NormalizeGuestPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        string s = rawPath.Trim();

        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
            s = s[(colonIdx + 1)..];

        int semiIdx = s.IndexOf(';');
        if (semiIdx >= 0)
            s = s[..semiIdx];

        s = s.Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal))
            s = s.Replace("//", "/", StringComparison.Ordinal);

        return s.Trim('/').ToUpperInvariant();
    }

    public DvdFileEntry? FindFile(string guestPath)
    {
        string norm = NormalizeGuestPath(guestPath);
        if (string.IsNullOrEmpty(norm))
            return null;

        if (_filesByVirtualPath.TryGetValue(norm, out var entry))
            return entry;

        // Also check if guest omitted DATA/ prefix for an archive inside DATA/
        if (_filesByVirtualPath.TryGetValue("DATA/" + norm, out entry))
            return entry;

        // Or check filename-only match as fallback
        string fileName = Path.GetFileName(norm);
        foreach (var kvp in _filesByVirtualPath)
        {
            if (string.Equals(Path.GetFileName(kvp.Key), fileName, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    public DvdFileEntry? FindFileByLba(uint lba)
    {
        foreach (var entry in _sortedByLba)
        {
            if (lba >= entry.StartLba && lba < entry.EndLba)
                return entry;
        }
        return null;
    }

    public bool ReadSectors(uint startLba, uint sectorCount, Span<byte> destination)
    {
        int totalBytes = checked((int)(sectorCount * SectorSize));
        if (destination.Length < totalBytes)
            return false;

        destination[..totalBytes].Clear();

        for (uint s = 0; s < sectorCount; s++)
        {
            uint curLba = startLba + s;
            var entry = FindFileByLba(curLba);
            if (entry == null)
                continue;

            long fileOffset = (long)(curLba - entry.StartLba) * SectorSize;
            if (fileOffset >= entry.SizeBytes)
                continue;

            int bytesToRead = (int)Math.Min(SectorSize, entry.SizeBytes - fileOffset);
            Span<byte> sectorSlice = destination.Slice((int)(s * SectorSize), bytesToRead);

            using var fs = new FileStream(entry.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(fileOffset, SeekOrigin.Begin);
            int readTotal = 0;
            while (readTotal < bytesToRead)
            {
                int r = fs.Read(sectorSlice[readTotal..]);
                if (r <= 0) break;
                readTotal += r;
            }
        }

        return true;
    }

    public bool ReadSectorsToRam(uint startLba, uint sectorCount, byte* ram, uint destGuestAddr)
    {
        uint phys = destGuestAddr & 0x01FFFFFFu;
        int totalBytes = checked((int)(sectorCount * SectorSize));
        if (phys + (uint)totalBytes > 32 * 1024 * 1024)
            return false;

        var span = new Span<byte>(ram + phys, totalBytes);
        return ReadSectors(startLba, sectorCount, span);
    }

    public int Open(string guestPath, int flags = 1)
    {
        var entry = FindFile(guestPath);
        if (entry == null || !File.Exists(entry.HostPath))
        {
            Console.WriteLine($"[DVD NOT FOUND] guestPath='{guestPath}' norm='{NormalizeGuestPath(guestPath)}'");
            return -2; // ENOENT
        }

        var stream = new FileStream(entry.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int fd = _nextFd++;
        _openHandles[fd] = new OpenFileHandle
        {
            Stream = stream,
            Entry = entry
        };
        Console.WriteLine($"[DVD OPENED] fd={fd} path='{entry.VirtualPath}' size={entry.SizeBytes}");
        return fd;
    }

    public int Close(int fd)
    {
        if (_openHandles.Remove(fd, out var handle))
        {
            handle.Dispose();
            return 0;
        }
        return -9; // EBADF
    }

    public int Read(int fd, Span<byte> buffer)
    {
        if (!_openHandles.TryGetValue(fd, out var handle))
            return -9;

        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int r = handle.Stream.Read(buffer[totalRead..]);
            if (r <= 0) break;
            totalRead += r;
        }
        return totalRead;
    }

    public int ReadToRam(int fd, byte* ram, uint destGuestAddr, int byteCount)
    {
        if (byteCount <= 0)
            return 0;

        uint phys = destGuestAddr & 0x01FFFFFFu;
        if (phys + (uint)byteCount > 32 * 1024 * 1024)
            return -14; // EFAULT

        var span = new Span<byte>(ram + phys, byteCount);
        return Read(fd, span);
    }

    public long Seek(int fd, long offset, int whence)
    {
        if (!_openHandles.TryGetValue(fd, out var handle))
            return -9;

        SeekOrigin origin = whence switch
        {
            0 => SeekOrigin.Begin,   // SEEK_SET
            1 => SeekOrigin.Current, // SEEK_CUR
            2 => SeekOrigin.End,     // SEEK_END
            _ => SeekOrigin.Begin
        };

        return handle.Stream.Seek(offset, origin);
    }

    public static string ReadGuestCString(byte* ram, uint guestAddr, int maxBytes = 256)
    {
        uint phys = guestAddr & 0x01FFFFFFu;
        int len = 0;
        while (len < maxBytes && phys + (uint)len < 32 * 1024 * 1024)
        {
            if (ram[phys + len] == 0)
                break;
            len++;
        }
        return len == 0 ? string.Empty : Encoding.ASCII.GetString(ram + phys, len);
    }

    /// <summary>
    /// Populates a PS2 sceCdlFILE structure in EE RAM:
    /// +0x00: uint32 lsn (LBA)
    /// +0x04: uint32 size (bytes)
    /// +0x08: char name[16]
    /// +0x18: byte date[8]
    /// </summary>
    public bool PopulateCdlFileStruct(byte* ram, uint cdlFileStructAddr, string guestPath)
    {
        var entry = FindFile(guestPath);
        if (entry == null)
            return false;

        uint phys = cdlFileStructAddr & 0x01FFFFFFu;
        Span<byte> pkt = new Span<byte>(ram + phys, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt[0..4], entry.StartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt[4..8], (uint)entry.SizeBytes);

        pkt[8..24].Clear();
        string shortName = Path.GetFileName(entry.VirtualPath) + ";1";
        byte[] nameBytes = Encoding.ASCII.GetBytes(shortName);
        nameBytes.AsSpan(0, Math.Min(15, nameBytes.Length)).CopyTo(pkt[8..24]);

        // Timestamp BCD
        pkt[24] = 0x00;
        pkt[25] = 0x00;
        pkt[26] = 0x12;
        pkt[27] = 0x15;
        pkt[28] = 0x09;
        pkt[29] = 0x07;
        return true;
    }

    public int OpenEaFileSysSlot(byte* ram, uint cFileSysAddr, uint openReqAddr)
    {
        string guestPath = ReadGuestCString(ram, openReqAddr + 28, 255);
        Console.WriteLine($"[CFILESYS OPEN] guestPath='{guestPath}' openReq=0x{openReqAddr:X8}");
        var fileEntry = FindFile(guestPath);
        if (fileEntry == null)
            return 0;

        int fd = Open(guestPath);
        if (fd < 0)
            return 0;

        uint sysPhys = cFileSysAddr & 0x01FFFFFFu;
        uint tableBase = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 24, 4));
        uint maxSlots = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 28, 4));
        if (tableBase == 0 || maxSlots <= 1)
        {
            Close(fd);
            return 0;
        }

        uint chosenSlot = 0;
        for (uint slot = 1; slot < maxSlots; slot++)
        {
            uint entryPhys = (tableBase + slot * 260u) & 0x01FFFFFFu;
            uint inUse = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + entryPhys + 60, 4));
            if (inUse == 0)
            {
                chosenSlot = slot;
                break;
            }
        }

        if (chosenSlot == 0)
        {
            Close(fd);
            return 0;
        }

        _eaSlotToFd[chosenSlot] = fd;

        uint slotPhys = (tableBase + chosenSlot * 260u) & 0x01FFFFFFu;
        Span<byte> entry = new Span<byte>(ram + slotPhys, 260);
        entry.Slice(20, 40).Clear();

        uint streamTblPhys = (0x00598080u + 0u * 12u) & 0x01FFFFFFu;
        uint eeRingBuf = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + streamTblPhys + 4, 4));
        uint streamMutex = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + streamTblPhys + 8, 4));

        BinaryPrimitives.WriteUInt32LittleEndian(entry[0..4], (uint)fileEntry.SizeBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..20], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..28], chosenSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[32..36], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[60..64], 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[64..68], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..72], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..76], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..80], 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[80..84], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[84..88], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[88..92], eeRingBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[92..96], streamMutex);

        entry.Slice(96, 64).Clear();
        byte[] pathBytes = Encoding.ASCII.GetBytes(guestPath);
        pathBytes.AsSpan(0, Math.Min(63, pathBytes.Length)).CopyTo(entry.Slice(96, 63));

        BinaryPrimitives.WriteUInt32LittleEndian(entry[160..164], 256u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[164..168], fileEntry.StartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[212..216], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[216..220], 0xFFFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[228..232], 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[232..236], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[236..240], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[240..244], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[244..248], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[248..252], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[252..256], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[256..260], 0u);

        return (int)chosenSlot;
    }

    private int ReadToSpu2Ram(int fd, byte[] spu2Ram, uint destAddr, int count)
    {
        if (count <= 0 || !_openHandles.TryGetValue(fd, out var handle))
            return 0;

        int offset = (int)(destAddr & 0x001FFFFFu);
        if (offset >= spu2Ram.Length)
            return 0;

        int maxCount = Math.Min(count, spu2Ram.Length - offset);
        int total = 0;
        while (total < maxCount)
        {
            int read = handle.Stream.Read(spu2Ram.AsSpan(offset + total, maxCount - total));
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    public int ReadEaFileSysSlot(byte* ram, uint cFileSysAddr, uint readReqAddr, byte[]? spu2Ram = null)
    {
        uint reqPhys = readReqAddr & 0x01FFFFFFu;
        ReadOnlySpan<byte> req = new ReadOnlySpan<byte>(ram + reqPhys, 40);
        uint slot = BinaryPrimitives.ReadUInt32LittleEndian(req[4..8]);
        byte reqFlags = req[8];
        ushort reqDestFlags = BinaryPrimitives.ReadUInt16LittleEndian(req[10..12]);
        uint reqOffset = BinaryPrimitives.ReadUInt32LittleEndian(req[12..16]);
        uint destBuf = BinaryPrimitives.ReadUInt32LittleEndian(req[32..36]);
        int byteCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(req[36..40]);

        uint sysPhys = cFileSysAddr & 0x01FFFFFFu;
        uint tableBase = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 24, 4));
        uint maxSlots = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 28, 4));
        if (slot == 0 || slot >= maxSlots || !_eaSlotToFd.TryGetValue(slot, out int fd))
            return 0;

        uint slotPhys = (tableBase + slot * 260u) & 0x01FFFFFFu;
        Span<byte> entry = new Span<byte>(ram + slotPhys, 260);
        if (BinaryPrimitives.ReadUInt32LittleEndian(entry[60..64]) == 0)
            return 0;

        ushort entrySpace = BinaryPrimitives.ReadUInt16LittleEndian(entry[30..32]);
        if ((reqFlags & 0x02) != 0)
        {
            entrySpace = (ushort)((entrySpace & ~0x003Fu) | (reqDestFlags & 0x003Fu));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[30..32], entrySpace);
        }

        uint offset = (reqFlags & 0x08) != 0
            ? reqOffset
            : BinaryPrimitives.ReadUInt32LittleEndian(entry[32..36]);

        Seek(fd, offset, 0);
        byte[]? targetSpu2 = spu2Ram ?? _spu2Ram;
        int bytesRead = (entrySpace & 0x3Fu) == 3 && targetSpu2 != null
            ? ReadToSpu2Ram(fd, targetSpu2, destBuf, byteCount)
            : ReadToRam(fd, ram, destBuf, byteCount);
        if (bytesRead < 0)
            bytesRead = 0;

        // Console.WriteLine($"[CFILESYS READ] slot={slot} fd={fd} path='{_openHandles[fd].Entry.VirtualPath}' offset={offset} dest=0x{destBuf:X8} count={byteCount} -> read={bytesRead}");

        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..28], slot);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[32..36], offset + (uint)bytesRead);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[52..56], destBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[56..60], (uint)byteCount);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[80..84], (uint)bytesRead);

        return bytesRead;
    }

    public int CloseEaFileSysSlot(byte* ram, uint cFileSysAddr, uint slot)
    {
        uint sysPhys = cFileSysAddr & 0x01FFFFFFu;
        uint tableBase = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 24, 4));
        uint maxSlots = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + sysPhys + 28, 4));
        if (slot > 0 && slot < maxSlots && tableBase != 0)
        {
            uint slotPhys = (tableBase + slot * 260u) & 0x01FFFFFFu;
            Span<byte> entry = new Span<byte>(ram + slotPhys, 260);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..8], 0xFFFFFFFFu);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[60..64], 0u);
        }

        if (_eaSlotToFd.Remove(slot, out int fd))
        {
            Close(fd);
        }

        return 1;
    }

    /// <summary>
    /// Thread-safe positional read from either an open EA FileSys slot (created by OpenEaFileSysSlot)
    /// or a raw open fd, without mutating FileStream.Position.
    /// </summary>
    public int ReadEaSlotAtOffset(uint slotOrFd, long offset, Span<byte> destination)
    {
        if (destination.IsEmpty || offset < 0)
            return 0;

        OpenFileHandle? handle = null;
        if (_eaSlotToFd.TryGetValue(slotOrFd, out int fd))
            _openHandles.TryGetValue(fd, out handle);
        else
            _openHandles.TryGetValue((int)slotOrFd, out handle);

        if (handle == null)
            return 0;

        if (offset >= handle.Entry.SizeBytes)
            return 0;

        int maxAvail = (int)Math.Min((long)destination.Length, handle.Entry.SizeBytes - offset);
        int totalRead = 0;
        while (totalRead < maxAvail)
        {
            int r = RandomAccess.Read(handle.Stream.SafeFileHandle, destination.Slice(totalRead, maxAvail - totalRead), offset + totalRead);
            if (r <= 0)
                break;
            totalRead += r;
        }
        return totalRead;
    }

    public void Dispose()
    {
        foreach (var h in _openHandles.Values)
            h.Dispose();
        _openHandles.Clear();
        _eaSlotToFd.Clear();
    }
}
