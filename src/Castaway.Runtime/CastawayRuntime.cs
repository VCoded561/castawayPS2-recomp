using System.Buffers.Binary;
using Castaway.Core;

namespace Castaway.Runtime;

public sealed unsafe class CastawayRuntime : IDisposable
{
    public EeKernel Kernel { get; }
    public DmaController Dma { get; }
    public MmioRouter Mmio { get; }
    public DvdVirtualFileSystem Dvd { get; }
    public PadService Pad { get; }
    public MemoryCardService MemoryCard { get; }
    public Spu2AudioService Audio { get; }
    public SifRpcRouter SifRpc { get; }

    public CastawayRuntime(
        string? dvdRootPath = null,
        string? savesRootDir = null,
        bool initOpenAl = false)
    {
        Kernel = new EeKernel();
        Dma = new DmaController(Kernel);
        Mmio = new MmioRouter(Kernel, Dma);
        Dvd = new DvdVirtualFileSystem(dvdRootPath);
        Pad = new PadService();
        MemoryCard = new MemoryCardService(savesRootDir, Kernel);
        Audio = new Spu2AudioService(initOpenAl);
        SifRpc = new SifRpcRouter(Kernel, Dvd, Pad, MemoryCard, Audio);

        Kernel.OnSifSetDma = SifRpc.HandleSifSetDma;
        Kernel.OnSifGetReg = Mmio.ReadSifReg;
        Kernel.OnSifSetReg = Mmio.WriteSifReg;
    }

    /// <summary>
    /// Attaches the Phase 3 Runtime to the global RuntimeDispatcher and pre-seeds BIOS RAM.
    /// </summary>
    public void Install(CpuContext* ctx, byte* ram, bool installHleHooks = true)
    {
        Mmio.ActiveContext = ctx;
        Mmio.ActiveRam = ram;

        if (ram != null)
        {
            EeKernel.SeedBiosMemory(ram);
        }

        RuntimeDispatcher.SyscallHandler = (c, r, id) =>
        {
            Mmio.ActiveContext = c;
            Mmio.ActiveRam = r;
            Kernel.DispatchSyscall(c, r, id);
        };

        RuntimeDispatcher.CustomMmioRead32 = Mmio.Read32;
        RuntimeDispatcher.CustomMmioWrite32 = Mmio.Write32;
        RuntimeDispatcher.CustomMmioRead128 = Mmio.Read128;
        RuntimeDispatcher.CustomMmioWrite128 = Mmio.Write128;

        if (installHleHooks)
        {
            RegisterSlus21664Hooks();
        }
    }

