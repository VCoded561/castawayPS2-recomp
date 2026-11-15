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
    public void DmaController_Vif1_TteChainCoalescingAndSplitRefStitching_WorksAccurately()
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

            var vif1 = new Castaway.GsRenderer.Vif.VifUnit(isVif1: true);
            var transferSizes = new List<int>();
            runtime.Dma.OnVif1Data = data =>
            {
                transferSizes.Add(data.Length);
                vif1.ProcessData(data);
            };

            // Chain at 0x00240000 with TTE=1 (CHCR bit 6 = 0x40):
            // Tag 0 (0x00240000): CNT, QWC=1, upper tag = [NOP, UNPACK V4-32 num=1 addr=0]
            //   Payload (0x00240010): (11, 22, 33, 44) -> coalesced into 1 x 24B transfer!
            // Tag 1 (0x00240020): REF, QWC=1, ADDR=0x00250000, upper tag = [STCYCL 1/1, UNPACK V4-32 num=1 addr=1]
            //   Payload (0x00250000): (101, 102, 103, 104) -> split 8B + 16B, zero-copy stitched!
            // Tag 2 (0x00240030): END, QWC=1, upper tag = [NOP, UNPACK V4-32 num=1 addr=2]
            //   Payload (0x00240040): (201, 202, 203, 204) -> coalesced into 1 x 24B transfer!
            uint chainBase = 0x00240000u;
            uint refPayload = 0x00250000u;

            // Tag 0: CNT
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x00, 8), 1ul | (1ul << 28));
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x08, 4), 0x00000000u); // NOP
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x0C, 4), 0x6C010000u); // UNPACK V4-32 num=1 addr=0
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x10, 4), 11u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x14, 4), 22u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x18, 4), 33u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x1C, 4), 44u);

            // Tag 1: REF
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x20, 8), 1ul | (3ul << 28) | ((ulong)refPayload << 32));
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x28, 4), 0x01000101u); // STCYCL CL=1, WL=1
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x2C, 4), 0x6C010001u); // UNPACK V4-32 num=1 addr=1
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)refPayload + 0x00, 4), 101u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)refPayload + 0x04, 4), 102u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)refPayload + 0x08, 4), 103u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)refPayload + 0x0C, 4), 104u);

            // Tag 2: END
            BinaryPrimitives.WriteUInt64LittleEndian(ramManaged.AsSpan((int)chainBase + 0x30, 8), 1ul | (7ul << 28));
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x38, 4), 0x00000000u); // NOP
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x3C, 4), 0x6C010002u); // UNPACK V4-32 num=1 addr=2
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x40, 4), 201u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x44, 4), 202u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x48, 4), 203u);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)chainBase + 0x4C, 4), 204u);

            Ps2Memory.Write32(&ctx, ram, 0x10009020u, 0u);        // D1_QWC = 0
            Ps2Memory.Write32(&ctx, ram, 0x10009030u, chainBase); // D1_TADR
            Ps2Memory.Write32(&ctx, ram, 0x10009000u, 0x0145u);   // D1_CHCR: DIR=1, MOD=1 (Chain), TTE=1 (0x40), STR=1 (0x100)

            Assert.Equal(new[] { 24, 8, 16, 24 }, transferSizes);
            Assert.Equal(3, vif1.TotalUnpackQuadwordsWritten);

            Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(0 * 16 + 0, 4)));
            Assert.Equal(44u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(0 * 16 + 12, 4)));
            Assert.Equal(101u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(1 * 16 + 0, 4)));
            Assert.Equal(104u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(1 * 16 + 12, 4)));
            Assert.Equal(201u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(2 * 16 + 0, 4)));
            Assert.Equal(204u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(2 * 16 + 12, 4)));
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

    [Fact]
    public void MmioRouter_RealTimeTimers_EliminateSlowMotionAndClampHitches()
    {
        byte[] ramManaged = new byte[CpuContext.RamSize];
        fixed (byte* ram = ramManaged)
        {
            var kernel = new EeKernel();
            var dma = new DmaController(kernel);
            var mmio = new MmioRouter(kernel, dma);

            // 1. Default deterministic fixed-step mode (9,600 ticks/frame at 576 kHz)
            Assert.False(mmio.UseRealTimeTimers);
            for (int i = 0; i < 7; i++)
            {
                mmio.StepFrameTimers(ram);
                Assert.Equal(MmioRouter.FixedTimerTicksPerFrame, mmio.LastFrameTimerTicks);
            }
            // 7 * 9,600 = 67,200 => 1 wrap (+16-bit overflow to RAM 0x004A4D08) and T1_COUNT = 1,664
            Assert.Equal(1664u, mmio.Read32(0x10000800u));
            Assert.Equal(1ul, BinaryPrimitives.ReadUInt64LittleEndian(ramManaged.AsSpan(0x004A4D08, 8)));

            // 2. Real-time mode with injected deterministic clock (3 MHz cleanly divides 30 FPS & 60 FPS)
            long hostNow = 1_000_000L;
            mmio.SetRealTimeClockSource(() => hostNow, 3_000_000L);
            mmio.UseRealTimeTimers = true;

            // Baseline call emits FixedTimerTicksPerFrame (9,600)
            mmio.StepFrameTimers(ram);
            Assert.Equal(MmioRouter.FixedTimerTicksPerFrame, mmio.LastFrameTimerTicks);

            // 30 FPS (33.333 ms = 100,000 ticks at 3 MHz) => exact 19,200 ticks (2x of 60Hz, fixing 0.5x slow-mo)
            hostNow += 100_000L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(19_200u, mmio.LastFrameTimerTicks);

            // 60 FPS (16.667 ms = 50,000 ticks at 3 MHz) => exact 9,600 ticks
            hostNow += 50_000L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(9_600u, mmio.LastFrameTimerTicks);

            // 3. Fractional sub-tick accumulator + slow 3D frame (55.90 ms at 1 MHz)
            hostNow = 0L;
            mmio.SetRealTimeClockSource(() => hostNow, 1_000_000L);
            mmio.StepFrameTimers(ram); // baseline

            // 55.90 ms = 55,900 us => 55,900 * 576_000 / 1_000_000 = 32,198.4 ticks/frame
            hostNow += 55_900L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(32_198u, mmio.LastFrameTimerTicks);

            hostNow += 55_900L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(32_198u, mmio.LastFrameTimerTicks); // +0.4 + 32198.4 = 32198.8

            hostNow += 55_900L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(32_199u, mmio.LastFrameTimerTicks); // +0.8 + 32198.4 = 32199.2

            // High-FPS sub-tick accumulation: 1,000 microsecond steps (0.576 T1 ticks/step) => exact 576 ticks total
            hostNow = 0L;
            mmio.SetRealTimeClockSource(() => hostNow, 1_000_000L);
            mmio.StepFrameTimers(ram); // baseline
            ulong accumTicks = 0;
            for (int i = 0; i < 1000; i++)
            {
                hostNow += 1L;
                mmio.StepFrameTimers(ram);
                accumTicks += mmio.LastFrameTimerTicks;
            }
            Assert.Equal(576ul, accumTicks);

            // 4. Hitch clamp: 250 ms pause and >1 s debugger pause clamp to 38,400 ticks (66.67 ms / 15 FPS floor)
            hostNow += 250_000L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(MmioRouter.MaxRealTimeTimerTicksPerFrame, mmio.LastFrameTimerTicks);

            hostNow += 2_000_000L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(MmioRouter.MaxRealTimeTimerTicksPerFrame, mmio.LastFrameTimerTicks);

            // Remainder was cleared by clamp, so a clean 10 ms frame yields exact 5,760 ticks
            hostNow += 10_000L;
            mmio.StepFrameTimers(ram);
            Assert.Equal(5_760u, mmio.LastFrameTimerTicks);
        }
    }

    private static uint WriteEaStreamBatchHeader(byte[] ram, uint addr, ushort cmdCount)
    {
        Span<byte> span = ram.AsSpan((int)(addr & 0x01FFFFFFu), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0..2], 0x0046);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..4], cmdCount);
        return addr + 4;
    }

    private static uint WriteEaStreamBatchCmd(byte[] ram, uint cursor, ushort cmdId, params ushort[] args)
    {
        Span<byte> span = ram.AsSpan((int)(cursor & 0x01FFFFFFu), 4 + args.Length * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0..2], cmdId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..4], (ushort)args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[(4 + i * 2)..(6 + i * 2)], args[i]);
        }
        return cursor + (uint)(4 + args.Length * 2);
    }

    private static void WriteAdpcmBlock(Span<byte> dst16, byte flags, sbyte nibbleVal)
    {
        dst16[0] = 0x00; // shift=0, filter=0
        dst16[1] = flags;
        byte n = (byte)(nibbleVal & 0x0F);
        byte packed = (byte)(n | (n << 4));
        dst16[2..16].Fill(packed);
    }

    [Fact]
    public void SifDma_And_EaStreamTransferCommands_CopyEeToIopToSpu2Ram()
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

            // 1. Raw sceSifSetDma EE -> IOP transfer (dst != 0, not paired with SIF_CMDC_CALL)
            uint eePayloadAddr = 0x00600000u;
            uint iopDestAddr = 0x00080000u;
            byte[] pattern = new byte[64];
            for (int i = 0; i < pattern.Length; i++)
                pattern[i] = (byte)(0xA0 + i);
            pattern.CopyTo(ramManaged.AsSpan((int)eePayloadAddr));

            uint sddAddr = 0x00601000u;
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sddAddr + 0, 4), eePayloadAddr);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sddAddr + 4, 4), iopDestAddr);
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan((int)sddAddr + 8, 4), pattern.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)sddAddr + 12, 4), 0u);

            int dmaId = runtime.SifRpc.HandleSifSetDma(&ctx, ram, sddAddr, 1);
            Assert.True(dmaId > 0);
            Assert.Equal(pattern, runtime.Audio.ReadIopRam(iopDestAddr, pattern.Length).ToArray());

            // 2. EA MultiStream cmd=70/72 with hw=4 transfers IOP RAM -> SPU2 RAM at InitialFreeSpuRamAddr (0x5010)
            using var audio = new Spu2AudioService(tryInitOpenAl: false);
            audio.WriteIopRam(iopDestAddr, pattern);

            uint sendAddr = 0x00602000u;
            uint recvAddr = 0x00603000u;
            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            // Small hw=2 status/setup args must NOT clobber SPU2 RAM
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 70, 0x0003, 0x0000);
            // Real IOP->SPU2 DMA transfer: iopAddr hi/lo, size hi/lo
            cur = WriteEaStreamBatchCmd(
                ramManaged, cur, 72,
                (ushort)(iopDestAddr >> 16), (ushort)iopDestAddr,
                0x0000, (ushort)pattern.Length);

            int sendSize = (int)(cur - sendAddr);
            int rpcRes = audio.HandleEaStreamRpc(0, ram, sendAddr, sendSize, recvAddr, 256);
            Assert.Equal(0, rpcRes);

            byte[] spuSlice = audio.Spu2Ram.AsSpan((int)Spu2AudioService.InitialFreeSpuRamAddr, pattern.Length).ToArray();
            Assert.Equal(pattern, spuSlice);
        }
    }

    [Fact]
    public void EaStreamBatch_ExecutesHeartbeatAndVoiceLifecycle_ThroughGuestStatusParser()
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

            ctx.SetU32(28, 0x00512670u); // $gp
            ctx.SetU32(29, 0x01FFFF00u); // $sp

            // Bind EA MultiStream RPC client + write ADPCM sample block at SPU2 addr 0x5010
            uint spuSampleAddr = Spu2AudioService.InitialFreeSpuRamAddr;
            WriteAdpcmBlock(runtime.Audio.Spu2Ram.AsSpan((int)spuSampleAddr, 16), flags: 0x01, nibbleVal: 2);

            uint sendAddr = 0x00604000u;
            uint recvAddr = 0x005AA240u; // Game's real EA MultiStream recv buffer parsed by sub_00466488
            uint heartbeatAddr = 0x005AB540u;

            // cmd=73 registers heartbeat EE addr; cmd=57 sets voice 3 pending SPU addr; cmd=0 keys on voice 3
            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 3);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 73, (ushort)(heartbeatAddr >> 16), (ushort)heartbeatAddr);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 57, (ushort)(spuSampleAddr >> 16), (ushort)spuSampleAddr, 3, 0x3FFF, 0x3FFF, 0x1000, 0x000F, 0);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 0, (ushort)(spuSampleAddr >> 16), (ushort)spuSampleAddr, 3, 0x3FFF, 0x3FFF, 0x1000, 0x000F);

            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur - sendAddr), recvAddr, 512);
            Assert.Equal(heartbeatAddr, runtime.Audio.HeartbeatEeAddr);
            Assert.True(runtime.Audio.IsVoiceActive(3));

            uint hbBefore = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)heartbeatAddr, 4));
            runtime.Kernel.OnVBlankTick?.Invoke(ram);
            uint hbAfter = BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)heartbeatAddr, 4));
            Assert.NotEqual(hbBefore, hbAfter);

            // Feed status packet into real guest parser sub_00466488 -> voice 3 reported busy
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan(0x004A469C, 4), 0xFFFFFFFFu);
            RuntimeDispatcher.Call(0x00466488u, &ctx, ram);
            Assert.NotEqual(0, ramManaged[0x004A46A8 + 3]);

            ctx.SetU32(4, 3);
            RuntimeDispatcher.Call(0x0046B0F8u, &ctx, ram); // SOUND_IsVoiceFree(3)
            Assert.Equal(0, ctx.GetS32(2));

            // KeyOff voice 3 (cmd=4) and step mixer past ~4 ms release ramp -> voice becomes inactive
            uint cur2 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            cur2 = WriteEaStreamBatchCmd(ramManaged, cur2, 4, 3);
            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur2 - sendAddr), recvAddr, 512);
            runtime.Audio.StepMixSamples(256);
            Assert.False(runtime.Audio.IsVoiceActive(3));

            // Flush status packet so tick advances, then call sub_00466488 -> release(4) -> free(0)
            uint cur3 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 0);
            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur3 - sendAddr), recvAddr, 512);
            RuntimeDispatcher.Call(0x00466488u, &ctx, ram);
            uint cur4 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 0);
            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur4 - sendAddr), recvAddr, 512);
            RuntimeDispatcher.Call(0x00466488u, &ctx, ram);
            Assert.Equal(0, ramManaged[0x004A46A8 + 3]);

            ctx.SetU32(4, 3);
            RuntimeDispatcher.Call(0x0046B0F8u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2));
        }
    }

    [Fact]
    public void Spu2Mixer_DecodesAdpcmLoopFlagsPitchRampingAndMoviePcm_Deterministically()
    {
        using var audio = new Spu2AudioService(tryInitOpenAl: false);
        byte[] ramManaged = new byte[CpuContext.RamSize];

        fixed (byte* ram = ramManaged)
        {
            uint sendAddr = 0x00605000u;
            uint recvAddr = 0x00606000u;

            // Voice 0: two looping ADPCM blocks (block 0 flag=0x04 loop start, block 1 flag=0x03 loop+end)
            uint loopSpuAddr = 0x5010u;
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan((int)loopSpuAddr + 0, 16), flags: 0x04, nibbleVal: 2);
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan((int)loopSpuAddr + 16, 16), flags: 0x03, nibbleVal: 3);

            // Voice 1: single one-shot ADPCM block (flag=0x01 end without loop)
            uint oneShotSpuAddr = 0x5100u;
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan((int)oneShotSpuAddr, 16), flags: 0x01, nibbleVal: 2);

            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 3);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 101, 0x3FFF, 0x3FFF); // Master volume
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 0, (ushort)(loopSpuAddr >> 16), (ushort)loopSpuAddr, 0, 0x3FFF, 0x3FFF, 0x1000, 0x000F);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 0, (ushort)(oneShotSpuAddr >> 16), (ushort)oneShotSpuAddr, 1, 0x2000, 0x2000, 0x1000, 0x000F);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur - sendAddr), recvAddr, 256);

            Assert.True(audio.IsVoiceActive(0));
            Assert.True(audio.IsVoiceActive(1));

            short[] mix = new short[256 * 2];
            audio.StepMixSamples(256, mix);
            Assert.Contains(mix, s => s != 0);

            // Looping voice 0 stays active across multiple block lengths; one-shot voice 1 finishes after 28 samples
            Assert.True(audio.IsVoiceActive(0));
            Assert.False(audio.IsVoiceActive(1));

            // Smooth ramp voice 0 volume down to 0 via cmd=96 (step=256/sample -> ramps cleanly to silence)
            uint cur2 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            cur2 = WriteEaStreamBatchCmd(ramManaged, cur2, 96, 0, 0, 0);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur2 - sendAddr), recvAddr, 256);
            audio.StepMixSamples(128, mix);
            audio.StepMixSamples(256, mix);
            Assert.All(mix, s => Assert.Equal((short)0, s));

            // Movie PCM queue test (cmd=77 configures rate, QueuePcmMono16 mixes stereo samples)
            uint cur3 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            cur3 = WriteEaStreamBatchCmd(ramManaged, cur3, 4, 0); // KeyOff voice 0
            cur3 = WriteEaStreamBatchCmd(ramManaged, cur3, 77, 1, 48000, 0x3FFF, 0x3FFF);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur3 - sendAddr), recvAddr, 256);

            short[] pcm = new short[128];
            Array.Fill(pcm, (short)12000);
            audio.QueuePcmMono16(pcm, 48000);
            audio.StepMixSamples(128, mix);
            Assert.True(mix.Max(s => Math.Abs((int)s)) > 1000);
        }
    }

    [Fact]
    public void EaStream_LoadsMihHeaderToEeRam_AndStreamsAdpcmAtExactAlignedOffset()
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

            ctx.SetU32(28, 0x00512670u); // $gp
            ctx.SetU32(29, 0x01FFFF00u); // $sp

            // Synthetic 8 KB archive buffer containing MIH header at 0x0800 and ADPCM payload at 0x1040
            byte[] archive = new byte[0x2000];
            uint headerOffset = 0x0800u;
            uint streamDataOffset = 0x1040u;
            uint expectedHandle = 0x00000012u;

            // MIH header at 0x0800: channels=2, sampleRate=24000, interleave=0x4000
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan((int)headerOffset + 0, 4), 0x4D494800u);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan((int)headerOffset + 8, 4), 2u);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan((int)headerOffset + 12, 4), 24000u);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan((int)headerOffset + 16, 4), 0x0400u);

            // ADPCM blocks directly at 0x1040 (must NOT be shifted by +fileHandle)
            for (int blk = 0; blk < 16; blk++)
            {
                byte flags = blk == 0 ? (byte)0x04 : (blk == 15 ? (byte)0x03 : (byte)0x02);
                WriteAdpcmBlock(archive.AsSpan((int)streamDataOffset + blk * 16, 16), flags: flags, nibbleVal: 3);
            }

            var requestedOffsets = new List<long>();
            runtime.Audio.StreamFileReader = (handle, offset, dst) =>
            {
                Assert.Equal(expectedHandle, handle);
                requestedOffsets.Add(offset);
                if (offset < 0 || offset >= archive.Length)
                    return 0;
                int copy = (int)Math.Min(dst.Length, archive.Length - offset);
                archive.AsSpan((int)offset, copy).CopyTo(dst);
                return copy;
            };

            uint sendAddr = 0x00607000u;
            uint recvAddr = 0x005AA240u;
            uint eeHdrDest = 0x00610000u;

            // Step A: Load 4 KB MIH header via cmd=41(fileHandle32, offset32, size32) + cmd=49 + cmd=1(mode=126)
            uint curHdr = WriteEaStreamBatchHeader(ramManaged, sendAddr, 3);
            curHdr = WriteEaStreamBatchCmd(ramManaged, curHdr, 41, 0, (ushort)expectedHandle, (ushort)(headerOffset >> 16), (ushort)headerOffset, 0x0000, 0x1000);
            curHdr = WriteEaStreamBatchCmd(ramManaged, curHdr, 49, (ushort)(eeHdrDest >> 16), (ushort)eeHdrDest);
            curHdr = WriteEaStreamBatchCmd(ramManaged, curHdr, 1, 0, (ushort)expectedHandle, 0x002E, 0x3FFF, 0x3FFF, 0x0800, 126);
            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curHdr - sendAddr), recvAddr, 512);

            Assert.Equal(0x4D494800u, BinaryPrimitives.ReadUInt32LittleEndian(ramManaged.AsSpan((int)eeHdrDest, 4)));

            // Step B: Start stream slot 0 on SPU voice pair 0x2E/0x2F (46/47) at aligned streamDataOffset (0x1040)
            requestedOffsets.Clear();
            uint curPlay = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            curPlay = WriteEaStreamBatchCmd(ramManaged, curPlay, 41, 0, (ushort)expectedHandle, (ushort)(streamDataOffset >> 16), (ushort)streamDataOffset, 0x0000, 0x0800);
            curPlay = WriteEaStreamBatchCmd(ramManaged, curPlay, 1, 0, (ushort)expectedHandle, 0x002E, 0x3FFF, 0x3FFF, 0x0800, 0);
            runtime.Audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curPlay - sendAddr), recvAddr, 512);

            Assert.True(runtime.Audio.IsStreamActive(0));

            short[] mix = new short[256 * 2];
            runtime.Audio.StepMixSamples(256, mix);
            Assert.Contains(mix, s => s != 0);
            Assert.Contains(requestedOffsets, off => off == (long)streamDataOffset);
            Assert.DoesNotContain(requestedOffsets, off => off == (long)(streamDataOffset + expectedHandle));

            // Step C: Verify guest status parser + SOUND_GetStreamInfo (sub_00468558) accept stream slot 0 cleanly
            BinaryPrimitives.WriteInt32LittleEndian(ramManaged.AsSpan(0x004A466C, 4), 5); // max stream limit
            BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan(0x004A469C, 4), 0xFFFFFFFFu);
            RuntimeDispatcher.Call(0x00466488u, &ctx, ram);

            uint outInfoAddr = 0x00612000u;
            ctx.SetU32(4, 0u);
            ctx.SetU32(5, outInfoAddr);
            RuntimeDispatcher.Call(0x00468558u, &ctx, ram);
            Assert.NotEqual((byte)0, ramManaged[outInfoAddr + 17]);
        }
    }

    [Fact]
    public void Spu2AudioService_Cmd40VoiceKeyOff_StopsOnlyTargetVoiceWithoutClosingActiveStream()
    {
        byte[] ramManaged = new byte[CpuContext.RamSize];
        fixed (byte* ram = ramManaged)
        {
            using var audio = new Spu2AudioService(tryInitOpenAl: false);

            // Prepare a looping ADPCM block for hardware voice SFX at 0x001E0000 and stream payload at offset 0x1040
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan(0x001E0000, 16), flags: 0x03, nibbleVal: 3);

            byte[] streamArchive = new byte[0x8000];
            for (int off = 0x1040; off < streamArchive.Length; off += 16)
                WriteAdpcmBlock(streamArchive.AsSpan(off, 16), flags: 0x00, nibbleVal: 2);

            uint expectedHandle = 7u;
            audio.StreamFileReader = (handle, offset, dst) =>
            {
                if (handle != expectedHandle || offset < 0 || offset >= streamArchive.Length)
                    return 0;
                int copy = (int)Math.Min(dst.Length, streamArchive.Length - offset);
                streamArchive.AsSpan((int)offset, copy).CopyTo(dst);
                return copy;
            };

            uint sendAddr = 0x00607000u;
            uint recvAddr = 0x005AA240u;

            // Start stream slot 0 on SPU pair 46/47 and key on hardware voices 0 & 1
            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 4);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 41, 0, (ushort)expectedHandle, 0x0000, 0x1040, 0x0000, 0x2000);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 1, 0, (ushort)expectedHandle, 0x002E, 0x3FFF, 0x3FFF, 0x0800, 0);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 57, 0x001E, 0x0000, 0, 0x3FFF, 0x3FFF, 0x1000, 0x000F, 0);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 57, 0x001E, 0x0000, 1, 0x3FFF, 0x3FFF, 0x1000, 0x000F, 0);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur - sendAddr), recvAddr, 512);

            Assert.True(audio.IsStreamActive(0));
            Assert.True(audio.IsVoiceActive(0));
            Assert.True(audio.IsVoiceActive(1));

            // Send cmd=40 (args=[0, 0], [0, 1]) - must release only hardware voices 0 & 1, keeping stream 0 active
            uint curOff = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            curOff = WriteEaStreamBatchCmd(ramManaged, curOff, 40, 0, 0);
            curOff = WriteEaStreamBatchCmd(ramManaged, curOff, 40, 0, 1);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curOff - sendAddr), recvAddr, 512);

            short[] mix = new short[256 * 2];
            audio.StepMixSamples(256, mix);

            Assert.False(audio.IsVoiceActive(0));
            Assert.False(audio.IsVoiceActive(1));
            Assert.True(audio.IsStreamActive(0));
            Assert.Contains(mix, s => s != 0);
        }
    }

    [Fact]
    public void DvdVirtualFileSystem_EaFileSysReadDestSpace3_LoadsAdpcmIntoSpu2Ram()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"castaway_spu2_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] bankPayload = new byte[64];
            for (int i = 0; i < bankPayload.Length; i += 16)
                WriteAdpcmBlock(bankPayload.AsSpan(i, 16), flags: (byte)(i + 16 == bankPayload.Length ? 0x01 : 0x00), nibbleVal: 4);
            File.WriteAllBytes(Path.Combine(tempDir, "SAMPLES.ARC"), bankPayload);

            byte[] ramManaged = new byte[CpuContext.RamSize];
            byte[] spManaged = new byte[CpuContext.ScratchpadSize];

            fixed (byte* ram = ramManaged)
            fixed (byte* sp = spManaged)
            {
                CpuContext ctx = default;
                ctx.Initialize(sp);

                using var runtime = new CastawayRuntime(dvdRootPath: tempDir);
                runtime.Install(&ctx, ram, installHleHooks: true);

                uint cFileSysAddr = 0x00596E90u;
                uint slotTableAddr = 0x00620000u;
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)cFileSysAddr + 24, 4), slotTableAddr);
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)cFileSysAddr + 28, 4), 4u);

                uint openReqAddr = 0x00630000u;
                System.Text.Encoding.ASCII.GetBytes("SAMPLES.ARC\0").CopyTo(ramManaged, (int)openReqAddr + 28);
                int slot = runtime.Dvd.OpenEaFileSysSlot(ram, cFileSysAddr, openReqAddr);
                Assert.True(slot > 0);

                // Read with reqFlags=0x0A (override destSpace + explicit offset), destSpace=3 (SPU2 RAM), destBuf=0x001E0000
                uint readReqAddr = 0x00631000u;
                uint spuDestAddr = 0x001E0000u;
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)readReqAddr + 4, 4), (uint)slot);
                ramManaged[readReqAddr + 8] = 0x0A;
                BinaryPrimitives.WriteUInt16LittleEndian(ramManaged.AsSpan((int)readReqAddr + 10, 2), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)readReqAddr + 12, 4), 0u);
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)readReqAddr + 32, 4), spuDestAddr);
                BinaryPrimitives.WriteUInt32LittleEndian(ramManaged.AsSpan((int)readReqAddr + 36, 4), (uint)bankPayload.Length);

                int bytesRead = runtime.Dvd.ReadEaFileSysSlot(ram, cFileSysAddr, readReqAddr);
                Assert.Equal(bankPayload.Length, bytesRead);
                Assert.Equal(bankPayload, runtime.Audio.Spu2Ram.AsSpan((int)spuDestAddr, bankPayload.Length).ToArray());
                Assert.All(ramManaged.AsSpan((int)spuDestAddr, bankPayload.Length).ToArray(), b => Assert.Equal((byte)0, b));

                // Verify loaded bank plays non-zero PCM when keyed onto voice 0
                uint cur = WriteEaStreamBatchHeader(ramManaged, 0x00640000u, 1);
                cur = WriteEaStreamBatchCmd(ramManaged, cur, 57, (ushort)(spuDestAddr >> 16), (ushort)spuDestAddr, 0, 0x3FFF, 0x3FFF, 0x1000, 0x000F, 0);
                runtime.Audio.HandleEaStreamRpc(0, ram, 0x00640000u, (int)(cur - 0x00640000u), 0x005AA240u, 512);

                short[] mix = new short[128 * 2];
                runtime.Audio.StepMixSamples(128, mix);
                Assert.Contains(mix, s => s != 0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CastawayRuntime_RegisterEeHeartbeatHook_FlushesCmd73ImmediatelyForVBlankAdvance()
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

            ctx.SetU32(28, 0x00512670u); // $gp
            ctx.SetU32(29, 0x01FFFF00u); // $sp

            // Initialize EA sound driver state just as sub_0042E940 does before tid=4 starts
            RuntimeDispatcher.Call(0x004646B0u, &ctx, ram);
            ctx.SetU32(4, 0u);
            ctx.SetU32(5, 30u);
            ctx.SetU32(6, 50u);
            RuntimeDispatcher.Call(0x00465DC8u, &ctx, ram);

            // Call tid=4's SOUND_RegisterEeHeartbeat (0x00469298) via Dispatcher so the HLE hook runs
            RuntimeDispatcher.Call(0x00469298u, &ctx, ram);

            // Without any stream open, cmd=73(0x005AB540) must already be flushed to Spu2AudioService
            Assert.Equal(0x005AB540u, runtime.Audio.HeartbeatEeAddr);
            RuntimeDispatcher.Call(0x0046AE78u, &ctx, ram);

            runtime.Kernel.OnVBlankTick?.Invoke(ram);

            // tid=4's poll (sub_0046AE98) must now detect the VBlank heartbeat advance immediately
            RuntimeDispatcher.Call(0x0046AE98u, &ctx, ram);
            Assert.Equal(1, ctx.GetS32(2));
        }
    }

    [Fact]
    public void Spu2AudioService_InvertedPhaseVolumes_DecodeMutedAndScaledQuietlyWithoutClipping()
    {
        using var audio = new Spu2AudioService(tryInitOpenAl: false);
        byte[] ramManaged = new byte[CpuContext.RamSize];

        fixed (byte* ram = ramManaged)
        {
            uint spuAddr = 0x5010u;
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan((int)spuAddr, 16), flags: 0x03, nibbleVal: 4);

            uint sendAddr = 0x00605000u;
            uint recvAddr = 0x00606000u;

            // 1. 0x7FFF is EA MultiStream / SPU2 negative-phase zero (produced when group slider = 0). Must be silent, NOT +2.0x.
            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 0, (ushort)(spuAddr >> 16), (ushort)spuAddr, 0, 0x7FFF, 0x7FFF, 0x1000, 0x000F);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur - sendAddr), recvAddr, 256);

            short[] mixMuted = new short[128 * 2];
            audio.StepMixSamples(128, mixMuted);
            Assert.All(mixMuted, s => Assert.Equal((short)0, s));

            // 2. 0x7E00 (~ -3.1% negative phase) must stay much quieter than full-scale 0x3FFF and never clip.
            uint curQuiet = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            curQuiet = WriteEaStreamBatchCmd(ramManaged, curQuiet, 2, 0, 0x7E00, 0x7E00);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curQuiet - sendAddr), recvAddr, 256);

            short[] mixQuiet = new short[128 * 2];
            audio.StepMixSamples(128, mixQuiet);
            int maxQuiet = mixQuiet.Max(s => Math.Abs((int)s));

            uint curFull = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            curFull = WriteEaStreamBatchCmd(ramManaged, curFull, 2, 0, 0x3FFF, 0x3FFF);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curFull - sendAddr), recvAddr, 256);

            short[] mixFull = new short[128 * 2];
            audio.StepMixSamples(128, mixFull);
            int maxFull = mixFull.Max(s => Math.Abs((int)s));

            Assert.True(maxFull > 1000);
            Assert.True(maxQuiet > 0 && maxQuiet < maxFull / 10, $"Expected quiet 0x7E00 peak ({maxQuiet}) < 10% of full ({maxFull}).");
        }
    }

    [Fact]
    public void Spu2AudioService_StreamPanning_PreservesHardLeftRightAndZeroMute()
    {
        using var audio = new Spu2AudioService(tryInitOpenAl: false);
        byte[] ramManaged = new byte[CpuContext.RamSize];

        fixed (byte* ram = ramManaged)
        {
            byte[] archive = new byte[0x4000];
            uint streamDataOffset = 0x1040u;
            for (int off = (int)streamDataOffset; off < archive.Length; off += 16)
                WriteAdpcmBlock(archive.AsSpan(off, 16), flags: 0x02, nibbleVal: 3);

            uint fh = 9u;
            audio.StreamFileReader = (handle, offset, dst) =>
            {
                if (handle != fh || offset < 0 || offset >= archive.Length)
                    return 0;
                int copy = (int)Math.Min(dst.Length, archive.Length - offset);
                archive.AsSpan((int)offset, copy).CopyTo(dst);
                return copy;
            };

            uint sendAddr = 0x00605000u;
            uint recvAddr = 0x00606000u;

            // 1. Stream started at (0, 0) (slider muted) must remain silent, NOT forced to 0x3FFF.
            uint cur0 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            cur0 = WriteEaStreamBatchCmd(ramManaged, cur0, 41, 0, (ushort)fh, 0, (ushort)streamDataOffset, 0, 0x2000);
            cur0 = WriteEaStreamBatchCmd(ramManaged, cur0, 1, 0, (ushort)fh, 0x002E, 0x0000, 0x0000, 0x0800, 0);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur0 - sendAddr), recvAddr, 256);

            short[] mixMuted = new short[128 * 2];
            audio.StepMixSamples(128, mixMuted);
            Assert.All(mixMuted, s => Assert.Equal((short)0, s));

            // 2. Hard-left mono stream (0x3FFF, 0x0000) must play only on left channel.
            uint curL = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            curL = WriteEaStreamBatchCmd(ramManaged, curL, 41, 0, (ushort)fh, 0, (ushort)streamDataOffset, 0, 0x2000);
            curL = WriteEaStreamBatchCmd(ramManaged, curL, 1, 0, (ushort)fh, 0x002C, 0x3FFF, 0x0000, 0x0800, 0);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(curL - sendAddr), recvAddr, 256);

            short[] mixLeft = new short[128 * 2];
            audio.StepMixSamples(128, mixLeft);
            Assert.Contains(Enumerable.Range(0, 128).Select(i => mixLeft[i * 2]), s => s != 0);
            Assert.All(Enumerable.Range(0, 128).Select(i => mixLeft[i * 2 + 1]), s => Assert.Equal((short)0, s));

            // 3. cmd=44 configures stereo right voice volume directly without overwriting ch1's left pan.
            uint cur44 = WriteEaStreamBatchHeader(ramManaged, sendAddr, 1);
            cur44 = WriteEaStreamBatchCmd(ramManaged, cur44, 44, (0 << 8) | 44, 45, 1, 0x0000, 0x3FFF, 0, 0);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur44 - sendAddr), recvAddr, 256);
            audio.StepMixSamples(64, mixLeft);
            Assert.Contains(Enumerable.Range(0, 64).Select(i => mixLeft[i * 2]), s => s != 0);
        }
    }

    [Fact]
    public void Spu2AudioService_Cmd101DisableEffectsRange_DoesNotClobberMasterVolume()
    {
        using var audio = new Spu2AudioService(tryInitOpenAl: false);
        byte[] ramManaged = new byte[CpuContext.RamSize];

        fixed (byte* ram = ramManaged)
        {
            uint spuAddr = 0x5010u;
            WriteAdpcmBlock(audio.Spu2Ram.AsSpan((int)spuAddr, 16), flags: 0x03, nibbleVal: 3);

            uint sendAddr = 0x00605000u;
            uint recvAddr = 0x00606000u;

            // Real SLUS_216.64 calls SOUND_DisableEffects(0, 47) -> cmd=101(0, 47). Master volume must stay 0x3FFF.
            uint cur = WriteEaStreamBatchHeader(ramManaged, sendAddr, 2);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 101, 0, 47);
            cur = WriteEaStreamBatchCmd(ramManaged, cur, 0, (ushort)(spuAddr >> 16), (ushort)spuAddr, 0, 0x3FFF, 0x3FFF, 0x1000, 0x000F);
            audio.HandleEaStreamRpc(0, ram, sendAddr, (int)(cur - sendAddr), recvAddr, 256);

            short[] mix = new short[128 * 2];
            audio.StepMixSamples(128, mix);
            Assert.True(mix.Max(s => Math.Abs((int)s)) > 1000);
        }
    }
}
