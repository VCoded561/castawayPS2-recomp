using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Castaway.GsRenderer.Diagnostics;
using Castaway.GsRenderer.Gs;

namespace Castaway.Host;

public static class FrameSnapshotWriter
{
    public static string WriteSnapshot(
        FrameProfileRecorder recorder,
        GsPrimitiveBatch? frontBatch,
        byte[]? glRgba,
        int glWidth,
        int glHeight)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string dir = Path.Combine("Snapshots", $"frame_{recorder.CapturedFrame}_{timestamp}");
        Directory.CreateDirectory(dir);

        // 1. Save OpenGL framebuffer capture (flipping OpenGL bottom-up rows to top-down BMP)
        if (glRgba != null && glWidth > 0 && glHeight > 0)
        {
            WriteRgbaBmpTopDownFromBottomUp(Path.Combine(dir, "gl_framebuffer.bmp"), glRgba, glWidth, glHeight);
        }

        // 2. Write 1-frame CPU performance profile and VU1 skinning diagnostic capture
        WritePerfProfile(Path.Combine(dir, "perf_profile.txt"), recorder, frontBatch);
        string vu1Diag = recorder.GetVu1SkinDiagReport();
        if (!string.IsNullOrEmpty(vu1Diag))
        {
            File.WriteAllText(Path.Combine(dir, "vu1_skin_diag.txt"), vu1Diag);
        }

        // 3. Write OpenGL draw calls and dump textures
        if (frontBatch != null)
        {
            WriteBatchDrawCalls(Path.Combine(dir, "batch_drawcalls.txt"), frontBatch);
            DumpBatchTextures(Path.Combine(dir, "textures"), frontBatch);
        }

        // 4. Replay this frame through 100%-accurate Software GS and compare against OpenGL
        if (recorder.InitialGsState != null)
        {
            var swGs = new GsProcessor { Mode = GsRendererMode.Software };
            swGs.RestoreState(recorder.InitialGsState);
            var swWatch = Stopwatch.StartNew();
            foreach (var pkt in recorder.GifPackets)
            {
                swGs.SubmitGifPacket(pkt.Data, pkt.Source);
            }
            swWatch.Stop();

            int swW = 640;
            int swH = 448;
            int dispFbp = (int)(recorder.Dispfb1 & 0x1FFul) * 32;
            int dispFbw = Math.Max(1, (int)((recorder.Dispfb1 >> 9) & 0x3Ful)) * 64;
            var dispPsm = (GsPsm)((recorder.Dispfb1 >> 15) & 0x1Ful);
            if (recorder.Dispfb1 == 0)
            {
                dispFbp = (int)(swGs.Frame[0] & 0x1FFul) * 32;
                dispFbw = Math.Max(1, (int)((swGs.Frame[0] >> 16) & 0x3Ful)) * 64;
                dispPsm = (GsPsm)((swGs.Frame[0] >> 24) & 0x3Ful);
            }

            uint[] swDisp = ReadFramebufferRgba(swGs.Vram, dispFbp, dispFbw, dispPsm, swW, swH);
            WritePackedRgbaBmp(Path.Combine(dir, "sw_reference_dispfb1.bmp"), swDisp, swW, swH);

            int f0Block = (int)(swGs.Frame[0] & 0x1FFul) * 32;
            int f0W = Math.Max(1, (int)((swGs.Frame[0] >> 16) & 0x3Ful)) * 64;
            var f0Psm = (GsPsm)((swGs.Frame[0] >> 24) & 0x3Ful);
            uint[]? swF0 = null;
            if (f0Block != dispFbp || f0Psm != dispPsm)
            {
                swF0 = ReadFramebufferRgba(swGs.Vram, f0Block, f0W, f0Psm, swW, swH);
                WritePackedRgbaBmp(Path.Combine(dir, "sw_reference_frame0.bmp"), swF0, swW, swH);
            }

            int z0Block = (int)(swGs.Zbuf[0] & 0x1FFul) * 32;
            VramDumper.DumpUnswizzledBmp(Path.Combine(dir, "sw_reference_zbuf.bmp"), swGs.Vram, z0Block, f0W, GsPsm.Psmz32, swW, swH);

            if (glRgba != null && glWidth > 0 && glHeight > 0)
            {
                bool dispHasPixels = HasNonBlackRgb(swDisp);
                bool f0HasPixels = swF0 != null && HasNonBlackRgb(swF0);
                bool useF0Ref = !dispHasPixels && f0HasPixels;
                uint[] diffRef = useF0Ref ? swF0! : swDisp;
                string refLabel = useF0Ref ? $"FRAME[0](0x{f0Block:X},{f0W},{f0Psm})" : $"DISPFB1(0x{dispFbp:X},{dispFbw},{dispPsm})";

                WritePixelDiff(
                    Path.Combine(dir, "diff_summary.txt"),
                    Path.Combine(dir, "diff_heatmap.bmp"),
                    glRgba, glWidth, glHeight,
                    diffRef, swW, swH,
                    recorder, swWatch.ElapsedMilliseconds, dispFbp, dispFbw, dispPsm, refLabel, frontBatch);
            }
        }

