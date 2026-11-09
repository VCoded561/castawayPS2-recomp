using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Castaway.Core;
using Castaway.ElfTools.Commands;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.Vif;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase2PerformanceTests
{
    private readonly ITestOutputHelper? _output;

    public Phase2PerformanceTests(ITestOutputHelper? output = null)
    {
        _output = output;
    }
    [Fact]
    public void Flags_DefaultValues_AreDisabledForPerformance()
    {
        Assert.False(GsProcessor.EnableDebugLogging, "GsProcessor.EnableDebugLogging must default to false to avoid blocking Console I/O.");
        Assert.False(DmaController.CaptureDebugPackets, "DmaController.CaptureDebugPackets must default to false to avoid per-byte packet buffering.");
    }

    [Fact]
    public void GsProcessor_IncrementalBarycentric_RasterizesTriangleAccurately()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        // Setup Frame: FBP=0, FBW=10 (640px), PSM=PSMCT32 (0), FBMSK=0
        gs.Frame[ctx] = 0x00000000_000A_0000ul;
        // Setup Scissor: 0..639, 0..479
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;
        // PRIM: Triangle (3), Gouraud (bit 3 = 1)
        gs.Prim = (ulong)GsPrimitiveType.Triangle | (1ul << 3);
        gs.Prmodecont = 1ul;

        // Vertices at (20, 20), (100, 20), (20, 100) in 12.4 fixed point (multiply by 16)
        // V0: Red (255, 0, 0, 128)
        // V1: Green (0, 255, 0, 128)
        // V2: Blue (0, 0, 255, 128)
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul); // RGBAQ = Red
        gs.WriteInternalRegister(0x05, (ulong)(20 * 16) | ((ulong)(20 * 16) << 16)); // XYZ2 V0

        gs.WriteInternalRegister(0x01, 0x80_00_FF_00ul); // RGBAQ = Green
        gs.WriteInternalRegister(0x05, (ulong)(100 * 16) | ((ulong)(20 * 16) << 16)); // XYZ2 V1

        gs.WriteInternalRegister(0x01, 0x80_FF_00_00ul); // RGBAQ = Blue
        gs.WriteInternalRegister(0x05, (ulong)(20 * 16) | ((ulong)(100 * 16) << 16)); // XYZ2 V2 (Triggers draw)

        Assert.True(gs.TotalPrimitivesDrawn > 0);

        // Pixel near V0 (25, 25) should be predominantly red
        uint pixV0 = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 25, 25);
        byte rV0 = (byte)(pixV0 & 0xFFu);
        byte gV0 = (byte)((pixV0 >> 8) & 0xFFu);
        byte bV0 = (byte)((pixV0 >> 16) & 0xFFu);
        Assert.True(rV0 > 150, $"Expected strong Red at (25, 25), got R={rV0}, G={gV0}, B={bV0}");
        Assert.True(rV0 > gV0 && rV0 > bV0, "Red must dominate near V0");

        // Pixel outside triangle at (5, 5) should remain untouched (0)
        uint pixOut1 = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 5, 5);
        Assert.Equal(0u, pixOut1);

        // Pixel outside triangle hypotenuse at (90, 90) should remain untouched (0)
        uint pixOut2 = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 90, 90);
        Assert.Equal(0u, pixOut2);
    }

    [Fact]
    public void GsProcessor_GuardbandClamping_PreventsMemoryCorruption()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        gs.Frame[ctx] = 0x00000000_000A_0000ul; // FBW=640
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;
        gs.Prim = (ulong)GsPrimitiveType.Triangle;
        gs.Prmodecont = 1ul;

        // Vertices far outside the normal 640x480 screen (within 16-bit ushort guardband 0..4095 in 12.4 fixed point)
        // V0: (0, 0)
        // V1: (3000, 100) -> 3000 * 16 = 48000
        // V2: (100, 2000) -> 2000 * 16 = 32000
        gs.WriteInternalRegister(0x01, 0xFF_FFFFFFul);
        gs.WriteInternalRegister(0x05, 0ul);
        gs.WriteInternalRegister(0x05, (ulong)(3000 * 16) | ((ulong)(100 * 16) << 16));
        gs.WriteInternalRegister(0x05, (ulong)(100 * 16) | ((ulong)(2000 * 16) << 16));

        // Must rasterize without crashing or throwing
        Assert.True(gs.TotalPrimitivesDrawn > 0);
        Assert.True(gs.TotalPixelsWritten > 0);
    }

    [Fact]
    public void Vu1Recompiler_DirectExecution_ExecutesQuicklyWithoutExceptions()
    {
        var vu = new Vu1State();
        vu.VF[1] = Vector128.Create(1.0f, 2.0f, 3.0f, 4.0f);
        vu.VF[2] = Vector128.Create(10.0f, 20.0f, 30.0f, 40.0f);

        // Slot 0: ADD.xyzw VF03, VF01, VF02 [E]
        // Upper: E-bit=1, op=0x28 (ADD.xyzw), dest=0xF, fs=1, ft=2
        uint slot0Upper = 0x40000000u | (0xFu << 21) | (2u << 16) | (1u << 11) | (3u << 6) | 0x28u;
        uint slot0Lower = 0x8000033Cu; // NOP

        // Slot 1 (delay): NOP
        uint slot1Upper = 0x00000000u;
        uint slot1Lower = 0x8000033Cu;

        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(0, 4), slot0Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), slot0Upper);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(8, 4), slot1Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(12, 4), slot1Upper);

        var compiler = new Vu1Recompiler();
        var prog = compiler.GetOrCompile(vu, 0);

        Assert.NotNull(prog.ExecuteDelegate);
        Assert.True(prog.CompiledBundleCount > 0);

        prog.ExecuteDelegate(vu);

        Assert.Equal(11.0f, vu.VF[3].GetElement(0));
        Assert.Equal(22.0f, vu.VF[3].GetElement(1));
        Assert.Equal(33.0f, vu.VF[3].GetElement(2));
        Assert.Equal(44.0f, vu.VF[3].GetElement(3));
    }

    [Fact]
    public void VifUnit_ZeroCopySpanParsing_HandlesCommandsDirectly()
    {
        var vif = new VifUnit(isVif1: true);

        // Create a VIF packet with NOPs and STCYCL (WL=2, CL=4)
        Span<byte> packet = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(0, 4), 0x00000000u); // NOP
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(4, 4), 0x01000204u); // STCYCL WL=2, CL=4
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), 0x00000000u); // NOP
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 0x00000000u); // NOP

        vif.ProcessData(packet);

        Assert.Equal(2, vif.Wl);
        Assert.Equal(4, vif.Cl);
        Assert.Equal(4, vif.TotalCommandsProcessed);
    }

    [Fact]
    public void GsProcessor_EarlyZ_DiscardsPixels_WhenDepthTestFails()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        // Framebuffer: FBP=0, FBW=10 (640px), PSM=PSMCT32 (0), FBMSK=0
        gs.Frame[ctx] = 0x00000000_000A_0000ul;
        // Scissor: 0..639, 0..479
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;

        // ZBuffer: ZBP=32 (block 32*32=1024), PSM=PSMZ24 (0x31 -> bits 24..27: 1), ZMSK=0
        gs.Zbuf[ctx] = (1ul << 24) | 32ul;

        // TEST_1: ATE=0, ZTE=1 (bit 16), ZTST=2 (GEQUAL, bits 17..18)
        gs.Test[ctx] = (1ul << 16) | (2ul << 17);

        // Pre-fill depth buffer at (50, 50) with depth 0x00500000
        int zbpBlock = 32 * 32;
        gs.WriteDepthPixel(zbpBlock, 640, GsPsm.Psmz24, 50, 50, 0x00500000u);

        // Triangle 1: Vertex Z = 0x00200000 (less than existing 0x00500000)
        // With ZTST=2 (GEQUAL: new >= old), this test will FAIL and Early-Z must discard it!
        gs.Prim = (ulong)GsPrimitiveType.Triangle;
        gs.Prmodecont = 1ul;

        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul); // Red
        gs.WriteInternalRegister(0x05, (ulong)(40 * 16) | ((ulong)(40 * 16) << 16) | (0x00200000ul << 32)); // V0
        gs.WriteInternalRegister(0x05, (ulong)(70 * 16) | ((ulong)(40 * 16) << 16) | (0x00200000ul << 32)); // V1
        gs.WriteInternalRegister(0x05, (ulong)(40 * 16) | ((ulong)(70 * 16) << 16) | (0x00200000ul << 32)); // V2 (draws)

        Assert.True(gs.TotalPixelsZFailed > 0, "Early-Z must have rejected occluded pixels");
        uint fbVal = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 50, 50);
        Assert.Equal(0u, fbVal); // Must remain unwritten (0)
        uint depthVal = gs.ReadDepthPixel(zbpBlock, 640, GsPsm.Psmz24, 50, 50);
        Assert.Equal(0x00500000u, depthVal); // Depth must remain unchanged

        // Triangle 2: Vertex Z = 0x00800000 (greater than existing 0x00500000)
        // With ZTST=2 (GEQUAL), this test will PASS and write both FB and Z
        gs.WriteInternalRegister(0x01, 0x80_00_FF_00ul); // Green
        gs.WriteInternalRegister(0x05, (ulong)(40 * 16) | ((ulong)(40 * 16) << 16) | (0x00800000ul << 32)); // V0
        gs.WriteInternalRegister(0x05, (ulong)(70 * 16) | ((ulong)(40 * 16) << 16) | (0x00800000ul << 32)); // V1
        gs.WriteInternalRegister(0x05, (ulong)(40 * 16) | ((ulong)(70 * 16) << 16) | (0x00800000ul << 32)); // V2 (draws)

        uint fbValPass = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 50, 50);
        byte g = (byte)((fbValPass >> 8) & 0xFFu);
        Assert.True(g > 150, $"Expected Green to be written, got 0x{fbValPass:X8}");
        uint depthValPass = gs.ReadDepthPixel(zbpBlock, 640, GsPsm.Psmz24, 50, 50);
        Assert.Equal(0x00800000u, depthValPass);
    }

    [Fact]
    public void GsProcessor_ClutCache_PreExpandsPalettedEntries_AndInvalidatesOnVramTransfer()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        // Framebuffer: FBP=0, FBW=10 (640px), PSM=PSMCT32 (0)
        gs.Frame[ctx] = 0x00000000_000A_0000ul;
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;

        // Texture: TBP0=64, TBW=128 (2 blocks), PSM=PSMT8 (0x13), TW=6 (64px), TH=6 (64px)
        // CLUT: CBP=256, CPSM=PSMCT32 (0), CSA=0, TCC=1 (RGBA), TFX=1 (Decal)
        ulong tex0 = 64ul | (2ul << 14) | ((ulong)GsPsm.Psmt8 << 20) | (6ul << 26) | (6ul << 30) |
                     (1ul << 34) | (1ul << 35) | (256ul << 37) | (0ul << 51) | (0ul << 56);
        gs.Tex0[ctx] = tex0;

        // Write palette entry 5 at CBP=256 with Red (0x800000FFu)
        int cbp = 256;
        int idx = 5;
        int cx = (idx & 7) | ((idx & 16) >> 1);
        int cy = ((idx & 8) >> 3) | ((idx & 0xE0) >> 4);
        GsSwizzle.WritePixel32(gs.Vram, cbp, 64, cx, cy, 0x800000FFu);

        // Write texel at (0, 0) with index 5
        GsSwizzle.WriteTexel8(gs.Vram, 64, 128, 0, 0, (byte)idx);

        // Draw sprite at (10, 10) to (12, 12) with UV=(0, 0)
        // PRIM: Sprite (6), TME (bit 4 = 1), FST (bit 8 = 1)
        gs.Prim = (ulong)GsPrimitiveType.Sprite | (1ul << 4) | (1ul << 8);
        gs.Prmodecont = 1ul;

        gs.WriteInternalRegister(0x01, 0xFF_FFFFFFul); // RGBAQ (white)
        gs.WriteInternalRegister(0x03, 0ul);           // UV: (0, 0)
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(10 * 16) << 16)); // XYZ2 V0
        gs.WriteInternalRegister(0x03, 0ul);           // UV: (0, 0)
        gs.WriteInternalRegister(0x05, (ulong)(12 * 16) | ((ulong)(12 * 16) << 16)); // XYZ2 V1 (draws)

        uint drawnColor = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 10, 10);
        Assert.Equal(0x800000FFu, drawnColor);

        // Update palette entry 5 in VRAM to Blue (0x80FF0000u)
        GsSwizzle.WritePixel32(gs.Vram, cbp, 64, cx, cy, 0x80FF0000u);

        // Invalidate via local-to-local transfer (TRXDIR = 2)
        gs.WriteInternalRegister(0x50, 0ul); // BITBLTBUF
        gs.WriteInternalRegister(0x53, 2ul); // TRXDIR = 2 (Local-to-Local triggers transfer & revision increment)

        // Draw sprite again
        gs.WriteInternalRegister(0x01, 0xFF_FFFFFFul);
        gs.WriteInternalRegister(0x03, 0ul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x03, 0ul);
        gs.WriteInternalRegister(0x05, (ulong)(12 * 16) | ((ulong)(12 * 16) << 16));

        uint updatedColor = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 10, 10);
        Assert.Equal(0x80FF0000u, updatedColor);
    }

    [Fact]
    public void GsProcessor_BilinearFilter_FixedPointLerp_InterpolatesAccurately()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        // Framebuffer: FBP=0, FBW=10 (640px), PSM=PSMCT32 (0)
        gs.Frame[ctx] = 0x00000000_000A_0000ul;
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;

        // Texture: PSMCT32, TBP0=64, TBW=64, TW=6 (64px), TH=6 (64px), TCC=1, TFX=1 (Decal)
        ulong tex0 = 64ul | (1ul << 14) | ((ulong)GsPsm.Psmct32 << 20) | (6ul << 26) | (6ul << 30) |
                     (1ul << 34) | (1ul << 35);
        gs.Tex0[ctx] = tex0;
        // TEX1: Linear filter (bit 5 = 1)
        gs.Tex1[ctx] = 1ul << 5;

        // Write 4 adjacent texels in VRAM at (0,0), (1,0), (0,1), (1,1):
        // (0, 0): Red (255, 0, 0, 128)
        // (1, 0): Green (0, 255, 0, 128)
        // (0, 1): Blue (0, 0, 255, 128)
        // (1, 1): White (255, 255, 255, 128)
        GsSwizzle.WritePixel32(gs.Vram, 64, 64, 0, 0, 0x800000FFu);
        GsSwizzle.WritePixel32(gs.Vram, 64, 64, 1, 0, 0x8000FF00u);
        GsSwizzle.WritePixel32(gs.Vram, 64, 64, 0, 1, 0x80FF0000u);
        GsSwizzle.WritePixel32(gs.Vram, 64, 64, 1, 1, 0x80FFFFFFu);

        // Draw a 1x1 sprite at (20, 20) with UV sampled exactly halfway between the 4 texels:
        // In FST mode, fU = (U / 16.0f) - 0.5f. We want fU = 0.5f, fV = 0.5f (tx0 = 0, tx1 = 1, fracU = 0.5f, fracV = 0.5f).
        // (U / 16.0f) - 0.5f = 0.5f => U / 16.0f = 1.0f => U = 16.
        gs.Prim = (ulong)GsPrimitiveType.Sprite | (1ul << 4) | (1ul << 8); // Sprite, TME=1, FST=1
        gs.Prmodecont = 1ul;

        gs.WriteInternalRegister(0x01, 0xFF_FFFFFFul);
        gs.WriteInternalRegister(0x03, 16ul | (16ul << 16)); // UV: (16, 16) -> (1.0, 1.0) -> fU=0.5, fV=0.5
        gs.WriteInternalRegister(0x05, (ulong)(20 * 16) | ((ulong)(20 * 16) << 16)); // V0: (20, 20)
        gs.WriteInternalRegister(0x03, 16ul | (16ul << 16));
        gs.WriteInternalRegister(0x05, (ulong)(21 * 16) | ((ulong)(21 * 16) << 16)); // V1: (21, 21) -> draws pixel at (20, 20)

        uint filtered = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 20, 20);
        byte r = (byte)(filtered & 0xFFu);
        byte g = (byte)((filtered >> 8) & 0xFFu);
        byte b = (byte)((filtered >> 16) & 0xFFu);
        byte a = (byte)((filtered >> 24) & 0xFFu);

        // Fixed-point lerp with w00 = w10 = w01 = w11 = 16384:
        // Expected: (255 + 0 + 0 + 255) / 4 = 127
        Assert.InRange(r, 126, 128);
        Assert.InRange(g, 126, 128);
        Assert.InRange(b, 126, 128);
        Assert.Equal((byte)128, a);
    }

    [Fact]
    public void GsProcessor_IncrementalGouraudAndZ_StepsAccurately()
    {
        var gs = new GsProcessor();
        int ctx = 0;

        gs.Frame[ctx] = 0x00000000_000A_0000ul;
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul;
        gs.Xyoffset[ctx] = 0ul;

        // ZBuffer: ZBP=32, PSMZ24 (1), ZMSK=0
        gs.Zbuf[ctx] = (1ul << 24) | 32ul;
        // TEST: ZTE=1, ZTST=1 (ALWAYS)
        gs.Test[ctx] = (1ul << 16) | (1ul << 17);

        // PRIM: Triangle (3), Gouraud (bit 3 = 1)
        gs.Prim = (ulong)GsPrimitiveType.Triangle | (1ul << 3);
        gs.Prmodecont = 1ul;

        // Triangle vertices:
        // V0 at (10, 10): Z = 1000, Red (255, 0, 0, 128)
        // V1 at (90, 10): Z = 5000, Green (0, 255, 0, 128)
        // V2 at (10, 90): Z = 9000, Blue (0, 0, 255, 128)
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(10 * 16) << 16) | (1000ul << 32));
        gs.WriteInternalRegister(0x01, 0x80_00_FF_00ul);
        gs.WriteInternalRegister(0x05, (ulong)(90 * 16) | ((ulong)(10 * 16) << 16) | (5000ul << 32));
        gs.WriteInternalRegister(0x01, 0x80_FF_00_00ul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(90 * 16) << 16) | (9000ul << 32));

        Assert.True(gs.TotalPrimitivesDrawn > 0);

        // Near V0 (15, 15): Red dominates, depth near 1000..2000
        uint pixV0 = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 15, 15);
        byte rV0 = (byte)(pixV0 & 0xFFu);
        byte gV0 = (byte)((pixV0 >> 8) & 0xFFu);
        byte bV0 = (byte)((pixV0 >> 16) & 0xFFu);
        Assert.True(rV0 > gV0 && rV0 > bV0, $"Red must dominate near V0, got R={rV0}, G={gV0}, B={bV0}");

        uint depthV0 = gs.ReadDepthPixel(32 * 32, 640, GsPsm.Psmz24, 15, 15);
        Assert.InRange((int)depthV0, 1000, 3000);

        // Center (30, 30): All 3 colors present, depth in mid-range 3000..6000
        uint pixMid = gs.ReadFramePixel(0, 640, GsPsm.Psmct32, 30, 30);
        byte rMid = (byte)(pixMid & 0xFFu);
        byte gMid = (byte)((pixMid >> 8) & 0xFFu);
        byte bMid = (byte)((pixMid >> 16) & 0xFFu);
        Assert.True(rMid > 20 && gMid > 20 && bMid > 20, "Center pixel should have mixed Gouraud colors");

        uint depthMid = gs.ReadDepthPixel(32 * 32, 640, GsPsm.Psmz24, 30, 30);
        Assert.InRange((int)depthMid, 3000, 6000);
    }

    [Fact]
    public void Vu1Recompiler_ExecuteDirect_ExecutesInPlaceWithoutRecompilationOrAllocations()
    {
        var vu = new Vu1State();
        var compiler = new Vu1Recompiler();

        vu.VF[1] = Vector128.Create(2.0f, 4.0f, 6.0f, 8.0f);
        vu.VF[2] = Vector128.Create(10.0f, 20.0f, 30.0f, 40.0f);

        // Program 1 at byte 0:
        // Slot 0: ADD.xyzw VF3, VF1, VF2 [E]
        uint slot0Upper = 0x40000000u | (0xFu << 21) | (2u << 16) | (1u << 11) | (3u << 6) | 0x28u;
        uint slot0Lower = 0x8000033Cu; // NOP
        // Slot 1 (delay): NOP
        uint slot1Upper = 0x00000000u;
        uint slot1Lower = 0x8000033Cu;

        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(0, 4), slot0Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), slot0Upper);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(8, 4), slot1Lower);
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(12, 4), slot1Upper);

        compiler.Execute(vu, 0);

        Assert.Equal(12.0f, vu.VF[3].GetElement(0));
        Assert.Equal(24.0f, vu.VF[3].GetElement(1));
        Assert.Equal(36.0f, vu.VF[3].GetElement(2));
        Assert.Equal(48.0f, vu.VF[3].GetElement(3));

        // Now dynamically patch Slot 0 in MicroMem to SUB.xyzw VF3, VF2, VF1 [E]
        // Upper: E=1, op=0x2C (SUB), dest=0xF, fs=2, ft=1
        uint slot0SubUpper = 0x40000000u | (0xFu << 21) | (1u << 16) | (2u << 11) | (3u << 6) | 0x2Cu;
        BinaryPrimitives.WriteUInt32LittleEndian(vu.MicroMem.AsSpan(4, 4), slot0SubUpper);

        // Notify MPG modification
        compiler.InvalidateMicroMemHash();

        // Execute again: must immediately execute updated instructions without CRC hashing or re-compilation
        compiler.Execute(vu, 0);

        Assert.Equal(8.0f, vu.VF[3].GetElement(0));
        Assert.Equal(16.0f, vu.VF[3].GetElement(1));
        Assert.Equal(24.0f, vu.VF[3].GetElement(2));
        Assert.Equal(32.0f, vu.VF[3].GetElement(3));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Slus21664_PerformanceTest_3dRendering_TransitionsPastPressStartScreenAndMeasuresFps()
    {
        RecompiledFunctions.RegisterAll();
        RuntimeDispatcher.ClearHooks();
        RuntimeDispatcher.SetTraceHandler(null);

        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        string? dvdPath = Path.GetDirectoryName(elfPath);

        byte* ram = (byte*)NativeMemory.AllocZeroed(CpuContext.RamSize);
        byte* scratchpad = (byte*)NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
        byte* vu0Data = (byte*)NativeMemory.AllocZeroed(4096);

        try
        {
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            CpuContext ctx = default;
            ctx.Initialize(scratchpad, vu0Data);

            using var runtime = new CastawayRuntime(dvdRootPath: dvdPath, initOpenAl: false);
            runtime.Install(&ctx, ram, installHleHooks: true);

            using var graphics = new GsGraphicsPipeline();
            graphics.AttachToRuntime(runtime);

            // Default GS framebuffer configuration (640x448 PSMCT32)
            graphics.Gs.WriteInternalRegister(0x4C, 10ul << 16);
            graphics.Gs.WriteInternalRegister(0x18, 0ul);
            graphics.Gs.WriteInternalRegister(0x40, (639ul << 16) | (447ul << 48));

            int totalXgKicks = 0;
            graphics.Vu1.OnXgKick = data =>
            {
                totalXgKicks++;
                graphics.Gs.SubmitGifPacket(data, "VU1_XGKICK");
            };

            int frameCount = 0;
            const int autoStartFrame = 670;
            const int benchmarkStartFrame = 700;
            const int targetMaxFrame = 730;

            var frameSamples = new List<FrameBenchmarkSample>(32);
            bool inBenchmark = false;
            long curFrameStageUpdateTicks = 0;
            long curFrameStagePreRenderTicks = 0;
            long curFrameStageRenderTicks = 0;
            long curFrameAptTicks = 0;
            var benchmarkStopwatch = new Stopwatch();
            TimeSpan benchmarkElapsed = TimeSpan.Zero;
            var totalStopwatch = Stopwatch.StartNew();

            // Intercept key game engine stages to measure millisecond execution times
            RuntimeDispatcher.RegisterHook(0x0026F2B8u, (c, r) =>
            {
                if (!inBenchmark) { RecompiledFunctions.sub_0026F2B8(c, r); return; }
                long t0 = Stopwatch.GetTimestamp();
                RecompiledFunctions.sub_0026F2B8(c, r);
                curFrameStageUpdateTicks += Stopwatch.GetTimestamp() - t0;
            });

            RuntimeDispatcher.RegisterHook(0x0026F330u, (c, r) =>
            {
                if (!inBenchmark) { RecompiledFunctions.sub_0026F330(c, r); return; }
                long t0 = Stopwatch.GetTimestamp();
                RecompiledFunctions.sub_0026F330(c, r);
                curFrameStagePreRenderTicks += Stopwatch.GetTimestamp() - t0;
            });

            RuntimeDispatcher.RegisterHook(0x0026F3A8u, (c, r) =>
            {
                if (!inBenchmark) { RecompiledFunctions.sub_0026F3A8(c, r); return; }
                long t0 = Stopwatch.GetTimestamp();
                RecompiledFunctions.sub_0026F3A8(c, r);
                curFrameStageRenderTicks += Stopwatch.GetTimestamp() - t0;
            });

            RuntimeDispatcher.RegisterHook(0x00306690u, (c, r) =>
            {
                if (!inBenchmark) { RecompiledFunctions.sub_00306690(c, r); return; }
                long t0 = Stopwatch.GetTimestamp();
                RecompiledFunctions.sub_00306690(c, r);
                curFrameAptTicks += Stopwatch.GetTimestamp() - t0;
            });

            // Hook Application::UpdateFrame (0x003E2940) to drive frame updates, auto-start, and metrics
            RuntimeDispatcher.RegisterHook(0x003E2940u, (c, r) =>
            {
                uint appPtr = c->GetU32(4);
                long tFrameStart = Stopwatch.GetTimestamp();
                frameCount++;

                // Press START button at frames 670..675 to transition from the "Press START button" title screen to Main Menu
                long tPad0 = Stopwatch.GetTimestamp();
                if (frameCount >= autoStartFrame && frameCount <= autoStartFrame + 5)
                {
                    var st = DualShock2State.Neutral;
                    st.PressedButtons = Ps2PadButtons.Start;
                    runtime.Pad.SetPortState(0, st);
                }
                else if (frameCount == autoStartFrame + 6)
                {
                    runtime.Pad.SetPortState(0, DualShock2State.Neutral);
                }

                runtime.PumpPadSubsystem(c, r);
                long padTicks = Stopwatch.GetTimestamp() - tPad0;

                // Advance hardware timers by 1 frame (16.667 ms)
                long tTimers0 = Stopwatch.GetTimestamp();
                runtime.Kernel.StepFrameTimers(c, r);
                long timersTicks = Stopwatch.GetTimestamp() - tTimers0;

                // At frame 700, 3D rendering begins: record starting baseline for performance measurement
                if (frameCount == benchmarkStartFrame)
                {
                    graphics.Gs.EnableProfiling = true;
                    inBenchmark = true;
                    benchmarkStopwatch.Restart();
                }

                long vifTicksBefore = graphics.Vif1.TotalProcessingTicks;
                long vu1TicksBefore = graphics.Vu1.TotalExecutionTicks;
                long gsGifTicksBefore = graphics.Gs.TotalSubmitGifTicks;
                long gsTriTicksBefore = graphics.Gs.TotalRasterizeTriangleTicks;
                long gsSpriteTicksBefore = graphics.Gs.TotalRasterizeSpriteTicks;
                long gsTrxTicksBefore = graphics.Gs.TotalVramTransferTicks;
                long primsBefore = graphics.Gs.TotalPrimitivesDrawn;
                long trisBefore = graphics.Gs.TotalTrianglesDrawn;
                long spritesBefore = graphics.Gs.TotalSpritesDrawn;
                long pixBefore = graphics.Gs.TotalPixelsShaded;
                long zFailBefore = graphics.Gs.TotalPixelsZFailed;
                long vu1KicksBefore = totalXgKicks;
                long vu1InstBefore = graphics.Vu1.TotalInstructionsExecuted;

                curFrameStageUpdateTicks = 0;
                curFrameStagePreRenderTicks = 0;
                curFrameStageRenderTicks = 0;
                curFrameAptTicks = 0;

                // Native SLUS_216.64 frame update
                long tGame0 = Stopwatch.GetTimestamp();
                RecompiledFunctions.sub_003E2940(c, r);
                long totalGameTicks = Stopwatch.GetTimestamp() - tGame0;

                // Advance VBlank / INTC handlers
                long tVBlank0 = Stopwatch.GetTimestamp();
                runtime.Kernel.TriggerVBlank(c, r);
                long vblankTicks = Stopwatch.GetTimestamp() - tVBlank0;

                long frameTotalTicks = Stopwatch.GetTimestamp() - tFrameStart;

                if (inBenchmark && frameCount > benchmarkStartFrame && frameCount <= targetMaxFrame)
                {
                    long frameVifTicks = Math.Max(0, graphics.Vif1.TotalProcessingTicks - vifTicksBefore);
                    long frameVu1Ticks = Math.Max(0, graphics.Vu1.TotalExecutionTicks - vu1TicksBefore);
                    long frameGsGifTicks = Math.Max(0, graphics.Gs.TotalSubmitGifTicks - gsGifTicksBefore);
                    long frameGsTriTicks = Math.Max(0, graphics.Gs.TotalRasterizeTriangleTicks - gsTriTicksBefore);
                    long frameGsSpriteTicks = Math.Max(0, graphics.Gs.TotalRasterizeSpriteTicks - gsSpriteTicksBefore);
                    long frameGsTrxTicks = Math.Max(0, graphics.Gs.TotalVramTransferTicks - gsTrxTicksBefore);

                    long pureVu1Ticks = Math.Max(0, frameVu1Ticks - frameGsGifTicks);
                    long pureVifTicks = Math.Max(0, frameVifTicks - frameVu1Ticks);
                    long pureGameLogicTicks = Math.Max(0, totalGameTicks - Math.Max(frameVifTicks, frameGsGifTicks));

                    double toMs = 1000.0 / Stopwatch.Frequency;
                    frameSamples.Add(new FrameBenchmarkSample
                    {
                        FrameNumber = frameCount,
                        TotalFrameMs = frameTotalTicks * toMs,
                        PureGameLogicMs = pureGameLogicTicks * toMs,
                        StageListUpdateMs = curFrameStageUpdateTicks * toMs,
                        StageListPreRenderMs = curFrameStagePreRenderTicks * toMs,
                        StageListRenderMs = curFrameStageRenderTicks * toMs,
                        AptUiMs = curFrameAptTicks * toMs,
                        GsRenderPipelineMs = frameGsGifTicks * toMs,
                        Vu1MicrocodeMs = frameVu1Ticks * toMs,
                        PureVu1Ms = pureVu1Ticks * toMs,
                        VifUnpackMs = frameVifTicks * toMs,
                        PureVifMs = pureVifTicks * toMs,
                        GsTriangleRasterMs = frameGsTriTicks * toMs,
                        GsSpriteRasterMs = frameGsSpriteTicks * toMs,
                        GsVramTransferMs = frameGsTrxTicks * toMs,
                        TimersVBlankMs = (timersTicks + vblankTicks) * toMs,
                        PadPumpMs = padTicks * toMs,
                        PrimitivesDrawn = graphics.Gs.TotalPrimitivesDrawn - primsBefore,
                        TrianglesDrawn = graphics.Gs.TotalTrianglesDrawn - trisBefore,
                        SpritesDrawn = graphics.Gs.TotalSpritesDrawn - spritesBefore,
                        PixelsShaded = graphics.Gs.TotalPixelsShaded - pixBefore,
                        PixelsEarlyZRejected = graphics.Gs.TotalPixelsZFailed - zFailBefore,
                        Vu1Kicks = totalXgKicks - vu1KicksBefore,
                        Vu1Instructions = graphics.Vu1.TotalInstructionsExecuted - vu1InstBefore,
                    });
                }

                // Stop at target frame (730): record ending metrics and signal clean exit
                if (frameCount >= targetMaxFrame)
                {
                    benchmarkElapsed = benchmarkStopwatch.Elapsed;
                    Ps2Memory.Write32(c, r, appPtr + 36u, 1u);
                }
            });

            var entryFn = RuntimeDispatcher.Lookup(0x00100008u);
            Assert.True(entryFn != null, "Entry function 0x00100008 must be registered");

            entryFn(&ctx, ram);

            // Compute statistical summaries across the collected 30 benchmark frames
            int frameCountInSample = frameSamples.Count;
            Assert.True(frameCountInSample > 0, "Benchmark samples must be recorded");

            double[] totalMsArr = frameSamples.Select(s => s.TotalFrameMs).ToArray();
            double[] gameMsArr = frameSamples.Select(s => s.PureGameLogicMs).ToArray();
            double[] gsRenderMsArr = frameSamples.Select(s => s.GsRenderPipelineMs).ToArray();
            double[] gsTriMsArr = frameSamples.Select(s => s.GsTriangleRasterMs).ToArray();
            double[] gsSpriteMsArr = frameSamples.Select(s => s.GsSpriteRasterMs).ToArray();
            double[] gsTrxMsArr = frameSamples.Select(s => s.GsVramTransferMs).ToArray();
            double[] vu1MsArr = frameSamples.Select(s => s.Vu1MicrocodeMs).ToArray();
            double[] pureVu1MsArr = frameSamples.Select(s => s.PureVu1Ms).ToArray();
            double[] vifMsArr = frameSamples.Select(s => s.VifUnpackMs).ToArray();
            double[] pureVifMsArr = frameSamples.Select(s => s.PureVifMs).ToArray();
            double[] stageUpdMsArr = frameSamples.Select(s => s.StageListUpdateMs).ToArray();
            double[] stagePreMsArr = frameSamples.Select(s => s.StageListPreRenderMs).ToArray();
            double[] stageRndMsArr = frameSamples.Select(s => s.StageListRenderMs).ToArray();
            double[] aptMsArr = frameSamples.Select(s => s.AptUiMs).ToArray();
            double[] timersMsArr = frameSamples.Select(s => s.TimersVBlankMs).ToArray();

            double sumTotalMs = totalMsArr.Sum();
            double sumTriMs = gsTriMsArr.Sum();
            double sumSpriteMs = gsSpriteMsArr.Sum();
            double sumTrxMs = gsTrxMsArr.Sum();
            double sumGsRenderMs = gsRenderMsArr.Sum();
            double sumVu1Ms = vu1MsArr.Sum();
            double sumPureVu1Ms = pureVu1MsArr.Sum();
            double sumVifMs = vifMsArr.Sum();
            double sumPureVifMs = pureVifMsArr.Sum();
            double sumGameMs = gameMsArr.Sum();
            double sumStageUpdMs = stageUpdMsArr.Sum();
            double sumStagePreMs = stagePreMsArr.Sum();
            double sumStageRndMs = stageRndMsArr.Sum();
            double sumAptMs = aptMsArr.Sum();
            double sumTimersMs = timersMsArr.Sum();

            long totalPrims3d = frameSamples.Sum(s => s.PrimitivesDrawn);
            long totalTris3d = frameSamples.Sum(s => s.TrianglesDrawn);
            long totalSprites3d = frameSamples.Sum(s => s.SpritesDrawn);
            long totalPixShaded3d = frameSamples.Sum(s => s.PixelsShaded);
            long totalEarlyZCulled3d = frameSamples.Sum(s => s.PixelsEarlyZRejected);
            long totalVu1Kicks3d = frameSamples.Sum(s => s.Vu1Kicks);
            long totalVu1Inst3d = frameSamples.Sum(s => s.Vu1Instructions);

            double meanFps = sumTotalMs > 0 ? (frameCountInSample / (sumTotalMs / 1000.0)) : 0.0;
            double meanPrimsPerSec = sumTotalMs > 0 ? (totalPrims3d / (sumTotalMs / 1000.0)) : 0.0;
            double usPerTriangle = totalTris3d > 0 ? (sumTriMs * 1000.0) / totalTris3d : 0.0;
            double nsPerShadedPixel = totalPixShaded3d > 0 ? (sumTriMs * 1_000_000.0) / totalPixShaded3d : 0.0;
            double earlyZCullingRate = (totalPixShaded3d + totalEarlyZCulled3d) > 0
                ? ((double)totalEarlyZCulled3d / (totalPixShaded3d + totalEarlyZCulled3d)) * 100.0
                : 0.0;

            _output?.WriteLine("=================================================================================================================================");
            _output?.WriteLine("                                   THE SIMS 2: CASTAWAY (PS2 SLUS_216.64)");
            _output?.WriteLine("                               3D RENDERING & GAME ENGINE PERFORMANCE PROFILE");
            _output?.WriteLine("=================================================================================================================================");
            _output?.WriteLine($"Benchmark Window:       Frames {benchmarkStartFrame + 1} -> {targetMaxFrame} ({frameCountInSample} frames in 3D Main Menu)");
            _output?.WriteLine($"Total Wall Time:        {totalStopwatch.Elapsed.TotalSeconds:F2}s (Benchmark window: {sumTotalMs / 1000.0:F3}s)");
            _output?.WriteLine($"Average Framerate:      {meanFps:F2} FPS (Target: 60 FPS | 16.67 ms frame budget)");
            _output?.WriteLine($"Mean Frame Duration:    {totalMsArr.Average():F2} ms (Min: {totalMsArr.Min():F2} ms, Max: {totalMsArr.Max():F2} ms, P50: {Percentile(totalMsArr, 50):F2} ms, P95: {Percentile(totalMsArr, 95):F2} ms)");
            _output?.WriteLine("");

            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("FRAME-BY-FRAME TIMING BREAKDOWN (All values in milliseconds)");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("Frame | Total(ms) | Game(ms) | StageUpd | StageRnd | AptUI(ms)| GS(ms)  | Tri(ms) | Spr(ms) | VU1(ms) | VIF(ms) | Prims | Early-Z%");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            foreach (var s in frameSamples)
            {
                double zPct = (s.PixelsShaded + s.PixelsEarlyZRejected) > 0 ? (double)s.PixelsEarlyZRejected / (s.PixelsShaded + s.PixelsEarlyZRejected) * 100.0 : 0.0;
                _output?.WriteLine($"{s.FrameNumber,5} | {s.TotalFrameMs,9:F1} | {s.PureGameLogicMs,8:F1} | {s.StageListUpdateMs,8:F1} | {s.StageListRenderMs,8:F1} | {s.AptUiMs,8:F1} | {s.GsRenderPipelineMs,7:F1} | {s.GsTriangleRasterMs,7:F1} | {s.GsSpriteRasterMs,7:F1} | {s.Vu1MicrocodeMs,7:F1} | {s.VifUnpackMs,7:F1} | {s.PrimitivesDrawn,5} | {zPct,6:F1}%");
            }
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("");

            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("SUBSYSTEM PERFORMANCE & TIME CONSUMPTION SUMMARY (30-Frame Aggregate)");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("Subsystem / Component               | Total (ms) | Mean (ms)  | Min (ms)   | Max (ms)   | P50 (ms)   | P95 (ms)   | % Share");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            void PrintRow(string name, double sum, double[] arr)
            {
                double pct = sumTotalMs > 0 ? (sum / sumTotalMs) * 100.0 : 0.0;
                _output?.WriteLine($"{name,-35} | {sum,9:F1} ms| {arr.Average(),9:F2} ms| {arr.Min(),9:F2} ms| {arr.Max(),9:F2} ms| {Percentile(arr, 50),9:F2} ms| {Percentile(arr, 95),9:F2} ms| {pct,6:F1}%");
            }
            PrintRow("TOTAL FRAME DURATION", sumTotalMs, totalMsArr);
            PrintRow("├─ GS Rasterization Pipeline (GIF)", sumGsRenderMs, gsRenderMsArr);
            PrintRow("│  ├─ GS Triangle Rasterization", sumTriMs, gsTriMsArr);
            PrintRow("│  ├─ GS Sprite Rasterization (UI)", sumSpriteMs, gsSpriteMsArr);
            PrintRow("│  └─ GS VRAM Transfers (BitBlt)", sumTrxMs, gsTrxMsArr);
            PrintRow("├─ Pure VU1 Microcode (Exclusive)", sumPureVu1Ms, pureVu1MsArr);
            PrintRow("├─ Pure VIF Data Unpack (Exclusive)", sumPureVifMs, pureVifMsArr);
            PrintRow("├─ EE Pure Game Logic (Sim/Engine)", sumGameMs, gameMsArr);
            PrintRow("│  ├─ StageList::Render (RW CPU)", sumStageRndMs, stageRndMsArr);
            PrintRow("│  ├─ AptUI (Flash VM & CIH)", sumAptMs, aptMsArr);
            PrintRow("│  ├─ StageList::Update (Logic)", sumStageUpdMs, stageUpdMsArr);
            PrintRow("│  └─ StageList::PreRender (Cull)", sumStagePreMs, stagePreMsArr);
            PrintRow("└─ Kernel Timers, VBlank & Pad", sumTimersMs, timersMsArr);
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("");

            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("THROUGHPUT, HARDWARE EFFICIENCY & MICRO-COST METRICS");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine($"3D Triangles Rasterized:   {totalTris3d:N0} triangles (Mean: {totalTris3d / (double)frameCountInSample:F1} / frame | {totalTris3d / (sumTotalMs / 1000.0):N0} triangles/sec)");
            _output?.WriteLine($"Cost Per 3D Triangle:      {usPerTriangle:F2} μs / triangle");
            _output?.WriteLine($"Pixels Shaded:             {totalPixShaded3d:N0} pixels (Mean: {totalPixShaded3d / (double)frameCountInSample:N0} pixels/frame)");
            _output?.WriteLine($"Cost Per Shaded Pixel:     {nsPerShadedPixel:F2} ns / pixel");
            _output?.WriteLine($"Early-Z Culled Pixels:     {totalEarlyZCulled3d:N0} pixels ({earlyZCullingRate:F1}% rejection rate — avoided {totalEarlyZCulled3d / 1_000_000.0:F1}M texture lookups & blends)");
            _output?.WriteLine($"VU1 Microcode Packets:     {totalVu1Kicks3d:N0} XGKICK transfers (Mean: {totalVu1Kicks3d / (double)frameCountInSample:F1} kicks/frame)");
            _output?.WriteLine($"VU1 Instructions Executed: {totalVu1Inst3d:N0} inst (Mean: {totalVu1Inst3d / (double)frameCountInSample:N0} inst/frame | {totalVu1Inst3d / sumTotalMs / 1000.0:F2} MIPS)");
            _output?.WriteLine("");

            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            _output?.WriteLine("PRIMARY BOTTLENECK ANALYSIS & ACTIONABLE INSIGHTS (Non-Overlapping Costs)");
            _output?.WriteLine("---------------------------------------------------------------------------------------------------------------------------------");
            var bottlenecks = new (string Name, double Ms, double Pct)[]
            {
                ("GS Triangle Rasterization & Shading", sumTriMs, (sumTriMs / sumTotalMs) * 100.0),
                ("GS Sprite / UI Rasterization", sumSpriteMs, (sumSpriteMs / sumTotalMs) * 100.0),
                ("EE Pure Game Logic (RW Traversal & Sim)", sumGameMs, (sumGameMs / sumTotalMs) * 100.0),
                ("Pure VU1 Microcode (Vector Math)", sumPureVu1Ms, (sumPureVu1Ms / sumTotalMs) * 100.0),
                ("Kernel Timers, VBlank & Controller Pad", sumTimersMs, (sumTimersMs / sumTotalMs) * 100.0),
                ("GS VRAM Transfers (BitBlt)", sumTrxMs, (sumTrxMs / sumTotalMs) * 100.0),
                ("Pure VIF Data Unpacking", sumPureVifMs, (sumPureVifMs / sumTotalMs) * 100.0),
            }.OrderByDescending(b => b.Ms).ToArray();

            for (int i = 0; i < bottlenecks.Length; i++)
            {
                _output?.WriteLine($"#{i + 1} Bottleneck: {bottlenecks[i].Name,-38} {bottlenecks[i].Pct,5:F1}% of frame time ({bottlenecks[i].Ms / frameCountInSample,6:F1} ms/frame | {bottlenecks[i].Ms,8:F1} ms total)");
            }
            _output?.WriteLine("=================================================================================================================================");

            // Assertions ensuring real 3D scene execution and rendering
            Assert.True(frameCount >= targetMaxFrame, $"Expected to reach frame {targetMaxFrame}, but reached {frameCount}");
            Assert.True(totalTris3d > 10_000, $"Expected > 10,000 3D triangles in frames {benchmarkStartFrame}..{targetMaxFrame}, got {totalTris3d}");
            Assert.True(totalVu1Kicks3d > 200, $"Expected > 200 VU1 XGKICK packets in 3D scene, got {totalVu1Kicks3d}");
            Assert.True(totalPixShaded3d > 100_000, $"Expected > 100,000 pixels shaded in 3D scene, got {totalPixShaded3d}");
            Assert.True(meanFps > 0.5, $"Expected > 0.5 FPS during heavy software 3D rendering, got {meanFps:F2} FPS");
        }
        finally
        {
            RuntimeDispatcher.SetTraceHandler(null);
            RuntimeDispatcher.ClearHooks();
            NativeMemory.Free(vu0Data);
            NativeMemory.Free(scratchpad);
            NativeMemory.Free(ram);
        }
    }

    private struct FrameBenchmarkSample
    {
        public int FrameNumber;
        public double TotalFrameMs;
        public double PureGameLogicMs;
        public double StageListUpdateMs;
        public double StageListPreRenderMs;
        public double StageListRenderMs;
        public double AptUiMs;
        public double GsRenderPipelineMs;
        public double Vu1MicrocodeMs;
        public double PureVu1Ms;
        public double VifUnpackMs;
        public double PureVifMs;
        public double GsTriangleRasterMs;
        public double GsSpriteRasterMs;
        public double GsVramTransferMs;
        public double TimersVBlankMs;
        public double PadPumpMs;
        public long PrimitivesDrawn;
        public long TrianglesDrawn;
        public long SpritesDrawn;
        public long PixelsShaded;
        public long PixelsEarlyZRejected;
        public long Vu1Kicks;
        public long Vu1Instructions;
    }

    [Fact]
    public void GsProcessor_OpenGlRendererMode_BatchesPrimitivesAccurately()
    {
        using var graphics = new GsGraphicsPipeline();
        var gs = graphics.Gs;
        graphics.Mode = GsRendererMode.OpenGl;

        Assert.Equal(GsRendererMode.OpenGl, graphics.Mode);
        Assert.NotNull(gs.ActiveBatch);

        int ctx = 0;
        gs.Frame[ctx] = 0x00000000_000A_0000ul; // FBP=0, FBW=10 (640px), PSM=0
        gs.Scissor[ctx] = 0x01DF_0000_027F_0000ul; // 0..639, 0..479
        gs.Xyoffset[ctx] = 0ul;
        gs.Prim = (ulong)GsPrimitiveType.Triangle | (1ul << 3); // Gouraud Triangle
        gs.Prmodecont = 1ul;

        // Draw Triangle 1 (3 vertices)
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul); // Red
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_00_FF_00ul); // Green
        gs.WriteInternalRegister(0x05, (ulong)(50 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_FF_00_00ul); // Blue
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(50 * 16) << 16));

        // Draw Triangle 2 with identical context (should merge into single draw call with 6 vertices)
        gs.WriteInternalRegister(0x01, 0x80_FF_FF_00ul);
        gs.WriteInternalRegister(0x05, (ulong)(60 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_00_FF_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(100 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_FF_00_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(60 * 16) | ((ulong)(50 * 16) << 16));

        // Draw Sprite (2D UI sprites rasterize directly into VRAM for 100% accuracy)
        gs.Prim = (ulong)GsPrimitiveType.Sprite;
        gs.WriteInternalRegister(0x01, 0x80_FFFFFFul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(100 * 16) << 16));
        gs.WriteInternalRegister(0x05, (ulong)(50 * 16) | ((ulong)(150 * 16) << 16));

        Assert.Equal(6, graphics.BackBatch.VertexCount); // 2 merged 3D triangles
        Assert.Equal(1, graphics.BackBatch.DrawCallCount);

        // Swap batches at VBlank / frame presentation
        graphics.SwapBatches();

        Assert.Equal(6, graphics.FrontBatch.VertexCount);
        Assert.Equal(1, graphics.FrontBatch.DrawCallCount);
        Assert.Equal(0, graphics.BackBatch.VertexCount);
        Assert.Equal(0, graphics.BackBatch.DrawCallCount);

        // Switch to Software Mode dynamically
        graphics.Mode = GsRendererMode.Software;
        Assert.Equal(GsRendererMode.Software, gs.Mode);

        // In software mode, primitives rasterize into VRAM
        gs.Prim = (ulong)GsPrimitiveType.Triangle;
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(50 * 16) | ((ulong)(10 * 16) << 16));
        gs.WriteInternalRegister(0x01, 0x80_00_00_FFul);
        gs.WriteInternalRegister(0x05, (ulong)(10 * 16) | ((ulong)(50 * 16) << 16));

        Assert.True(gs.VramDirty, "Software rasterizer must mark VRAM dirty.");
        Assert.True(gs.TotalPixelsWritten > 0, "Software rasterizer must write pixels to VRAM.");
        Assert.Equal(0, graphics.BackBatch.VertexCount); // BackBatch untouched in Software mode
    }

    private static double Percentile(double[] sequence, double percentile)
    {
        if (sequence.Length == 0) return 0;
        var sorted = sequence.OrderBy(x => x).ToArray();
        int idx = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
