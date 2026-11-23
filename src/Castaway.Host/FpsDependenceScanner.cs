using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Castaway.Core;
using Castaway.Overrides;
using Castaway.Runtime;

namespace Castaway.Host;

/// <summary>
/// One recorded frame or 30 Hz input sample in a manual gameplay session.
/// Stores both the DualShock 2 pad state and the actual 576 kHz hardware timer ticks elapsed on that frame,
/// so variable-FPS play sessions (including framerate drops and freezes) can be replayed and normalized.
/// </summary>
public readonly record struct RecordedFrameInputSample(
    uint TimerTicks576kHz,
    Ps2PadButtons Buttons,
    byte LeftStickX,
    byte LeftStickY,
    byte RightStickX,
    byte RightStickY)
{
    public DualShock2State ToPadState() => new()
    {
        PressedButtons = Buttons,
        LeftStickX = LeftStickX,
        LeftStickY = LeftStickY,
        RightStickX = RightStickX,
        RightStickY = RightStickY
    };
}

/// <summary>
/// Mode 2 Interactive Input-Recorded Playthrough &amp; Multi-Rate Framerate-Dependence Diff Scanner.
/// - Records manual gameplay inputs from boot (or toggled live via F12) to a compact binary .inp file.
/// - Replays the recorded playthrough deterministically across:
///   1) Pass A: Native 30 FPS Baseline (19,200 timer ticks / frame = 33.333 ms)
///   2) Pass B: High/Variable Framerate (e.g. 120 FPS = 4,800 ticks/frame, or the exact recorded variable-FPS cadence)
/// - Compares .data/.bss (0x00490000..0x005C0000) across both runs, detects any 4:1 / frame-count scaling bugs,
///   and reports the exact GuestAddress, 30Hz vs High-FPS delta, and the GuestPC function that wrote to it.
/// </summary>
public sealed unsafe class FpsDependenceScanner
{
    private const uint FileMagic = 0x504E4943u; // "CINP"
    private const uint FileVersion = 1u;

    // Known intentional per-render-frame counters or RNG state that are expected to differ across frame rates
    private static readonly HashSet<uint> IgnoredAddresses =
    [
        0x0049FDB4u, // Odd/even render frame toggle in sub_003E2D98
        0x0049FDB8u, // _framecount (intentionally increments per render frame for SAnimator2 pose cache)
        0x0049FDBCu, // _vblankcount
        0x0049FDC0u, // _vblankdelta
        0x0049FDC4u, // _dt (per-frame delta seconds, naturally 1/30 vs 1/120)
        0x0049FDC8u, // _sim_dt (per-frame sim delta seconds)
        0x0049FDCCu, // _inv_dt (1.0f / _dt)
        0x0057B040u, // _dt history [0]
        0x0057B044u, // _dt history [1]
        0x0057B2D8u, // _dt_clamped30
        0x0057B2DCu, // _dt_clamped20
        0x0057B2E0u, // _dt_max15
        0x005294B0u, // Apt per-frame delta seconds
        0x005294BCu, // Apt per-frame delta ms
        0x0049A760u, // AptViewer per-frame delta seconds
        0x0049A700u, // Unused write-only render counter in sub_002FEAB0
        0x0049FE58u, // Unused write-only vblank stamp in EController::Update (0x004621D0)
        0x004A3F70u, // GS/VIF1 display packet queue render submission counter (sub_004281C8)
        0x00596DB0u, // GS/VIF1 display packet queue ring index
        0x00596DB8u, // GS/VIF1 display packet queue ring index
        0x00596DC0u, // GS/VIF1 display packet queue ring index
        0x00596DC8u, // GS/VIF1 display packet queue ring index
        0x00596DD0u, // GS/VIF1 display packet queue ring index
        0x00596DD8u, // GS/VIF1 display packet queue completed flip counter
        0x00596DE0u, // GS/VIF1 display packet queue submitted flip counter
        0x00596E3Cu, // GS/VIF1 display packet queue semaphore post counter
        0x00596E40u, // GS/VIF1 display packet queue flip counter
        0x004A41D4u, // EA Audio background thread (tid=4) IOP heartbeat mirror
        0x005AB540u, // EA Audio background thread (tid=4) IOP heartbeat counter
        0x004A4228u, // EA Audio background thread (tid=4) command counter
        0x005AA244u, // EA Audio background thread (tid=4) command ring index
        0x004A1B54u, // RenderWare frame arena linear allocator watermark
    ];

