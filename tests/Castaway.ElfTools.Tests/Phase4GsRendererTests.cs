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

                // PSMZ32 swizzle (isZ: true)
                uint expectedZ = 0x00FFFFFFu ^ ((uint)y << 8) ^ (uint)x;
                GsSwizzle.WritePixel32(vram, baseBlock: 32, bwPixels: 128, x, y, expectedZ, isZ: true);
                uint actualZ = GsSwizzle.ReadPixel32(vram, baseBlock: 32, bwPixels: 128, x, y, isZ: true);
                Assert.Equal(expectedZ, actualZ);
            }
        }

        // Verify PSMZ32 swizzle32Z blockAddressXor = 0x18 (24 * 256 = 6144 bytes)
        int addrColor = GsSwizzle.GetPixelByteAddress32(baseBlock: 0, bwPixels: 64, x: 0, y: 0, isZ: false);
        int addrDepth = GsSwizzle.GetPixelByteAddress32(baseBlock: 0, bwPixels: 64, x: 0, y: 0, isZ: true);
        Assert.Equal(0, addrColor);
        Assert.Equal(0x18 * 256, addrDepth);

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

    [Fact]
    public void Vif1_StagingBuffer_HandlesArbitrarySplitChunksAndUnknownCommands()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MakeVifCode(0x01, 0, 0x0201)); // STCYCL WL=2, CL=1 (Filling mode)
        bw.Write(MakeVifCode(0x30, 0, 0));      // STROW
        bw.Write(10u); bw.Write(20u); bw.Write(30u); bw.Write(40u);
        bw.Write(MakeVifCode(0x31, 0, 0));      // STCOL
        bw.Write(100u); bw.Write(200u); bw.Write(300u); bw.Write(400u);
        bw.Write(MakeVifCode(0x20, 0, 0));      // STMASK
        bw.Write(0x00E4_0093u);
        bw.Write(MakeVifCode(0x09, 0, 0));      // Unknown/reserved single-word command
        bw.Write(MakeVifCode(0x05, 0, 2));      // STMOD = 2 (Difference mode)
        bw.Write(MakeVifCode(0x79, 4, 0x0002)); // UNPACKM V3-16 (0x69 | 0x10 = 0x79), num=4, addr=2
        bw.Write((short)-5); bw.Write((short)12); bw.Write((short)-30);
        bw.Write((short)100); bw.Write((short)-200); bw.Write((short)300);

        byte[] full = ms.ToArray();

        var vifRef = new VifUnit(isVif1: true);
        var vifSplit = new VifUnit(isVif1: true);

        // Pre-seed DataMem identically so write-protect (maskBits=3) is also verified
        for (int i = 0; i < vifRef.DataMem.Length; i++)
        {
            byte v = (byte)(i * 37 + 13);
            vifRef.DataMem[i] = v;
            vifSplit.DataMem[i] = v;
        }

        vifRef.ProcessData(full);

        // Feed split chunks of uneven sizes (1, 3, 2, 4, 5, 7, remaining) across headers & payloads
        int[] chunkSizes = [1, 3, 2, 4, 5, 7, 4, 9, 3, 100];
        int pos = 0;
        int idx = 0;
        while (pos < full.Length)
        {
            int take = Math.Min(chunkSizes[idx++ % chunkSizes.Length], full.Length - pos);
            vifSplit.ProcessData(full.AsSpan(pos, take));
            pos += take;
        }

        Assert.Equal(vifRef.DataMem, vifSplit.DataMem);
        Assert.Equal(vifRef.Row, vifSplit.Row);
        Assert.Equal(vifRef.Col, vifSplit.Col);
        Assert.Equal(vifRef.TotalUnpackQuadwordsWritten, vifSplit.TotalUnpackQuadwordsWritten);
    }

    [Fact]
    public void Vif1_ExecuteUnpackFast_MatchesReferenceAcrossAllMasksModesAndCycleConfigs()
    {
        // Mask hits all 4 modes (0=Data, 1=Row, 2=Col, 3=WriteProtect) across all 4 cycles
        const uint testMask = 0xE4_93_6C_1Bu;
        (int vn, int vl)[] formats =
        [
            (3, 0), (2, 0), (1, 0), (0, 0), // V4-32, V3-32, V2-32, S-32
            (3, 1), (2, 1), (1, 1), (0, 1), // V4-16, V3-16, V2-16, S-16
            (3, 2), (2, 2), (1, 2), (0, 2), // V4-8,  V3-8,  V2-8,  S-8
            (3, 3),                         // V4-5
        ];
        (int cl, int wl)[] cycleConfigs =
        [
            (1, 1),
            (3, 1), // Skipping write (CL > WL)
            (2, 4), // Filling write (CL < WL, WL=4)
            (1, 6), // Filling write with WL > 4 (tests cycle clamping at 3)
        ];

        byte[] payloadBytes = new byte[256];
        for (int i = 0; i < payloadBytes.Length; i++)
            payloadBytes[i] = (byte)(i * 73 + 0x85);

        foreach (var (vn, vl) in formats)
        foreach (bool usn in new[] { false, true })
        foreach (bool useMask in new[] { false, true })
        foreach (uint mode in new uint[] { 0, 1, 2, 3 })
        foreach (var (cl, wl) in cycleConfigs)
        {
            const int dstVectors = 7;
            const int dstQwordAddr = 12;

            var vif = new VifUnit(isVif1: true);
            uint[] refMem = new uint[vif.DataMem.Length / 4];
            uint[] refRow = [0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u];
            uint[] refCol = [0xAAAAAAAAu, 0xBBBBBBBBu, 0xCCCCCCCCu, 0xDDDDDDDDu];

            for (int q = 0; q < refMem.Length; q++)
            {
                uint sentinel = 0x5A000000u + (uint)q;
                refMem[q] = sentinel;
                BinaryPrimitives.WriteUInt32LittleEndian(vif.DataMem.AsSpan(q * 4, 4), sentinel);
            }

            Array.Copy(refRow, vif.Row, 4);
            Array.Copy(refCol, vif.Col, 4);
            vif.Mask = testMask;
            vif.Cycle = (uint)((wl << 8) | cl);
            vif.Mode = mode;

            byte cmd = (byte)(0x60 | (useMask ? 0x10 : 0) | (vn << 2) | vl);
            ushort imm = (ushort)((usn ? 0x4000 : 0) | dstQwordAddr);

            int srcVecsNeeded = cl >= wl
                ? dstVectors
                : (dstVectors / wl) * cl + Math.Min(dstVectors % wl, cl);
            int bytesPerVec = vl == 3 ? 2 : ((vn + 1) << (2 - vl));
            int alignedPayloadBytes = ((srcVecsNeeded * bytesPerVec) + 3) & ~3;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(MakeVifCode(cmd, (byte)dstVectors, imm));
            bw.Write(payloadBytes, 0, alignedPayloadBytes);

            vif.ProcessData(ms.ToArray());

            // Run independent scalar reference implementation
            ReferenceExecuteUnpack(
                payloadBytes.AsSpan(0, alignedPayloadBytes),
                refMem,
                vif.DataMemQwordMask,
                refRow,
                refCol,
                testMask,
                mode,
                cl,
                wl,
                dstQwordAddr,
                dstVectors,
                vn,
                vl,
                usn,
                useMask);

            for (int q = 0; q < refMem.Length; q++)
            {
                uint actual = BinaryPrimitives.ReadUInt32LittleEndian(vif.DataMem.AsSpan(q * 4, 4));
                Assert.Equal(refMem[q], actual);
            }
            Assert.Equal(refRow, vif.Row);
        }
    }

    private static void ReferenceExecuteUnpack(
        ReadOnlySpan<byte> payload,
        uint[] memWords,
        int qwordMask,
        uint[] row,
        uint[] col,
        uint mask,
        uint mode,
        int cl,
        int wl,
        int dstQwordAddr,
        int dstVectors,
        int vn,
        int vl,
        bool usn,
        bool useMask)
    {
        int srcByteOffset = 0;
        int cycleStep = 0;
        Span<uint> rawVec = stackalloc uint[4];

        for (int i = 0; i < dstVectors; i++)
        {
            bool isData = cl >= wl || cycleStep < cl;
            rawVec.Clear();

            if (isData)
            {
                if (vn == 3 && vl == 3)
                {
                    ushort v = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(srcByteOffset, 2));
                    rawVec[0] = (uint)((v & 0x001F) << 3);
                    rawVec[1] = (uint)(((v >> 5) & 0x001F) << 3);
                    rawVec[2] = (uint)(((v >> 10) & 0x001F) << 3);
                    rawVec[3] = (v & 0x8000) != 0 ? 0x80u : 0x00u;
                    srcByteOffset += 2;
                }
                else
                {
                    for (int c = 0; c <= vn; c++)
                    {
                        switch (vl)
                        {
                            case 0:
                                rawVec[c] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(srcByteOffset, 4));
                                srcByteOffset += 4;
                                break;
                            case 1:
                                ushort u16 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(srcByteOffset, 2));
                                rawVec[c] = usn ? u16 : unchecked((uint)(int)(short)u16);
                                srcByteOffset += 2;
                                break;
                            case 2:
                                byte u8 = payload[srcByteOffset];
                                rawVec[c] = usn ? u8 : unchecked((uint)(int)(sbyte)u8);
                                srcByteOffset += 1;
                                break;
                        }
                    }

                    if (vn == 0)
                    {
                        rawVec[1] = rawVec[0];
                        rawVec[2] = rawVec[0];
                        rawVec[3] = rawVec[0];
                    }
                    else if (vn == 1)
                    {
                        rawVec[2] = rawVec[0];
                        rawVec[3] = rawVec[1];
                    }
                }
            }

            int clampedCycle = Math.Min(cycleStep, 3);
            for (int comp = 0; comp < 4; comp++)
            {
                uint maskBits = useMask
                    ? (mask >> ((clampedCycle * 4 + comp) * 2)) & 0x3u
                    : (!isData ? 1u : 0u);

                if (maskBits == 3)
                    continue;

                uint finalVal;
                if (maskBits == 1)
                {
                    finalVal = row[comp];
                }
                else if (maskBits == 2)
                {
                    finalVal = col[clampedCycle];
                }
                else
                {
                    uint v = rawVec[comp];
                    if (!(vn == 3 && vl == 3))
                    {
                        switch (mode & 0x3u)
                        {
                            case 1:
                                v = unchecked(v + row[comp]);
                                break;
                            case 2:
                                v = unchecked(v + row[comp]);
                                row[comp] = v;
                                break;
                            case 3:
                                row[comp] = v;
                                break;
                        }
                    }
                    finalVal = v;
                }

                memWords[dstQwordAddr * 4 + comp] = finalVal;
            }

            dstQwordAddr = (dstQwordAddr + 1) & qwordMask;
            cycleStep++;
            if (cl >= wl)
            {
                if (cycleStep >= wl)
                {
                    dstQwordAddr = (dstQwordAddr + (cl - wl)) & qwordMask;
                    cycleStep = 0;
                }
            }
            else if (cycleStep >= wl)
            {
                cycleStep = 0;
            }
        }
    }

    private delegate void Sub0330Delegate(Vu1State vu, ref int steps, ref Vector128<float> acc, bool updateFlags);

    [Fact]
    public void GsProcessor_OpenGlMode_SkipsMipmapScratchpadWrites_AndPreservesSoftwareParity()
    {
        static GsProcessor CreateSetup(GsRendererMode mode)
        {
            var gs = new GsProcessor { Mode = mode };
            // Upload an 8x8 PSMCT32 source texture at block 0x0800 (TBW=1 -> 64px)
            ulong bitbltTex = (0x0800ul << 32) | (1ul << 48) | ((ulong)GsPsm.Psmct32 << 56);
            gs.WriteInternalRegister(0x50, bitbltTex);
            gs.WriteInternalRegister(0x51, 0ul);
            gs.WriteInternalRegister(0x52, 8ul | (8ul << 32));
            gs.WriteInternalRegister(0x53, 0ul);

            byte[] pkt = new byte[16 + 8 * 8 * 4];
            BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(0, 8), 16ul | (1ul << 15) | (2ul << 58)); // 16 qwords
            for (int i = 0; i < 64; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(16 + i * 4, 4), 0x80102030u + (uint)i);
            gs.SubmitGifPacket(pkt);
            return gs;
        }

        static void DrawTexturedSprite(GsProcessor gs, int fbpBlock)
        {
            // FRAME_1 at fbpBlock, FBW=1 (64px), PSMCT32
            gs.WriteInternalRegister(0x4C, ((ulong)(fbpBlock / 32) & 0x1FFul) | (1ul << 16));
            gs.WriteInternalRegister(0x18, 0ul);
            gs.WriteInternalRegister(0x40, (63ul << 16) | (63ul << 48));
            ulong tex0 = 0x0800ul | (1ul << 14) | ((ulong)GsPsm.Psmct32 << 20) | (3ul << 26) | (3ul << 30) | (1ul << 34) | (1ul << 35);
            gs.WriteInternalRegister(0x06, tex0);
            gs.WriteInternalRegister(0x00, 0x116ul); // Sprite | TME | FST
            gs.WriteInternalRegister(0x03, 0ul);
            gs.WriteInternalRegister(0x05, 0ul);
            gs.WriteInternalRegister(0x03, (8ul << 4) | ((8ul << 4) << 16));
            gs.WriteInternalRegister(0x05, (8ul << 4) | ((8ul << 4) << 16));
        }

        var gsGl = CreateSetup(GsRendererMode.OpenGl);
        var gsSw = CreateSetup(GsRendererMode.Software);

        // Dead mipmap scratchpad targets in [0x3100, 0x32A0) must be skipped in OpenGL mode, executed in Software mode
        DrawTexturedSprite(gsGl, 0x3100);
        DrawTexturedSprite(gsSw, 0x3100);
        Assert.Equal(0, gsGl.TotalSpritesDrawn);
        Assert.Equal(0u, gsGl.ReadFramePixel(0x3100, 64, GsPsm.Psmct32, 3, 3));
        Assert.Equal(1, gsSw.TotalSpritesDrawn);
        Assert.NotEqual(0u, gsSw.ReadFramePixel(0x3100, 64, GsPsm.Psmct32, 3, 3));

        // 0x2D00 is outside [0x3100, 0x32A0) so OpenGL mode must NOT skip it
        DrawTexturedSprite(gsGl, 0x2D00);
        Assert.Equal(1, gsGl.TotalSpritesDrawn);
    }

    [Fact]
    public void GsProcessor_SpriteBakeCache_CachesMultipleSignaturesAt0x2D00_AndFlushesOnVramRead()
    {
        static void UploadSolidTex(GsProcessor gs, int tbpBlock, uint baseColor)
        {
            ulong bitblt = ((ulong)tbpBlock << 32) | (1ul << 48) | ((ulong)GsPsm.Psmct32 << 56);
            gs.WriteInternalRegister(0x50, bitblt);
            gs.WriteInternalRegister(0x51, 0ul);
            gs.WriteInternalRegister(0x52, 16ul | (16ul << 32));
            gs.WriteInternalRegister(0x53, 0ul);

            byte[] pkt = new byte[16 + 16 * 16 * 4];
            BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(0, 8), 64ul | (1ul << 15) | (2ul << 58)); // 64 qwords
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    uint c = (baseColor & 0xFF000000u) | (((baseColor + (uint)(x * 3 + y * 5)) & 0xFFFFFFu));
                    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(16 + (y * 16 + x) * 4, 4), c);
                }
            }
            gs.SubmitGifPacket(pkt);
        }

        static void BakeSprite(GsProcessor gs, int srcBlock, int dstBlock)
        {
            gs.WriteInternalRegister(0x4C, ((ulong)(dstBlock / 32) & 0x1FFul) | (1ul << 16)); // FBP=dstBlock, FBW=1 (64px)
            gs.WriteInternalRegister(0x47, 0ul); // TEST_1 = 0 (ZTE=0, matching VU1 texture-bake sprites)
            gs.WriteInternalRegister(0x18, 0ul);
            gs.WriteInternalRegister(0x40, (15ul << 16) | (15ul << 48));
            ulong tex0 = (ulong)srcBlock | (1ul << 14) | ((ulong)GsPsm.Psmct32 << 20) | (4ul << 26) | (4ul << 30) | (1ul << 34) | (1ul << 35);
            gs.WriteInternalRegister(0x06, tex0);
            gs.WriteInternalRegister(0x00, 0x116ul); // Sprite | TME | FST
            gs.WriteInternalRegister(0x03, 0ul);
            gs.WriteInternalRegister(0x05, 0ul);
            gs.WriteInternalRegister(0x03, (16ul << 4) | ((16ul << 4) << 16));
            gs.WriteInternalRegister(0x05, (16ul << 4) | ((16ul << 4) << 16));
        }

        var gsGl = new GsProcessor { Mode = GsRendererMode.OpenGl };
        var gsSw = new GsProcessor { Mode = GsRendererMode.Software };

        UploadSolidTex(gsGl, 0x1000, 0x80102030u);
        UploadSolidTex(gsGl, 0x1200, 0x80405060u);
        UploadSolidTex(gsSw, 0x1000, 0x80102030u);
        UploadSolidTex(gsSw, 0x1200, 0x80405060u);

        // Bake A to 0x2D00 (first bake populates VRAM & _spriteBakeCache)
        BakeSprite(gsGl, 0x1000, 0x2D00);
        BakeSprite(gsSw, 0x1000, 0x2D00);
        uint expectedA57 = gsSw.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 5, 7);
        Assert.Equal(expectedA57, gsGl.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 5, 7));

        // Overwrite 0x2D00 with B, then sample 0x2D00 -> 0x0400 via decoded texture cache
        BakeSprite(gsGl, 0x1200, 0x2D00);
        BakeSprite(gsSw, 0x1200, 0x2D00);
        uint expectedB57 = gsSw.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 5, 7);
        Assert.NotEqual(expectedA57, expectedB57);
        Assert.Equal(expectedB57, gsGl.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 5, 7));
        BakeSprite(gsGl, 0x2D00, 0x0400);
        BakeSprite(gsSw, 0x2D00, 0x0400);

        // Re-bake A -> 0x2D00 (hits _spriteBakeCache despite B overwriting 0x2D00; in OpenGL mode VRAM write is deferred)
        BakeSprite(gsGl, 0x1000, 0x2D00);
        BakeSprite(gsSw, 0x1000, 0x2D00);
        var deferredField = typeof(GsProcessor).GetField("_deferredVramBakes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var deferredDict = (System.Collections.IDictionary)deferredField.GetValue(gsGl)!;
        Assert.True(deferredDict.Contains(0x2D00));
        Assert.Equal(expectedB57, gsGl.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 5, 7));

        // CaptureState flushes deferred OpenGL sprite bakes to VRAM; 0x2D00 must hold A and 0x0400 must hold B
        gsGl.CaptureState();
        Assert.Equal(
            gsSw.ReadFramePixel(0x0400, 64, GsPsm.Psmct32, 9, 3),
            gsGl.ReadFramePixel(0x0400, 64, GsPsm.Psmct32, 9, 3));
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal(
                    gsSw.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, x, y),
                    gsGl.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, x, y));
            }
        }

        // Mutating source texture 0x1000 must invalidate cached signature and reflect new pixels
        UploadSolidTex(gsGl, 0x1000, 0x80AABBCCu);
        UploadSolidTex(gsSw, 0x1000, 0x80AABBCCu);
        BakeSprite(gsGl, 0x1000, 0x2D00);
        BakeSprite(gsSw, 0x1000, 0x2D00);
        gsGl.CaptureState();
        Assert.Equal(
            gsSw.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 11, 13),
            gsGl.ReadFramePixel(0x2D00, 64, GsPsm.Psmct32, 11, 13));
    }

    [Fact]
    public void Vu1Aot_GetDirtyRegionMask_IdentifyLoadedRegions_And_Sub0330_WorkAccurately()
    {
        var aotType = typeof(Vu1Recompiler).Assembly.GetType("Castaway.GsRenderer.Vu1.Vu1AotPrograms", throwOnError: true)!;
        const System.Reflection.BindingFlags StaticAll =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        var getDirtyMask = aotType.GetMethod("GetDirtyRegionMaskForRange", StaticAll)!;
        var identifyFull = aotType.GetMethod("IdentifyLoadedRegions", StaticAll, null, [typeof(byte[])], null)!;
        var identifyDirty = aotType.GetMethod("IdentifyLoadedRegions", StaticAll, null, [typeof(byte[]), typeof(uint), typeof(uint)], null)!;

        // 1. Validate dirty region mask & partial CRC32 revalidation behavior
        uint allRegionsMask = (uint)getDirtyMask.Invoke(null, [0, Vu1State.MicroMemSize])!;
        Assert.Equal(0x3Fu, allRegionsMask & 0x3Fu);

        // Wraparound write across 0x4000 must cover Base0 region at offset 0 (bit 0x04) as well
        uint wrapMask = (uint)getDirtyMask.Invoke(null, [0x3FC0, 128])!;
        Assert.NotEqual(0u, wrapMask & 0x04u);

        // Zero dirty mask preserves current mask without scanning MicroMem
        byte[] emptyMicroMem = new byte[Vu1State.MicroMemSize];
        Assert.Equal(0x25u, (uint)identifyDirty.Invoke(null, [emptyMicroMem, 0x25u, 0u])!);
        Assert.Equal(0u, (uint)identifyDirty.Invoke(null, [emptyMicroMem, 0x3Fu, allRegionsMask])!);

        // Load embedded Base0 MPG from SLUS_216.64 .vutext and confirm partial invalidation keeps Base0 detected
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(elfPath);
        var vutext = reader.FindSection(".vutext")!;
        byte[] raw = reader.GetRawBytes();
        byte[] micro = new byte[Vu1State.MicroMemSize];
        int start = (int)vutext.FileOffset;
        int end = start + (int)vutext.Size - 8;
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
            raw.AsSpan(i + 4, mpgBytes).CopyTo(micro.AsSpan((addr * 8) & Vu1State.MicroMemMask, mpgBytes));
            i += 4 + mpgBytes - 4;
        }

        uint fullDetected = (uint)identifyFull.Invoke(null, [micro])!;
        Assert.NotEqual(0u, fullDetected & 0x04u);
        uint revalidated = (uint)identifyDirty.Invoke(null, [micro, 0u, allRegionsMask])!;
        Assert.Equal(fullDetected, revalidated);

        // 2. Validate Sub_Base0_0330 fast block copy vs exact instruction loop reference across fast-path & fallbacks
        var sub0330Method = aotType.GetMethod("Sub_Base0_0330", StaticAll);
        Assert.NotNull(sub0330Method);
        var sub0330 = (Sub0330Delegate)Delegate.CreateDelegate(typeof(Sub0330Delegate), sub0330Method!);

        static void RunReference0330(Vu1State vu, ref int steps)
        {
            while (true)
            {
                vu.VF[1] = MemoryMarshal.Read<Vector128<float>>(vu.DataMem.AsSpan((vu.VI[2] & 0x3FF) << 4, 16));
                vu.VI[2] = (ushort)((vu.VI[2] + 1) & 0x3FF);
                vu.VF[2] = MemoryMarshal.Read<Vector128<float>>(vu.DataMem.AsSpan((vu.VI[2] & 0x3FF) << 4, 16));
                vu.VI[2] = (ushort)((vu.VI[2] + 1) & 0x3FF);
                vu.VF[3] = MemoryMarshal.Read<Vector128<float>>(vu.DataMem.AsSpan((vu.VI[2] & 0x3FF) << 4, 16));
                vu.VI[2] = (ushort)((vu.VI[2] + 1) & 0x3FF);
                vu.VI[4] = unchecked((ushort)(vu.VI[4] - 3));
                MemoryMarshal.Write(vu.DataMem.AsSpan((vu.VI[3] & 0x3FF) << 4, 16), in vu.VF[1]);
                vu.VI[3] = (ushort)((vu.VI[3] + 1) & 0x3FF);
                MemoryMarshal.Write(vu.DataMem.AsSpan((vu.VI[3] & 0x3FF) << 4, 16), in vu.VF[2]);
                vu.VI[3] = (ushort)((vu.VI[3] + 1) & 0x3FF);
                bool cont = (short)vu.VI[4] > 0;
                MemoryMarshal.Write(vu.DataMem.AsSpan((vu.VI[3] & 0x3FF) << 4, 16), in vu.VF[3]);
                vu.VI[3] = (ushort)((vu.VI[3] + 1) & 0x3FF);
                steps += 8;
                if (cont)
                {
                    if (steps >= 65536) return;
                    continue;
                }
                steps += 2;
                return;
            }
        }

        (ushort srcQ, ushort dstQ, short rem, int initSteps)[] cases =
        [
            (10, 200, 15, 0),    // Exact multiple of 3, fast path
            (32, 400, 7, 100),   // Non-multiple remainder (7 -> 3 iters = 9 qwords, vi4 = -2), fast path
            (50, 52, 6, 0),      // Overlapping src/dst, fallback loop
            (1020, 100, 9, 0),   // Source wraps past 1024, fallback loop
            (80, 90, 0, 0),      // rem == 0 executes 1 iteration in fallback loop
            (10, 200, 30, 65510) // Near step cap, partial fast-path bailout -> exact reference step parity
        ];

        foreach (var (srcQ, dstQ, rem, initSteps) in cases)
        {
            var vuAot = new Vu1State();
            var vuRef = new Vu1State();
            for (int b = 0; b < Vu1State.DataMemSize; b++)
            {
                byte v = (byte)((b * 37 + srcQ) & 0xFF);
                vuAot.DataMem[b] = v;
                vuRef.DataMem[b] = v;
            }
            vuAot.VI[2] = vuRef.VI[2] = srcQ;
            vuAot.VI[3] = vuRef.VI[3] = dstQ;
            vuAot.VI[4] = vuRef.VI[4] = unchecked((ushort)rem);

            int stepsAot = initSteps;
            int stepsRef = initSteps;
            Vector128<float> accAot = Vector128<float>.Zero;
            sub0330(vuAot, ref stepsAot, ref accAot, false);
            RunReference0330(vuRef, ref stepsRef);

            Assert.Equal(stepsRef, stepsAot);
            Assert.Equal(vuRef.VI[2], vuAot.VI[2]);
            Assert.Equal(vuRef.VI[3], vuAot.VI[3]);
            Assert.Equal(vuRef.VI[4], vuAot.VI[4]);
            Assert.Equal(vuRef.VF[1], vuAot.VF[1]);
            Assert.Equal(vuRef.VF[2], vuAot.VF[2]);
            Assert.Equal(vuRef.VF[3], vuAot.VF[3]);
            Assert.True(vuRef.DataMem.AsSpan().SequenceEqual(vuAot.DataMem));
        }
    }

    [Fact]
    public void Ps2Vu0_BlendMask_BroadcastBc_VecMax_VecMin_MatchScalarPs2Parity()
    {
        var a = Vector128.Create(1.5f, -2.25f, 0.0f, BitConverter.UInt32BitsToSingle(0x80000001u)); // normal, neg, +0, neg-denorm
        var b = Vector128.Create(10.0f, -20.0f, BitConverter.UInt32BitsToSingle(0x80000000u), BitConverter.UInt32BitsToSingle(0x00000007u)); // normal, neg, -0, +denorm

        for (uint mask = 0; mask <= 15u; mask++)
        {
            var blended = Ps2Vu0.BlendMask(a, b, mask);
            for (int lane = 0; lane < 4; lane++)
            {
                bool takeNew = (mask & (1u << (3 - lane))) != 0;
                uint expectedBits = BitConverter.SingleToUInt32Bits((takeNew ? b : a).GetElement(lane));
                uint actualBits = BitConverter.SingleToUInt32Bits(blended.GetElement(lane));
                Assert.Equal(expectedBits, actualBits);
            }
        }

        for (uint bc = 0; bc < 4u; bc++)
        {
            var broadcast = Ps2Vu0.BroadcastBc(a, bc);
            uint expectedBits = BitConverter.SingleToUInt32Bits(a.GetElement((int)bc));
            for (int lane = 0; lane < 4; lane++)
            {
                Assert.Equal(expectedBits, BitConverter.SingleToUInt32Bits(broadcast.GetElement(lane)));
            }
        }

        uint[] interestingBits =
        [
            0x00000000u, 0x80000000u, // +0, -0
            0x00000001u, 0x80000001u, // +denorm, -denorm
            0x3F800000u, 0xBF800000u, // +1.0, -1.0
            0x42C80000u, 0xC2C80000u, // +100.0, -100.0
            0x7F7FFFFFu, 0xFF7FFFFFu, // +FLT_MAX, -FLT_MAX
            0x7FFFFFFFu, 0xFFFFFFFFu  // +PS2_MAX, -PS2_MAX
        ];

        for (int i = 0; i < interestingBits.Length; i += 4)
        {
            var va = Vector128.Create(
                BitConverter.UInt32BitsToSingle(interestingBits[i]),
                BitConverter.UInt32BitsToSingle(interestingBits[(i + 1) % interestingBits.Length]),
                BitConverter.UInt32BitsToSingle(interestingBits[(i + 2) % interestingBits.Length]),
                BitConverter.UInt32BitsToSingle(interestingBits[(i + 3) % interestingBits.Length]));
            for (int j = 0; j < interestingBits.Length; j += 4)
            {
                var vb = Vector128.Create(
                    BitConverter.UInt32BitsToSingle(interestingBits[(j + 3) % interestingBits.Length]),
                    BitConverter.UInt32BitsToSingle(interestingBits[(j + 2) % interestingBits.Length]),
                    BitConverter.UInt32BitsToSingle(interestingBits[(j + 1) % interestingBits.Length]),
                    BitConverter.UInt32BitsToSingle(interestingBits[j]));

                var vmax = Ps2Vu0.VecMax(va, vb);
                var vmin = Ps2Vu0.VecMin(va, vb);
                for (int lane = 0; lane < 4; lane++)
                {
                    float fa = va.GetElement(lane);
                    float fb = vb.GetElement(lane);
                    Assert.Equal(BitConverter.SingleToUInt32Bits(Ps2Float.Max(fa, fb)), BitConverter.SingleToUInt32Bits(vmax.GetElement(lane)));
                    Assert.Equal(BitConverter.SingleToUInt32Bits(Ps2Float.Min(fa, fb)), BitConverter.SingleToUInt32Bits(vmin.GetElement(lane)));
                }
            }
        }
    }

    [Fact]
    public void GsSwizzle_WriteRect_SpanFastPaths_MatchScalarSwizzle()
    {
        byte[] fastVram = new byte[GsSwizzle.VramByteSize];
        byte[] refVram = new byte[GsSwizzle.VramByteSize];

        // PSMT4 (including odd width, partial offset chunked writes, non-zero dsax/dsay)
        byte[] src4 = new byte[128 * 64];
        for (int i = 0; i < src4.Length; i++) src4[i] = (byte)((i * 73 + 19) & 0xFF);
        int fx4 = 0, fy4 = 0, rx4 = 0, ry4 = 0;
        int c4a = GsSwizzle.WriteRect4(fastVram, 0x100, 128, 3, 5, 65, 37, src4.AsSpan(0, 77), ref fx4, ref fy4);
        int c4b = GsSwizzle.WriteRect4(fastVram, 0x100, 128, 3, 5, 65, 37, src4.AsSpan(c4a), ref fx4, ref fy4);
        foreach (byte packed in src4)
        {
            if (ry4 >= 37) break;
            GsSwizzle.WriteTexel4(refVram, 0x100, 128, 3 + rx4, 5 + ry4, (byte)(packed & 0x0F));
            if (++rx4 >= 65) { rx4 = 0; ry4++; }
            if (ry4 < 37)
            {
                GsSwizzle.WriteTexel4(refVram, 0x100, 128, 3 + rx4, 5 + ry4, (byte)((packed >> 4) & 0x0F));
                if (++rx4 >= 65) { rx4 = 0; ry4++; }
            }
        }
        Assert.Equal(rx4, fx4);
        Assert.Equal(ry4, fy4);
        Assert.True(c4a + c4b > 0);
        Assert.True(refVram.AsSpan().SequenceEqual(fastVram));

        // PSMT8
        Array.Clear(fastVram);
        Array.Clear(refVram);
        byte[] src8 = new byte[130 * 70];
        for (int i = 0; i < src8.Length; i++) src8[i] = (byte)(i * 31 + 7);
        int fx8 = 0, fy8 = 0;
        GsSwizzle.WriteRect8(fastVram, 0x220, 256, 7, 9, 130, 70, src8, ref fx8, ref fy8);
        int idx8 = 0;
        for (int y = 0; y < 70; y++)
            for (int x = 0; x < 130; x++)
                GsSwizzle.WriteTexel8(refVram, 0x220, 256, 7 + x, 9 + y, src8[idx8++]);
        Assert.True(refVram.AsSpan().SequenceEqual(fastVram));

        // PSMCT16S & PSMZ16
        foreach (var (is16S, isZ) in new[] { (true, false), (false, true) })
        {
            Array.Clear(fastVram);
            Array.Clear(refVram);
            byte[] src16 = new byte[70 * 40 * 2];
            for (int i = 0; i < src16.Length; i++) src16[i] = (byte)(i * 17 + 3);
            int fx16 = 0, fy16 = 0;
            GsSwizzle.WriteRect16(fastVram, 0x300, 128, 5, 3, 70, 40, is16S, isZ, src16, ref fx16, ref fy16);
            int off16 = 0;
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 70; x++, off16 += 2)
                    GsSwizzle.WritePixel16(refVram, 0x300, 128, 5 + x, 3 + y, BinaryPrimitives.ReadUInt16LittleEndian(src16.AsSpan(off16, 2)), is16S, isZ);
            Assert.True(refVram.AsSpan().SequenceEqual(fastVram));
        }

        // PSMCT32 & PSMZ32
        foreach (bool isZ in new[] { false, true })
        {
            Array.Clear(fastVram);
            Array.Clear(refVram);
            byte[] src32 = new byte[72 * 40 * 4];
            for (int i = 0; i < src32.Length; i++) src32[i] = (byte)(i * 29 + 11);
            int fx32 = 0, fy32 = 0;
            GsSwizzle.WriteRect32(fastVram, 0x400, 128, 3, 2, 72, 40, isZ, src32, ref fx32, ref fy32);
            int off32 = 0;
            for (int y = 0; y < 40; y++)
                for (int x = 0; x < 72; x++, off32 += 4)
                    GsSwizzle.WritePixel32(refVram, 0x400, 128, 3 + x, 2 + y, BinaryPrimitives.ReadUInt32LittleEndian(src32.AsSpan(off32, 4)), isZ);
            Assert.True(refVram.AsSpan().SequenceEqual(fastVram));
        }
    }

    [Fact]
    public void GsProcessor_BuildDrawContext_InvalidatesOnRegisterMutations()
    {
        var gs = new GsProcessor { Mode = GsRendererMode.Software };

        // Configure Context 1 solid flat triangle (PRIM=3: Triangle) and draw 2 triangles at distinct colors
        gs.WriteInternalRegister(0x18, 0ul); // XYOFFSET_1 = (0, 0)
        gs.WriteInternalRegister(0x4C, 0x00000000000A0000ul); // FRAME_1: FBP=0, FBW=640, PSMCT32
        gs.WriteInternalRegister(0x47, 0x0000000000030000ul); // TEST_1: ZTE=1, ZTST=ALWAYS, ATE=0
        gs.WriteInternalRegister(0x00, 0x03ul); // PRIM = Triangle, flat

        // Triangle 1 at (10..20, 10..20) color Red
        gs.WriteInternalRegister(0x01, 0x8000007Ful);
        gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)20 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)20 << 4) << 16));

        // Triangle 2 (cache hit) at (30..40, 10..20) color Green
        gs.WriteInternalRegister(0x01, 0x80007F00ul);
        gs.WriteInternalRegister(0x05, ((ulong)30 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)40 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)30 << 4) | (((ulong)20 << 4) << 16));

        // Switch FRAME_1 to FBP block 0x80 (FBP=4 -> block 128) -> must invalidate cached DrawContext!
        gs.WriteInternalRegister(0x4C, 0x00000000000A0004ul);
        gs.WriteInternalRegister(0x01, 0x807F0000ul); // Blue
        gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)20 << 4) | (((ulong)10 << 4) << 16));
        gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)20 << 4) << 16));

        // Original framebuffer FBP=0 retains Triangle 1 Red and Triangle 2 Green
        Assert.Equal(0x8000007Fu, gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 12, 12));
        Assert.Equal(0x80007F00u, gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 32, 12));
        // Second framebuffer FBP=0x80 received Triangle 3 Blue
        Assert.Equal(0x807F0000u, gs.ReadFramePixel(0x80, 640, GsPsm.Psmct32, 12, 12));
    }

    [Fact]
    public void Ovl2_ParticleMicroprogram_AotMatchesDecoded_AndPreservesHorizontalPositions()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(elfPath);
        byte[] raw = reader.GetRawBytes();

        // Load Ovl2 (0x0048CFA8, 2048 bytes -> dst 0x15E0) from SLUS_216.64 into MicroMem
        var vutext = reader.FindSection(".vutext")!;
        uint fileOff = vutext.FileOffset + (0x0048CFA8u - vutext.VAddr);

        var vuAot = new Vu1State();
        var vuDec = new Vu1State();
        raw.AsSpan((int)fileOff, 2048).CopyTo(vuAot.MicroMem.AsSpan(0x15E0, 2048));
        raw.AsSpan((int)fileOff, 2048).CopyTo(vuDec.MicroMem.AsSpan(0x15E0, 2048));

        // Identify loaded regions via Vu1AotPrograms
        var aotType = typeof(Vu1Recompiler).Assembly.GetType("Castaway.GsRenderer.Vu1.Vu1AotPrograms")!;
        var identifyFull = aotType.GetMethod("IdentifyLoadedRegions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, [typeof(byte[])])!;
        uint regionMask = (uint)identifyFull.Invoke(null, [vuAot.MicroMem])!;
        Assert.NotEqual(0u, regionMask & 2u);

        // Setup shared VU1 constants matching sub_003E8850 (0x3A3..0x3B5) and screen proj (0x43..0x46)
        void SetupVuData(Vu1State vu)
        {
            vu.Top = 0x49;
            vu.VI[1] = 0x49;
            // Identity world->view at 0x3A3..0x3A6, view->screen at 0x43..0x46
            static void WriteMat(byte[] dm, int qw, float tz)
            {
                MemoryMarshal.Write(dm.AsSpan((qw + 0) << 4, 16), Vector128.Create(1f, 0f, 0f, 0f));
                MemoryMarshal.Write(dm.AsSpan((qw + 1) << 4, 16), Vector128.Create(0f, 1f, 0f, 0f));
                MemoryMarshal.Write(dm.AsSpan((qw + 2) << 4, 16), Vector128.Create(0f, 0f, 1f, 0f));
                MemoryMarshal.Write(dm.AsSpan((qw + 3) << 4, 16), Vector128.Create(0f, 0f, tz, 1f));
            }
            WriteMat(vu.DataMem, 0x3A3, 10f);
            // Perspective-like screen matrix at 0x43..0x46: x' = x*500, y' = y*500, z' = z, w' = z
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x43 << 4, 16), Vector128.Create(500f, 0f, 0f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x44 << 4, 16), Vector128.Create(0f, 500f, 0f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x45 << 4, 16), Vector128.Create(0f, 0f, 1f, 1f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x46 << 4, 16), Vector128.Create(0f, 0f, 0f, 0f));

            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3A7 << 4, 16), Vector128.Create(2048f, 2048f, 1f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3A8 << 4, 16), Vector128.Create(100f, 100f, 1f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3A9 << 4, 16), Vector128.Create(1250f, 2048f, 1250.2f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3AA << 4, 16), Vector128.Create(0.5f, 1f, 0f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3AB << 4, 16), Vector128.Create(1f, 1f, 0f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3AC << 4, 16), Vector128.Create(1f, 1f, 1f, 1f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3AE << 4, 16), Vector128.Create(0f, 0f, 0f, 0f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3AF << 4, 16), Vector128.Create(0f, 0f, 2048f, 2048f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3B0 << 4, 16), Vector128.Create(1f, 1f, 1f, 1f));
            MemoryMarshal.Write(vu.DataMem.AsSpan(0x3B2 << 4, 16), Vector128.Create(0f, -9.8f, 0f, 1f));
            BinaryPrimitives.WriteUInt64LittleEndian(vu.DataMem.AsSpan((0x3B3 << 4) + 0, 8), 0x302A400000008010UL);
            BinaryPrimitives.WriteUInt64LittleEndian(vu.DataMem.AsSpan((0x3B3 << 4) + 8, 8), 0x0412UL);

            int top = 0x49;
            MemoryMarshal.Write(vu.DataMem.AsSpan((top + 99) << 4, 16), Vector128.Create(1f, 1f, 1f, 1f));

            // Pack particle span [0, 3) (start1=0, end1=3, start2=0, end2=0) into dm[top+0]
            BinaryPrimitives.WriteUInt32LittleEndian(vu.DataMem.AsSpan((top << 4) + 0, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(vu.DataMem.AsSpan((top << 4) + 4, 4), 3u);
            BinaryPrimitives.WriteUInt32LittleEndian(vu.DataMem.AsSpan((top << 4) + 8, 4), 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(vu.DataMem.AsSpan((top << 4) + 12, 4), 0u);

            // 3 particles at distinctly different X positions (-2, 0, +2)
            float[] xs = [-2f, 0f, 2f];
            for (int p = 0; p < 3; p++)
            {
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 1 + p) << 4), 16), Vector128.Create(0.5f));
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 17 + p) << 4), 16), Vector128.Create(0.5f));
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 33 + p) << 4), 16), Vector128.Create(1.0f));
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 49 + p) << 4), 16), Vector128.Create(128, 128, 128, 128));
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 65 + p * 2) << 4), 16), Vector128.Create(xs[p], 0f, 0f, 1f));
                MemoryMarshal.Write(vu.DataMem.AsSpan(((top + 65 + p * 2 + 1) << 4), 16), Vector128.Create(xs[p], -1f, 0f, 1f));
            }
        }

        SetupVuData(vuAot);
        SetupVuData(vuDec);

        const System.Reflection.BindingFlags AnyStatic =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        const System.Reflection.BindingFlags AnyInst =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var decType = typeof(Vu1Recompiler).Assembly.GetType("Castaway.GsRenderer.Vu1.Vu1DecodedExecutor", throwOnError: true)!;
        object decExec = Activator.CreateInstance(decType, nonPublic: true)!;
        var decExecute = decType.GetMethod("Execute", AnyInst, [typeof(Vu1State), typeof(ushort)])!;
        var aotTryExecute = aotType.GetMethod("TryExecute", AnyStatic, [typeof(Vu1State), typeof(ushort), typeof(uint)])!;

        List<byte[]> aotKicks = [], decKicks = [];
        vuAot.OnXgKick = s => aotKicks.Add(s.ToArray());
        vuDec.OnXgKick = s => decKicks.Add(s.ToArray());

        // Execute 0x15E0 then 0x16F8
        Assert.True((bool)aotTryExecute.Invoke(null, [vuAot, (ushort)0x15E0, regionMask])!);
        decExecute.Invoke(decExec, [vuDec, (ushort)0x15E0]);
        Assert.True((bool)aotTryExecute.Invoke(null, [vuAot, (ushort)0x16F8, regionMask])!);
        decExecute.Invoke(decExec, [vuDec, (ushort)0x16F8]);

        for (int i = 0; i < 32; i++)
            Assert.Equal(vuDec.VF[i], vuAot.VF[i]);
        for (int i = 0; i < 16; i++)
            Assert.Equal(vuDec.VI[i], vuAot.VI[i]);
        Assert.Equal(decKicks.Count, aotKicks.Count);
        for (int k = 0; k < decKicks.Count; k++)
            Assert.True(decKicks[k].AsSpan().SequenceEqual(aotKicks[k]), $"Kick {k} mismatch");
        Assert.True(vuDec.DataMem.AsSpan().SequenceEqual(vuAot.DataMem));

        // Also execute 0x1CE8 (second MSCNT pass used by Ovl2) and verify AOT == Decoded
        Assert.True((bool)aotTryExecute.Invoke(null, [vuAot, (ushort)0x1CE8, regionMask])!);
        decExecute.Invoke(decExec, [vuDec, (ushort)0x1CE8]);
        Assert.Equal(decKicks.Count, aotKicks.Count);
        for (int k = 0; k < decKicks.Count; k++)
            Assert.True(decKicks[k].AsSpan().SequenceEqual(aotKicks[k]), $"Kick {k} (after 0x1CE8) mismatch");

        // Verify distinct particle X coordinates in the emitted XGKICK packet (xs = [-2, 0, +2])
        Assert.NotEmpty(aotKicks);
        AssertDistinctQuadXCoords(aotKicks, expectedParticles: 3);
    }

    [Fact]
    public void Ovl2_Vif1ParticlePacket_PreservesDistinctScreenX_AndSizes()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(elfPath);
        byte[] raw = reader.GetRawBytes();
        var vutext = reader.FindSection(".vutext")!;
        uint fileOff = vutext.FileOffset + (0x0048CFA8u - vutext.VAddr);

        using var pipeline = new GsGraphicsPipeline();
        var prevKick = pipeline.Vu1.OnXgKick;
        List<byte[]> kicks = [];
        pipeline.Vu1.OnXgKick = s => { kicks.Add(s.ToArray()); prevKick?.Invoke(s); };

        // Load Ovl2 via VIF1 MPG at 0x15E0 (0x2BC * 8) -> wait: 0x15E0 / 8 = 0x2BC; 2048 bytes = 256 insns (num=0)
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Upload Ovl2 (256 instructions = 2048 bytes -> MPG num=0 at addr=0x2BC)
        bw.Write(MakeVifCode(0x4A, 0, 0x15E0 / 8));
        bw.Write(raw, (int)fileOff, 2048);

        // Setup BASE=0x049, OFFSET=0x049 (via 0x03 BASE, 0x02 OFFSET) and STCYCL WL=1, CL=1
        bw.Write(MakeVifCode(0x03, 0, 0x0049)); // BASE
        bw.Write(MakeVifCode(0x02, 0, 0x0049)); // OFFSET
        bw.Write(MakeVifCode(0x01, 0, 0x0101)); // STCYCL WL=1, CL=1

        // Screen matrix at dm[0x43..0x46] (FLG=0, addr=0x43, num=4, V4-32 = 0x6C)
        bw.Write(MakeVifCode(0x6C, 4, 0x0043));
        WriteVec4(bw, 500f, 0f, 0f, 0f);
        WriteVec4(bw, 0f, 500f, 0f, 0f);
        WriteVec4(bw, 0f, 0f, 1f, 1f);
        WriteVec4(bw, 0f, 0f, 0f, 0f);

        // Per-emitter header (FLG=0): world->view mat at 0x3A3..0x3A6 (4) + 12 constants 0x3A7..0x3B2
        static void WriteVec4(BinaryWriter w, float x, float y, float z, float wVal)
        {
            w.Write(x); w.Write(y); w.Write(z); w.Write(wVal);
        }
        bw.Write(MakeVifCode(0x6C, 17, 0x03A3));
        WriteVec4(bw, 1f, 0f, 0f, 0f);
        WriteVec4(bw, 0f, 1f, 0f, 0f);
        WriteVec4(bw, 0f, 0f, 1f, 0f);
        WriteVec4(bw, 0f, 0f, 10f, 1f);
        WriteVec4(bw, 2048f, 2048f, 1f, 0f);   // 0x3A7
        WriteVec4(bw, 100f, 100f, 1f, 0f);     // 0x3A8
        WriteVec4(bw, 1250f, 2048f, 1250.2f, 0f); // 0x3A9
        WriteVec4(bw, 0.5f, 1f, 0f, 0f);       // 0x3AA
        WriteVec4(bw, 1f, 1f, 0f, 0f);         // 0x3AB
        WriteVec4(bw, 1f, 1f, 1f, 1f);         // 0x3AC
        WriteVec4(bw, 0f, 0f, 0f, 0f);         // 0x3AD
        WriteVec4(bw, 0f, 0f, 0f, 0f);         // 0x3AE
        WriteVec4(bw, 0f, 0f, 2048f, 2048f);   // 0x3AF
        WriteVec4(bw, 1f, 1f, 1f, 1f);         // 0x3B0
        WriteVec4(bw, 0f, 0f, 0f, 0f);         // 0x3B1
        WriteVec4(bw, 0f, -9.8f, 0f, 1f);      // 0x3B2
        bw.Write(0x302A400000008010UL);        // 0x3B3 GIFTAG lo
        bw.Write(0x0412UL);                    // 0x3B3 GIFTAG hi

        // Also dm[TOP+99] = (1,1,1,1) -> absolute 0x49 + 99 = 0xAC
        bw.Write(MakeVifCode(0x6C, 1, 0x00AC));
        WriteVec4(bw, 1f, 1f, 1f, 1f);

        // MSCALF 0x15E0 (0x15E0 / 8 = 0x2BC)
        bw.Write(MakeVifCode(0x15, 0, 0x15E0 / 8));

        // Exact 48-QW packet format from sub_003E9F48:
        // 0x6E018000 (V4-8, FLG=1, num=1, addr=0): 4 bytes (Wait! in sub_003E9F48 4($v1) has start1, end1? Wait: V4-8 is 4 bytes -> 4x32 ints!)
        bw.Write(0x6E018000u);
        bw.Write((sbyte)0); bw.Write((sbyte)3); bw.Write((sbyte)0); bw.Write((sbyte)0);

        // 0x60308001 (S-32, FLG=1, num=48, addr=1): 48 floats (t0..t15, size0..size15, rot0..rot15)
        bw.Write(0x60308001u);
        for (int i = 0; i < 16; i++) bw.Write(0.5f);
        for (int i = 0; i < 16; i++) bw.Write(0.5f);
        for (int i = 0; i < 16; i++) bw.Write(1.0f);

        // 0x6C208041 (V4-32, FLG=1, num=32, addr=65): 32 vec4s (16 particles * pos+vel)
        bw.Write(0x6C208041u);
        float[] xs = [-2f, 0f, 2f];
        for (int i = 0; i < 16; i++)
        {
            float px = i < 3 ? xs[i] : 0f;
            WriteVec4(bw, px, 0f, 0f, 1f);
            WriteVec4(bw, px, -1f, 0f, 1f);
        }

        // 0x6F10C031 (V4-5, USN=1, FLG=1, num=16, addr=49): 16 ushort RGBA5551
        bw.Write(0x6F10C031u);
        for (int i = 0; i < 16; i++) bw.Write((ushort)0xFFFF);

        // 0x17000000 (MSCNT)
        bw.Write(0x17000000u);

        pipeline.Vif1.ProcessData(ms.ToArray());

        Assert.NotEmpty(kicks);
        AssertDistinctQuadXCoords(kicks, expectedParticles: 3);
    }

    [Fact]
    public void Ovl2_MultiPacketMscnt_LoopsBackTo16F8_OnEndDelaySlotBranch_WithoutEntering1CE8()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(elfPath);
        byte[] raw = reader.GetRawBytes();
        var vutext = reader.FindSection(".vutext")!;
        uint fileOff = vutext.FileOffset + (0x0048CFA8u - vutext.VAddr);

        using var pipeline = new GsGraphicsPipeline();
        var prevKick = pipeline.Vu1.OnXgKick;
        List<byte[]> kicks = [];
        pipeline.Vu1.OnXgKick = s => { kicks.Add(s.ToArray()); prevKick?.Invoke(s); };

        static void WriteVec4(BinaryWriter w, float x, float y, float z, float wVal)
        {
            w.Write(x); w.Write(y); w.Write(z); w.Write(wVal);
        }

        // First packet initializes Ovl2 (MSCALF 0x15E0) + sends packet 0 + MSCNT.
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(MakeVifCode(0x4A, 0, 0x15E0 / 8));
            bw.Write(raw, (int)fileOff, 2048);
            bw.Write(MakeVifCode(0x03, 0, 0x0049)); // BASE
            bw.Write(MakeVifCode(0x02, 0, 0x0049)); // OFFSET
            bw.Write(MakeVifCode(0x01, 0, 0x0101)); // STCYCL WL=1, CL=1

            bw.Write(MakeVifCode(0x6C, 4, 0x0043));
            WriteVec4(bw, 500f, 0f, 0f, 0f);
            WriteVec4(bw, 0f, 500f, 0f, 0f);
            WriteVec4(bw, 0f, 0f, 1f, 1f);
            WriteVec4(bw, 0f, 0f, 0f, 0f);

            bw.Write(MakeVifCode(0x6C, 17, 0x03A3));
            WriteVec4(bw, 1f, 0f, 0f, 0f);
            WriteVec4(bw, 0f, 1f, 0f, 0f);
            WriteVec4(bw, 0f, 0f, 1f, 0f);
            WriteVec4(bw, 0f, 0f, 10f, 1f);
            WriteVec4(bw, 2048f, 2048f, 1f, 0f);
            WriteVec4(bw, 100f, 100f, 1f, 0f);
            WriteVec4(bw, 1250f, 2048f, 1250.2f, 0f);
            WriteVec4(bw, 0.5f, 1f, 0f, 0f);
            WriteVec4(bw, 1f, 1f, 0f, 0f);
            WriteVec4(bw, 1f, 1f, 1f, 1f);
            WriteVec4(bw, 0f, 0f, 0f, 0f);
            WriteVec4(bw, 0f, 0f, 0f, 0f);
            WriteVec4(bw, 0f, 0f, 2048f, 2048f);
            WriteVec4(bw, 1f, 1f, 1f, 1f);
            WriteVec4(bw, 0f, 0f, 0f, 0f);
            WriteVec4(bw, 0f, -9.8f, 0f, 1f);
            bw.Write(0x302A400000008010UL);
            bw.Write(0x0412UL);

            bw.Write(MakeVifCode(0x6C, 1, 0x00AC));
            WriteVec4(bw, 1f, 1f, 1f, 1f);
            bw.Write(MakeVifCode(0x15, 0, 0x15E0 / 8)); // MSCALF 0x15E0
            pipeline.Vif1.ProcessData(ms.ToArray());
        }

        // Before [923] [E] NOP | B 0x16F8 fix, MSCNT #1 exited with Tpc = 0x1CE8 instead of 0x16F8,
        // causing MSCNT #2/#3 to enter mid-loop at 0x1CE8 and emit garbage quads.
        float[][] packetXs =
        [
            [-2f, 0f, 2f],
            [-3f, -1f, 1.5f],
            [-1.5f, 0.5f, 3f]
        ];

        for (int p = 0; p < packetXs.Length; p++)
        {
            int kicksBefore = kicks.Count;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(0x6E018000u);
            bw.Write((sbyte)0); bw.Write((sbyte)3); bw.Write((sbyte)0); bw.Write((sbyte)0);

            bw.Write(0x60308001u);
            for (int i = 0; i < 16; i++) bw.Write(0.5f);
            for (int i = 0; i < 16; i++) bw.Write(0.5f);
            for (int i = 0; i < 16; i++) bw.Write(1.0f);

            bw.Write(0x6C208041u);
            for (int i = 0; i < 16; i++)
            {
                float px = i < 3 ? packetXs[p][i] : 0f;
                WriteVec4(bw, px, 0f, 0f, 1f);
                WriteVec4(bw, px, -1f, 0f, 1f);
            }

            bw.Write(0x6F10C031u);
            for (int i = 0; i < 16; i++) bw.Write((ushort)0xFFFF);

            bw.Write(0x17000000u); // MSCNT
            pipeline.Vif1.ProcessData(ms.ToArray());

            Assert.Equal((ushort)0x16F8, pipeline.Vu1.Tpc);
            Assert.Equal(kicksBefore + 1, kicks.Count);
            AssertDistinctQuadXCoords([kicks[^1]], expectedParticles: 3);
        }
    }

    private static void AssertDistinctQuadXCoords(List<byte[]> kicks, int expectedParticles)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        var summary = new System.Text.StringBuilder();
        foreach (byte[] kick in kicks)
        {
            if (kick.Length < 16) continue;
            ulong tagLo = BinaryPrimitives.ReadUInt64LittleEndian(kick.AsSpan(0, 8));
            ulong tagHi = BinaryPrimitives.ReadUInt64LittleEndian(kick.AsSpan(8, 8));
            int nloop = (int)(tagLo & 0x7FFFu);
            int flg = (int)((tagLo >> 58) & 0x3u);
            int nreg = (int)((tagLo >> 60) & 0xFu);
            if (nreg == 0) nreg = 16;
            summary.Append($"[len={kick.Length},nloop={nloop},flg={flg},nreg={nreg},regs=0x{tagHi:X}] ");

            int off = 16;
            for (int loop = 0; loop < nloop; loop++)
            {
                if (flg == 0) // PACKED
                {
                    for (int r = 0; r < nreg && off + 16 <= kick.Length; r++, off += 16)
                    {
                        byte reg = (byte)((tagHi >> (r * 4)) & 0xFu);
                        if (reg == 0x0E) // A+D
                        {
                            byte adReg = kick[off + 8];
                            if (adReg == 0x04 || adReg == 0x05)
                            {
                                xs.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off, 2)) / 16.0f);
                                ys.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off + 4, 2)) / 16.0f);
                            }
                        }
                        else if (reg == 0x04 || reg == 0x05) // XYZF2 / XYZ2
                        {
                            xs.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off, 2)) / 16.0f);
                            ys.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off + 4, 2)) / 16.0f);
                        }
                    }
                }
                else if (flg == 1) // REGLIST (64-bit per reg)
                {
                    for (int r = 0; r < nreg && off + 8 <= kick.Length; r++, off += 8)
                    {
                        byte reg = (byte)((tagHi >> (r * 4)) & 0xFu);
                        if (reg == 0x04 || reg == 0x05)
                        {
                            xs.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off, 2)) / 16.0f);
                            ys.Add(BinaryPrimitives.ReadUInt16LittleEndian(kick.AsSpan(off + 2, 2)) / 16.0f);
                        }
                    }
                }
            }
        }
        Assert.True(xs.Count >= expectedParticles * 2, $"Too few XYZ vertices ({xs.Count}): {summary}");
        float minX = xs.Min();
        float maxX = xs.Max();
        Assert.True(maxX - minX > 20f, $"Particle quads collapsed horizontally! minX={minX}, maxX={maxX}, xs=[{string.Join(", ", xs.Take(16))}], info={summary}");
    }

    [Fact]
    public void EeFastParticle_RealSpawnCode_Produces3DSpread_AcrossEmitterShapes()
    {
        Castaway.Generated.RecompiledFunctions.RegisterAll();
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

            // Init RNG: sub_003E9338(seed) + sub_003E92B8()
            ctx.SetU64(4, 0x12345678UL);
            RuntimeDispatcher.Call(0x003E9338u, &ctx, ram);
            RuntimeDispatcher.Call(0x003E92B8u, &ctx, ram);

            // Allocate guest scratch structs at 0x01200000..
            uint typeAddr = 0x01200000u;
            uint emitAddr = 0x01201000u;
            uint matAddr = 0x01202000u;
            uint partAddr = 0x01203000u;

            // Rotated+translated 4x4 matrix at matAddr and store pointer at 0x0057B500
            MemoryMarshal.Write(new Span<byte>(ram + matAddr + 0, 16), Vector128.Create(0.8f, 0f, 0.6f, 0f));
            MemoryMarshal.Write(new Span<byte>(ram + matAddr + 16, 16), Vector128.Create(0f, 1f, 0f, 0f));
            MemoryMarshal.Write(new Span<byte>(ram + matAddr + 32, 16), Vector128.Create(-0.6f, 0f, 0.8f, 0f));
            MemoryMarshal.Write(new Span<byte>(ram + matAddr + 48, 16), Vector128.Create(10f, 20f, 30f, 1f));
            Ps2Memory.Write32(&ctx, ram, 0x0057B500u, matAddr);

            static void WriteF32(CpuContext* c, byte* r, uint addr, float val)
                => Ps2Memory.Write32(c, r, addr, BitConverter.SingleToUInt32Bits(val));
            static float ReadF32(CpuContext* c, byte* r, uint addr)
                => BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, addr));

            // Emitter fields
            WriteF32(&ctx, ram, emitAddr + 392u, 0.25f);
            Ps2Memory.Write32(&ctx, ram, emitAddr + 400u, typeAddr);

            // Run configurations of emitterType flags
            (string name, uint f1, uint f2)[] cases =
            [
                ("ConeSphere", 0x0038u, 0x0040u),
                ("AsinSphere", 0x0038u, 0x8040u),
                ("BoxPlusCone", 0x0238u, 0x0040u),
                ("MatrixBoxCone", 0x4638u, 0x0040u),
                ("BoxDivSphere", 0x00010638u, 0x0040u),
                ("Ring2D", 0x0000u, 0x0002u),
            ];

            foreach (var (name, f1, f2) in cases)
            {
                new Span<byte>(ram + typeAddr, 256).Clear();
                WriteF32(&ctx, ram, typeAddr + 24u, 2.0f);         // speed min
                WriteF32(&ctx, ram, typeAddr + 28u, 1.0f);         // speed range
                WriteF32(&ctx, ram, typeAddr + 32u, 1.5f);         // ring radius
                WriteF32(&ctx, ram, typeAddr + 36u, 2.0f);         // radius range
                WriteF32(&ctx, ram, typeAddr + 40u, 0.0f);         // theta min
                WriteF32(&ctx, ram, typeAddr + 44u, 1.5707964f);   // theta range (pi/2 so sin(pi/2)-sin(0)=1)
                Ps2Memory.Write32(&ctx, ram, typeAddr + 72u, f1);  // flags1
                Ps2Memory.Write32(&ctx, ram, typeAddr + 76u, f2);  // flags2
                WriteF32(&ctx, ram, typeAddr + 80u, 1.5f);         // box x
                WriteF32(&ctx, ram, typeAddr + 84u, 1.5f);         // box y
                WriteF32(&ctx, ram, typeAddr + 88u, 1.5f);         // box z
                WriteF32(&ctx, ram, typeAddr + 108u, 1.5f);        // phi cone

                var xs = new List<float>();
                var ys = new List<float>();
                var zs = new List<float>();
                for (int i = 0; i < 16; i++)
                {
                    new Span<byte>(ram + partAddr, 32).Clear();
                    ctx.SetU64(4, emitAddr);
                    ctx.SetU64(5, partAddr);
                    ctx.F[12] = i / 16.0f;
                    ctx.F[13] = 1.0f / 30.0f;
                    RuntimeDispatcher.Call(0x003E62A0u, &ctx, ram);
                    xs.Add(ReadF32(&ctx, ram, partAddr + 0u));
                    ys.Add(ReadF32(&ctx, ram, partAddr + 4u));
                    zs.Add(ReadF32(&ctx, ram, partAddr + 8u));
                }

                float spanX = xs.Max() - xs.Min();
                float spanY = ys.Max() - ys.Min();
                float spanZ = zs.Max() - zs.Min();
                Assert.True(spanX > 0.5f && xs.All(float.IsFinite), $"{name} collapsed/NaN X: spanX={spanX}, xs=[{string.Join(", ", xs.Take(6))}]");
                Assert.True(spanY > 0.5f && ys.All(float.IsFinite), $"{name} collapsed/NaN Y: spanY={spanY}, ys=[{string.Join(", ", ys.Take(6))}]");
                if (name != "Ring2D")
                    Assert.True(spanZ > 0.2f && zs.All(float.IsFinite), $"{name} collapsed/NaN Z: spanZ={spanZ}, zs=[{string.Join(", ", zs.Take(6))}]");
            }
        }
    }

    [Fact]
    public void Cop0Count_SeedsFastParticleRng_AndPortalEmitterProducesHorizontalSpread()
    {
        Castaway.Generated.RecompiledFunctions.RegisterAll();
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

            // Real boot sequence calls 0x00431380 (mfc0 $v0, $c0_9) -> sub_003E9338(seed) -> sub_003E92B8().
            RuntimeDispatcher.Call(0x00431380u, &ctx, ram);
            uint seed = ctx.GetU32(2);
            Assert.NotEqual(0u, seed);

            ctx.SetU64(4, seed);
            RuntimeDispatcher.Call(0x003E9338u, &ctx, ram);
            RuntimeDispatcher.Call(0x003E92B8u, &ctx, ram);

            uint nonZeroEntries = 0;
            for (uint i = 0; i < 128; i++)
            {
                if (Ps2Memory.Read32(&ctx, ram, 0x0057B300u + i * 4u) != 0u)
                    nonZeroEntries++;
            }
            Assert.True(nonZeroEntries > 100, $"Expected seeded RNG table at 0x0057B300, got only {nonZeroEntries}/128 non-zero");

            uint typeAddr = 0x01204000u;
            uint emitAddr = 0x01205000u;
            uint partAddr = 0x01206000u;
            static void WriteF32(CpuContext* c, byte* r, uint addr, float val)
                => Ps2Memory.Write32(c, r, addr, BitConverter.SingleToUInt32Bits(val));
            static float ReadF32(CpuContext* c, byte* r, uint addr)
                => BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, addr));

            new Span<byte>(ram + typeAddr, 256).Clear();
            WriteF32(&ctx, ram, typeAddr + 24u, -1.0f);       // spd min
            WriteF32(&ctx, ram, typeAddr + 28u, 0.0f);        // spd range
            WriteF32(&ctx, ram, typeAddr + 32u, 0.0f);        // radius
            WriteF32(&ctx, ram, typeAddr + 36u, 0.0f);        // radius range
            WriteF32(&ctx, ram, typeAddr + 40u, 1.5707964f);  // theta min
            WriteF32(&ctx, ram, typeAddr + 44u, 0.0872665f);  // theta range
            Ps2Memory.Write32(&ctx, ram, typeAddr + 72u, 0x042402AFu); // portal flags1
            Ps2Memory.Write32(&ctx, ram, typeAddr + 76u, 0xC0000040u); // portal flags2
            WriteF32(&ctx, ram, typeAddr + 80u, 0.4f);        // box x
            WriteF32(&ctx, ram, typeAddr + 84u, 0.4f);        // box y
            WriteF32(&ctx, ram, typeAddr + 88u, 0.0f);        // box z
            WriteF32(&ctx, ram, typeAddr + 108u, 6.2831855f); // phi cone

            Ps2Memory.Write32(&ctx, ram, emitAddr + 400u, typeAddr);

            var xs = new List<float>();
            var ys = new List<float>();
            for (int i = 0; i < 16; i++)
            {
                new Span<byte>(ram + partAddr, 32).Clear();
                ctx.SetU64(4, emitAddr);
                ctx.SetU64(5, partAddr);
                ctx.F[12] = i / 16.0f;
                ctx.F[13] = 1.0f / 30.0f;
                RuntimeDispatcher.Call(0x003E62A0u, &ctx, ram);
                xs.Add(ReadF32(&ctx, ram, partAddr + 0u));
                ys.Add(ReadF32(&ctx, ram, partAddr + 4u));
            }

            float spanX = xs.Max() - xs.Min();
            float spanY = ys.Max() - ys.Min();
            Assert.True(spanX > 0.25f, $"Portal emitter X still collapsed: spanX={spanX}, xs=[{string.Join(", ", xs.Take(6))}]");
            Assert.True(spanY > 0.25f, $"Portal emitter Y still collapsed: spanY={spanY}, ys=[{string.Join(", ", ys.Take(6))}]");
        }
    }

    [Fact]
    public void EeFastParticle_FullSpawnAndTick_PopulatesValidPacket_WithSpreadAndBoundedSizes()
    {
        Castaway.Generated.RecompiledFunctions.RegisterAll();
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

            ctx.SetU64(4, 0x12345678UL);
            RuntimeDispatcher.Call(0x003E9338u, &ctx, ram);
            RuntimeDispatcher.Call(0x003E92B8u, &ctx, ram);

            static void WriteF32(CpuContext* c, byte* r, uint addr, float val)
                => Ps2Memory.Write32(c, r, addr, BitConverter.SingleToUInt32Bits(val));
            static float ReadF32(CpuContext* c, byte* r, uint addr)
                => BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, addr));
            static void WriteIdentity(byte* r, uint addr)
            {
                for (int i = 0; i < 4; i++)
                    MemoryMarshal.Write(new Span<byte>(r + addr + i * 16, 16), Vector128.Create(i == 0 ? 1f : 0f, i == 1 ? 1f : 0f, i == 2 ? 1f : 0f, i == 3 ? 1f : 0f));
            }

            uint typeAddr = 0x01210000u;
            uint emitAddr = 0x01211000u;
            uint hdrAddr = 0x01212000u;
            uint pktPtrArr = 0x01213000u;
            uint pktAddr = 0x01214000u;
            uint rgbaAddr = 0x01215000u;

            new Span<byte>(ram + typeAddr, 0x6000).Clear();
            WriteF32(&ctx, ram, typeAddr + 0u, 30.0f);       // emit rate
            WriteF32(&ctx, ram, typeAddr + 4u, 0.0f);        // rot speed
            WriteF32(&ctx, ram, typeAddr + 8u, 0.5f);        // size min
            WriteF32(&ctx, ram, typeAddr + 12u, 0.25f);      // size range
            WriteF32(&ctx, ram, typeAddr + 20u, 1.0f);       // lifetime
            WriteF32(&ctx, ram, typeAddr + 24u, 2.0f);       // speed min
            WriteF32(&ctx, ram, typeAddr + 28u, 1.0f);       // speed range
            WriteF32(&ctx, ram, typeAddr + 32u, 1.5f);       // ring radius
            WriteF32(&ctx, ram, typeAddr + 36u, 2.0f);       // radius range
            WriteF32(&ctx, ram, typeAddr + 40u, 0.0f);       // theta min
            WriteF32(&ctx, ram, typeAddr + 44u, 1.5707964f); // theta range
            Ps2Memory.Write32(&ctx, ram, typeAddr + 72u, 0x0038u); // flags1: rand speed + sqrt radius
            Ps2Memory.Write32(&ctx, ram, typeAddr + 76u, 0x8040u); // flags2: asin sphere + rand theta
            WriteF32(&ctx, ram, typeAddr + 108u, 1.5f);      // phi cone

            WriteIdentity(ram, emitAddr + 80u);  // local orient/mat
            WriteIdentity(ram, emitAddr + 144u); // world mat
            WriteIdentity(ram, emitAddr + 256u); // parent mat
            WriteF32(&ctx, ram, emitAddr + 340u, 1.0f);      // opacity > 0.005f
            WriteF32(&ctx, ram, emitAddr + 344u, 1.0f);      // age/scale
            WriteF32(&ctx, ram, emitAddr + 348u, 1.0f);      // remain
            Ps2Memory.Write16(&ctx, ram, emitAddr + 364u, 1);  // packetCount
            Ps2Memory.Write16(&ctx, ram, emitAddr + 366u, 16); // capacity
            WriteF32(&ctx, ram, emitAddr + 392u, 0.25f);     // inner radius
            Ps2Memory.Write32(&ctx, ram, emitAddr + 400u, typeAddr);
            Ps2Memory.Write32(&ctx, ram, emitAddr + 404u, hdrAddr);
            Ps2Memory.Write32(&ctx, ram, emitAddr + 416u, pktPtrArr);
            Ps2Memory.Write32(&ctx, ram, pktPtrArr, pktAddr);

            WriteF32(&ctx, ram, hdrAddr + 44u, 0.0f);
            WriteF32(&ctx, ram, hdrAddr + 116u, 0.5f);
            WriteF32(&ctx, ram, rgbaAddr + 0u, 1f);
            WriteF32(&ctx, ram, rgbaAddr + 4u, 1f);
            WriteF32(&ctx, ram, rgbaAddr + 8u, 1f);
            WriteF32(&ctx, ram, rgbaAddr + 12u, 1f);

            // Init packet & header VIF tags via real SLUS_216.64 functions
            ctx.SetU64(4, emitAddr);
            RuntimeDispatcher.Call(0x003E9F48u, &ctx, ram);
            ctx.SetU64(4, hdrAddr);
            ctx.SetU64(5, 0x8040UL);
            RuntimeDispatcher.Call(0x003E9ED0u, &ctx, ram);

            // Spawn 16 particles via sub_003E4F78
            ctx.SetU64(4, emitAddr);
            ctx.SetU64(5, 16UL);
            ctx.SetU64(6, rgbaAddr);
            ctx.F[12] = 0.0f;
            RuntimeDispatcher.Call(0x003E4F78u, &ctx, ram);
            Assert.Equal(16, ctx.GetS32(2));

            // Tick emitter once via sub_003E5C88(emitAddr, dt=0.05f)
            ctx.SetU64(4, emitAddr);
            ctx.SetU64(5, 0UL);
            ctx.F[12] = 0.05f;
            RuntimeDispatcher.Call(0x003E5C88u, &ctx, ram);

            // Inspect spawned packet at pktAddr:
            // offset 12..75: ages[16], 76..139: sizes[16], 208..719: pos+vel[16]
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();
            var sizes = new List<float>();
            for (int i = 0; i < 16; i++)
            {
                sizes.Add(ReadF32(&ctx, ram, pktAddr + 76u + (uint)(i * 4)));
                xs.Add(ReadF32(&ctx, ram, pktAddr + 208u + (uint)(i * 32) + 0u));
                ys.Add(ReadF32(&ctx, ram, pktAddr + 208u + (uint)(i * 32) + 4u));
                zs.Add(ReadF32(&ctx, ram, pktAddr + 208u + (uint)(i * 32) + 8u));
            }
            Assert.True(xs.Max() - xs.Min() > 0.5f, $"Spawned X collapsed: xs=[{string.Join(", ", xs.Take(6))}]");
            Assert.True(ys.Max() - ys.Min() > 0.5f, $"Spawned Y collapsed: ys=[{string.Join(", ", ys.Take(6))}]");
            Assert.True(zs.Max() - zs.Min() > 0.2f, $"Spawned Z collapsed: zs=[{string.Join(", ", zs.Take(6))}]");
            Assert.True(sizes.All(s => float.IsFinite(s) && s >= 0.49f && s <= 0.8f), $"Bad sizes: [{string.Join(", ", sizes.Take(6))}]");

            // Now run real sub_003E8850 (emitter DMA/VIF1 packet builder) -> DmaController -> Vif1 -> Ovl2 -> GS!
            using var pipeline = new GsGraphicsPipeline();
            List<byte[]> kicks = [];
            var prevKick = pipeline.Vu1.OnXgKick;
            pipeline.Vu1.OnXgKick = s => { kicks.Add(s.ToArray()); prevKick?.Invoke(s); };
            runtime.Dma.OnVif1Data = d => pipeline.Vif1.ProcessData(d);

            // Preload Ovl2 into VU1 MicroMem and view->screen matrix at dm[0x43..0x46]
            var vutext = new Ps2ElfReader(elfPath).FindSection(".vutext")!;
            byte[] raw = File.ReadAllBytes(elfPath);
            uint fileOff = vutext.FileOffset + (0x0048CFA8u - vutext.VAddr);
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(MakeVifCode(0x4A, 0, 0x15E0 / 8));
                bw.Write(raw, (int)fileOff, 2048);
                bw.Write(MakeVifCode(0x01, 0, 0x0101));
                bw.Write(MakeVifCode(0x6C, 4, 0x0043));
                bw.Write(500f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
                bw.Write(0f); bw.Write(500f); bw.Write(0f); bw.Write(0f);
                bw.Write(0f); bw.Write(0f); bw.Write(1f); bw.Write(1f);
                bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
                pipeline.Vif1.ProcessData(ms.ToArray());
            }

            // Setup emitter matrix shifted +10 in Z so particles are in front of camera
            WriteF32(&ctx, ram, emitAddr + 144u + 56u, 10.0f);
            Ps2Memory.Write32(&ctx, ram, 0x00594FE8u, 640u);
            Ps2Memory.Write32(&ctx, ram, 0x00594FECu, 480u);
            RuntimeDispatcher.Call(0x003E9EB0u, &ctx, ram);

            // Mock renderCtx (0x01216000) & rwDevice (0x01217000) & texStruct (0x01218000) + stub jr $ra; nop at 0x00480000
            uint renderCtx = 0x01216000u;
            uint rwDev = 0x01217000u;
            uint texObj = 0x01218000u;
            uint stubPc = 0x01219000u;
            RuntimeDispatcher.RegisterHook(stubPc, (c, r) => { });
            Ps2Memory.Write32(&ctx, ram, renderCtx + 96u, rwDev);
            Ps2Memory.Write32(&ctx, ram, renderCtx + 1568u, 0x70000000u);
            Ps2Memory.Write32(&ctx, ram, renderCtx + 1572u, 0x70000000u);
            Ps2Memory.Write32(&ctx, ram, renderCtx + 1576u, 16368u);
            Ps2Memory.Write32(&ctx, ram, renderCtx + 1580u, 1u);
            Ps2Memory.Write16(&ctx, ram, rwDev + 296u, 0);
            Ps2Memory.Write32(&ctx, ram, rwDev + 300u, stubPc);
            Ps2Memory.Write16(&ctx, ram, rwDev + 528u, 0);
            Ps2Memory.Write32(&ctx, ram, rwDev + 532u, stubPc);
            Ps2Memory.Write32(&ctx, ram, emitAddr + 408u, texObj);

            ctx.SetU64(4, emitAddr);
            ctx.SetU64(5, renderCtx);
            RuntimeDispatcher.Call(0x003E8850u, &ctx, ram);

            // Terminate Scratchpad DMA chain at curPtr with DMA end (0x70000000) & run VIF1 DMA Ch1 (Chain + TTE)
            uint curSpr = Ps2Memory.Read32(&ctx, ram, renderCtx + 1572u);
            Assert.True(curSpr > 0x70000000u && curSpr < 0x70004000u, $"Unexpected SPR ptr 0x{curSpr:X8}");
            Ps2Memory.Write64(&ctx, ram, curSpr, 0x70000000UL);
            Ps2Memory.Write64(&ctx, ram, curSpr + 8u, 0UL);
            runtime.Dma.WriteChannelRegister(&ctx, ram, 1, 0x20u, 0u); // D1_QWC = 0
            runtime.Dma.WriteChannelRegister(&ctx, ram, 1, 0x30u, 0x70000000u); // D1_TADR
            runtime.Dma.WriteChannelRegister(&ctx, ram, 1, 0x00u, 0x00000145u); // D1_CHCR: DIR=1, MODE=Chain, TTE=1, STR=1

            Assert.NotEmpty(kicks);
            AssertDistinctQuadXCoords(kicks, expectedParticles: 16);
        }
    }

    [Fact]
    public void Ovl4_And_Base_AotMatchesDecoded_AcrossAllLightingAndClippingFlags()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var elf = new Ps2ElfReader(elfPath);
        var vutext = elf.FindSection(".vutext");
        Assert.NotNull(vutext);

        byte[] raw = File.ReadAllBytes(elfPath);
        int start = (int)vutext!.FileOffset;
        int end = Math.Min(raw.Length, start + (int)vutext.Size) - 8;

        // Extract Base0/Base1 and Ovl4 MPG regions from .vutext
        var coalesced = new List<(int Addr, List<byte> Bytes)>();
        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);
            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800) continue;
            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)vutext.Size) continue;
            int byteAddr = addr * 8;
            if (coalesced.Count > 0 && coalesced[^1].Addr + coalesced[^1].Bytes.Count == byteAddr)
                coalesced[^1].Bytes.AddRange(raw.AsSpan(i + 4, mpgBytes).ToArray());
            else
                coalesced.Add((byteAddr, new List<byte>(raw.AsSpan(i + 4, mpgBytes).ToArray())));
            i += 4 + mpgBytes - 4;
        }

        byte[] microMem = new byte[Vu1State.MicroMemSize];
        // Load all Base regions (Addr < 0x15E0) and the last overlay (Ovl4, which contains 0x15E8 & 0x29A8)
        int ovlCount = 0;
        foreach (var reg in coalesced)
        {
            if (reg.Addr < 0x15E0)
                Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
            else
            {
                ovlCount++;
                if (ovlCount == 4)
                    Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
            }
        }

        var aotCompiler = new Vu1Recompiler();
        var decCompiler = new Vu1Recompiler();

        static void WriteQw(byte[] dm, int q, float x, float y, float z, float w)
        {
            int o = (q & 0x3FF) << 4;
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 12, 4), w);
        }

        static void WriteQwInt(byte[] dm, int q, int x, int y, int z, int w)
        {
            int o = (q & 0x3FF) << 4;
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 12, 4), w);
        }

        ushort[] testFlags = [0x0000, 0x0008, 0x0408, 0x0028, 0x0088, 0x0128, 0x0428, 0x4000, 0x4400];
        foreach (ushort flag in testFlags)
        {
            var aot = new Vu1State();
            var dec = new Vu1State();
            Buffer.BlockCopy(microMem, 0, aot.MicroMem, 0, microMem.Length);
            Buffer.BlockCopy(microMem, 0, dec.MicroMem, 0, microMem.Length);

            var rng = new Random(12345 + flag);
            for (int r = 1; r < 32; r++)
            {
                var v = Vector128.Create(
                    (float)(rng.NextDouble() * 4.0 - 2.0),
                    (float)(rng.NextDouble() * 4.0 - 2.0),
                    (float)(rng.NextDouble() * 4.0 - 2.0),
                    (float)(rng.NextDouble() * 4.0 - 2.0));
                aot.VF[r] = dec.VF[r] = v;
            }
            aot.ACC = dec.ACC = Vector128.Create(1.5f, -2.5f, 3.25f, 0.75f);
            aot.Top = dec.Top = 0x50;

            byte[] dm = aot.DataMem;
            // GIF tags / constants
            WriteQwInt(dm, 0x0, 0x8000, 0x30000000, 0x00000412, 0);
            WriteQwInt(dm, 0x1, 0x0001, 0x10000000, 0x0000000E, 0);
            WriteQw(dm, 0x2, 0f, 0f, 0f, 0f);
            WriteQw(dm, 0x3, 4095f, 4095f, 16777215f, 0f);
            WriteQw(dm, 0x4, 255f, 255f, 255f, 255f);
            WriteQw(dm, 0x5, 255f, 255f, 255f, 255f);

            // Object -> World & World -> Screen & Clip matrices
            WriteQw(dm, 0x6, 0.01f, 0f, 0f, 0f);
            WriteQw(dm, 0x7, 0f, 0.01f, 0f, 0f);
            WriteQw(dm, 0x8, 0f, 0f, 0.01f, 0f);
            WriteQw(dm, 0x9, 0f, 0f, 10f, 1f);

            WriteQw(dm, 0x1E, 320f, 0f, 0f, 0f);
            WriteQw(dm, 0x1F, 0f, 224f, 0f, 0f);
            WriteQw(dm, 0x20, 0f, 0f, 65536f, 0f);
            WriteQw(dm, 0x21, 2048f, 2048f, 0f, 1f);

            WriteQw(dm, 0x3B, 0.007874f, 0f, 0f, 0f);
            WriteQw(dm, 0x3C, 0f, 0.007874f, 0f, 0f);
            WriteQw(dm, 0x3D, 0f, 0f, 0.007874f, 0f);

            WriteQw(dm, 0x3F, 1f, 0f, 0f, 0f);
            WriteQw(dm, 0x40, 0f, 1f, 0f, 0f);
            WriteQw(dm, 0x41, 0f, 0f, 1f, 1f);
            WriteQw(dm, 0x42, 0f, 0f, 0f, 0f);

            WriteQw(dm, 0x43, 320f, 0f, 0f, 0f);
            WriteQw(dm, 0x44, 0f, 224f, 0f, 0f);
            WriteQw(dm, 0x45, 0f, 0f, 16777215f, 1f);
            WriteQw(dm, 0x46, 2048f, 2048f, 0f, 0f);

            // Lighting & Material
            WriteQw(dm, 0x2E, 0.7f, 0.7f, 0.8f, 0f);
            WriteQw(dm, 0x2F, 1.5f, 1.1f, 0.6f, 0f);
            WriteQw(dm, 0x30, 0.3f, 0.2f, 0.1f, 0f);
            WriteQw(dm, 0x31, 0f, 0f, 0f, 0f);
            WriteQw(dm, 0x32, 0f, 0f, 0f, 1f);
            WriteQw(dm, 0x33, 0.75f, 0.84f, 0f, 0f);
            WriteQw(dm, 0x34, 0.25f, 0.54f, 0f, 0f);
            WriteQw(dm, 0x35, 0.60f, 0.00f, 0f, 0f);
            WriteQw(dm, 0x37, 128f, 128f, 128f, 128f);
            WriteQw(dm, 0x38, 128f, 128f, 128f, 128f);
            WriteQwInt(dm, 0x39, flag, 0x004A, 0x004E, 10);
            WriteQw(dm, 0x3A, 0f, 0f, 0f, 0.5f);
            WriteQwInt(dm, 0x48, 0, 0, 0, 613);

            // Input vertex buffer at Top = 0x50 (count = 8 vertices, stride 5)
            WriteQwInt(dm, 0x50, 8, 0, 0, 0);
            for (int v = 0; v < 8; v++)
            {
                int b = 0x51 + v * 5;
                int px = (v % 3 == 0) ? -2500 : (v * 300 - 900); // mix on-screen & frustum-clipping verts
                int py = (v % 2 == 0) ? 200 : -200;
                int pz = (v == 5) ? -1200 : (v * 100);
                int adc = v < 2 ? 0x8000 : 0;
                WriteQwInt(dm, b + 0, px, py, pz, adc);
                WriteQwInt(dm, b + 1, 60 - v * 15, 80, 70, 127);
                WriteQwInt(dm, b + 2, 1024 * v, 512 * v, 4096, 0);
                WriteQwInt(dm, b + 3, 40 + v * 10, 50 + v * 5, 60, 127);
                WriteQwInt(dm, b + 4, 40 + v * 10, 50 + v * 5, 60, 127);
            }

            Buffer.BlockCopy(aot.DataMem, 0, dec.DataMem, 0, dm.Length);

            var aotKicks = new List<byte[]>();
            var decKicks = new List<byte[]>();
            aot.OnXgKick = s => aotKicks.Add(s.ToArray());
            dec.OnXgKick = s => decKicks.Add(s.ToArray());

            aotCompiler.Execute(aot, 0x15E8);
            decCompiler.ExecuteDecodedOnly(dec, 0x15E8);

            Assert.Equal(decKicks.Count, aotKicks.Count);
            for (int k = 0; k < decKicks.Count; k++)
            {
                Assert.True(decKicks[k].AsSpan().SequenceEqual(aotKicks[k]),
                    $"Ovl4 0x15E8 XGKICK #{k} mismatch for flag=0x{flag:X4} (len dec={decKicks[k].Length} aot={aotKicks[k].Length})");
            }
            Assert.True(dec.DataMem.AsSpan().SequenceEqual(aot.DataMem),
                $"Ovl4 0x15E8 DataMem mismatch for flag=0x{flag:X4}");
            for (int r = 1; r < 32; r++)
                Assert.Equal(dec.VF[r], aot.VF[r]);
            for (int r = 1; r < 16; r++)
                Assert.Equal(dec.VI[r], aot.VI[r]);
        }
    }

    [Fact]
    public void AllBaseAndOvl4EntryPoints_AotMatchesDecoded_BitExact()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var elf = new Ps2ElfReader(elfPath);
        var vutext = elf.FindSection(".vutext");
        Assert.NotNull(vutext);

        byte[] raw = File.ReadAllBytes(elfPath);
        int start = (int)vutext!.FileOffset;
        int end = Math.Min(raw.Length, start + (int)vutext.Size) - 8;

        var coalesced = new List<(int Addr, List<byte> Bytes)>();
        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);
            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800) continue;
            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)vutext.Size) continue;
            int byteAddr = addr * 8;
            if (coalesced.Count > 0 && coalesced[^1].Addr + coalesced[^1].Bytes.Count == byteAddr)
                coalesced[^1].Bytes.AddRange(raw.AsSpan(i + 4, mpgBytes).ToArray());
            else
                coalesced.Add((byteAddr, new List<byte>(raw.AsSpan(i + 4, mpgBytes).ToArray())));
            i += 4 + mpgBytes - 4;
        }

        byte[] microMem = new byte[Vu1State.MicroMemSize];
        int ovlCount = 0;
        foreach (var reg in coalesced)
        {
            if (reg.Addr < 0x15E0)
                Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
            else
            {
                ovlCount++;
                if (ovlCount == 4)
                    Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
            }
        }

        ushort[] entries =
        [
            0x0010, 0x0108, 0x03C0, 0x0438, 0x04C8, 0x04F8, 0x0548,
            0x0598, 0x05E8, 0x06B0, 0x0728, 0x07F0,
            0x15E8
        ];

        static void WriteQw(byte[] dm, int qw, float x, float y, float z, float w)
        {
            int o = qw * 16;
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 12, 4), w);
        }

        static void WriteQwInt(byte[] dm, int qw, int x, int y, int z, int w)
        {
            int o = qw * 16;
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 12, 4), w);
        }

        var aotCompiler = new Vu1Recompiler();
        var decCompiler = new Vu1Recompiler();
        var rng = new Random(1337);

        var failures = new List<string>();
        foreach (ushort entry in entries)
        {
            for (int trial = 0; trial < 3; trial++)
            {
                var aot = new Vu1State();
                var dec = new Vu1State();
                Buffer.BlockCopy(microMem, 0, aot.MicroMem, 0, microMem.Length);
                Buffer.BlockCopy(microMem, 0, dec.MicroMem, 0, microMem.Length);

                aot.Top = 0x50;
                aot.Itop = 0x50;
                dec.Top = 0x50;
                dec.Itop = 0x50;

                // Seed VF/VI/Q/P/R/I/ACC identically to test live-in preservation & math
                for (int r = 1; r < 32; r++)
                {
                    var v = Vector128.Create(
                        (float)(rng.NextDouble() * 4.0 - 2.0),
                        (float)(rng.NextDouble() * 4.0 - 2.0),
                        (float)(rng.NextDouble() * 4.0 - 2.0),
                        (float)(rng.NextDouble() * 4.0 - 2.0));
                    aot.VF[r] = v;
                    dec.VF[r] = v;
                }
                for (int r = 1; r < 16; r++)
                {
                    ushort vi = (ushort)(r == 1 ? 0x50 : (r * 3 + trial));
                    aot.VI[r] = vi;
                    dec.VI[r] = vi;
                }

                byte[] dm = aot.DataMem;
                for (int qw = 0; qw < 128; qw++)
                {
                    WriteQw(dm, qw,
                        (float)(rng.NextDouble() * 2.0 - 1.0),
                        (float)(rng.NextDouble() * 2.0 - 1.0),
                        (float)(rng.NextDouble() * 2.0 - 1.0),
                        1.0f);
                }

                // Set valid matrices & headers at standard RenderWare/Castaway slots
                WriteQw(dm, 0x00, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x01, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x02, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x03, 0f, 0f, -10f, 1f);
                WriteQw(dm, 0x08, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x09, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x0A, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x0B, 0f, 0f, 10f, 1f);
                WriteQw(dm, 0x10, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x11, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x12, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x13, 0f, 0f, 0f, 1f);
                WriteQw(dm, 0x14, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x15, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x16, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x17, 0f, 0f, 10f, 1f);
                WriteQw(dm, 0x18, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x19, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x1A, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x1B, 0f, 0f, 0f, 1f);
                WriteQw(dm, 0x1C, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x1D, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x1E, 0f, 0f, 1f, 0f);
                WriteQw(dm, 0x1F, 0f, 0f, 10f, 1f);

                WriteQw(dm, 0x2E, 0.4f, 0.5f, 0.6f, 0f);
                WriteQw(dm, 0x2F, 1.2f, 0.9f, 0.5f, 0f);
                WriteQw(dm, 0x30, 0.3f, 0.2f, 0.1f, 0f);
                WriteQw(dm, 0x37, 128f, 128f, 128f, 128f);
                WriteQw(dm, 0x38, 128f, 128f, 128f, 128f);
                int flag = trial == 0 ? 0x0028 : (trial == 1 ? 0x0408 : 0x0088);
                WriteQwInt(dm, 0x39, flag, 0x004A, 0x004E, 10);
                WriteQw(dm, 0x3A, 0f, 0f, 0f, 1f);
                WriteQw(dm, 0x3F, 1f, 0f, 0f, 0f);
                WriteQw(dm, 0x40, 0f, 1f, 0f, 0f);
                WriteQw(dm, 0x41, 0f, 0f, 1f, 1f);
                WriteQw(dm, 0x42, 0f, 0f, 0f, 0f);
                WriteQw(dm, 0x43, 320f, 0f, 0f, 0f);
                WriteQw(dm, 0x44, 0f, 224f, 0f, 0f);
                WriteQw(dm, 0x45, 0f, 0f, 16777215f, 1f);
                WriteQw(dm, 0x46, 2048f, 2048f, 0f, 0f);
                WriteQwInt(dm, 0x48, 0, 0, 0, 613);

                // Small valid input buffer at 0x50
                if (entry is 0x0010 or 0x0108 or 0x0438 or 0x04C8 or 0x04F8 or 0x0548 or 0x0598 or 0x05E8 or 0x06B0 or 0x0728 or 0x07F0 or 0x15E0)
                {
                    // Setup/matrix/lighting microprograms expect normal floats at Top (0x50..0x5F)
                    for (int q = 0x50; q < 0x60; q++)
                    {
                        WriteQw(dm, q,
                            (float)(rng.NextDouble() * 2.0 - 1.0) + 0.1f,
                            (float)(rng.NextDouble() * 2.0 - 1.0) + 0.2f,
                            (float)(rng.NextDouble() * 2.0 - 1.0) + 0.3f,
                            1.0f);
                    }
                }
                else
                {
                    WriteQwInt(dm, 0x50, 6, 0x100, 2, 0);
                    for (int v = 0; v < 6; v++)
                    {
                        int b = 0x51 + v * 5;
                        WriteQwInt(dm, b + 0, v * 200 - 400, (v % 2 == 0) ? 150 : -150, v * 50, v < 2 ? 0x8000 : 0);
                        WriteQwInt(dm, b + 1, 50, 80, 60, 127);
                        WriteQwInt(dm, b + 2, 512 * v, 256 * v, 4096, 0);
                        WriteQwInt(dm, b + 3, 64, 64, 64, 128);
                        WriteQwInt(dm, b + 4, 64, 64, 64, 128);
                    }
                }

                // Also populate skinning bone table at 0x180..0x1FF so 0x29A8 doesn't read garbage bone indices
                if (entry == 0x29A8)
                {
                    WriteQwInt(dm, 0x50, 4, 0, 0, 0);
                    for (int v = 0; v < 4; v++)
                    {
                        int b = 0x51 + v * 4;
                        WriteQw(dm, b + 0, v * 0.5f, 0.2f, 0.1f, 0f);
                        BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan((b + 0) * 16 + 12, 4), 0x180);
                        WriteQw(dm, b + 1, 0.5f, 0.5f, 0.5f, 1.0f);
                        WriteQw(dm, b + 2, 0.25f * v, 0.5f, 1.0f, 0.0f);
                        WriteQwInt(dm, b + 3, 80, 90, 100, 128);
                    }
                    for (int q = 0x180; q < 0x200; q += 4)
                    {
                        WriteQw(dm, q + 0, 1f, 0f, 0f, 0f);
                        WriteQw(dm, q + 1, 0f, 1f, 0f, 0f);
                        WriteQw(dm, q + 2, 0f, 0f, 1f, 0f);
                        WriteQw(dm, q + 3, 0f, 0f, 0f, 1f);
                    }
                }

                Buffer.BlockCopy(aot.DataMem, 0, dec.DataMem, 0, dm.Length);

                var aotKicks = new List<byte[]>();
                var decKicks = new List<byte[]>();
                aot.OnXgKick = s => aotKicks.Add(s.ToArray());
                dec.OnXgKick = s => decKicks.Add(s.ToArray());

                aotCompiler.Execute(aot, entry);
                decCompiler.ExecuteDecodedOnly(dec, entry);

                string? err = null;
                if (decKicks.Count != aotKicks.Count)
                    err = $"XGKICK count dec={decKicks.Count} aot={aotKicks.Count}";
                for (int k = 0; err == null && k < decKicks.Count; k++)
                {
                    if (!decKicks[k].AsSpan().SequenceEqual(aotKicks[k]))
                    {
                        int len = Math.Min(decKicks[k].Length, aotKicks[k].Length);
                        for (int off = 0; off < len; off += 16)
                        {
                            if (!decKicks[k].AsSpan(off, Math.Min(16, len - off)).SequenceEqual(aotKicks[k].AsSpan(off, Math.Min(16, len - off))))
                            {
                                var dv = MemoryMarshal.Read<Vector128<uint>>(decKicks[k].AsSpan(off, 16));
                                var av = MemoryMarshal.Read<Vector128<uint>>(aotKicks[k].AsSpan(off, 16));
                                err = $"XGKICK #{k} qw={off / 16} dec=0x{dv.GetElement(0):X8},{dv.GetElement(1):X8},{dv.GetElement(2):X8},{dv.GetElement(3):X8} aot=0x{av.GetElement(0):X8},{av.GetElement(1):X8},{av.GetElement(2):X8},{av.GetElement(3):X8}";
                                break;
                            }
                        }
                        err ??= $"XGKICK #{k} len dec={decKicks[k].Length} aot={aotKicks[k].Length}";
                    }
                }
                if (err == null && !dec.DataMem.AsSpan().SequenceEqual(aot.DataMem))
                {
                    for (int qw = 0; qw < 1024; qw++)
                    {
                        int o = qw * 16;
                        if (!dec.DataMem.AsSpan(o, 16).SequenceEqual(aot.DataMem.AsSpan(o, 16)))
                        {
                            var dv = MemoryMarshal.Read<Vector128<float>>(dec.DataMem.AsSpan(o, 16));
                            var av = MemoryMarshal.Read<Vector128<float>>(aot.DataMem.AsSpan(o, 16));
                            err = $"DM[0x{qw:X2}] dec={dv} aot={av}";
                            break;
                        }
                    }
                }
                for (int r = 1; err == null && r < 32; r++)
                {
                    if (!dec.VF[r].AsUInt32().Equals(aot.VF[r].AsUInt32()))
                        err = $"VF{r} dec={dec.VF[r]} aot={aot.VF[r]}";
                }
                for (int r = 1; err == null && r < 16; r++)
                {
                    if (dec.VI[r] != aot.VI[r])
                        err = $"VI{r} dec=0x{dec.VI[r]:X4} aot=0x{aot.VI[r]:X4}";
                }
                if (err != null)
                {
                    failures.Add($"0x{entry:X4} t{trial}: {err}");
                    break;
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Ovl4_15E8_UnusedLightSlotGarbageBits_CompareAotVsDecoded()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        var elf = new Ps2ElfReader(elfPath);
        var vutext = elf.FindSection(".vutext");
        Assert.NotNull(vutext);

        byte[] raw = File.ReadAllBytes(elfPath);
        int start = (int)vutext!.FileOffset;
        int end = Math.Min(raw.Length, start + (int)vutext.Size) - 8;

        var coalesced = new List<(int Addr, List<byte> Bytes)>();
        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);
            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800) continue;
            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)vutext.Size) continue;
            int byteAddr = addr * 8;
            if (coalesced.Count > 0 && coalesced[^1].Addr + coalesced[^1].Bytes.Count == byteAddr)
                coalesced[^1].Bytes.AddRange(raw.AsSpan(i + 4, mpgBytes).ToArray());
            else
                coalesced.Add((byteAddr, new List<byte>(raw.AsSpan(i + 4, mpgBytes).ToArray())));
            i += 4 + mpgBytes - 4;
        }

        byte[] microMem = new byte[Vu1State.MicroMemSize];
        int ovlCount = 0;
        foreach (var reg in coalesced)
        {
            if (reg.Addr < 0x15E0)
                Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
            else if (++ovlCount == 4)
                Buffer.BlockCopy(reg.Bytes.ToArray(), 0, microMem, reg.Addr, reg.Bytes.Count);
        }

        static void WriteQw(byte[] dm, int q, float x, float y, float z, float w)
        {
            int o = (q & 0x3FF) << 4;
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteSingleLittleEndian(dm.AsSpan(o + 12, 4), w);
        }
        static void WriteQwInt(byte[] dm, int q, int x, int y, int z, int w)
        {
            int o = (q & 0x3FF) << 4;
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 0, 4), x);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 4, 4), y);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 8, 4), z);
            BinaryPrimitives.WriteInt32LittleEndian(dm.AsSpan(o + 12, 4), w);
        }

        uint[] badPatterns = [0xFFFFFFFFu, 0x7FC00000u, 0x7F800000u, 0xFF800000u];
        var aotCompiler = new Vu1Recompiler();
        var decCompiler = new Vu1Recompiler();
        var diffs = new List<string>();

        foreach (uint pat in badPatterns)
        {
            for (int mode = 0; mode < 2; mode++)
            {
                var aot = new Vu1State();
                var dec = new Vu1State();
                Buffer.BlockCopy(microMem, 0, aot.MicroMem, 0, microMem.Length);
                Buffer.BlockCopy(microMem, 0, dec.MicroMem, 0, microMem.Length);
                aot.Top = dec.Top = 0x50;
                byte[] dm = aot.DataMem;

                WriteQwInt(dm, 0x0, 0x8000, 0x30000000, 0x00000412, 0);
                WriteQwInt(dm, 0x1, 0x0001, 0x10000000, 0x0000000E, 0);
                WriteQw(dm, 0x3, 4095f, 4095f, 16777215f, 0f);
                WriteQw(dm, 0x4, 255f, 255f, 255f, 255f);
                WriteQw(dm, 0x5, 255f, 255f, 255f, 255f);
                WriteQw(dm, 0x6, 0.01f, 0f, 0f, 0f);
                WriteQw(dm, 0x7, 0f, 0.01f, 0f, 0f);
                WriteQw(dm, 0x8, 0f, 0f, 0.01f, 0f);
                WriteQw(dm, 0x9, 0f, 0f, 10f, 1f);
                WriteQw(dm, 0x3B, 0.007874f, 0f, 0f, 0f);
                WriteQw(dm, 0x3C, 0f, 0.007874f, 0f, 0f);
                WriteQw(dm, 0x3D, 0f, 0f, 0.007874f, 0f);
                WriteQw(dm, 0x43, 320f, 0f, 0f, 0f);
                WriteQw(dm, 0x44, 0f, 224f, 0f, 0f);
                WriteQw(dm, 0x45, 0f, 0f, 16777215f, 1f);
                WriteQw(dm, 0x46, 2048f, 2048f, 0f, 0f);

                WriteQw(dm, 0x2E, 0.7f, 0.7f, 0.8f, 0f);
                WriteQw(dm, 0x2F, 1.5f, 1.1f, 0.6f, 0f);
                WriteQw(dm, 0x30, 0f, 0f, 0f, 0f);
                WriteQw(dm, 0x31, 0f, 0f, 0f, 0f);
                WriteQw(dm, 0x32, 0f, 0f, 0f, 0f);
                WriteQw(dm, 0x33, 0.75f, 0f, 0f, 0f);
                WriteQw(dm, 0x34, 0.25f, 0f, 0f, 0f);
                WriteQw(dm, 0x35, 0.60f, 0f, 0f, 0f);

                float badF = BitConverter.UInt32BitsToSingle(pat);
                if (mode == 0)
                    WriteQw(dm, 0x32, badF, badF, badF, 0f); // unused light 3 color has heap garbage, dir=0
                else
                    WriteQw(dm, 0x33, 0.75f, 0f, 0f, badF);  // unused light 3 dir has heap garbage, color=0

                WriteQw(dm, 0x37, 128f, 128f, 128f, 128f);
                WriteQw(dm, 0x38, 128f, 128f, 128f, 128f);
                WriteQwInt(dm, 0x39, 0x0008, 0x004A, 0x004E, 10);
                WriteQw(dm, 0x3A, 0f, 0f, 0f, 0.5f);
                WriteQwInt(dm, 0x48, 0, 0, 0, 613);
                WriteQwInt(dm, 0x50, 4, 0, 0, 0);
                for (int v = 0; v < 4; v++)
                {
                    int b = 0x51 + v * 5;
                    WriteQwInt(dm, b + 0, v * 200, 100, 50, v < 2 ? 0x8000 : 0);
                    WriteQwInt(dm, b + 1, 60, 80, 70, 127);
                    WriteQwInt(dm, b + 2, 1024, 512, 4096, 0);
                }
                Buffer.BlockCopy(aot.DataMem, 0, dec.DataMem, 0, dm.Length);

                aotCompiler.Execute(aot, 0x15E8);
                decCompiler.ExecuteDecodedOnly(dec, 0x15E8);

                int outOff = (613 + 4) * 16;
                var dCol = MemoryMarshal.Read<Vector128<int>>(dec.DataMem.AsSpan(outOff, 16));
                var aCol = MemoryMarshal.Read<Vector128<int>>(aot.DataMem.AsSpan(outOff, 16));
                if (!dCol.Equals(aCol))
                    diffs.Add($"pat=0x{pat:X8} mode={mode}: dec={dCol} aot={aCol}");
            }
        }
        Assert.True(diffs.Count == 0, string.Join("\n", diffs));
    }

    [Fact]
    public void Stage9B_DeferredH2l_ClutAndStridedTextures_MatchSoftwareReference()
    {
        var glGs = new GsProcessor { Mode = GsRendererMode.OpenGl, ActiveBatch = new GsPrimitiveBatch() };
        var swGs = new GsProcessor { Mode = GsRendererMode.Software };

        static void UploadH2l(GsProcessor gs, int dbp, int dbw, GsPsm dpsm, int rrw, int rrh, byte[] data, int splitAt = -1)
        {
            ulong bitblt = ((ulong)(uint)dbp << 32) | ((ulong)(uint)dbw << 48) | ((ulong)(uint)dpsm << 56);
            gs.WriteInternalRegister(0x50, bitblt); // BITBLTBUF
            gs.WriteInternalRegister(0x51, 0ul);    // TRXPOS (0,0)
            gs.WriteInternalRegister(0x52, (ulong)(uint)rrw | ((ulong)(uint)rrh << 32)); // TRXREG
            gs.WriteInternalRegister(0x53, 0ul);    // TRXDIR = Host->Local

            if (splitAt > 0 && splitAt < data.Length)
            {
                SendImagePacket(gs, data.AsSpan(0, splitAt));
                SendImagePacket(gs, data.AsSpan(splitAt));
            }
            else
            {
                SendImagePacket(gs, data);
            }
        }

        static void KickTexturedTri(GsProcessor gs, ulong tex0)
        {
            gs.WriteInternalRegister(0x18, 0ul); // XYOFFSET_1
            gs.WriteInternalRegister(0x4C, 0x00000000000A0000ul); // FRAME_1 FBP=0, FBW=640
            gs.WriteInternalRegister(0x40, (639ul << 16) | (447ul << 48)); // SCISSOR_1
            gs.WriteInternalRegister(0x47, 0x0000000000030000ul); // TEST_1
            gs.WriteInternalRegister(0x06, tex0); // TEX0_1
            gs.WriteInternalRegister(0x00, 0x03ul | (1ul << 4)); // PRIM = Triangle, TME=1
            gs.WriteInternalRegister(0x01, 0x80808080ul);
            gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)10 << 4) << 16));
            gs.WriteInternalRegister(0x05, ((ulong)40 << 4) | (((ulong)10 << 4) << 16));
            gs.WriteInternalRegister(0x05, ((ulong)10 << 4) | (((ulong)40 << 4) << 16));
        }

        static void SendImagePacket(GsProcessor gs, ReadOnlySpan<byte> bytes)
        {
            int qwords = (bytes.Length + 15) >> 4;
            byte[] pkt = new byte[16 + qwords * 16];
            ulong tagLo = (ulong)qwords | (1ul << 15) | (2ul << 58); // EOP=1, FLG=IMAGE(2)
            BinaryPrimitives.WriteUInt64LittleEndian(pkt.AsSpan(0, 8), tagLo);
            bytes.CopyTo(pkt.AsSpan(16));
            gs.SubmitGifPacket(pkt);
        }

        var rng = new Random(12345);
        // 1. 16x16 PSMCT32 CLUT at block 0x2000 (1024 bytes) + 64x64 PSMT8 multi-chunk texture at block 0x2100 (4096 bytes)
        byte[] clut8 = new byte[16 * 16 * 4];
        rng.NextBytes(clut8);
        byte[] tex8 = new byte[64 * 64];
        rng.NextBytes(tex8);

        UploadH2l(glGs, 0x2000, 1, GsPsm.Psmct32, 16, 16, clut8);
        UploadH2l(swGs, 0x2000, 1, GsPsm.Psmct32, 16, 16, clut8);
        UploadH2l(glGs, 0x2100, 1, GsPsm.Psmt8, 64, 64, tex8, splitAt: 2048);
        UploadH2l(swGs, 0x2100, 1, GsPsm.Psmt8, 64, 64, tex8, splitAt: 2048);

        // 2. 8x2 PSMCT32 CLUT at block 0x2200 (64 bytes) + 16x8 PSMT4 uploaded, decoded as 8x8 strided!
        byte[] clut4 = new byte[8 * 2 * 4];
        rng.NextBytes(clut4);
        byte[] tex4 = new byte[16 * 8 / 2];
        rng.NextBytes(tex4);

        UploadH2l(glGs, 0x2200, 1, GsPsm.Psmct32, 8, 2, clut4);
        UploadH2l(swGs, 0x2200, 1, GsPsm.Psmct32, 8, 2, clut4);
        UploadH2l(glGs, 0x2300, 1, GsPsm.Psmt4, 16, 8, tex4);
        UploadH2l(swGs, 0x2300, 1, GsPsm.Psmt4, 16, 8, tex4);

        // Switch swGs to OpenGl AFTER all swizzled VRAM uploads so ActiveBatch captures its VRAM-decoded textures
        swGs.Mode = GsRendererMode.OpenGl;
        swGs.ActiveBatch = new GsPrimitiveBatch();

        ulong tex0_8 = 0x2100ul | (1ul << 14) | ((ulong)GsPsm.Psmt8 << 20) | (6ul << 26) | (6ul << 30) | (1ul << 34)
            | (0x2000ul << 37) | (0ul << 51) | (0ul << 55) | (0ul << 56) | (1ul << 61);
        KickTexturedTri(glGs, tex0_8);
        KickTexturedTri(swGs, tex0_8);

        ulong tex0_4 = 0x2300ul | (1ul << 14) | ((ulong)GsPsm.Psmt4 << 20) | (3ul << 26) | (3ul << 30) | (1ul << 34)
            | (0x2200ul << 37) | (0ul << 51) | (0ul << 55) | (0ul << 56) | (1ul << 61);
        KickTexturedTri(glGs, tex0_4);
        KickTexturedTri(swGs, tex0_4);

        Assert.Equal(2, glGs.ActiveBatch!.Textures.Count);
        Assert.Equal(2, swGs.ActiveBatch!.Textures.Count);
        Assert.Equal(swGs.ActiveBatch.Textures[0].Pixels, glGs.ActiveBatch.Textures[0].Pixels);
        Assert.Equal(swGs.ActiveBatch.Textures[1].Pixels, glGs.ActiveBatch.Textures[1].Pixels);
        Assert.True(glGs.DeferredClutHits >= 2, $"Expected >=2 deferred CLUT hits, got {glGs.DeferredClutHits}");
        Assert.Equal(0, glGs.DeferredClutMisses);
        Assert.Equal(0, glGs.DeferredH2lFlushes);

        // 3. Partial-height UI texture (TRXREG=256x256, only top 64 rows sent before next TRXDIR=0 for CLUT)
        // overwriting an existing deferred 256x256 texture at the same address (0x3528).
        byte[] old3dTex = new byte[256 * 256];
        rng.NextBytes(old3dTex);
        byte[] uiPartialRows = new byte[256 * 64];
        rng.NextBytes(uiPartialRows);
        byte[] uiClut = new byte[16 * 16 * 4];
        rng.NextBytes(uiClut);

        swGs.Mode = GsRendererMode.Software;
        swGs.ActiveBatch = null;
        UploadH2l(glGs, 0x3528, 4, GsPsm.Psmt8, 256, 256, old3dTex);
        UploadH2l(swGs, 0x3528, 4, GsPsm.Psmt8, 256, 256, old3dTex);

        // Upload only 256x64 bytes under TRXREG=(256,256), then immediately start CLUT TRXDIR=0 at 0x3628!
        static void UploadPartialH2l(GsProcessor gs, int dbp, int dbw, GsPsm dpsm, int rrw, int rrh, byte[] partialBytes)
        {
            ulong bitblt = ((ulong)(uint)dbp << 32) | ((ulong)(uint)dbw << 48) | ((ulong)(uint)dpsm << 56);
            gs.WriteInternalRegister(0x50, bitblt);
            gs.WriteInternalRegister(0x51, 0ul);
            gs.WriteInternalRegister(0x52, (ulong)(uint)rrw | ((ulong)(uint)rrh << 32));
            gs.WriteInternalRegister(0x53, 0ul);
            SendImagePacket(gs, partialBytes);
        }

        UploadPartialH2l(glGs, 0x3528, 4, GsPsm.Psmt8, 256, 256, uiPartialRows);
        UploadPartialH2l(swGs, 0x3528, 4, GsPsm.Psmt8, 256, 256, uiPartialRows);
        UploadH2l(glGs, 0x3628, 1, GsPsm.Psmct32, 16, 16, uiClut);
        UploadH2l(swGs, 0x3628, 1, GsPsm.Psmct32, 16, 16, uiClut);

        swGs.Mode = GsRendererMode.OpenGl;
        swGs.ActiveBatch = new GsPrimitiveBatch();
        glGs.ActiveBatch = new GsPrimitiveBatch();
        ulong tex0_ui = 0x3528ul | (4ul << 14) | ((ulong)GsPsm.Psmt8 << 20) | (8ul << 26) | (8ul << 30) | (1ul << 34)
            | (0x3628ul << 37) | (0ul << 51) | (0ul << 55) | (0ul << 56) | (1ul << 61);
        KickTexturedTri(glGs, tex0_ui);
        KickTexturedTri(swGs, tex0_ui);
        Assert.Equal(swGs.ActiveBatch.Textures[0].Pixels, glGs.ActiveBatch.Textures[0].Pixels);
    }

    [Fact]
    public unsafe void Stage10C_UnalignedPairHelpers_MatchLdlLdrAndLwlLwrBitExact()
    {
        byte[] ramRef = new byte[256];
        byte[] ramFast = new byte[256];
        var rng = new Random(999);
        rng.NextBytes(ramRef);
        Buffer.BlockCopy(ramRef, 0, ramFast, 0, ramRef.Length);

        CpuContext ctxRef = default;
        CpuContext ctxFast = default;
        fixed (byte* pRef = ramRef)
        fixed (byte* pFast = ramFast)
        {
            for (uint off = 16; off < 24; off++)
            {
                // LDL/LDR vs Read64Unaligned
                ctxRef.SetU64(2, 0xDEAD_BEEF_CAFE_BABEul);
                Ps2Memory.LoadDoubleLeft(&ctxRef, pRef, 2, off + 7u);
                Ps2Memory.LoadDoubleRight(&ctxRef, pRef, 2, off);
                ulong fast64 = Ps2Memory.Read64Unaligned(&ctxFast, pFast, off);
                Assert.Equal(ctxRef.GetU64(2), fast64);

                // LWL/LWR vs Read32Unaligned (sign-extended to 64-bit)
                ctxRef.SetU64(3, 0x1122_3344_5566_7788ul);
                Ps2Memory.LoadWordLeft(&ctxRef, pRef, 3, off + 3u);
                Ps2Memory.LoadWordRight(&ctxRef, pRef, 3, off);
                ulong fast32 = unchecked((ulong)(long)(int)Ps2Memory.Read32Unaligned(&ctxFast, pFast, off));
                Assert.Equal(ctxRef.GetU64(3), fast32);

                // SDL/SDR vs Write64Unaligned
                ulong w64 = 0x0123_4567_89AB_CDEFul ^ ((ulong)off * 0x1111_1111_1111_1111ul);
                ctxRef.SetU64(4, w64);
                Ps2Memory.StoreDoubleLeft(&ctxRef, pRef, 4, off + 64u + 7u);
                Ps2Memory.StoreDoubleRight(&ctxRef, pRef, 4, off + 64u);
                Ps2Memory.Write64Unaligned(&ctxFast, pFast, off + 64u, w64);
                Assert.Equal(ramRef, ramFast);

                // SWL/SWR vs Write32Unaligned
                uint w32 = 0x89AB_CDEFu ^ (off * 0x0101_0101u);
                ctxRef.SetZ32(5, w32);
                Ps2Memory.StoreWordLeft(&ctxRef, pRef, 5, off + 128u + 3u);
                Ps2Memory.StoreWordRight(&ctxRef, pRef, 5, off + 128u);
                Ps2Memory.Write32Unaligned(&ctxFast, pFast, off + 128u, w32);
                Assert.Equal(ramRef, ramFast);
            }
        }
    }

    [Fact]
    public void Stage13_OffsetTable16_And_FlatTriangleFast16Lut_MatchReference()
    {
        // 1. Verify precomputed OffsetTable16/16S/Z16/Z16S match exact block+halfword formula across multi-page 256x128 surface
        for (int isZ = 0; isZ <= 1; isZ++)
        {
            for (int is16S = 0; is16S <= 1; is16S++)
            {
                ReadOnlySpan<byte> blkTbl = isZ != 0
                    ? (is16S != 0 ? GsSwizzle.BlockTableZ16S : GsSwizzle.BlockTableZ16)
                    : (is16S != 0 ? GsSwizzle.BlockTable16S : GsSwizzle.BlockTable16);
                for (int y = 0; y < 128; y += 3)
                {
                    for (int x = 0; x < 256; x += 5)
                    {
                        int pagesPerRow = 256 >> 6;
                        int pageBlockBase = 0x3E00 + ((y >> 6) * pagesPerRow + (x >> 6)) * 32;
                        int blockInPage = blkTbl[(((y & 63) >> 3) << 2) + ((x & 63) >> 4)];
                        int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;
                        int hwInBlock = GsSwizzle.ColumnHalfwordTable16[((y & 7) << 4) + (x & 15)];
                        int expected = (((blockIdx << 7) + hwInBlock) << 1) & GsSwizzle.VramByteMask;
                        int actual = GsSwizzle.GetPixelByteAddress16(0x3E00, 256, x, y, is16S: is16S != 0, isZ: isZ != 0);
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }

        // 2. Verify RasterizeFlatTriangleFast16 LUT blend path on Psmct16S matches exact per-channel PS2 blend formula
        var gs = new GsProcessor { Mode = GsRendererMode.Software };
        // FRAME_1: FBP=0x3E00 (0x1F0 * 32), FBW=4 (256px), PSM=Psmct16S (0x0A), FBMSK=0
        gs.WriteInternalRegister(0x4C, 0x1F0ul | (4ul << 16) | ((ulong)GsPsm.Psmct16S << 24));
        gs.WriteInternalRegister(0x18, 0ul); // XYOFFSET_1 = (0,0)
        gs.WriteInternalRegister(0x40, (255ul << 16) | (255ul << 48)); // SCISSOR_1 = [0..255, 0..255]
        gs.WriteInternalRegister(0x47, (1ul << 16) | (1ul << 17)); // TEST_1: ATE=0, ZTE=1, ZTST=ALWAYS(1)
        gs.WriteInternalRegister(0x4E, 1ul << 32); // ZBUF_1: ZMSK=1
        gs.WriteInternalRegister(0x42, 0x44ul); // ALPHA_1: standard (Cs - Cd) * As >> 7 + Cd
        gs.WriteInternalRegister(0x1A, 1ul); // COLCLAMP=1

        // Seed background VRAM with varied 16-bit colors
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                uint bg = (uint)((x * 4) | ((y * 4) << 8) | (((x + y) * 2) << 16) | 0x80000000u);
                int addr = GsSwizzle.GetPixelByteAddress16(0x3E00, 256, x, y, is16S: true);
                BinaryPrimitives.WriteUInt16LittleEndian(gs.Vram.AsSpan(addr, 2), GsSwizzle.Pack32To16(bg));
            }
        }

        // Draw flat alpha-blended triangle (PRIM=Triangle|ABE = 3 | 0x40 = 0x43, RGBA=(40,80,120,0x40))
        byte sR = 40, sG = 80, sB = 120, sA = 0x40;
        gs.WriteInternalRegister(0x00, 0x43ul);
        gs.WriteInternalRegister(0x01, (ulong)sR | ((ulong)sG << 8) | ((ulong)sB << 16) | ((ulong)sA << 24));
        gs.WriteInternalRegister(0x05, (8ul << 4) | ((8ul << 4) << 16));
        gs.WriteInternalRegister(0x05, (56ul << 4) | ((8ul << 4) << 16));
        gs.WriteInternalRegister(0x05, (8ul << 4) | ((56ul << 4) << 16));

        // Check interior pixel (20, 20) matches exact Expand16To32 -> blend -> Pack32To16
        uint origBg = (uint)((20 * 4) | ((20 * 4) << 8) | (((20 + 20) * 2) << 16) | 0x80000000u);
        uint expDst = GsSwizzle.Expand16To32(GsSwizzle.Pack32To16(origBg));
        int dR = (int)(expDst & 0xFFu), dG = (int)((expDst >> 8) & 0xFFu), dB = (int)((expDst >> 16) & 0xFFu);
        int oR = Math.Clamp((((sR - dR) * sA) >> 7) + dR, 0, 255);
        int oG = Math.Clamp((((sG - dG) * sA) >> 7) + dG, 0, 255);
        int oB = Math.Clamp((((sB - dB) * sA) >> 7) + dB, 0, 255);
        ushort expected16 = GsSwizzle.Pack32To16((uint)(oR | (oG << 8) | (oB << 16) | (sA << 24)));
        int chkAddr = GsSwizzle.GetPixelByteAddress16(0x3E00, 256, 20, 20, is16S: true);
        ushort actual16 = BinaryPrimitives.ReadUInt16LittleEndian(gs.Vram.AsSpan(chkAddr, 2));
        Assert.Equal(expected16, actual16);
    }

    private static uint MakeVifCode(byte cmd, byte num, ushort imm)
        => ((uint)cmd << 24) | ((uint)num << 16) | imm;
}
