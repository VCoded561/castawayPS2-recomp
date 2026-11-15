using System.Buffers.Binary;
using System.Text;
using Castaway.Core;

namespace Castaway.Runtime;

public sealed unsafe class SifRpcRouter
{
    public const uint SidFileIo      = 0x80000001u;
    public const uint SidIopHeap     = 0x80000003u;
    public const uint SidLoadFile    = 0x80000006u;
    public const uint SidPad1        = 0x80000100u;
    public const uint SidPad2        = 0x80000101u;
    public const uint SidUsbKeybd    = 0x80000211u;
    public const uint SidMcServ      = 0x80000400u;
    public const uint SidCdInit      = 0x80000592u;
    public const uint SidCdSCmd      = 0x80000593u;
    public const uint SidCdNCmd      = 0x80000595u;
    public const uint SidCdReady     = 0x80000596u;
    public const uint SidCdSearch    = 0x80000597u;
    public const uint SidCdDiskReady = 0x8000059Au;
    public const uint SidCdMode      = 0x8000059Cu;
    public const uint SidLibSd       = 0x80000701u;
    public const uint SidEaStream    = 0x00012345u;

    private readonly EeKernel _kernel;
    private readonly DvdVirtualFileSystem _dvd;
    private readonly PadService _pad;
    private readonly MemoryCardService _mc;
    private readonly Spu2AudioService _audio;

    private readonly Dictionary<uint, uint> _boundClients = new();
    private uint _nextIopHeapAddr = 0x00100000u;
    private int _nextModuleId = 10;
    private int _nextDmaId = 1;

    public IReadOnlyDictionary<uint, uint> BoundClients => _boundClients;

    public SifRpcRouter(
        EeKernel kernel,
        DvdVirtualFileSystem dvd,
        PadService pad,
        MemoryCardService mc,
        Spu2AudioService audio)
    {
        _kernel = kernel;
        _dvd = dvd;
        _pad = pad;
        _mc = mc;
        _audio = audio;
    }

    public int BindRpc(byte* ram, uint clientPtr, uint sid, uint mode)
    {
        uint phys = clientPtr & 0x01FFFFFFu;
        if (clientPtr == 0 || phys + 40 > CpuContext.RamSize)
            return -1;

        _boundClients[phys] = sid;

        // sceSifClientData layout:
        // +0x00..0x17: sceSifRpcData rpcd (24 bytes)
        // +0x18 (24): uint command
        // +0x1C (28): uint buff (receive buffer ptr)
        // +0x20 (32): uint cbuff
        // +0x24 (36): uint server (checked for != 0 by callers!)
        Span<byte> clientSpan = new Span<byte>(ram + phys, 40);
        // Write non-zero server handle (use sid | 0x80000000 or sid itself)
        uint serverHandle = sid != 0 ? sid : 0xDEAD0001u;
        BinaryPrimitives.WriteUInt32LittleEndian(clientSpan[0x1C..0x20], clientPtr + 0x40u);
        BinaryPrimitives.WriteUInt32LittleEndian(clientSpan[0x24..0x28], serverHandle);

        return 0;
    }

