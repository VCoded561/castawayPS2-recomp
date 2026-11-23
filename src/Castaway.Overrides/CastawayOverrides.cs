using System.Numerics;
using Castaway.Core;
using Castaway.GsRenderer;
using Castaway.GsRenderer.Gs;
using Castaway.Runtime;
using Silk.NET.Input;

namespace Castaway.Overrides;

public sealed class OverrideOptions
{
    public int ResolutionScale { get; set; } = 2;
    public bool EnableWidescreen { get; set; } = false;
    public float TargetAspectRatio { get; set; } = 16f / 9f;
    public bool EnableModernControls { get; set; } = true;
    public float MouseSensitivity { get; set; } = 0.015f;
    public GsRendererMode RendererMode { get; set; } = GsRendererMode.Software;
    public int TargetFps { get; set; } = 60; // 60 FPS default for smooth PS2 NTSC field rate
    public int MsaaSamples { get; set; } = 4; // OpenGL MSAA sample count (0/1 = off, 2, 4, 8, 16)
    public bool EnableFramerateIndependence { get; set; } = true;
}

/// <summary>
/// Phase 6 non-destructive enhancements living exclusively in Castaway.Overrides.
/// Uses RuntimeDispatcher address hooks without modifying src/Castaway.Generated/.
/// </summary>
public sealed unsafe class CastawayOverrides
{
    // PS2Display::GetAspectRatio() in SLUS_216.64 (0x00433B00, vtable[+336] on *(0x004A0728))
    // Originally returns hardcoded 4:3 (0x3FAAAAAB) in $f0; called by all 3D camera frustum builders.
    public const uint DisplayGetAspectRatioAddr = 0x00433B00u;
    public const float NativePs2AspectRatio = 4f / 3f;
    public const double NativeConsoleTickPeriod = 1.0 / 30.0;

    public OverrideOptions Options { get; }
    public Action? OnSnapshotRequested { get; set; }
    public Action? OnRecordInputsToggleRequested { get; set; }
    private CastawayRuntime? _runtime;
    private GsGraphicsPipeline? _graphics;
    private float _viewportAspectRatio;
    private Vector2 _lastMousePos;
    private bool _hasLastMousePos;
    private float _pendingMouseDeltaX;
    private float _pendingMouseDeltaY;
    private float _pendingScrollDelta;

    // -------------------------------------------------------------------------
    // Framerate Independence Clock & 30 Hz Accumulator State
    // -------------------------------------------------------------------------
    private const uint TimerTicksPer30Hz = 19_200u; // 576,000 Hz / 30 Hz = exact 19,200 ticks
    private const uint TimerTicksPer60Hz = 9_600u;  // 576,000 Hz / 60 Hz = exact 9,600 ticks
    private long _tick30HzAccumTicks;
    private long _tick60HzAccumTicks;
    private uint _totalTicks60Hz;
    private bool _ticks60HzInitialized;
    private long _aptMsRemainderNum;
    private uint _aptTotalMsInt;
    private uint _aptPrevMsInt;
    private bool _aptClockInitialized;
    private uint _renderFrameIndex;
    private readonly long[] _aptRepeatAccumTicks = new long[4];
    private readonly uint[] _aptRepeatLastFrame = new uint[4];
    private readonly Dictionary<uint, float> _hudSpeedHideTimers = new();
    private readonly Dictionary<uint, float> _liveMode164Timers = new();
    private readonly Dictionary<uint, float> _liveMode12Timers = new();
    private readonly Dictionary<uint, (float RemX, float RemY)> _cursorSubPixelRemainders = new();

    /// <summary>Number of whole 30 Hz console ticks (33.333 ms) elapsed on the current render frame.</summary>
    public int CurrentFrameTicks30Hz { get; private set; }
    /// <summary>Monotonically increasing 30 Hz console tick index (decoupled from render _framecount 0x0049FDB8).</summary>
    public uint TotalTicks30Hz { get; private set; } = 1u;
    /// <summary>True continuous frame delta seconds (free of SLUS_216.64's 1/60s floor and 60 Hz quantization).</summary>
    public float ContinuousDtSeconds { get; private set; } = 1f / 30f;

    public CastawayOverrides(OverrideOptions? options = null)
    {
        Options = options ?? new OverrideOptions();
    }

    public void ResetTimingState()
    {
        _tick30HzAccumTicks = 0L;
        _tick60HzAccumTicks = 0L;
        _totalTicks60Hz = 0u;
        _ticks60HzInitialized = false;
        _aptMsRemainderNum = 0L;
        _aptTotalMsInt = 0u;
        _aptPrevMsInt = 0u;
        _aptClockInitialized = false;
        _renderFrameIndex = 0u;
        Array.Clear(_aptRepeatAccumTicks);
        Array.Clear(_aptRepeatLastFrame);
        CurrentFrameTicks30Hz = 1;
        TotalTicks30Hz = 1u;
        ContinuousDtSeconds = 1f / 30f;
        _hudSpeedHideTimers.Clear();
        _liveMode164Timers.Clear();
        _liveMode12Timers.Clear();
        _cursorSubPixelRemainders.Clear();
    }

