using System.Buffers.Binary;
using Castaway.Core;

namespace Castaway.Runtime;

public enum Ps2ThreadStatus : int
{
    Run         = 0x01,
    Ready       = 0x02,
    Wait        = 0x04,
    Suspend     = 0x08,
    WaitSuspend = 0x0C,
    Dormant     = 0x10
}

public sealed class EeThreadState
{
    public int Id { get; init; }
    public Ps2ThreadStatus Status { get; set; } = Ps2ThreadStatus.Dormant;
    public uint EntryPc { get; set; }
    public uint StackBase { get; set; }
    public int StackSize { get; set; }
    public uint GpReg { get; set; }
    public int InitPriority { get; set; }
    public int CurrentPriority { get; set; }
    public uint Attr { get; set; }
    public uint Option { get; set; }
    public int WaitType { get; set; } // 0=none, 1=Sleep, 2=Sema
    public int WaitSemaId { get; set; } = -1;
    public int WakeupCount { get; set; }
    public volatile bool WakingUpFromWait;
    public CpuContext SavedContext;
    public readonly AutoResetEvent SleepEvent = new(false);
    public Thread? HostThread { get; set; }
}

public sealed class EeSemaState
{
    public int Id { get; init; }
    public int Count { get; set; }
    public int MaxCount { get; set; }
    public int InitCount { get; set; }
    public uint Attr { get; set; }
    public uint Option { get; set; }
    public Queue<int> WaitingThreadIds { get; } = new();
    public readonly AutoResetEvent Event = new(false);
}

public sealed class EeEventFlagState
{
    public int Id { get; init; }
    public uint Attr { get; set; }
    public uint Option { get; set; }
    public uint Bits { get; set; }
}

public sealed class EeIntcHandlerEntry
{
    public int Id { get; init; }
    public int Cause { get; init; }
    public uint HandlerPc { get; init; }
    public uint Arg { get; init; }
    public uint Gp { get; init; }
    public bool Enabled { get; set; } = true;
}

public sealed class EeDmacHandlerEntry
{
    public int Id { get; init; }
    public int Channel { get; init; }
    public uint HandlerPc { get; init; }
    public uint Arg { get; init; }
    public uint Gp { get; init; }
    public bool Enabled { get; set; } = true;
}

public sealed class EeAlarmEntry
{
    public int Id { get; init; }
    public ushort Ticks { get; init; }
    public uint HandlerPc { get; init; }
    public uint CommonArg { get; init; }
    public uint Gp { get; init; }
}

public sealed unsafe class EeKernel
{
    public const int MaxThreads = 256;
    public const int MaxSemaphores = 512;
    public const int MaxEventFlags = 256;

    // Exit sentinel address placed in $ra when invoking guest callbacks or threads
    public const uint ThreadExitSentinelPc = 0x001000C8u;

    private readonly Dictionary<int, EeThreadState> _threads = new();
    private readonly Dictionary<int, EeSemaState> _semas = new();
    private readonly Dictionary<int, EeEventFlagState> _eventFlags = new();
    private readonly List<EeIntcHandlerEntry> _intcHandlers = new();
    private readonly List<EeDmacHandlerEntry> _dmacHandlers = new();
    private readonly List<EeAlarmEntry> _alarms = new();
    private readonly Dictionary<int, uint> _customSyscallTable = new();

    private readonly object _syncLock = new();
    private readonly object _allocatorLock = new();
    private readonly object _eaSemaLock = new();
    [ThreadStatic] private static int s_currentThreadId;
    private volatile bool _isShuttingDown;

    public object EaSemaLock => _eaSemaLock;

    public void EnterAllocatorSection()
    {
        Monitor.Enter(_allocatorLock);
    }

    public void ExitAllocatorSection()
    {
        if (Monitor.IsEntered(_allocatorLock))
        {
            Monitor.Exit(_allocatorLock);
        }
    }

    public void ReleaseAllocatorIfHeld()
    {
        while (Monitor.IsEntered(_allocatorLock))
        {
            Monitor.Exit(_allocatorLock);
        }
    }

    private int _nextThreadId = 2;
    private int _nextSemaId = 1;
    private int _nextEventFlagId = 1;
    private int _nextHandlerId = 1;
    private int _nextAlarmId = 1;

    private uint _heapBase = 0x005B96D8u;
    private uint _heapEnd = 0x01FFE000u;
    private uint _osdConfigParam = 0;
    private ulong _gsImr = 0x0000FF00u;
    private uint _vsyncFlagPtr1;
    private uint _vsyncFlagPtr2;

    public uint IntcStat { get; set; }
    public uint IntcMask { get; set; }
    public uint DmacStat { get; set; } // Lower 16 = STAT, Upper 16 = MASK

    public int CurrentThreadId
    {
        get => s_currentThreadId != 0 ? s_currentThreadId : 1;
        set => s_currentThreadId = value;
    }
    public uint HeapBase => _heapBase;
    public uint HeapEnd => _heapEnd;
    public IReadOnlyDictionary<int, EeThreadState> Threads => _threads;
    public IReadOnlyDictionary<int, EeSemaState> Semaphores => _semas;
    public IReadOnlyList<EeIntcHandlerEntry> IntcHandlers => _intcHandlers;
    public IReadOnlyList<EeDmacHandlerEntry> DmacHandlers => _dmacHandlers;

    public unsafe delegate int SifSetDmaHandler(CpuContext* ctx, byte* ram, uint sddAddr, uint count);

    public SifSetDmaHandler? OnSifSetDma { get; set; }
    public Func<uint, uint>? OnSifGetReg { get; set; }
    public Action<uint, uint>? OnSifSetReg { get; set; }

    public EeKernel()
    {
        // Create main thread (ID = 1)
        _threads[1] = new EeThreadState
        {
            Id = 1,
            Status = Ps2ThreadStatus.Run,
            EntryPc = 0x00100008u,
            StackBase = 0x01FFE000u,
            StackSize = 0x00002000,
            GpReg = 0x00512670u,
            InitPriority = 0,
            CurrentPriority = 0
        };
    }

