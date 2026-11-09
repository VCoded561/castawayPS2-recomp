using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Castaway.Core;
using Castaway.ElfTools.Elf;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.OpenGl;
using Castaway.GsRenderer.Vif;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase4GsRendererTests
{
    [Fact]
    public void Vif1_Unpack_Formats_CycleModes_Stmod_And_Mask_WorkAccurately()
    {
        var vif1 = new VifUnit(isVif1: true);

        // Configure BASE=10, OFFSET=20 -> TOPS=10 initially
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MakeVifCode(0x03, 0, 10));     // BASE = 10
        bw.Write(MakeVifCode(0x02, 0, 20));     // OFFSET = 20 (DBF=false, TOPS=10)
        bw.Write(MakeVifCode(0x01, 0, 0x0102)); // STCYCL: CL=2, WL=1 (Skipping write: write 1, skip 1)

        // STROW = (10, 20, 30, 40)
        bw.Write(MakeVifCode(0x30, 0, 0));
        bw.Write(10u);
        bw.Write(20u);
        bw.Write(30u);
        bw.Write(40u);

        // STMOD = 1 (Offset mode: unpacked + Row)
        bw.Write(MakeVifCode(0x05, 0, 1));

        // UNPACK V4-32 (0x6C), num=2 vectors, FLG=1 (0x8000 | addr=5 -> qword 15 and qword 17 due to CL=2, WL=1)
        bw.Write(MakeVifCode(0x6C, 2, 0x8005));
        // Vector 0: (1, 2, 3, 4) -> + Row(10,20,30,40) = (11, 22, 33, 44) at qword 15
        bw.Write(1u); bw.Write(2u); bw.Write(3u); bw.Write(4u);
        // Vector 1: (100, 200, 300, 400) -> + Row(10,20,30,40) = (110, 220, 330, 440) at qword 17 (qword 16 skipped!)
        bw.Write(100u); bw.Write(200u); bw.Write(300u); bw.Write(400u);

        // Reset STMOD = 0, STCYCL CL=1, WL=1, and test V4-5 (0x6F) RGB5A1 unpack at qword 0
        bw.Write(MakeVifCode(0x05, 0, 0));
        bw.Write(MakeVifCode(0x01, 0, 0x0101));
        bw.Write(MakeVifCode(0x6F, 1, 0x0000));
        // RGB5A1: R=31 (248), G=16 (128), B=8 (64), A=1 (0x80) -> 0x8000 | (8<<10) | (16<<5) | 31 = 0xA21F
        bw.Write((uint)0x0000A21Fu);

        vif1.ProcessData(ms.ToArray());

        // Check qword 15 (byte 15 * 16 = 240)
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(15 * 16 + 0, 4)));
        Assert.Equal(22u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(15 * 16 + 4, 4)));
        Assert.Equal(33u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(15 * 16 + 8, 4)));
        Assert.Equal(44u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(15 * 16 + 12, 4)));

        // Check qword 16 was skipped (remains 0)
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(16 * 16, 4)));

        // Check qword 17
        Assert.Equal(110u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(17 * 16 + 0, 4)));
        Assert.Equal(220u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(17 * 16 + 4, 4)));
        Assert.Equal(330u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(17 * 16 + 8, 4)));
        Assert.Equal(440u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(17 * 16 + 12, 4)));

        // Check V4-5 at qword 0
        Assert.Equal(248u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(0, 4)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(4, 4)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(8, 4)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(vif1.DataMem.AsSpan(12, 4)));
    }

    [Fact]
    public void GsSwizzle_32Bit_16Bit_8Bit_4Bit_And_ClutLookup_AreBitExact()
    {
        byte[] vram = new byte[GsSwizzle.VramByteSize];

        // 1. Test PSMCT32 swizzling across multiple pages and blocks
        for (int y = 0; y < 64; y += 7)
        {
            for (int x = 0; x < 128; x += 9)
            {
                uint expected = 0x80000000u | ((uint)y << 16) | (uint)x;
                GsSwizzle.WritePixel32(vram, baseBlock: 0, bwPixels: 128, x, y, expected);
                uint actual = GsSwizzle.ReadPixel32(vram, baseBlock: 0, bwPixels: 128, x, y);
                Assert.Equal(expected, actual);
            }
        }

        // 2. Test PSMT8 + CSM1 CLUT lookup (including indices 8..15 and 16..23 tile swap)
        int cbp = 256;
        for (int idx = 0; idx < 256; idx++)
        {
            int cx = (idx & 7) | ((idx & 16) >> 1);
            int cy = ((idx & 8) >> 3) | ((idx & 0xE0) >> 4);
            uint paletteColor = 0x80000000u | ((uint)idx << 16) | ((uint)(255 - idx) << 8) | (uint)idx;
            GsSwizzle.WritePixel32(vram, cbp, 64, cx, cy, paletteColor);
        }

        for (int idx = 0; idx < 256; idx++)
        {
            uint lookedUp = GsSwizzle.LookupClutCsm1(vram, cbp, GsPsm.Psmct32, 0, (byte)idx, is8Bit: true);
            uint expected = 0x80000000u | ((uint)idx << 16) | ((uint)(255 - idx) << 8) | (uint)idx;
            Assert.Equal(expected, lookedUp);
        }
    }

    [Fact]
    public void Pipeline_Vif1_Vu1Recompiler_XgKick_To_GsRasterizer_EndToEnd()
    {
        using var pipeline = new GsGraphicsPipeline();

        // Configure GS Framebuffer (FBP=0, FBW=10 (640px), PSM=PSMCT32) and XYOFFSET=0
        pipeline.Gs.WriteInternalRegister(0x4C, 10ul << 16); // FRAME_1
        pipeline.Gs.WriteInternalRegister(0x18, 0ul);        // XYOFFSET_1
        pipeline.Gs.WriteInternalRegister(0x40, (639ul << 16) | (479ul << 48)); // SCISSOR_1

        // Build a VIF1 packet that:
        // 1. Unpacks a 3-qword GIF packet into VU1 DataMem at qword 0:
        //    - Qword 0: GIFTag (NLOOP=2, EOP=1, PRE=1, PRIM=6 (Sprite), FLG=0 (PACKED), NREG=1, REGS=0xE (A+D))
        //      Wait: let's use REGS = [RGBAQ (0x1), XYZ2 (0x5)] (NREG=2, NLOOP=2) OR A+D (0xE)!
        //      With NREG=2, REGS=0x51 (reg0=RGBAQ, reg1=XYZ2), NLOOP=1, plus 1 vertex before it:
        //      Even simpler: NLOOP=3, EOP=1, PRE=1, PRIM=6 (Sprite), FLG=0 (PACKED), NREG=1, REGS=0xE (A+D):
        //      - A+D 0: RGBAQ (0x01) = Red(0x10), Green(0x20), Blue(0x30), Alpha(0x80) -> after VU1 adds +10 to Red in VF1!
        //      - A+D 1: XYZ2 (0x05) = X=(10<<4), Y=(10<<4), Z=100
        //      - A+D 2: XYZ2 (0x05) = X=(20<<4), Y=(20<<4), Z=100
        // 2. Uploads a VU1 microprogram via MPG at address 0 that:
        //    - [0] NOP             | LQ.xyzw vf1, 1(vi0)   (loads RGBAQ data qword)
        //    - [1] [I] NOP         | LOI 32.0f             (loads I = 32)
        //    - [2] FTOI0.x vf2, vf1| IADDIU vi1, vi0, 0    (sets vi1 = 0 for XGKICK)
        //    - [3] [E] NOP         | XGKICK vi1            (kicks GIF packet at 0(vi1)!)
        //    - [4] NOP             | NOP                   (E-bit delay slot)
        // 3. Triggers MSCAL 0!

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // UNPACK V4-32 (0x6C), num=4 quadwords at addr=0
        bw.Write(MakeVifCode(0x6C, 4, 0));

        // Qword 0: GIFTag: NLOOP=3, EOP=1 (0x8003), PRE=1 (bit 46), PRIM=6 (Sprite, bits 47..57), FLG=0, NREG=1 (bits 60..63)
        ulong gifTagLo = 0x8003ul | (1ul << 46) | (6ul << 47) | (1ul << 60);
        ulong gifTagHi = 0x0Eul; // A+D
        bw.Write(gifTagLo);
        bw.Write(gifTagHi);

        // Qword 1: A+D -> RGBAQ (0x01): R=0x40, G=0x80, B=0xC0, A=0x80, Q=1.0f
        ulong rgbaqData = 0x40ul | (0x80ul << 8) | (0xC0ul << 16) | (0x80ul << 24) | ((ulong)BitConverter.SingleToUInt32Bits(1.0f) << 32);
        bw.Write(rgbaqData);
        bw.Write(0x01ul);

        // Qword 2: A+D -> XYZ2 (0x05): X = 4 << 4, Y = 4 << 4, Z = 50
        ulong xyz0 = ((ulong)(4 << 4)) | ((ulong)(4 << 4) << 16) | (50ul << 32);
        bw.Write(xyz0);
        bw.Write(0x05ul);

        // Qword 3: A+D -> XYZ2 (0x05): X = 12 << 4, Y = 12 << 4, Z = 50
        ulong xyz1 = ((ulong)(12 << 4)) | ((ulong)(12 << 4) << 16) | (50ul << 32);
        bw.Write(xyz1);
        bw.Write(0x05ul);

        // MPG: 3 instructions at VU1 MicroMem address 0
        bw.Write(MakeVifCode(0x4A, 3, 0));
        // Insn 0: Upper = NOP (0x000002FF), Lower = IADDIU vi1, vi0, 0 (0x10010000)
        bw.Write(0x10010000u);
        bw.Write(0x000002FFu);
        // Insn 1: Upper = [E] NOP (0x400002FF), Lower = XGKICK vi1 (top=0x40, is=1, subOp=0x6C -> 0x80000EC0 | (1 << 11))
        uint xgkickVi1 = (0x40u << 25) | (1u << 11) | (0x1Bu << 6) | 0x3Cu; // subOp = (0x1B << 2) | 0 = 0x6C
        bw.Write(xgkickVi1);
        bw.Write(0x400002FFu);
        // Insn 2 (Delay slot): Upper = NOP, Lower = NOP
        bw.Write(0x8000033Cu);
        bw.Write(0x000002FFu);

        // MSCAL 0
        bw.Write(MakeVifCode(0x14, 0, 0));

        pipeline.Vif1.ProcessData(ms.ToArray());

        Assert.Equal(1, pipeline.Vu1Compiler.CachedProgramCount);
        Assert.Equal(1, pipeline.Vu1.TotalXgKicksExecuted);
        Assert.Equal(1, pipeline.Gs.TotalPrimitivesDrawn);

        // Verify sprite pixels inside [4..11, 4..11] were written to swizzled 4 MB GS VRAM
        uint pixelInside = pipeline.Gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 6, 6);
        Assert.Equal(0x80C08040u, pixelInside);

        uint pixelOutside = pipeline.Gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 2, 2);
        Assert.Equal(0u, pixelOutside);
    }

    [Fact]
    public void GsProcessor_TexturedSprite_With_Psmt8_And_Csm1Clut_RendersExactColors()
    {
        var gs = new GsProcessor();

        // Setup Context 1 Framebuffer at block 0 (FBP=0, FBW=10, PSMCT32)
        gs.WriteInternalRegister(0x4C, 10ul << 16);
        gs.WriteInternalRegister(0x18, 0ul);
        gs.WriteInternalRegister(0x40, (639ul << 16) | (479ul << 48));

        // 1. Upload an 8x8 PSMT8 texture at TBP0 = block 512 (TBW = 2 -> 128px) via Host-to-Local transfer
        ulong bitbltTex = ((ulong)512 << 32) | (2ul << 48) | ((ulong)GsPsm.Psmt8 << 56);
        gs.WriteInternalRegister(0x50, bitbltTex);
        gs.WriteInternalRegister(0x51, 0ul); // TRXPOS = (0, 0)
        gs.WriteInternalRegister(0x52, 8ul | (8ul << 32)); // TRXREG = 8x8
        gs.WriteInternalRegister(0x53, 0ul); // TRXDIR = Host-to-Local

        // Fill 8x8 texture with palette index 42 (64 bytes = 4 quadwords)
        byte[] texPacket = new byte[16 + 64];
        BinaryPrimitives.WriteUInt64LittleEndian(texPacket.AsSpan(0, 8), 4ul | (1ul << 15) | (2ul << 58)); // IMAGE mode, 4 qwords
        for (int i = 0; i < 64; i++)
            texPacket[16 + i] = 42;
        gs.SubmitGifPacket(texPacket);

        // 2. Upload a 16x16 PSMCT32 CLUT at CBP = block 768 (DBW = 1 -> 64px)
        ulong bitbltClut = ((ulong)768 << 32) | (1ul << 48) | ((ulong)GsPsm.Psmct32 << 56);
        gs.WriteInternalRegister(0x50, bitbltClut);
        gs.WriteInternalRegister(0x51, 0ul);
        gs.WriteInternalRegister(0x52, 16ul | (16ul << 32));
        gs.WriteInternalRegister(0x53, 0ul);

        byte[] clutPacket = new byte[16 + 256 * 4];
        BinaryPrimitives.WriteUInt64LittleEndian(clutPacket.AsSpan(0, 8), 64ul | (1ul << 15) | (2ul << 58)); // IMAGE mode, 64 qwords
        int cx42 = (42 & 7) | ((42 & 16) >> 1);
        int cy42 = ((42 & 8) >> 3) | ((42 & 0xE0) >> 4);
        int linearIdx42 = cy42 * 16 + cx42;
        const uint ClutColor42 = 0x80112233u; // R=0x33, G=0x22, B=0x11, A=0x80
        BinaryPrimitives.WriteUInt32LittleEndian(clutPacket.AsSpan(16 + linearIdx42 * 4, 4), ClutColor42);
        gs.SubmitGifPacket(clutPacket);

        // 3. Configure TEX0_1: TBP0=512, TBW=2, PSM=PSMT8 (0x13), TW=3 (8), TH=3 (8), TCC=1 (RGBA), TFX=1 (Decal), CBP=768, CPSM=PSMCT32 (0)
        ulong tex0 = 512ul
                   | (2ul << 14)
                   | ((ulong)GsPsm.Psmt8 << 20)
                   | (3ul << 26)
                   | (3ul << 30)
                   | (1ul << 34)
                   | (1ul << 35)
                   | (768ul << 37);
        gs.WriteInternalRegister(0x06, tex0);

        // 4. Draw a Textured Sprite (PRIM = 6 (Sprite) | TME (bit 4) | FST (bit 8) = 0x116) from (0,0) to (8,8)
        gs.WriteInternalRegister(0x00, 0x116ul);
        gs.WriteInternalRegister(0x03, 0ul); // UV = (0, 0)
        gs.WriteInternalRegister(0x05, 0ul | (0ul << 16) | (10ul << 32)); // XYZ2 = (0, 0, 10)
        gs.WriteInternalRegister(0x03, (8ul << 4) | ((8ul << 4) << 16)); // UV = (8.0, 8.0)
        gs.WriteInternalRegister(0x05, (8ul << 4) | ((8ul << 4) << 16) | (10ul << 32)); // XYZ2 = (8.0, 8.0, 10)

        uint sampled = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 3, 3);
        Assert.Equal(ClutColor42, sampled);
    }

    [Fact]
    public void Slus21664_EmbeddedVuText_MpgBlocks_AllCompileCleanlyInVu1Recompiler()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(elfPath);
        var vutext = reader.FindSection(".vutext");
        Assert.NotNull(vutext);

        byte[] raw = reader.GetRawBytes();
        int start = (int)vutext!.FileOffset;
        int end = start + (int)vutext.Size - 8;

        var vu1 = new Vu1State();
        var compiler = new Vu1Recompiler();
        int compiledBlocks = 0;

        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);

            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800)
                continue;

            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)vutext.Size)
                continue;

            int destByteAddr = (addr * 8) & Vu1State.MicroMemMask;
            raw.AsSpan(i + 4, mpgBytes).CopyTo(vu1.MicroMem.AsSpan(destByteAddr, mpgBytes));
            compiler.InvalidateMicroMemHash();

            var compiled = compiler.GetOrCompile(vu1, (ushort)destByteAddr);
            Assert.NotNull(compiled.ExecuteDelegate);
            Assert.True(compiled.CompiledBundleCount > 0);
            Assert.Contains($"Vu1Prog_{destByteAddr:X4}_", compiled.EmittedCSharpSource);

            compiledBlocks++;
            i += 4 + mpgBytes - 4;
        }

        Assert.Equal(14, compiledBlocks);
    }

    [Fact]
    public void OpenGl46_Shaders_And_WindowOptions_EnforceCoreProfileAndDsaDesign()
    {
        var opts = GsOpenGlRenderer.CreateOpenGl46WindowOptions();
        Assert.Equal(4, opts.API.Version.MajorVersion);
        Assert.Equal(6, opts.API.Version.MinorVersion);
        Assert.Contains("#version 460 core", GsOpenGlRenderer.UnswizzleComputeShaderSource);
        Assert.Contains("#version 460 core", GsOpenGlRenderer.PresentVertexShaderSource);
        Assert.Contains("#version 460 core", GsOpenGlRenderer.PresentFragmentShaderSource);
    }

    [Fact]
    public void GsSwizzle_4Bit_Psmt4_With_Multiple_Csa_Palettes_RendersBitExact()
    {
        byte[] vram = new byte[GsSwizzle.VramByteSize];

        // 1. Verify PSMT4 swizzling round-trip across multiple pages and blocks
        int tbp = 512;
        int bw = 256;
        for (int y = 0; y < 128; y += 5)
        {
            for (int x = 0; x < 256; x += 7)
            {
                byte expectedNibble = (byte)((x ^ y) & 0x0F);
                GsSwizzle.WriteTexel4(vram, tbp, bw, x, y, expectedNibble);
                byte readNibble = GsSwizzle.ReadTexel4(vram, tbp, bw, x, y);
                Assert.Equal(expectedNibble, readNibble);
            }
        }

        // 2. Verify 16-color CLUT lookups across multiple CSA offsets (0, 1, 3, 16, 20)
        int cbp = 1024;
        int[] testCsas = [0, 1, 3, 16, 20];
        foreach (int csa in testCsas)
        {
            for (int i = 0; i < 16; i++)
            {
                int effectiveIdx = (csa * 16 + i) & 0x1FF;
                int cbpTarget = cbp + (effectiveIdx >> 8) * 4;
                int idx256 = effectiveIdx & 0xFF;
                int cx = (idx256 & 7) | ((idx256 & 16) >> 1);
                int cy = ((idx256 & 8) >> 3) | ((idx256 & 0xE0) >> 4);
                uint color = 0x80000000u | ((uint)csa << 16) | ((uint)i << 8) | 0xAAu;
                GsSwizzle.WritePixel32(vram, cbpTarget, 64, cx, cy, color);
            }
        }

        foreach (int csa in testCsas)
        {
            for (byte i = 0; i < 16; i++)
            {
                uint expected = 0x80000000u | ((uint)csa << 16) | ((uint)i << 8) | 0xAAu;
                uint actual = GsSwizzle.LookupClutCsm1(vram, cbp, GsPsm.Psmct32, csa, i, is8Bit: false);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void GsProcessor_SplitGifPacket_ResumesAcrossDmaBoundaries_Accurately()
    {
        var gs = new GsProcessor();

        // 1. Set PRIM = Triangle (0x03)
        gs.WriteInternalRegister(0x00, 0x03ul);

        // 2. Build a PACKED GIF stream: nloop=3, nreg=2 (reg0=RGBAQ 0x01, reg1=XYZ2 0x05)
        // Total payload = 6 quadwords (96 bytes) representing 3 vertices of a triangle.
        byte[] fullPacket = new byte[16 + 6 * 16];
        ulong tagLo = 3ul | (1ul << 15) | (0ul << 58) | (2ul << 60); // nloop=3, eop=1, flg=PACKED(0), nreg=2
        ulong tagHi = 0x01ul | (0x05ul << 4); // r0=RGBAQ, r1=XYZ2
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(0, 8), tagLo);
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(8, 8), tagHi);

        // Vertex 0: RGBAQ + XYZ2
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(16, 8), 0x00808080ul); // RGBA
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(32, 8), (100ul << 4) | ((100ul << 4) << 32)); // X=100, Y=100
        // Vertex 1: RGBAQ + XYZ2
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(48, 8), 0x00808080ul);
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(64, 8), (200ul << 4) | ((100ul << 4) << 32)); // X=200, Y=100
        // Vertex 2: RGBAQ + XYZ2
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(80, 8), 0x00808080ul);
        BinaryPrimitives.WriteUInt64LittleEndian(fullPacket.AsSpan(96, 8), (150ul << 4) | ((200ul << 4) << 32)); // X=150, Y=200

        // Split packet across DMA boundaries:
        // Chunk 1: GIFTAG (16B) + Vertex 0 (32B) = 48 bytes
        // Chunk 2: Vertex 1 (32B) + Vertex 2 (32B) = 64 bytes
        byte[] chunk1 = fullPacket.AsSpan(0, 48).ToArray();
        byte[] chunk2 = fullPacket.AsSpan(48, 64).ToArray();

        long primsBefore = gs.TotalPrimitivesDrawn;
        gs.SubmitGifPacket(chunk1);
        Assert.Equal(primsBefore, gs.TotalPrimitivesDrawn); // Triangle not yet complete (only 1 vertex)

        gs.SubmitGifPacket(chunk2);
        Assert.Equal(primsBefore + 1, gs.TotalPrimitivesDrawn); // Triangle completed on chunk 2!

        // 3. Test REGLIST mode split across DMA boundaries:
        // nloop=2, nreg=2 -> total 4 registers = 2 quadwords (32 bytes).
        // r0 = 0x08 (CLAMP_1), r1 = 0x09 (CLAMP_2)
        byte[] reglistPacket = new byte[16 + 32];
        ulong regTagLo = 2ul | (1ul << 15) | (1ul << 58) | (2ul << 60); // nloop=2, eop=1, flg=REGLIST(1), nreg=2
        ulong regTagHi = 0x08ul | (0x09ul << 4);
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(0, 8), regTagLo);
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(8, 8), regTagHi);

        // Quadword 0: reg0 = 0x1111, reg1 = 0x2222
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(16, 8), 0x1111ul);
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(24, 8), 0x2222ul);
        // Quadword 1: reg0 = 0x3333, reg1 = 0x4444
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(32, 8), 0x3333ul);
        BinaryPrimitives.WriteUInt64LittleEndian(reglistPacket.AsSpan(40, 8), 0x4444ul);

        // Split into chunk A (GIFTAG + 1st quadword = 32B) and chunk B (2nd quadword = 16B)
        byte[] chunkA = reglistPacket.AsSpan(0, 32).ToArray();
        byte[] chunkB = reglistPacket.AsSpan(32, 16).ToArray();

        gs.SubmitGifPacket(chunkA);
        Assert.Equal(0x1111ul, gs.Clamp[0]);
        Assert.Equal(0x2222ul, gs.Clamp[1]);

        gs.SubmitGifPacket(chunkB);
        Assert.Equal(0x3333ul, gs.Clamp[0]);
        Assert.Equal(0x4444ul, gs.Clamp[1]);
    }

    [Fact]
    public void Vu1_Loi_LatchesAtEndOfBundle_UpperInstructionReadsPreviousI()
    {
        var vu = new Vu1State();
        var compiler = new Vu1Recompiler();

        // Initialize vu.I with 1.0f and VF2 with (4.0f, 6.0f, 8.0f, 10.0f)
        vu.I = 1.0f;
        vu.WriteVf(2, Vector128.Create(4.0f, 6.0f, 8.0f, 10.0f), 0xF);

        // Bundle 0: [I] MULi.xyzw vf1, vf2, I | LOI 2.5f
        // upper: bit 31=1 (isImm), dest=0xF (xyzw, bits 24..21), fs=2 (bits 15..11), fd=1 (bits 10..6), op=0x1E (bits 5..0)
        uint upper0 = 0x80000000u | (0xFu << 21) | (2u << 11) | (1u << 6) | 0x1Eu;
        uint lower0 = BitConverter.SingleToUInt32Bits(2.5f);

        // Bundle 1: [E] MULi.xyzw vf3, vf2, I | NOP (E-bit set: upper bit 30 = 0x40000000u)
        // Reads the new vu.I = 2.5f latched by bundle 0!
        uint upper1 = 0x40000000u | (0xFu << 21) | (2u << 11) | (3u << 6) | 0x1Eu;
        uint lower1 = 0x8000033Cu; // NOP

        // Write to MicroMem at slot 0
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(0, 4), lower0);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), upper0);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(8, 4), lower1);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(12, 4), upper1);
        // Delay slot at slot 2: NOP
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(16, 4), 0x8000033Cu);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(20, 4), 0x000002FFu);

        compiler.Execute(vu, 0);

        Assert.Equal(2.5f, vu.I);
        var vf1 = vu.ReadVf(1); // Read previous I = 1.0f
        Assert.Equal(4.0f, vf1.GetElement(0));
        Assert.Equal(6.0f, vf1.GetElement(1));
        Assert.Equal(8.0f, vf1.GetElement(2));
        Assert.Equal(10.0f, vf1.GetElement(3));

        var vf3 = vu.ReadVf(3); // Read latched I = 2.5f
        Assert.Equal(10.0f, vf3.GetElement(0)); // 4.0 * 2.5
        Assert.Equal(15.0f, vf3.GetElement(1)); // 6.0 * 2.5
        Assert.Equal(20.0f, vf3.GetElement(2)); // 8.0 * 2.5
        Assert.Equal(25.0f, vf3.GetElement(3)); // 10.0 * 2.5
    }

    private static uint MakeVifCode(byte cmd, byte num, ushort imm)
        => ((uint)cmd << 24) | ((uint)num << 16) | imm;
}
