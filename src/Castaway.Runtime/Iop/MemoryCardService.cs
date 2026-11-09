using System.Buffers.Binary;
using System.Text;

namespace Castaway.Runtime;

public sealed unsafe class MemoryCardService : IDisposable
{
    public const int CardCapacityClusters = 8192; // ~8 MB standard PS2 memory card
    public const int EntryByteSize = 64;

    private readonly string _savesRootDir;
    private readonly string[] _currentPortDir = ["/", "/"];
    private readonly Dictionary<int, FileStream> _openHandles = new();
    private int _nextFd = 1;
    private readonly EeKernel? _kernel;

    public string SavesRootDir => _savesRootDir;

    public MemoryCardService(string? savesRootDir = null, EeKernel? kernel = null)
    {
        _savesRootDir = string.IsNullOrWhiteSpace(savesRootDir)
            ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Saves"))
            : Path.GetFullPath(savesRootDir);
        _kernel = kernel;

        Directory.CreateDirectory(GetSlotRootPath(0));
        Directory.CreateDirectory(GetSlotRootPath(1));
    }

    public string GetSlotRootPath(int port) =>
        Path.Combine(_savesRootDir, $"mc{Math.Clamp(port, 0, 1)}");

    private string ResolveHostPath(int port, string guestName)
    {
        string slotRoot = GetSlotRootPath(port);
        string curDir = _currentPortDir[Math.Clamp(port, 0, 1)].Trim('/');

        string rel = guestName.Replace('\\', '/').Trim();
        string combined;
        if (rel.StartsWith('/'))
            combined = rel.TrimStart('/');
        else if (string.IsNullOrEmpty(curDir))
            combined = rel;
        else
            combined = $"{curDir}/{rel}";

        // Collapse .. segments safely inside slotRoot
        string full = Path.GetFullPath(Path.Combine(slotRoot, combined));
        if (!full.StartsWith(slotRoot, StringComparison.OrdinalIgnoreCase))
            return slotRoot;

        return full;
    }