    private readonly List<RecordedFrameInputSample> _recordedSamples = new(4096);
    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }
    public int RecordedFrameCount => _recordedSamples.Count;
    public double RecordedTotalSeconds { get; private set; }

    public void StartRecording(string outputPath)
    {
        _recordedSamples.Clear();
        RecordedTotalSeconds = 0.0;
        OutputPath = outputPath;
        IsRecording = true;
        Console.WriteLine($"\n[FPS-DEPS RECORDER] >>> Started recording manual inputs & frame timings to '{outputPath}' (Press F12 or exit to save) <<<\n");
    }

    public void RecordFrame(uint timerTicks576kHz, DualShock2State padState)
    {
        if (!IsRecording)
            return;

        _recordedSamples.Add(new RecordedFrameInputSample(
            timerTicks576kHz,
            padState.PressedButtons,
            padState.LeftStickX,
            padState.LeftStickY,
            padState.RightStickX,
            padState.RightStickY));
        RecordedTotalSeconds += timerTicks576kHz / (double)MmioRouter.Timer1TicksPerSecond;
    }

    public void StopAndSave()
    {
        if (!IsRecording)
            return;

        IsRecording = false;
        if (string.IsNullOrEmpty(OutputPath) || _recordedSamples.Count == 0)
        {
            Console.WriteLine("[FPS-DEPS RECORDER] Recording stopped (0 frames captured).");
            return;
        }

        string fullPath = Path.GetFullPath(OutputPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);
        bw.Write(FileMagic);
        bw.Write(FileVersion);
        bw.Write(_recordedSamples.Count);
        foreach (var s in _recordedSamples)
        {
            bw.Write(s.TimerTicks576kHz);
            bw.Write((ushort)s.Buttons);
            bw.Write(s.LeftStickX);
            bw.Write(s.LeftStickY);
            bw.Write(s.RightStickX);
            bw.Write(s.RightStickY);
        }

        Console.WriteLine($"\n[FPS-DEPS RECORDER] >>> Saved {_recordedSamples.Count} frames ({RecordedTotalSeconds:F2}s of gameplay) to '{fullPath}' <<<");
        Console.WriteLine($"[FPS-DEPS RECORDER] >>> Run comparison with: dotnet run --project src/Castaway.Host -- --scan-fps-deps \"{fullPath}\" <<<\n");
    }

    public static List<RecordedFrameInputSample> LoadRecording(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        uint magic = br.ReadUInt32();
        if (magic != FileMagic)
            throw new InvalidDataException($"Invalid input recording magic 0x{magic:X8} in '{path}'.");
        uint ver = br.ReadUInt32();
        if (ver != FileVersion)
            throw new InvalidDataException($"Unsupported input recording version {ver} in '{path}'.");

        int count = br.ReadInt32();
        var list = new List<RecordedFrameInputSample>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            uint ticks = br.ReadUInt32();
            var buttons = (Ps2PadButtons)br.ReadUInt16();
            byte lx = br.ReadByte();
            byte ly = br.ReadByte();
            byte rx = br.ReadByte();
            byte ry = br.ReadByte();
            list.Add(new RecordedFrameInputSample(ticks, buttons, lx, ly, rx, ry));
        }
        return list;
    }

    /// <summary>
    /// Converts a variable-framerate recorded input stream into a canonical 30 Hz timeline
    /// (19,200 ticks = 33.333 ms per 30 Hz slice), OR-latching short button presses so no button tap
    /// is ever dropped when replaying at 30 FPS vs 120 FPS vs variable FPS.
    /// </summary>
    public static List<DualShock2State> NormalizeTo30HzTimeline(IReadOnlyList<RecordedFrameInputSample> rawSamples)
    {
        const uint TicksPer30Hz = 19_200u;
        var slices = new List<DualShock2State>();
        if (rawSamples.Count == 0)
            return slices;

        uint accumTicks = 0u;
        Ps2PadButtons latchedButtons = Ps2PadButtons.None;
        DualShock2State latestState = rawSamples[0].ToPadState();

        foreach (var sample in rawSamples)
        {
            uint step = sample.TimerTicks576kHz > 0 ? sample.TimerTicks576kHz : 9_600u;
            latestState = sample.ToPadState();
            latchedButtons |= sample.Buttons;
            accumTicks += step;

            while (accumTicks >= TicksPer30Hz)
            {
                accumTicks -= TicksPer30Hz;
                var sliceState = latestState;
                sliceState.PressedButtons |= latchedButtons;
                slices.Add(sliceState);
                latchedButtons = sample.Buttons; // Carry currently held buttons into next slice
            }
        }

        if (slices.Count == 0)
        {
            var sliceState = latestState;
            sliceState.PressedButtons |= latchedButtons;
            slices.Add(sliceState);
        }

        return slices;
    }

    /// <summary>
    /// Expands a 30 Hz canonical input timeline into a sub-stepped schedule (e.g. subSteps=1 for 30 FPS,
    /// subSteps=4 for 120 FPS, or variable jittered sub-steps to simulate PS2/PC framerate drops).
    /// Total elapsed 576 kHz ticks across the entire schedule is 100% identical across all passes.
    /// </summary>
    public static List<RecordedFrameInputSample> BuildPassSchedule(
        IReadOnlyList<DualShock2State> timeline30Hz,
        int subStepsPer30Hz,
        bool simulateVariableFpsDrops = false)
    {
        const uint TicksPer30Hz = 19_200u;
        var schedule = new List<RecordedFrameInputSample>(timeline30Hz.Count * Math.Max(1, subStepsPer30Hz));

        for (int i = 0; i < timeline30Hz.Count; i++)
        {
            var pad = timeline30Hz[i];
            if (simulateVariableFpsDrops)
            {
                // Alternate between 120 FPS bursts (4x 4800), 60 FPS (2x 9600), 40 FPS + 120 FPS (14400 + 4800),
                // and 30 FPS heavy frames (1x 19200) while summing to exactly 19,200 ticks per 30 Hz slice!
                switch (i % 4)
                {
                    case 0: // 120 FPS (4 frames @ 8.33 ms)
                        for (int s = 0; s < 4; s++)
                            schedule.Add(new RecordedFrameInputSample(4_800u, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                        break;
                    case 1: // 60 FPS (2 frames @ 16.67 ms)
                        for (int s = 0; s < 2; s++)
                            schedule.Add(new RecordedFrameInputSample(9_600u, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                        break;
                    case 2: // Mixed 40 FPS hitch + 120 FPS recovery (25.0 ms + 8.33 ms = 33.33 ms)
                        schedule.Add(new RecordedFrameInputSample(14_400u, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                        schedule.Add(new RecordedFrameInputSample(4_800u, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                        break;
                    default: // 30 FPS heavy frame (1 frame @ 33.33 ms)
                        schedule.Add(new RecordedFrameInputSample(TicksPer30Hz, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                        break;
                }
            }
            else
            {
                int steps = Math.Max(1, subStepsPer30Hz);
                uint baseTicks = TicksPer30Hz / (uint)steps;
                uint remTicks = TicksPer30Hz - baseTicks * (uint)steps;
                for (int s = 0; s < steps; s++)
                {
                    uint t = baseTicks + (s == steps - 1 ? remTicks : 0u);
                    schedule.Add(new RecordedFrameInputSample(t, pad.PressedButtons, pad.LeftStickX, pad.LeftStickY, pad.RightStickX, pad.RightStickY));
                }
            }
        }

        return schedule;
    }

    public sealed class PassCaptureResult
    {
        public required string PassName { get; init; }
        public required int TotalFramesExecuted { get; init; }
        public required uint TotalTimerTicks { get; init; }
        public required byte[] InitialDataBss { get; init; }
        public required byte[] FinalDataBss { get; init; }
        public required uint[] WriterPcTable { get; init; }
    }

    public static void CompareAndReport(PassCaptureResult baseline30, PassCaptureResult highRatePass)
    {
        const uint BaseAddr = RuntimeDispatcher.DataWatchBase;
        const uint ByteLen = RuntimeDispatcher.DataWatchLimit - RuntimeDispatcher.DataWatchBase;
        int wordCount = (int)(ByteLen >> 2);

        double frameRatio = baseline30.TotalFramesExecuted > 0
            ? (double)highRatePass.TotalFramesExecuted / baseline30.TotalFramesExecuted
            : 4.0;

        Console.WriteLine("\n================================================================================");
        Console.WriteLine($"[FPS-DEPS SCAN REPORT] {baseline30.PassName} ({baseline30.TotalFramesExecuted} frames) vs {highRatePass.PassName} ({highRatePass.TotalFramesExecuted} frames)");
        Console.WriteLine($"  Simulated Gameplay Time : {baseline30.TotalTimerTicks / (double)MmioRouter.Timer1TicksPerSecond:F3}s ({baseline30.TotalTimerTicks} ticks vs {highRatePass.TotalTimerTicks} ticks)");
        Console.WriteLine($"  Frame Count Ratio       : {frameRatio:F2}x");
        Console.WriteLine("--------------------------------------------------------------------------------");

        var findings = new List<(double Score, string Line)>();

        for (int i = 0; i < wordCount; i++)
        {
            uint addr = BaseAddr + (uint)(i * 4);
            if (IgnoredAddresses.Contains(addr))
                continue;

            int off = i * 4;
            uint init30 = BinaryPrimitives.ReadUInt32LittleEndian(baseline30.InitialDataBss.AsSpan(off, 4));
            uint end30 = BinaryPrimitives.ReadUInt32LittleEndian(baseline30.FinalDataBss.AsSpan(off, 4));
            uint initHi = BinaryPrimitives.ReadUInt32LittleEndian(highRatePass.InitialDataBss.AsSpan(off, 4));
            uint endHi = BinaryPrimitives.ReadUInt32LittleEndian(highRatePass.FinalDataBss.AsSpan(off, 4));

            if (end30 == endHi)
                continue;

            uint writerPc = highRatePass.WriterPcTable[i] != 0 ? highRatePass.WriterPcTable[i] : baseline30.WriterPcTable[i];

            // Skip heap pointer addresses (0x00600000..0x01FFFFFF)
            bool looksLikeHeapPtr = (end30 >= 0x00600000u && end30 < 0x02000000u && (end30 & 3u) == 0) ||
                                    (endHi >= 0x00600000u && endHi < 0x02000000u && (endHi & 3u) == 0);
            if (looksLikeHeapPtr)
                continue;

            // Check if both values are valid normalized floats in [-1e6, 1e6]
            float fInit30 = BitConverter.UInt32BitsToSingle(init30);
            float fEnd30 = BitConverter.UInt32BitsToSingle(end30);
            float fInitHi = BitConverter.UInt32BitsToSingle(initHi);
            float fEndHi = BitConverter.UInt32BitsToSingle(endHi);

            bool isFloat = IsPlausibleFloat(init30) && IsPlausibleFloat(end30) && IsPlausibleFloat(endHi) &&
                           ((end30 & 0x7F800000u) != 0 || (endHi & 0x7F800000u) != 0);

            if (isFloat)
            {
                float df30 = fEnd30 - fInit30;
                float dfHi = fEndHi - fInitHi;
                float absDiff = MathF.Abs(fEndHi - fEnd30);
                if (absDiff <= 0.02f)
                    continue; // Normal IEEE-754 sub-stepping precision difference

                if (MathF.Abs(df30) >= 0.01f)
                {
                    float ratio = dfHi / df30;
                    if (ratio >= 1.45f && ratio <= frameRatio * 2.5f)
                    {
                        findings.Add((Math.Abs(ratio - frameRatio),
                            $"  [FLOAT {ratio,5:F2}x] Addr=0x{addr:X8} | Δ30={df30,+10:F4} (end={fEnd30:F4}) | ΔHi={dfHi,+10:F4} (end={fEndHi:F4}) | WriterPC=0x{writerPc:X8}"));
                    }
                }
            }
            else
            {
                long d30 = (int)end30 - (int)init30;
                long dHi = (int)endHi - (int)initHi;
                if (d30 != 0 && Math.Abs(d30) <= 100_000 && Math.Abs(dHi) <= 400_000)
                {
                    double ratio = (double)dHi / d30;
                    if (ratio >= 1.45 && ratio <= frameRatio * 2.5)
                    {
                        findings.Add((Math.Abs(ratio - frameRatio),
                            $"  [INT32 {ratio,5:F2}x] Addr=0x{addr:X8} | Δ30={d30,+10} (end={(int)end30}) | ΔHi={dHi,+10} (end={(int)endHi}) | WriterPC=0x{writerPc:X8}"));
                    }
                }
            }
        }

        if (findings.Count == 0)
        {
            Console.WriteLine("  [PASS] Zero frame-rate scaling bugs detected in .data/.bss (0x00490000..0x005C0000)!");
        }
        else
        {
            Console.WriteLine($"  Found {findings.Count} candidate address(es) scaling with frame count:");
            foreach (var item in findings.OrderBy(f => f.Score).Take(40))
            {
                Console.WriteLine(item.Line);
            }
        }

        // Also print explicit status of the key timing & UI globals we hooked
        PrintKeyGlobalsSummary(baseline30, highRatePass);
        Console.WriteLine("================================================================================\n");
    }

    private static void PrintKeyGlobalsSummary(PassCaptureResult b30, PassCaptureResult bHi)
    {
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("  Key Timing & Subsystem Globals Verification:");
        CheckFloatGlobal(b30, bHi, 0x0049FDE8u, "EEngine Total Elapsed Sec (0x0049FDE8)");
        CheckFloatGlobal(b30, bHi, 0x005294B4u, "Apt UI Total Elapsed Sec  (0x005294B4)");
        CheckUIntGlobal(b30, bHi,  0x005294C0u, "Apt UI Total Elapsed Ms   (0x005294C0)");
        CheckUIntGlobal(b30, bHi,  0x004997F0u, "H2DTarget 6-Frame Cycle   (0x004997F0)");
        CheckUIntGlobal(b30, bHi,  0x0049A778u, "Apt RepeatCount[Up]       (0x0049A778)");
        CheckUIntGlobal(b30, bHi,  0x0049A77Cu, "Apt RepeatCount[Down]     (0x0049A77C)");
        CheckUIntGlobal(b30, bHi,  0x00492908u, "FlowManager CurrentState  (0x00492908)");
    }

    private static void CheckFloatGlobal(PassCaptureResult b30, PassCaptureResult bHi, uint addr, string label)
    {
        int off = (int)(addr - RuntimeDispatcher.DataWatchBase);
        float v30 = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(b30.FinalDataBss.AsSpan(off, 4)));
        float vHi = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bHi.FinalDataBss.AsSpan(off, 4)));
        Console.WriteLine($"    {label,-40} : 30Hz={v30,10:F4} | HighFPS={vHi,10:F4} | Diff={vHi - v30,+8:F4}");
    }

    private static void CheckUIntGlobal(PassCaptureResult b30, PassCaptureResult bHi, uint addr, string label)
    {
        int off = (int)(addr - RuntimeDispatcher.DataWatchBase);
        uint v30 = BinaryPrimitives.ReadUInt32LittleEndian(b30.FinalDataBss.AsSpan(off, 4));
        uint vHi = BinaryPrimitives.ReadUInt32LittleEndian(bHi.FinalDataBss.AsSpan(off, 4));
        Console.WriteLine($"    {label,-40} : 30Hz={v30,10} | HighFPS={vHi,10} | Diff={(long)vHi - v30,+8}");
    }

    private static bool IsPlausibleFloat(uint bits)
    {
        if (bits == 0u)
            return true;
        uint exp = (bits >> 23) & 0xFFu;
        if (exp is < 100u or > 150u) // ~[1e-8, 8e6]
            return false;
        float f = BitConverter.UInt32BitsToSingle(bits);
        return !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
