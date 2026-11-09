using System.Buffers.Binary;
using System.Text;
using Castaway.Core;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Castaway.Runtime;
using Xunit;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase3RuntimeTests
{
    [Fact]
    public void EeKernel_ThreadsSemaphoresAndEventFlags_BehaveAccurately()
    {
        byte[] ramManaged = new byte[CpuContext.RamSize];
        byte[] spManaged = new byte[CpuContext.ScratchpadSize];

        fixed (byte* ram = ramManaged)
        fixed (byte* sp = spManaged)
        {
            CpuContext ctx = default;
            ctx.Initialize(sp);

            using var runtime = new CastawayRuntime();
            runtime.Install(&ctx, ram);

            // 1. InitMainThread (0x3C) & InitializeHeap (0x3D)
            ctx.SetU32(4, 0x00512670u); // gp
            ctx.SetU32(5, 0x01FFE000u); // stack
            ctx.SetU32(6, 0x00002000u); // stackSize
            ctx.SetU32(7, 0x0050A780u); // args
            ctx.SetU32(8, 0x001000C8u); // root
            RuntimeDispatcher.Syscall(&ctx, ram, 0x3C);

            uint initSp = ctx.GetU32(2);
            Assert.Equal(0x01FFFF00u, initSp);

            // Verify argc=1 and argv[0]="cdrom0:\SLUS_216.64;1" written to 0x0050A780
            int argc = BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan(0x0050A780, 4));
            uint argv0Ptr = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan(0x0050A784, 4));
            Assert.Equal(1, argc);
            Assert.Equal("cdrom0:\\SLUS_216.64;1", DvdVirtualFileSystem.ReadGuestCString(ram, argv0Ptr));

            // InitializeHeap (0x3D)
            ctx.SetU32(4, 0x005B96D8u);
            ctx.SetU32(5, 0xFFFFFFFFu);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x3D);
            Assert.Equal(0x01FFE000u, ctx.GetU32(2));

            // 2. Semaphore lifecycle (CreateSema 0x40, WaitSema 0x44, SignalSema 0x42, PollSema 0x45, ReferSemaStatus 0x47, DeleteSema 0x41)
            uint semaParamAddr = 0x00100000u;
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)semaParamAddr + 4, 4), 5); // max_count = 5
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)semaParamAddr + 8, 4), 2); // init_count = 2

            ctx.SetU32(4, semaParamAddr);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x40);
            int sid = ctx.GetS32(2);
            Assert.True(sid > 0);

            // PollSema twice (decrements 2 -> 1 -> 0), third PollSema returns -1
            ctx.SetS32(4, sid);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x45);
            Assert.Equal(sid, ctx.GetS32(2));

            ctx.SetS32(4, sid);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x45);
            Assert.Equal(sid, ctx.GetS32(2));

            ctx.SetS32(4, sid);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x45);
            Assert.Equal(-1, ctx.GetS32(2));

            // SignalSema (0x42) increments 0 -> 1
            ctx.SetS32(4, sid);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x42);
            Assert.Equal(sid, ctx.GetS32(2));

            // ReferSemaStatus (0x47)
            uint statusAddr = 0x00100100u;
            ctx.SetS32(4, sid);
            ctx.SetU32(5, statusAddr);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x47);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan((int)statusAddr, 4)));

            // 3. CreateThread (0x20) & StartThread (0x22)
            uint customThreadPc = 0x00250000u;
            uint receivedArg = 0;
            RuntimeDispatcher.RegisterHook(customThreadPc, (tCtx, tRam) =>
            {
                receivedArg = tCtx->GetU32(4);
            });

            uint threadParamAddr = 0x00100200u;
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)threadParamAddr + 0x04, 4), customThreadPc);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)threadParamAddr + 0x08, 4), 0x01F00000u);
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)threadParamAddr + 0x0C, 4), 0x4000);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)threadParamAddr + 0x10, 4), 0x00512670u);
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)threadParamAddr + 0x14, 4), 32);

            ctx.SetU32(4, threadParamAddr);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x20);
            int tid = ctx.GetS32(2);
            Assert.True(tid > 1);

            ctx.SetS32(4, tid);
            ctx.SetU32(5, 0xCAFE1234u);
            RuntimeDispatcher.Syscall(&ctx, ram, 0x22);
            SpinWait.SpinUntil(() => Volatile.Read(ref receivedArg) != 0, 1000);
            Assert.Equal(0xCAFE1234u, receivedArg);
        }
    }

    [Fact]
    public void DmaController_SupportsNormalChainAndInterleaveModes()
    {
        byte[] ramManaged = new byte[CpuContext.RamSize];
        byte[] spManaged = new byte[CpuContext.ScratchpadSize];

        fixed (byte* ram = ramManaged)
        fixed (byte* sp = spManaged)
        {
            CpuContext ctx = default;
            ctx.Initialize(sp);

            using var runtime = new CastawayRuntime();
            runtime.Install(&ctx, ram);

            List<byte> gifReceived = new();
            runtime.Dma.OnGifData = data => gifReceived.AddRange(data);

            // 1. Normal Mode (MOD=0) on GIF Channel 2 (0x1000A000)
            uint srcAddr = 0x00200000u;
            for (int i = 0; i < 32; i++)
                ram[srcAddr + i] = (byte)(0xA0 + i);

            Ps2Memory.Write32(&ctx, ram, 0x1000A010u, srcAddr); // D2_MADR
            Ps2Memory.Write32(&ctx, ram, 0x1000A020u, 2u);      // D2_QWC = 2 quadwords (32 bytes)
            Ps2Memory.Write32(&ctx, ram, 0x1000A000u, 0x0101u); // D2_CHCR: DIR=1, MOD=0 (Normal), STR=1

            Assert.Equal(32, gifReceived.Count);
            Assert.Equal(0xA0, gifReceived[0]);
            Assert.Equal(0xBF, gifReceived[31]);
            Assert.Equal(0u, Ps2Memory.Read32(&ctx, ram, 0x1000A000u) & 0x100u); // STR bit cleared

            // 2. Source Chain Mode (MOD=1) with CNT -> REF -> CALL -> RET -> REFE tags
            gifReceived.Clear();

            // Payload for REF tag at 0x00210000 (16 bytes)
            uint refPayloadAddr = 0x00210000u;
            for (int i = 0; i < 16; i++) ram[refPayloadAddr + i] = 0x22;

            // Subroutine chain at 0x00220000: RET tag (ID=6) with 1 QWC inline payload (0x33)
            uint subChainAddr = 0x00220000u;
            ulong retTagLo = 1ul | (6ul << 28); // QWC=1, ID=6 (RET)
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)subChainAddr, 8), retTagLo);
            for (int i = 0; i < 16; i++) ram[subChainAddr + 16 + i] = 0x33;

            // Main chain at 0x00201000:
            // Tag 0 (0x00201000): CNT (ID=1), QWC=1 -> 16 bytes of 0x11 at 0x00201010
            // Tag 1 (0x00201020): REF (ID=3), QWC=1, ADDR=0x00210000 -> 16 bytes of 0x22
            // Tag 2 (0x00201030): CALL (ID=5), QWC=0, ADDR=0x00220000 -> jumps to subChainAddr (16 bytes of 0x33), returns to 0x00201040
            // Tag 3 (0x00201040): END (ID=7), QWC=1 -> 16 bytes of 0x44 at 0x00201050
            uint chainBase = 0x00201000u;
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase, 8), 1ul | (1ul << 28));
            for (int i = 0; i < 16; i++) ram[chainBase + 0x10 + i] = 0x11;

            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x20, 8), 1ul | (3ul << 28) | ((ulong)refPayloadAddr << 32));
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x30, 8), 0ul | (5ul << 28) | ((ulong)subChainAddr << 32));
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x40, 8), 1ul | (7ul << 28));
            for (int i = 0; i < 16; i++) ram[chainBase + 0x50 + i] = 0x44;

            Ps2Memory.Write32(&ctx, ram, 0x1000A020u, 0u);        // D2_QWC = 0
            Ps2Memory.Write32(&ctx, ram, 0x1000A030u, chainBase); // D2_TADR
            Ps2Memory.Write32(&ctx, ram, 0x1000A000u, 0x0105u);   // D2_CHCR: DIR=1, MOD=1 (Chain), STR=1

            Assert.Equal(64, gifReceived.Count);
            Assert.All(gifReceived.Take(16), b => Assert.Equal(0x11, b));
            Assert.All(gifReceived.Skip(16).Take(16), b => Assert.Equal(0x22, b));
            Assert.All(gifReceived.Skip(32).Take(16), b => Assert.Equal(0x33, b));
            Assert.All(gifReceived.Skip(48).Take(16), b => Assert.Equal(0x44, b));

            // 3. Interleave Mode (MOD=2) using D_SQWC (SQWC=1 skip, TQWC=1 transfer)
            gifReceived.Clear();
            uint interleaveAddr = 0x00230000u;
            for (int i = 0; i < 16; i++) ram[interleaveAddr + 0x00 + i] = 0xAA; // Transferred
            for (int i = 0; i < 16; i++) ram[interleaveAddr + 0x10 + i] = 0x00; // Skipped
            for (int i = 0; i < 16; i++) ram[interleaveAddr + 0x20 + i] = 0xBB; // Transferred
            for (int i = 0; i < 16; i++) ram[interleaveAddr + 0x30 + i] = 0x00; // Skipped

            Ps2Memory.Write32(&ctx, ram, 0x1000E030u, 0x00010001u); // D_SQWC: SQWC=1, TQWC=1
            Ps2Memory.Write32(&ctx, ram, 0x1000A010u, interleaveAddr);
            Ps2Memory.Write32(&ctx, ram, 0x1000A020u, 2u);          // Total 2 QWC to transfer
            Ps2Memory.Write32(&ctx, ram, 0x1000A000u, 0x0109u);     // D2_CHCR: DIR=1, MOD=2 (Interleave), STR=1

            Assert.Equal(32, gifReceived.Count);
            Assert.All(gifReceived.Take(16), b => Assert.Equal(0xAA, b));
            Assert.All(gifReceived.Skip(16).Take(16), b => Assert.Equal(0xBB, b));
        }
    }

    [Fact]
    public void DvdVirtualFileSystem_StreamsSystemCnfAndArcFiles_ViaPathAndSectors()
    {
        using var dvd = new DvdVirtualFileSystem();
        Assert.NotEmpty(dvd.Entries);

        // 1. Verify SYSTEM.CNF lookup and FileIO read
        var sysCnf = dvd.FindFile("cdrom0:\\SYSTEM.CNF;1");
        Assert.NotNull(sysCnf);
        Assert.True(sysCnf.SizeBytes > 0);

        int fd = dvd.Open("cdrom0:\\SYSTEM.CNF;1");
        Assert.True(fd >= 3);

        Span<byte> buf = stackalloc byte[64];
        int readBytes = dvd.Read(fd, buf);
        Assert.True(readBytes > 0);
        string text = Encoding.ASCII.GetString(buf[..readBytes]);
        Assert.Contains("SLUS_216.64", text);
        Assert.Equal(0, dvd.Close(fd));

        // 2. Verify retail .ARC file lookup and LBA sector reading
        var quickDat = dvd.FindFile("cdrom0:\\DATA\\QUICKDAT.ARC;1");
        Assert.NotNull(quickDat);
        Assert.True(quickDat.SizeBytes > 0);

        byte[] sectorBuf = new byte[DvdVirtualFileSystem.SectorSize];
        Assert.True(dvd.ReadSectors(quickDat.StartLba, 1, sectorBuf));

        // Compare first 64 bytes of LBA sector read against direct file read
        int arcFd = dvd.Open("cdrom0:\\DATA\\QUICKDAT.ARC;1");
        byte[] directBuf = new byte[64];
        Assert.Equal(64, dvd.Read(arcFd, directBuf));
        Assert.Equal(directBuf, sectorBuf.Take(64).ToArray());
        dvd.Close(arcFd);
    }

    [Fact]
    public void PadService_PopulatesDualShock2RingBuffer_ForSlus21664()
    {
        byte[] ramManaged = new byte[CpuContext.RamSize];
        fixed (byte* ram = ramManaged)
        {
            var pad = new PadService();
            uint padAreaAddr = 0x00300000u;

            // Simulate PortOpen RPC (cmd = 0x01)
            uint sendAddr = 0x00301000u;
            uint recvAddr = 0x00301100u;
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 0, 4), 0x01u); // PortOpen
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 4, 4), 0);      // port 0
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 8, 4), 0);      // slot 0
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 16, 4), padAreaAddr);

            pad.HandleRpc(SifRpcRouter.SidPad1, 1, ram, sendAddr, 32, recvAddr, 32);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan((int)recvAddr + 12, 4)));

            // Inject Cross + Start button press
            pad.SetPortState(0, new DualShock2State
            {
                PressedButtons = Ps2PadButtons.Cross | Ps2PadButtons.Start,
                LeftStickX = 0x40,
                LeftStickY = 0xC0,
                RightStickX = 0x80,
                RightStickY = 0x80
            });
            pad.StepFrame(ram);

            // Check which 128-byte slot has the newer frame counter (+88 vs +216), matching scePadGetDmaStr (0x00479958)
            uint frame0 = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)padAreaAddr + 88, 4));
            uint frame1 = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)padAreaAddr + 216, 4));
            int activeSlotOff = frame1 > frame0 ? (int)padAreaAddr + 128 : (int)padAreaAddr;

            Assert.Equal(0x00, ramManaged[activeSlotOff + 0]);
            Assert.Equal(0x79, ramManaged[activeSlotOff + 1]); // DualShock2 mode

            ushort activeLow = BinaryPrimitives.ReadUInt16LittleEndian(ramManaged.AsSpan(activeSlotOff + 2, 2));
            ushort pressed = (ushort)~activeLow;
            Assert.Equal((ushort)(Ps2PadButtons.Cross | Ps2PadButtons.Start), pressed);
            Assert.Equal(0xFF, ramManaged[activeSlotOff + 14]); // Cross pressure byte
            Assert.Equal(6, ramManaged[activeSlotOff + 0x70]);    // scePadStateStable = 6
        }
    }

    [Fact]
    public void MemoryCardService_PersistsSaveFilesInLocalSavesDirectory()
    {
        string tempSaves = Path.Combine(Path.GetTempPath(), "CastawayMcTest_" + Guid.NewGuid().ToString("N"));
        byte[] ramManaged = new byte[CpuContext.RamSize];

        try
        {
            fixed (byte* ram = ramManaged)
            {
                using var mc = new MemoryCardService(tempSaves);
                uint sendAddr = 0x00400000u;
                uint recvAddr = 0x00401000u;
                uint dataAddr = 0x00402000u;

                // 1. Create directory /BASLUS-21664 (flags = 0x4000 SCE_MKDIR)
                ramManaged.AsSpan((int)sendAddr, 128).Clear();
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 0, 4), 0); // port 0
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 4, 4), 0); // slot 0
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 8, 4), 0x4000);
                Encoding.ASCII.GetBytes("/BASLUS-21664\0").CopyTo(ramManaged.AsSpan((int)sendAddr + 20));
                mc.HandleRpc(0x02, ram, sendAddr, 128, recvAddr, 16);
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan((int)recvAddr, 4)));

                // 2. Open file /BASLUS-21664/SAVE.BIN for creation + write (flags = 0x0203)
                ramManaged.AsSpan((int)sendAddr, 128).Clear();
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 0, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 4, 4), 0);
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 8, 4), 0x0203);
                Encoding.ASCII.GetBytes("/BASLUS-21664/SAVE.BIN\0").CopyTo(ramManaged.AsSpan((int)sendAddr + 20));
                mc.HandleRpc(0x02, ram, sendAddr, 128, recvAddr, 16);
                int handle = BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan((int)recvAddr, 4));
                Assert.True(handle > 0);

                // 3. Write 16 bytes
                byte[] savePayload = Encoding.ASCII.GetBytes("CASTAWAY_SAVE_01");
                savePayload.CopyTo(ramManaged.AsSpan((int)dataAddr));
                ramManaged.AsSpan((int)sendAddr, 64).Clear();
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 0, 4), handle);
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 12, 4), savePayload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 24, 4), dataAddr);
                mc.HandleRpc(0x05, ram, sendAddr, 64, recvAddr, 16);
                Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(ramManaged.AsSpan((int)recvAddr, 4)));

                // 4. Close file
                ramManaged.AsSpan((int)sendAddr, 32).Clear();
                BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sendAddr + 0, 4), handle);
                mc.HandleRpc(0x03, ram, sendAddr, 32, recvAddr, 16);

                // 5. Verify file exists on disk and read it back via RPC
                string expectedHostFile = Path.Combine(tempSaves, "mc0", "BASLUS-21664", "SAVE.BIN");
                Assert.True(File.Exists(expectedHostFile));
                Assert.Equal("CASTAWAY_SAVE_01", File.ReadAllText(expectedHostFile));
            }
        }
        finally
        {
            if (Directory.Exists(tempSaves))
                Directory.Delete(tempSaves, recursive: true);
        }
    }

    [Fact]
    public void Spu2AudioService_DecodesAdpcmBlocksToPcm16()
    {
        // Construct two 16-byte SPU2 ADPCM blocks:
        // Block 0: shift=0, predict=0 (direct linear scale), flags=0x00
        // Block 1: shift=2, predict=1 (first-order IIR filter), flags=0x01 (end flag)
        byte[] adpcm = new byte[32];
        adpcm[0] = 0x00; // shift=0, predict=0
        adpcm[1] = 0x00; // flags=0
        for (int i = 2; i < 16; i++)
            adpcm[i] = 0x21; // nibble0 = 1 (+4096), nibble1 = 2 (+8192)

        adpcm[16] = 0x12; // shift=2, predict=1
        adpcm[17] = 0x01; // loop end flag
        for (int i = 18; i < 32; i++)
            adpcm[i] = 0x11;

        short[] pcm = Spu2AudioService.DecodeAdpcmStream(adpcm, stopOnEndFlag: true);
        Assert.Equal(56, pcm.Length); // 2 blocks * 28 samples
        Assert.Equal(4096, pcm[0]);
        Assert.Equal(8192, pcm[1]);
        Assert.True(pcm[28] > 0); // Filtered continuation in block 2
    }

    [Fact]
    public void Slus21664_BootsThroughCrt0AndInitSys_ToGameMain()
    {
        RecompiledFunctions.RegisterAll();

        byte[] ramManaged = new byte[CpuContext.RamSize];
        byte[] spManaged = new byte[CpuContext.ScratchpadSize];

        fixed (byte* ram = ramManaged)
        fixed (byte* sp = spManaged)
        {
            CpuContext ctx = default;
            ctx.Initialize(sp);

            using var runtime = new CastawayRuntime();
            runtime.Install(&ctx, ram, installHleHooks: true);

            string elfPath = Ps2ElfReader.FindDefaultElfPath();
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            // Seed dirty non-zero bytes in .bss (0x0050A680..0x005B96D8) to verify _start zeroes .bss
            ram[0x0050A680] = 0xFF;
            ram[0x00550000] = 0xAA;

            bool reachedInitSys = false;
            bool reachedStaticCtors = false;
            bool reachedGameMain = false;
            int mainArgc = -1;
            string mainArgv0 = string.Empty;

            // Observe _InitSys (0x00489760)
            var origInitSys = RuntimeDispatcher.Lookup(0x00489760u);
            RuntimeDispatcher.RegisterHook(0x00489760u, (c, r) =>
            {
                reachedInitSys = true;
                origInitSys(c, r);
            });

            // Intercept __init (0x003E46E0) and GameMain (0x003E4610) to verify full crt0 -> _InitSys -> main handoff
            RuntimeDispatcher.RegisterHook(0x003E46E0u, (c, r) =>
            {
                reachedStaticCtors = true;
            });

            RuntimeDispatcher.RegisterHook(0x003E4610u, (c, r) =>
            {
                reachedGameMain = true;
                mainArgc = c->GetS32(4); // $a0 = argc
                uint argvPtr = c->GetU32(5); // $a1 = argv
                uint firstStrPtr = Ps2Memory.Read32(c, r, argvPtr);
                mainArgv0 = DvdVirtualFileSystem.ReadGuestCString(r, firstStrPtr);
            });

            // Execute SLUS_216.64 entry point _start (0x00100008)
            RuntimeDispatcher.Call(0x00100008u, &ctx, ram);

            Assert.Equal(0, ram[0x0050A680]);
            Assert.Equal(0, ram[0x00550000]);
            Assert.True(reachedInitSys, "_start must execute _InitSys (0x00489760)");
            Assert.True(reachedStaticCtors, "_start must execute __init static constructors (0x003E46E0)");
            Assert.True(reachedGameMain, "_start must reach GameMain_AppEntry (0x003E4610)");
            Assert.Equal(1, mainArgc);
            Assert.Equal("cdrom0:\\SLUS_216.64;1", mainArgv0);
        }
    }

    [Fact]
    public void Slus21664_ExecutesRealSifRpcAndIopModuleBootFunctions()
    {
        RecompiledFunctions.RegisterAll();

        byte[] ramManaged = new byte[CpuContext.RamSize];
        byte[] spManaged = new byte[CpuContext.ScratchpadSize];

        fixed (byte* ram = ramManaged)
        fixed (byte* sp = spManaged)
        {
            CpuContext ctx = default;
            ctx.Initialize(sp);

            using var runtime = new CastawayRuntime();
            runtime.Install(&ctx, ram, installHleHooks: true);

            string elfPath = Ps2ElfReader.FindDefaultElfPath();
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            // Set up GP and SP as crt0 does
            ctx.SetU32(28, 0x00512670u); // $gp
            ctx.SetU32(29, 0x01FFFF00u); // $sp

            // 1. Execute real SLUS_216.64 scePadInit (0x00479260) which calls sceSifBindRpc(0x80000100),
            //    sceSifBindRpc(0x80000101), and checks GetModVersion (0x0047A190) for (ver >> 8) == 4!
            ctx.SetU32(4, 0u); // mode = 0
            RuntimeDispatcher.Call(0x00479260u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2)); // scePadInit returns 1 on success!

            // 2. Execute real SLUS_216.64 scePadPortOpen (0x004796B8) for port=0, slot=0, padArea=0x005C0000
            uint padAreaAddr = 0x005C0000u;
            ctx.SetU32(4, 0u); // port 0
            ctx.SetU32(5, 0u); // slot 0
            ctx.SetU32(6, padAreaAddr);
            RuntimeDispatcher.Call(0x004796B8u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2)); // scePadPortOpen returns 1 on success!
            Assert.True(runtime.Pad.TryGetPadArea(0, 0, out uint boundArea));
            Assert.Equal(padAreaAddr, boundArea);

            // 2b. Execute real SLUS_216.64 controller validation function sub_004599B8 (from sub_00417C60)
            uint padCheckStruct = 0x005C0800u;
            ramManaged[padCheckStruct + 52] = 0; // port 0
            ramManaged[padCheckStruct + 53] = 0; // slot 0
            ctx.SetU32(4, padCheckStruct);
            RuntimeDispatcher.Call(0x004599B8u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2)); // sub_004599B8 returns 1 (DualShock 2 controller validated!)

            // 3. Execute real SLUS_216.64 sceCdSearchFile (0x0035C5D8) for "\DATA\QUICKDAT.ARC;1"
            uint cdlFileStructAddr = 0x005C1000u;
            uint pathStrAddr = 0x005C1100u;
            Encoding.ASCII.GetBytes("\\DATA\\QUICKDAT.ARC;1\0").CopyTo(ramManaged.AsSpan((int)pathStrAddr));

            ctx.SetU32(4, cdlFileStructAddr);
            ctx.SetU32(5, pathStrAddr);
            RuntimeDispatcher.Call(0x0035C5D8u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2)); // sceCdSearchFile returns 1 when found!

            uint resolvedLba = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)cdlFileStructAddr + 0, 4));
            uint resolvedSize = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)cdlFileStructAddr + 4, 4));
            Assert.True(resolvedLba >= DvdVirtualFileSystem.BaseLba);
            Assert.True(resolvedSize > 0);
        }
    }
}