    public int CallRpc(
        CpuContext* ctx,
        byte* ram,
        uint clientPtr,
        uint rpcNum,
        uint mode,
        uint sendBuf,
        int sendSize,
        uint recvBuf,
        int recvSize,
        uint endFunc,
        uint endParam)
    {
        uint clientPhys = clientPtr & 0x01FFFFFFu;
        if (!_boundClients.TryGetValue(clientPhys, out uint sid))
        {
            if (clientPtr != 0 && clientPhys + 40 <= CpuContext.RamSize)
            {
                sid = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + clientPhys + 0x24, 4));
            }
        }

        string sName = GetServerName(sid);
        Console.WriteLine($"[SIF RPC] {sName} (SID=0x{sid:X8}) rpc=0x{rpcNum:X2} mode={mode} client=0x{clientPtr:X8} sendBuf=0x{sendBuf:X8}({sendSize}) recvBuf=0x{recvBuf:X8}({recvSize}) endFunc=0x{endFunc:X8} endParam=0x{endParam:X8}");

        int status = DispatchRpcBySid(sid, rpcNum, ram, sendBuf, sendSize, recvBuf, recvSize, endParam);

        // If caller supplied an async endFunc callback (e.g. EA Stream IRX 0x0046A448 or FileIO/McServ), invoke it
        if (endFunc != 0 && ctx != null)
        {
            EeKernel.InvokeGuestCallback(ctx, ram, endFunc, endParam, 0, ctx->GetU32(28));
        }

        return status;
    }

    public static string GetServerName(uint sid) => sid switch
    {
        SidFileIo => "FILEIO",
        SidIopHeap => "IOPHEAP",
        SidLoadFile => "LOADFILE",
        SidPad1 => "PAD1",
        SidPad2 => "PAD2",
        SidUsbKeybd => "USBKEYBD",
        SidMcServ => "MCSERV",
        SidCdInit or SidCdSCmd or SidCdNCmd or SidCdReady or SidCdSearch or SidCdDiskReady or SidCdMode => "CDVD",
        SidLibSd => "LIBSD",
        SidEaStream => "EASTREAM",
        _ => "UNKNOWN"
    };

    public int DispatchRpcBySid(
        uint sid,
        uint rpcNum,
        byte* ram,
        uint sendBufAddr,
        int sendSize,
        uint recvBufAddr,
        int recvSize,
        uint endParam = 0)
    {
        switch (sid)
        {
            case SidFileIo:
                return HandleFileIoRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidIopHeap:
                return HandleIopHeapRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidLoadFile:
                return HandleLoadFileRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidPad1:
            case SidPad2:
                return _pad.HandleRpc(sid, rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidUsbKeybd:
                return HandleUsbKeybdRpc(rpcNum, ram, recvBufAddr, recvSize);

            case SidMcServ:
                return _mc.HandleRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize, endParam);

            case SidCdInit:
            case SidCdSCmd:
            case SidCdNCmd:
            case SidCdReady:
            case SidCdSearch:
            case SidCdDiskReady:
            case SidCdMode:
                return HandleCdvdRpc(sid, rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidLibSd:
                return _audio.HandleLibSdRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            case SidEaStream:
                return _audio.HandleEaStreamRpc(rpcNum, ram, sendBufAddr, sendSize, recvBufAddr, recvSize);

            default:
                if (recvBufAddr != 0 && recvSize >= 4)
                {
                    uint recvPhys = recvBufAddr & 0x01FFFFFFu;
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + recvPhys, 4), 0);
                }
                return 0;
        }
    }

    private int HandleFileIoRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        Span<byte> send = sendBufAddr != 0
            ? new Span<byte>(ram + (sendBufAddr & 0x01FFFFFFu), Math.Max(16, sendSize))
            : Span<byte>.Empty;
        Span<byte> recv = recvBufAddr != 0
            ? new Span<byte>(ram + (recvBufAddr & 0x01FFFFFFu), Math.Max(4, recvSize))
            : Span<byte>.Empty;

        int res = 0;
        switch (rpcNum)
        {
            case 0: // Open: +0=int flags, +4=int mode, +8=char path[]
                if (send.Length >= 12)
                {
                    int flags = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    string path = ReadCString(send[8..]);
                    res = _dvd.Open(path, flags);
                    Console.WriteLine($"[FILEIO OPEN] path='{path}' flags={flags} -> fd={res}");
                }
                break;

            case 1: // Close: +0=int fd
                if (send.Length >= 4)
                {
                    int fd = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    res = _dvd.Close(fd);
                    Console.WriteLine($"[FILEIO CLOSE] fd={fd} -> res={res}");
                }
                break;

            case 2: // Read: +0=int fd, +4=uint bufPtr, +8=int size, +12=uintintrDataPtr
                if (send.Length >= 12)
                {
                    int fd = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    uint bufPtr = BinaryPrimitives.ReadUInt32LittleEndian(send[4..8]);
                    int size = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                    uint intrPtr = send.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(send[12..16]) : 0;
                    if (intrPtr != 0 && (intrPtr & 0x01FFFFFFu) + 32 <= CpuContext.RamSize)
                    {
                        new Span<byte>(ram + (intrPtr & 0x01FFFFFFu), 32).Clear();
                    }
                    res = _dvd.ReadToRam(fd, ram, bufPtr, size);
                    if (fd != 0 || size > 64)
                    {
                        Console.WriteLine($"[FILEIO READ] fd={fd} buf=0x{bufPtr:X8} size={size} -> read={res}");
                    }
                }
                break;

            case 3: // Write
                if (send.Length >= 12)
                {
                    res = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                }
                break;

            case 4: // Lseek: +0=int fd, +4=int offset, +8=int whence
                if (send.Length >= 12)
                {
                    int fd = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    int offset = BinaryPrimitives.ReadInt32LittleEndian(send[4..8]);
                    int whence = BinaryPrimitives.ReadInt32LittleEndian(send[8..12]);
                    res = (int)_dvd.Seek(fd, offset, whence);
                    Console.WriteLine($"[FILEIO LSEEK] fd={fd} offset={offset} whence={whence} -> res={res}");
                }
                break;

            default:
                res = 0;
                break;
        }

        if (recv.Length >= 4)
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], res);

        return 0;
    }

    private int HandleIopHeapRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        Span<byte> send = sendBufAddr != 0
            ? new Span<byte>(ram + (sendBufAddr & 0x01FFFFFFu), Math.Max(4, sendSize))
            : Span<byte>.Empty;
        Span<byte> recv = recvBufAddr != 0
            ? new Span<byte>(ram + (recvBufAddr & 0x01FFFFFFu), Math.Max(4, recvSize))
            : Span<byte>.Empty;

        uint ret = 0;
        if (rpcNum == 1 && send.Length >= 4) // AllocIopHeap
        {
            uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(send[0..4]);
            uint aligned = (bytes + 63u) & ~63u;
            if (_nextIopHeapAddr + aligned <= Spu2AudioService.IopRamSize)
            {
                ret = _nextIopHeapAddr;
                _nextIopHeapAddr += aligned;
            }
            else
            {
                // Wrap into high temporary scratch area if exhausted cleanly; log if tracing.
                ret = _nextIopHeapAddr < Spu2AudioService.IopRamSize ? _nextIopHeapAddr : 0u;
            }

            if (_audio.EnableTraceLogging)
            {
                Console.WriteLine($"[IOP_HEAP] Alloc({bytes} bytes, aligned={aligned}) -> 0x{ret:X8} (next=0x{_nextIopHeapAddr:X8})");
            }
        }
        else if (rpcNum == 2 && _audio.EnableTraceLogging && send.Length >= 4)
        {
            uint freeAddr = BinaryPrimitives.ReadUInt32LittleEndian(send[0..4]);
            Console.WriteLine($"[IOP_HEAP] Free(0x{freeAddr:X8})");
        }

        if (recv.Length >= 4)
            BinaryPrimitives.WriteUInt32LittleEndian(recv[0..4], ret);

        return 0;
    }

    private int HandleLoadFileRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        Span<byte> recv = recvBufAddr != 0
            ? new Span<byte>(ram + (recvBufAddr & 0x01FFFFFFu), Math.Max(8, recvSize))
            : Span<byte>.Empty;

        if (rpcNum == 255) // GetVersion (checked at 0x00488AF4 against "3000")
        {
            if (recv.Length >= 4)
                "3000"u8.CopyTo(recv[0..4]);
        }
        else if (recv.Length >= 4)
        {
            int modId = _nextModuleId++;
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], modId);
            if (recv.Length >= 8)
                BinaryPrimitives.WriteInt32LittleEndian(recv[4..8], 0); // modres = 0
        }

        return 0;
    }

    private static int HandleUsbKeybdRpc(uint rpcNum, byte* ram, uint recvBufAddr, int recvSize)
    {
        if (recvBufAddr != 0 && recvSize >= 4)
        {
            uint recvPhys = recvBufAddr & 0x01FFFFFFu;
            new Span<byte>(ram + recvPhys, Math.Min(recvSize, 64)).Clear();
            BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + recvPhys, 4), 1);
        }
        return 0;
    }

    private int HandleCdvdRpc(uint sid, uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        Span<byte> send = sendBufAddr != 0
            ? new Span<byte>(ram + (sendBufAddr & 0x01FFFFFFu), Math.Max(4, sendSize))
            : Span<byte>.Empty;
        Span<byte> recv = recvBufAddr != 0
            ? new Span<byte>(ram + (recvBufAddr & 0x01FFFFFFu), Math.Max(4, recvSize))
            : Span<byte>.Empty;

        switch (sid)
        {
            case SidCdInit: // 0x80000592
                if (recv.Length >= 4)
                    BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 1);
                break;

            case SidCdDiskReady: // 0x8000059A -> 2 = Complete
            case SidCdReady:     // 0x80000596 -> 2 = Complete
                if (recv.Length >= 4)
                    BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 2);
                break;

            case SidCdMode: // 0x8000059C -> 0x12 (PS2DVD)
                if (recv.Length >= 4)
                    BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0x12);
                break;

            case SidCdSearch: // 0x80000597 (sceCdSearchFile at 0x0035C7C8)
            {
                if (sendBufAddr != 0 && send.Length >= 0x28)
                {
                    int pathOffset = (sendSize == 0x124) ? 0x20 : 0x24;
                    string path = ReadCString(send[pathOffset..]);
                    bool found = _dvd.PopulateCdlFileStruct(ram, sendBufAddr, path);
                    Console.WriteLine($"[CDVD SEARCH] path='{path}' found={found}");
                    if (recv.Length >= 4)
                        BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], found ? 1 : 0);
                }
                break;
            }

            case SidCdNCmd: // 0x80000595 (N-Command: Read=1, Standby=2, Stop=3, Pause=4, Seek=5)
            {
                if ((rpcNum == 1 || rpcNum == 15) && send.Length >= 12)
                {
                    uint lba = BinaryPrimitives.ReadUInt32LittleEndian(send[0..4]);
                    uint sectors = BinaryPrimitives.ReadUInt32LittleEndian(send[4..8]);
                    uint dstBuf = BinaryPrimitives.ReadUInt32LittleEndian(send[8..12]);
                    bool ok = _dvd.ReadSectorsToRam(lba, sectors, ram, dstBuf);
                    Console.WriteLine($"[CDVD READ_SECTORS] lba={lba} sectors={sectors} dst=0x{dstBuf:X8} ok={ok}");
                }
                if (recv.Length >= 4)
                    BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 1);
                break;
            }

            case SidCdSCmd: // 0x80000593 (S-Command: ReadClock=1, Status=5, etc.)
            {
                if (rpcNum == 1 && recv.Length >= 8) // sceCdReadClock
                {
                    recv[0] = 0x00; // stat = 0 (OK)
                    recv[1] = 0x00; // second
                    recv[2] = 0x30; // minute
                    recv[3] = 0x12; // hour
                    recv[4] = 0x00; // pad
                    recv[5] = 0x15; // day
                    recv[6] = 0x09; // month
                    recv[7] = 0x07; // year (2007)
                }
                else if (recv.Length >= 4)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 1);
                }
                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// Handles low-level sceSifSetDma (Syscall 0x77) transfers so even unhooked SIF packet paths work.
    /// </summary>
    public int HandleSifSetDma(CpuContext* ctx, byte* ram, uint sddAddr, uint count)
    {
        uint phys = sddAddr & 0x01FFFFFFu;
        for (uint i = 0; i < count; i++)
        {
            uint entryOff = phys + i * 16;
            if (entryOff + 16 > CpuContext.RamSize)
                break;

            ReadOnlySpan<byte> entry = new ReadOnlySpan<byte>(ram + entryOff, 16);
            uint src = BinaryPrimitives.ReadUInt32LittleEndian(entry[0..4]);
            uint dst = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(entry[8..12]);
            uint attr = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);

            if (src == 0 || size <= 0)
                continue;

            uint srcPhys = src & 0x01FFFFFFu;
            if (srcPhys + (uint)size > CpuContext.RamSize)
                continue;

            bool isSifCmdAttr = (attr & 0x44u) != 0 || dst == 0;
            if (isSifCmdAttr && size >= 16)
            {
                ReadOnlySpan<byte> pkt = new ReadOnlySpan<byte>(ram + srcPhys, size);
                uint cid = BinaryPrimitives.ReadUInt32LittleEndian(pkt[8..12]);

                if (cid == 0x80000009u && size >= 40) // SIF_CMDC_BIND
                {
                    uint clientPtr = BinaryPrimitives.ReadUInt32LittleEndian(pkt[28..32]);
                    uint sid = BinaryPrimitives.ReadUInt32LittleEndian(pkt[32..36]);
                    BindRpc(ram, clientPtr, sid, 0);
                    continue;
                }
                else if (cid == 0x8000000Au && size >= 48) // SIF_CMDC_CALL
                {
                    int sendSize = BinaryPrimitives.ReadInt32LittleEndian(pkt[16..20]);
                    uint recvBuf = BinaryPrimitives.ReadUInt32LittleEndian(pkt[20..24]);
                    int recvSize = BinaryPrimitives.ReadInt32LittleEndian(pkt[24..28]);
                    uint clientPtr = BinaryPrimitives.ReadUInt32LittleEndian(pkt[32..36]);
                    uint rpcNum = BinaryPrimitives.ReadUInt32LittleEndian(pkt[36..40]);
                    uint sendBuf = i > 0
                        ? BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + phys + (i - 1) * 16, 4))
                        : 0u;
                    CallRpc(ctx, ram, clientPtr, rpcNum, 0, sendBuf, sendSize, recvBuf, recvSize, 0, 0);
                    continue;
                }
            }

            // Skip sendBuf entry paired with an immediately following SIF_CMDC_CALL packet
            bool pairedWithRpcCall = false;
            if (i + 1 < count && entryOff + 32 <= CpuContext.RamSize)
            {
                ReadOnlySpan<byte> nextEntry = new ReadOnlySpan<byte>(ram + entryOff + 16, 16);
                uint nextSrc = BinaryPrimitives.ReadUInt32LittleEndian(nextEntry[0..4]);
                int nextSize = BinaryPrimitives.ReadInt32LittleEndian(nextEntry[8..12]);
                uint nextAttr = BinaryPrimitives.ReadUInt32LittleEndian(nextEntry[12..16]);
                uint nextPhys = nextSrc & 0x01FFFFFFu;
                if ((nextAttr & 0x44u) != 0 && nextSize >= 48 && nextPhys + 12 <= CpuContext.RamSize)
                {
                    uint nextCid = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + nextPhys + 8, 4));
                    if (nextCid == 0x8000000Au)
                        pairedWithRpcCall = true;
                }
            }

            if (!pairedWithRpcCall && dst != 0)
            {
                _audio.WriteIopRam(dst, new ReadOnlySpan<byte>(ram + srcPhys, size));
            }
        }

        return _nextDmaId++;
    }

    private static string ReadCString(ReadOnlySpan<byte> span)
    {
        int idx = span.IndexOf((byte)0);
        if (idx < 0) idx = span.Length;
        return idx == 0 ? string.Empty : Encoding.ASCII.GetString(span[..idx]);
    }
}
