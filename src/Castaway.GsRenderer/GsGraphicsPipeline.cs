using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Castaway.Core;
using Castaway.GsRenderer.Diagnostics;
using Castaway.GsRenderer.Gs;
using Castaway.GsRenderer.OpenGl;
using Castaway.GsRenderer.Vif;
using Castaway.GsRenderer.Vu1;
using Castaway.Runtime;

namespace Castaway.GsRenderer;

/// <summary>
/// Unified Phase 4 Graphics Pipeline connecting DMA Channels (VIF0 Ch0, VIF1 Ch1, GIF Ch2),
/// VIF0/VIF1 Unpackers, the VU1 Microcode AOT/Hash Recompiler, the 4 MB swizzled GS Processor,
/// and the Silk.NET OpenGL 4.6 DSA + Compute Shader Renderer.
/// </summary>
public sealed unsafe class GsGraphicsPipeline : IDisposable
{
    private GCHandle _vu1DataPin;
    private GCHandle _vu1MicroPin;

    public VifUnit Vif0 { get; }
    public VifUnit Vif1 { get; }
    public Vu1State Vu1 { get; }
    public Vu1Recompiler Vu1Compiler { get; }
    public GsProcessor Gs { get; }
    public GsOpenGlRenderer OpenGl { get; }

    public GsPrimitiveBatch BackBatch { get; private set; } = new();
    public GsPrimitiveBatch FrontBatch { get; private set; } = new();
    public byte[] FrontVram { get; } = new byte[GsSwizzle.VramByteSize];
    private readonly object _batchLock = new();
    public object BatchLock => _batchLock;

    public GsRendererMode Mode
    {
        get => Gs.Mode;
        set => Gs.Mode = value;
    }

