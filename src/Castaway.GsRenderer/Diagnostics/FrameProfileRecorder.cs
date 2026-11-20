using System.Text;
using Castaway.GsRenderer.Gs;

namespace Castaway.GsRenderer.Diagnostics;

public sealed class GsSnapshotState
{
    public byte[] Vram = Array.Empty<byte>();
    public ulong Prim, Rgbaq, St, Uv, Prmodecont, Prmode, Texclut, Scanmsk, Texa, Fogcol, Dthe, Colclamp, Pabe;
    public ulong[] Tex0 = new ulong[2];
    public ulong[] Tex1 = new ulong[2];
    public ulong[] Tex2 = new ulong[2];
    public ulong[] Clamp = new ulong[2];
    public ulong[] Xyoffset = new ulong[2];
    public ulong[] Scissor = new ulong[2];
    public ulong[] Alpha = new ulong[2];
    public ulong[] Test = new ulong[2];
    public ulong[] Fba = new ulong[2];
    public ulong[] Frame = new ulong[2];
    public ulong[] Zbuf = new ulong[2];
    public ulong Bitbltbuf, Trxpos, Trxreg, Trxdir, Signal, Finish, Label;
    public float CurS, CurT, CurQ;
    public ushort CurU, CurV;
    public byte CurR, CurG, CurB, CurA, CurF;
    public GsVertex[] VtxQueue = new GsVertex[3];
    public int VtxCount;
    public int ActiveCbp;
    public GsPsm ActiveCpsm;
    public byte[] TrxBuffer = Array.Empty<byte>();
    public int TrxPixelX, TrxPixelY;
    public bool TrxActive;
    public int PendingImageBytes, PendingMode, PendingNreg, PendingRegIdx, PendingTotalRegs;
    public ulong PendingTagHi;
    public bool PendingEop;
}

public readonly record struct RecordedGifPacket(string Source, byte[] Data);

public readonly record struct SoftSprBucketKey(
    string Path,
    int FbpBlock,
    int FbwPixels,
    GsPsm Fpsm,
    int Tbp0,
    int Tw,
    int Th,
    GsPsm Psm,
    int Cbp,
    GsPsm Cpsm,
    int Tfx,
    int Tcc,
    bool Ate,
    int Atst,
    byte Aref,
    int Afail,
    bool Abe,
    ulong AlphaReg,
    bool Zte,
    int Ztst,
    bool Zmsk,
    bool Date,
    bool Fba,
    uint Fbmsk,
    bool HasTex,
    bool LinearFilter);

public sealed class SoftSprBucketStats
{
    public int Count;
    public long TotalPixels;
    public long TotalTicks;
    public int LastClipX0, LastClipY0, LastClipX1, LastClipY1;
}

public readonly record struct SoftTriBucketKey(int FbpBlock, GsPsm Fpsm, int Tbp0, bool Tme, bool Abe, bool Ate, bool Zte);

public sealed class SoftTriBucketStats
{
    public int Count;
    public long TotalTicks;
}

public readonly record struct TrxBucketKey(string Kind, int SrcBlock, int DstBlock, GsPsm Psm, int Width, int Height);

public sealed class TrxBucketStats
{
    public int Count;
    public long TotalBytes;
    public long TotalTicks;
}

public sealed class Vu1BucketStats
{
    public int Calls;
    public long TotalBundles;
    public long TotalXgKicks;
    public long TotalTicks;
}

public sealed class GifSourceStats
{
    public int Packets;
    public long TotalBytes;
}