    /// <summary>
    /// Seeds BIOS structures in low EE RAM so SLUS_216.64's InitTLBFunctions (0x00489650)
    /// finds the expected kernel magic offsets even if called directly.
    /// </summary>
    public static void SeedBiosMemory(byte* ram)
    {
        // 0x00489650 searches for 0x004895F8 at base+524 and 0x004895C0 at base+360
        const uint biosBase = 0x00001000u;
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + biosBase + 524, 4), 0x004895F8u);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + biosBase + 360, 4), 0x004895C0u);
    }

    public void DispatchSyscall(CpuContext* ctx, byte* ram, uint syscallId)
    {
        // Check if syscallId is 0 and read from $v1
        if (syscallId == 0)
            syscallId = ctx->GetU32(3);

        // Normalize negative syscall IDs (e.g. 0xFFFFFFBD -> -0x43 -> 0x43)
        int signedId = (int)syscallId;
        uint normId = signedId < 0 ? (uint)(-signedId) : syscallId;

        switch (normId)
        {
            case 0x00: // RFU000_FullReset
            case 0x01: // ResetEE
                ctx->SetS32(2, 0);
                break;

            case 0x02: // SetGsCrt(short interlace, short pal_ntsc, short field)
                ctx->SetS32(2, 0);
                break;

            case 0x04: // Exit
                ExitCurrentThread(ctx, delete: false);
                break;

            case 0x10: // AddIntcHandler(int cause, void* handler, int next, void* arg)
                ctx->SetS32(2, AddIntcHandler(
                    (int)ctx->GetU32(4),
                    ctx->GetU32(5),
                    (int)ctx->GetU32(6),
                    ctx->GetU32(7),
                    ctx->GetU32(28)));
                break;

            case 0x11: // RemoveIntcHandler(int cause, int handlerId)
                ctx->SetS32(2, RemoveIntcHandler((int)ctx->GetU32(4), (int)ctx->GetU32(5)));
                break;

            case 0x12: // AddDmacHandler(int channel, void* handler, int next, void* arg)
                ctx->SetS32(2, AddDmacHandler(
                    (int)ctx->GetU32(4),
                    ctx->GetU32(5),
                    (int)ctx->GetU32(6),
                    ctx->GetU32(7),
                    ctx->GetU32(28)));
                break;

            case 0x13: // RemoveDmacHandler(int channel, int handlerId)
                ctx->SetS32(2, RemoveDmacHandler((int)ctx->GetU32(4), (int)ctx->GetU32(5)));
                break;

            case 0x14: // EnableIntc
            case 0x1A: // _iEnableIntc
            {
                int cause = (int)ctx->GetU32(4);
                uint bit = 1u << (cause & 31);
                bool wasSet = (IntcMask & bit) != 0;
                IntcMask |= bit;
                ctx->SetS32(2, wasSet ? 1 : 0);
                break;
            }

            case 0x15: // DisableIntc
            case 0x1B: // _iDisableIntc
            {
                int cause = (int)ctx->GetU32(4);
                uint bit = 1u << (cause & 31);
                bool wasSet = (IntcMask & bit) != 0;
                IntcMask &= ~bit;
                ctx->SetS32(2, wasSet ? 1 : 0);
                break;
            }

            case 0x16: // EnableDmac
            case 0x1C: // _iEnableDmac
            {
                int ch = (int)ctx->GetU32(4);
                uint maskBit = 1u << ((ch & 15) + 16);
                bool wasSet = (DmacStat & maskBit) != 0;
                DmacStat |= maskBit;
                ctx->SetS32(2, wasSet ? 1 : 0);
                break;
            }

            case 0x17: // DisableDmac
            case 0x1D: // _iDisableDmac
            {
                int ch = (int)ctx->GetU32(4);
                uint maskBit = 1u << ((ch & 15) + 16);
                bool wasSet = (DmacStat & maskBit) != 0;
                DmacStat &= ~maskBit;
                ctx->SetS32(2, wasSet ? 1 : 0);
                break;
            }

            case 0x18: // SetAlarm
            case 0x1E: // _iSetAlarm
            case 0xFC:
            case 0xFE:
            {
                ushort ticks = (ushort)ctx->GetU32(4);
                uint handler = ctx->GetU32(5);
                uint common = ctx->GetU32(6);
                int id = _nextAlarmId++;
                _alarms.Add(new EeAlarmEntry
                {
                    Id = id,
                    Ticks = ticks,
                    HandlerPc = handler,
                    CommonArg = common,
                    Gp = ctx->GetU32(28)
                });
                ctx->SetS32(2, id);
                break;
            }

            case 0x19: // ReleaseAlarm
            case 0x1F: // _iReleaseAlarm
            case 0xFD:
            case 0xFF:
            {
                int id = (int)ctx->GetU32(4);
                int removed = _alarms.RemoveAll(a => a.Id == id);
                ctx->SetS32(2, removed > 0 ? 0 : -1);
                break;
            }

            case 0x20: // CreateThread(ThreadParam* param)
                ctx->SetS32(2, CreateThread(ctx, ram, ctx->GetU32(4)));
                break;

            case 0x21: // DeleteThread(int tid)
                ctx->SetS32(2, DeleteThread((int)ctx->GetU32(4)));
                break;

            case 0x22: // StartThread(int tid, void* arg)
                ctx->SetS32(2, StartThread(ctx, ram, (int)ctx->GetU32(4), ctx->GetU32(5)));
                break;

            case 0x23: // ExitThread()
            case 0x24: // ExitDeleteThread()
                ExitCurrentThread(ctx, delete: normId == 0x24);
                break;

            case 0x25: // TerminateThread(int tid)
            case 0x26: // iTerminateThread(int tid)
                ctx->SetS32(2, TerminateThread((int)ctx->GetU32(4)));
                break;

            case 0x29: // ChangeThreadPriority(int tid, int prio)
            case 0x2A: // iChangeThreadPriority
                ctx->SetS32(2, ChangeThreadPriority((int)ctx->GetU32(4), (int)ctx->GetU32(5)));
                break;

            case 0x2B: // RotateThreadReadyQueue(int prio)
            case 0x2C: // iRotateThreadReadyQueue
                if (normId == 0x2B && !_inTriggerVBlank)
                {
                    Thread.Yield();
                }
                ctx->SetS32(2, (int)ctx->GetU32(4));
                break;

            case 0x2D: // ReleaseWaitThread(int tid)
            case 0x2E: // iReleaseWaitThread
                ctx->SetS32(2, ReleaseWaitThread((int)ctx->GetU32(4)));
                break;

            case 0x2F: // GetThreadId()
                if (CurrentThreadId == 1 && !_inTriggerVBlank && ram != null && _intcHandlers.Count > 0)
                {
                    for (int vb = 0; vb < 4 &&
                         BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + 0x00596E00u, 4)) != 0 &&
                         BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + 0x00596DFCu, 4)) >= 2; vb++)
                    {
                        TriggerVBlank(ctx, ram);
                    }
                }
                ctx->SetS32(2, CurrentThreadId);
                break;

            case 0x30: // ReferThreadStatus(int tid, ThreadParam* info)
            case 0x31: // iReferThreadStatus
                ctx->SetS32(2, ReferThreadStatus(ram, (int)ctx->GetU32(4), ctx->GetU32(5)));
                break;

            case 0x32: // SleepThread()
                ctx->SetS32(2, SleepThread(ctx));
                break;

            case 0x33: // WakeupThread(int tid)
            case 0x34: // iWakeupThread
                ctx->SetS32(2, WakeupThread((int)ctx->GetU32(4)));
                break;

            case 0x35: // CancelWakeupThread(int tid)
            case 0x36: // iCancelWakeupThread
                ctx->SetS32(2, CancelWakeupThread((int)ctx->GetU32(4)));
                break;

            case 0x37: // SuspendThread(int tid)
            case 0x38: // iSuspendThread
                ctx->SetS32(2, SuspendThread((int)ctx->GetU32(4)));
                break;

            case 0x39: // ResumeThread(int tid)
            case 0x3A: // iResumeThread
                ctx->SetS32(2, ResumeThread((int)ctx->GetU32(4)));
                break;

            case 0x3C: // InitMainThread(gp, stack, stack_size, args, root)
                ctx->SetU32(2, InitMainThread(ctx, ram,
                    ctx->GetU32(4),
                    ctx->GetU32(5),
                    (int)ctx->GetU32(6),
                    ctx->GetU32(7),
                    ctx->GetU32(8)));
                break;

            case 0x3D: // InitializeHeap(heap, heap_size)
                ctx->SetU32(2, InitializeHeap(ctx->GetU32(4), (int)ctx->GetU32(5)));
                break;

            case 0x3E: // EndOfHeap()
                ctx->SetU32(2, _heapEnd);
                break;

            case 0x40: // CreateSema(SemaParam* param)
                ctx->SetS32(2, CreateSema(ram, ctx->GetU32(4)));
                break;

            case 0x41: // DeleteSema(int sid)
                ctx->SetS32(2, DeleteSema((int)ctx->GetU32(4)));
                break;

            case 0x42: // SignalSema(int sid)
            case 0x43: // iSignalSema(int sid)
                ctx->SetS32(2, SignalSema((int)ctx->GetU32(4)));
                break;

            case 0x44: // WaitSema(int sid)
                ctx->SetS32(2, WaitSema(ctx, ram, (int)ctx->GetU32(4)));
                break;

            case 0x45: // PollSema(int sid)
            case 0x46: // iPollSema(int sid)
                ctx->SetS32(2, PollSema((int)ctx->GetU32(4)));
                break;

            case 0x47: // ReferSemaStatus(int sid, SemaParam* status)
            case 0x48: // iReferSemaStatus
                ctx->SetS32(2, ReferSemaStatus(ram, (int)ctx->GetU32(4), ctx->GetU32(5)));
                break;

            case 0x4A: // SetOsdConfigParam(uint* param)
            {
                uint addr = ctx->GetU32(4) & 0x01FFFFFFu;
                if (addr + 4 <= CpuContext.RamSize)
                    _osdConfigParam = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + addr, 4));
                ctx->SetS32(2, 0);
                break;
            }

            case 0x4B: // GetOsdConfigParam(uint* param)
            {
                uint addr = ctx->GetU32(4) & 0x01FFFFFFu;
                if (addr + 4 <= CpuContext.RamSize)
                    BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + addr, 4), _osdConfigParam);
                ctx->SetS32(2, 0);
                break;
            }

            case 0x50: // CreateEventFlag(EventFlagParam* param)
                ctx->SetS32(2, CreateEventFlag(ram, ctx->GetU32(4)));
                break;

            case 0x51: // DeleteEventFlag(int ef)
                ctx->SetS32(2, _eventFlags.Remove((int)ctx->GetU32(4)) ? 0 : -1);
                break;

            case 0x52: // SetEventFlag(int ef, uint bits)
            case 0x53: // iSetEventFlag
            {
                if (_eventFlags.TryGetValue((int)ctx->GetU32(4), out var ef))
                {
                    ef.Bits |= ctx->GetU32(5);
                    ctx->SetS32(2, 0);
                }
                else ctx->SetS32(2, -1);
                break;
            }

            case 0x54: // ClearEventFlag(int ef, uint bits)
            case 0x55: // iClearEventFlag
            {
                if (_eventFlags.TryGetValue((int)ctx->GetU32(4), out var ef))
                {
                    ef.Bits &= ctx->GetU32(5);
                    ctx->SetS32(2, 0);
                }
                else ctx->SetS32(2, -1);
                break;
            }

            case 0x56: // WaitEventFlag
            case 0x57: // PollEventFlag
            case 0x58: // iPollEventFlag
                ctx->SetS32(2, WaitOrPollEventFlag(ram, (int)ctx->GetU32(4), ctx->GetU32(5), (int)ctx->GetU32(6), ctx->GetU32(7), pollOnly: normId != 0x56));
                break;

            case 0x59: // ReferEventFlagStatus
                ctx->SetS32(2, 0);
                break;

            case 0x5A: // Copy
            case 0x5B: // GetEntryAddress
                ctx->SetU32(2, 0x00001000u);
                break;

            case 0x5C: // EnableIntcHandler
            case 0x5D: // DisableIntcHandler
            {
                int hid = (int)ctx->GetU32(4);
                var h = _intcHandlers.FirstOrDefault(x => x.Id == hid);
                if (h != null) h.Enabled = (normId == 0x5C);
                ctx->SetS32(2, 1);
                break;
            }

            case 0x5E: // EnableDmacHandler
            case 0x5F: // DisableDmacHandler
            {
                int hid = (int)ctx->GetU32(4);
                var h = _dmacHandlers.FirstOrDefault(x => x.Id == hid);
                if (h != null) h.Enabled = (normId == 0x5E);
                ctx->SetS32(2, 1);
                break;
            }

            case 0x64: // FlushCache
            case 0x68: // iFlushCache
                ctx->SetS32(2, 0);
                break;

            case 0x6E: // SetOsdConfigParam2
            case 0x6F: // GetOsdConfigParam2
            {
                uint buf = ctx->GetU32(4) & 0x01FFFFFFu;
                int size = (int)ctx->GetU32(5);
                if (normId == 0x6F && buf != 0 && size > 0 && buf + (uint)size <= CpuContext.RamSize)
                {
                    new Span<byte>(ram + buf, size).Clear();
                }
                ctx->SetS32(2, 0);
                break;
            }

            case 0x70: // GsGetIMR
                ctx->SetU64(2, _gsImr);
                break;

            case 0x71: // GsPutIMR
            {
                ulong old = _gsImr;
                _gsImr = ctx->GetU64(4);
                ctx->SetU64(2, old);
                break;
            }

            case 0x73: // SetVSyncFlag(uint* ptr1, ulong* ptr2)
                _vsyncFlagPtr1 = ctx->GetU32(4);
                _vsyncFlagPtr2 = ctx->GetU32(5);
                if (_vsyncFlagPtr1 != 0)
                    BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + (_vsyncFlagPtr1 & 0x01FFFFFFu), 4), 1);
                if (_vsyncFlagPtr2 != 0)
                    BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(ram + (_vsyncFlagPtr2 & 0x01FFFFFFu), 8), 0xFF00u);
                ctx->SetS32(2, 0);
                break;

            case 0x74: // SetSyscall(int num, uint address)
            {
                int num = (int)ctx->GetU32(4);
                uint addr = ctx->GetU32(5);
                // Ignore kernel-space patch addresses (>= 0x80000000) from _InitSys
                if (addr < 0x80000000u)
                    _customSyscallTable[num] = addr;
                ctx->SetS32(2, 0);
                break;
            }

            case 0x76: // sceSifDmaStat(int trid)
                ctx->SetS32(2, -1); // < 0 means transfer already completed!
                break;

            case 0x77: // sceSifSetDma(SifDmaTransfer* sdd, int len)
            {
                uint sdd = ctx->GetU32(4);
                uint len = ctx->GetU32(5);
                int id = OnSifSetDma != null ? OnSifSetDma(ctx, ram, sdd, len) : 1;
                ctx->SetS32(2, id);
                break;
            }

            case 0x78: // sceSifSetDChain()
                ctx->SetS32(2, 0);
                break;

            case 0x79: // sceSifSetReg(uint reg, uint val)
                OnSifSetReg?.Invoke(ctx->GetU32(4), ctx->GetU32(5));
                ctx->SetS32(2, 0);
                break;

            case 0x7A: // sceSifGetReg(uint reg)
                ctx->SetU32(2, OnSifGetReg != null ? OnSifGetReg(ctx->GetU32(4)) : 0x00070000u);
                break;

            case 0x7C: // Deci2Call — return -1 so retail builds don't hang waiting on devkit host
                ctx->SetS32(2, -1);
                break;

            case 0x7E: // MachineType
                ctx->SetS32(2, 0);
                break;

            case 0x7F: // GetMemorySize
                ctx->SetU32(2, CpuContext.RamSize); // 32 MB (0x02000000)
                break;

            case 0x80: // QueryBootMode
                ctx->SetU32(2, 0);
                break;

            case 0x82: // GetThreadTLS
                ctx->SetU32(2, 0);
                break;

            case 0x83: // FindAddress (used by 0x00489650 InitTLBFunctions)
            {
                SeedBiosMemory(ram);
                uint target = ctx->GetU32(6);
                if (target == 0x004895F8u)
                    ctx->SetU32(2, 0x00001000u + 524u);
                else if (target == 0x004895C0u)
                    ctx->SetU32(2, 0x00001000u + 360u);
                else
                    ctx->SetU32(2, 0x00001000u);
                break;
            }

            default:
                ctx->SetS32(2, 0);
                break;
        }
    }

    public uint InitMainThread(CpuContext* ctx, byte* ram, uint gp, uint stack, int stackSize, uint argsAddr, uint rootAddr)
    {
        uint topOfStack;
        if (stack == 0xFFFFFFFFu)
        {
            topOfStack = 0x02000000u;
        }
        else if (stackSize < 0)
        {
            topOfStack = stack;
        }
        else
        {
            topOfStack = stack + (uint)stackSize;
        }

        // Reserve 0x100 bytes at top of stack for kernel frame
        uint sp = (topOfStack - 0x100u) & ~0xFu;

        if (_threads.TryGetValue(1, out var mainThread))
        {
            mainThread.GpReg = gp;
            mainThread.StackBase = stack == 0xFFFFFFFFu ? (topOfStack - (uint)Math.Max(0x2000, stackSize)) : stack;
            mainThread.StackSize = stackSize;
            mainThread.Status = Ps2ThreadStatus.Run;
        }

        // Populate argc=1 and argv[0]="cdrom0:\SLUS_216.64;1" in args buffer (0x0050A780)
        if (argsAddr != 0)
        {
            uint physArgs = argsAddr & 0x01FFFFFFu;
            if (physArgs + 64 <= CpuContext.RamSize)
            {
                Span<byte> argsSpan = new Span<byte>(ram + physArgs, 64);
                argsSpan.Clear();
                uint strAddr = argsAddr + 16u;
                BinaryPrimitives.WriteInt32LittleEndian(argsSpan[0..4], 1); // argc = 1
                BinaryPrimitives.WriteUInt32LittleEndian(argsSpan[4..8], strAddr); // argv[0]
                ReadOnlySpan<byte> elfArg = "cdrom0:\\SLUS_216.64;1\0"u8;
                elfArg.CopyTo(argsSpan[16..]);
            }
        }

        return sp;
    }

    public uint InitializeHeap(uint heapBase, int heapSize)
    {
        _heapBase = heapBase;
        if (heapSize == -1 || (uint)heapSize == 0xFFFFFFFFu)
        {
            // Leave room below stack (0x01FFE000)
            _heapEnd = _threads.TryGetValue(1, out var main) && main.StackBase > heapBase
                ? main.StackBase
                : 0x01FFE000u;
        }
        else
        {
            _heapEnd = heapBase + (uint)heapSize;
        }
        return _heapEnd;
    }

    public int CreateThread(CpuContext* ctx, byte* ram, uint paramAddr)
    {
        uint phys = paramAddr & 0x01FFFFFFu;
        if (phys + 48 > CpuContext.RamSize)
            return -1;

        ReadOnlySpan<byte> p = new ReadOnlySpan<byte>(ram + phys, 48);
        // ThreadParam struct layout:
        // +0x00: int status
        // +0x04: uint func (entry PC)
        // +0x08: uint stack
        // +0x0C: int stackSize
        // +0x10: uint gpReg
        // +0x14: int initPriority
        // +0x18: int currentPriority
        // +0x1C: uint attr
        // +0x20: uint option
        uint func = BinaryPrimitives.ReadUInt32LittleEndian(p[0x04..0x08]);
        uint stack = BinaryPrimitives.ReadUInt32LittleEndian(p[0x08..0x0C]);
        int stackSize = BinaryPrimitives.ReadInt32LittleEndian(p[0x0C..0x10]);
        uint gpReg = BinaryPrimitives.ReadUInt32LittleEndian(p[0x10..0x14]);
        int initPrio = BinaryPrimitives.ReadInt32LittleEndian(p[0x14..0x18]);
        uint attr = BinaryPrimitives.ReadUInt32LittleEndian(p[0x1C..0x20]);
        uint option = BinaryPrimitives.ReadUInt32LittleEndian(p[0x20..0x24]);

        lock (_syncLock)
        {
            int id = _nextThreadId++;
            var thread = new EeThreadState
            {
                Id = id,
                Status = Ps2ThreadStatus.Dormant,
                EntryPc = func,
                StackBase = stack,
                StackSize = stackSize,
                GpReg = gpReg != 0 ? gpReg : (ctx != null ? ctx->GetU32(28) : 0),
                InitPriority = initPrio,
                CurrentPriority = initPrio,
                Attr = attr,
                Option = option
            };

            _threads[id] = thread;
            return id;
        }
    }

    public int DeleteThread(int tid)
    {
        lock (_syncLock)
        {
            if (tid <= 1 || !_threads.TryGetValue(tid, out var t))
                return -1;
            if (t.Status == Ps2ThreadStatus.Run)
                return -1;
            t.SleepEvent.Set();
            _threads.Remove(tid);
            return tid;
        }
    }

    public int StartThread(CpuContext* callerCtx, byte* ram, int tid, uint arg)
    {
        EeThreadState? t;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(tid, out t) || t.Status != Ps2ThreadStatus.Dormant)
                return -1;

            uint stackTop = (t.StackBase + (uint)Math.Max(0x100, t.StackSize) - 0x20u) & ~0xFu;

            var threadCtx = new CpuContext();
            threadCtx.Initialize(callerCtx != null ? callerCtx->Scratchpad : null, callerCtx != null ? callerCtx->Vu0DataMem : null);
            threadCtx.PC = t.EntryPc;
            threadCtx.SetU32(29, stackTop);               // $sp
            threadCtx.SetU32(4, arg);                     // $a0 = arg
            threadCtx.SetU32(28, t.GpReg);                // $gp
            threadCtx.SetU32(31, ThreadExitSentinelPc);   // $ra = ExitThread stub

            t.SavedContext = threadCtx;
            t.Status = Ps2ThreadStatus.Ready;
        }

        Console.WriteLine($"[EeKernel] StartThread(tid={tid}, entry=0x{t.EntryPc:X8}, arg=0x{arg:X8})");

        if (tid == 1)
            return tid;

        // Spawn a background host thread for guest worker thread
        var workerThread = new Thread(() =>
        {
            s_currentThreadId = tid;
            byte* workerScratchpad = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
            byte* workerVu0Data = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(4096);
            CpuContext localCtx = t.SavedContext;
            localCtx.Scratchpad = workerScratchpad;
            localCtx.Vu0DataMem = workerVu0Data;

            lock (_syncLock)
            {
                t.Status = Ps2ThreadStatus.Run;
            }

            try
            {
                uint pc = localCtx.PC;
                Console.WriteLine($"[EeKernel] Worker thread {tid} started executing at PC=0x{pc:X8} (sp=0x{localCtx.GetU32(29):X8})");
                if (pc != 0 && pc != ThreadExitSentinelPc && !_isShuttingDown)
                {
                    RuntimeDispatcher.Call(pc, &localCtx, ram);
                }
                Console.WriteLine($"[EeKernel] Worker thread {tid} finished execution");
            }
            catch (Exception) when (_isShuttingDown)
            {
                // Expected during host shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EeKernel] Worker thread {tid} unhandled exception: {ex}");
            }
            finally
            {
                ReleaseAllocatorIfHeld();
                lock (_syncLock)
                {
                    t.SavedContext = localCtx;
                    t.Status = Ps2ThreadStatus.Dormant;
                    t.WakingUpFromWait = false;
                }
                System.Runtime.InteropServices.NativeMemory.Free(workerScratchpad);
                System.Runtime.InteropServices.NativeMemory.Free(workerVu0Data);
            }
        })
        {
            Name = $"PS2_EE_Thread_{tid}",
            IsBackground = true
        };

        t.HostThread = workerThread;
        workerThread.Start();
        return tid;
    }

    public void ExitCurrentThread(CpuContext* ctx, bool delete)
    {
        int tid = CurrentThreadId;
        lock (_syncLock)
        {
            if (_threads.TryGetValue(tid, out var t))
            {
                t.Status = Ps2ThreadStatus.Dormant;
                if (delete && tid != 1)
                    _threads.Remove(tid);
            }
        }

        if (ctx != null)
        {
            ctx->PC = 0;
        }
    }

    public int TerminateThread(int tid)
    {
        lock (_syncLock)
        {
            if (tid == CurrentThreadId || !_threads.TryGetValue(tid, out var t))
                return -1;
            t.Status = Ps2ThreadStatus.Dormant;
            t.WaitType = 0;
            t.WaitSemaId = -1;
            t.SleepEvent.Set();
            return tid;
        }
    }

    public void ShutdownThreads(int waitTimeoutMs = 250)
    {
        _isShuttingDown = true;
        List<Thread> workersToJoin = new();
        lock (_syncLock)
        {
            foreach (var kvp in _threads)
            {
                if (kvp.Key != 1)
                {
                    kvp.Value.Status = Ps2ThreadStatus.Dormant;
                    kvp.Value.WaitType = 0;
                    kvp.Value.WaitSemaId = -1;
                    try
                    {
                        kvp.Value.SleepEvent.Set();
                    }
                    catch { }

                    if (kvp.Value.HostThread != null && kvp.Value.HostThread.IsAlive)
                    {
                        workersToJoin.Add(kvp.Value.HostThread);
                    }
                }
            }

            foreach (var s in _semas.Values)
            {
                try
                {
                    s.Event.Set();
                }
                catch { }
            }
        }

        foreach (var th in workersToJoin)
        {
            try
            {
                if (!th.Join(waitTimeoutMs))
                {
                    th.Interrupt();
                    th.Join(50);
                }
            }
            catch
            {
            }
        }
    }

    public int ChangeThreadPriority(int tid, int prio)
    {
        int targetId = tid == 0 ? CurrentThreadId : tid;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(targetId, out var t))
                return -1;
            int old = t.CurrentPriority;
            t.CurrentPriority = prio;
            return old;
        }
    }

    public int ReleaseWaitThread(int tid)
    {
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(tid, out var t))
                return -1;
            if (t.Status == Ps2ThreadStatus.Wait)
                t.Status = Ps2ThreadStatus.Ready;
            else if (t.Status == Ps2ThreadStatus.WaitSuspend)
                t.Status = Ps2ThreadStatus.Suspend;
            t.WaitType = 0;
            t.WaitSemaId = -1;
            t.SleepEvent.Set();
            return tid;
        }
    }

    public int ReferThreadStatus(byte* ram, int tid, uint infoAddr)
    {
        int targetId = tid == 0 ? CurrentThreadId : tid;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(targetId, out var t))
                return -1;

            uint phys = infoAddr & 0x01FFFFFFu;
            if (infoAddr != 0 && phys + 48 <= CpuContext.RamSize)
            {
                Span<byte> p = new Span<byte>(ram + phys, 48);
                p.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(p[0x00..0x04], (int)t.Status);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x04..0x08], t.EntryPc);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x08..0x0C], t.StackBase);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x0C..0x10], t.StackSize);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x10..0x14], t.GpReg);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x14..0x18], t.InitPriority);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x18..0x1C], t.CurrentPriority);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x1C..0x20], t.Attr);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x20..0x24], t.Option);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x24..0x28], t.WaitType);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x28..0x2C], t.WaitSemaId);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x2C..0x30], t.WakeupCount);
            }

            return (int)t.Status;
        }
    }

    public int SleepThread(CpuContext* ctx)
    {
        int tid = CurrentThreadId;
        EeThreadState? t;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(tid, out t))
                return -1;

            if (t.WakeupCount > 0)
            {
                t.WakeupCount--;
                return 0;
            }

            t.Status = Ps2ThreadStatus.Wait;
            t.WaitType = 1;
        }

        if (_isShuttingDown)
        {
            if (tid != 1)
                throw new OperationCanceledException("PS2 worker thread shutdown");
            lock (_syncLock)
            {
                t.Status = Ps2ThreadStatus.Run;
                t.WaitType = 0;
            }
            return 0;
        }

        if (tid == 1)
        {
            lock (_syncLock)
            {
                t.Status = Ps2ThreadStatus.Run;
                t.WaitType = 0;
            }
            return 0;
        }

        while (true)
        {
            if (_isShuttingDown)
                throw new OperationCanceledException("PS2 worker thread shutdown");
            try
            {
                t.SleepEvent.WaitOne(100);
            }
            finally
            {
                t.WakingUpFromWait = false;
            }
            if (_isShuttingDown)
                throw new OperationCanceledException("PS2 worker thread shutdown");
            lock (_syncLock)
            {
                if (t.Status != Ps2ThreadStatus.Wait || t.WaitType != 1)
                {
                    if (t.Status == Ps2ThreadStatus.Ready)
                        t.Status = Ps2ThreadStatus.Run;
                    break;
                }
                if (t.WakeupCount > 0)
                {
                    t.WakeupCount--;
                    t.Status = Ps2ThreadStatus.Run;
                    t.WaitType = 0;
                    break;
                }
            }
        }
        return 0;
    }

    public int WakeupThread(int tid)
    {
        int targetId = tid == 0 ? CurrentThreadId : tid;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(targetId, out var t))
                return -1;

            if (t.Status == Ps2ThreadStatus.Wait && t.WaitType == 1)
            {
                t.Status = Ps2ThreadStatus.Ready;
                t.WaitType = 0;
                t.SleepEvent.Set();
            }
            else if (t.Status == Ps2ThreadStatus.WaitSuspend && t.WaitType == 1)
            {
                t.Status = Ps2ThreadStatus.Suspend;
                t.WaitType = 0;
                t.SleepEvent.Set();
            }
            else
            {
                t.WakeupCount++;
            }

            return t.WakeupCount;
        }
    }

    public int CancelWakeupThread(int tid)
    {
        int targetId = tid == 0 ? CurrentThreadId : tid;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(targetId, out var t))
                return -1;
            int prev = t.WakeupCount;
            t.WakeupCount = 0;
            return prev;
        }
    }

    public int SuspendThread(int tid)
    {
        int targetId = tid == 0 ? CurrentThreadId : tid;
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(targetId, out var t))
                return -1;

            t.Status = t.Status switch
            {
                Ps2ThreadStatus.Wait => Ps2ThreadStatus.WaitSuspend,
                Ps2ThreadStatus.Dormant => Ps2ThreadStatus.Dormant,
                _ => Ps2ThreadStatus.Suspend
            };
            return 0;
        }
    }

    public int ResumeThread(int tid)
    {
        lock (_syncLock)
        {
            if (!_threads.TryGetValue(tid, out var t))
                return -1;

            t.Status = t.Status switch
            {
                Ps2ThreadStatus.WaitSuspend => Ps2ThreadStatus.Wait,
                Ps2ThreadStatus.Suspend => Ps2ThreadStatus.Ready,
                _ => t.Status
            };
            return 0;
        }
    }

    public int CreateSema(byte* ram, uint paramAddr)
    {
        uint phys = paramAddr & 0x01FFFFFFu;
        if (phys + 24 > CpuContext.RamSize)
            return -1;

        ReadOnlySpan<byte> p = new ReadOnlySpan<byte>(ram + phys, 24);
        // SemaParam layout:
        // +0x00: int count
        // +0x04: int max_count
        // +0x08: int init_count
        // +0x0C: int wait_threads
        // +0x10: uint attr
        // +0x14: uint option
        int maxCount = BinaryPrimitives.ReadInt32LittleEndian(p[0x04..0x08]);
        int initCount = BinaryPrimitives.ReadInt32LittleEndian(p[0x08..0x0C]);
        uint attr = BinaryPrimitives.ReadUInt32LittleEndian(p[0x10..0x14]);
        uint option = BinaryPrimitives.ReadUInt32LittleEndian(p[0x14..0x18]);

        lock (_syncLock)
        {
            int id = _nextSemaId++;
            _semas[id] = new EeSemaState
            {
                Id = id,
                Count = initCount,
                InitCount = initCount,
                MaxCount = maxCount > 0 ? maxCount : 1,
                Attr = attr,
                Option = option
            };
            return id;
        }
    }

    public int DeleteSema(int sid)
    {
        lock (_syncLock)
        {
            if (_semas.Remove(sid, out var s))
            {
                s.Event.Set();
                return sid;
            }
            return -1;
        }
    }

    public int SignalSema(int sid)
    {
        EeThreadState? wokenWaiter = null;
        lock (_syncLock)
        {
            if (!_semas.TryGetValue(sid, out var s))
                return -1;

            if (s.WaitingThreadIds.Count > 0)
            {
                int waiterId = s.WaitingThreadIds.Dequeue();
                if (_threads.TryGetValue(waiterId, out var t))
                {
                    bool wasWaitingOnThisSema = t.Status == Ps2ThreadStatus.Wait && t.WaitSemaId == sid;
                    t.Status = t.Status == Ps2ThreadStatus.WaitSuspend ? Ps2ThreadStatus.Suspend : Ps2ThreadStatus.Ready;
                    t.WaitType = 0;
                    t.WaitSemaId = -1;
                    if (wasWaitingOnThisSema && waiterId >= 2 && waiterId != CurrentThreadId)
                    {
                        t.WakingUpFromWait = true;
                        wokenWaiter = t;
                    }
                }
            }

            if (s.Count < s.MaxCount)
                s.Count++;

            s.Event.Set();
        }

        return sid;
    }

    public int WaitSema(CpuContext* ctx, int sid) => WaitSema(ctx, null, sid);

    public int WaitSema(CpuContext* ctx, byte* ram, int sid)
    {
        int tid = CurrentThreadId;
        int mainWaitCount = 0;

        while (true)
        {
            if (_isShuttingDown)
            {
                if (tid != 1)
                    throw new OperationCanceledException("PS2 worker thread shutdown");
                return -1;
            }

            EeSemaState? s;
            lock (_syncLock)
            {
                if (!_semas.TryGetValue(sid, out s))
                    return -1;

                if (_threads.TryGetValue(tid, out var t))
                {
                    if (t.Status != Ps2ThreadStatus.Run && t.Status != Ps2ThreadStatus.Wait && t.Status != Ps2ThreadStatus.Ready)
                        return -1;
                }

                if (s.Count > 0)
                {
                    s.Count--;
                    if (_threads.TryGetValue(tid, out var tRunning))
                    {
                        tRunning.Status = Ps2ThreadStatus.Run;
                        tRunning.WaitType = 0;
                        tRunning.WaitSemaId = -1;
                        tRunning.WakingUpFromWait = false;
                    }
                    return sid;
                }

                if (_threads.TryGetValue(tid, out var tWaiting))
                {
                    tWaiting.Status = Ps2ThreadStatus.Wait;
                    tWaiting.WaitType = 2;
                    tWaiting.WaitSemaId = sid;
                }

                if (!s.WaitingThreadIds.Contains(tid))
                    s.WaitingThreadIds.Enqueue(tid);
            }

            if (tid == 1)
            {
                OnMainThreadIdleTick?.Invoke();

                bool hasRunningWorkers = false;
                lock (_syncLock)
                {
                    if (_threads.TryGetValue(2, out var tMainGame))
                    {
                        hasRunningWorkers = tMainGame.Status != Ps2ThreadStatus.Dormant;
                    }
                    else
                    {
                        foreach (var pair in _threads)
                        {
                            if (pair.Key != 1 && pair.Value.Status != Ps2ThreadStatus.Dormant)
                            {
                                hasRunningWorkers = true;
                                break;
                            }
                        }
                    }
                }

                bool signaled = s.Event.WaitOne(hasRunningWorkers ? 4 : 0);
                if (_isShuttingDown)
                    return -1;

                if (!signaled && !hasRunningWorkers && ram != null && ctx != null && _intcHandlers.Count > 0)
                {
                    TriggerVBlank(ctx, ram);
                }

                mainWaitCount++;
                if (!hasRunningWorkers && mainWaitCount >= 2)
                {
                    return sid;
                }
            }
            else
            {
                uint ra = ctx != null ? ctx->GetU32(31) : 0u;
                bool isHeapMutex = s.Option == 0x69696969u ||
                                   ra == 0x003D84B8u ||
                                   ra == 0x003C0F60u ||
                                   ra == 0x003C11D8u ||
                                   ra == 0x003C20A8u ||
                                   ra == 0x003C4208u;
                bool isEaThreadSema = ra == 0x003E1F38u;

                bool sig;
                try
                {
                    sig = s.Event.WaitOne(10);
                }
                finally
                {
                    lock (_syncLock)
                    {
                        if (_threads.TryGetValue(tid, out var tWoke))
                        {
                            tWoke.WakingUpFromWait = false;
                        }
                    }
                }

                if (_isShuttingDown)
                    throw new OperationCanceledException("PS2 worker thread shutdown");

                if (!sig && tid == 2)
                {
                    mainWaitCount++;
                    if (mainWaitCount == 1 || mainWaitCount % 25 == 0)
                    {
                        Console.WriteLine($"[EeKernel] MainGameThread (tid=2) waiting in WaitSema(sid={sid}, init={s.InitCount}, max={s.MaxCount}, opt=0x{s.Option:X8}) waitCount={mainWaitCount} RA=0x{ra:X8}");
                    }
                    if (!isHeapMutex && ram != null && ctx != null && _intcHandlers.Count > 0)
                    {
                        TriggerVBlank(ctx, ram);
                    }
                    if (!isHeapMutex && !isEaThreadSema && mainWaitCount >= 5)
                    {
                        Console.WriteLine($"[EeKernel] MainGameThread (tid=2) breaking out of WaitSema(sid={sid}) after {mainWaitCount} waits! RA=0x{ra:X8}");
                        lock (_syncLock)
                        {
                            if (_threads.TryGetValue(tid, out var tRun2))
                            {
                                tRun2.Status = Ps2ThreadStatus.Run;
                                tRun2.WaitType = 0;
                                tRun2.WaitSemaId = -1;
                                tRun2.WakingUpFromWait = false;
                            }
                        }
                        return sid;
                    }
                }
            }
        }
    }

    public int PollSema(int sid)
    {
        lock (_syncLock)
        {
            if (!_semas.TryGetValue(sid, out var s))
                return -1;
            if (s.Count <= 0)
                return -1;
            s.Count--;
            return sid;
        }
    }

    public int ReferSemaStatus(byte* ram, int sid, uint statusAddr)
    {
        lock (_syncLock)
        {
            if (!_semas.TryGetValue(sid, out var s))
                return -1;

            uint phys = statusAddr & 0x01FFFFFFu;
            if (statusAddr != 0 && phys + 24 <= CpuContext.RamSize)
            {
                Span<byte> p = new Span<byte>(ram + phys, 24);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x00..0x04], s.Count);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x04..0x08], s.MaxCount);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x08..0x0C], s.InitCount);
                BinaryPrimitives.WriteInt32LittleEndian(p[0x0C..0x10], s.WaitingThreadIds.Count);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x10..0x14], s.Attr);
                BinaryPrimitives.WriteUInt32LittleEndian(p[0x14..0x18], s.Option);
            }

            return sid;
        }
    }

    public int CreateEventFlag(byte* ram, uint paramAddr)
    {
        uint phys = paramAddr & 0x01FFFFFFu;
        if (phys + 12 > CpuContext.RamSize)
            return -1;

        ReadOnlySpan<byte> p = new ReadOnlySpan<byte>(ram + phys, 12);
        uint attr = BinaryPrimitives.ReadUInt32LittleEndian(p[0..4]);
        uint option = BinaryPrimitives.ReadUInt32LittleEndian(p[4..8]);
        uint initPattern = BinaryPrimitives.ReadUInt32LittleEndian(p[8..12]);

        lock (_syncLock)
        {
            int id = _nextEventFlagId++;
            _eventFlags[id] = new EeEventFlagState
            {
                Id = id,
                Attr = attr,
                Option = option,
                Bits = initPattern
            };
            return id;
        }
    }

    public int WaitOrPollEventFlag(byte* ram, int efId, uint waitPattern, int mode, uint resBitsPtr, bool pollOnly)
    {
        EeEventFlagState? ef;
        lock (_syncLock)
        {
            if (!_eventFlags.TryGetValue(efId, out ef))
                return -1;

            bool matched = (mode & 1) != 0
                ? (ef.Bits & waitPattern) != 0          // OR mode
                : (ef.Bits & waitPattern) == waitPattern; // AND mode

            if (!matched && pollOnly)
                return -1;

            if (resBitsPtr != 0)
            {
                uint phys = resBitsPtr & 0x01FFFFFFu;
                if (phys + 4 <= CpuContext.RamSize)
                    BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + phys, 4), ef.Bits);
            }

            if ((mode & 0x10) != 0)
                ef.Bits = 0; // Clear all
            else if ((mode & 0x20) != 0)
                ef.Bits &= ~waitPattern; // Clear matched

            return 0;
        }
    }

    public int AddIntcHandler(int cause, uint handlerPc, int next, uint arg, uint gp)
    {
        lock (_syncLock)
        {
            int id = _nextHandlerId++;
            var entry = new EeIntcHandlerEntry
            {
                Id = id,
                Cause = cause,
                HandlerPc = handlerPc,
                Arg = arg,
                Gp = gp,
                Enabled = true
            };
            if (next == 0)
                _intcHandlers.Insert(0, entry);
            else
                _intcHandlers.Add(entry);
            return id;
        }
    }

    public int RemoveIntcHandler(int cause, int handlerId)
    {
        lock (_syncLock)
        {
            int removed = _intcHandlers.RemoveAll(h => h.Cause == cause && h.Id == handlerId);
            return removed > 0 ? 0 : -1;
        }
    }

    public int AddDmacHandler(int channel, uint handlerPc, int next, uint arg, uint gp)
    {
        lock (_syncLock)
        {
            int id = _nextHandlerId++;
            var entry = new EeDmacHandlerEntry
            {
                Id = id,
                Channel = channel,
                HandlerPc = handlerPc,
                Arg = arg,
                Gp = gp,
                Enabled = true
            };
            if (next == 0)
                _dmacHandlers.Insert(0, entry);
            else
                _dmacHandlers.Add(entry);
            return id;
        }
    }

    public int RemoveDmacHandler(int channel, int handlerId)
    {
        lock (_syncLock)
        {
            int removed = _dmacHandlers.RemoveAll(h => h.Channel == channel && h.Id == handlerId);
            return removed > 0 ? 0 : -1;
        }
    }

    public void FireDmacInterrupt(CpuContext* ctx, byte* ram, int channel)
    {
        uint bit = 1u << (channel & 15);
        DmacStat |= bit;

        // Check if channel interrupt mask bit is enabled (upper 16 bits of D_STAT)
        uint maskBit = bit << 16;
        if ((DmacStat & maskBit) == 0 || ctx == null)
            return;

        EeDmacHandlerEntry[] handlers;
        lock (_syncLock)
        {
            handlers = _dmacHandlers.ToArray();
        }

        foreach (var h in handlers)
        {
            if (h.Channel == channel && h.Enabled && h.HandlerPc != 0)
            {
                InvokeGuestCallback(ctx, ram, h.HandlerPc, (uint)channel, h.Arg, h.Gp);
            }
        }
    }

    private bool _inTriggerVBlank;
    public unsafe delegate void VBlankTickHandler(byte* ram);
    internal MmioRouter? Mmio { get; set; }
    public Action? OnMainThreadIdleTick { get; set; }
    public VBlankTickHandler? OnVBlankTick { get; set; }

    public void StepFrameTimers(CpuContext* ctx, byte* ram)
    {
        // Advance hardware timers (fixed 16.667 ms step or real-time wall-clock delta) and assert VBlank/timer INTC bits
        Mmio?.StepFrameTimers(ram);
        IntcStat |= (1u << 9) | (1u << 10);

        if (ctx == null || _inTriggerVBlank)
            return;

        _inTriggerVBlank = true;
        try
        {
            foreach (var h in _intcHandlers.ToArray())
            {
                if ((h.Cause == 9 || h.Cause == 10) &&
                    h.Enabled && ((IntcMask & (1u << h.Cause)) != 0) && h.HandlerPc != 0)
                {
                    InvokeGuestCallback(ctx, ram, h.HandlerPc, (uint)h.Cause, h.Arg, h.Gp);
                }
            }
        }
        finally
        {
            _inTriggerVBlank = false;
        }
    }

    public void TriggerVBlank(CpuContext* ctx, byte* ram)
    {
        // Set INTC VBlank start (bit 2) and VBlank end (bit 3)
        IntcStat |= (1u << 2) | (1u << 3);

        if (ram != null)
            OnVBlankTick?.Invoke(ram);

        if (_vsyncFlagPtr1 != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + (_vsyncFlagPtr1 & 0x01FFFFFFu), 4), 1);
        if (_vsyncFlagPtr2 != 0)
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(ram + (_vsyncFlagPtr2 & 0x01FFFFFFu), 8), ~_gsImr & 0xFF00u);

        if (ctx == null || _inTriggerVBlank)
            return;

        _inTriggerVBlank = true;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var h in _intcHandlers.ToArray())
                {
                    if ((h.Cause == 2 || h.Cause == 3) &&
                        h.Enabled && ((IntcMask & (1u << h.Cause)) != 0) && h.HandlerPc != 0)
                    {
                        InvokeGuestCallback(ctx, ram, h.HandlerPc, (uint)h.Cause, h.Arg, h.Gp);
                    }
                }

                // If the engine's GS/VIF1 command queue (0x00596D50+176 = 0x00596E00) is no longer paused on an odd-field VBlank flip (or < 2 buffers queued), stop after 1 pass
                if (ram == null ||
                    BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + 0x00596E00u, 4)) == 0 ||
                    BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + 0x00596DFCu, 4)) < 2)
                    break;
            }
        }
        finally
        {
            _inTriggerVBlank = false;
        }
    }

    public static void InvokeGuestCallback(CpuContext* callerCtx, byte* ram, uint funcPc, uint arg0, uint arg1, uint gp)
    {
        if (funcPc == 0)
            return;

        CpuContext saved = *callerCtx;
        try
        {
            callerCtx->SetU32(4, arg0);
            callerCtx->SetU32(5, arg1);
            if (gp != 0)
                callerCtx->SetU32(28, gp);
            callerCtx->SetU32(31, ThreadExitSentinelPc);
            RuntimeDispatcher.Call(funcPc, callerCtx, ram);
        }
        finally
        {
            *callerCtx = saved;
        }
    }

    /// <summary>
    /// Formats a compact summary of all EE kernel threads and their current wait state for watchdog diagnostics.
    /// </summary>
    public string FormatThreadStates()
    {
        lock (_syncLock)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _threads)
            {
                var t = kv.Value;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"T{t.Id}:{t.Status}(wait={t.WaitType},sema={t.WaitSemaId},entry=0x{t.EntryPc:X8})");
            }
            return sb.ToString();
        }
    }
}
