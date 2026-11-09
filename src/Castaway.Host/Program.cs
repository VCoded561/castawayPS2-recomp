using System.Diagnostics;
using System.Runtime.InteropServices;
using Castaway.Core;
using Castaway.ElfTools.Commands;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Vu1;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.OpenGl;
using Castaway.Overrides;
using Castaway.Runtime;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Castaway.Host;

public static unsafe class Program
{
    public static int Main(string[] args)
    {
        string? elfPath = null;
        string? dvdPath = null;
        string? traceBinaryPath = null;
        int maxTraceRecords = 500_000;
        int maxFrames = 0; // 0 = unlimited
        int autoStartFrame = 0;
        bool headless = false;
        bool dumpVram = false;
        bool fpsExplicitlySet = false;
        var overrideOpts = new OverrideOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--elf" when i + 1 < args.Length:
                    elfPath = args[++i];
                    break;
                case "--dvd" when i + 1 < args.Length:
                    dvdPath = args[++i];
                    break;
                case "--trace-binary" when i + 1 < args.Length:
                    traceBinaryPath = args[++i];
                    break;
                case "--trace-max-records" when i + 1 < args.Length && int.TryParse(args[++i], out int mtr):
                    maxTraceRecords = mtr;
                    break;
                case "--frames" or "--max-frames" or "--headless-frames" when i + 1 < args.Length && int.TryParse(args[++i], out int mf):
                    maxFrames = mf;
                    if (args[i - 1] == "--headless-frames")
                        headless = true;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--auto-start-frame" when i + 1 < args.Length && int.TryParse(args[++i], out int asf):
                    autoStartFrame = asf;
                    break;
                case "--dump-vram":
                    dumpVram = true;
                    break;
                case "--scale" when i + 1 < args.Length && int.TryParse(args[++i], out int sc):
                    overrideOpts.ResolutionScale = Math.Clamp(sc, 1, 8);
                    break;
                case "--widescreen":
                    overrideOpts.EnableWidescreen = true;
                    break;
                case "--aspect" when i + 1 < args.Length && float.TryParse(args[++i], out float ar):
                    overrideOpts.EnableWidescreen = true;
                    overrideOpts.TargetAspectRatio = ar;
                    break;
                case "--renderer" when i + 1 < args.Length:
                    string rMode = args[++i].Trim().ToLowerInvariant();
                    overrideOpts.RendererMode = rMode == "software" ? GsRendererMode.Software : GsRendererMode.OpenGl;
                    break;
                case "--fps" when i + 1 < args.Length:
                    fpsExplicitlySet = true;
                    string fpsStr = args[++i].Trim().ToLowerInvariant();
                    if (fpsStr == "uncapped" || fpsStr == "0")
                        overrideOpts.TargetFps = 0;
                    else if (int.TryParse(fpsStr, out int targetFps))
                        overrideOpts.TargetFps = Math.Max(0, targetFps);
                    break;
            }
        }

        if (headless && !fpsExplicitlySet)
        {
            overrideOpts.TargetFps = 0;
            overrideOpts.RendererMode = GsRendererMode.Software;
        }

        elfPath ??= Ps2ElfReader.FindDefaultElfPath();
        if (!File.Exists(elfPath))
        {
            Console.Error.WriteLine($"[Castaway.Host] ELF not found: {elfPath}");
            return 1;
        }

        dvdPath ??= Path.GetDirectoryName(elfPath);

        Console.WriteLine($"[Castaway.Host] ELF: {elfPath}");
        Console.WriteLine($"[Castaway.Host] DVD Root: {dvdPath}");
        Console.WriteLine($"[Castaway.Host] Mode: {(headless ? "Headless" : $"OpenGL 4.6 Windowed ({overrideOpts.ResolutionScale}x scale)")} | Renderer: {overrideOpts.RendererMode} | Target FPS: {(overrideOpts.TargetFps == 0 ? "Uncapped" : $"{overrideOpts.TargetFps} FPS (PS2 Locked)")}");

        if (headless)
        {
            return RunExecution(elfPath, dvdPath, traceBinaryPath, maxTraceRecords, maxFrames, autoStartFrame, overrideOpts, dumpVram, window: null, gl: null, input: null);
        }

        int winW = 640 * Math.Clamp(overrideOpts.ResolutionScale, 1, 4);
        int winH = 448 * Math.Clamp(overrideOpts.ResolutionScale, 1, 4);
        var winOpts = GsOpenGlRenderer.CreateOpenGl46WindowOptions(
            "The Sims 2: Castaway — PS2 Static Recompilation (SLUS_216.64)",
            winW,
            winH);
        winOpts.VSync = true;

        using var window = Window.Create(winOpts);
        GL? gl = null;
        IInputContext? input = null;
        int exitCode = 0;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            input = window.CreateInput();
            Console.WriteLine($"[Castaway.Host] OpenGL Context: {gl.GetStringS(StringName.Version)} | {gl.GetStringS(StringName.Renderer)}");
        };

        window.Initialize();

        try
        {
            exitCode = RunExecution(elfPath, dvdPath, traceBinaryPath, maxTraceRecords, maxFrames, autoStartFrame, overrideOpts, dumpVram, window, gl, input);
        }
        finally
        {
            input?.Dispose();
            gl?.Dispose();
            window.Reset();
        }

        return exitCode;
    }

    private static int RunExecution(
        string elfPath,
        string? dvdPath,
        string? traceBinaryPath,
        int maxTraceRecords,
        int maxFrames,
        int autoStartFrame,
        OverrideOptions overrideOpts,
        bool dumpVram,
        IWindow? window,
        GL? gl,
        IInputContext? input)
    {
        RecompiledFunctions.RegisterAll();
        RuntimeDispatcher.ClearHooks();
        RuntimeDispatcher.SetTraceHandler(null);

        byte* ram = (byte*)NativeMemory.AllocZeroed(CpuContext.RamSize);
        byte* scratchpad = (byte*)NativeMemory.AllocZeroed(CpuContext.ScratchpadSize);
        byte* vu0Data = (byte*)NativeMemory.AllocZeroed(4096);
        CpuContext ctx = default;
        ctx.Initialize(scratchpad, vu0Data);

        using var runtime = new CastawayRuntime(dvdRootPath: dvdPath, initOpenAl: window != null);
        using var graphics = new GsGraphicsPipeline();
        var overrides = new CastawayOverrides(overrideOpts);

        BinaryTraceRecorder? traceRecorder = null;
        if (!string.IsNullOrEmpty(traceBinaryPath))
        {
            traceRecorder = new BinaryTraceRecorder(traceBinaryPath, maxTraceRecords);
            Console.WriteLine($"[Castaway.Host] Recording binary trace to: {traceBinaryPath} (max {maxTraceRecords} records)");
        }

        try
        {
            CastawayRuntime.LoadElfSegmentsToRam(elfPath, ram);
            runtime.Install(&ctx, ram, installHleHooks: true);
            graphics.AttachToRuntime(runtime);

            // Default GS framebuffer configuration (640x448 PSMCT32)
            graphics.Gs.WriteInternalRegister(0x4C, 10ul << 16);
            graphics.Gs.WriteInternalRegister(0x18, 0ul);
            graphics.Gs.WriteInternalRegister(0x40, (639ul << 16) | (447ul << 48));

            if (gl != null)
            {
                graphics.OpenGl.Initialize(gl, 640, 448, overrideOpts.ResolutionScale);
            }

            if (input != null)
            {
                overrides.AttachInputEvents(input);
            }

            overrides.Install(runtime, graphics);

            if (traceRecorder != null)
            {
                RuntimeDispatcher.SetTraceHandler(traceRecorder.OnTraceEvent);
            }

            int frameCount = 0;
            long lastFrameTick = 0;
            var sw = Stopwatch.StartNew();

            int xgkickCount = 0;
            graphics.Vu1.OnXgKick = data =>
            {
                xgkickCount++;
                graphics.Gs.SubmitGifPacket(data, "VU1_XGKICK");
            };

            int presentationCount = 0;
            bool inApplicationUpdateFrame = false;
            int lastPresentedCount = -1;
            long lastMainThreadPresentMs = -1000;
            bool userRequestedExit = false;
            long exitRequestedAtMs = 0;

            if (window != null)
            {
                runtime.Kernel.OnMainThreadIdleTick = () =>
                {
                    window.DoEvents();
                    if (!userRequestedExit &&
                        (window.IsClosing ||
                         (input != null && input.Keyboards.Count > 0 && input.Keyboards[0].IsKeyPressed(Key.Escape))))
                    {
                        userRequestedExit = true;
                        exitRequestedAtMs = sw.ElapsedMilliseconds;
                        Console.WriteLine("\n================================================================================");
                        Console.WriteLine(">>> [SHUTDOWN SEQUENCE INITIATED] (User requested exit: Window closed or ESC pressed)");
                        Console.WriteLine(">>> Note: Any logs below this line are SLUS_216.64 TEARDOWN & CLEANUP, NOT gameplay!");
                        Console.WriteLine("================================================================================\n");
                    }
                    else if (userRequestedExit && sw.ElapsedMilliseconds - exitRequestedAtMs >= 250)
                    {
                        runtime.Kernel.ShutdownThreads();
                        return;
                    }

                    overrides.UpdateInput(runtime.Pad, input, ram);

                    int curPresent = Volatile.Read(ref presentationCount);
                    long nowMs = sw.ElapsedMilliseconds;
                    bool shouldPresent;
                    if (graphics.Mode == GsRendererMode.OpenGl)
                    {
                        // In OpenGL mode, presentation is strictly synchronized to completed batch swaps,
                        // or initial boot splash screens before batches begin (FrontBatch.VertexCount == 0 && VramDirty).
                        shouldPresent = curPresent != lastPresentedCount || (graphics.FrontBatch.VertexCount == 0 && graphics.Gs.VramDirty && nowMs - lastMainThreadPresentMs >= 33);
                    }
                    else
                    {
                        shouldPresent = curPresent != lastPresentedCount || (graphics.Gs.VramDirty && nowMs - lastMainThreadPresentMs >= 33);
                    }

                    if (gl != null && shouldPresent)
                    {
                        lastPresentedCount = curPresent;
                        lastMainThreadPresentMs = nowMs;

                        byte[]? renderVram = curPresent > 0 ? graphics.FrontVram : null;

                        // 1. Always present the 100% accurate PS2 VRAM display buffer (BitBlts, FMVs, 2D backgrounds, Flash UI)
                        graphics.OpenGl.RenderFrame(graphics.Gs, window.FramebufferSize.X, window.FramebufferSize.Y, runtime.Mmio.GsDispfb1, renderVram);

                        // 2. In OpenGl mode, overlay the GPU-accelerated 3D primitive batch on top
                        if (graphics.Mode == GsRendererMode.OpenGl)
                        {
                            lock (graphics.BatchLock)
                            {
                                if (graphics.FrontBatch.VertexCount > 0)
                                {
                                    graphics.OpenGl.RenderBatch(graphics.FrontBatch, graphics.Gs, window.FramebufferSize.X, window.FramebufferSize.Y, renderVram);
                                }
                            }
                        }
                        window.SwapBuffers();
                    }
                };
            }

            // Hook Display Buffer Flip / VBlank Present (0x00428AE8) to signal main thread presentation
            // on every frame flip (both during boot splash animations and in-game frame loop).
            RuntimeDispatcher.RegisterHook(0x00428AE8u, (c, r) =>
            {
                RecompiledFunctions.sub_00428AE8(c, r);

                // Do not swap batches mid-frame while Application::UpdateFrame is active!
                // The complete frame batch will be swapped atomically when sub_003E2940 finishes.
                if (inApplicationUpdateFrame)
                    return;

                if (graphics.Mode == GsRendererMode.OpenGl)
                {
                    // Only swap and present when geometry was actually drawn in BackBatch!
                    // Prevents RwCameraClear / WaitSema from swapping an empty (0 vertex) batch and flashing a black screen.
                    if (graphics.BackBatch.VertexCount > 0)
                    {
                        graphics.SwapBatches();
                        int flipNum = Interlocked.Increment(ref presentationCount);

                        if (dumpVram && (flipNum == 1 || flipNum == 10 || flipNum == 20))
                        {
                            ulong dispfb1 = runtime.Mmio.GsDispfb1;
                            int fbpDisp = (int)(dispfb1 & 0x1FFul) * 32;
                            int fbwDisp = Math.Max(1, (int)((dispfb1 >> 9) & 0x3Ful)) * 64;
                            var psmDisp = (GsPsm)((dispfb1 >> 15) & 0x1Ful);
                            VramDumper.DumpUnswizzledBmp($"fb_splash_flip{flipNum}.bmp", graphics.Gs.Vram, fbpDisp, fbwDisp, psmDisp);
                        }
                    }
                }
                else
                {
                    graphics.SwapBatches();
                    int flipNum = Interlocked.Increment(ref presentationCount);

                    if (dumpVram && (flipNum == 1 || flipNum == 10 || flipNum == 20))
                    {
                        ulong dispfb1 = runtime.Mmio.GsDispfb1;
                        int fbpDisp = (int)(dispfb1 & 0x1FFul) * 32;
                        int fbwDisp = Math.Max(1, (int)((dispfb1 >> 9) & 0x3Ful)) * 64;
                        var psmDisp = (GsPsm)((dispfb1 >> 15) & 0x1Ful);
                        VramDumper.DumpUnswizzledBmp($"fb_splash_flip{flipNum}.bmp", graphics.Gs.Vram, fbpDisp, fbwDisp, psmDisp);
                    }
                }
            });

            // Scene and Event diagnostics
            RuntimeDispatcher.RegisterHook(0x0034EBE0u, (c, r) =>
            {
                Console.WriteLine($"[HOOK 0x0034EBE0 - Splash Destructor] called! a0=0x{c->GetU32(4):X8} a1=0x{c->GetU32(5):X8} RA=0x{c->GetU32(31):X8}");
                RecompiledFunctions.sub_0034EBE0(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x003E4090u, (c, r) =>
            {
                Console.WriteLine($"[HOOK 0x003E4090 - Application::SetNextScene] app=0x{c->GetU32(4):X8} sceneId=0x{c->GetU32(5):X8} p1=0x{c->GetU32(6):X8} p2=0x{c->GetU32(7):X8} RA=0x{c->GetU32(31):X8}");
                RecompiledFunctions.sub_003E4090(c, r);
            });

            // Memory Card Subsystem diagnostics
            RuntimeDispatcher.RegisterHook(0x00435490u, (c, r) =>
            {
                uint thisPtr = c->GetU32(4);
                uint port = c->GetU32(5);
                uint outResPtr = c->GetU32(6);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x00435490 - McCheck] this=0x{thisPtr:X8} port={port} outResPtr=0x{outResPtr:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_00435490(c, r);
                uint ret = c->GetU32(2);
                uint val = outResPtr != 0 ? Ps2Memory.Read32(c, r, outResPtr) : 0;
                Console.WriteLine($"[HOOK 0x00435490 POST] ret={ret} *outResPtr={val}");
            });

            RuntimeDispatcher.RegisterHook(0x00476EC0u, (c, r) =>
            {
                uint port = c->GetU32(4);
                uint slot = c->GetU32(5);
                uint typePtr = c->GetU32(6);
                uint freePtr = c->GetU32(7);
                uint formatPtr = c->GetU32(8);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x00476EC0 - sceMcGetInfo] port={port} slot={slot} typePtr=0x{typePtr:X8} freePtr=0x{freePtr:X8} formatPtr=0x{formatPtr:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_00476EC0(c, r);
                uint ret = c->GetU32(2);
                Console.WriteLine($"[HOOK 0x00476EC0 POST] ret={ret}");
            });

            RuntimeDispatcher.RegisterHook(0x00477040u, (c, r) =>
            {
                uint port = c->GetU32(4);
                uint slot = c->GetU32(5);
                string pattern = DvdVirtualFileSystem.ReadGuestCString(r, c->GetU32(6));
                uint flags = c->GetU32(7);
                uint maxEnt = c->GetU32(8);
                uint tablePtr = c->GetU32(9);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x00477040 - sceMcGetDir] port={port} slot={slot} pattern='{pattern}' flags={flags} maxEnt={maxEnt} tablePtr=0x{tablePtr:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_00477040(c, r);
                uint ret = c->GetU32(2);
                Console.WriteLine($"[HOOK 0x00477040 POST] ret={ret}");
            });

            RuntimeDispatcher.RegisterHook(0x002737C8u, (c, r) =>
            {
                uint stage = c->GetU32(4);
                uint sceneId = c->GetU32(5);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002737C8 - Stage::ChangeScene frame={frameCount}] stage=0x{stage:X8} sceneId=0x{sceneId:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_002737C8(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x002933B8u, (c, r) =>
            {
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002933B8 - State 33 Trigger] called! RA=0x{ra:X8}");
                RecompiledFunctions.sub_002933B8(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x002A6230u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002A6230 - M2MTarget::ctor] this=0x{a0:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_002A6230(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x002C7AE0u, (c, r) =>
            {
                uint s1 = c->GetU32(17);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002C7AE0 - MemoryCardCheckState] s1=0x{s1:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_002C7AE0(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x00270F08u, (c, r) =>
            {
                uint stage = c->GetU32(4);
                uint ra = c->GetU32(31);
                uint p1318 = Ps2Memory.Read32(c, r, 0x00511318u);
                float timer = p1318 != 0 ? BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, p1318 + 52)) : -1f;
                Console.WriteLine($"[HOOK 0x00270F08 - SplashStage::Update] stage=0x{stage:X8} timer={timer:F2}s RA=0x{ra:X8}");
                RecompiledFunctions.sub_00270F08(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x00271608u, (c, r) =>
            {
                uint stage = c->GetU32(4);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x00271608 - SplashWrapper::Update] stage=0x{stage:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_00271608(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x0026F2B8u, (c, r) =>
            {
                uint s1 = Ps2Memory.Read32(c, r, 0x0050A6F8u);
                uint begin = s1 != 0 ? Ps2Memory.Read32(c, r, s1) : 0;
                uint end = s1 != 0 ? Ps2Memory.Read32(c, r, s1 + 4) : 0;
                int count = s1 != 0 && end >= begin ? (int)(end - begin) / 4 : -1;
                if (frameCount <= 5 || frameCount % 30 == 0)
                {
                    Console.WriteLine($"[HOOK 0x0026F2B8 - StageList::Update frame={frameCount}] count={count}");
                    if (count > 0 && count < 20)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            uint stagePtr = Ps2Memory.Read32(c, r, begin + (uint)(i * 4));
                            uint vtable = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 24) : 0;
                            uint updateFn = vtable != 0 ? Ps2Memory.Read32(c, r, vtable + 44) : 0;
                            uint flag116 = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 116) : 0;
                            uint flag36 = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 36) : 0;
                            uint actState = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 60) : 0;
                            uint actStateId = actState != 0 ? Ps2Memory.Read32(c, r, actState) : 0xFFFFFFFFu;
                            uint actVtable = actState != 0 ? Ps2Memory.Read32(c, r, actState + 24) : 0;
                            uint actMethod36 = actVtable != 0 ? Ps2Memory.Read32(c, r, actVtable + 36) : 0;
                            uint pend100 = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 100) : 0;
                            uint pend104 = stagePtr != 0 ? Ps2Memory.Read32(c, r, stagePtr + 104) : 0;
                            Console.WriteLine($"  Stage[{i}] = 0x{stagePtr:X8} vtbl=0x{vtable:X8} upd=0x{updateFn:X8} f116={flag116} f36={flag36} act=0x{actState:X8}(id={actStateId},m36=0x{actMethod36:X8}) pend=({pend100},{pend104})");
                        }
                    }
                }
                RecompiledFunctions.sub_0026F2B8(c, r);
                if (frameCount <= 5 || frameCount % 30 == 0)
                {
                    Console.WriteLine($"[HOOK 0x0026F2B8 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00349A68u, (c, r) =>
            {
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x00349A68 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_00349A68(c, r);
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x00349A68 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x002FE518u, (c, r) =>
            {
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x002FE518 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_002FE518(c, r);
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x002FE518 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x002FE368u, (c, r) =>
            {
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x002FE368 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_002FE368(c, r);
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x002FE368 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x002FE6E0u, (c, r) =>
            {
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x002FE6E0 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_002FE6E0(c, r);
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x002FE6E0 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x002FE5C8u, (c, r) =>
            {
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x002FE5C8 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_002FE5C8(c, r);
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x002FE5C8 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x0026F330u, (c, r) =>
            {
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x0026F330 - StageList::PreRender ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_0026F330(c, r);
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x0026F330 - StageList::PreRender EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x00306820u, (c, r) =>
            {
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x00306820 - Apt::Render ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_00306820(c, r);
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x00306820 - Apt::Render EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x00362F58u, (c, r) =>
            {
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x00362F58 ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_00362F58(c, r);
                if (frameCount <= 8) Console.WriteLine($"[HOOK 0x00362F58 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x0038A860u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_0038A860(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005)
                {
                    Console.WriteLine($"[SLOW 0x0038A860 - AptCIH::render frame={frameCount}] a0=0x{a0:X8} dt={dt:F3}s (t={t0:F3}s..{sw.Elapsed.TotalSeconds:F3}s)");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00367848u, (c, r) =>
            {
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_00367848(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005 || frameCount == 5)
                {
                    Console.WriteLine($"[HOOK 0x00367848 frame={frameCount}] dt={dt:F3}s (t={t0:F3}s..{sw.Elapsed.TotalSeconds:F3}s) v0=0x{c->GetU32(2):X8}");
                }
            });

            RuntimeDispatcher.RegisterHook(0x003ABA60u, (c, r) =>
            {
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_003ABA60(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005 || frameCount == 5)
                {
                    Console.WriteLine($"[HOOK 0x003ABA60 frame={frameCount}] dt={dt:F3}s (t={t0:F3}s..{sw.Elapsed.TotalSeconds:F3}s)");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00381968u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a3 = c->GetU32(7);
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_00381968(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005)
                {
                    Console.WriteLine($"[SLOW 0x00381968 - AptShape::Render frame={frameCount}] a0=0x{a0:X8} a3=0x{a3:X8} dt={dt:F3}s (t={t0:F3}s..{sw.Elapsed.TotalSeconds:F3}s)");
                }
            });

            RuntimeDispatcher.RegisterHook(0x003A3D68u, (c, r) =>
            {
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_003A3D68(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005)
                {
                    uint fn9898 = Ps2Memory.Read32(c, r, 0x00529898u);
                    Console.WriteLine($"[SLOW 0x003A3D68 frame={frameCount}] fn9898=0x{fn9898:X8} dt={dt:F3}s");
                }
            });

            RuntimeDispatcher.RegisterHook(0x003442A8u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                int count = (int)Ps2Memory.Read32(c, r, a0 + 4u);
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_003442A8(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005)
                {
                    Console.WriteLine($"[SLOW 0x003442A8 frame={frameCount}] a0=0x{a0:X8} cmdCount={count} dt={dt:F3}s");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00302478u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                int triCount = (int)Ps2Memory.Read32(c, r, a0 + 4u);
                uint rend = Ps2Memory.Read32(c, r, 0x00525C48u);
                uint vt = rend != 0 ? Ps2Memory.Read32(c, r, rend + 96u) : 0;
                uint fn28 = vt != 0 ? Ps2Memory.Read32(c, r, vt + 28u) : 0;
                uint fn244 = vt != 0 ? Ps2Memory.Read32(c, r, vt + 244u) : 0;
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_00302478(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005 || frameCount == 5)
                {
                    Console.WriteLine($"[HOOK 0x00302478 - DrawSolidShape frame={frameCount}] a0=0x{a0:X8} a1={a1} tris={triCount} rend=0x{rend:X8} fn28=0x{fn28:X8} fn244=0x{fn244:X8} dt={dt:F3}s");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00302838u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                int triCount = (int)Ps2Memory.Read32(c, r, a0 + 4u);
                int texIdx = (int)Ps2Memory.Read32(c, r, a0 + 84u);
                uint texPtr = (texIdx >= 0 && texIdx < 4096) ? Ps2Memory.Read32(c, r, 0x005261D0u + (uint)(texIdx * 4)) : 0;
                uint texVt = texPtr != 0 ? Ps2Memory.Read32(c, r, texPtr + 76u) : 0;
                uint texFn20 = texVt != 0 ? Ps2Memory.Read32(c, r, texVt + 20u) : 0;
                double t0 = sw.Elapsed.TotalSeconds;
                RecompiledFunctions.sub_00302838(c, r);
                double dt = sw.Elapsed.TotalSeconds - t0;
                if (dt >= 0.005 || frameCount == 5)
                {
                    Console.WriteLine($"[HOOK 0x00302838 - DrawTexShape frame={frameCount}] a0=0x{a0:X8} a1={a1} tris={triCount} texIdx={texIdx} texPtr=0x{texPtr:X8} texFn20=0x{texFn20:X8} dt={dt:F3}s");
                }
            });

            RuntimeDispatcher.RegisterHook(0x0026F3A8u, (c, r) =>
            {
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x0026F3A8 - StageList::Render ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_0026F3A8(c, r);
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x0026F3A8 - StageList::Render EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x00349B98u, (c, r) =>
            {
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x00349B98 - AptUI::Render ENTER frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                RecompiledFunctions.sub_00349B98(c, r);
                if (frameCount <= 5) Console.WriteLine($"[HOOK 0x00349B98 - AptUI::Render EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
            });

            RuntimeDispatcher.RegisterHook(0x00427E40u, (c, r) =>
            {
                if (frameCount <= 5)
                {
                    Console.WriteLine($"[HOOK 0x00427E40 - DmaSubmit frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s] RA=0x{c->GetU32(31):X8}");
                }
                RecompiledFunctions.sub_00427E40(c, r);
                if (frameCount <= 5)
                {
                    Console.WriteLine($"[HOOK 0x00427E40 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00272000u, (c, r) =>
            {
                uint stageMgr = c->GetU32(4);
                if (frameCount <= 5 || frameCount % 60 == 0)
                {
                    uint act = stageMgr != 0 ? Ps2Memory.Read32(c, r, stageMgr + 60) : 0;
                    uint f116 = stageMgr != 0 ? Ps2Memory.Read32(c, r, stageMgr + 116) : 0;
                    uint sl = Ps2Memory.Read32(c, r, 0x0050A6F8u);
                    uint slB = sl != 0 ? Ps2Memory.Read32(c, r, sl) : 0;
                    uint slE = sl != 0 ? Ps2Memory.Read32(c, r, sl + 4) : 0;
                    Console.WriteLine($"[HOOK 0x00272000 - StageManager::Update frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s] mgr=0x{stageMgr:X8} f116={f116} act=0x{act:X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8} sl=(0x{sl:X8}:0x{slB:X8}..0x{slE:X8})");
                }
                RecompiledFunctions.sub_00272000(c, r);
                if (frameCount <= 5 || frameCount % 60 == 0)
                {
                    uint sl = Ps2Memory.Read32(c, r, 0x0050A6F8u);
                    uint slB = sl != 0 ? Ps2Memory.Read32(c, r, sl) : 0;
                    uint slE = sl != 0 ? Ps2Memory.Read32(c, r, sl + 4) : 0;
                    Console.WriteLine($"[HOOK 0x00272000 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s] s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8} sl=(0x{sl:X8}:0x{slB:X8}..0x{slE:X8})");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00264268u, (c, r) =>
            {
                uint stage = c->GetU32(4);
                if (frameCount <= 3 || frameCount % 60 == 0)
                {
                    uint f116 = stage != 0 ? Ps2Memory.Read32(c, r, stage + 116) : 0;
                    uint act = stage != 0 ? Ps2Memory.Read32(c, r, stage + 60) : 0;
                    uint actVtable = act != 0 ? Ps2Memory.Read32(c, r, act + 24) : 0;
                    uint m36 = actVtable != 0 ? Ps2Memory.Read32(c, r, actVtable + 36) : 0;
                    Console.WriteLine($"[HOOK 0x00264268 - Stage::UpdatePump frame={frameCount}] stage=0x{stage:X8} f116={f116} act=0x{act:X8} m36=0x{m36:X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                }
                RecompiledFunctions.sub_00264268(c, r);
                if (frameCount <= 3 || frameCount % 60 == 0)
                {
                    Console.WriteLine($"[HOOK 0x00264268 EXIT frame={frameCount}] s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                }
            });

            RuntimeDispatcher.RegisterHook(0x002BB5B8u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002BB5B8 - FlowManager::ChangeState] stateObj=0x{a0:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_002BB5B8(c, r);
            });

            static unsafe string ReadGuestString(byte* r, uint addr, int maxLen = 512)
            {
                if (addr == 0) return "<null>";
                uint phys = addr & 0x01FFFFFFu;
                if (phys >= 0x02000000u) return $"<0x{addr:X8}>";
                int len = 0;
                while (len < maxLen && phys + len < 0x02000000u && r[phys + len] != 0)
                    len++;
                return System.Text.Encoding.ASCII.GetString(r + phys, len);
            }

            RuntimeDispatcher.RegisterHook(0x0029C9E0u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x0029C9E0 - G2DTarget::SpawnDialog] a0=0x{a0:X8} a1=0x{a1:X8} RA=0x{ra:X8}");
                RecompiledFunctions.sub_0029C9E0(c, r);
                uint st332 = Ps2Memory.Read32(c, r, a0 + 332u);
                float t320 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a0 + 320u));
                float t324 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a0 + 324u));
                Console.WriteLine($"[HOOK 0x0029C9E0 EXIT] G2DTarget.332={st332} t320={t320:F3} t324={t324:F3}");
            });

            uint lastG2d332 = 0xFFFFFFFFu;
            RuntimeDispatcher.RegisterHook(0x0029C760u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint f144 = Ps2Memory.Read32(c, r, a0 + 144u);
                uint st332 = Ps2Memory.Read32(c, r, a0 + 332u);
                uint cnt152 = Ps2Memory.Read32(c, r, a0 + 152u);
                float t324 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a0 + 324u));
                if (st332 != lastG2d332 || frameCount <= 5 || frameCount % 20 == 0)
                {
                    Console.WriteLine($"[HOOK 0x0029C760 - G2DTarget::Update frame={frameCount}] a0=0x{a0:X8} f144={f144} st332={st332} cnt152={cnt152} t324={t324:F3}");
                    lastG2d332 = st332;
                }
                RecompiledFunctions.sub_0029C760(c, r);
                uint st332Post = Ps2Memory.Read32(c, r, a0 + 332u);
                if (st332Post != st332)
                {
                    Console.WriteLine($"[HOOK 0x0029C760 EXIT frame={frameCount}] st332 changed {st332} -> {st332Post}");
                    lastG2d332 = st332Post;
                }
            });

            RuntimeDispatcher.RegisterHook(0x0029C048u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                string varName = ReadGuestString(r, a1);
                Console.WriteLine($"[HOOK 0x0029C048 - G2DTarget::OnGetExists frame={frameCount}] a0=0x{a0:X8} var='{varName}'");
                RecompiledFunctions.sub_0029C048(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x0029C1D8u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                string varName = ReadGuestString(r, a1);
                Console.WriteLine($"[HOOK 0x0029C1D8 - G2DTarget::OnGetVariable frame={frameCount}] a0=0x{a0:X8} var='{varName}'");
                RecompiledFunctions.sub_0029C1D8(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x0029C358u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                uint a2 = c->GetU32(6);
                string cmd = ReadGuestString(r, a1);
                string arg = ReadGuestString(r, a2);
                Console.WriteLine($"[HOOK 0x0029C358 - G2DTarget::OnFsCommand frame={frameCount}] a0=0x{a0:X8} cmd='{cmd}' arg='{arg}'");
                RecompiledFunctions.sub_0029C358(c, r);
                uint st332 = Ps2Memory.Read32(c, r, a0 + 332u);
                Console.WriteLine($"[HOOK 0x0029C358 EXIT] G2DTarget.332={st332}");
            });

            uint lastMc284 = 0xFFFFFFFFu;
            RuntimeDispatcher.RegisterHook(0x002C3C38u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint g2d = Ps2Memory.Read32(c, r, 0x00514F1Cu);
                uint g2d332 = g2d != 0 ? Ps2Memory.Read32(c, r, g2d + 332u) : 0xFFFFFFFFu;
                uint st284 = Ps2Memory.Read32(c, r, a0 + 284u);
                float t300 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a0 + 300u));
                if (st284 != lastMc284 || frameCount <= 5 || frameCount % 20 == 0)
                {
                    Console.WriteLine($"[HOOK 0x002C3C38 - MemoryCardCheckState::Update frame={frameCount}] a0=0x{a0:X8} st284={st284} t300={t300:F3} g2d332={g2d332}");
                    lastMc284 = st284;
                }
                RecompiledFunctions.sub_002C3C38(c, r);
                uint st284After = Ps2Memory.Read32(c, r, a0 + 284u);
                if (st284After != st284)
                {
                    Console.WriteLine($"[HOOK 0x002C3C38 EXIT frame={frameCount}] st284 changed {st284} -> {st284After}");
                    lastMc284 = st284After;
                }
            });

            RuntimeDispatcher.RegisterHook(0x00277070u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                uint apt = Ps2Memory.Read32(c, r, 0x0052570Cu);
                Console.WriteLine($"[HOOK 0x00277070 - ScreenManager::OpenScreen frame={frameCount}] mgr=0x{a0:X8} screenId={a1} apt=0x{apt:X8}");
                RecompiledFunctions.sub_00277070(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x00306610u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                uint t0 = c->GetU32(8);
                uint t1 = c->GetU32(9);
                uint t2 = c->GetU32(10);
                string fnName = ReadGuestString(r, a1);
                string arg0 = t0 >= 1 ? ReadGuestString(r, t1) : "";
                string arg1 = t0 >= 2 ? ReadGuestString(r, t2) : "";
                Console.WriteLine($"[HOOK 0x00306610 - AptCall frame={frameCount}] apt=0x{a0:X8} fn='{fnName}' argc={t0} arg0='{arg0}' arg1='{arg1}'");
                RecompiledFunctions.sub_00306610(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x00306BD8u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint a1 = c->GetU32(5);
                string url = ReadGuestString(r, a0);
                string target = ReadGuestString(r, a1);
                Console.WriteLine($"[HOOK 0x00306BD8 - Apt::LoadMovie frame={frameCount}] url='{url}' target='{target}'");
                RecompiledFunctions.sub_00306BD8(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x00306690u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                if (frameCount <= 5 || frameCount % 20 == 0)
                {
                    uint f848 = Ps2Memory.Read32(c, r, a0 + 848u);
                    uint q3072 = Ps2Memory.Read32(c, r, 0x00513900u);
                    uint q3076 = Ps2Memory.Read32(c, r, 0x00513904u);
                    uint dtMsPrev = Ps2Memory.Read32(c, r, 0x005294BCu);
                    float timeSec = BitConverter.Int32BitsToSingle((int)Ps2Memory.Read32(c, r, 0x005294B4u));
                    uint timeMs = Ps2Memory.Read32(c, r, 0x005294C0u);
                    Console.WriteLine($"[HOOK 0x00306690 - Apt::TickAndRender frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s] apt=0x{a0:X8} f848={f848} swfQueue={q3072}..{q3076} dtMs_prev={dtMsPrev} timeSec={timeSec:F4} timeMs={timeMs}");
                }
                RecompiledFunctions.sub_00306690(c, r);
                if (frameCount <= 5 || frameCount % 20 == 0)
                {
                    Console.WriteLine($"[HOOK 0x00306690 EXIT frame={frameCount} t={sw.Elapsed.TotalSeconds:F3}s]");
                }
            });

            RuntimeDispatcher.RegisterHook(0x00301A48u, (c, r) =>
            {
                string s = ReadGuestString(r, c->GetU32(4));
                Console.WriteLine($"[HOOK 0x00301A48 - AptHostLoadSwf frame={frameCount}] file='{s}'");
                RecompiledFunctions.sub_00301A48(c, r);
            });

            RuntimeDispatcher.RegisterHook(0x0026CEE8u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x0026CEE8 - StartupStage::Enter] a0=0x{a0:X8} RA=0x{ra:X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                RecompiledFunctions.sub_0026CEE8(c, r);
                Console.WriteLine($"[HOOK 0x0026CEE8 EXIT] s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
            });

            RuntimeDispatcher.RegisterHook(0x002C8660u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                uint ra = c->GetU32(31);
                Console.WriteLine($"[HOOK 0x002C8660 - MMUTarget::ctor] a0=0x{a0:X8} RA=0x{ra:X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                RecompiledFunctions.sub_002C8660(c, r);
                Console.WriteLine($"[HOOK 0x002C8660 EXIT] v0=0x{c->GetU32(2):X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
            });

            RuntimeDispatcher.RegisterHook(0x0026CFF0u, (c, r) =>
            {
                uint a0 = c->GetU32(4);
                if (frameCount % 60 == 0)
                {
                    Console.WriteLine($"[HOOK 0x0026CFF0 - StartupStage::Update frame={frameCount}] a0=0x{a0:X8} s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                }
                RecompiledFunctions.sub_0026CFF0(c, r);
                if (frameCount % 60 == 0)
                {
                    Console.WriteLine($"[HOOK 0x0026CFF0 EXIT frame={frameCount}] s0=0x{c->GetU32(16):X8} s1=0x{c->GetU32(17):X8} sp=0x{c->GetU32(29):X8}");
                }
            });

            // Hook Application::UpdateFrame (0x003E2940) to step frame logic and advance VBlank
            RuntimeDispatcher.RegisterHook(0x003E2940u, (c, r) =>
            {
                uint appPtr = c->GetU32(4);

                if (Volatile.Read(ref userRequestedExit))
                {
                    Ps2Memory.Write32(c, r, appPtr + 36u, 1u);
                    return;
                }

                frameCount++;
                ThrottleFrameRate(overrideOpts.TargetFps, ref lastFrameTick);

                if (window == null)
                {
                    if (autoStartFrame > 0 && frameCount >= autoStartFrame && frameCount <= autoStartFrame + 5)
                    {
                        var st = DualShock2State.Neutral;
                        st.PressedButtons = Ps2PadButtons.Start;
                        runtime.Pad.SetPortState(0, st);
                    }
                    else if (autoStartFrame > 0 && frameCount == autoStartFrame + 6)
                    {
                        runtime.Pad.SetPortState(0, DualShock2State.Neutral);
                    }
                    overrides.UpdateInput(runtime.Pad, input, r);
                }
                runtime.PumpPadSubsystem(c, r);

                uint flowState = Ps2Memory.Read32(c, r, 0x00492908u);
                uint flowTarget = Ps2Memory.Read32(c, r, 0x0049290Cu);
                uint p1318 = Ps2Memory.Read32(c, r, 0x00511318u);
                float splashTimer = p1318 != 0 ? BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, p1318 + 52)) : 0f;
                uint movieCtrl = Ps2Memory.Read32(c, r, 0x00492904u);
                uint movieState = movieCtrl != 0 ? Ps2Memory.Read32(c, r, movieCtrl + 60u) : 0;
                uint movieStateId = movieState != 0 ? Ps2Memory.Read32(c, r, movieState) : 0xFFFFFFFFu;
                uint movieFlag132 = movieCtrl != 0 ? Ps2Memory.Read32(c, r, movieCtrl + 132u) : 0;
                float dtFdc4 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, 0x0049FDC4u));
                if (frameCount <= 5 || frameCount % 30 == 0 || frameCount == 25 || frameCount == 40)
                {
                    Console.WriteLine($"[FRAME {frameCount} FLOW] flowState={flowState} flowTarget={flowTarget} splashTimer={splashTimer:F2}s dt={dtFdc4:F4} movieCtrl=0x{movieCtrl:X8} movieState=0x{movieState:X8} stateId={movieStateId} flag132={movieFlag132}");
                }

                // Unblock boot splash test removed to allow natural SLUS_216.64 state progression

                uint state = Ps2Memory.Read32(c, r, appPtr + 308u);
                uint nextState = Ps2Memory.Read32(c, r, appPtr + 312u);
                uint scenePtr = Ps2Memory.Read32(c, r, appPtr + 316u);
                uint sceneId = Ps2Memory.Read32(c, r, appPtr + 320u);
                if (state != 2 || nextState != 2 || frameCount <= 5 || frameCount % 60 == 0)
                {
                    Console.WriteLine($"[FRAME {frameCount} PRE t={sw.Elapsed.TotalSeconds:F3}s] state={state} nextState={nextState} scene=0x{scenePtr:X8} nextSceneId=0x{sceneId:X8}");
                }

                // Advance hardware timers by 1 frame (16.667 ms) and fire INTC_TIM0/TIM1 once per frame
                runtime.Kernel.StepFrameTimers(c, r);

                // Execute native SLUS_216.64 frame update
                inApplicationUpdateFrame = true;
                try
                {
                    RecompiledFunctions.sub_003E2940(c, r);
                }
                finally
                {
                    inApplicationUpdateFrame = false;
                }

                uint postState = Ps2Memory.Read32(c, r, appPtr + 308u);
                uint postNextState = Ps2Memory.Read32(c, r, appPtr + 312u);
                uint postScenePtr = Ps2Memory.Read32(c, r, appPtr + 316u);
                if (postState != 2 || postNextState != 2 || frameCount <= 5 || frameCount % 60 == 0)
                {
                    Console.WriteLine($"[FRAME {frameCount} POST t={sw.Elapsed.TotalSeconds:F3}s] state={postState} nextState={postNextState} scene=0x{postScenePtr:X8}");
                }

                // Advance VBlank / INTC handlers
                runtime.Kernel.TriggerVBlank(c, r);

                // Fallback batch swap for any frame where primitives were drawn but 0x00428AE8 was not hit at the end
                if (graphics.Mode == GsRendererMode.OpenGl && graphics.BackBatch.VertexCount > 0)
                {
                    graphics.SwapBatches();
                    Interlocked.Increment(ref presentationCount);
                }

                if (frameCount <= 5 || frameCount % 60 == 0)
                {
                    int nonZeroVramWords = 0;
                    int nonBlackRgbWords = 0;
                    uint sampleWord = 0;
                    int sampleWordIdx = -1;
                    ReadOnlySpan<uint> vramWords = MemoryMarshal.Cast<byte, uint>(graphics.Gs.Vram);
                    for (int i = 0; i < vramWords.Length; i++)
                    {
                        uint w = vramWords[i];
                        if (w != 0)
                        {
                            nonZeroVramWords++;
                            if ((w & 0x00FFFFFFu) != 0 && (w & 0x00FFFFFFu) != 0x00808080u)
                            {
                                nonBlackRgbWords++;
                                if (sampleWordIdx < 0)
                                {
                                    sampleWord = w;
                                    sampleWordIdx = i;
                                }
                            }
                            else if (sampleWordIdx < 0)
                            {
                                sampleWord = w;
                                sampleWordIdx = i;
                            }
                        }
                    }

                    Console.WriteLine(
                        $"[Castaway.Host] Frame {frameCount} | Elapsed: {sw.Elapsed.TotalSeconds:F2}s | " +
                        $"GIF: {graphics.Gs.TotalGifTagsProcessed} | Trx: {graphics.Gs.TotalVramTransfers} | " +
                        $"Prims: {graphics.Gs.TotalPrimitivesDrawn} (Vtx: {graphics.Gs.TotalVerticesKicked}) | XGKicks: {graphics.Vu1.TotalXgKicksExecuted} | " +
                        $"PixShaded: {graphics.Gs.TotalPixelsShaded} PixWritten: {graphics.Gs.TotalPixelsWritten} PixZFail: {graphics.Gs.TotalPixelsZFailed} | " +
                        $"FRAME1=0x{graphics.Gs.Frame[0]:X16} FRAME2=0x{graphics.Gs.Frame[1]:X16} DISPFB1=0x{runtime.Mmio.GsDispfb1:X16} DISPFB2=0x{runtime.Mmio.GsDispfb2:X16} | " +
                        $"XYOFF1=0x{graphics.Gs.Xyoffset[0]:X16} SCISSOR1=0x{graphics.Gs.Scissor[0]:X16} | " +
                        $"NonZeroVRAM={nonZeroVramWords} NonBlackRGB={nonBlackRgbWords} Sample[0x{sampleWordIdx:X5}]=0x{sampleWord:X8}");
                }

                if (dumpVram && (frameCount == 1 || frameCount == 5 || frameCount == 20 || frameCount % 60 == 0 || (maxFrames > 0 && frameCount == maxFrames)))
                {
                    ulong frame0 = graphics.Gs.Frame[0];
                    int fbp0 = (int)(frame0 & 0x1FFul) * 32;
                    int fbw0 = Math.Max(1, (int)((frame0 >> 16) & 0x3Ful)) * 64;
                    var psm0 = (GsPsm)((frame0 >> 24) & 0x3Ful);

                    ulong frame1 = graphics.Gs.Frame[1];
                    int fbp1 = (int)(frame1 & 0x1FFul) * 32;
                    int fbw1 = Math.Max(1, (int)((frame1 >> 16) & 0x3Ful)) * 64;
                    var psm1 = (GsPsm)((frame1 >> 24) & 0x3Ful);

                    VramDumper.DumpRawVramBmp($"vram_linear_frame{frameCount}.bmp", graphics.Gs.Vram);
                    VramDumper.DumpUnswizzledBmp($"fb_ctxt1_frame{frameCount}.bmp", graphics.Gs.Vram, fbp0, fbw0, psm0);
                    VramDumper.DumpUnswizzledBmp($"fb_ctxt2_frame{frameCount}.bmp", graphics.Gs.Vram, fbp1, fbw1, psm1);

                    ulong dispfb1 = runtime.Mmio.GsDispfb1;
                    if (dispfb1 != 0)
                    {
                        int fbpDisp = (int)(dispfb1 & 0x1FFul) * 32;
                        int fbwDisp = Math.Max(1, (int)((dispfb1 >> 9) & 0x3Ful)) * 64;
                        var psmDisp = (GsPsm)((dispfb1 >> 15) & 0x1Ful);
                        VramDumper.DumpUnswizzledBmp($"fb_dispfb1_frame{frameCount}.bmp", graphics.Gs.Vram, fbpDisp, fbwDisp, psmDisp);
                    }
                }

                if (maxFrames > 0 && frameCount >= maxFrames)
                {
                    Console.WriteLine("\n================================================================================");
                    Console.WriteLine($">>> [SHUTDOWN SEQUENCE INITIATED] (Target frame limit of {maxFrames} frames reached)");
                    Console.WriteLine(">>> Note: Any logs below this line are SLUS_216.64 TEARDOWN & CLEANUP, NOT gameplay!");
                    Console.WriteLine("================================================================================\n");
                    Ps2Memory.Write32(c, r, appPtr + 36u, 1u);
                }
            });

            int mainEnterCount = 0;
            RuntimeDispatcher.RegisterHook(0x003E4610u, (c, r) =>
            {
                int count = Interlocked.Increment(ref mainEnterCount);
                Console.WriteLine($"[HOOK 0x003E4610 - main #{count}] entered! tid={runtime.Kernel.CurrentThreadId} RA=0x{c->GetU32(31):X8} sp=0x{c->GetU32(29):X8} a0={c->GetU32(4)} a1=0x{c->GetU32(5):X8}");
                if (count > 1)
                {
                    Console.WriteLine($"[HOOK 0x003E4610 - UNEXPECTED RE-ENTRY STACK]\n{Environment.StackTrace}");
                    return;
                }
                RecompiledFunctions.sub_003E4610(c, r);
                Console.WriteLine($"[HOOK 0x003E4610 - main] returned! v0={c->GetS32(2)}");
            });

            RuntimeDispatcher.RegisterHook(0x00489AF0u, (c, r) =>
            {
                Console.WriteLine($"[HOOK 0x00489AF0 - exit] called with code={c->GetS32(4)}!");
                RecompiledFunctions.sub_00489AF0(c, r);
            });

            var entryFn = RuntimeDispatcher.Lookup(0x00100008u);
            if (entryFn == null)
            {
                Console.Error.WriteLine("[Castaway.Host] Failed to lookup _start (0x00100008)!");
                return 1;
            }

            Console.WriteLine("[Castaway.Host] Booting SLUS_216.64 from _start (0x00100008)...");
            entryFn(&ctx, ram);

            Console.WriteLine("\n================================================================================");
            Console.WriteLine(
                $">>> [SHUTDOWN SEQUENCE COMPLETED] Clean exit after {frameCount} frame(s) ({sw.Elapsed.TotalSeconds:F2}s).\n" +
                $">>> GIF Tags: {graphics.Gs.TotalGifTagsProcessed}, Transfers: {graphics.Gs.TotalVramTransfers}, Prims: {graphics.Gs.TotalPrimitivesDrawn}.");
            Console.WriteLine("================================================================================\n");
            return 0;
        }
        finally
        {
            RuntimeDispatcher.SetTraceHandler(null);
            runtime.Dispose();
            RuntimeDispatcher.ClearHooks();
            traceRecorder?.Dispose();
            NativeMemory.Free(vu0Data);
            NativeMemory.Free(scratchpad);
            NativeMemory.Free(ram);
        }
    }

    /// <summary>
    /// High-precision sub-millisecond frame pacer enforcing exact PS2 NTSC hardware timing (default 30.0 FPS / 33.333 ms).
    /// </summary>
    private static void ThrottleFrameRate(int targetFps, ref long lastFrameTick)
    {
        if (targetFps <= 0)
            return;

        long targetTicksPerFrame = Stopwatch.Frequency / targetFps;
        long now = Stopwatch.GetTimestamp();
        if (lastFrameTick == 0)
        {
            lastFrameTick = now;
            return;
        }

        long targetTick = lastFrameTick + targetTicksPerFrame;
        while (true)
        {
            now = Stopwatch.GetTimestamp();
            long remainingTicks = targetTick - now;
            if (remainingTicks <= 0)
                break;

            long remainingMs = (remainingTicks * 1000) / Stopwatch.Frequency;
            if (remainingMs > 2)
            {
                Thread.Sleep(1);
            }
            else if (remainingMs > 0)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(20);
            }
        }

        long afterNow = Stopwatch.GetTimestamp();
        // Prevent catch-up bursts if a frame hitched significantly
        if (afterNow - targetTick > targetTicksPerFrame * 2)
        {
            lastFrameTick = afterNow;
        }
        else
        {
            lastFrameTick = targetTick;
        }
    }
}

/// <summary>
/// Phase 5 Binary Trace Recorder writing 16-byte packed TraceRecord structs
/// ([uint GuestPC, uint EventType, uint GprCrc32, uint Cop1Crc32]).
/// </summary>
public sealed unsafe class BinaryTraceRecorder : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _maxRecords;
    private int _writtenRecords;

    public int WrittenRecords => _writtenRecords;

    public BinaryTraceRecorder(string path, int maxRecords = 500_000)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
        _maxRecords = maxRecords;
    }

    public void OnTraceEvent(uint guestPc, uint eventType, CpuContext* ctx)
    {
        if (_writtenRecords >= _maxRecords)
            return;

        TraceRecord rec = new()
        {
            GuestPc = guestPc,
            EventType = eventType,
            GprCrc32 = ctx->ComputeGprCrc32(),
            Cop1Crc32 = ctx->ComputeCop1Crc32()
        };

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref rec, 1));
        _stream.Write(bytes);
        _writtenRecords++;
    }

    public void Dispose()
    {
        _stream.Flush();
        _stream.Dispose();
    }
}