    /// <summary>
    /// Registers high-level interception hooks on SLUS_216.64's BIOS patchers, SIF RPC, and FileIO functions.
    /// </summary>
    public void RegisterSlus21664Hooks()
    {
        // 1. Thread exit sentinel stub at 0x001000C8 & .text base padding guard at 0x00100000
        RuntimeDispatcher.RegisterHook(EeKernel.ThreadExitSentinelPc, (ctx, ram) =>
        {
            Kernel.ExitCurrentThread(ctx, delete: false);
        });
        RuntimeDispatcher.RegisterHook(0x00100000u, (ctx, ram) =>
        {
            Console.WriteLine($"[HOOK 0x00100000 - Padding Guard] Ignored call to 0x00100000 from tid={Kernel.CurrentThreadId} RA=0x{ctx->GetU32(31):X8} a0=0x{ctx->GetU32(4):X8}");
            ctx->SetS32(2, 0);
        });

        // 2. _InitSys BIOS kernel-memory patchers & SceKerneltopThread daemon
        RuntimeDispatcher.RegisterHook(0x00489650u, (ctx, ram) => { ctx->SetS32(2, 0); }); // InitTLBFunctions
        RuntimeDispatcher.RegisterHook(0x00489B88u, (ctx, ram) => { ctx->SetS32(2, 0); }); // InitAlarm
        RuntimeDispatcher.RegisterHook(0x00483150u, (ctx, ram) => { ctx->SetS32(2, 0); }); // InitThread (SceKerneltopThread infinite WaitSema loop)
        RuntimeDispatcher.RegisterHook(0x00489888u, (ctx, ram) => { ctx->SetS32(2, 0); }); // InitSemaphores kernel patcher
        RuntimeDispatcher.RegisterHook(0x0048AE28u, (ctx, ram) => { ctx->SetS32(2, 0); }); // InitExecPS2 / InitOsd

        // 3. SIF RPC Core Hooks
        // sceSifSendCmd (0x00485178) -> returns transfer ID 1
        RuntimeDispatcher.RegisterHook(0x00485178u, (ctx, ram) =>
        {
            uint cmd = ctx->GetU32(4);
            uint packet = ctx->GetU32(5);
            uint size = ctx->GetU32(6);
            uint ra = ctx->GetU32(31);
            Console.WriteLine($"[SIF CMD] cmd=0x{cmd:X8} packet=0x{packet:X8} size={size} RA=0x{ra:X8}");
            ctx->SetS32(2, 1);
        });

        // sceSifBindRpc (0x00485A38): $a0=clientPtr, $a1=sid, $a2=mode
        RuntimeDispatcher.RegisterHook(0x00485A38u, (ctx, ram) =>
        {
            Mmio.ActiveContext = ctx;
            Mmio.ActiveRam = ram;
            int res = SifRpc.BindRpc(ram, ctx->GetU32(4), ctx->GetU32(5), ctx->GetU32(6));
            ctx->SetS32(2, res);
        });

        // sceSifCallRpc (0x00485C18):
        // $a0(4)=clientPtr, $a1(5)=rpcNum, $a2(6)=mode, $a3(7)=sendBuf,
        // $t0(8)=sendSize, $t1(9)=recvBuf, $t2(10)=recvSize, $t3(11)=endFunc, 0($sp)=endParam
        RuntimeDispatcher.RegisterHook(0x00485C18u, (ctx, ram) =>
        {
            Mmio.ActiveContext = ctx;
            Mmio.ActiveRam = ram;
            uint spPhys = ctx->GetU32(29) & 0x01FFFFFFu;
            uint endParam = spPhys + 4 <= CpuContext.RamSize
                ? BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ram + spPhys, 4))
                : 0u;

            int res = SifRpc.CallRpc(
                ctx,
                ram,
                clientPtr: ctx->GetU32(4),
                rpcNum:    ctx->GetU32(5),
                mode:      ctx->GetU32(6),
                sendBuf:   ctx->GetU32(7),
                sendSize:  (int)ctx->GetU32(8),
                recvBuf:   ctx->GetU32(9),
                recvSize:  (int)ctx->GetU32(10),
                endFunc:   ctx->GetU32(11),
                endParam:  endParam);

            ctx->SetS32(2, res);
        });

        // sceSifCheckStatRpc (0x00485E18) -> 0 (idle / completed)
        RuntimeDispatcher.RegisterHook(0x00485E18u, (ctx, ram) =>
        {
            ctx->SetS32(2, 0);
        });

        // sceSifRpcLoop (0x00486278) -> yield background RPC server thread back to caller
        RuntimeDispatcher.RegisterHook(0x00486278u, (ctx, ram) =>
        {
            ctx->SetS32(2, 0);
        });

        // 4. Controller Subsystem & IOP Event Poll Threads
        // Ps2ControllerUpdate (0x00432BB0) & IOP CD Event Poll Thread (0x00438C68) -> return immediately so threads become dormant cleanly
        RuntimeDispatcher.RegisterHook(0x00432BB0u, (ctx, ram) =>
        {
        });
        RuntimeDispatcher.RegisterHook(0x00438C68u, (ctx, ram) =>
        {
        });

        // 4b. DelayThread (0x004832E0):
        // Sony libkernl DelayThread(uint usec) creates a temporary semaphore (opt=0x00509B60)
        // and schedules a Timer 2 (INTC_TIM2=11) alarm via SetAlarm (0x0048A960).
        // Yield host thread briefly and return 0 without creating/leaking Timer 2 semaphores.
        RuntimeDispatcher.RegisterHook(0x004832E0u, (ctx, ram) =>
        {
            Thread.Yield();
            ctx->SetS32(2, 0);
        });

        // 4c. Multi-Core Thread-Safety for Guest di/ei Synchronization Primitives:
        // On PS2, EA::Allocator::Mutex (0x003D8428/0x003D8510 + inlined in 0x003C0E78, 0x003C10B8,
        // 0x003C1958, 0x003C4148) and EA::Thread::Semaphore (0x003E1ED0, 0x003E1F98, 0x003E2020)
        // use di/ei interrupt disabling for atomicity on a single EE core.
        // Serialize these across host OS threads using reentrant/atomic C# locks.
        RuntimeDispatcher.RegisterHook(0x003D8428u, (ctx, ram) =>
        {
            Kernel.EnterAllocatorSection();
            RuntimeDispatcher.Call(0x003D8428u, ctx, ram);
        });
        RuntimeDispatcher.RegisterHook(0x003D8510u, (ctx, ram) =>
        {
            try
            {
                RuntimeDispatcher.Call(0x003D8510u, ctx, ram);
            }
            finally
            {
                Kernel.ExitAllocatorSection();
            }
        });
        foreach (uint allocFn in new[] { 0x003C0E78u, 0x003C10B8u, 0x003C1958u, 0x003C4148u })
        {
            uint fnAddr = allocFn;
            RuntimeDispatcher.RegisterHook(fnAddr, (ctx, ram) =>
            {
                Kernel.EnterAllocatorSection();
                try
                {
                    RuntimeDispatcher.Call(fnAddr, ctx, ram);
                }
                finally
                {
                    Kernel.ExitAllocatorSection();
                }
            });
        }

        // EA::Thread::Semaphore::Wait (0x003E1ED0): $a0=semaObj, $a1=timeout
        // Layout: +4 = kernelSemaId, +8 = maxCount, +12 = waiters, +16 = count
        RuntimeDispatcher.RegisterHook(0x003E1ED0u, (ctx, ram) =>
        {
            uint semaPhys = ctx->GetU32(4) & 0x01FFFFFFu;
            uint timeout = ctx->GetU32(5);
            int result = 0;

            lock (Kernel.EaSemaLock)
            {
                if (timeout == 0xFFFFFFFFu)
                {
                    while (BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 16, 4)) == 0)
                    {
                        int waiters = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 12, 4));
                        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 12, 4), waiters + 1);
                        int sid = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 4, 4));

                        Monitor.Exit(Kernel.EaSemaLock);
                        try
                        {
                            // Temporarily set RA=0x003E1F38 so WaitSema recognizes EA::Thread::Semaphore::Wait
                            uint savedRa = ctx->GetU32(31);
                            ctx->SetU32(31, 0x003E1F38u);
                            try
                            {
                                Kernel.WaitSema(ctx, ram, sid);
                            }
                            finally
                            {
                                ctx->SetU32(31, savedRa);
                            }
                        }
                        finally
                        {
                            Monitor.Enter(Kernel.EaSemaLock);
                        }
                    }
                }

                int count = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 16, 4));
                if (count != 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 16, 4), count - 1);
                    result = 1;
                }
            }

            ctx->SetS32(2, result);
        });

        // EA::Thread::Semaphore::Post (0x003E1F98): $a0=semaObj -> returns 1
        RuntimeDispatcher.RegisterHook(0x003E1F98u, (ctx, ram) =>
        {
            uint semaPhys = ctx->GetU32(4) & 0x01FFFFFFu;
            int sidToSignal = -1;

            lock (Kernel.EaSemaLock)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 16, 4));
                int maxCount = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 8, 4));
                if (count < maxCount)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 16, 4), count + 1);
                }

                int waiters = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 12, 4));
                if (waiters != 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 12, 4), waiters - 1);
                    sidToSignal = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 4, 4));
                }
            }

            if (sidToSignal >= 0)
            {
                Kernel.SignalSema(sidToSignal);
            }

            ctx->SetS32(2, 1);
        });

        // EA::Thread::Semaphore::iPost (0x003E2020): $a0=semaObj
        RuntimeDispatcher.RegisterHook(0x003E2020u, (ctx, ram) =>
        {
            uint semaPhys = ctx->GetU32(4) & 0x01FFFFFFu;
            int sidToSignal = -1;

            lock (Kernel.EaSemaLock)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 16, 4));
                int maxCount = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 8, 4));
                if (count < maxCount)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 16, 4), count + 1);
                }

                int waiters = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 12, 4));
                if (waiters != 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ram + semaPhys + 12, 4), waiters - 1);
                    sidToSignal = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(ram + semaPhys + 4, 4));
                }
            }

            if (sidToSignal >= 0)
            {
                Kernel.SignalSema(sidToSignal);
            }
        });

        // 4. IOP Reboot & Module Loader Hooks
        // sceSifRebootIop (0x004893E0) & sceSifSyncIop (0x00489390) -> 1 (ready)
        RuntimeDispatcher.RegisterHook(0x004893E0u, (ctx, ram) => { ctx->SetS32(2, 1); });
        RuntimeDispatcher.RegisterHook(0x00489390u, (ctx, ram) => { ctx->SetS32(2, 1); });
        RuntimeDispatcher.RegisterHook(0x00419900u, (ctx, ram) => { ctx->SetS32(2, 1); }); // LoadModule wrapper

        // 5. Direct FileIO Hooks (sceOpen, sceClose, sceLseek, sceRead)
        // sceOpen (0x00486C80): $a0=pathPtr, $a1=flags
        RuntimeDispatcher.RegisterHook(0x00486C80u, (ctx, ram) =>
        {
            string path = DvdVirtualFileSystem.ReadGuestCString(ram, ctx->GetU32(4));
            int flags = (int)ctx->GetU32(5);
            int fd = Dvd.Open(path, flags);
            ctx->SetS32(2, fd);
        });

        // sceClose (0x00486F10): $a0=fd
        RuntimeDispatcher.RegisterHook(0x00486F10u, (ctx, ram) =>
        {
            int fd = (int)ctx->GetU32(4);
            ctx->SetS32(2, Dvd.Close(fd));
        });

        // sceLseek (0x00487088): $a0=fd, $a1=offset, $a2=whence
        RuntimeDispatcher.RegisterHook(0x00487088u, (ctx, ram) =>
        {
            int fd = (int)ctx->GetU32(4);
            int offset = (int)ctx->GetU32(5);
            int whence = (int)ctx->GetU32(6);
            ctx->SetS32(2, (int)Dvd.Seek(fd, offset, whence));
        });

        // sceRead (0x004872C8): $a0=fd, $a1=bufPtr, $a2=size
        RuntimeDispatcher.RegisterHook(0x004872C8u, (ctx, ram) =>
        {
            int fd = (int)ctx->GetU32(4);
            uint bufPtr = ctx->GetU32(5);
            int size = (int)ctx->GetU32(6);
            ctx->SetS32(2, Dvd.ReadToRam(fd, ram, bufPtr, size));
        });

        // 6. EA IOP Streamer FileSys (CFileSys @ 0x00596E90) Hooks
        // CFileSys::Open (0x0042EAE0): $a0=cFileSysPtr, $a1=openReqPtr
        RuntimeDispatcher.RegisterHook(0x0042EAE0u, (ctx, ram) =>
        {
            int slot = Dvd.OpenEaFileSysSlot(ram, ctx->GetU32(4), ctx->GetU32(5));
            ctx->SetS32(2, slot);
        });

        // CFileSys::Close (0x0042EEB8): $a0=cFileSysPtr, $a1=slot
        RuntimeDispatcher.RegisterHook(0x0042EEB8u, (ctx, ram) =>
        {
            int res = Dvd.CloseEaFileSysSlot(ram, ctx->GetU32(4), ctx->GetU32(5));
            ctx->SetS32(2, res);
        });

        // CFileSys::Read (0x0042F6C8): $a0=cFileSysPtr, $a1=readReqPtr
        RuntimeDispatcher.RegisterHook(0x0042F6C8u, (ctx, ram) =>
        {
            int bytesRead = Dvd.ReadEaFileSysSlot(ram, ctx->GetU32(4), ctx->GetU32(5));
            ctx->SetS32(2, bytesRead);
        });

        // 8. CAptSystem::~CAptSystem (0x003064A0) shutdown order fix:
        // Retail SLUS_216.64 calls AptEngine::DestroyAllocators (0x00361460) at 0x00306500 BEFORE
        // AptEngine::Shutdown (0x003611D8) at 0x00306578, causing a use-after-free of *(0x0052991C) / *(0x00529918).
        // Defer 0x00361460 while g_bAptInitialized (*(0x0052994C)) != 0, and invoke it right after 0x003611D8 completes.
        RuntimeDispatcher.RegisterHook(0x00361460u, (ctx, ram) =>
        {
            if (Ps2Memory.Read32(ctx, ram, 0x0052994Cu) != 0u)
                return;

            RuntimeDispatcher.Call(0x00361460u, ctx, ram);
            Ps2Memory.Write32(ctx, ram, 0x00529918u, 0u);
            Ps2Memory.Write32(ctx, ram, 0x0052991Cu, 0u);
        });

        RuntimeDispatcher.RegisterHook(0x003611D8u, (ctx, ram) =>
        {
            RuntimeDispatcher.Call(0x003611D8u, ctx, ram);
            if (Ps2Memory.Read32(ctx, ram, 0x0052991Cu) != 0u || Ps2Memory.Read32(ctx, ram, 0x00529918u) != 0u)
            {
                RuntimeDispatcher.Call(0x00361460u, ctx, ram);
                Ps2Memory.Write32(ctx, ram, 0x00529918u, 0u);
                Ps2Memory.Write32(ctx, ram, 0x0052991Cu, 0u);
            }
        });

        // 9. Ps2ControllerUpdate::SyncQueue (0x00417A88), Audio::WaitQueueEmpty (0x00431D40),
        // and MessageThread::SyncQueue (0x003E3DA0):
        // Under cooperative single-threaded execution, drain all pending commands in the controller
        // command queue (0x00594DF8 + 112) synchronously via 0x00417BD0, mark the audio command ring
        // buffer (+1920) read index equal to write index, and return immediately from MessageThread::SyncQueue.
        RuntimeDispatcher.RegisterHook(0x00417A88u, (ctx, ram) =>
        {
            const uint queueAddr = 0x00594DF8u + 112u;
            int guard = 0;
            while (guard++ < 64 && Ps2Memory.Read32(ctx, ram, queueAddr + 40u) != Ps2Memory.Read32(ctx, ram, queueAddr + 44u))
            {
                ctx->SetU32(4, 0x00594DF8u);
                RuntimeDispatcher.Call(0x00417BD0u, ctx, ram);
            }
        });

        RuntimeDispatcher.RegisterHook(0x00431D40u, (ctx, ram) =>
        {
            uint audioThis = ctx->GetU32(4);
            if (audioThis != 0u)
            {
                uint ringBuf = audioThis + 1920u;
                uint writeIdx = Ps2Memory.Read32(ctx, ram, ringBuf + 40u);
                Ps2Memory.Write32(ctx, ram, ringBuf + 44u, writeIdx);
            }
            RuntimeDispatcher.Call(0x00431D40u, ctx, ram);
        });

        RuntimeDispatcher.RegisterHook(0x003E3DA0u, (ctx, ram) =>
        {
        });

        // EA::Thread::ThreadSleep / Yield (0x003DF2F0) called by Resource Loader (0x00452378) after each command:
        RuntimeDispatcher.RegisterHook(0x003DF2F0u, (ctx, ram) =>
        {
            Thread.Yield();
        });
    }

    /// <summary>
    /// Pumps the PS2 controller subsystem (PadManager::Update, PadManager::Read, and event queue).
    /// Called per frame to step DMA buffers and ensure PadDevice[0] offset 576 is updated to 1
    /// so the engine unpacks pad state and marks the controller connected.
    /// </summary>
    public void PumpPadSubsystem(CpuContext* ctx, byte* ram)
    {
        if (ctx == null || ram == null)
            return;

        // Ensure DMA ring buffers are fresh
        Pad.StepFrame(ram);

        // Verify PadManager and PadDevice are allocated
        uint pad0 = Ps2Memory.Read32(ctx, ram, 0x0050A758u);
        if (pad0 == 0)
            return;

        // Ensure PadDevice[0] and PadDevice[1] are flagged connected (field_4 |= 2) and have data ready (field_576 = 1)
        Ps2Memory.Write32(ctx, ram, pad0 + 4u, Ps2Memory.Read32(ctx, ram, pad0 + 4u) | 2u);
        Ps2Memory.Write32(ctx, ram, pad0 + 576u, 1u);

        uint padArray = Ps2Memory.Read32(ctx, ram, 0x004A067Cu);
        if (padArray != 0)
        {
            uint pad1 = Ps2Memory.Read32(ctx, ram, padArray + 4u);
            if (pad1 != 0 && pad1 != pad0)
            {
                Ps2Memory.Write32(ctx, ram, pad1 + 4u, Ps2Memory.Read32(ctx, ram, pad1 + 4u) | 2u);
                Ps2Memory.Write32(ctx, ram, pad1 + 576u, 1u);
            }
        }

        CpuContext saved = *ctx;
        try
        {
            // 1. PadManager::Update (0x00432C48)
            ctx->SetU32(4, 0x00594DF8u);
            RuntimeDispatcher.Call(0x00432C48u, ctx, ram);

            // Re-assert connected flags in case PadManager::Update modified them
            Ps2Memory.Write32(ctx, ram, pad0 + 4u, Ps2Memory.Read32(ctx, ram, pad0 + 4u) | 2u);
            Ps2Memory.Write32(ctx, ram, pad0 + 576u, 1u);
            if (padArray != 0)
            {
                uint pad1 = Ps2Memory.Read32(ctx, ram, padArray + 4u);
                if (pad1 != 0 && pad1 != pad0)
                {
                    Ps2Memory.Write32(ctx, ram, pad1 + 4u, Ps2Memory.Read32(ctx, ram, pad1 + 4u) | 2u);
                    Ps2Memory.Write32(ctx, ram, pad1 + 576u, 1u);
                }
            }

            // 2. PadManager::Read (0x00417940) -> invokes scePadRead and unpacks pad state
            ctx->SetU32(4, 0x00594DF8u);
            RuntimeDispatcher.Call(0x00417940u, ctx, ram);

            // 3. PadManager event processor (0x00417BD0) — drain all queued commands
            const uint queueAddr = 0x00594DF8u + 112u;
            int guard = 0;
            while (guard++ < 64 && Ps2Memory.Read32(ctx, ram, queueAddr + 40u) != Ps2Memory.Read32(ctx, ram, queueAddr + 44u))
            {
                ctx->SetU32(4, 0x00594DF8u);
                RuntimeDispatcher.Call(0x00417BD0u, ctx, ram);
            }
        }
        finally
        {
            *ctx = saved;
        }
    }

    public static void LoadElfSegmentsToRam(string elfPath, byte* ram)
    {
        using var fs = new FileStream(elfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> ehdr = stackalloc byte[52];
        if (fs.Read(ehdr) < 52)
            throw new InvalidDataException("Invalid ELF header.");

        uint phOff = BinaryPrimitives.ReadUInt32LittleEndian(ehdr[28..32]);
        ushort phEntSize = BinaryPrimitives.ReadUInt16LittleEndian(ehdr[42..44]);
        ushort phNum = BinaryPrimitives.ReadUInt16LittleEndian(ehdr[44..46]);

        Span<byte> phdr = stackalloc byte[32];
        for (int i = 0; i < phNum; i++)
        {
            fs.Seek(phOff + i * phEntSize, SeekOrigin.Begin);
            if (fs.Read(phdr) < 32) break;

            uint pType = BinaryPrimitives.ReadUInt32LittleEndian(phdr[0..4]);
            if (pType != 1u) // PT_LOAD = 1
                continue;

            uint pOffset = BinaryPrimitives.ReadUInt32LittleEndian(phdr[4..8]);
            uint pVAddr = BinaryPrimitives.ReadUInt32LittleEndian(phdr[8..12]);
            uint pFileSz = BinaryPrimitives.ReadUInt32LittleEndian(phdr[16..20]);
            uint pMemSz = BinaryPrimitives.ReadUInt32LittleEndian(phdr[20..24]);

            uint physAddr = pVAddr & 0x01FFFFFFu;
            if (physAddr + pMemSz > CpuContext.RamSize)
                continue;

            new Span<byte>(ram + physAddr, (int)pMemSz).Clear();
            if (pFileSz > 0)
            {
                fs.Seek(pOffset, SeekOrigin.Begin);
                var dest = new Span<byte>(ram + physAddr, (int)pFileSz);
                int readTotal = 0;
                while (readTotal < (int)pFileSz)
                {
                    int r = fs.Read(dest[readTotal..]);
                    if (r <= 0) break;
                    readTotal += r;
                }
            }
        }
    }

    public void Dispose()
    {
        Kernel.ShutdownThreads();
        Dvd.Dispose();
        MemoryCard.Dispose();
        Audio.Dispose();
    }
}