        string fullPath = Path.GetFullPath(dir);
        Console.WriteLine($"[SNAPSHOT] Saved Frame {recorder.CapturedFrame} snapshot -> {fullPath}");
        return fullPath;
    }

    private static void WritePerfProfile(string path, FrameProfileRecorder r, GsPrimitiveBatch? batch)
    {
        double tickToMs = 1000.0 / Stopwatch.Frequency;
        var sb = new StringBuilder(16384);
        sb.AppendLine($"=== Frame {r.CapturedFrame} Snapshot Performance Profile ===");
        sb.AppendLine($"FrameUpdate: {r.FrameUpdateTicks * tickToMs:F2} ms | RenderBatch: {r.RenderBatchTicks * tickToMs:F2} ms");
        sb.AppendLine(
            $"Pipeline deltas (ms): Vif1={r.Vif1TicksDelta * tickToMs:F2}, Vu1={r.Vu1TicksDelta * tickToMs:F2}, " +
            $"Gif={r.GifTicksDelta * tickToMs:F2}, Trx={r.TrxTicksDelta * tickToMs:F2}, " +
            $"SoftTri={r.SoftTriTicksDelta * tickToMs:F2}, SoftSpr={r.SoftSprTicksDelta * tickToMs:F2}");
        double vif1UnpackMs = r.Vif1UnpackTicksDelta * tickToMs;
        double vif1MicroCbMs = r.Vif1MicroCbTicksDelta * tickToMs;
        double vif1MicroCbExclMs = (r.Vif1MicroCbTicksDelta - r.Vu1TicksDelta) * tickToMs;
        double vif1DirectMs = r.Vif1DirectGifTicksDelta * tickToMs;
        double vif1OtherMs = (r.Vif1TicksDelta - r.Vif1MicroCbTicksDelta - r.Vif1UnpackTicksDelta - r.Vif1DirectGifTicksDelta) * tickToMs;
        sb.AppendLine(
            $"Vif1 breakdown: ms={r.Vif1TicksDelta * tickToMs:F2} (Unpack={vif1UnpackMs:F2}, MicroCb={vif1MicroCbMs:F2} [Vu1={r.Vu1TicksDelta * tickToMs:F2} + Excl={vif1MicroCbExclMs:F2}], Direct={vif1DirectMs:F2}, Parse/Stage/Mpg={vif1OtherMs:F2}) | " +
            $"xfers={r.Vif1TransfersDelta} bytes={r.Vif1BytesDelta} cmds={r.Vif1CommandsDelta} unk={r.Vif1UnknownCmdDelta} | " +
            $"unpackQw={r.Vif1UnpackQwordsDelta} (fast={r.Vif1UnpackFastDelta}, slow={r.Vif1UnpackSlowDelta}) | " +
            $"stage(fast={r.Vif1StagedFastDelta}, slow={r.Vif1StagedSlowDelta}, bytes={r.Vif1StagedBytesDelta}) | " +
            $"mpg={r.Vif1MpgCallsDelta} ({r.Vif1MpgBytesDelta} B)");
        if (batch != null)
        {
            sb.AppendLine($"OpenGL Batch: DrawCalls={batch.DrawCallCount}, Vertices={batch.VertexCount}, Textures={batch.Textures.Count} | PreSwapBack=({r.BackBatchDrawCallsBeforeSwap} calls, {r.BackBatchVerticesBeforeSwap} vtx) | AtPresent=({r.FrontBatchDrawCallsAtPresent} calls, {r.FrontBatchVerticesAtPresent} vtx)");
        }
        sb.AppendLine();

        sb.AppendLine("--- GIF Sources ---");
        foreach (var kv in r.GifSources.OrderByDescending(x => x.Value.TotalBytes))
        {
            sb.AppendLine($"  {kv.Key,-14} packets={kv.Value.Packets,5} bytes={kv.Value.TotalBytes,10}");
        }
        sb.AppendLine();

        sb.AppendLine("--- VU1 Microprograms (sorted by TotalMs) ---");
        double totalVu1Ms = 0;
        long totalBundles = 0;
        int totalVuCalls = 0;
        foreach (var kv in r.Vu1Buckets.OrderByDescending(x => x.Value.TotalTicks))
        {
            double ms = kv.Value.TotalTicks * tickToMs;
            totalVu1Ms += ms;
            totalBundles += kv.Value.TotalBundles;
            totalVuCalls += kv.Value.Calls;
            double avgBundles = kv.Value.Calls > 0 ? (double)kv.Value.TotalBundles / kv.Value.Calls : 0;
            sb.AppendLine($"  0x{kv.Key:X4} | ms={ms,7:F2} | calls={kv.Value.Calls,5} | bundles={kv.Value.TotalBundles,9} (avg {avgBundles,6:F1}) | xgkicks={kv.Value.TotalXgKicks,6}");
        }
        sb.AppendLine($"  TOTAL  | ms={totalVu1Ms,7:F2} | calls={totalVuCalls,5} | bundles={totalBundles,9}");
        sb.AppendLine();

        sb.AppendLine("--- SoftSpr Buckets (sorted by TotalMs) ---");
        double totalSprMs = 0;
        long totalSprCalls = 0;
        long totalSprPix = 0;
        foreach (var kv in r.SoftSprBuckets.OrderByDescending(x => x.Value.TotalTicks))
        {
            var k = kv.Key;
            var s = kv.Value;
            double ms = s.TotalTicks * tickToMs;
            totalSprMs += ms;
            totalSprCalls += s.Count;
            totalSprPix += s.TotalPixels;
            sb.AppendLine(
                $"  [{k.Path,-9}] ms={ms,7:F2} calls={s.Count,5} pix={s.TotalPixels,10} | " +
                $"FB=0x{k.FbpBlock:X}({k.FbwPixels},{k.Fpsm}) TEX=0x{k.Tbp0:X}({k.Tw}x{k.Th},{k.Psm},CBP=0x{k.Cbp:X},{k.Cpsm}) " +
                $"TFX={k.Tfx}/TCC={k.Tcc} Lin={B(k.LinearFilter)} | " +
                $"ATE={B(k.Ate)}({k.Atst}/{k.Aref}/AF{k.Afail}) ABE={B(k.Abe)}(0x{k.AlphaReg:X}) " +
                $"ZTE={B(k.Zte)}({k.Ztst},MSK={B(k.Zmsk)}) DATE={B(k.Date)} FBA={B(k.Fba)} FBMSK=0x{k.Fbmsk:X8} | " +
                $"sample=[{s.LastClipX0}..{s.LastClipX1},{s.LastClipY0}..{s.LastClipY1}]");
        }
        sb.AppendLine($"  TOTAL SoftSpr: ms={totalSprMs:F2}, calls={totalSprCalls}, pix={totalSprPix}");
        sb.AppendLine();

        sb.AppendLine("--- SoftTri Buckets ---");
        foreach (var kv in r.SoftTriBuckets.OrderByDescending(x => x.Value.TotalTicks))
        {
            var k = kv.Key;
            sb.AppendLine($"  calls={kv.Value.Count,5} ms={kv.Value.TotalTicks * tickToMs,7:F2} | FB=0x{k.FbpBlock:X}({k.Fpsm}) TBP=0x{k.Tbp0:X} TME={B(k.Tme)} ABE={B(k.Abe)} ATE={B(k.Ate)} ZTE={B(k.Zte)}");
        }
        sb.AppendLine();

        sb.AppendLine("--- VRAM Transfers ---");
        foreach (var kv in r.TrxBuckets.OrderByDescending(x => x.Value.TotalTicks))
        {
            var k = kv.Key;
            sb.AppendLine($"  [{k.Kind,-12}] calls={kv.Value.Count,4} bytes={kv.Value.TotalBytes,9} ms={kv.Value.TotalTicks * tickToMs,7:F2} | src=0x{k.SrcBlock:X} dst=0x{k.DstBlock:X} ({k.Width}x{k.Height},{k.Psm})");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteBatchDrawCalls(string path, GsPrimitiveBatch batch)
    {
        var sb = new StringBuilder(32768);
        sb.AppendLine($"=== OpenGL FrontBatch Draw Calls ({batch.DrawCallCount} calls, {batch.VertexCount} vertices, {batch.Textures.Count} unique textures) ===");
        for (int i = 0; i < batch.DrawCallCount; i++)
        {
            ref readonly var c = ref batch.DrawCalls[i];
            ref readonly var dc = ref c.DrawContext;
            ulong texHash = (dc.TexIndex >= 0 && dc.TexIndex < batch.Textures.Count) ? batch.Textures[dc.TexIndex].Hash64 : 0ul;
            GsGlVertex v0 = c.StartVertex < batch.VertexCount ? batch.Vertices[c.StartVertex] : default;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            float minR = float.MaxValue, maxR = float.MinValue, minG = float.MaxValue, maxG = float.MinValue;
            float minB = float.MaxValue, maxB = float.MinValue, minA = float.MaxValue, maxA = float.MinValue;
            int end = Math.Min(batch.VertexCount, c.StartVertex + c.VertexCount);
            for (int vi = c.StartVertex; vi < end; vi++)
            {
                ref readonly var v = ref batch.Vertices[vi];
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
                if (v.R < minR) minR = v.R;
                if (v.R > maxR) maxR = v.R;
                if (v.G < minG) minG = v.G;
                if (v.G > maxG) maxG = v.G;
                if (v.B < minB) minB = v.B;
                if (v.B > maxB) maxB = v.B;
                if (v.A < minA) minA = v.A;
                if (v.A > maxA) maxA = v.A;
            }

            sb.AppendLine(
                $"#{i,4} {c.PrimType,-8} vtx={c.StartVertex,6}+{c.VertexCount,-5} " +
                $"FB=0x{dc.FbpBlock:X}({dc.FbwPixels},{dc.Fpsm}) ZB=0x{dc.ZbpBlock:X}({dc.Zpsm},ZMSK={B(dc.Zmsk)}) | " +
                $"TME={B(dc.Tme)} FST={B(dc.Fst)} TEX=0x{dc.Tbp0:X}({dc.Tw}x{dc.Th},{dc.Psm}) CBP=0x{dc.Cbp:X}({dc.Cpsm},CSA={dc.Csa}) idx={dc.TexIndex} hash={texHash:X16} TFX={dc.Tfx} TCC={dc.Tcc} Lin={B(dc.LinearFilter)} Clamp=({dc.Wms},{dc.Wmt}) | " +
                $"ATE={B(dc.Ate)}(ST={dc.Atst},REF={dc.Aref},AF={dc.Afail}) ABE={B(dc.Abe)}(A={dc.SpecA},B={dc.SpecB},C={dc.SpecC},D={dc.SpecD},FIX=0x{dc.Fix:X2},CLAMP={B(dc.Colclamp)}) ZTE={B(dc.Zte)}(ST={dc.Ztst}) DATE={B(dc.Date)}(M={dc.Datm}) FBA={B(dc.Fba)} FBMSK=0x{dc.Fbmsk:X8} | " +
                $"SC=({c.Scx0},{c.Scy0})-({c.Scx1},{c.Scy1}) bbox=[{minX:F1}..{maxX:F1},{minY:F1}..{maxY:F1}] " +
                $"rgbaMin=({minR:F2},{minG:F2},{minB:F2},{minA:F2}) rgbaMax=({maxR:F2},{maxG:F2},{maxB:F2},{maxA:F2}) | " +
                $"v0=({v0.X:F1},{v0.Y:F1},{v0.Z:F4}) rgba=({v0.R:F2},{v0.G:F2},{v0.B:F2},{v0.A:F2}) stq=({v0.S:G4},{v0.T:G4},{v0.Q:G4}) uv=({v0.U:F1},{v0.V:F1})");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void DumpBatchTextures(string texDir, GsPrimitiveBatch batch)
    {
        if (batch.Textures.Count == 0)
            return;

        Directory.CreateDirectory(texDir);
        var texInfoByIdx = new Dictionary<int, (int Tbp0, GsPsm Psm, int Cbp, GsPsm Cpsm)>();
        for (int i = 0; i < batch.DrawCallCount; i++)
        {
            ref readonly var dc = ref batch.DrawCalls[i].DrawContext;
            if (dc.TexIndex >= 0 && !texInfoByIdx.ContainsKey(dc.TexIndex))
            {
                texInfoByIdx[dc.TexIndex] = (dc.Tbp0, dc.Psm, dc.Cbp, dc.Cpsm);
            }
        }

        for (int i = 0; i < batch.Textures.Count; i++)
        {
            var tex = batch.Textures[i];
            if (tex.Pixels.Length == 0 || tex.Width <= 0 || tex.Height <= 0)
                continue;

            string tag = texInfoByIdx.TryGetValue(i, out var info)
                ? $"TBP0x{info.Tbp0:X}_{info.Psm}_CBP0x{info.Cbp:X}"
                : $"hash_{tex.Hash64:X16}";

            WritePackedRgbaBmp(Path.Combine(texDir, $"tex_{i:D3}_{tag}_{tex.Width}x{tex.Height}.bmp"), tex.Pixels, tex.Width, tex.Height);

            bool hasNonOpaqueAlpha = false;
            int total = Math.Min(tex.Pixels.Length, tex.Width * tex.Height);
            for (int p = 0; p < total; p++)
            {
                if (((tex.Pixels[p] >> 24) & 0xFFu) != 0x80u)
                {
                    hasNonOpaqueAlpha = true;
                    break;
                }
            }
            if (hasNonOpaqueAlpha)
            {
                uint[] alphaGray = new uint[total];
                for (int p = 0; p < total; p++)
                {
                    byte a = (byte)((tex.Pixels[p] >> 24) & 0xFFu);
                    byte vis = (byte)Math.Min(255, a * 2); // PS2 alpha 0x80 == 100%
                    alphaGray[p] = (uint)(vis | (vis << 8) | (vis << 16) | 0xFF000000u);
                }
                WritePackedRgbaBmp(Path.Combine(texDir, $"tex_{i:D3}_{tag}_{tex.Width}x{tex.Height}_alpha.bmp"), alphaGray, tex.Width, tex.Height);
            }
        }
    }

    private static bool HasNonBlackRgb(uint[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if ((pixels[i] & 0x00FFFFFFu) != 0)
                return true;
        }
        return false;
    }

    private static void WritePixelDiff(
        string summaryPath,
        string heatmapPath,
        byte[] glRgbaBottomUp, int glW, int glH,
        uint[] swRgbaTopDown, int swW, int swH,
        FrameProfileRecorder r, long swReplayMs, int dispFbp, int dispFbw, GsPsm dispPsm,
        string refLabel,
        GsPrimitiveBatch? batch)
    {
        int total = swW * swH;
        uint[] heatmap = new uint[total];
        int diffGt4 = 0, diffGt16 = 0, diffGt48 = 0, maxDiff = 0;
        long sumAbsDiff = 0;
        var worstSamples = new List<(int Diff, int X, int Y, byte GlR, byte GlG, byte GlB, byte SwR, byte SwG, byte SwB)>(64);

        for (int y = 0; y < swH; y++)
        {
            // Sample centered texel from GL backbuffer (bottom-up rows)
            int gyTop = Math.Clamp((int)((y + 0.5) * glH / swH), 0, glH - 1);
            int gyBottom = glH - 1 - gyTop;
            for (int x = 0; x < swW; x++)
            {
                int gx = Math.Clamp((int)((x + 0.5) * glW / swW), 0, glW - 1);
                int glOff = (gyBottom * glW + gx) * 4;
                byte glR = glRgbaBottomUp[glOff + 0];
                byte glG = glRgbaBottomUp[glOff + 1];
                byte glB = glRgbaBottomUp[glOff + 2];

                uint swPx = swRgbaTopDown[y * swW + x];
                byte swR = (byte)(swPx & 0xFFu);
                byte swG = (byte)((swPx >> 8) & 0xFFu);
                byte swB = (byte)((swPx >> 16) & 0xFFu);

                int dR = Math.Abs(glR - swR);
                int dG = Math.Abs(glG - swG);
                int dB = Math.Abs(glB - swB);
                int d = Math.Max(dR, Math.Max(dG, dB));
                sumAbsDiff += dR + dG + dB;

                if (d > 4) diffGt4++;
                if (d > 16) diffGt16++;
                if (d > 48) diffGt48++;
                if (d > maxDiff) maxDiff = d;

                if (d > 16 && (worstSamples.Count < 40 || d > worstSamples[^1].Diff))
                {
                    if (worstSamples.Count == 0 || ((x + y * 7) % 13 == 0) || d > 100)
                    {
                        worstSamples.Add((d, x, y, glR, glG, glB, swR, swG, swB));
                        worstSamples.Sort((a, b) => b.Diff.CompareTo(a.Diff));
                        if (worstSamples.Count > 40)
                            worstSamples.RemoveAt(worstSamples.Count - 1);
                    }
                }

                if (d <= 4)
                {
                    byte dimR = (byte)(swR / 3);
                    byte dimG = (byte)(swG / 3);
                    byte dimB = (byte)(swB / 3);
                    heatmap[y * swW + x] = (uint)(dimR | (dimG << 8) | (dimB << 16) | 0xFF000000u);
                }
                else
                {
                    byte hot = (byte)Math.Min(255, 96 + d);
                    heatmap[y * swW + x] = (uint)hot | ((uint)hot << 16) | 0xFF000000u;
                }
            }
        }

        WritePackedRgbaBmp(heatmapPath, heatmap, swW, swH);

        var sb = new StringBuilder(4096);
        sb.AppendLine($"=== Frame {r.CapturedFrame} GL vs PS2 Software GS Accuracy Report ===");
        sb.AppendLine($"DISPFB1: FBP=0x{dispFbp:X} FBW={dispFbw} PSM={dispPsm} | SWRefSource={refLabel} | GLSize={glW}x{glH} | SWRefSize={swW}x{swH} | SWReplay={swReplayMs}ms");
        if (batch != null)
        {
            sb.AppendLine($"OpenGL Batch: DrawCalls={batch.DrawCallCount}, Vertices={batch.VertexCount}, Textures={batch.Textures.Count} | PreSwapBack=({r.BackBatchDrawCallsBeforeSwap} calls, {r.BackBatchVerticesBeforeSwap} vtx) | AtPresent=({r.FrontBatchDrawCallsAtPresent} calls, {r.FrontBatchVerticesAtPresent} vtx)");
        }
        sb.AppendLine($"Compared Pixels : {total}");
        sb.AppendLine($"Matching (<=4)  : {total - diffGt4} ({100.0 * (total - diffGt4) / total:F2}%)");
        sb.AppendLine($"Diff > 4        : {diffGt4} ({100.0 * diffGt4 / total:F2}%)");
        sb.AppendLine($"Diff > 16       : {diffGt16} ({100.0 * diffGt16 / total:F2}%)");
        sb.AppendLine($"Diff > 48       : {diffGt48} ({100.0 * diffGt48 / total:F2}%)");
        sb.AppendLine($"Max Channel Diff: {maxDiff}");
        sb.AppendLine($"Mean ChannelErr : {sumAbsDiff / (double)(total * 3):F3}");
        sb.AppendLine();
        sb.AppendLine("Worst sampled discrepancies (x, y: GL vs SW_Ref):");
        foreach (var s in worstSamples.Take(25))
        {
            sb.AppendLine($"  ({s.X,3},{s.Y,3}) diff={s.Diff,3} | GL=({s.GlR,3},{s.GlG,3},{s.GlB,3}) SW=({s.SwR,3},{s.SwG,3},{s.SwB,3})");
        }
        File.WriteAllText(summaryPath, sb.ToString());
    }

    private static uint[] ReadFramebufferRgba(byte[] vram, int fbpBlock, int fbwPixels, GsPsm psm, int width, int height)
    {
        uint[] rgba = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (psm is GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S)
                {
                    ushort c16 = GsSwizzle.ReadPixel16(vram, fbpBlock, fbwPixels, x, y, is16S: psm is GsPsm.Psmct16S or GsPsm.Psmz16S, isZ: psm is GsPsm.Psmz16 or GsPsm.Psmz16S);
                    rgba[y * width + x] = GsSwizzle.Expand16To32(c16);
                }
                else
                {
                    rgba[y * width + x] = GsSwizzle.ReadPixel32(vram, fbpBlock, fbwPixels, x, y);
                }
            }
        }
        return rgba;
    }

    private static void WriteRgbaBmpTopDownFromBottomUp(string path, byte[] rgbaBottomUp, int width, int height)
    {
        int imageBytes = width * height * 4;
        byte[] bmp = new byte[54 + imageBytes];
        InitBmpHeader(bmp, width, height, imageBytes);

        int outOff = 54;
        for (int y = 0; y < height; y++)
        {
            int srcRow = (height - 1 - y) * width * 4;
            for (int x = 0; x < width; x++)
            {
                int srcOff = srcRow + x * 4;
                bmp[outOff + 0] = rgbaBottomUp[srcOff + 2]; // B
                bmp[outOff + 1] = rgbaBottomUp[srcOff + 1]; // G
                bmp[outOff + 2] = rgbaBottomUp[srcOff + 0]; // R
                bmp[outOff + 3] = 0xFF;
                outOff += 4;
            }
        }
        File.WriteAllBytes(path, bmp);
    }

    private static void WritePackedRgbaBmp(string path, uint[] rgba, int width, int height)
    {
        int pixelCount = width * height;
        int imageBytes = pixelCount * 4;
        byte[] bmp = new byte[54 + imageBytes];
        InitBmpHeader(bmp, width, height, imageBytes);

        int outOff = 54;
        for (int i = 0; i < pixelCount && i < rgba.Length; i++)
        {
            uint p = rgba[i];
            bmp[outOff + 0] = (byte)((p >> 16) & 0xFFu); // B
            bmp[outOff + 1] = (byte)((p >> 8) & 0xFFu);  // G
            bmp[outOff + 2] = (byte)(p & 0xFFu);         // R
            bmp[outOff + 3] = 0xFF;
            outOff += 4;
        }
        File.WriteAllBytes(path, bmp);
    }

    private static void InitBmpHeader(byte[] bmp, int width, int height, int imageBytes)
    {
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), 54 + imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -height); // top-down
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), imageBytes);
    }

    private static char B(bool v) => v ? '1' : '0';
}
