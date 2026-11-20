using System.Runtime.InteropServices;
using Castaway.Core;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.OpenGl;
using Castaway.Overrides;
using Castaway.Runtime;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Xunit;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase6OverridesTests
{
    [Theory]
    [InlineData(640, 448)]
    [InlineData(800, 600)]
    [InlineData(1920, 1080)]
    [InlineData(3440, 1440)]
    [InlineData(1000, 1000)]
    [InlineData(900, 1600)]
    public void OpenGlAspectRatioHook_MatchesAnyViewportAndFixesProjectionMatrixRatio(int vpWidth, int vpHeight)
    {
        RecompiledFunctions.RegisterAll();
        RuntimeDispatcher.ClearHooks();
        RuntimeDispatcher.SetTraceHandler(null);

        byte* ram = (byte*)NativeMemory.AllocZeroed(CpuContext.RamSize);
        byte* scratchpad = (byte*)NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
        byte* vu0Data = (byte*)NativeMemory.AllocZeroed(4096);

        try
        {
            string elfPath = Ps2ElfReader.FindDefaultElfPath();
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            CpuContext ctx = default;
            ctx.Initialize(scratchpad, vu0Data);

            using var runtime = new CastawayRuntime(initOpenAl: false);
            using var graphics = new GsGraphicsPipeline();
            var overrides = new CastawayOverrides(new OverrideOptions
            {
                RendererMode = GsRendererMode.OpenGl
            });

            overrides.SetViewportAspectRatio(vpWidth, vpHeight);
            overrides.Install(runtime, graphics);

            // Call PS2Display::GetAspectRatio() (0x00433B00)
            ctx.SetU32(31, 0x00100100u);
            RecompiledFunctions.sub_00433B00(&ctx, ram);
            float returnedAspect = ctx.F[0];
            float expectedAspect = (float)vpWidth / vpHeight;
            Assert.InRange(returnedAspect, expectedAspect - 1e-5f, expectedAspect + 1e-5f);

            // Feed returned aspect into sub_003DBFD0(outMat, fovY, aspect, near, far)
            const uint matAddr = 0x00800000u;
            ctx.SetU32(29, 0x01F00000u); // $sp
            ctx.SetU32(4, matAddr);      // $a0 = outMat
            ctx.F[12] = 45.0f * (448.0f / 640.0f);
            ctx.F[13] = returnedAspect;
            ctx.F[14] = 1.0f;
            ctx.F[15] = 200.0f;
            ctx.SetU32(31, 0x00100100u);
            RecompiledFunctions.sub_003DBFD0(&ctx, ram);

            float m00 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(&ctx, ram, matAddr + 0u));
            float m11 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(&ctx, ram, matAddr + 20u));
            Assert.True(m00 > 0f && m11 > 0f);

            // In OpenGL, NDC [-1,1] stretches to vpWidth x vpHeight, so square pixels require m00 * vpWidth == m11 * vpHeight
            float pixelScaleRatio = (m00 * vpWidth) / (m11 * vpHeight);
            Assert.InRange(pixelScaleRatio, 0.999f, 1.001f);
        }
        finally
        {
            RuntimeDispatcher.ClearHooks();
            NativeMemory.Free(ram);
            NativeMemory.Free(scratchpad);
            NativeMemory.Free(vu0Data);
        }
    }

    [Fact]
    public void SoftwareModeWithoutWidescreen_PreservesNativePs2FourThreeAspect()
    {
        RecompiledFunctions.RegisterAll();
        RuntimeDispatcher.ClearHooks();
        RuntimeDispatcher.SetTraceHandler(null);

        byte* ram = (byte*)NativeMemory.AllocZeroed(CpuContext.RamSize);
        byte* scratchpad = (byte*)NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
        byte* vu0Data = (byte*)NativeMemory.AllocZeroed(4096);

        try
        {
            string elfPath = Ps2ElfReader.FindDefaultElfPath();
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            CpuContext ctx = default;
            ctx.Initialize(scratchpad, vu0Data);

            using var runtime = new CastawayRuntime(initOpenAl: false);
            using var graphics = new GsGraphicsPipeline();
            var overrides = new CastawayOverrides(new OverrideOptions
            {
                RendererMode = GsRendererMode.Software,
                EnableWidescreen = false
            });

            overrides.SetViewportAspectRatio(1920, 1080);
            overrides.Install(runtime, graphics);

            ctx.SetU32(31, 0x00100100u);
            RecompiledFunctions.sub_00433B00(&ctx, ram);
            Assert.InRange(ctx.F[0], (4f / 3f) - 1e-5f, (4f / 3f) + 1e-5f);
        }
        finally
        {
            RuntimeDispatcher.ClearHooks();
            NativeMemory.Free(ram);
            NativeMemory.Free(scratchpad);
            NativeMemory.Free(vu0Data);
        }
    }

    [Fact]
    public void OpenGlMsaa_CompilesShaders_ResolvesAntialiasedEdges_AndHandlesDatePass()
    {
        var winOpts = GsOpenGlRenderer.CreateOpenGl46WindowOptions("Castaway MSAA Test", 128, 128);
        winOpts.IsVisible = false;

        using var window = Window.Create(winOpts);
        GL? gl = null;
        window.Load += () => gl = window.CreateOpenGL();
        window.Initialize();

        try
        {
            Assert.NotNull(gl);
            using var renderer = new GsOpenGlRenderer();
            renderer.Initialize(gl!, baseWidth: 640, baseHeight: 448, scaleMultiplier: 1, msaaSamples: 4);
            Assert.Equal(GLEnum.NoError, gl!.GetError());

            var gs = new GsProcessor { Mode = GsRendererMode.OpenGl };
            var batch = new GsPrimitiveBatch();

            var dc = new GsDrawContext
            {
                Zte = false,
                Abe = false,
                Tme = false,
                Fbmsk = 0u
            };

            // Diagonal triangle across the center of the viewport so MSAA edge samples trigger
            batch.AddTriangle(
                in dc,
                0, 0, 639, 447,
                0, 0,
                new GsGlVertex { X = 100f, Y = 50f, Z = 0.5f, W = 1f, R = 1f, G = 1f, B = 1f, A = 1f },
                new GsGlVertex { X = 540f, Y = 120f, Z = 0.5f, W = 1f, R = 1f, G = 1f, B = 1f, A = 1f },
                new GsGlVertex { X = 180f, Y = 400f, Z = 0.5f, W = 1f, R = 1f, G = 1f, B = 1f, A = 1f });

            // Include a DATE draw call to verify the sampler2DMS DATE shader path
            var dateDc = dc;
            dateDc.Date = true;
            dateDc.Datm = 0;
            batch.AddTriangle(
                in dateDc,
                0, 0, 639, 447,
                0, 0,
                new GsGlVertex { X = 200f, Y = 100f, Z = 0.5f, W = 1f, R = 1f, G = 0f, B = 0f, A = 1f },
                new GsGlVertex { X = 420f, Y = 140f, Z = 0.5f, W = 1f, R = 1f, G = 0f, B = 0f, A = 1f },
                new GsGlVertex { X = 240f, Y = 320f, Z = 0.5f, W = 1f, R = 1f, G = 0f, B = 0f, A = 1f });

            renderer.RenderBatch(batch, gs, 128, 128);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            byte[]? msaaPixels = renderer.CaptureBackbufferRgba(128, 128);
            Assert.NotNull(msaaPixels);

            if (renderer.MaxSupportedMsaaSamples >= 2)
            {
                Assert.True(renderer.ActiveMsaaSamples >= 2);
                int intermediatePixels = CountIntermediateRedPixels(msaaPixels!);
                Assert.True(intermediatePixels > 0, $"Expected antialiased edge pixels with {renderer.ActiveMsaaSamples}x MSAA, got 0.");
            }

            // Toggle MSAA off at runtime and verify zero GL errors and single-sample crisp edges
            renderer.SetMsaaSamples(0);
            renderer.RenderBatch(batch, gs, 128, 128, useDisplayTextureBackdrop: true);
            Assert.Equal(GLEnum.NoError, gl.GetError());
            Assert.Equal(1, renderer.ActiveMsaaSamples);
        }
        finally
        {
            gl?.Dispose();
            window.Reset();
        }
    }

    [Fact]
    public void OpenGlBatch_PreservesPreviousFrameColor_DuringPartialDialogOverlay()
    {
        var winOpts = GsOpenGlRenderer.CreateOpenGl46WindowOptions("Castaway VRAM Persistence Test", 128, 128);
        winOpts.IsVisible = false;

        using var window = Window.Create(winOpts);
        GL? gl = null;
        window.Load += () => gl = window.CreateOpenGL();
        window.Initialize();

        try
        {
            Assert.NotNull(gl);
            using var renderer = new GsOpenGlRenderer();
            renderer.Initialize(gl!, baseWidth: 640, baseHeight: 448, scaleMultiplier: 1, msaaSamples: 4);

            var gs = new GsProcessor { Mode = GsRendererMode.OpenGl };

            // Frame 1: Fullscreen green scene (normal frame)
            var frame1 = new GsPrimitiveBatch();
            var opaqueDc = new GsDrawContext { Zte = false, Abe = false, Tme = false, Fbmsk = 0u };
            AddFullscreenQuad(frame1, in opaqueDc, r: 0f, g: 1f, b: 0f, a: 1f);
            renderer.RenderBatch(frame1, gs, 128, 128);
            Assert.Equal(GLEnum.NoError, gl!.GetError());

            // Frame 2: Saving dialog frame (Call #0 masks RGB via Fbmsk=0x7FFFFFFF; Call #1 draws centered dialog box)
            var frame2 = new GsPrimitiveBatch();
            var maskRgbDc = new GsDrawContext
            {
                Zte = false,
                Abe = true,
                SpecA = 0,
                SpecB = 1,
                SpecC = 0,
                SpecD = 1,
                Tme = false,
                Fbmsk = 0x7FFFFFFFu
            };
            AddFullscreenQuad(frame2, in maskRgbDc, r: 0f, g: 0f, b: 0f, a: 0f);
            AddCenteredQuad(frame2, in opaqueDc, x0: 200f, y0: 140f, x1: 440f, y1: 308f, r: 1f, g: 0f, b: 0f, a: 1f);

            renderer.RenderBatch(frame2, gs, 128, 128);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            byte[]? pixels = renderer.CaptureBackbufferRgba(128, 128);
            Assert.NotNull(pixels);

            // Outer pixel (4, 4) must still have Frame 1's green color (not cleared to black)
            int outerIdx = (4 * 128 + 4) * 4;
            Assert.True(pixels![outerIdx + 1] > 100, $"Expected persistent green backdrop at (4,4), got RGBA=({pixels[outerIdx]},{pixels[outerIdx + 1]},{pixels[outerIdx + 2]},{pixels[outerIdx + 3]})");

            // Center pixel (64, 64) must have Frame 2's centered dialog red color
            int centerIdx = (64 * 128 + 64) * 4;
            Assert.True(pixels[centerIdx] > 100, $"Expected centered overlay red at (64,64), got RGBA=({pixels[centerIdx]},{pixels[centerIdx + 1]},{pixels[centerIdx + 2]},{pixels[centerIdx + 3]})");

            // Verify persistence also holds with MSAA disabled
            renderer.SetMsaaSamples(0);
            renderer.RenderBatch(frame1, gs, 128, 128);
            renderer.RenderBatch(frame2, gs, 128, 128);
            Assert.Equal(GLEnum.NoError, gl.GetError());

            byte[]? singleSamplePixels = renderer.CaptureBackbufferRgba(128, 128);
            Assert.NotNull(singleSamplePixels);
            Assert.True(singleSamplePixels![outerIdx + 1] > 100);
            Assert.True(singleSamplePixels[centerIdx] > 100);
        }
        finally
        {
            gl?.Dispose();
            window.Reset();
        }
    }

    private static void AddFullscreenQuad(GsPrimitiveBatch batch, in GsDrawContext dc, float r, float g, float b, float a)
    {
        AddCenteredQuad(batch, in dc, 0f, 0f, 640f, 448f, r, g, b, a);
    }

    private static void AddCenteredQuad(GsPrimitiveBatch batch, in GsDrawContext dc, float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        var v00 = new GsGlVertex { X = x0, Y = y0, Z = 0.5f, W = 1f, R = r, G = g, B = b, A = a };
        var v10 = new GsGlVertex { X = x1, Y = y0, Z = 0.5f, W = 1f, R = r, G = g, B = b, A = a };
        var v01 = new GsGlVertex { X = x0, Y = y1, Z = 0.5f, W = 1f, R = r, G = g, B = b, A = a };
        var v11 = new GsGlVertex { X = x1, Y = y1, Z = 0.5f, W = 1f, R = r, G = g, B = b, A = a };
        batch.AddTriangle(in dc, 0, 0, 639, 447, 0, 0, in v00, in v10, in v01);
        batch.AddTriangle(in dc, 0, 0, 639, 447, 0, 0, in v10, in v11, in v01);
    }

    private static int CountIntermediateRedPixels(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = rgba[i];
            if (r > 4 && r < 120)
                count++;
        }
        return count;
    }
}
