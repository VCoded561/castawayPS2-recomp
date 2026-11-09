using System.Runtime.InteropServices;
using Castaway.Core;
using Castaway.ElfTools.Commands;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Castaway.ElfTools.Tests;

public unsafe class Phase5BootAndTraceTests
{
    private readonly ITestOutputHelper _output;

    public Phase5BootAndTraceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Slus21664_FullUnmockedBoot_ReachesApplicationThreadAndFrameLoop()
    {
        RecompiledFunctions.RegisterAll();
        RuntimeDispatcher.ClearHooks();
        RuntimeDispatcher.SetTraceHandler(null);

        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        byte* ram = (byte*)NativeMemory.AllocZeroed(CpuContext.RamSize);
        byte* scratchpad = (byte*)NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
        byte* vu0Data = (byte*)NativeMemory.AllocZeroed(4096);
        bool bootTaskCompleted = true;

        try
        {
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);

            CpuContext ctx = default;
            ctx.Initialize(scratchpad, vu0Data);

            using var runtime = new CastawayRuntime(initOpenAl: false);
            runtime.Install(&ctx, ram, installHleHooks: true);

            using var gsPipeline = new GsGraphicsPipeline();
            gsPipeline.AttachToRuntime(runtime);

            int xgkickIdx = 0;
            gsPipeline.Vu1.OnXgKick = data =>
            {
                xgkickIdx++;
                gsPipeline.Gs.SubmitGifPacket(data);
            };

            bool reachedGlobalCtors = false;
            bool reachedGameMain = false;
            bool reachedAppThreadEntry = false;
            bool reachedPadUnpack = false;
            int frameLoopIterations = 0;
            uint lastPc = 0;

            RuntimeDispatcher.RegisterHook(0x00417158u, (c, r) =>
            {
                reachedPadUnpack = true;
                RecompiledFunctions.sub_00417158(c, r);
            });

            int totalCalls = 0;
            uint[] ring = new uint[32];
            int ringIdx = 0;

            uint midFramePad0 = 0;
            uint midFrame576 = 0;
            uint midFrame508 = 0;
            uint midFrame512 = 0;
            uint midFrameConnected = 0;

            // Hook Application::UpdateFrame (0x003E2940) to count frames and break after 3 frames
            RuntimeDispatcher.RegisterHook(0x003E2940u, (c, r) =>
            {
                frameLoopIterations++;
                runtime.PumpPadSubsystem(c, r);
                runtime.Kernel.StepFrameTimers(c, r);

                // Execute the actual frame update for the first 2 frames, then signal exit on frame 3
                if (frameLoopIterations <= 2)
                {
                    RecompiledFunctions.sub_003E2940(c, r);
                    runtime.Kernel.TriggerVBlank(c, r);

                    if (frameLoopIterations == 2)
                    {
                        midFramePad0 = Ps2Memory.Read32(c, r, 0x0050A758u);
                        if (midFramePad0 != 0)
                        {
                            midFrame576 = Ps2Memory.Read32(c, r, midFramePad0 + 576u);
                            midFrame508 = Ps2Memory.Read32(c, r, midFramePad0 + 508u);
                            midFrame512 = Ps2Memory.Read32(c, r, midFramePad0 + 512u);

                            CpuContext temp = *c;
                            c->SetU32(4, midFramePad0);
                            RuntimeDispatcher.Call(0x00459150u, c, r);
                            midFrameConnected = c->GetU32(2);
                            *c = temp;
                        }
                    }
                }
                else
                {
                    // Set Application + 36 (m_bQuit) = 1 so loop at 0x003E28F0 exits cleanly!
                    uint appPtr = c->GetU32(4);
                    Ps2Memory.Write32(c, r, appPtr + 36u, 1u);
                }
            });

            RuntimeDispatcher.SetTraceHandler((pc, evt, c) =>
            {
                if (evt == 0)
                {
                    lastPc = pc;
                    ring[ringIdx++ & 31] = pc;
                    if (pc == 0x0048B2A8u) reachedGlobalCtors = true;
                    if (pc == 0x003E4610u) reachedGameMain = true;
                    if (pc == 0x003E27F8u) reachedAppThreadEntry = true;
                    if (++totalCalls > 2_000_000)
                    {
                        var hist = string.Join(" -> ", Enumerable.Range(0, 16).Select(i => $"0x{ring[(ringIdx - 16 + i) & 31]:X8}"));
                        throw new TimeoutException($"Spin detected after {totalCalls} calls! Recent PCs: {hist}");
                    }
                }
            });

            var entryFn = RuntimeDispatcher.Lookup(0x00100008u);
            Assert.True(entryFn != null);
            CpuContext* ctxPtr = &ctx;
            bootTaskCompleted = false;
            var bootTask = Task.Run(() => entryFn(ctxPtr, ram));
            if (!bootTask.Wait(TimeSpan.FromMilliseconds(15000)))
            {
                var hist = string.Join(" -> ", Enumerable.Range(0, 16).Select(i => $"0x{ring[(ringIdx - 16 + i) & 31]:X8}"));
                throw new TimeoutException($"Intra-function spin at PC=0x{ctx.PC:X8} (lastPc=0x{lastPc:X8}, totalCalls={totalCalls})! Recent PCs: {hist}");
            }
            bootTaskCompleted = true;

            _output.WriteLine($"Last PC: 0x{lastPc:X8}, GlobalCtors={reachedGlobalCtors}, GameMain={reachedGameMain}, AppThreadEntry={reachedAppThreadEntry}, Frames={frameLoopIterations}, XGKicks={xgkickIdx}");
            _output.WriteLine($"GS Stats: Prims={gsPipeline.Gs.TotalPrimitivesDrawn}, Verts={gsPipeline.Gs.TotalVerticesKicked}, Tags={gsPipeline.Gs.TotalGifTagsProcessed}, VramTransfers={gsPipeline.Gs.TotalVramTransfers}");
            _output.WriteLine($"GS Ctxt1: Frame=0x{gsPipeline.Gs.Frame[0]:X16}, Scissor=0x{gsPipeline.Gs.Scissor[0]:X16}, XYOffset=0x{gsPipeline.Gs.Xyoffset[0]:X16}, Test=0x{gsPipeline.Gs.Test[0]:X16}");
            _output.WriteLine($"GS Ctxt2: Frame=0x{gsPipeline.Gs.Frame[1]:X16}, Scissor=0x{gsPipeline.Gs.Scissor[1]:X16}, XYOffset=0x{gsPipeline.Gs.Xyoffset[1]:X16}, Test=0x{gsPipeline.Gs.Test[1]:X16}");
            _output.WriteLine($"Threads created: {runtime.Kernel.Threads.Count}, Semas: {runtime.Kernel.Semaphores.Count}");
            bool padOpen = runtime.Pad.TryGetPadArea(0, 0, out uint pa);
            _output.WriteLine($"Pad 0 Area: open={padOpen}, addr=0x{pa:X8}");
            if (padOpen)
            {
                var s0 = new ReadOnlySpan<byte>(ram + (pa & 0x01FFFFFFu), 128);
                var s1 = new ReadOnlySpan<byte>(ram + (pa & 0x01FFFFFFu) + 128, 128);
                _output.WriteLine($"Slot 0: frame={System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s0.Slice(0x58, 4))}, state={s0[0x70]}, reqState={s0[0x71]}, ok={s0[0x72]}, len={System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(s0.Slice(0x60, 4))}");
                _output.WriteLine($"Slot 1: frame={System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s1.Slice(0x58, 4))}, state={s1[0x70]}, reqState={s1[0x71]}, ok={s1[0x72]}, len={System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(s1.Slice(0x60, 4))}");
                _output.WriteLine($"Slot 0 [0..32]: {Convert.ToHexString(s0.Slice(0, 32))}");
            }
            foreach (var (tid, th) in runtime.Kernel.Threads)
            {
                _output.WriteLine($"Thread {tid}: EntryPc=0x{th.EntryPc:X8}, Status={th.Status}, Prio={th.CurrentPriority}");
            }
            for (uint a = 0x00503430u; a <= 0x00503460u; a += 4)
            {
                _output.WriteLine($"0x{a:X8}: 0x{Ps2Memory.Read32(ctxPtr, ram, a):X8}");
            }
            _output.WriteLine($"RenderState: 0x00596D7C={Ps2Memory.Read32(ctxPtr, ram, 0x00596D7Cu):X8}, 0x00596DF8={Ps2Memory.Read32(ctxPtr, ram, 0x00596DF8u):X8}, 0x00596DFC={Ps2Memory.Read32(ctxPtr, ram, 0x00596DFCu):X8}, 0x00596E00={Ps2Memory.Read32(ctxPtr, ram, 0x00596E00u):X8}");

            Assert.True(reachedGlobalCtors, "Must execute __do_global_ctors (0x0048B2A8)");
            Assert.True(reachedGameMain, "Must execute GameMain_AppEntry (0x003E4610)");
            Assert.True(reachedAppThreadEntry, "Must enter Application::ThreadEntry (0x003E27F8)");
            Assert.True(frameLoopIterations >= 1, $"Expected >= 1 frame loop iterations at 0x003E2940, got {frameLoopIterations} (lastPc=0x{lastPc:X8})");

            _output.WriteLine($"MidFrame 2 Pad State: Pad0=0x{midFramePad0:X8}, 576={midFrame576}, 508=0x{midFrame508:X8}, 512=0x{midFrame512:X8}, Connected={midFrameConnected}");
            Assert.True(midFramePad0 != 0, "PadDevice 0 must be allocated at 0x0050A758 during frame update");
            Assert.Equal(1u, midFrame576);
            Assert.True(midFrame508 != 0, "PadDevice 508 staging state must be allocated");
            Assert.True(midFrame512 != 0, "PadDevice 512 published state must be allocated");
            Assert.True(midFrameConnected <= 1u);
        }
        finally
        {
            RuntimeDispatcher.SetTraceHandler(null);
            RuntimeDispatcher.ClearHooks();
            if (bootTaskCompleted)
            {
                NativeMemory.Free(vu0Data);
                NativeMemory.Free(scratchpad);
                NativeMemory.Free(ram);
            }
        }
    }

    [Fact]
    public void Diagnostic_InspectVramBitmaps()
    {
        string rootDir = Directory.GetCurrentDirectory();
        foreach (int frame in new[] { 5, 15, 60 })
        {
            string vramPath = Path.Combine(rootDir, $"vram_linear_frame{frame}.bmp");
            string dispfbPath = Path.Combine(rootDir, $"fb_dispfb1_frame{frame}.bmp");
            string ctxt1Path = Path.Combine(rootDir, $"fb_ctxt1_frame{frame}.bmp");
            string ctxt2Path = Path.Combine(rootDir, $"fb_ctxt2_frame{frame}.bmp");

            if (File.Exists(vramPath))
            {
                byte[] vramBmp = File.ReadAllBytes(vramPath);
                int pixelOffset = 54;
                int totalPixels = (vramBmp.Length - pixelOffset) / 4;
                int nonBlackFb = 0;
                int nonBlackTex = 0;
                int totalFbPixels = 0;
                int totalTexPixels = 0;

                int fbPixelCutoff = 160 * 2048;

                for (int i = 0; i < totalPixels; i++)
                {
                    int off = pixelOffset + i * 4;
                    byte b = vramBmp[off + 0];
                    byte g = vramBmp[off + 1];
                    byte r = vramBmp[off + 2];
                    bool isNonBlack = (r > 0 || g > 0 || b > 0);

                    if (i < fbPixelCutoff)
                    {
                        totalFbPixels++;
                        if (isNonBlack) nonBlackFb++;
                    }
                    else
                    {
                        totalTexPixels++;
                        if (isNonBlack) nonBlackTex++;
                    }
                }

                Console.WriteLine($"[vram_linear_frame{frame}.bmp] Total Pixels: {totalPixels}");
                Console.WriteLine($"  FB Pages 0x00..0x9F (0..{fbPixelCutoff-1}): {nonBlackFb} / {totalFbPixels} non-black pixels ({(float)nonBlackFb / totalFbPixels * 100:F2}%)");
                Console.WriteLine($"  Tex Pages 0xA0+ ({fbPixelCutoff}..{totalPixels-1}): {nonBlackTex} / {totalTexPixels} non-black pixels ({(float)nonBlackTex / totalTexPixels * 100:F2}%)");
            }

            foreach (var (p, name) in new[] { (dispfbPath, $"fb_dispfb1_frame{frame}.bmp"), (ctxt1Path, $"fb_ctxt1_frame{frame}.bmp"), (ctxt2Path, $"fb_ctxt2_frame{frame}.bmp") })
            {
                if (File.Exists(p))
                {
                    byte[] dispBmp = File.ReadAllBytes(p);
                    int pixelOffset = 54;
                    int totalPixels = (dispBmp.Length - pixelOffset) / 4;
                    int nonBlack = 0;

                    for (int i = 0; i < totalPixels; i++)
                    {
                        int off = pixelOffset + i * 4;
                        byte b = dispBmp[off + 0];
                        byte g = dispBmp[off + 1];
                        byte r = dispBmp[off + 2];
                        if (r > 0 || g > 0 || b > 0)
                            nonBlack++;
                    }

                    Console.WriteLine($"[{name}] Total Pixels: {totalPixels}, Non-Black: {nonBlack} ({(float)nonBlack / totalPixels * 100:F2}%)");
                }
            }
        }
    }
}