/// <summary>
/// Captures a single-frame lossless trace of GIF packets, initial GS state/VRAM,
/// VU1 microprogram timings, and CPU fallback sprite/triangle/transfer buckets for snapshot analysis.
/// Zero overhead when not attached.
/// </summary>
public sealed class FrameProfileRecorder
{
    public int CapturedFrame { get; set; }
    public GsSnapshotState? InitialGsState { get; set; }
    public ulong Dispfb1 { get; set; }
    public ulong Dispfb2 { get; set; }
    public ulong EndFrame0 { get; set; }
    public int BackBatchDrawCallsBeforeSwap { get; set; }
    public int BackBatchVerticesBeforeSwap { get; set; }
    public int FrontBatchDrawCallsAtPresent { get; set; }
    public int FrontBatchVerticesAtPresent { get; set; }
    public long FrameUpdateTicks { get; set; }
    public long RenderBatchTicks { get; set; }
    public long Vif1TicksDelta { get; set; }
    public long Vu1TicksDelta { get; set; }
    public long GifTicksDelta { get; set; }
    public long TrxTicksDelta { get; set; }
    public long SoftTriTicksDelta { get; set; }
    public long SoftSprTicksDelta { get; set; }
    public long Vif1TransfersDelta { get; set; }
    public long Vif1BytesDelta { get; set; }
    public long Vif1CommandsDelta { get; set; }
    public long Vif1UnpackQwordsDelta { get; set; }
    public long Vif1UnpackFastDelta { get; set; }
    public long Vif1UnpackSlowDelta { get; set; }
    public long Vif1StagedFastDelta { get; set; }
    public long Vif1StagedSlowDelta { get; set; }
    public long Vif1StagedBytesDelta { get; set; }
    public long Vif1UnknownCmdDelta { get; set; }
    public long Vif1MpgCallsDelta { get; set; }
    public long Vif1MpgBytesDelta { get; set; }
    public long Vif1UnpackTicksDelta { get; set; }
    public long Vif1MicroCbTicksDelta { get; set; }
    public long Vif1DirectGifTicksDelta { get; set; }
    public long Vu1XgKickTicksDelta { get; set; }
    public long DmaTotalTicksDelta { get; set; }
    public long DmaVif1TicksDelta { get; set; }
    public long DmaGifTicksDelta { get; set; }
    public long DmaOtherTicksDelta { get; set; }
    public long TotalPcSamples { get; set; }
    public long[] PhaseSamples { get; } = new long[8];
    public Dictionary<uint, int> EePcSamples { get; } = new(256);

    public List<RecordedGifPacket> GifPackets { get; } = new(2048);
    public Dictionary<string, GifSourceStats> GifSources { get; } = new(StringComparer.Ordinal);
    public Dictionary<SoftSprBucketKey, SoftSprBucketStats> SoftSprBuckets { get; } = new(128);
    public Dictionary<SoftTriBucketKey, SoftTriBucketStats> SoftTriBuckets { get; } = new(32);
    public Dictionary<TrxBucketKey, TrxBucketStats> TrxBuckets { get; } = new(32);
    public Dictionary<ushort, Vu1BucketStats> Vu1Buckets { get; } = new(32);

    private readonly StringBuilder _vu1SkinDiag = new(65536);
    public int Vu1SkinDiagCount { get; private set; }

    public void AppendVu1SkinDiag(string text)
    {
        _vu1SkinDiag.AppendLine(text);
        Vu1SkinDiagCount++;
    }

    public string GetVu1SkinDiagReport() => _vu1SkinDiag.ToString();

    public void RecordGifPacket(string source, ReadOnlySpan<byte> data)
    {
        GifPackets.Add(new RecordedGifPacket(source, data.ToArray()));
        if (!GifSources.TryGetValue(source, out var st))
        {
            st = new GifSourceStats();
            GifSources[source] = st;
        }
        st.Packets++;
        st.TotalBytes += data.Length;
    }

    public void RecordSoftSprite(in SoftSprBucketKey key, int clipX0, int clipY0, int clipX1, int clipY1, int pixels, long ticks)
    {
        if (!SoftSprBuckets.TryGetValue(key, out var st))
        {
            st = new SoftSprBucketStats();
            SoftSprBuckets[key] = st;
        }
        st.Count++;
        st.TotalPixels += pixels;
        st.TotalTicks += ticks;
        st.LastClipX0 = clipX0;
        st.LastClipY0 = clipY0;
        st.LastClipX1 = clipX1;
        st.LastClipY1 = clipY1;
    }

    public void RecordSoftTriangle(in SoftTriBucketKey key, long ticks)
    {
        if (!SoftTriBuckets.TryGetValue(key, out var st))
        {
            st = new SoftTriBucketStats();
            SoftTriBuckets[key] = st;
        }
        st.Count++;
        st.TotalTicks += ticks;
    }

    public void RecordTransfer(in TrxBucketKey key, long bytes, long ticks)
    {
        if (!TrxBuckets.TryGetValue(key, out var st))
        {
            st = new TrxBucketStats();
            TrxBuckets[key] = st;
        }
        st.Count++;
        st.TotalBytes += bytes;
        st.TotalTicks += ticks;
    }

    public void RecordVu1Call(ushort startByteAddr, long ticks, long bundles, long xgKicks)
    {
        if (!Vu1Buckets.TryGetValue(startByteAddr, out var st))
        {
            st = new Vu1BucketStats();
            Vu1Buckets[startByteAddr] = st;
        }
        st.Calls++;
        st.TotalTicks += ticks;
        st.TotalBundles += bundles;
        st.TotalXgKicks += xgKicks;
    }
}