    public GsGraphicsPipeline()
    {
        Vu1 = new Vu1State();
        _vu1DataPin = GCHandle.Alloc(Vu1.DataMem, GCHandleType.Pinned);
        _vu1MicroPin = GCHandle.Alloc(Vu1.MicroMem, GCHandleType.Pinned);
        RuntimeDispatcher.Vu1Data = (byte*)_vu1DataPin.AddrOfPinnedObject();
        RuntimeDispatcher.Vu1Micro = (byte*)_vu1MicroPin.AddrOfPinnedObject();

        Vu1Compiler = new Vu1Recompiler();
        Gs = new GsProcessor();
        Gs.ActiveBatch = BackBatch;
        OpenGl = new GsOpenGlRenderer();

        Vif0 = new VifUnit(isVif1: false);
        Vif1 = new VifUnit(isVif1: true, microMem: Vu1.MicroMem, dataMem: Vu1.DataMem);

        // Wire VIF1 -> VU1 Recompiler & GS Path 2 (DIRECT)
        Vif1.OnMicroMemoryRangeModified = Vu1Compiler.InvalidateMicroMemRange;
        Vif1.OnDirectGifData = data => Gs.SubmitGifPacket(data, "VIF1_DIRECT");
        bool logVu1Skin = Environment.GetEnvironmentVariable("CASTAWAY_LOG_VU1_SKIN") == "1";
        int loggedSkinCalls = 0;
        FrameProfileRecorder? lastSnapRecorder = null;
        int snapMat0010Calls = 0;
        int snapMat0108Calls = 0;
        int snap2E20Calls = 0;
        int snapSkinCalls = 0;
        Vif1.OnMicroProgramCall = (startByteAddr, isContinue) =>
        {
            Vu1.Top = Vif1.Top;
            Vu1.Itop = Vif1.Itop;
            ushort entryAddr = isContinue ? Vu1.Tpc : startByteAddr;
            var recorder = Gs.FrameRecorder;
            if (!ReferenceEquals(recorder, lastSnapRecorder))
            {
                lastSnapRecorder = recorder;
                snapMat0010Calls = 0;
                snapMat0108Calls = 0;
                snap2E20Calls = 0;
                snapSkinCalls = 0;
            }

            bool wantSnap2E20 = recorder != null && !isContinue && entryAddr == 0x2E20 && snap2E20Calls < 6;
            bool wantSnapMat = recorder != null && !isContinue &&
                ((entryAddr == 0x0010 && snapMat0010Calls < 4) || (entryAddr == 0x0108 && snapMat0108Calls < 4));
            bool wantSnap = recorder != null && !isContinue &&
                (recorder.Vu1SkinDiagCount < 60 || wantSnap2E20 || wantSnapMat);
            bool wantEnv = logVu1Skin && !isContinue && loggedSkinCalls < 6 && Gs.CurrentFrame >= 10 && entryAddr is (0x29A8 or 0x2650);
            if ((!wantSnap && !wantEnv) || entryAddr is not (0x0010 or 0x0108 or 0x29A8 or 0x2650 or 0x2E20 or 0x2410 or 0x0D20))
            {
                Vu1Compiler.Execute(Vu1, entryAddr);
                return;
            }

            if (entryAddr is (0x0010 or 0x0108))
            {
                int topMat = Vu1.Top & 0x3FF;
                Vu1Compiler.Execute(Vu1, entryAddr);
                if (wantSnapMat && recorder != null)
                {
                    int matIdx = entryAddr == 0x0010 ? ++snapMat0010Calls : ++snapMat0108Calls;
                    uint maskMat = Vu1AotPrograms.IdentifyLoadedRegions(Vu1.MicroMem);
                    var sbMat = new StringBuilder(512);
                    sbMat.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VU1 MAT #{matIdx}] entry=0x{entryAddr:X4} mask=0x{maskMat:X2} top={topMat} P={Vu1.P:G6} Q={Vu1.Q:G6}"));
                    for (int r = 6; r <= 9; r++)
                    {
                        var q = Vu1.LoadQuadword(r);
                        sbMat.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"  bone0 dm[{r,2}]: ({q[0]:F6}, {q[1]:F6}, {q[2]:F6}, {q[3]:F6})"));
                    }
                    for (int r = 59; r <= 61; r++)
                    {
                        var q = Vu1.LoadQuadword(r);
                        float len = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]);
                        sbMat.AppendLine(string.Create(CultureInfo.InvariantCulture,
                            $"  nrmM  dm[{r,2}]: ({q[0]:F6}, {q[1]:F6}, {q[2]:F6}, {q[3]:F6}) len={len:F6}"));
                    }
                    recorder.AppendVu1SkinDiag(sbMat.ToString());
                }
                return;
            }

            if (entryAddr == 0x0D20)
            {
                int topLit = Vu1.Top & 0x3FF;
                int vtxLit = Vu1.LoadInt16(topLit, 0x8u) & 0xFFF;
                int outLit = Vu1.LoadInt16(0x48, 0x1u) & 0x3FF;
                Vu1Compiler.Execute(Vu1, entryAddr);
                if (wantSnap && recorder != null)
                {
                    uint maskLit = Vu1AotPrograms.IdentifyLoadedRegions(Vu1.MicroMem);
                    recorder.AppendVu1SkinDiag(string.Create(CultureInfo.InvariantCulture,
                        $"[VU1 0x0D20] mask=0x{maskLit:X2} top={topLit} vtx={vtxLit} out={outLit} vi15={Vu1.VI[15]}"));
                }
                return;
            }

            int top = Vu1.Top & 0x3FF;
            int vtxCount = Vu1.LoadInt16(top, 0x8u) & 0xFFF;
            int flags = Vu1.LoadInt16(0x39, 0x8u);
            int outBase = Vu1.LoadInt16(0x48, 0x1u) & 0x3FF;
            int stride = (flags & 0x4000) == 0 && entryAddr is (0x2650 or 0x2410) ? 9 : 5;
            int sampleCount = Math.Clamp(vtxCount, 0, 128);

            var prePos = new Vector128<float>[sampleCount];
            var preVi8 = new int[sampleCount];
            var preAdc = new bool[sampleCount];
            var preNorm = new Vector128<float>[sampleCount];
            var preW = new Vector128<float>[sampleCount];
            for (int k = 0; k < sampleCount; k++)
            {
                int vBase = (top + 1 + k * stride) & 0x3FF;
                prePos[k] = Vu1.LoadQuadword(vBase);
                int wField = BitConverter.SingleToInt32Bits(prePos[k][3]);
                preVi8[k] = wField & 31;
                preAdc[k] = (wField & 0x8000) != 0;
                preNorm[k] = Vu1.LoadQuadword(vBase + 1);
                preW[k] = Vu1.LoadQuadword(vBase + 4);
            }

            // Capture lighting & bone matrices before execution (in case microcode overwrites anything)
            var preLight = new Vector128<float>[11];
            for (int i = 0; i < 11; i++)
                preLight[i] = Vu1.LoadQuadword(46 + i); // dm[46..56]
            var preNormMat = new Vector128<float>[3];
            for (int i = 0; i < 3; i++)
                preNormMat[i] = Vu1.LoadQuadword(59 + i); // dm[59..61]

            Vu1Compiler.Execute(Vu1, entryAddr);

            uint regionMask = Vu1AotPrograms.IdentifyLoadedRegions(Vu1.MicroMem);
            int badVi8 = 0, zeroInN = 0, zeroOutN = 0, darkCount = 0, brightCount = 0;
            int minR = 255, maxR = 0;
            var darkIndices = new List<int>(8);
            var brightIndices = new List<int>(8);
            for (int k = 0; k < sampleCount; k++)
            {
                int vBase = (top + 1 + k * stride) & 0x3FF;
                var nOut = Vu1.LoadQuadword(vBase + 1);
                var cOut = Vu1.LoadQuadword((outBase + 4 + k * 3) & 0x3FF);
                int r = BitConverter.SingleToInt32Bits(cOut[0]);
                int g = BitConverter.SingleToInt32Bits(cOut[1]);
                int b = BitConverter.SingleToInt32Bits(cOut[2]);
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (preVi8[k] >= 16) badVi8++;
                int inNx = BitConverter.SingleToInt32Bits(preNorm[k][0]);
                int inNy = BitConverter.SingleToInt32Bits(preNorm[k][1]);
                int inNz = BitConverter.SingleToInt32Bits(preNorm[k][2]);
                if (inNx == 0 && inNy == 0 && inNz == 0) zeroInN++;
                bool isZeroOutN = MathF.Abs(nOut[0]) + MathF.Abs(nOut[1]) + MathF.Abs(nOut[2]) < 1e-4f;
                if (isZeroOutN) zeroOutN++;

                if (r <= 90 && g <= 90 && b <= 100)
                {
                    darkCount++;
                    if (darkIndices.Count < 8) darkIndices.Add(k);
                }
                else if (r > 100 || g > 100 || b > 100)
                {
                    brightCount++;
                    if (brightIndices.Count < 8) brightIndices.Add(k);
                }
            }

            bool interesting = darkCount > 0 || badVi8 > 0 || zeroInN > 0 || zeroOutN > 0 || snapSkinCalls < 10 || wantSnap2E20;
            if (!interesting && !wantEnv)
                return;

            var sb = new StringBuilder(4096);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"[VU1 SKIN f={Gs.CurrentFrame}] entry=0x{entryAddr:X4} mask=0x{regionMask:X2} top={top} vtx={vtxCount} stride={stride} flags=0x{flags:X4} out={outBase} dark={darkCount} bright={brightCount} badVi8={badVi8} zeroInN={zeroInN} zeroOutN={zeroOutN} R=[{minR}..{maxR}]"));

            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  Ambient dm[46]: ({preLight[0][0]:F3},{preLight[0][1]:F3},{preLight[0][2]:F3},{preLight[0][3]:F3})"));
            for (int l = 0; l < 4; l++)
            {
                var lc = preLight[1 + l];
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  LightCol[{l}] dm[{47 + l}]: ({lc[0]:F3},{lc[1]:F3},{lc[2]:F3},{lc[3]:F3})"));
            }
            for (int l = 0; l < 3; l++)
            {
                var ld = preLight[5 + l];
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  LightDir[{l}] dm[{51 + l}]: ({ld[0]:F4},{ld[1]:F4},{ld[2]:F4},{ld[3]:F4})"));
            }
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  Mat dm[55]: ({preLight[9][0]:F3},{preLight[9][1]:F3},{preLight[9][2]:F3},{preLight[9][3]:F3}) dm[56]: ({preLight[10][0]:F3},{preLight[10][1]:F3},{preLight[10][2]:F3},{preLight[10][3]:F3})"));
            for (int i = 0; i < 3; i++)
            {
                var nm = preNormMat[i];
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  NormMat[{i}] dm[{59 + i}]: ({nm[0]:F4},{nm[1]:F4},{nm[2]:F4},{nm[3]:F4})"));
            }

            for (int b = 0; b < 4; b++)
            {
                int q0 = 6 + b * 4;
                var c0 = Vu1.LoadQuadword(q0);
                var c1 = Vu1.LoadQuadword(q0 + 1);
                var c2 = Vu1.LoadQuadword(q0 + 2);
                var c3 = Vu1.LoadQuadword(q0 + 3);
                float l0 = MathF.Sqrt(c0[0] * c0[0] + c0[1] * c0[1] + c0[2] * c0[2]);
                float l1 = MathF.Sqrt(c1[0] * c1[0] + c1[1] * c1[1] + c1[2] * c1[2]);
                float l2 = MathF.Sqrt(c2[0] * c2[0] + c2[1] * c2[1] + c2[2] * c2[2]);
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  bone[{b}] dm[{q0}..{q0 + 3}] lens=({l0:F5},{l1:F5},{l2:F5}) r0=({c0[0]:F5},{c0[1]:F5},{c0[2]:F5}) r1=({c1[0]:F5},{c1[1]:F5},{c1[2]:F5}) r2=({c2[0]:F5},{c2[1]:F5},{c2[2]:F5}) r3=({c3[0]:F5},{c3[1]:F5},{c3[2]:F5})"));
            }

            void AppendVertList(string label, List<int> indices)
            {
                foreach (int idx in indices)
                {
                    int vBase = (top + 1 + idx * stride) & 0x3FF;
                    var p = prePos[idx];
                    var nIn = preNorm[idx];
                    var nOut = Vu1.LoadQuadword(vBase + 1);
                    var w = preW[idx];
                    var cOut = Vu1.LoadQuadword((outBase + 4 + idx * 3) & 0x3FF);
                    int r = BitConverter.SingleToInt32Bits(cOut[0]);
                    int g = BitConverter.SingleToInt32Bits(cOut[1]);
                    int b = BitConverter.SingleToInt32Bits(cOut[2]);
                    int a = BitConverter.SingleToInt32Bits(cOut[3]);
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {label} v[{idx,3}] vi8={preVi8[idx],2} adc={(preAdc[idx] ? 1 : 0)} posI=({BitConverter.SingleToInt32Bits(p[0])},{BitConverter.SingleToInt32Bits(p[1])},{BitConverter.SingleToInt32Bits(p[2])}) posF=({p[0]:F2},{p[1]:F2},{p[2]:F2}) inN_I=({BitConverter.SingleToInt32Bits(nIn[0])},{BitConverter.SingleToInt32Bits(nIn[1])},{BitConverter.SingleToInt32Bits(nIn[2])},w={BitConverter.SingleToInt32Bits(nIn[3])}) outN=({nOut[0]:F3},{nOut[1]:F3},{nOut[2]:F3}) wInt=({BitConverter.SingleToInt32Bits(w[0])},{BitConverter.SingleToInt32Bits(w[1])},{BitConverter.SingleToInt32Bits(w[2])},{BitConverter.SingleToInt32Bits(w[3])}) RGB=({r},{g},{b},{a})"));
                }
            }

            AppendVertList("DARK  ", darkIndices);
            AppendVertList("BRIGHT", brightIndices);
            if (darkIndices.Count == 0 && brightIndices.Count == 0)
            {
                var fallback = Enumerable.Range(0, Math.Min(sampleCount, 4)).ToList();
                AppendVertList("SAMPLE", fallback);
            }

            string report = sb.ToString();
            if (wantSnap && recorder != null && interesting)
            {
                if (wantSnap2E20)
                    snap2E20Calls++;
                else
                    snapSkinCalls++;
                recorder.AppendVu1SkinDiag(report);
            }
            if (wantEnv)
            {
                loggedSkinCalls++;
                Console.Write(report);
            }
        };

        // Wire VU1 XGKICK -> GS Path 1
        Vu1.OnXgKick = data => Gs.SubmitGifPacket(data, "VU1_XGKICK");
    }

    /// <summary>
    /// Atomically swaps the front and back primitive batches at VBlank / frame presentation.
    /// </summary>
    public void SwapBatches()
    {
        lock (_batchLock)
        {
            var temp = FrontBatch;
            FrontBatch = BackBatch;
            BackBatch = temp;
            BackBatch.Reset();
            Gs.ActiveBatch = BackBatch;

            // Snapshot VRAM for thread-safe host presentation
            Buffer.BlockCopy(Gs.Vram, 0, FrontVram, 0, GsSwizzle.VramByteSize);
        }
    }

    /// <summary>
    /// Attaches the Phase 4 Graphics Pipeline to the Phase 3 Runtime DMA Controller.
    /// </summary>
    public void AttachToRuntime(CastawayRuntime runtime)
    {
        AttachToDma(runtime.Dma);
        Gs.OnFinish = runtime.Mmio.SetGsCsrFinish;
    }

    public void AttachToDma(DmaController dma)
    {
        dma.OnVif0Data = Vif0.ProcessData;
        dma.OnVif1Data = Vif1.ProcessData;
        dma.OnVif1ReadData = Gs.ReadVramToHost;
        dma.OnGifData = data => Gs.SubmitGifPacket(data, "DMA_GIF");
    }

    public void Dispose()
    {
        if (_vu1DataPin.IsAllocated)
        {
            _vu1DataPin.Free();
            RuntimeDispatcher.Vu1Data = null;
        }
        if (_vu1MicroPin.IsAllocated)
        {
            _vu1MicroPin.Free();
            RuntimeDispatcher.Vu1Micro = null;
        }
        OpenGl.Dispose();
    }
}