    public void SetViewportAspectRatio(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            float aspect = Math.Clamp((float)width / height, 0.2f, 8.0f);
            Volatile.Write(ref _viewportAspectRatio, aspect);
        }
    }

    public float GetEffectiveAspectRatio()
    {
        if (_graphics != null && _graphics.Mode == GsRendererMode.OpenGl)
        {
            float vpAspect = Volatile.Read(ref _viewportAspectRatio);
            if (vpAspect > 0.0f)
                return vpAspect;
            return Options.EnableWidescreen ? Math.Clamp(Options.TargetAspectRatio, 0.2f, 8.0f) : NativePs2AspectRatio;
        }

        if (Options.EnableWidescreen)
        {
            float vpAspect = Volatile.Read(ref _viewportAspectRatio);
            return vpAspect > 0.0f ? vpAspect : Math.Clamp(Options.TargetAspectRatio, 0.2f, 8.0f);
        }

        return NativePs2AspectRatio;
    }

    public void Install(CastawayRuntime runtime, GsGraphicsPipeline graphics)
    {
        _runtime = runtime;
        _graphics = graphics;
        _graphics.Mode = Options.RendererMode;
        graphics.OpenGl.SetResolutionScale(Options.ResolutionScale);
        graphics.OpenGl.SetMsaaSamples(Options.MsaaSamples);

        InstallRenderWareUiHook();
        InstallAspectRatioHook();
        if (Options.EnableFramerateIndependence)
        {
            InstallFramerateIndependenceHooks();
        }
    }

    private void InstallRenderWareUiHook()
    {
        // 0x00433F40 is an unlabelled leaf RenderWare 2D UI coordinate transformation routine inside
        // sub_00433E48 that converts a 2D bounding rect (a2 = [x0, y0, x1, y1]) relative to an origin
        // (a3 = [ox, oy]) into screen-space destination (a1 = [x0 - ox, (y0 - oy) - 1.0, x1 - ox, (y1 - oy) - 1.0]).
        RuntimeDispatcher.RegisterHook(0x00433F40u, (c, r) =>
        {
            uint ra = c->GetU32(31);
            uint a1 = c->GetU32(5);
            uint a2 = c->GetU32(6);
            uint a3 = c->GetU32(7);
            float a3_0 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a3 + 0));
            float a3_1 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a3 + 4));
            float a2_0 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 0));
            float a2_1 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 4));
            float a2_2 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 8));
            float a2_3 = BitConverter.UInt32BitsToSingle(Ps2Memory.Read32(c, r, a2 + 12));

            float out0 = a2_0 - a3_0;
            float out1 = (a2_1 - a3_1) - 1.0f;
            float out2 = a2_2 - a3_0;
            float out3 = (a2_3 - a3_1) - 1.0f;

            Ps2Memory.Write32(c, r, a1 + 0, BitConverter.SingleToUInt32Bits(out0));
            Ps2Memory.Write32(c, r, a1 + 4, BitConverter.SingleToUInt32Bits(out1));
            Ps2Memory.Write32(c, r, a1 + 8, BitConverter.SingleToUInt32Bits(out2));
            Ps2Memory.Write32(c, r, a1 + 12, BitConverter.SingleToUInt32Bits(out3));

            RuntimeDispatcher.OnReturn(c, ra);
        });
    }

    private void InstallAspectRatioHook()
    {
        // Hook PS2Display::GetAspectRatio() at 0x00433B00 (vtable[+336] on *(0x004A0728)).
        // Original MIPS returns hardcoded 4:3 (0x3FAAAAAB) in $f0; returning the live OpenGL
        // viewport aspect ratio scales m[0][0] = cot(fovY/2) / aspect in sub_003DBFD0 (Hor+)
        // and updates world frustum culling planes in sub_003EA738 so any aspect ratio renders
        // without 3D stretching.
        RuntimeDispatcher.RegisterHook(DisplayGetAspectRatioAddr, (ctx, ram) =>
        {
            float aspect = GetEffectiveAspectRatio();
            ctx->SetFpuBits(0, BitConverter.SingleToUInt32Bits(aspect));
            RuntimeDispatcher.OnReturn(ctx, ctx->GetU32(31));
        });
    }

    /// <summary>
    /// Installs all 6 pillars of framerate-independence hooks so SLUS_216.64 runs at variable/high
    /// framerates (15 FPS..300+ FPS) and handles stalls/freezes without gameplay, menu, animation,
    /// or particle speedups/slowdowns.
    /// </summary>
    private void InstallFramerateIndependenceHooks()
    {
        // =====================================================================
        // PILLAR 1: Master Engine Clock (EEngine::EndFrame @ 0x003E2D98)
        // =====================================================================
        // sub_003E2D98 quantizes raw elapsed seconds into integer 60 Hz VBlank ticks (0x0049FDC0 = 1..15)
        // and floors _dt (0x0049FDC4) at 1/60s (0.016667s), causing a 2x..5x speedup above 60 FPS.
        // We run the original function so its semaphore wait, flip, and timer reset execute normally,
        // then overwrite 0x0049FDC4 (_dt), 0x0057B2D8/DC/E0, and advance our exact integer 30 Hz tick accumulator.
        RuntimeDispatcher.RegisterHook(0x003E2D98u, (ctx, ram) =>
        {
            RuntimeDispatcher.Call(0x003E2D98u, ctx, ram);
            _renderFrameIndex++;

            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                // Fixed-timer deterministic mode: preserve exact retail behavior
                float fixedDt = Ps2Memory.ReadF32(ctx, ram, 0x0049FDC4u);
                ContinuousDtSeconds = fixedDt > 0f ? fixedDt : (1f / 30f);
                CurrentFrameTicks30Hz = 1;
                TotalTicks30Hz++;
                return;
            }

            uint frameTicks = _runtime.Mmio.LastFrameTimerTicks;
            if (frameTicks == 0u)
                frameTicks = MmioRouter.FixedTimerTicksPerFrame;
            if (frameTicks > MmioRouter.MaxRealTimeTimerTicksPerFrame)
                frameTicks = MmioRouter.MaxRealTimeTimerTicksPerFrame;

            // Exact continuous frame delta seconds (clamped to [1e-6s, 1/15s] so 5,000+ FPS uncapped stays 1.0x)
            float dt = Math.Clamp(frameTicks / (float)MmioRouter.Timer1TicksPerSecond, 1e-6f, 1f / 15f);
            ContinuousDtSeconds = dt;

            // Advance exact integer 30 Hz logical tick accumulator (19,200 ticks = exactly 1/30s @ 576 kHz)
            // Zero floating-point boundary drift at 30, 60, 120, 240, or variable FPS!
            _tick30HzAccumTicks += frameTicks;
            int ticks30 = (int)(_tick30HzAccumTicks / TimerTicksPer30Hz);
            if (ticks30 > 0)
            {
                _tick30HzAccumTicks -= (long)ticks30 * TimerTicksPer30Hz;
                if (ticks30 > 2)
                {
                    ticks30 = 2;
                    _tick30HzAccumTicks = 0L;
                }
                CurrentFrameTicks30Hz = ticks30;
                TotalTicks30Hz += (uint)ticks30;
            }
            else
            {
                CurrentFrameTicks30Hz = 0;
            }

            // Maintain accurate 60 Hz cumulative tick counter (0x0049FDBC) without the 1-tick-per-frame floor
            if (!_ticks60HzInitialized)
            {
                _ticks60HzInitialized = true;
                _totalTicks60Hz = Ps2Memory.Read32(ctx, ram, 0x0049FDBCu);
                _tick60HzAccumTicks = 0L;
            }
            else
            {
                _tick60HzAccumTicks += frameTicks;
                uint ticks60 = (uint)(_tick60HzAccumTicks / TimerTicksPer60Hz);
                if (ticks60 > 0u)
                {
                    _tick60HzAccumTicks -= (long)ticks60 * TimerTicksPer60Hz;
                    _totalTicks60Hz += ticks60;
                }
                Ps2Memory.Write32(ctx, ram, 0x0049FDBCu, _totalTicks60Hz);
            }

            // Overwrite engine delta globals with true unquantized dt (no 1/60s floor!)
            // Note: 0x0049FDB8 (_framecount) is intentionally left incrementing every render frame
            // so SAnimator2::EvaluateIfNewFrame (0x00404910) evaluates smooth character poses every frame.
            Ps2Memory.WriteF32(ctx, ram, 0x0049FDC4u, dt);
            Ps2Memory.WriteF32(ctx, ram, 0x0057B2D8u, Math.Clamp(dt, 1e-6f, 1f / 30f));
            Ps2Memory.WriteF32(ctx, ram, 0x0057B2DCu, Math.Clamp(dt, 1e-6f, 0.05f));
            Ps2Memory.WriteF32(ctx, ram, 0x0057B2E0u, dt);
            Ps2Memory.WriteF32(ctx, ram, 0x0049FDCCu, 1.0f / dt);
            Ps2Memory.WriteF32(ctx, ram, 0x0057B040u, dt);
            Ps2Memory.WriteF32(ctx, ram, 0x0057B044u, dt);
        });

        // =====================================================================
        // PILLAR 2A: Flash UI / Menu D-Pad & Stick Auto-Repeat (AptViewer::RepeatCheck @ 0x00306D10)
        // =====================================================================
        // Original sub_00306D10 adds apt_dt (0x0049A760) to 0x0049A788, computes $s0 = (int)(accum * 29.999998f),
        // and wraps via fmodf(accum, 0.125f) WITHOUT subtracting $s0 / 30.0f.
        // At native 30 FPS on PS2, this produces a 0, 1, 2, 4 -> 1, 2, 3, 4 ramp (~75 count units/sec)
        // against initial threshold 22 (0.367s initial delay) and steady threshold 12 (~6.0 repeats/sec).
        // At >30 FPS unhooked, it fires 2x..10x too fast because 0x0049A788 stays in [0.033s, 0.125s) across
        // many sub-frames. Stepping the exact original PS2 30 FPS formula once per 19,200 ticks (1/30s)
        // reproduces the exact authentic PS2 30 FPS feel identically at 30, 60, 120, and variable FPS.
        RuntimeDispatcher.RegisterHook(0x00306D10u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x00306D10u, ctx, ram);
                return;
            }

            uint ra = ctx->GetU32(31);
            int dir = (int)ctx->GetU32(5); // $a1: 0=Up, 1=Down, 2=Left, 3=Right
            if ((uint)dir >= 4u)
            {
                ctx->SetU32(2, 0u);
                RuntimeDispatcher.OnReturn(ctx, ra);
                return;
            }

            // Detect fresh press after release (sub_00306D10 was not called for this dir on previous frame)
            uint curFrame = _renderFrameIndex;
            if (_aptRepeatLastFrame[dir] != curFrame && _aptRepeatLastFrame[dir] + 1u != curFrame)
            {
                _aptRepeatAccumTicks[dir] = 0L;
            }
            _aptRepeatLastFrame[dir] = curFrame;

            uint frameTicks = _runtime.Mmio.LastFrameTimerTicks;
            if (frameTicks == 0u)
                frameTicks = MmioRouter.FixedTimerTicksPerFrame;
            if (frameTicks > MmioRouter.MaxRealTimeTimerTicksPerFrame)
                frameTicks = MmioRouter.MaxRealTimeTimerTicksPerFrame;

            _aptRepeatAccumTicks[dir] += frameTicks;

            uint countAddr = 0x0049A778u + (uint)(dir * 4);
            uint threshAddr = 0x0049A768u + (uint)(dir * 4);
            int curCount = (int)Ps2Memory.Read32(ctx, ram, countAddr);
            int threshold = (int)Ps2Memory.Read32(ctx, ram, threshAddr);
            if (threshold <= 0)
                threshold = 22;

            uint fired = 0u;
            int maxSteps = 2;
            while (_aptRepeatAccumTicks[dir] >= TimerTicksPer30Hz && maxSteps-- > 0)
            {
                _aptRepeatAccumTicks[dir] -= TimerTicksPer30Hz;

                // Exact native PS2 30 FPS step math from 0x00306D20..0x00306DD4
                float phase = Ps2Memory.ReadF32(ctx, ram, 0x0049A788u) + (1.0f / 30.0f);
                int addUnits = (int)(phase * 29.999998f);
                if (addUnits > 4)
                    addUnits = 4;
                phase %= 0.125f;
                Ps2Memory.WriteF32(ctx, ram, 0x0049A788u, phase);

                curCount += addUnits;
                if (curCount >= threshold)
                {
                    fired = 1u;
                    curCount = 0;
                    threshold = Math.Max(12, threshold - 3);
                    Ps2Memory.Write32(ctx, ram, threshAddr, (uint)threshold);
                    break;
                }
            }

            Ps2Memory.Write32(ctx, ram, countAddr, (uint)curCount);
            ctx->SetU32(2, fired);
            RuntimeDispatcher.OnReturn(ctx, ra);
        });

        // =====================================================================
        // PILLAR 2B: Raw Controller Button Auto-Repeat (EController::UpdateAutoRepeat @ 0x00462230)
        // =====================================================================
        // Original sub_00462230 decrements held button byte counters (+440..+471) by -1 on every render frame.
        // We gate held-button counter decrements to 30 Hz ticks (CurrentFrameTicks30Hz) while still passing
        // newly-pressed edges (24($v1) -> 436($a0)) and button-release resets immediately on every frame.
        RuntimeDispatcher.RegisterHook(0x00462230u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x00462230u, ctx, ram);
                return;
            }

            uint ra = ctx->GetU32(31);
            uint ctrlPtr = ctx->GetU32(4);
            uint padStatePtr = Ps2Memory.Read32(ctx, ram, ctrlPtr + 512u);
            if (padStatePtr == 0)
            {
                RuntimeDispatcher.OnReturn(ctx, ra);
                return;
            }

            uint newlyPressed = Ps2Memory.Read32(ctx, ram, padStatePtr + 24u);
            uint currentlyHeld = Ps2Memory.Read32(ctx, ram, padStatePtr + 8u);
            byte initDelay = Ps2Memory.Read8(ctx, ram, ctrlPtr + 472u);
            byte repeatRate = Ps2Memory.Read8(ctx, ram, ctrlPtr + 473u);
            uint repeatOut = newlyPressed;

            int ticks = CurrentFrameTicks30Hz;
            for (int i = 0; i < 32; i++)
            {
                uint bit = 1u << i;
                uint counterAddr = ctrlPtr + 440u + (uint)i;
                if ((newlyPressed & bit) != 0 || (currentlyHeld & bit) == 0)
                {
                    Ps2Memory.Write8(ctx, ram, counterAddr, initDelay);
                }
                else if (ticks > 0)
                {
                    int cur = Ps2Memory.Read8(ctx, ram, counterAddr);
                    if (cur == 0)
                        cur = 256; // Preserve MIPS 8-bit unsigned wrap (0 - 1 == 0xFF)
                    int next = cur - ticks;
                    if (next <= 0)
                    {
                        repeatOut |= bit;
                        Ps2Memory.Write8(ctx, ram, counterAddr, repeatRate);
                    }
                    else
                    {
                        Ps2Memory.Write8(ctx, ram, counterAddr, (byte)next);
                    }
                }
            }

            Ps2Memory.Write32(ctx, ram, ctrlPtr + 436u, repeatOut);
            RuntimeDispatcher.OnReturn(ctx, ra);
        });

        // =====================================================================
        // PILLAR 3: Apt Flash UI Millisecond Clock (sub_0033C948)
        // =====================================================================
        // Eliminates sub-millisecond float->uint truncation drift and the 300,000 ms (5-minute) baseline reset
        // glitch at 0x0033CA40 by maintaining an exact integer millisecond accumulator from 576 kHz timer ticks.
        // Note: _AptInternalUpdate (0x00361A50) already has a built-in while (accum >= msPerFrame) loop that
        // steps each SWF at its own authored rate (e.g. 16 ms = 60 FPS for EA Logo / UI transitions, or
        // 33 ms = 30 FPS for 30 FPS SWFs), so we must NOT clamp headerPtr+36 (msPerFrame) to 33 ms.
        RuntimeDispatcher.RegisterHook(0x0033C948u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x0033C948u, ctx, ram);
                return;
            }

            float fixedStepOverride = Ps2Memory.ReadF32(ctx, ram, 0x0049AE08u);
            if (fixedStepOverride != 0f)
            {
                RuntimeDispatcher.Call(0x0033C948u, ctx, ram);
                return;
            }

            uint ra = ctx->GetU32(31);
            uint frameTicks = _runtime.Mmio.LastFrameTimerTicks;
            if (frameTicks == 0u)
                frameTicks = MmioRouter.FixedTimerTicksPerFrame;
            if (frameTicks > MmioRouter.MaxRealTimeTimerTicksPerFrame)
                frameTicks = MmioRouter.MaxRealTimeTimerTicksPerFrame;

            float dtSec = frameTicks / (float)MmioRouter.Timer1TicksPerSecond;
            if (!_aptClockInitialized)
            {
                _aptClockInitialized = true;
                _aptTotalMsInt = Ps2Memory.Read32(ctx, ram, 0x005294C0u);
                _aptPrevMsInt = _aptTotalMsInt;
                _aptMsRemainderNum = 0L;
            }

            float prevSec = Ps2Memory.ReadF32(ctx, ram, 0x005294B4u);
            Ps2Memory.WriteF32(ctx, ram, 0x005294B8u, prevSec);
            Ps2Memory.Write32(ctx, ram, 0x005294C4u, _aptPrevMsInt);

            // Exact rational conversion: deltaMs = (frameTicks * 1000 + rem) / 576_000
            long num = (long)frameTicks * 1000L + _aptMsRemainderNum;
            uint deltaMsInt = (uint)(num / MmioRouter.Timer1TicksPerSecond);
            _aptMsRemainderNum = num % MmioRouter.Timer1TicksPerSecond;

            _aptTotalMsInt += deltaMsInt;
            _aptPrevMsInt = _aptTotalMsInt;

            float curSec = (_aptTotalMsInt + (_aptMsRemainderNum / (double)MmioRouter.Timer1TicksPerSecond)) * 0.001f > 0
                ? (float)((_aptTotalMsInt + (_aptMsRemainderNum / (double)MmioRouter.Timer1TicksPerSecond)) * 0.001)
                : 0f;
            Ps2Memory.WriteF32(ctx, ram, 0x005294B4u, curSec);
            Ps2Memory.WriteF32(ctx, ram, 0x005294B0u, dtSec);
            Ps2Memory.Write32(ctx, ram, 0x005294C0u, _aptTotalMsInt);
            Ps2Memory.Write32(ctx, ram, 0x005294BCu, deltaMsInt);

            RuntimeDispatcher.OnReturn(ctx, ra);
        });

        // =====================================================================
        // PILLAR 5A: HUD 6-Frame Sub-Target Round-Robin & Speed Icon Hide Timer (H2DTarget::Update @ 0x0029F598)
        // =====================================================================
        // In sub_0029F598:
        // 1. 0x004997F0 increments mod 6 every frame to round-robin HUDTarget::Update (0x002F6068),
        //    PLDTarget::Update (0x002D5618), and MOTTarget::UpdateMotives (0x002ABBE0, which steps motive bars
        //    every 5th call). On sub-frames where CurrentFrameTicks30Hz == 0, we temporarily set 0x004997F0
        //    so (++cycle % 6) lands on an idle slot (1, 3, or 5) and restore the true cycle after the call!
        // 2. 592($s2) (+0x250, speed icon auto-hide countdown initialized to 6 frames = 0.2s) decrements by -1
        //    every frame at 0x0029F9D0. We convert 592($s2) into a real-time float timer (6 / 30.0f = 0.20s).
        RuntimeDispatcher.RegisterHook(0x0029F598u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x0029F598u, ctx, ram);
                return;
            }

            uint h2dPtr = ctx->GetU32(4);
            int preHideCounter = (int)Ps2Memory.Read32(ctx, ram, h2dPtr + 592u);
            uint savedCycle = Ps2Memory.Read32(ctx, ram, 0x004997F0u);
            bool isSubFrame = CurrentFrameTicks30Hz == 0;

            if (isSubFrame)
            {
                // Setting 0x004997F0 to 0 causes sub_0029F598's (++v0 % 6) at 0x0029F71C to evaluate case 1 (idle no-op),
                // preventing HUDTarget, PLDTarget, and MOTTarget::UpdateMotives from ticking on non-30Hz sub-frames.
                Ps2Memory.Write32(ctx, ram, 0x004997F0u, 0u);
            }

            RuntimeDispatcher.Call(0x0029F598u, ctx, ram);

            if (isSubFrame)
            {
                Ps2Memory.Write32(ctx, ram, 0x004997F0u, savedCycle);
            }

            int postHideCounter = (int)Ps2Memory.Read32(ctx, ram, h2dPtr + 592u);
            if (postHideCounter == 6)
            {
                // Newly armed or refreshed to 6 (0.20s) by sub_002A0408 during UpdateSpeedControls
                _hudSpeedHideTimers[h2dPtr] = 6.0f / 30.0f;
                Ps2Memory.Write32(ctx, ram, h2dPtr + 592u, 6u);
            }
            else if (preHideCounter > 0)
            {
                if (!_hudSpeedHideTimers.TryGetValue(h2dPtr, out float rem))
                    rem = preHideCounter / 30.0f;

                rem -= ContinuousDtSeconds;
                if (rem > 0f)
                {
                    _hudSpeedHideTimers[h2dPtr] = rem;
                    int scaledFrames = Math.Max(2, (int)MathF.Ceiling(rem * 30.0f));
                    Ps2Memory.Write32(ctx, ram, h2dPtr + 592u, (uint)scaledFrames);
                }
                else
                {
                    _hudSpeedHideTimers.Remove(h2dPtr);
                    if (postHideCounter != 0)
                    {
                        // Let the next frame perform the 1 -> 0 transition and invoke sub_002A0408(h2dPtr, 0)
                        Ps2Memory.Write32(ctx, ram, h2dPtr + 592u, 1u);
                    }
                }
            }
            else
            {
                _hudSpeedHideTimers.Remove(h2dPtr);
            }
        });

        // =====================================================================
        // PILLAR 5B: ELiveMode Frame Countdowns (sub_00268970 & sub_00268B68)
        // =====================================================================
        // 1. sub_00268970 (at 0x00268A84) decrements 164($s0) by -1 every render frame when 160($s0) != 0.
        //    Gating the decrement to 30 Hz ticks preserves exact live-mode camera/transition timing.
        RuntimeDispatcher.RegisterHook(0x00268970u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers || CurrentFrameTicks30Hz > 0)
            {
                RuntimeDispatcher.Call(0x00268970u, ctx, ram);
                return;
            }

            // On sub-frames (CurrentFrameTicks30Hz == 0), preserve any active SimStage+164 countdown
            uint stageListObj = Ps2Memory.Read32(ctx, ram, 0x0050A6F8u);
            uint simStage = 0u;
            uint saved164 = 0u;
            if (stageListObj != 0)
            {
                uint begin = Ps2Memory.Read32(ctx, ram, stageListObj);
                uint end = Ps2Memory.Read32(ctx, ram, stageListObj + 4u);
                for (uint p = begin; p < end && p - begin < 80u; p += 4u)
                {
                    uint st = Ps2Memory.Read32(ctx, ram, p);
                    if (st != 0 && Ps2Memory.Read32(ctx, ram, st + 28u) == 0x53494D53u) // "SIMS"
                    {
                        simStage = st;
                        saved164 = Ps2Memory.Read32(ctx, ram, st + 164u);
                        if (saved164 > 0u && Ps2Memory.Read32(ctx, ram, st + 160u) != 0u)
                        {
                            // Pre-increment by +1 so the sub-frame's -1 leaves it unchanged and > 0
                            Ps2Memory.Write32(ctx, ram, st + 164u, saved164 + 1u);
                        }
                        break;
                    }
                }
            }

            RuntimeDispatcher.Call(0x00268970u, ctx, ram);
        });

        // 2. sub_00268B68 (at 0x00268BCC) decrements 12($s1) by -1 every render frame during live-mode
        //    camera/UI state transitions (initialized to 6 frames at 0x00268D4C or 3 frames at 0x00268DCC).
        RuntimeDispatcher.RegisterHook(0x00268B68u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers || CurrentFrameTicks30Hz > 0)
            {
                RuntimeDispatcher.Call(0x00268B68u, ctx, ram);
                return;
            }

            uint liveModePtr = ctx->GetU32(4);
            uint pre12 = Ps2Memory.Read32(ctx, ram, liveModePtr + 12u);
            RuntimeDispatcher.Call(0x00268B68u, ctx, ram);
            uint post12 = Ps2Memory.Read32(ctx, ram, liveModePtr + 12u);
            // If sub_00268B68 decremented 12($s1) on a non-30Hz sub-frame (and didn't reset it to 0/3/6),
            // restore pre12 so the state machine steps at 30 Hz.
            if (pre12 > 1u && post12 == pre12 - 1u)
            {
                Ps2Memory.Write32(ctx, ram, liveModePtr + 12u, pre12);
            }
        });

        // =====================================================================
        // PILLAR 5C: 14-Dot Loading Spinner Comet Fade (sub_00338948)
        // =====================================================================
        // sub_00338948 advances the active dot every 0.75s using $f12 (dt), but subtracts a hardcoded
        // 0.005f (0x3BA3D70A) per render frame from all 14 dot alphas (968($s0)..1020($s0)) at 0x003389CC.
        // We rescale the fade delta to 0.005f * (dt * 30.0f) = 0.15f * dt so the trail length is identical at any FPS.
        RuntimeDispatcher.RegisterHook(0x00338948u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x00338948u, ctx, ram);
                return;
            }

            uint spinnerPtr = ctx->GetU32(4);
            float dtArg = BitConverter.UInt32BitsToSingle(ctx->GetFpuBits(12));
            uint preIdx = Ps2Memory.Read32(ctx, ram, spinnerPtr + 964u);
            Span<float> preDots = stackalloc float[14];
            for (int i = 0; i < 14; i++)
            {
                preDots[i] = Ps2Memory.ReadF32(ctx, ram, spinnerPtr + 968u + (uint)(i * 4));
            }

            RuntimeDispatcher.Call(0x00338948u, ctx, ram);

            uint postIdx = Ps2Memory.Read32(ctx, ram, spinnerPtr + 964u);
            float effectiveDt = dtArg > 0f ? Math.Clamp(dtArg, 0f, 1f / 15f) : ContinuousDtSeconds;
            float fadeStep = 0.005f * (effectiveDt * 30.0f);
            for (int i = 0; i < 14; i++)
            {
                float baseAlpha = (postIdx != preIdx && i == (int)preIdx) ? 1.0f : preDots[i];
                float newAlpha = MathF.Max(0.0f, baseAlpha - fadeStep);
                Ps2Memory.WriteF32(ctx, ram, spinnerPtr + 968u + (uint)(i * 4), newAlpha);
            }
        });

        // =====================================================================
        // PILLAR 5D: Sub-Pixel Cursor Movement Accumulation (CursorMovementTracker @ 0x0014F1F8)
        // =====================================================================
        // Original sub_0014F1F8 (used by Island Map LSMTarget and Build/Buy cursor) truncates
        // (speed * stick * dt) directly to integer pixels via cvtws.s (0x0014F344/4C) and discards
        // fractional pixels every frame. At >400 FPS (e.g. 5,000 FPS uncapped) or gentle stick tilts,
        // 400 px/s * dt < 1.0 px -> truncates to 0 every frame and the cursor freezes completely.
        RuntimeDispatcher.RegisterHook(0x0014F1F8u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x0014F1F8u, ctx, ram);
                return;
            }

            uint ra = ctx->GetU32(31);
            uint trackerPtr = ctx->GetU32(4);

            // Offset +40: flag indicating controller-cursor mode instead of joystick mode
            if (Ps2Memory.Read32(ctx, ram, trackerPtr + 40u) != 0u)
            {
                RuntimeDispatcher.OnReturn(ctx, ra);
                return;
            }

            float curSpeed = Ps2Memory.ReadF32(ctx, ram, trackerPtr + 8u);
            if (curSpeed < 0f)
            {
                curSpeed = 0f;
                Ps2Memory.WriteF32(ctx, ram, trackerPtr + 8u, 0f);
            }

            float dt = Ps2Memory.ReadF32(ctx, ram, 0x0049FDC4u);
            if (dt < 0f)
                dt = 0f;

            float stickX = Ps2Memory.ReadF32(ctx, ram, trackerPtr + 44u);
            float stickY = Ps2Memory.ReadF32(ctx, ram, trackerPtr + 48u);
            float mag = MathF.Sqrt(stickX * stickX + stickY * stickY);
            if (mag > 1.0f)
                mag = 1.0f;

            float maxSpeed = Ps2Memory.ReadF32(ctx, ram, trackerPtr + 0u);
            float targetSpeed = maxSpeed * mag;
            const float accel = 800.0f; // 0x44480000

            float midSpeed = MathF.Min(curSpeed + dt * 0.5f * accel, targetSpeed);
            float nextSpeed = MathF.Min(curSpeed + dt * accel, targetSpeed);
            Ps2Memory.WriteF32(ctx, ram, trackerPtr + 8u, nextSpeed);

            if (mag <= 0.001f || midSpeed <= 0f)
            {
                _cursorSubPixelRemainders.Remove(trackerPtr);
                ctx->SetU32(4, trackerPtr);
                ctx->SetU32(5, 0u);
                ctx->SetU32(6, 0u);
                RuntimeDispatcher.Call(0x00177068u, ctx, ram);
                RuntimeDispatcher.OnReturn(ctx, ra);
                return;
            }

            // UpdateCurrentFuzzyEdgeInfluence (0x00177068) normalizes (a1, -a2) as a direction vector
            int dirX = (int)MathF.Round(stickX * 1000.0f);
            int dirY = (int)MathF.Round(stickY * 1000.0f);
            ctx->SetU32(4, trackerPtr);
            ctx->SetU32(5, unchecked((uint)dirX));
            ctx->SetU32(6, unchecked((uint)dirY));
            RuntimeDispatcher.Call(0x00177068u, ctx, ram);

            float edgeInfluence = Ps2Memory.ReadF32(ctx, ram, trackerPtr + 4u);
            float scale = Math.Clamp(1.0f - edgeInfluence, 0f, 1f);
            float stepPx = midSpeed * dt * scale;

            _cursorSubPixelRemainders.TryGetValue(trackerPtr, out var rem);
            float accumX = rem.RemX + stepPx * stickX;
            float accumY = rem.RemY + stepPx * stickY;

            int moveX = Math.Clamp((int)MathF.Truncate(accumX), -50, 50);
            int moveY = Math.Clamp((int)MathF.Truncate(accumY), -50, 50);
            _cursorSubPixelRemainders[trackerPtr] = (accumX - moveX, accumY - moveY);

            if (moveX != 0 || moveY != 0)
            {
                int curX = (int)Ps2Memory.Read32(ctx, ram, trackerPtr + 32u);
                int curY = (int)Ps2Memory.Read32(ctx, ram, trackerPtr + 36u);
                Ps2Memory.Write32(ctx, ram, trackerPtr + 32u, unchecked((uint)(curX + moveX)));
                Ps2Memory.Write32(ctx, ram, trackerPtr + 36u, unchecked((uint)(curY - moveY)));
            }

            RuntimeDispatcher.OnReturn(ctx, ra);
        });

        // =====================================================================
        // PILLAR 5E: Camera Orbit Rotation & Zoom (ESimsCam::HandleRotation @ 0x00308DA0 & HandleZoom @ 0x003091B8)
        // =====================================================================
        // 1. ESimsCam::HandleRotation (0x00308DA0) multiplies stickX*|stickX| * dt * rotSpeed, then compares
        //    fabsf(deltaAngle) > 0.5f degrees PER FRAME (0x00308F78..0x00308F8C). Above 120 FPS partial stick
        //    deflection drops below 0.5 deg/frame (sluggish), and at 200-240+ FPS even full stick deflection
        //    falls <= 0.5 deg/frame so camera rotation stops completely!
        //    Fix: At >30 FPS (dt < 1/30s), evaluate HandleRotation with 1/30s dt so the 0.5-deg deadzone and
        //    Build/Buy 90-deg snap behave identically to native 30 FPS, then rescale continuous Live-Mode
        //    angular/target delta by (realDt * 30.0f) for silky-smooth 1.0x speed at any framerate.
        RuntimeDispatcher.RegisterHook(0x00308DA0u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x00308DA0u, ctx, ram);
                return;
            }

            float savedDt = Ps2Memory.ReadF32(ctx, ram, 0x0049FDC4u);
            if (savedDt <= 0f || savedDt >= (1.0f / 30.0f))
            {
                RuntimeDispatcher.Call(0x00308DA0u, ctx, ram);
                return;
            }

            uint camPtr = ctx->GetU32(4);
            float preRotAng = Ps2Memory.ReadF32(ctx, ram, camPtr + 1804u);
            float preAutoRot = Ps2Memory.ReadF32(ctx, ram, camPtr + 1892u);
            float preTargetX = Ps2Memory.ReadF32(ctx, ram, camPtr + 1788u);
            float preTargetY = Ps2Memory.ReadF32(ctx, ram, camPtr + 1792u);
            float preTargetZ = Ps2Memory.ReadF32(ctx, ram, camPtr + 1796u);

            Ps2Memory.WriteF32(ctx, ram, 0x0049FDC4u, 1.0f / 30.0f);
            RuntimeDispatcher.Call(0x00308DA0u, ctx, ram);
            Ps2Memory.WriteF32(ctx, ram, 0x0049FDC4u, savedDt);

            uint moved = ctx->GetU32(2); // $v0
            uint camMode = Ps2Memory.Read32(ctx, ram, camPtr + 1780u);
            if (moved != 0u && camMode != 3u)
            {
                float postAutoRot = Ps2Memory.ReadF32(ctx, ram, camPtr + 1892u);
                // If preAutoRot != 0 and 0x00308F6C did NOT clear 1892($s0), rotation came purely
                // from HandlePlayerRotation auto-follow (not dt-scaled stick input); leave untouched.
                bool pureAutoFollow = preAutoRot != 0f && postAutoRot == preAutoRot;
                if (!pureAutoFollow)
                {
                    float scale = savedDt * 30.0f;
                    float postRotAng = Ps2Memory.ReadF32(ctx, ram, camPtr + 1804u);
                    float rawDelta = postRotAng - preRotAng;
                    if (rawDelta > 180.0f) rawDelta -= 360.0f;
                    else if (rawDelta < -180.0f) rawDelta += 360.0f;

                    float trueRotAng = preRotAng + rawDelta * scale;
                    while (trueRotAng >= 360.0f) trueRotAng -= 360.0f;
                    while (trueRotAng < 0.0f) trueRotAng += 360.0f;
                    Ps2Memory.WriteF32(ctx, ram, camPtr + 1804u, trueRotAng);

                    // Scale any no-camera-zone target shift proportionally so wall collision never jitters
                    float postTargetX = Ps2Memory.ReadF32(ctx, ram, camPtr + 1788u);
                    float postTargetY = Ps2Memory.ReadF32(ctx, ram, camPtr + 1792u);
                    float postTargetZ = Ps2Memory.ReadF32(ctx, ram, camPtr + 1796u);
                    if (postTargetX != preTargetX || postTargetY != preTargetY || postTargetZ != preTargetZ)
                    {
                        Ps2Memory.WriteF32(ctx, ram, camPtr + 1788u, preTargetX + (postTargetX - preTargetX) * scale);
                        Ps2Memory.WriteF32(ctx, ram, camPtr + 1792u, preTargetY + (postTargetY - preTargetY) * scale);
                        Ps2Memory.WriteF32(ctx, ram, camPtr + 1796u, preTargetZ + (postTargetZ - preTargetZ) * scale);
                    }
                }
            }
        });

        // 2. ESimsCam::HandleZoom (0x003091B8) reads zoomSpeed from modeParams+132 (0x00309210) and applies
        //    zoom -= stickY * zoomSpeed WITHOUT multiplying by dt at all (8x too fast at 240 FPS, turning
        //    tiny incidental stick Y bleed into aggressive zooming). Temporarily scale modeParams+132 by
        //    (ContinuousDtSeconds * 30.0f) across the call so zoom & tilt speed match PS2 30 FPS identically.
        RuntimeDispatcher.RegisterHook(0x003091B8u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x003091B8u, ctx, ram);
                return;
            }

            uint camPtr = ctx->GetU32(4);
            uint modeParamsPtr = Ps2Memory.Read32(ctx, ram, camPtr);
            if (modeParamsPtr == 0u)
            {
                RuntimeDispatcher.Call(0x003091B8u, ctx, ram);
                return;
            }

            float origZoomSpeed = Ps2Memory.ReadF32(ctx, ram, modeParamsPtr + 132u);
            float dtScale = Math.Clamp(ContinuousDtSeconds * 30.0f, 1e-5f, 2.0f);
            Ps2Memory.WriteF32(ctx, ram, modeParamsPtr + 132u, origZoomSpeed * dtScale);
            RuntimeDispatcher.Call(0x003091B8u, ctx, ram);
            Ps2Memory.WriteF32(ctx, ram, modeParamsPtr + 132u, origZoomSpeed);
        });

        // =====================================================================
        // PILLAR 5F: Island Map Spritesheet & Icon Animations (LSMTarget::Update @ 0x002F41D8)
        // =====================================================================
        // LSMTarget::Update increments m_frameCounter (312($s3)) by +1 every render frame (0x002F4208):
        // - Warmup delay: skips update while m_frameCounter < 8 (0.267s @ 30 Hz)
        // - Plumbbob icon spritesheet (0x0049A4C0 "LSM_plumbbob"): advances every 8 frames ((cnt >> 3) % 4)
        // - Lit Tiki torch spritesheet (0x0049A4D0): flickers every 4 frames ((cnt & 3) == 0)
        // - MoodMeter pulse (0x002F4A40): fires every 30 frames (cnt % 30 == 4) while cnt < 300
        // On non-30Hz sub-frames, we keep cursor/scrolling running at full FPS while holding 312($s3)
        // at a neutral value (8 -> +1 = 9: passes >= 8, fails (cnt & 3) == 0, fails cnt % 30 == 4)
        // and restoring the true 30 Hz counter afterward.
        RuntimeDispatcher.RegisterHook(0x002F41D8u, (ctx, ram) =>
        {
            if (_runtime == null || !_runtime.Mmio.UseRealTimeTimers)
            {
                RuntimeDispatcher.Call(0x002F41D8u, ctx, ram);
                return;
            }

            uint lsmPtr = ctx->GetU32(4);
            int origCounter = (int)Ps2Memory.Read32(ctx, ram, lsmPtr + 312u);

            if (CurrentFrameTicks30Hz == 0)
            {
                // Sub-frame: keep cursor/UI active if already past warmup, without advancing sprite timers
                Ps2Memory.Write32(ctx, ram, lsmPtr + 312u, origCounter >= 8 ? 8u : 0u);
                RuntimeDispatcher.Call(0x002F41D8u, ctx, ram);
                Ps2Memory.Write32(ctx, ram, lsmPtr + 312u, unchecked((uint)origCounter));
                return;
            }

            RuntimeDispatcher.Call(0x002F41D8u, ctx, ram);
            if (CurrentFrameTicks30Hz > 1 && origCounter >= 8)
            {
                // Heavy frame (<30 FPS): advance extra logical 30 Hz ticks so map icons don't slow down
                int after = (int)Ps2Memory.Read32(ctx, ram, lsmPtr + 312u);
                Ps2Memory.Write32(ctx, ram, lsmPtr + 312u, unchecked((uint)(after + (CurrentFrameTicks30Hz - 1))));
            }
        });
    }

    public void AttachInputEvents(IInputContext input)
    {
        foreach (var mouse in input.Mice)
        {
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnMouseScroll;
        }

        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == Key.F8)
        {
            if (_graphics != null)
            {
                int maxSamples = Math.Max(1, _graphics.OpenGl.MaxSupportedMsaaSamples);
                int cur = Options.MsaaSamples <= 1 ? 1 : Options.MsaaSamples;
                int next = cur switch
                {
                    1 => maxSamples >= 2 ? 2 : 1,
                    2 => maxSamples >= 4 ? 4 : 1,
                    4 => maxSamples >= 8 ? 8 : 1,
                    _ => 1
                };
                Options.MsaaSamples = next;
                _graphics.OpenGl.SetMsaaSamples(next);
                Console.WriteLine($"\n[Castaway.Overrides] >>> MSAA toggled to: {(next <= 1 ? "Off" : $"{next}x")} <<<\n");
            }
        }
        else if (key == Key.F9)
        {
            if (_graphics != null)
            {
                _graphics.Mode = _graphics.Mode == GsRendererMode.OpenGl ? GsRendererMode.Software : GsRendererMode.OpenGl;
                Options.RendererMode = _graphics.Mode;
                Console.WriteLine($"\n[Castaway.Overrides] >>> Renderer Mode toggled to: {_graphics.Mode} <<<\n");
            }
        }
        else if (key == Key.F10)
        {
            Options.TargetFps = Options.TargetFps switch
            {
                30 => 60,
                60 => 120,
                120 => 0,
                _ => 30
            };
            Console.WriteLine($"\n[Castaway.Overrides] >>> Target FPS toggled to: {(Options.TargetFps == 0 ? "Uncapped" : $"{Options.TargetFps} FPS")} <<<\n");
        }
        else if (key == Key.F11)
        {
            OnSnapshotRequested?.Invoke();
        }
        else if (key == Key.F12)
        {
            OnRecordInputsToggleRequested?.Invoke();
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (!_hasLastMousePos)
        {
            _lastMousePos = pos;
            _hasLastMousePos = true;
            return;
        }

        Vector2 delta = pos - _lastMousePos;
        _lastMousePos = pos;

        // Only rotate camera when right mouse button is held or cursor is captured
        if (mouse.IsButtonPressed(MouseButton.Right) || mouse.Cursor.CursorMode == CursorMode.Raw)
        {
            _pendingMouseDeltaX += delta.X * Options.MouseSensitivity;
            _pendingMouseDeltaY += delta.Y * Options.MouseSensitivity;
        }
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel wheel)
    {
        _pendingScrollDelta += wheel.Y;
    }

    /// <summary>
    /// Updates PadService from Silk.NET input (Xbox controller + Keyboard) and overlays
    /// Phase 6 mouse-delta right-stick camera rotation &amp; scroll-wheel zoom.
    /// </summary>
    public void UpdateInput(PadService padService, IInputContext? input, byte* ram)
    {
        padService.UpdateFromSilkInput(input, ram);

        if (!Options.EnableModernControls || input == null)
            return;

        var state = padService.GetPortState(0);

        if (input.Mice.Count > 0)
        {
            var mouse = input.Mice[0];
            if (mouse.IsButtonPressed(MouseButton.Left))
                state.PressedButtons |= Ps2PadButtons.Cross;
            if (mouse.IsButtonPressed(MouseButton.Middle))
                state.PressedButtons |= Ps2PadButtons.Triangle;
        }

        if (MathF.Abs(_pendingMouseDeltaX) > 0.001f || MathF.Abs(_pendingMouseDeltaY) > 0.001f)
        {
            float rx = Math.Clamp(_pendingMouseDeltaX, -1f, 1f);
            float ry = Math.Clamp(_pendingMouseDeltaY, -1f, 1f);
            state.RightStickX = (byte)Math.Clamp((int)MathF.Round((rx + 1f) * 127.5f), 0, 255);
            state.RightStickY = (byte)Math.Clamp((int)MathF.Round((ry + 1f) * 127.5f), 0, 255);
            _pendingMouseDeltaX = 0f;
            _pendingMouseDeltaY = 0f;
        }

        if (MathF.Abs(_pendingScrollDelta) > 0.01f)
        {
            // Scroll wheel maps to Right Stick Y (camera zoom in/out)
            float zoomAxis = _pendingScrollDelta > 0f ? -0.85f : 0.85f;
            state.RightStickY = (byte)Math.Clamp((int)MathF.Round((zoomAxis + 1f) * 127.5f), 0, 255);
            _pendingScrollDelta = 0f;
        }

        padService.SetPortState(0, state);
    }
}