    public int HandleRpc(
        uint rpcNum,
        byte* ram,
        uint sendBufAddr,
        int sendSize,
        uint recvBufAddr,
        int recvSize,
        uint endParam = 0)
    {
        if (sendBufAddr == 0 || recvBufAddr == 0)
            return 0;

        uint sendPhys = sendBufAddr & 0x01FFFFFFu;
        uint recvPhys = recvBufAddr & 0x01FFFFFFu;
        Span<byte> send = new Span<byte>(ram + sendPhys, Math.Max(16, sendSize));
        Span<byte> recv = new Span<byte>(ram + recvPhys, Math.Max(4, recvSize));

        Console.WriteLine($"[MC RPC] rpc=0x{rpcNum:X2} sendSize={sendSize} recvSize={recvSize} endParam=0x{endParam:X8}");

        int result = rpcNum switch
        {
            0xFE => HandleInit(recv),
            0x01 or 0x08 or 0x78 => HandleGetInfo(ram, send, recv, endParam),
            0x02 or 0x72 => HandleOpen(send),
            0x03 or 0x73 => HandleClose(send),
            0x04 or 0x75 => HandleRead(ram, send, recv),
            0x05 or 0x76 => HandleWrite(ram, send),
            0x06 or 0x74 => HandleSeek(send),
            0x07 or 0x10 or 0x7C => HandleFormat(send),
            0x0A or 0x7A => HandleFlush(send),
            0x0C => HandleChdir(ram, send),
            0x0D or 0x7D => HandleGetDir(ram, send),
            0x0E or 0x7E => 0, // SetFileInfo
            0x0F or 0x79 => HandleDelete(send),
            0x11 or 0x12 or 0x80 => 0, // Unformat
            _ => 0
        };

        if (rpcNum != 0xFE)
        {
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], result);
        }

        // Failsafe: Ensure sceMc_sema_timer (0x004A4C18) is signaled so sceMcSync never hangs
        uint semaAddr = 0x004A4C18u & 0x01FFFFFFu;
        if (semaAddr + 4 <= 32 * 1024 * 1024)
        {
            int timerSemaId = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaAddr, 4));
            if (timerSemaId > 0 && _kernel != null)
            {
                _kernel.SignalSema(timerSemaId);
            }
        }

        return 0;
    }

    private static int HandleInit(Span<byte> recv)
    {
        if (recv.Length >= 12)
        {
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0);
            BinaryPrimitives.WriteInt32LittleEndian(recv[4..8], 0x0210); // mcserv version
            BinaryPrimitives.WriteInt32LittleEndian(recv[8..12], 0x0210); // mcman version
        }
        else if (recv.Length >= 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0);
        }
        Console.WriteLine("[MC INIT] version=0x0210");
        return 0;
    }

    private int HandleGetInfo(byte* ram, ReadOnlySpan<byte> send, Span<byte> recv, uint endParam)
    {
        int port = send.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(send[4..8]) : 0;
        int slot = send.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(send[8..12]) : 0;
        uint paramFromSend = send.Length >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(send[28..32]) : 0;
        uint targetEndParam = endParam != 0 ? endParam : (paramFromSend != 0 ? paramFromSend : 0x005AC0C0u);

        Directory.CreateDirectory(GetSlotRootPath(port));
        Console.WriteLine($"[MC GETINFO] port={port} slot={slot} targetEndParam=0x{targetEndParam:X8}");

        // 1. Write mcEndParam2 (unpacked by endFunc 0x00476E68)
        uint endPhys = targetEndParam & 0x01FFFFFFu;
        if (endPhys + 148 <= 32 * 1024 * 1024)
        {
            // +0 (0x00): int type = sceMcTypePS2 (2)
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + endPhys + 0, 4), 2);
            // +4 (0x04): int freeClusters = 8192
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + endPhys + 4, 4), CardCapacityClusters);
            // +144 (0x90): int formatted = 1
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + endPhys + 144, 4), 1);
        }

        // 2. Direct write to caller pointers stored at SLUS_216.64 globals:
        // 0x005ABBE8 = typePtr, 0x005ABBEC = freePtr, 0x005ABBF0 = formatPtr
        uint typePtr = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + (0x005ABBE8u & 0x01FFFFFFu), 4));
        uint freePtr = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + (0x005ABBECu & 0x01FFFFFFu), 4));
        uint formatPtr = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + (0x005ABBF0u & 0x01FFFFFFu), 4));

        if (typePtr != 0 && (typePtr & 0x01FFFFFFu) + 4 <= 32 * 1024 * 1024)
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + (typePtr & 0x01FFFFFFu), 4), 2);
        if (freePtr != 0 && (freePtr & 0x01FFFFFFu) + 4 <= 32 * 1024 * 1024)
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + (freePtr & 0x01FFFFFFu), 4), CardCapacityClusters);
        if (formatPtr != 0 && (formatPtr & 0x01FFFFFFu) + 4 <= 32 * 1024 * 1024)
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + (formatPtr & 0x01FFFFFFu), 4), 1);

        // Result: 0 = success
        BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0);
        return 0;
    }

    private int HandleOpen(ReadOnlySpan<byte> send)
    {
        // Layout:
        // +0: port
        // +4: slot
        // +8: flags
        // +20: name
        int port = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        int slot = send.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(send[4..8]) : 0;
        int flags = send.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(send[8..12]) : 0;
        string name = send.Length > 20 ? ReadFixedCString(send.Slice(20, Math.Min(1024, send.Length - 20))) : string.Empty;

        string hostPath = ResolveHostPath(port, name);
        Console.WriteLine($"[MC OPEN] port={port} slot={slot} flags=0x{flags:X4} name='{name}' -> host='{hostPath}'");

        const int SCE_CREAT = 0x0200;
        const int SCE_MKDIR = 0x4000;
        const int SCE_WRONLY = 0x0002;
        const int SCE_RDWR = 0x0003;

        if ((flags & SCE_MKDIR) != 0)
        {
            Directory.CreateDirectory(hostPath);
            return 0;
        }

        bool canCreate = (flags & SCE_CREAT) != 0;
        bool canWrite = (flags & (SCE_WRONLY | SCE_RDWR)) != 0;

        if (!File.Exists(hostPath) && !canCreate)
            return -4; // sceMcResNoEntry

        string? parentDir = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        FileMode mode = canCreate ? FileMode.OpenOrCreate : FileMode.Open;
        FileAccess access = canWrite ? FileAccess.ReadWrite : FileAccess.Read;

        var fs = new FileStream(hostPath, mode, access, FileShare.ReadWrite);
        int fd = _nextFd++;
        _openHandles[fd] = fs;
        return fd;
    }

    private int HandleClose(ReadOnlySpan<byte> send)
    {
        // Layout: +0: fd
        int handle = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        Console.WriteLine($"[MC CLOSE] handle={handle}");
        if (_openHandles.Remove(handle, out var fs))
        {
            fs.Flush();
            fs.Dispose();
            return 0;
        }
        return -5;
    }

    private int HandleRead(byte* ram, ReadOnlySpan<byte> send, Span<byte> recv)
    {
        // Layout:
        // +0: fd
        // +16: dataPtr
        // +20: size
        // +24: paramPtr (optional)
        int handle = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        uint dataPtr = send.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(send[16..20]) : 0;
        int size = send.Length >= 24 ? BinaryPrimitives.ReadInt32LittleEndian(send[20..24]) : 0;
        uint paramPtr = send.Length >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(send[24..28]) : 0;

        Console.WriteLine($"[MC READ] handle={handle} dataPtr=0x{dataPtr:X8} size={size}");

        if (!_openHandles.TryGetValue(handle, out var fs) || size < 0)
            return -5;

        uint phys = dataPtr & 0x01FFFFFFu;
        if (phys + (uint)size > 32 * 1024 * 1024)
            return -5;

        var dest = new Span<byte>(ram + phys, size);
        int totalRead = 0;
        while (totalRead < size)
        {
            int r = fs.Read(dest[totalRead..]);
            if (r <= 0) break;
            totalRead += r;
        }

        if (paramPtr != 0 && (paramPtr & 0x01FFFFFFu) + 64 <= 32 * 1024 * 1024)
        {
            new Span<byte>(ram + (paramPtr & 0x01FFFFFFu), 64).Clear();
        }

        return totalRead;
    }

    private int HandleWrite(byte* ram, ReadOnlySpan<byte> send)
    {
        // Layout:
        // +0: fd
        // +12: size
        // +24: dataPtr
        // +28: endParam
        int handle = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        int size = send.Length >= 16 ? BinaryPrimitives.ReadInt32LittleEndian(send[12..16]) : 0;
        uint dataPtr = send.Length >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(send[24..28]) : 0;

        Console.WriteLine($"[MC WRITE] handle={handle} dataPtr=0x{dataPtr:X8} size={size}");

        if (!_openHandles.TryGetValue(handle, out var fs) || size < 0)
            return -5;

        if (size > 0 && dataPtr != 0)
        {
            uint phys = dataPtr & 0x01FFFFFFu;
            if (phys + (uint)size <= 32 * 1024 * 1024)
            {
                fs.Write(new ReadOnlySpan<byte>(ram + phys, size));
            }
        }

        fs.Flush();
        return size;
    }

    private int HandleSeek(ReadOnlySpan<byte> send)
    {
        // Layout:
        // +0: fd
        // +12: offset
        // +20: whence
        int handle = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        int offset = send.Length >= 16 ? BinaryPrimitives.ReadInt32LittleEndian(send[12..16]) : 0;
        int origin = send.Length >= 24 ? BinaryPrimitives.ReadInt32LittleEndian(send[20..24]) : 0;

        Console.WriteLine($"[MC SEEK] handle={handle} offset={offset} origin={origin}");

        if (!_openHandles.TryGetValue(handle, out var fs))
            return -5;

        SeekOrigin seekOrigin = origin switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };

        return (int)fs.Seek(offset, seekOrigin);
    }

    private int HandleFlush(ReadOnlySpan<byte> send)
    {
        int handle = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        if (_openHandles.TryGetValue(handle, out var fs))
        {
            fs.Flush();
            return 0;
        }
        return -5;
    }

    private int HandleChdir(byte* ram, ReadOnlySpan<byte> send)
    {
        int port = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        uint pwdPtr = send.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(send[16..20]) : 0;
        string newDir = send.Length > 20 ? ReadFixedCString(send.Slice(20, Math.Min(1024, send.Length - 20))) : string.Empty;

        int clampedPort = Math.Clamp(port, 0, 1);
        if (pwdPtr != 0)
        {
            uint phys = pwdPtr & 0x01FFFFFFu;
            if (phys + 32 <= 32 * 1024 * 1024)
            {
                byte[] curBytes = Encoding.ASCII.GetBytes(_currentPortDir[clampedPort] + "\0");
                curBytes.CopyTo(new Span<byte>(ram + phys, curBytes.Length));
            }
        }

        string targetHost = ResolveHostPath(clampedPort, newDir);
        Console.WriteLine($"[MC CHDIR] port={port} newDir='{newDir}' -> target='{targetHost}'");
        if (!Directory.Exists(targetHost))
            return -4; // NoEntry

        string rel = Path.GetRelativePath(GetSlotRootPath(clampedPort), targetHost).Replace('\\', '/');
        _currentPortDir[clampedPort] = rel == "." ? "/" : "/" + rel.Trim('/');
        return 0;
    }

    private int HandleGetDir(byte* ram, ReadOnlySpan<byte> send)
    {
        // Layout:
        // +0: port
        // +4: slot
        // +8: flags
        // +12: maxEnt
        // +16: tablePtr
        // +20: pattern string
        int port = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        int slot = send.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(send[4..8]) : 0;
        int flags = send.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(send[8..12]) : 0;
        int maxEnt = send.Length >= 16 ? BinaryPrimitives.ReadInt32LittleEndian(send[12..16]) : 0;
        uint tablePtr = send.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(send[16..20]) : 0;
        string pattern = send.Length > 20 ? ReadFixedCString(send.Slice(20, Math.Min(1024, send.Length - 20))) : string.Empty;

        string searchHost = ResolveHostPath(port, pattern);
        string dirPath = Directory.Exists(searchHost)
            ? searchHost
            : (Path.GetDirectoryName(searchHost) ?? GetSlotRootPath(port));
        string searchPattern = Directory.Exists(searchHost)
            ? "*"
            : Path.GetFileName(searchHost);
        if (string.IsNullOrEmpty(searchPattern))
            searchPattern = "*";

        Console.WriteLine($"[MC GETDIR] port={port} slot={slot} maxEnt={maxEnt} tablePtr=0x{tablePtr:X8} pattern='{pattern}' -> dir='{dirPath}' filter='{searchPattern}'");

        if (!Directory.Exists(dirPath))
            return -4;

        var entries = new DirectoryInfo(dirPath).GetFileSystemInfos(searchPattern);
        int count = maxEnt <= 0 ? entries.Length : Math.Min(maxEnt, entries.Length);

        uint physBase = tablePtr & 0x01FFFFFFu;
        for (int i = 0; i < count; i++)
        {
            uint entryOff = physBase + (uint)(i * EntryByteSize);
            if (entryOff + EntryByteSize > 32 * 1024 * 1024)
                break;

            WriteMcTableEntry(new Span<byte>(ram + entryOff, EntryByteSize), entries[i]);
        }

        Console.WriteLine($"[MC GETDIR] returned {count} entries");
        return count;
    }

    private int HandleDelete(ReadOnlySpan<byte> send)
    {
        // Layout: +0: port, +4: slot, +20: name
        int port = send.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(send[0..4]) : 0;
        int slot = send.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(send[4..8]) : 0;
        string name = send.Length > 20 ? ReadFixedCString(send.Slice(20, Math.Min(1024, send.Length - 20))) : string.Empty;
        string hostPath = ResolveHostPath(port, name);
        Console.WriteLine($"[MC DELETE] port={port} slot={slot} name='{name}'");

        if (File.Exists(hostPath))
        {
            File.Delete(hostPath);
            return 0;
        }
        if (Directory.Exists(hostPath))
        {
            Directory.Delete(hostPath, recursive: true);
            return 0;
        }
        return -4;
    }

    private int HandleFormat(ReadOnlySpan<byte> send)
    {
        // Layout: +4: port, +8: slot
        int port = send.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(send[4..8]) : 0;
        int slot = send.Length >= 12 ? BinaryPrimitives.ReadInt32LittleEndian(send[8..12]) : 0;
        Console.WriteLine($"[MC FORMAT] port={port} slot={slot}");
        string root = GetSlotRootPath(port);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        return 0;
    }

    private static void WriteMcTableEntry(Span<byte> entry, FileSystemInfo info)
    {
        entry.Clear();
        bool isDir = info is DirectoryInfo;
        uint size = info is FileInfo fi ? (uint)fi.Length : 0u;
        ushort attr = isDir ? (ushort)0x8427 : (ushort)0x8417;

        // +16 (0x10): uint32 FileSizeByte
        BinaryPrimitives.WriteUInt32LittleEndian(entry[0x10..0x14], size);
        // +20 (0x14): ushort AttrFile
        BinaryPrimitives.WriteUInt16LittleEndian(entry[0x14..0x16], attr);

        // +32..63 (0x20..0x3F): char EntryName[32]
        byte[] nameBytes = Encoding.ASCII.GetBytes(info.Name);
        nameBytes.AsSpan(0, Math.Min(31, nameBytes.Length)).CopyTo(entry[0x20..0x40]);
    }

    private static string ReadFixedCString(ReadOnlySpan<byte> span)
    {
        int len = span.IndexOf((byte)0);
        if (len < 0) len = span.Length;
        return len == 0 ? string.Empty : Encoding.ASCII.GetString(span[..len]);
    }

    public void Dispose()
    {
        foreach (var fs in _openHandles.Values)
            fs.Dispose();
        _openHandles.Clear();
    }
}
