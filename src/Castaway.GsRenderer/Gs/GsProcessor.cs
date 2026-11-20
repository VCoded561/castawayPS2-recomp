using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Castaway.Core;
using Castaway.GsRenderer.Diagnostics;

namespace Castaway.GsRenderer.Gs;

public enum GsPrimitiveType : byte
{
    Point         = 0,
    Line          = 1,
    LineStrip     = 2,
    Triangle      = 3,
    TriangleStrip = 4,
    TriangleFan   = 5,
    Sprite        = 6
}

public struct GsVertex
{
    public int X;      // 12.4 unsigned fixed-point window X (0..65535)
    public int Y;      // 12.4 unsigned fixed-point window Y (0..65535)
    public uint Z;     // 32-bit (or 24-bit) unsigned depth
    public byte R;
    public byte G;
    public byte B;
    public byte A;
    public byte F;
    public float S;
    public float T;
    public float Q;
    public ushort U;   // 10.4 unsigned fixed-point texel U
    public ushort V;   // 10.4 unsigned fixed-point texel V
}

/// <summary>
/// Sony PS2 Graphics Synthesizer (GS) core processor:
/// - 4 MB swizzled virtual VRAM
/// - GIF packet parser (PACKED, REGLIST, IMAGE) across Paths 1, 2, and 3
/// - BITBLTBUF / TRXPOS / TRXREG / TRXDIR / HWREG transfer engine
/// - 1x native pixel-accurate software reference rasterizer with CLUT lookup, Z-test, Alpha test, and Alpha blending
/// </summary>
public sealed class GsProcessor
{
    public static bool EnableDebugLogging { get; set; } = false;

    public byte[] Vram { get; } = new byte[GsSwizzle.VramByteSize];
    public bool VramDirty { get; set; } = true;
    public GsRendererMode Mode { get; set; } = GsRendererMode.Software;
    public GsPrimitiveBatch? ActiveBatch { get; set; }

    // Internal GS Registers
    public ulong Prim { get; set; }
    public ulong Rgbaq { get; set; }
    public ulong St { get; set; }
    public ulong Uv { get; set; }
    public ulong Prmodecont { get; set; } = 1ul; // 1 = use PRIM attributes, 0 = use PRMODE
    public ulong Prmode { get; set; }
    public ulong Texclut { get; set; }
    public ulong Scanmsk { get; set; }
    public ulong Texa { get; set; } = 0x0000008000000000ul; // TA0=0, TA1=0x80
    public ulong Fogcol { get; set; }
    public ulong Dthe { get; set; }
    public ulong Colclamp { get; set; } = 1ul;
    public ulong Pabe { get; set; }

    // Dual-context registers (Index 0 = Context 1, Index 1 = Context 2)
    public readonly ulong[] Tex0 = new ulong[2];
    public readonly ulong[] Tex1 = new ulong[2];
    public readonly ulong[] Tex2 = new ulong[2];
    public readonly ulong[] Clamp = new ulong[2];
    public readonly ulong[] Xyoffset = new ulong[2];
    public readonly ulong[] Scissor = { 0x01DF_0000_027F_0000ul, 0x01DF_0000_027F_0000ul }; // 640x480 default
    public readonly ulong[] Alpha = new ulong[2];
    public readonly ulong[] Test = new ulong[2];
    public readonly ulong[] Fba = new ulong[2];
    public readonly ulong[] Frame = new ulong[2];
    public readonly ulong[] Zbuf = new ulong[2];

    // Transfer registers
    public ulong Bitbltbuf { get; set; }
    public ulong Trxpos { get; set; }
    public ulong Trxreg { get; set; }
    public ulong Trxdir { get; set; }
    public ulong Signal { get; set; }
    public ulong Finish { get; set; }
    public ulong Label { get; set; }

    // Current vertex latch state
    private float _curS;
    private float _curT;
    private float _curQ = 1.0f;
    private ushort _curU;
    private ushort _curV;
    private byte _curR = 0x80;
    private byte _curG = 0x80;
    private byte _curB = 0x80;
    private byte _curA = 0x80;
    private byte _curF;

    // Vertex queue (up to 3 vertices)
    private readonly GsVertex[] _vtxQueue = new GsVertex[3];
    private int _vtxCount;

    // Host-to-Local transfer state
    private string _currentGifSource = "";
    private int _activeCbp;
    private GsPsm _activeCpsm = GsPsm.Psmct32;
    private readonly List<byte> _trxBuffer = new(65536);
    private int _trxPixelX;
    private int _trxPixelY;
    private bool _trxActive;
    private int _pendingImageBytes;
    private int _pendingMode = -1; // -1: None, 0: PACKED, 1: REGLIST
    private ulong _pendingTagHi;
    private int _pendingNreg;
    private int _pendingRegIdx;
    private int _pendingTotalRegs;
    private bool _pendingEop;
    private int _uiTriLogs;
    private int _uiShadeLogs;

    // Pre-expanded CLUT cache per Context (16 entries for 4-bit, 256 entries for 8-bit)
    private readonly uint[][] _clutCache = { new uint[256], new uint[256] };
    private readonly int[] _cachedClutCbp = { -1, -1 };
    private readonly GsPsm[] _cachedClutCpsm = { (GsPsm)0xFF, (GsPsm)0xFF };
    private readonly int[] _cachedClutCsa = { -1, -1 };
    private readonly byte[] _cachedClutTa0 = { 0xFF, 0xFF };
    private readonly byte[] _cachedClutTa1 = { 0xFF, 0xFF };
    private readonly bool[] _cachedClutAem = { false, false };
    private readonly bool[] _cachedClutIs8Bit = { false, false };
    private readonly bool[] _cachedClutValid = { false, false };
    private readonly ulong[] _cachedClutHash64 = new ulong[2];

    private readonly bool[] _lastDcValid = new bool[2];
    private readonly GsDrawContext[] _lastDc = new GsDrawContext[2];
    private readonly bool[] _lastDcGouraud = new bool[2];
    private readonly GsPrimitiveBatch?[] _lastDcBatch = new GsPrimitiveBatch?[2];
    private readonly GsRendererMode[] _lastDcMode = { (GsRendererMode)255, (GsRendererMode)255 };
    private int _drawContextVersionCounter = 1;

    private readonly record struct GsTexCacheKey(
        int Tbp0, int Tbw, GsPsm Psm, int Tw, int Th,
        int Cbp, GsPsm Cpsm, int Csa, byte Ta0, byte Ta1, bool Aem);

    private readonly record struct GsSpriteWriteSignature(
        ulong SrcHash64, int SrcTw, int SrcTh,
        int ClipX1, int ClipY1,
        int Fx0Bits, int Fy0Bits, int Fx1Bits, int Fy1Bits,
        ushort U0, ushort V0, ushort U1, ushort V1,
        int S0Bits, int T0Bits, int S1Bits, int T1Bits, int Q1Bits,
        ulong Tex0Mask, ulong ClampReg, byte VR, byte VG, byte VB, byte VA, byte Flags);

    private sealed class GsCachedTexEntry
    {
        public int StartBlock;
        public int EndBlock;
        public int ClutStartBlock;
        public int ClutEndBlock;
        public ulong Hash64;
        public bool HasHash;
        public bool SharedWithBatch;
        public bool HasWriteSig;
        public GsSpriteWriteSignature WriteSig;
        public uint[] Pixels = Array.Empty<uint>();
    }

    private sealed class GsBakedSpriteEntry
    {
        public ulong DstHash64;
        public uint[] Pixels = Array.Empty<uint>();
    }

    private readonly record struct DeferredSpriteVramWrite(
        int FbpBlock, int FbwPixels, int Width, int Height, int StartBlock, int EndBlock, uint[] Pixels);

    private readonly record struct RawTexDecodeKey(
        ulong RawHash64, ulong ClutHash64, GsPsm Psm, int Width, int Height,
        byte Ta0, byte Ta1, bool Aem);

    private sealed class RawTexDecodeEntry
    {
        public ulong DstHash64;
        public uint[] Pixels = Array.Empty<uint>();
    }

    private struct DeferredH2lWrite
    {
        public int Dbp;
        public int Dbw;
        public GsPsm Dpsm;
        public int Rrw;
        public int Rrh;
        public int StartBlock;
        public int EndBlock;
        public int ByteLen;
        public ulong RawHash64;
        public byte[] RawBytes;
    }

    private const int MaxSpriteBakeCacheEntries = 1024;
    private const int MaxRawTexDecodeCacheEntries = 512;
    private const int MaxDeferredH2lEntries = 64;
    private readonly Dictionary<GsTexCacheKey, GsCachedTexEntry> _decodedTexCache = new(256);
    private readonly List<GsTexCacheKey> _invRemoveScratch = new(16);
    private readonly Dictionary<int, Stack<uint[]>> _texBufferPool = new(16);
    private readonly Dictionary<GsSpriteWriteSignature, GsBakedSpriteEntry> _spriteBakeCache = new(256);
    private readonly Queue<GsSpriteWriteSignature> _spriteBakeFifo = new(256);
    private readonly Dictionary<int, DeferredSpriteVramWrite> _deferredVramBakes = new(8);
    private readonly Dictionary<RawTexDecodeKey, RawTexDecodeEntry> _rawTexDecodeCache = new(512);
    private readonly Queue<RawTexDecodeKey> _rawTexDecodeFifo = new(512);
    private readonly List<DeferredH2lWrite> _deferredH2lUploads = new(32);
    private int _softTriInvalidatedFbp = -1;
    private int _softTriInvalidatedFbw;
    private GsPsm _softTriInvalidatedFpsm;

    // Diagnostics & Profiling
    public Action? OnFinish { get; set; }
    public bool EnableProfiling { get; set; } = false;
    public FrameProfileRecorder? FrameRecorder { get; set; }
    public long TotalGifTagsProcessed { get; private set; }
    public long TotalVerticesKicked { get; private set; }
    public long TotalPrimitivesDrawn { get; private set; }
    public long TotalTrianglesDrawn { get; set; }
    public long TotalSpritesDrawn { get; set; }
    public long TotalTexturedTriangles { get; set; }
    public long TotalFlatTriangles { get; set; }
    public long TotalTexturedPixels { get; set; }
    public long TotalFlatPixels { get; set; }
    public long TotalVramTransfers { get; private set; }
    public long TotalPixelsShaded { get; private set; }
    public long FastPackedStripsHit { get; private set; }
    public long FastPackedVertsHit { get; private set; }
    public long GenericPackedTags { get; private set; }
    public long H2lDedupHits { get; private set; }
    public long H2lDedupMisses { get; private set; }
    public long TotalPixelsWritten { get; private set; }
    public long TotalPixelsZFailed { get; private set; }

    public long TotalSubmitGifTicks { get; set; }
    public long TotalRasterizeTriangleTicks { get; set; }
    public long TotalRasterizeSpriteTicks { get; set; }
    public long TotalVramTransferTicks { get; set; }

    public GsProcessor()
    {
        // Context 1 defaults (FBW=10 / 640px, PSMCT32, FBP=0)
        Frame[0] = 0x00000000000A0000ul;
        Xyoffset[0] = (2048ul << 4) | ((2048ul << 4) << 32);
        Scissor[0] = 0x01DF_0000_027F_0000ul;
        Zbuf[0] = 0x00000000310000DCul;
        Test[0] = 0x0000000000050000ul;

        // Context 2 defaults (FBW=10 / 640px, PSMCT32, FBP=0)
        Frame[1] = 0x00000000000A0000ul;
        Xyoffset[1] = (2048ul << 4) | ((2048ul << 4) << 32);
        Scissor[1] = 0x01DF_0000_027F_0000ul;
        Zbuf[1] = 0x00000000310000DCul;
        Test[1] = 0x0000000000050000ul;
    }

    public ulong ActiveAttr => (Prmodecont & 1ul) != 0 ? Prim : Prmode;
    public int ActiveContext => (int)((ActiveAttr >> 9) & 1ul);
    public GsPrimitiveType ActivePrimType => (GsPrimitiveType)(Prim & 0x7ul);

    public GsSnapshotState CaptureState()
    {
        FlushAllDeferredVram();
        var s = new GsSnapshotState
        {
            Vram = (byte[])Vram.Clone(),
            Prim = Prim, Rgbaq = Rgbaq, St = St, Uv = Uv,
            Prmodecont = Prmodecont, Prmode = Prmode, Texclut = Texclut, Scanmsk = Scanmsk,
            Texa = Texa, Fogcol = Fogcol, Dthe = Dthe, Colclamp = Colclamp, Pabe = Pabe,
            Bitbltbuf = Bitbltbuf, Trxpos = Trxpos, Trxreg = Trxreg, Trxdir = Trxdir,
            Signal = Signal, Finish = Finish, Label = Label,
            CurS = _curS, CurT = _curT, CurQ = _curQ,
            CurU = _curU, CurV = _curV,
            CurR = _curR, CurG = _curG, CurB = _curB, CurA = _curA, CurF = _curF,
            VtxCount = _vtxCount,
            ActiveCbp = _activeCbp, ActiveCpsm = _activeCpsm,
            TrxBuffer = _trxBuffer.Count > 0 ? _trxBuffer.ToArray() : Array.Empty<byte>(),
            TrxPixelX = _trxPixelX, TrxPixelY = _trxPixelY, TrxActive = _trxActive,
            PendingImageBytes = _pendingImageBytes, PendingMode = _pendingMode,
            PendingNreg = _pendingNreg, PendingRegIdx = _pendingRegIdx, PendingTotalRegs = _pendingTotalRegs,
            PendingTagHi = _pendingTagHi, PendingEop = _pendingEop
        };
        Array.Copy(Tex0, s.Tex0, 2);
        Array.Copy(Tex1, s.Tex1, 2);
        Array.Copy(Tex2, s.Tex2, 2);
        Array.Copy(Clamp, s.Clamp, 2);
        Array.Copy(Xyoffset, s.Xyoffset, 2);
        Array.Copy(Scissor, s.Scissor, 2);
        Array.Copy(Alpha, s.Alpha, 2);
        Array.Copy(Test, s.Test, 2);
        Array.Copy(Fba, s.Fba, 2);
        Array.Copy(Frame, s.Frame, 2);
        Array.Copy(Zbuf, s.Zbuf, 2);
        Array.Copy(_vtxQueue, s.VtxQueue, 3);
        return s;
    }

    public void RestoreState(GsSnapshotState s)
    {
        Buffer.BlockCopy(s.Vram, 0, Vram, 0, Vram.Length);
        Prim = s.Prim; Rgbaq = s.Rgbaq; St = s.St; Uv = s.Uv;
        Prmodecont = s.Prmodecont; Prmode = s.Prmode; Texclut = s.Texclut; Scanmsk = s.Scanmsk;
        Texa = s.Texa; Fogcol = s.Fogcol; Dthe = s.Dthe; Colclamp = s.Colclamp; Pabe = s.Pabe;
        Array.Copy(s.Tex0, Tex0, 2);
        Array.Copy(s.Tex1, Tex1, 2);
        Array.Copy(s.Tex2, Tex2, 2);
        Array.Copy(s.Clamp, Clamp, 2);
        Array.Copy(s.Xyoffset, Xyoffset, 2);
        Array.Copy(s.Scissor, Scissor, 2);
        Array.Copy(s.Alpha, Alpha, 2);
        Array.Copy(s.Test, Test, 2);
        Array.Copy(s.Fba, Fba, 2);
        Array.Copy(s.Frame, Frame, 2);
        Array.Copy(s.Zbuf, Zbuf, 2);
        Bitbltbuf = s.Bitbltbuf; Trxpos = s.Trxpos; Trxreg = s.Trxreg; Trxdir = s.Trxdir;
        Signal = s.Signal; Finish = s.Finish; Label = s.Label;
        _curS = s.CurS; _curT = s.CurT; _curQ = s.CurQ;
        _curU = s.CurU; _curV = s.CurV;
        _curR = s.CurR; _curG = s.CurG; _curB = s.CurB; _curA = s.CurA; _curF = s.CurF;
        Array.Copy(s.VtxQueue, _vtxQueue, 3);
        _vtxCount = s.VtxCount;
        _activeCbp = s.ActiveCbp; _activeCpsm = s.ActiveCpsm;
        _trxBuffer.Clear();
        if (s.TrxBuffer.Length > 0) _trxBuffer.AddRange(s.TrxBuffer);
        _trxPixelX = s.TrxPixelX; _trxPixelY = s.TrxPixelY; _trxActive = s.TrxActive;
        _pendingImageBytes = s.PendingImageBytes; _pendingMode = s.PendingMode;
        _pendingNreg = s.PendingNreg; _pendingRegIdx = s.PendingRegIdx; _pendingTotalRegs = s.PendingTotalRegs;
        _pendingTagHi = s.PendingTagHi; _pendingEop = s.PendingEop;
        Array.Fill(_lastDcValid, false);
        Array.Fill(_cachedClutValid, false);
        _decodedTexCache.Clear();
        _texBufferPool.Clear();
        _spriteBakeCache.Clear();
        _spriteBakeFifo.Clear();
        _deferredVramBakes.Clear();
        ClearDeferredH2l();
        _rawTexDecodeCache.Clear();
        _rawTexDecodeFifo.Clear();
        _softTriInvalidatedFbp = -1;
        VramDirty = true;
    }

    /// <summary>
    /// Parses and executes a stream of 128-bit GIF packets (from Path 1 XGKICK, Path 2 VIF1 DIRECT, or Path 3 GIF DMA).
    /// </summary>
    public void SubmitGifPacket(ReadOnlySpan<byte> data, string source = "")
    {
        var rec = FrameRecorder;
        rec?.RecordGifPacket(source, data);
        long t0 = (EnableProfiling || rec != null) ? Stopwatch.GetTimestamp() : 0;
        try
        {
        _currentGifSource = source;
        int offset = 0;
        int totalBytes = data.Length & ~15; // 128-bit quadword aligned

        // 1. Consume any pending IMAGE payload from a previous DMA tag (e.g., cnt tag ended with IMAGE GIFTAG and next tag is ref)
        if (_pendingImageBytes > 0)
        {
            int chunk = Math.Min(_pendingImageBytes, totalBytes - offset);
            if (chunk > 0)
            {
                WriteHwregData(data.Slice(offset, chunk));
                offset += chunk;
                _pendingImageBytes -= chunk;
            }

            if (offset >= totalBytes)
                return;
        }

        // 2. Consume any pending PACKED payload from a previous DMA tag
        if (_pendingMode == 0)
        {
            int descPos = _pendingRegIdx % _pendingNreg;
            ref byte dataBase = ref MemoryMarshal.GetReference(data);
            while (_pendingRegIdx < _pendingTotalRegs && offset + 16 <= totalBytes)
            {
                int regDesc = (int)((_pendingTagHi >> (descPos * 4)) & 0xFul);
                ExecutePackedDescriptor(regDesc, ref Unsafe.Add(ref dataBase, offset));
                offset += 16;
                _pendingRegIdx++;
                if (++descPos == _pendingNreg) descPos = 0;
            }

            if (_pendingRegIdx < _pendingTotalRegs)
                return;

            bool wasEop = _pendingEop;
            _pendingMode = -1;
            if (wasEop && _currentGifSource == "VU1_XGKICK")
                return;
        }
        // 3. Consume any pending REGLIST payload from a previous DMA tag
        else if (_pendingMode == 1)
        {
            int descPos = _pendingRegIdx % _pendingNreg;
            while (_pendingRegIdx < _pendingTotalRegs && offset + 16 <= totalBytes)
            {
                ulong val0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                ulong val1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
                offset += 16;

                int desc0 = (int)((_pendingTagHi >> (descPos * 4)) & 0xFul);
                WriteInternalRegister((byte)desc0, val0);
                _pendingRegIdx++;
                if (++descPos == _pendingNreg) descPos = 0;

                if (_pendingRegIdx < _pendingTotalRegs)
                {
                    int desc1 = (int)((_pendingTagHi >> (descPos * 4)) & 0xFul);
                    WriteInternalRegister((byte)desc1, val1);
                    _pendingRegIdx++;
                    if (++descPos == _pendingNreg) descPos = 0;
                }
            }

            if (_pendingRegIdx < _pendingTotalRegs)
                return;

            bool wasEop = _pendingEop;
            _pendingMode = -1;
            if (wasEop && _currentGifSource == "VU1_XGKICK")
                return;
        }

        while (offset + 16 <= totalBytes)
        {
            ulong tagLo = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
            ulong tagHi = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
            offset += 16;

            TotalGifTagsProcessed++;

            int nloop = (int)(tagLo & 0x7FFFul);
            bool eop = ((tagLo >> 15) & 1ul) != 0;
            bool pre = ((tagLo >> 46) & 1ul) != 0;
            ulong primVal = (tagLo >> 47) & 0x7FFul;
            int flg = (int)((tagLo >> 58) & 0x3ul);
            int nreg = (int)((tagLo >> 60) & 0xFul);
            if (nreg == 0) nreg = 16;

            if (pre)
            {
                WriteInternalRegister(0x00, primVal);
            }

            if (nloop == 0)
            {
                if (eop && _currentGifSource == "VU1_XGKICK")
                    break;
                continue;
            }

            switch (flg)
            {
                case 0: // PACKED mode
                {
                    if (Mode == GsRendererMode.OpenGl && ActiveBatch != null && !EnableDebugLogging && nloop >= 3)
                    {
                        if (nreg == 3 && ((tagHi & 0xFFFul) is 0x512ul or 0x412ul) && offset + nloop * 48 <= totalBytes)
                        {
                            bool isXyzf2 = (tagHi & 0xF00ul) == 0x400ul;
                            if (TryProcessPackedStRgbaXyzOpenGlFast(data, offset, nloop, isXyzf2))
                            {
                                FastPackedStripsHit++;
                                FastPackedVertsHit += nloop;
                                offset += nloop * 48;
                                break;
                            }
                        }
                    }

                    if (nreg == 1 && (tagHi & 0xFul) == 0xEul && offset + nloop * 16 <= totalBytes)
                    {
                        GenericPackedTags++;
                        ref byte adBase = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
                        for (int k = 0; k < nloop; k++)
                        {
                            byte regAddr = (byte)(Unsafe.Add(ref adBase, 8) & 0x7Fu);
                            ulong regVal = Unsafe.ReadUnaligned<ulong>(ref adBase);
                            WriteInternalRegister(regAddr, regVal);
                            adBase = ref Unsafe.Add(ref adBase, 16);
                        }
                        offset += nloop * 16;
                        break;
                    }

                    GenericPackedTags++;
                    int totalRegs = nloop * nreg;
                    int regIdx = 0;
                    int descPos = 0;
                    ref byte dataBase = ref MemoryMarshal.GetReference(data);
                    while (regIdx < totalRegs && offset + 16 <= totalBytes)
                    {
                        int regDesc = (int)((tagHi >> (descPos * 4)) & 0xFul);
                        ExecutePackedDescriptor(regDesc, ref Unsafe.Add(ref dataBase, offset));
                        offset += 16;
                        regIdx++;
                        if (++descPos == nreg) descPos = 0;
                    }

                    if (regIdx < totalRegs)
                    {
                        _pendingMode = 0;
                        _pendingTagHi = tagHi;
                        _pendingNreg = nreg;
                        _pendingRegIdx = regIdx;
                        _pendingTotalRegs = totalRegs;
                        _pendingEop = eop;
                        return;
                    }
                    break;
                }

                case 1: // REGLIST mode (2 x 64-bit register values per 128-bit quadword)
                {
                    int totalRegs = nloop * nreg;
                    int regIdx = 0;
                    int descPos = 0;
                    while (regIdx < totalRegs && offset + 16 <= totalBytes)
                    {
                        ulong val0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                        ulong val1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
                        offset += 16;

                        int desc0 = (int)((tagHi >> (descPos * 4)) & 0xFul);
                        WriteInternalRegister((byte)desc0, val0);
                        regIdx++;
                        if (++descPos == nreg) descPos = 0;

                        if (regIdx < totalRegs)
                        {
                            int desc1 = (int)((tagHi >> (descPos * 4)) & 0xFul);
                            WriteInternalRegister((byte)desc1, val1);
                            regIdx++;
                            if (++descPos == nreg) descPos = 0;
                        }
                    }

                    if (regIdx < totalRegs)
                    {
                        _pendingMode = 1;
                        _pendingTagHi = tagHi;
                        _pendingNreg = nreg;
                        _pendingRegIdx = regIdx;
                        _pendingTotalRegs = totalRegs;
                        _pendingEop = eop;
                        return;
                    }
                    break;
                }

                case 2: // IMAGE mode (Host-to-Local HWREG transfer)
                case 3:
                {
                    int required = nloop * 16;
                    int available = Math.Min(required, totalBytes - offset);
                    if (available > 0)
                    {
                        WriteHwregData(data.Slice(offset, available));
                        offset += available;
                    }
                    _pendingImageBytes = required - available;
                    break;
                }
            }

            if (eop && _currentGifSource == "VU1_XGKICK")
            {
                break;
            }
        }
        }
        finally
        {
            if (t0 != 0)
                TotalSubmitGifTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private bool TryProcessPackedStRgbaXyzOpenGlFast(ReadOnlySpan<byte> data, int offset, int nloop, bool isXyzf2)
    {
        var primType = (GsPrimitiveType)(Prim & 7ul);
        if (primType != GsPrimitiveType.TriangleStrip && primType != GsPrimitiveType.Triangle)
            return false;
        if (_vtxCount != 0)
            return false;

        int ctx = ActiveContext;
        int fastFbp = (int)(Frame[ctx] & 0x1FFul) * 32;
        if (fastFbp >= 0x2000)
            return false;

        ulong attr = ActiveAttr;
        bool gouraud = ((attr >> 3) & 1ul) != 0;
        bool fastTme = ((attr >> 4) & 1ul) != 0;
        if (fastTme)
        {
            int fastTbp0 = (int)(Tex0[ctx] & 0x3FFFul);
            if (fastTbp0 == 0 || fastTbp0 == 0xA00)
                return false;
        }

        var batch = ActiveBatch!;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);
        int callIdx = -1;
        bool createdNew = false;
        bool dcTme = fastTme;

        const float inv16 = 0.0625f;
        const float inv128 = 0.0078125f;
        var fastZpsm = (GsPsm)(((Zbuf[ctx] >> 24) & 0xFul) | 0x30ul);
        float invZ = fastZpsm == GsPsm.Psmz24 ? (1.0f / 16777215.0f) : (1.0f / (float)uint.MaxValue);
        float curU = _curU * inv16;
        float curV = _curV * inv16;
        float scx0f = scx0;
        float scy0f = scy0;
        float scx1p1 = scx1 + 1;
        float scy1p1 = scy1 + 1;

        ref byte basePtr = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
        ref GsGlVertex vtxOutBase = ref MemoryMarshal.GetArrayDataReference(batch.Vertices);
        int vtxOutCount = batch.VertexCount;
        const int maxVerts = GsPrimitiveBatch.MaxVertices;

        int emittedTris = 0;
        int qCount = 0;
        GsGlVertex prev0 = default;
        GsGlVertex prev1 = default;
        bool isStrip = primType == GsPrimitiveType.TriangleStrip;
        Vector128<int> rgbaByteMask = Vector128.Create(0xFF);
        Vector128<float> inv128Vec = Vector128.Create(inv128);

        if (isStrip && gouraud)
        {
            ref byte vPtr = ref basePtr;

            // Peel vertex 0 -> prev0
            {
                float s0 = Unsafe.ReadUnaligned<float>(ref vPtr);
                float t0 = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 4));
                float rawQ0 = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 8));
                uint qBits0 = BitConverter.SingleToUInt32Bits(rawQ0) & 0x7FFFFFFFu;
                float q0 = (qBits0 - 1u) < 0x7F7FFFFFu ? rawQ0 : 1.0f;
                Vector128<float> rgba0 = Vector128.ConvertToSingle(
                    Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.Add(ref vPtr, 16)) & rgbaByteMask) * inv128Vec;
                ushort x0 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 32));
                ushort y0 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 36));
                ulong hi0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref vPtr, 40));
                uint z0 = isXyzf2 ? (uint)((hi0 >> 4) & 0xFFFFFFul) : (uint)hi0;
                prev0.X = (x0 - ofx) * inv16; prev0.Y = (y0 - ofy) * inv16; prev0.Z = z0 * invZ; prev0.W = 1.0f;
                Unsafe.As<float, Vector128<float>>(ref prev0.R) = rgba0;
                prev0.S = s0; prev0.T = t0; prev0.Q = q0; prev0.U = curU; prev0.V = curV;
                vPtr = ref Unsafe.Add(ref vPtr, 48);
            }
            // Peel vertex 1 -> prev1
            {
                float s1 = Unsafe.ReadUnaligned<float>(ref vPtr);
                float t1 = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 4));
                float rawQ1 = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 8));
                uint qBits1 = BitConverter.SingleToUInt32Bits(rawQ1) & 0x7FFFFFFFu;
                float q1 = (qBits1 - 1u) < 0x7F7FFFFFu ? rawQ1 : 1.0f;
                Vector128<float> rgba1 = Vector128.ConvertToSingle(
                    Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.Add(ref vPtr, 16)) & rgbaByteMask) * inv128Vec;
                ushort x1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 32));
                ushort y1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 36));
                ulong hi1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref vPtr, 40));
                uint z1 = isXyzf2 ? (uint)((hi1 >> 4) & 0xFFFFFFul) : (uint)hi1;
                prev1.X = (x1 - ofx) * inv16; prev1.Y = (y1 - ofy) * inv16; prev1.Z = z1 * invZ; prev1.W = 1.0f;
                Unsafe.As<float, Vector128<float>>(ref prev1.R) = rgba1;
                prev1.S = s1; prev1.T = t1; prev1.Q = q1; prev1.U = curU; prev1.V = curV;
                vPtr = ref Unsafe.Add(ref vPtr, 48);
            }
            qCount = 2;

            for (int i = 2; i < nloop; i++)
            {
                float s = Unsafe.ReadUnaligned<float>(ref vPtr);
                float t = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 4));
                float rawQ = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 8));
                uint qBits = BitConverter.SingleToUInt32Bits(rawQ) & 0x7FFFFFFFu;
                float q = (qBits - 1u) < 0x7F7FFFFFu ? rawQ : 1.0f;

                Vector128<float> rgbaF = Vector128.ConvertToSingle(
                    Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.Add(ref vPtr, 16)) & rgbaByteMask) * inv128Vec;

                ushort x = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 32));
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 36));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref vPtr, 40));
                uint z = isXyzf2 ? (uint)((hi >> 4) & 0xFFFFFFul) : (uint)hi;
                bool adc = ((hi >> 47) & 1ul) != 0;

                float fx = (x - ofx) * inv16;
                float fy = (y - ofy) * inv16;
                float fz = z * invZ;

                Unsafe.SkipInit(out GsGlVertex cur);
                cur.X = fx; cur.Y = fy; cur.Z = fz; cur.W = 1.0f;
                Unsafe.As<float, Vector128<float>>(ref cur.R) = rgbaF;
                cur.S = s; cur.T = t; cur.Q = q;
                cur.U = curU; cur.V = curV;

                if (!adc)
                {
                    float minFx = MathF.Min(prev0.X, MathF.Min(prev1.X, fx));
                    float maxFx = MathF.Max(prev0.X, MathF.Max(prev1.X, fx));
                    float minFy = MathF.Min(prev0.Y, MathF.Min(prev1.Y, fy));
                    float maxFy = MathF.Max(prev0.Y, MathF.Max(prev1.Y, fy));
                    if (maxFx - minFx < 2048.0f && maxFy - minFy < 2048.0f &&
                        maxFx >= scx0f && minFx <= scx1p1 && maxFy >= scy0f && minFy <= scy1p1 &&
                        vtxOutCount + 3 <= maxVerts)
                    {
                        if (callIdx < 0)
                        {
                            callIdx = PrepareFastTriangleDrawCall(batch, ctx, true, scx0, scy0, scx1, scy1, ofx, ofy, out createdNew, out dcTme);
                            if (callIdx < 0)
                                return false;
                        }
                        Unsafe.Add(ref vtxOutBase, vtxOutCount) = prev0;
                        Unsafe.Add(ref vtxOutBase, vtxOutCount + 1) = prev1;
                        Unsafe.Add(ref vtxOutBase, vtxOutCount + 2) = cur;
                        vtxOutCount += 3;
                        emittedTris++;
                    }
                }
                prev0 = prev1;
                prev1 = cur;
                vPtr = ref Unsafe.Add(ref vPtr, 48);
            }
        }
        else
        {
            for (int i = 0; i < nloop; i++)
            {
                ref byte vPtr = ref Unsafe.Add(ref basePtr, i * 48);
                float s = Unsafe.ReadUnaligned<float>(ref vPtr);
                float t = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 4));
                float rawQ = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref vPtr, 8));
                uint qBits = BitConverter.SingleToUInt32Bits(rawQ) & 0x7FFFFFFFu;
                float q = (qBits - 1u) < 0x7F7FFFFFu ? rawQ : 1.0f;

                Vector128<float> rgbaF = Vector128.ConvertToSingle(
                    Unsafe.ReadUnaligned<Vector128<int>>(ref Unsafe.Add(ref vPtr, 16)) & rgbaByteMask) * inv128Vec;

                ushort x = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 32));
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref vPtr, 36));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref vPtr, 40));
                uint z = isXyzf2 ? (uint)((hi >> 4) & 0xFFFFFFul) : (uint)hi;
                bool adc = ((hi >> 47) & 1ul) != 0;

                float fx = (x - ofx) * inv16;
                float fy = (y - ofy) * inv16;
                float fz = z * invZ;

                Unsafe.SkipInit(out GsGlVertex cur);
                cur.X = fx; cur.Y = fy; cur.Z = fz; cur.W = 1.0f;
                Unsafe.As<float, Vector128<float>>(ref cur.R) = rgbaF;
                cur.S = s; cur.T = t; cur.Q = q;
                cur.U = curU; cur.V = curV;

                if (isStrip)
                {
                    if (qCount == 2)
                    {
                        if (!adc)
                        {
                            float minFx = MathF.Min(prev0.X, MathF.Min(prev1.X, fx));
                            float maxFx = MathF.Max(prev0.X, MathF.Max(prev1.X, fx));
                            float minFy = MathF.Min(prev0.Y, MathF.Min(prev1.Y, fy));
                            float maxFy = MathF.Max(prev0.Y, MathF.Max(prev1.Y, fy));
                            if (maxFx - minFx < 2048.0f && maxFy - minFy < 2048.0f &&
                                maxFx >= scx0f && minFx <= scx1p1 && maxFy >= scy0f && minFy <= scy1p1 &&
                                vtxOutCount + 3 <= maxVerts)
                            {
                                if (callIdx < 0)
                                {
                                    ref readonly var dc = ref BuildDrawContext(ctx, gouraud);
                                    if (dc.FbpBlock >= 0x2000 || (dc.Tme && (dc.Tbp0 == 0 || dc.Tbp0 == 0xA00)))
                                        return false;
                                    dcTme = dc.Tme;
                                    callIdx = batch.PrepareTriangleDrawCall(in dc, scx0, scy0, scx1, scy1, ofx, ofy, out createdNew);
                                    if (callIdx < 0)
                                        return false;
                                }
                                var f0 = prev0; Unsafe.As<float, Vector128<float>>(ref f0.R) = rgbaF;
                                var f1 = prev1; Unsafe.As<float, Vector128<float>>(ref f1.R) = rgbaF;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount) = f0;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 1) = f1;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 2) = cur;
                                vtxOutCount += 3;
                                emittedTris++;
                            }
                        }
                        prev0 = prev1;
                        prev1 = cur;
                    }
                    else if (qCount == 0)
                    {
                        prev0 = cur;
                        qCount = 1;
                    }
                    else
                    {
                        prev1 = cur;
                        qCount = 2;
                    }
                }
                else
                {
                    if (adc)
                    {
                        qCount = 0;
                    }
                    else if (qCount == 0)
                    {
                        prev0 = cur;
                        qCount = 1;
                    }
                    else if (qCount == 1)
                    {
                        prev1 = cur;
                        qCount = 2;
                    }
                    else
                    {
                        float minFx = MathF.Min(prev0.X, MathF.Min(prev1.X, fx));
                        float maxFx = MathF.Max(prev0.X, MathF.Max(prev1.X, fx));
                        float minFy = MathF.Min(prev0.Y, MathF.Min(prev1.Y, fy));
                        float maxFy = MathF.Max(prev0.Y, MathF.Max(prev1.Y, fy));
                        if (maxFx - minFx < 2048.0f && maxFy - minFy < 2048.0f &&
                            maxFx >= scx0f && minFx <= scx1p1 && maxFy >= scy0f && minFy <= scy1p1 &&
                            vtxOutCount + 3 <= maxVerts)
                        {
                            if (callIdx < 0)
                            {
                                ref readonly var dc = ref BuildDrawContext(ctx, gouraud);
                                if (dc.FbpBlock >= 0x2000 || (dc.Tme && (dc.Tbp0 == 0 || dc.Tbp0 == 0xA00)))
                                    return false;
                                dcTme = dc.Tme;
                                callIdx = batch.PrepareTriangleDrawCall(in dc, scx0, scy0, scx1, scy1, ofx, ofy, out createdNew);
                                if (callIdx < 0)
                                    return false;
                            }
                            if (gouraud)
                            {
                                Unsafe.Add(ref vtxOutBase, vtxOutCount) = prev0;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 1) = prev1;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 2) = cur;
                            }
                            else
                            {
                                var f0 = prev0; Unsafe.As<float, Vector128<float>>(ref f0.R) = rgbaF;
                                var f1 = prev1; Unsafe.As<float, Vector128<float>>(ref f1.R) = rgbaF;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount) = f0;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 1) = f1;
                                Unsafe.Add(ref vtxOutBase, vtxOutCount + 2) = cur;
                            }
                            vtxOutCount += 3;
                            emittedTris++;
                        }
                        qCount = 0;
                    }
                }
            }
        }

        if (emittedTris > 0)
        {
            batch.VertexCount = vtxOutCount;
            batch.DrawCalls[callIdx].VertexCount += emittedTris * 3;
            TotalPrimitivesDrawn += emittedTris;
            TotalTrianglesDrawn += emittedTris;
            if (dcTme) TotalTexturedTriangles += emittedTris; else TotalFlatTriangles += emittedTris;
        }
        else if (callIdx >= 0)
        {
            batch.RollbackEmptyDrawCall(callIdx, createdNew);
        }

        TotalVerticesKicked += nloop;

        int lastIdx = nloop - 1;
        ref byte lastPtr = ref Unsafe.Add(ref basePtr, lastIdx * 48);
        _curS = Unsafe.ReadUnaligned<float>(ref lastPtr);
        _curT = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref lastPtr, 4));
        float lastRawQ = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref lastPtr, 8));
        _curQ = (float.IsFinite(lastRawQ) && lastRawQ != 0.0f) ? lastRawQ : 1.0f;
        _curR = Unsafe.Add(ref lastPtr, 16);
        _curG = Unsafe.Add(ref lastPtr, 20);
        _curB = Unsafe.Add(ref lastPtr, 24);
        _curA = Unsafe.Add(ref lastPtr, 28);
        if (isXyzf2)
            _curF = (byte)((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref lastPtr, 40)) >> 36) & 0xFFul);

        _vtxCount = qCount;
        for (int k = 0; k < qCount; k++)
        {
            ref byte qPtr = ref Unsafe.Add(ref basePtr, (nloop - qCount + k) * 48);
            float qRawQ = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref qPtr, 8));
            ulong qHi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qPtr, 40));
            _vtxQueue[k] = new GsVertex
            {
                X = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qPtr, 32)),
                Y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qPtr, 36)),
                Z = isXyzf2 ? (uint)((qHi >> 4) & 0xFFFFFFul) : (uint)qHi,
                R = Unsafe.Add(ref qPtr, 16),
                G = Unsafe.Add(ref qPtr, 20),
                B = Unsafe.Add(ref qPtr, 24),
                A = Unsafe.Add(ref qPtr, 28),
                F = isXyzf2 ? (byte)((qHi >> 36) & 0xFFul) : _curF,
                S = Unsafe.ReadUnaligned<float>(ref qPtr),
                T = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref qPtr, 4)),
                Q = (float.IsFinite(qRawQ) && qRawQ != 0.0f) ? qRawQ : 1.0f,
                U = _curU,
                V = _curV
            };
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int PrepareFastTriangleDrawCall(
        GsPrimitiveBatch batch, int ctx, bool gouraud,
        int scx0, int scy0, int scx1, int scy1, int ofx, int ofy,
        out bool createdNew, out bool dcTme)
    {
        createdNew = false;
        dcTme = false;
        ref readonly var dc = ref BuildDrawContext(ctx, gouraud);
        if (dc.FbpBlock >= 0x2000 || (dc.Tme && (dc.Tbp0 == 0 || dc.Tbp0 == 0xA00)))
            return -1;
        dcTme = dc.Tme;
        return batch.PrepareTriangleDrawCall(in dc, scx0, scy0, scx1, scy1, ofx, ofy, out createdNew);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExecutePackedDescriptor(int desc, ref byte qBase)
    {
        switch (desc)
        {
            case 0x0: // PRIM
                WriteInternalRegister(0x00, Unsafe.ReadUnaligned<ulong>(ref qBase) & 0x7FFul);
                break;

            case 0x1: // RGBAQ (R=bits 0..7, G=bits 32..39, B=bits 64..71, A=bits 96..103)
                _curR = qBase;
                _curG = Unsafe.Add(ref qBase, 4);
                _curB = Unsafe.Add(ref qBase, 8);
                _curA = Unsafe.Add(ref qBase, 12);
                break;

            case 0x2: // ST (S=word0, T=word1, Q=word2)
            {
                _curS = Unsafe.ReadUnaligned<float>(ref qBase);
                _curT = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref qBase, 4));
                float rawQ = Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref qBase, 8));
                _curQ = float.IsNaN(rawQ) || float.IsInfinity(rawQ) || rawQ == 0.0f ? 1.0f : rawQ;
                break;
            }

            case 0x3: // UV (U=bits 0..13, V=bits 32..45)
                _curU = (ushort)(Unsafe.ReadUnaligned<ushort>(ref qBase) & 0x3FFFu);
                _curV = (ushort)(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qBase, 4)) & 0x3FFFu);
                break;

            case 0x4: // XYZF2 (X=bits 0..15, Y=bits 32..47, Z=bits 68..91, F=bits 100..107, ADC=bit 111)
            {
                ushort x = Unsafe.ReadUnaligned<ushort>(ref qBase);
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qBase, 4));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qBase, 8));
                uint z = (uint)((hi >> 4) & 0xFFFFFFul);
                _curF = (byte)((hi >> 36) & 0xFFul);
                VertexKick(x, y, z, ((hi >> 47) & 1ul) != 0);
                break;
            }

            case 0x5: // XYZ2 (X=bits 0..15, Y=bits 32..47, Z=bits 64..95, ADC=bit 111)
            {
                ushort x = Unsafe.ReadUnaligned<ushort>(ref qBase);
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qBase, 4));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qBase, 8));
                VertexKick(x, y, (uint)hi, ((hi >> 47) & 1ul) != 0);
                break;
            }

            case 0x6: // TEX0_1
                WriteInternalRegister(0x06, Unsafe.ReadUnaligned<ulong>(ref qBase));
                break;

            case 0x7: // TEX0_2
                WriteInternalRegister(0x07, Unsafe.ReadUnaligned<ulong>(ref qBase));
                break;

            case 0x8: // CLAMP_1
                WriteInternalRegister(0x08, Unsafe.ReadUnaligned<ulong>(ref qBase));
                break;

            case 0x9: // CLAMP_2
                WriteInternalRegister(0x09, Unsafe.ReadUnaligned<ulong>(ref qBase));
                break;

            case 0xA: // FOG (F=bits 100..107)
                _curF = (byte)((Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qBase, 8)) >> 36) & 0xFFul);
                break;

            case 0xC: // XYZF3 (Vertex kick without drawing)
            {
                ushort x = Unsafe.ReadUnaligned<ushort>(ref qBase);
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qBase, 4));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qBase, 8));
                uint z = (uint)((hi >> 4) & 0xFFFFFFul);
                _curF = (byte)((hi >> 36) & 0xFFul);
                VertexKick(x, y, z, disableDraw: true);
                break;
            }

            case 0xD: // XYZ3 (Vertex kick without drawing)
            {
                ushort x = Unsafe.ReadUnaligned<ushort>(ref qBase);
                ushort y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref qBase, 4));
                uint z = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref qBase, 8));
                VertexKick(x, y, z, disableDraw: true);
                break;
            }

            case 0xE: // A+D (Data = lo 64 bits, Addr = byte 8)
                WriteInternalRegister((byte)(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref qBase, 8)) & 0x7Ful), Unsafe.ReadUnaligned<ulong>(ref qBase));
                break;

            case 0xF: // NOP
                break;
        }
    }

    public void WriteInternalRegister(byte regAddr, ulong value)
    {
        switch (regAddr)
        {
            case 0x00: // PRIM
            {
                ulong masked = value & 0x7FFul;
                if (Prim != masked)
                {
                    Prim = masked;
                    _lastDcValid[0] = false;
                    _lastDcValid[1] = false;
                }
                _vtxCount = 0; // Writing PRIM resets the vertex queue
                break;
            }

            case 0x01: // RGBAQ
                Rgbaq = value;
                _curR = (byte)(value & 0xFFul);
                _curG = (byte)((value >> 8) & 0xFFul);
                _curB = (byte)((value >> 16) & 0xFFul);
                _curA = (byte)((value >> 24) & 0xFFul);
                float parsedQ = BitConverter.UInt32BitsToSingle((uint)(value >> 32));
                _curQ = float.IsNaN(parsedQ) || float.IsInfinity(parsedQ) || parsedQ == 0.0f ? 1.0f : parsedQ;
                break;

            case 0x02: // ST
                St = value;
                _curS = BitConverter.UInt32BitsToSingle((uint)(value & 0xFFFFFFFFul));
                _curT = BitConverter.UInt32BitsToSingle((uint)(value >> 32));
                break;

            case 0x03: // UV
                Uv = value;
                _curU = (ushort)(value & 0x3FFFu);
                _curV = (ushort)((value >> 16) & 0x3FFFu);
                break;

            case 0x04: // XYZF2
                _curF = (byte)(value >> 56);
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)((value >> 32) & 0xFFFFFFu), disableDraw: false);
                break;

            case 0x05: // XYZ2
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)(value >> 32), disableDraw: false);
                break;

            case 0x06:
            case 0x07:
            {
                int ctx = regAddr - 0x06;
                if (Tex0[ctx] != value)
                {
                    Tex0[ctx] = value;
                    _lastDcValid[ctx] = false;
                }
                int cld = (int)((value >> 61) & 0x7ul);
                int cbp = (int)((value >> 37) & 0x3FFFul);
                var cpsm = (GsPsm)((value >> 51) & 0xFul);
                if (cld != 0 || _activeCbp == 0)
                {
                    if (_activeCbp != cbp || _activeCpsm != cpsm)
                    {
                        _activeCbp = cbp;
                        _activeCpsm = cpsm;
                        _lastDcValid[0] = false;
                        _lastDcValid[1] = false;
                    }
                }
                break;
            }
            case 0x08: if (Clamp[0] != value) { Clamp[0] = value; _lastDcValid[0] = false; } break;
            case 0x09: if (Clamp[1] != value) { Clamp[1] = value; _lastDcValid[1] = false; } break;
            case 0x0A: _curF = (byte)(value >> 56); break;

            case 0x0C: // XYZF3
                _curF = (byte)(value >> 56);
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)((value >> 32) & 0xFFFFFFu), disableDraw: true);
                break;

            case 0x0D: // XYZ3
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)(value >> 32), disableDraw: true);
                break;

            case 0x14: if (Tex1[0] != value) { Tex1[0] = value; _lastDcValid[0] = false; } break;
            case 0x15: if (Tex1[1] != value) { Tex1[1] = value; _lastDcValid[1] = false; } break;
            case 0x16: Tex2[0] = value; UpdateTex0FromTex2(0, value); break;
            case 0x17: Tex2[1] = value; UpdateTex0FromTex2(1, value); break;
            case 0x18: Xyoffset[0] = value; break;
            case 0x19: Xyoffset[1] = value; break;
            case 0x1A:
            {
                ulong masked = value & 1ul;
                if (Prmodecont != masked) { Prmodecont = masked; _lastDcValid[0] = false; _lastDcValid[1] = false; }
                break;
            }
            case 0x1B: if (Prmode != value) { Prmode = value; _lastDcValid[0] = false; _lastDcValid[1] = false; } break;
            case 0x1C: Texclut = value; break;
            case 0x22: Scanmsk = value; break;
            case 0x3B: if (Texa != value) { Texa = value; _lastDcValid[0] = false; _lastDcValid[1] = false; } break;
            case 0x3D: Fogcol = value; break;
            case 0x3F: break; // TEXFLUSH
            case 0x40:
            case 0x41:
            {
                int ctxIdx = regAddr - 0x40;
                ulong oldVal = Scissor[ctxIdx];
                Scissor[ctxIdx] = value;
                if (EnableDebugLogging && value != oldVal)
                {
                    int scx0 = (int)(value & 0x7FFul);
                    int scx1 = (int)((value >> 16) & 0x7FFul);
                    int scy0 = (int)((value >> 32) & 0x7FFul);
                    int scy1 = (int)((value >> 48) & 0x7FFul);
                    Console.WriteLine($"[GS SCISSOR_{ctxIdx+1}] value=0x{value:X16} (X:{scx0}-{scx1}, Y:{scy0}-{scy1}) from {_currentGifSource} | DMA PC=0x{RuntimeDispatcher.CurrentDmaPc:X8} RA=0x{RuntimeDispatcher.CurrentDmaRa:X8} Addr=0x{RuntimeDispatcher.CurrentDmaAddr:X8}");
                }
                break;
            }
            case 0x42: if (Alpha[0] != value) { Alpha[0] = value; _lastDcValid[0] = false; } break;
            case 0x43: if (Alpha[1] != value) { Alpha[1] = value; _lastDcValid[1] = false; } break;
            case 0x45: Dthe = value; break;
            case 0x46: if (Colclamp != value) { Colclamp = value; _lastDcValid[0] = false; _lastDcValid[1] = false; } break;
            case 0x47: if (Test[0] != value) { Test[0] = value; _lastDcValid[0] = false; } break;
            case 0x48: if (Test[1] != value) { Test[1] = value; _lastDcValid[1] = false; } break;
            case 0x49: Pabe = value; break;
            case 0x4A: if (Fba[0] != value) { Fba[0] = value; _lastDcValid[0] = false; } break;
            case 0x4B: if (Fba[1] != value) { Fba[1] = value; _lastDcValid[1] = false; } break;
            case 0x4C:
            case 0x4D:
            {
                int ctxIdx = regAddr - 0x4C;
                ulong oldVal = Frame[ctxIdx];
                if (oldVal != value)
                {
                    Frame[ctxIdx] = value;
                    _lastDcValid[ctxIdx] = false;
                }
                if (EnableDebugLogging && value != oldVal)
                {
                    int fbp = (int)(value & 0x1FFul);
                    if (TotalVramTransfers < 30 || (fbp * 32 != 0 && fbp * 32 != 0xA00))
                    {
                        int fbw = (int)((value >> 16) & 0x3Ful);
                        int psm = (int)((value >> 24) & 0x3Ful);
                        uint fbmsk = (uint)(value >> 32);
                        Console.WriteLine($"[GS FRAME_{ctxIdx+1}] f={CurrentFrame} value=0x{value:X16} (FBP=0x{fbp:X}/block 0x{fbp*32:X}, FBW={fbw*64}px, PSM=0x{psm:X2}/{(GsPsm)psm}, FBMSK=0x{fbmsk:X8}) from {_currentGifSource} | DMA PC=0x{RuntimeDispatcher.CurrentDmaPc:X8} RA=0x{RuntimeDispatcher.CurrentDmaRa:X8}");
                    }
                }
                break;
            }
            case 0x4E:
                if (Zbuf[0] != value) { Zbuf[0] = value; _lastDcValid[0] = false; }
                if (EnableDebugLogging)
                    Console.WriteLine($"[GS ZBUF_1] f={CurrentFrame} val=0x{value:X16} ZBP=0x{((value & 0x1FFul)*32):X} PSM=0x{((value>>24)&0xFul):X} ZMSK={(value>>32)&1ul} from {_currentGifSource}");
                break;
            case 0x4F:
                if (Zbuf[1] != value) { Zbuf[1] = value; _lastDcValid[1] = false; }
                if (EnableDebugLogging)
                    Console.WriteLine($"[GS ZBUF_2] f={CurrentFrame} val=0x{value:X16} ZBP=0x{((value & 0x1FFul)*32):X} PSM=0x{((value>>24)&0xFul):X} ZMSK={(value>>32)&1ul} from {_currentGifSource}");
                break;
            case 0x50: Bitbltbuf = value; break;
            case 0x51: Trxpos = value; break;
            case 0x52: Trxreg = value; break;
            case 0x53:
                Trxdir = value & 3ul;
                StartVramTransfer();
                break;
            case 0x54: // HWREG (64-bit write)
            {
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
                WriteHwregData(buf);
                break;
            }
            case 0x60: Signal = value; break;
            case 0x61: Finish = value; OnFinish?.Invoke(); break;
            case 0x62: Label = value; break;
        }
    }

    private void UpdateTex0FromTex2(int ctx, ulong tex2)
    {
        // TEX2 updates PSM (bits 20..25) and CLUT fields (bits 37..63) of TEX0
        const ulong mask = (0x3Ful << 20) | (0x7FFFFFFul << 37);
        ulong next = (Tex0[ctx] & ~mask) | (tex2 & mask);
        if (Tex0[ctx] != next)
        {
            Tex0[ctx] = next;
            _lastDcValid[ctx] = false;
        }

        int cld = (int)((tex2 >> 61) & 0x7ul);
        int cbp = (int)((tex2 >> 37) & 0x3FFFul);
        var cpsm = (GsPsm)((tex2 >> 51) & 0xFul);
        if (cld != 0 || _activeCbp == 0)
        {
            if (_activeCbp != cbp || _activeCpsm != cpsm)
            {
                _activeCbp = cbp;
                _activeCpsm = cpsm;
                _lastDcValid[0] = false;
                _lastDcValid[1] = false;
            }
        }
    }

    private void StartVramTransfer()
    {
        _trxBuffer.Clear();
        _trxPixelX = 0;
        _trxPixelY = 0;
        uint dir = (uint)(Trxdir & 3ul);

        int sbp = (int)(Bitbltbuf & 0x3FFFul);
        int sbw = Math.Max(1, (int)((Bitbltbuf >> 16) & 0x3Ful)) * 64;
        var spsm = (GsPsm)((Bitbltbuf >> 24) & 0x3Ful);
        int dbp = (int)((Bitbltbuf >> 32) & 0x3FFFul);
        int dbw = Math.Max(1, (int)((Bitbltbuf >> 48) & 0x3Ful)) * 64;
        var dpsm = (GsPsm)((Bitbltbuf >> 56) & 0x3Ful);
        int dsax = (int)((Trxpos >> 32) & 0x7FFul);
        int dsay = (int)((Trxpos >> 48) & 0x7FFul);
        int rrw = (int)(Trxreg & 0xFFFul);
        int rrh = (int)((Trxreg >> 32) & 0xFFFul);
        if (EnableDebugLogging && TotalVramTransfers < 25)
        {
            Console.WriteLine($"[GS TRX START #{TotalVramTransfers+1}] dir={dir} SBP=0x{sbp:X} DBP=0x{dbp:X} ({dbp*256/1024}KB) DBW={dbw} DPSM={dpsm} POS=({dsax},{dsay}) SIZE=({rrw}x{rrh}) from {_currentGifSource}");
        }

        if (dir == 0) // Host-to-Local
        {
            _trxActive = true;
            TotalVramTransfers++;
            if (EnableDebugLogging)
                Console.WriteLine($"[GS TRX HOST->VRAM] #{TotalVramTransfers} f={CurrentFrame} DBP=0x{dbp:X} DBW={dbw} DPSM={dpsm} POS=({dsax},{dsay}) SIZE=({rrw}x{rrh}) from {_currentGifSource}");
        }
        else if (dir == 1) // Local-to-Host
        {
            _trxActive = false;
            FlushAllDeferredVram();
            int ssax = (int)(Trxpos & 0x7FFul);
            int ssay = (int)((Trxpos >> 16) & 0x7FFul);
            PrepareVramToHostReadback(sbp, sbw, spsm, ssax, ssay, rrw, rrh);
            Console.WriteLine($"[GS TRX VRAM->HOST] f={CurrentFrame} SBP=0x{sbp:X} SBW={sbw} SPSM={spsm} POS=({ssax},{ssay}) SIZE=({rrw}x{rrh}) bytes={_readbackLength} from {_currentGifSource}");
        }
        else if (dir == 2) // Local-to-Local
        {
            _trxActive = false;
            if (EnableDebugLogging)
                Console.WriteLine($"[GS TRX LOCAL->LOCAL] #{TotalVramTransfers} f={CurrentFrame} SBP=0x{sbp:X} DBP=0x{dbp:X} DBW={dbw} DPSM={dpsm} POS=({dsax},{dsay}) SIZE=({rrw}x{rrh}) from {_currentGifSource}");
            long t0 = (EnableProfiling || FrameRecorder != null) ? Stopwatch.GetTimestamp() : 0;
            ExecuteLocalToLocalTransfer();
            if (t0 != 0) TotalVramTransferTicks += Stopwatch.GetTimestamp() - t0;
            TotalVramTransfers++;
        }
        else
        {
            _trxActive = false;
        }
    }

    private void WriteHwregData(ReadOnlySpan<byte> data)
    {
        if (!_trxActive)
            return;

        long t0 = (EnableProfiling || FrameRecorder != null) ? Stopwatch.GetTimestamp() : 0;
        try
        {
        int dbp = (int)((Bitbltbuf >> 32) & 0x3FFFul);
        int dbw = Math.Max(1, (int)((Bitbltbuf >> 48) & 0x3Ful)) * 64;
        var dpsm = (GsPsm)((Bitbltbuf >> 56) & 0x3Ful);

        int dsax = (int)((Trxpos >> 32) & 0x7FFul);
        int dsay = (int)((Trxpos >> 48) & 0x7FFul);

        int rrw = (int)(Trxreg & 0xFFFul);
        int rrh = (int)((Trxreg >> 32) & 0xFFFul);
        if (rrw <= 0 || rrh <= 0)
            return;

        ComputeBlockRange(dbp, dbw, dpsm, Math.Max(1, dsax + rrw), Math.Max(1, dsay + rrh), out int h2lStartBlk, out int h2lEndBlk);
        if (_deferredVramBakes.Count > 0)
        {
            FlushDeferredSpriteVramRange(h2lStartBlk, h2lEndBlk);
        }

        bool usedTrxBuffer = _trxBuffer.Count > 0;
        int fullTransferBytes = 0;
        if (!usedTrxBuffer && _trxPixelX == 0 && _trxPixelY == 0)
        {
            fullTransferBytes = dpsm switch
            {
                GsPsm.Psmct32 or GsPsm.Psmz32 => rrw * rrh * 4,
                GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S => rrw * rrh * 2,
                GsPsm.Psmt8 => rrw * rrh,
                GsPsm.Psmt4 => (rrw * rrh + 1) >> 1,
                _ => 0
            };
        }

        int effDbw = dpsm is GsPsm.Psmt4 or GsPsm.Psmt8 ? Math.Max(128, dbw) : dbw;
        bool canDeferH2l = Mode == GsRendererMode.OpenGl
            && !EnableDebugLogging
            && dsax == 0 && dsay == 0
            && fullTransferBytes > 1024 && data.Length >= fullTransferBytes
            && rrw <= effDbw
            && dpsm is GsPsm.Psmt4 or GsPsm.Psmt8 or GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmct32
            && (dpsm != GsPsm.Psmt4 || (rrw & 1) == 0);

        if (canDeferH2l)
        {
            var srcSlice = data.Slice(0, fullTransferBytes);
            ulong rawHash = ComputeBytesHash64(srcSlice);

            // Check if the newest overlapping deferred H2L write is an exact geometry match
            int exactIdx = -1;
            bool newestOverlapIsExact = false;
            for (int i = _deferredH2lUploads.Count - 1; i >= 0; i--)
            {
                var prev = _deferredH2lUploads[i];
                if (prev.Dbp == dbp && prev.Dbw == dbw && prev.Dpsm == dpsm && prev.Rrw == rrw && prev.Rrh == rrh)
                {
                    exactIdx = i;
                    if (!newestOverlapIsExact)
                        newestOverlapIsExact = true;
                    break;
                }
                if (BlocksOverlap(prev.StartBlock, prev.EndBlock, h2lStartBlk, h2lEndBlk))
                {
                    // An intervening overlapping write exists after any older exact match; keep looking for exact match to prune it.
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var older = _deferredH2lUploads[j];
                        if (older.Dbp == dbp && older.Dbw == dbw && older.Dpsm == dpsm && older.Rrw == rrw && older.Rrh == rrh)
                        {
                            ArrayPool<byte>.Shared.Return(older.RawBytes);
                            _deferredH2lUploads.RemoveAt(j);
                            break;
                        }
                    }
                    break;
                }
            }

            if (newestOverlapIsExact && exactIdx >= 0)
            {
                var existing = _deferredH2lUploads[exactIdx];
                if (existing.RawHash64 == rawHash && existing.ByteLen == fullTransferBytes)
                {
                    // Identical texture bytes uploaded to the same slot with no intervening overlap.
                    _trxPixelY = rrh;
                    _trxActive = false;
                    _trxBuffer.Clear();
                    if (FrameRecorder != null)
                    {
                        long elapsed = t0 != 0 ? (Stopwatch.GetTimestamp() - t0) : 0;
                        FrameRecorder.RecordTransfer(new TrxBucketKey("HostToLocal", 0, dbp, dpsm, rrw, rrh), fullTransferBytes, elapsed);
                    }
                    return;
                }

                // Reuse the exact same buffer in-place and move to end as newest.
                srcSlice.CopyTo(existing.RawBytes);
                existing.RawHash64 = rawHash;
                existing.ByteLen = fullTransferBytes;
                if (exactIdx != _deferredH2lUploads.Count - 1)
                {
                    _deferredH2lUploads.RemoveAt(exactIdx);
                    _deferredH2lUploads.Add(existing);
                }
                else
                {
                    _deferredH2lUploads[exactIdx] = existing;
                }
            }
            else
            {
                if (_deferredH2lUploads.Count >= MaxDeferredH2lEntries)
                {
                    var oldest = _deferredH2lUploads[0];
                    WriteDeferredH2lToVram(in oldest);
                    ArrayPool<byte>.Shared.Return(oldest.RawBytes);
                    _deferredH2lUploads.RemoveAt(0);
                }
                byte[] rented = ArrayPool<byte>.Shared.Rent(fullTransferBytes);
                srcSlice.CopyTo(rented);
                _deferredH2lUploads.Add(new DeferredH2lWrite
                {
                    Dbp = dbp,
                    Dbw = dbw,
                    Dpsm = dpsm,
                    Rrw = rrw,
                    Rrh = rrh,
                    StartBlock = h2lStartBlk,
                    EndBlock = h2lEndBlk,
                    ByteLen = fullTransferBytes,
                    RawHash64 = rawHash,
                    RawBytes = rented
                });
            }

            VramDirty = true;
            InvalidateDecodedTexturesAt(dbp, dbw, dpsm, rrw, rrh);
            _trxPixelY = rrh;
            _trxActive = false;
            _trxBuffer.Clear();
            if (FrameRecorder != null)
            {
                long elapsed = t0 != 0 ? (Stopwatch.GetTimestamp() - t0) : 0;
                FrameRecorder.RecordTransfer(new TrxBucketKey("HostToLocal", 0, dbp, dpsm, rrw, rrh), fullTransferBytes, elapsed);
            }
            return;
        }

        if (_deferredH2lUploads.Count > 0)
        {
            FlushDeferredH2lRange(h2lStartBlk, h2lEndBlk);
        }

        if (EnableDebugLogging && (TotalVramTransfers <= 15 || (TotalVramTransfers >= 180 && TotalVramTransfers <= 205)))
        {
            int nonZero = 0;
            foreach (byte b in data) if (b != 0) nonZero++;
            Console.WriteLine($"[GS TRX DATA #{TotalVramTransfers}] bytes={data.Length} nonZero={nonZero} DBP=0x{dbp:X} ({dbp*256/1024}KB) DPSM={dpsm} POS=({dsax},{dsay}) SIZE=({rrw}x{rrh})");
        }

        ReadOnlySpan<byte> buf;
        if (usedTrxBuffer)
        {
            _trxBuffer.AddRange(data);
            buf = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_trxBuffer);
        }
        else
        {
            buf = data;
        }
        int consumed;
        if (dpsm is GsPsm.Psmct32 or GsPsm.Psmz32)
        {
            consumed = GsSwizzle.WriteRect32(Vram, dbp, dbw, dsax, dsay, rrw, rrh, isZ: dpsm == GsPsm.Psmz32, buf, ref _trxPixelX, ref _trxPixelY);
        }
        else if (dpsm is GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S)
        {
            consumed = GsSwizzle.WriteRect16(
                Vram, dbp, dbw, dsax, dsay, rrw, rrh,
                is16S: dpsm is GsPsm.Psmct16S or GsPsm.Psmz16S,
                isZ: dpsm is GsPsm.Psmz16 or GsPsm.Psmz16S,
                buf, ref _trxPixelX, ref _trxPixelY);
        }
        else if (dpsm == GsPsm.Psmt8)
        {
            consumed = GsSwizzle.WriteRect8(Vram, dbp, dbw, dsax, dsay, rrw, rrh, buf, ref _trxPixelX, ref _trxPixelY);
        }
        else if (dpsm == GsPsm.Psmt4)
        {
            consumed = GsSwizzle.WriteRect4(Vram, dbp, dbw, dsax, dsay, rrw, rrh, buf, ref _trxPixelX, ref _trxPixelY);
        }
        else
        {
            consumed = 0;
            while (_trxPixelY < rrh)
            {
                int dstX = dsax + _trxPixelX;
                int dstY = dsay + _trxPixelY;

                if (dpsm is GsPsm.Psmct24 or GsPsm.Psmz24)
                {
                    if (consumed + 3 > buf.Length) break;
                    uint c24 = (uint)(buf[consumed] | (buf[consumed + 1] << 8) | (buf[consumed + 2] << 16));
                    int addr = GsSwizzle.GetPixelByteAddress32(dbp, dbw, dstX, dstY);
                    uint old32 = BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(addr, 4));
                    BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan(addr, 4), (old32 & 0xFF000000u) | c24);
                    consumed += 3;
                    AdvanceTrxPixel(rrw, rrh, 1);
                }
                else if (dpsm == GsPsm.Psmt8H)
                {
                    if (consumed + 1 > buf.Length) break;
                    int addr = GsSwizzle.GetPixelByteAddress32(dbp, dbw, dstX, dstY);
                    Vram[addr + 3] = buf[consumed];
                    consumed += 1;
                    AdvanceTrxPixel(rrw, rrh, 1);
                }
                else
                {
                    if (consumed + 4 > buf.Length) break;
                    uint c32 = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(consumed, 4));
                    GsSwizzle.WritePixel32(Vram, dbp, dbw, dstX, dstY, c32);
                    consumed += 4;
                    AdvanceTrxPixel(rrw, rrh, 1);
                }
            }
        }

        if (consumed > 0)
        {
            if (usedTrxBuffer)
                _trxBuffer.RemoveRange(0, consumed);
            VramDirty = true;
            if (_trxPixelY >= rrh)
            {
                InvalidateDecodedTexturesAt(dbp, dbw, dpsm, Math.Max(1, dsax + rrw), Math.Max(1, dsay + rrh));
            }
        }

        if (_trxPixelY >= rrh)
        {
            _trxActive = false;
            _trxBuffer.Clear();
        }
        else if (!usedTrxBuffer && consumed < data.Length)
        {
            _trxBuffer.AddRange(data.Slice(consumed));
        }

        if (FrameRecorder != null && consumed > 0)
        {
            long elapsed = t0 != 0 ? (Stopwatch.GetTimestamp() - t0) : 0;
            FrameRecorder.RecordTransfer(new TrxBucketKey("HostToLocal", 0, dbp, dpsm, rrw, rrh), consumed, elapsed);
        }
        }
        finally
        {
            if (t0 != 0) TotalVramTransferTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private void AdvanceTrxPixel(int rrw, int rrh, int step)
    {
        _trxPixelX += step;
        while (_trxPixelX >= rrw)
        {
            _trxPixelX -= rrw;
            _trxPixelY++;
        }
    }

    private byte[] _readbackBuffer = Array.Empty<byte>();
    private int _readbackPos;
    private int _readbackLength;

    public int ReadVramToHost(Span<byte> dest)
    {
        int avail = Math.Max(0, _readbackLength - _readbackPos);
        int toCopy = Math.Min(dest.Length, avail);
        if (toCopy > 0)
        {
            _readbackBuffer.AsSpan(_readbackPos, toCopy).CopyTo(dest);
            _readbackPos += toCopy;
        }
        if (toCopy < dest.Length)
        {
            dest.Slice(toCopy).Clear();
        }
        return dest.Length;
    }

    private void PrepareVramToHostReadback(int sbp, int sbw, GsPsm spsm, int ssax, int ssay, int rrw, int rrh)
    {
        _readbackPos = 0;
        _readbackLength = 0;
        if (rrw <= 0 || rrh <= 0)
            return;

        int totalPixels = rrw * rrh;
        int rawBytes = spsm switch
        {
            GsPsm.Psmct32 or GsPsm.Psmz32 => totalPixels * 4,
            GsPsm.Psmct24 or GsPsm.Psmz24 => totalPixels * 3,
            GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S => totalPixels * 2,
            GsPsm.Psmt8 or GsPsm.Psmt8H => totalPixels,
            GsPsm.Psmt4 or GsPsm.Psmt4HL or GsPsm.Psmt4HH => (totalPixels + 1) / 2,
            _ => totalPixels * 4
        };
        int alignedBytes = (rawBytes + 15) & ~15;
        if (_readbackBuffer.Length < alignedBytes)
            _readbackBuffer = new byte[alignedBytes];
        else
            Array.Clear(_readbackBuffer, 0, alignedBytes);

        int outIdx = 0;
        for (int y = 0; y < rrh; y++)
        {
            int srcY = ssay + y;
            for (int x = 0; x < rrw; x++)
            {
                int srcX = ssax + x;
                if (spsm is GsPsm.Psmct32 or GsPsm.Psmz32)
                {
                    uint c32 = GsSwizzle.ReadPixel32(Vram, sbp, sbw, srcX, srcY);
                    BinaryPrimitives.WriteUInt32LittleEndian(_readbackBuffer.AsSpan(outIdx, 4), c32);
                    outIdx += 4;
                }
                else if (spsm is GsPsm.Psmct24 or GsPsm.Psmz24)
                {
                    uint c32 = GsSwizzle.ReadPixel32(Vram, sbp, sbw, srcX, srcY);
                    _readbackBuffer[outIdx++] = (byte)(c32 & 0xFF);
                    _readbackBuffer[outIdx++] = (byte)((c32 >> 8) & 0xFF);
                    _readbackBuffer[outIdx++] = (byte)((c32 >> 16) & 0xFF);
                }
                else if (spsm is GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S)
                {
                    ushort c16 = GsSwizzle.ReadPixel16(Vram, sbp, sbw, srcX, srcY, is16S: spsm is GsPsm.Psmct16S or GsPsm.Psmz16S);
                    BinaryPrimitives.WriteUInt16LittleEndian(_readbackBuffer.AsSpan(outIdx, 2), c16);
                    outIdx += 2;
                }
                else if (spsm == GsPsm.Psmt8)
                {
                    _readbackBuffer[outIdx++] = GsSwizzle.ReadTexel8(Vram, sbp, Math.Max(128, sbw), srcX, srcY);
                }
                else if (spsm == GsPsm.Psmt8H)
                {
                    _readbackBuffer[outIdx++] = (byte)(GsSwizzle.ReadPixel32(Vram, sbp, sbw, srcX, srcY) >> 24);
                }
                else
                {
                    uint c32 = GsSwizzle.ReadPixel32(Vram, sbp, sbw, srcX, srcY);
                    BinaryPrimitives.WriteUInt32LittleEndian(_readbackBuffer.AsSpan(outIdx, 4), c32);
                    outIdx += 4;
                }
            }
        }
        _readbackLength = alignedBytes;
        int nonZeroWords = 0;
        for (int i = 0; i + 3 < _readbackLength; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(_readbackBuffer.AsSpan(i, 4)) != 0)
                nonZeroWords++;
        }
        Console.WriteLine($"[GS READBACK READY] nonZeroWords={nonZeroWords}/{_readbackLength / 4} firstWord=0x{(_readbackLength >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(_readbackBuffer.AsSpan(0, 4)) : 0u):X8}");
    }

    private void ExecuteLocalToLocalTransfer()
    {
        FlushAllDeferredVram();
        var rec = FrameRecorder;
        long t0 = rec != null ? Stopwatch.GetTimestamp() : 0;
        int sbp = (int)(Bitbltbuf & 0x3FFFul);
        int sbw = Math.Max(1, (int)((Bitbltbuf >> 16) & 0x3Ful)) * 64;
        var spsm = (GsPsm)((Bitbltbuf >> 24) & 0x3Ful);

        int dbp = (int)((Bitbltbuf >> 32) & 0x3FFFul);
        int dbw = Math.Max(1, (int)((Bitbltbuf >> 48) & 0x3Ful)) * 64;
        var dpsm = (GsPsm)((Bitbltbuf >> 56) & 0x3Ful);

        int ssax = (int)(Trxpos & 0x7FFul);
        int ssay = (int)((Trxpos >> 16) & 0x7FFul);
        int dsax = (int)((Trxpos >> 32) & 0x7FFul);
        int dsay = (int)((Trxpos >> 48) & 0x7FFul);

        int rrw = (int)(Trxreg & 0xFFFul);
        int rrh = (int)((Trxreg >> 32) & 0xFFFul);

        for (int y = 0; y < rrh; y++)
        {
            for (int x = 0; x < rrw; x++)
            {
                if (spsm == GsPsm.Psmt8 && dpsm == GsPsm.Psmt8)
                {
                    byte v = GsSwizzle.ReadTexel8(Vram, sbp, Math.Max(128, sbw), ssax + x, ssay + y);
                    GsSwizzle.WriteTexel8(Vram, dbp, Math.Max(128, dbw), dsax + x, dsay + y, v);
                }
                else if ((spsm is GsPsm.Psmct16 or GsPsm.Psmct16S) && (dpsm is GsPsm.Psmct16 or GsPsm.Psmct16S))
                {
                    ushort v = GsSwizzle.ReadPixel16(Vram, sbp, sbw, ssax + x, ssay + y, is16S: spsm == GsPsm.Psmct16S);
                    GsSwizzle.WritePixel16(Vram, dbp, dbw, dsax + x, dsay + y, v, is16S: dpsm == GsPsm.Psmct16S);
                }
                else
                {
                    uint v = GsSwizzle.ReadPixel32(Vram, sbp, sbw, ssax + x, ssay + y);
                    GsSwizzle.WritePixel32(Vram, dbp, dbw, dsax + x, dsay + y, v);
                }
            }
        }
        VramDirty = true;
        _cachedClutValid[0] = false;
        _cachedClutValid[1] = false;
        _lastDcValid[0] = false;
        _lastDcValid[1] = false;
        _lastDc[0].DecodedTexture = null;
        _lastDc[1].DecodedTexture = null;
        _decodedTexCache.Clear();
        if (rec != null)
        {
            rec.RecordTransfer(new TrxBucketKey("LocalToLocal", sbp, dbp, dpsm, rrw, rrh), rrw * rrh * 4L, Stopwatch.GetTimestamp() - t0);
        }
    }

    private void VertexKick(ushort x, ushort y, uint z, bool disableDraw)
    {
        TotalVerticesKicked++;

        var vtx = new GsVertex
        {
            X = x,
            Y = y,
            Z = z,
            R = _curR,
            G = _curG,
            B = _curB,
            A = _curA,
            F = _curF,
            S = _curS,
            T = _curT,
            Q = _curQ,
            U = _curU,
            V = _curV
        };

        var primType = ActivePrimType;
        if (EnableDebugLogging && TotalVerticesKicked >= 141 && TotalVerticesKicked <= 180)
        {
            int ctx = ActiveContext;
            var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);
            Console.WriteLine($"[VTX KICK #{TotalVerticesKicked}] prim={primType} ctx={ctx} dis={disableDraw} of=({ofx},{ofy}) sc=({scx0},{scy0})-({scx1},{scy1}) raw=({x},{y},{z}) px=({(x - ofx) / 16.0f:F1},{(y - ofy) / 16.0f:F1}) RGBA=0x{_curR:X2}{_curG:X2}{_curB:X2}{_curA:X2}");
        }
        switch (primType)
        {
            case GsPrimitiveType.Point:
                _vtxQueue[0] = vtx;
                _vtxCount = 0;
                if (!disableDraw)
                    RasterizePoint(_vtxQueue[0]);
                break;

            case GsPrimitiveType.Line:
                if (disableDraw)
                {
                    _vtxCount = 0;
                    break;
                }
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 2)
                {
                    _vtxCount = 0;
                    RasterizeLine(_vtxQueue[0], _vtxQueue[1]);
                }
                break;

            case GsPrimitiveType.LineStrip:
                if (disableDraw)
                {
                    _vtxQueue[0] = vtx;
                    _vtxCount = 1;
                    break;
                }
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount >= 2)
                {
                    RasterizeLine(_vtxQueue[0], _vtxQueue[1]);
                    _vtxQueue[0] = _vtxQueue[1];
                    _vtxCount = 1;
                }
                break;

            case GsPrimitiveType.Triangle:
                if (disableDraw)
                {
                    _vtxCount = 0;
                    break;
                }
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 3)
                {
                    _vtxCount = 0;
                    SubmitTriangle(_vtxQueue[0], _vtxQueue[1], _vtxQueue[2]);
                }
                break;

            case GsPrimitiveType.TriangleStrip:
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 3)
                {
                    if (!disableDraw)
                    {
                        SubmitTriangle(_vtxQueue[0], _vtxQueue[1], _vtxQueue[2]);
                    }
                    _vtxQueue[0] = _vtxQueue[1];
                    _vtxQueue[1] = _vtxQueue[2];
                    _vtxCount = 2;
                }
                break;

            case GsPrimitiveType.TriangleFan:
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 3)
                {
                    if (!disableDraw)
                    {
                        SubmitTriangle(_vtxQueue[0], _vtxQueue[1], _vtxQueue[2]);
                    }
                    _vtxQueue[1] = _vtxQueue[2];
                    _vtxCount = 2;
                }
                break;

            case GsPrimitiveType.Sprite:
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 2)
                {
                    _vtxCount = 0;
                    if (!disableDraw)
                        SubmitSprite(_vtxQueue[0], _vtxQueue[1]);
                }
                break;
        }
    }

    public int CurrentFrame { get; set; }
    public Action<int, int>? OnSpriteKicked { get; set; }
    private static int _frame800TotalPrims = 0;
    public static int Frame800ZFailLogs = 0;
    public static int Frame800ZPassLogs = 0;
    public static int Frame800TotalZPass = 0;
    public static int Frame800TotalZFail = 0;
    public static int Frame800TotalZeroAlpha = 0;
    public static int Frame800TotalColorWritten = 0;

    private void SubmitTriangle(in GsVertex v0, in GsVertex v1, in GsVertex v2)
    {
        SubmitTriangleClipped(in v0, in v1, in v2);
    }

    private void SubmitTriangleClipped(in GsVertex v0, in GsVertex v1, in GsVertex v2)
    {
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);

        const float inv16 = 0.0625f;
        const float inv128 = 0.0078125f;
        float fx0 = (v0.X - ofx) * inv16;
        float fy0 = (v0.Y - ofy) * inv16;
        float fx1 = (v1.X - ofx) * inv16;
        float fy1 = (v1.Y - ofy) * inv16;
        float fx2 = (v2.X - ofx) * inv16;
        float fy2 = (v2.Y - ofy) * inv16;

        // PS2 GS hardware rasterizer drops primitives whose bounding box exceeds 2048x2048 pixels
        float minFx = MathF.Min(fx0, MathF.Min(fx1, fx2));
        float maxFx = MathF.Max(fx0, MathF.Max(fx1, fx2));
        float minFy = MathF.Min(fy0, MathF.Min(fy1, fy2));
        float maxFy = MathF.Max(fy0, MathF.Max(fy1, fy2));
        if (maxFx - minFx >= 2048.0f || maxFy - minFy >= 2048.0f)
            return;

        // Early scissor bounding-box reject before BuildDrawContext / batching
        if (maxFx < scx0 || minFx > scx1 + 1 || maxFy < scy0 || minFy > scy1 + 1)
            return;

        bool gouraud = ((ActiveAttr >> 3) & 1ul) != 0;
        if (Mode == GsRendererMode.OpenGl && ActiveBatch != null)
        {
            int fastFbp = (int)(Frame[ctx] & 0x1FFul) * 32;
            if (fastFbp < 0x2000 && ((ActiveAttr >> 4) & 1ul) != 0)
            {
                int fastTbp0 = (int)(Tex0[ctx] & 0x3FFFul);
                if (fastTbp0 == 0 || fastTbp0 == 0xA00)
                    return;
            }
        }
        ref readonly var dc = ref BuildDrawContext(ctx, gouraud);

        if (Mode == GsRendererMode.OpenGl && ActiveBatch != null && dc.FbpBlock < 0x2000)
        {
            if (dc.Tme && (dc.Tbp0 == 0 || dc.Tbp0 == 0xA00))
                return;
            TotalPrimitivesDrawn++;
            TotalTrianglesDrawn++;
            if (dc.Tme) TotalTexturedTriangles++; else TotalFlatTriangles++;

            float invZ = dc.Zpsm == GsPsm.Psmz24 ? (1.0f / 16777215.0f) : (1.0f / (float)uint.MaxValue);
            float fz0 = Math.Clamp(v0.Z * invZ, 0.0f, 1.0f);
            float fz1 = Math.Clamp(v1.Z * invZ, 0.0f, 1.0f);
            float fz2 = Math.Clamp(v2.Z * invZ, 0.0f, 1.0f);

            byte r0 = gouraud ? v0.R : v2.R;
            byte grn0 = gouraud ? v0.G : v2.G;
            byte b0 = gouraud ? v0.B : v2.B;
            byte a0 = gouraud ? v0.A : v2.A;

            byte r1 = gouraud ? v1.R : v2.R;
            byte grn1 = gouraud ? v1.G : v2.G;
            byte b1 = gouraud ? v1.B : v2.B;
            byte a1 = gouraud ? v1.A : v2.A;

            var vtx0 = new GsGlVertex
            {
                X = fx0, Y = fy0, Z = fz0, W = 1.0f,
                R = r0 * inv128, G = grn0 * inv128, B = b0 * inv128, A = a0 * inv128,
                S = v0.S, T = v0.T, Q = v0.Q,
                U = v0.U * inv16, V = v0.V * inv16
            };
            var vtx1 = new GsGlVertex
            {
                X = fx1, Y = fy1, Z = fz1, W = 1.0f,
                R = r1 * inv128, G = grn1 * inv128, B = b1 * inv128, A = a1 * inv128,
                S = v1.S, T = v1.T, Q = v1.Q,
                U = v1.U * inv16, V = v1.V * inv16
            };
            var vtx2 = new GsGlVertex
            {
                X = fx2, Y = fy2, Z = fz2, W = 1.0f,
                R = v2.R * inv128, G = v2.G * inv128, B = v2.B * inv128, A = v2.A * inv128,
                S = v2.S, T = v2.T, Q = v2.Q,
                U = v2.U * inv16, V = v2.V * inv16
            };

            ActiveBatch.AddTriangle(in dc, scx0, scy0, scx1, scy1, ofx, ofy, in vtx0, in vtx1, in vtx2);
            return;
        }

        RasterizeTriangle(v0, v1, v2);
        if (_softTriInvalidatedFbp != dc.FbpBlock || _softTriInvalidatedFbw != dc.FbwPixels || _softTriInvalidatedFpsm != dc.Fpsm)
        {
            InvalidateDecodedTexturesAt(dc.FbpBlock, dc.FbwPixels, dc.Fpsm, dc.FbwPixels, 512);
            _softTriInvalidatedFbp = dc.FbpBlock;
            _softTriInvalidatedFbw = dc.FbwPixels;
            _softTriInvalidatedFpsm = dc.Fpsm;
        }
    }

    private void SubmitSprite(in GsVertex v0, in GsVertex v1)
    {
        int ctx = ActiveContext;
        ulong frameReg = Frame[ctx];
        int fbpBlock = (int)(frameReg & 0x1FFul) * 32;
        if (Mode == GsRendererMode.OpenGl && ActiveBatch != null && fbpBlock < 0x2000)
        {
            if (((ActiveAttr >> 4) & 1ul) != 0)
            {
                int fastTbp0 = (int)(Tex0[ctx] & 0x3FFFul);
                if (fastTbp0 == 0 || fastTbp0 == 0xA00)
                    return;
            }

            var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);
            float fx0 = (v0.X - ofx) / 16.0f;
            float fy0 = (v0.Y - ofy) / 16.0f;
            float fx1 = (v1.X - ofx) / 16.0f;
            float fy1 = (v1.Y - ofy) / 16.0f;
            float minFx = MathF.Min(fx0, fx1), maxFx = MathF.Max(fx0, fx1);
            float minFy = MathF.Min(fy0, fy1), maxFy = MathF.Max(fy0, fy1);
            if (maxFx < scx0 || minFx > scx1 + 1 || maxFy < scy0 || minFy > scy1 + 1)
                return;
            ref readonly var dc = ref BuildDrawContext(ctx, false);
            if (dc.Tme && (dc.Tbp0 == 0 || dc.Tbp0 == 0xA00))
                return;
            if (MathF.Abs(fx1 - fx0) < 2048.0f && MathF.Abs(fy1 - fy0) < 2048.0f)
            {
                TotalPrimitivesDrawn++;
                TotalSpritesDrawn++;
                float invZ = dc.Zpsm == GsPsm.Psmz24 ? (1.0f / 16777215.0f) : (1.0f / (float)uint.MaxValue);
                float fz1 = Math.Clamp(v1.Z * invZ, 0.0f, 1.0f);
                var glV0 = new GsGlVertex
                {
                    X = fx0, Y = fy0, Z = fz1, W = 1.0f,
                    R = v1.R / 128.0f, G = v1.G / 128.0f, B = v1.B / 128.0f, A = v1.A / 128.0f,
                    S = v0.S, T = v0.T, Q = v1.Q,
                    U = v0.U / 16.0f, V = v0.V / 16.0f
                };
                var glV1 = new GsGlVertex
                {
                    X = fx1, Y = fy1, Z = fz1, W = 1.0f,
                    R = v1.R / 128.0f, G = v1.G / 128.0f, B = v1.B / 128.0f, A = v1.A / 128.0f,
                    S = v1.S, T = v1.T, Q = v1.Q,
                    U = v1.U / 16.0f, V = v1.V / 16.0f
                };
                ActiveBatch.AddSprite(in dc, scx0, scy0, scx1, scy1, ofx, ofy, in glV0, in glV1);
            }
            return;
        }

        if (Mode == GsRendererMode.OpenGl && fbpBlock >= 0x3100 && fbpBlock < 0x32A0 && ((ActiveAttr >> 4) & 1ul) != 0)
        {
            return;
        }

        RasterizeSprite(v0, v1, out int maxWrittenX, out int maxWrittenY, out bool cacheUpdatedInPlace);
        if (!cacheUpdatedInPlace && maxWrittenX >= 0 && maxWrittenY >= 0)
        {
            int fbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64;
            var fpsm = (GsPsm)((frameReg >> 24) & 0x3Ful);
            InvalidateDecodedTexturesAt(fbpBlock, fbwPixels, fpsm, maxWrittenX + 1, maxWrittenY + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int OffsetX, int OffsetY, int Scx0, int Scy0, int Scx1, int Scy1) GetViewportBounds(int ctx)
    {
        ulong xyoff = Xyoffset[ctx];
        int ofx = (int)(xyoff & 0xFFFFul);
        int ofy = (int)((xyoff >> 32) & 0xFFFFul);

        ulong sc = Scissor[ctx];
        int scx0 = (int)(sc & 0x7FFul);
        int scx1 = (int)((sc >> 16) & 0x7FFul);
        int scy0 = (int)((sc >> 32) & 0x7FFul);
        int scy1 = (int)((sc >> 48) & 0x7FFul);

        return (ofx, ofy, scx0, scy0, scx1, scy1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly GsDrawContext BuildDrawContext(int ctx, bool gouraud)
    {
        if (_lastDcValid[ctx]
            && _lastDcGouraud[ctx] == gouraud
            && _lastDcMode[ctx] == Mode
            && ReferenceEquals(_lastDcBatch[ctx], ActiveBatch))
        {
            return ref _lastDc[ctx];
        }

        ulong attr = ActiveAttr;
        ulong frameReg = Frame[ctx];
        ulong zbufReg = Zbuf[ctx];
        ulong testReg = Test[ctx];
        ulong tex0 = Tex0[ctx];
        ulong clampReg = Clamp[ctx];
        ulong tex1 = Tex1[ctx];
        ulong alphaReg = Alpha[ctx];

        int cld = (int)((tex0 >> 61) & 0x7ul);
        int cbp = (int)((tex0 >> 37) & 0x3FFFul);
        var cpsm = (GsPsm)((tex0 >> 51) & 0xFul);
        if (cbp == 0 && _activeCbp != 0)
        {
            cbp = _activeCbp;
            cpsm = _activeCpsm;
        }
        else if (cld != 0)
        {
            _activeCbp = cbp;
            _activeCpsm = cpsm;
        }

        bool ate = (testReg & 1ul) != 0;
        int afail = (int)((testReg >> 12) & 3ul);
        bool zte = ((testReg >> 16) & 1ul) != 0;
        bool canEarlyZ = zte && (!ate || afail == 0 || afail == 2);

        bool tme = ((attr >> 4) & 1ul) != 0;
        var psm = (GsPsm)((tex0 >> 20) & 0x3Ful);
        uint[]? clutTable = null;

        if (tme && psm is GsPsm.Psmt8 or GsPsm.Psmt8H or GsPsm.Psmt4 or GsPsm.Psmt4HL or GsPsm.Psmt4HH)
        {
            int csa = (int)((tex0 >> 56) & 0x1Ful);
            byte ta0 = (byte)(Texa & 0xFFul);
            byte ta1 = (byte)((Texa >> 32) & 0xFFul);
            bool aem = ((Texa >> 15) & 1ul) != 0;
            bool is8Bit = psm is GsPsm.Psmt8 or GsPsm.Psmt8H;

            bool clutDirty = !_cachedClutValid[ctx]
                || _cachedClutCbp[ctx] != cbp
                || _cachedClutCpsm[ctx] != cpsm
                || _cachedClutCsa[ctx] != csa
                || _cachedClutTa0[ctx] != ta0
                || _cachedClutTa1[ctx] != ta1
                || _cachedClutAem[ctx] != aem
                || _cachedClutIs8Bit[ctx] != is8Bit;

            if (clutDirty)
            {
                if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
                    FlushDeferredVramRange(cbp, cbp + 3);
                uint[] cache = _clutCache[ctx];
                if (is8Bit)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        cache[i] = GsSwizzle.LookupClutCsm1(Vram, cbp, cpsm, csa, (byte)i, is8Bit: true, ta0, ta1, aem);
                    }
                    _cachedClutHash64[ctx] = ComputePixelsHash64(cache, 256);
                }
                else
                {
                    for (int i = 0; i < 16; i++)
                    {
                        cache[i] = GsSwizzle.LookupClutCsm1(Vram, cbp, cpsm, csa, (byte)i, is8Bit: false, ta0, ta1, aem);
                    }
                    _cachedClutHash64[ctx] = ComputePixelsHash64(cache, 16);
                }

                _cachedClutValid[ctx] = true;
                _cachedClutCbp[ctx] = cbp;
                _cachedClutCpsm[ctx] = cpsm;
                _cachedClutCsa[ctx] = csa;
                _cachedClutTa0[ctx] = ta0;
                _cachedClutTa1[ctx] = ta1;
                _cachedClutAem[ctx] = aem;
                _cachedClutIs8Bit[ctx] = is8Bit;
            }

            clutTable = _clutCache[ctx];
        }

        var dc = new GsDrawContext
        {
            Context = ctx,
            Gouraud = gouraud,
            Tme = tme,
            Abe = ((attr >> 6) & 1ul) != 0,
            Fst = ((attr >> 8) & 1ul) != 0,

            FbpBlock = (int)(frameReg & 0x1FFul) * 32,
            FbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64,
            Fpsm = (GsPsm)((frameReg >> 24) & 0x3Ful),
            Fbmsk = (uint)(frameReg >> 32),

            ZbpBlock = (int)(zbufReg & 0x1FFul) * 32,
            Zpsm = (GsPsm)(((zbufReg >> 24) & 0xFul) | 0x30ul),
            Zmsk = ((zbufReg >> 32) & 1ul) != 0,

            Ate = ate,
            Atst = (int)((testReg >> 1) & 7ul),
            Aref = (byte)((testReg >> 4) & 0xFFul),
            Afail = afail,
            Date = ((testReg >> 14) & 1ul) != 0,
            Datm = (int)((testReg >> 15) & 1ul),
            Zte = zte,
            Ztst = (int)((testReg >> 17) & 3ul),

            Tbp0 = (int)(tex0 & 0x3FFFul),
            Tbw = Math.Max(1, (int)((tex0 >> 14) & 0x3Ful)) * 64,
            Psm = psm,
            Tw = 1 << (int)Math.Min(10ul, (tex0 >> 26) & 0xFul),
            Th = 1 << (int)Math.Min(10ul, (tex0 >> 30) & 0xFul),
            Tcc = (int)((tex0 >> 34) & 1ul),
            Tfx = (int)((tex0 >> 35) & 3ul),
            Cbp = cbp,
            Cpsm = cpsm,
            Csa = (int)((tex0 >> 56) & 0x1Ful),

            Wms = (int)(clampReg & 3ul),
            Wmt = (int)((clampReg >> 2) & 3ul),
            MinU = (uint)((clampReg >> 4) & 0x3FFul),
            MaxU = (uint)((clampReg >> 14) & 0x3FFul),
            MinV = (uint)((clampReg >> 24) & 0x3FFul),
            MaxV = (uint)((clampReg >> 34) & 0x3FFul),

            Ta0 = (byte)(Texa & 0xFFul),
            Ta1 = (byte)((Texa >> 32) & 0xFFul),
            Aem = ((Texa >> 15) & 1ul) != 0,
            LinearFilter = ((tex1 >> 5) & 1ul) != 0,

            SpecA = (int)(alphaReg & 3ul),
            SpecB = (int)((alphaReg >> 2) & 3ul),
            SpecC = (int)((alphaReg >> 4) & 3ul),
            SpecD = (int)((alphaReg >> 6) & 3ul),
            Fix = (byte)((alphaReg >> 32) & 0xFFul),
            Colclamp = (Colclamp & 1ul) != 0,
            Fba = (Fba[ctx] & 1ul) != 0,

            CanEarlyZ = canEarlyZ,
            ClutTable = clutTable,
            DecodedTexture = null,
            DecodedTextureHash64 = 0,
            TexIndex = -1
        };

        if (tme && dc.Tw <= 1024 && dc.Th <= 1024)
        {
            bool needBatchTex = Mode == GsRendererMode.OpenGl && ActiveBatch != null && dc.FbpBlock < 0x2000;
            var entry = GetOrDecodeTexture(in dc, needHash: true);
            dc.DecodedTexture = entry.Pixels;
            dc.DecodedTextureHash64 = entry.Hash64;
            if (needBatchTex)
            {
                entry.SharedWithBatch = true;
                dc.TexIndex = ActiveBatch!.RegisterTexture(dc.Tw, dc.Th, entry.Hash64, entry.Pixels);
            }
        }

        int ver = (_drawContextVersionCounter + 1) & 0x7FFFFFFF;
        if (ver == 0) ver = 1;
        _drawContextVersionCounter = ver;
        dc.StateVersion = ver;
        _lastDcValid[ctx] = true;
        _lastDc[ctx] = dc;
        _lastDcGouraud[ctx] = gouraud;
        _lastDcMode[ctx] = Mode;
        _lastDcBatch[ctx] = ActiveBatch;

        return ref _lastDc[ctx];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPalettedPsm(GsPsm psm)
        => psm is GsPsm.Psmt8 or GsPsm.Psmt8H or GsPsm.Psmt4 or GsPsm.Psmt4HL or GsPsm.Psmt4HH;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BlocksOverlap(int aStart, int aEnd, int bStart, int bEnd)
        => aStart <= bEnd && bStart <= aEnd;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeBlockRange(int baseBlock, int bwPixels, GsPsm psm, int width, int height, out int startBlock, out int endBlock)
    {
        startBlock = baseBlock;
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        int pagesPerRow, pageCol, pageRow;
        switch (psm)
        {
            case GsPsm.Psmt4:
                pagesPerRow = Math.Max(1, Math.Max(128, bwPixels) >> 7);
                pageCol = (w - 1) >> 7;
                pageRow = (h - 1) >> 7;
                if (pageCol == 0 && pageRow == 0)
                {
                    int maxBx = (w - 1) >> 5;
                    int maxBy = (h - 1) >> 4;
                    endBlock = baseBlock + GsSwizzle.BlockTable16[(maxBy << 2) + maxBx];
                    return;
                }
                break;
            case GsPsm.Psmt8:
                pagesPerRow = Math.Max(1, Math.Max(128, bwPixels) >> 7);
                pageCol = (w - 1) >> 7;
                pageRow = (h - 1) >> 6;
                if (pageCol == 0 && pageRow == 0)
                {
                    int maxBx = (w - 1) >> 4;
                    int maxBy = (h - 1) >> 4;
                    endBlock = baseBlock + GsSwizzle.BlockTable32[(maxBy << 3) + maxBx];
                    return;
                }
                break;
            case GsPsm.Psmct16:
            case GsPsm.Psmct16S:
            case GsPsm.Psmz16:
            case GsPsm.Psmz16S:
                pagesPerRow = Math.Max(1, bwPixels >> 6);
                pageCol = (w - 1) >> 6;
                pageRow = (h - 1) >> 6;
                if (psm == GsPsm.Psmct16 && pageCol == 0 && pageRow == 0)
                {
                    int maxBx = (w - 1) >> 4;
                    int maxBy = (h - 1) >> 3;
                    endBlock = baseBlock + GsSwizzle.BlockTable16[(maxBy << 2) + maxBx];
                    return;
                }
                break;
            default:
                pagesPerRow = Math.Max(1, bwPixels >> 6);
                pageCol = (w - 1) >> 6;
                pageRow = (h - 1) >> 5;
                if (pageCol == 0 && pageRow == 0 && psm is GsPsm.Psmct32 or GsPsm.Psmct24 or GsPsm.Psmt8H or GsPsm.Psmt4HL or GsPsm.Psmt4HH)
                {
                    int maxBx = (w - 1) >> 3;
                    int maxBy = (h - 1) >> 3;
                    endBlock = baseBlock + GsSwizzle.BlockTable32[(maxBy << 3) + maxBx];
                    return;
                }
                break;
        }
        int lastPageIdx = pageRow * pagesPerRow + pageCol;
        endBlock = baseBlock + lastPageIdx * 32 + 31;
    }

    private static unsafe ulong ComputeBytesHash64(ReadOnlySpan<byte> bytes)
    {
        uint crc0 = 0x12345678u;
        uint crc1 = 0x9ABCDEF0u;
        int len = bytes.Length;
        fixed (byte* p = bytes)
        {
            int i = 0;
            while (i + 32 <= len)
            {
                crc0 = BitOperations.Crc32C(crc0, *(ulong*)(p + i));
                crc1 = BitOperations.Crc32C(crc1, *(ulong*)(p + i + 8));
                crc0 = BitOperations.Crc32C(crc0, *(ulong*)(p + i + 16));
                crc1 = BitOperations.Crc32C(crc1, *(ulong*)(p + i + 24));
                i += 32;
            }
            while (i + 8 <= len)
            {
                crc0 = BitOperations.Crc32C(crc0, *(ulong*)(p + i));
                i += 8;
            }
            while (i < len)
            {
                crc1 = BitOperations.Crc32C(crc1, p[i]);
                i++;
            }
        }
        crc0 = BitOperations.Crc32C(crc0, (uint)len);
        return ((ulong)crc0 << 32) | crc1;
    }

    private void InvalidateDecodedTexturesAt(int fbpBlock, int fbwPixels, GsPsm fpsm, int width, int height)
    {
        if (fbpBlock == 0 || fbpBlock == 0xA00)
            return;

        ComputeBlockRange(fbpBlock, fbwPixels, fpsm, width, height, out int startBlock, out int endBlock);

        if (_cachedClutValid[0] && BlocksOverlap(_cachedClutCbp[0], _cachedClutCbp[0] + 3, startBlock, endBlock))
            _cachedClutValid[0] = false;
        if (_cachedClutValid[1] && BlocksOverlap(_cachedClutCbp[1], _cachedClutCbp[1] + 3, startBlock, endBlock))
            _cachedClutValid[1] = false;

        for (int i = 0; i < 2; i++)
        {
            if (!_lastDcValid[i])
                continue;
            ref readonly var ldc = ref _lastDc[i];
            if (!ldc.Tme)
                continue;
            ComputeBlockRange(ldc.Tbp0, ldc.Tbw, ldc.Psm, ldc.Tw, ldc.Th, out int ts, out int te);
            if (BlocksOverlap(ts, te, startBlock, endBlock) ||
                (IsPalettedPsm(ldc.Psm) && BlocksOverlap(ldc.Cbp, ldc.Cbp + 3, startBlock, endBlock)))
            {
                _lastDcValid[i] = false;
                _lastDc[i].DecodedTexture = null;
            }
        }

        if (_decodedTexCache.Count == 0)
            return;

        _invRemoveScratch.Clear();
        foreach (var kv in _decodedTexCache)
        {
            var e = kv.Value;
            if (BlocksOverlap(e.StartBlock, e.EndBlock, startBlock, endBlock) ||
                (e.ClutEndBlock >= e.ClutStartBlock && BlocksOverlap(e.ClutStartBlock, e.ClutEndBlock, startBlock, endBlock)))
            {
                _invRemoveScratch.Add(kv.Key);
            }
        }
        int removeCount = _invRemoveScratch.Count;
        if (removeCount > 0)
        {
            for (int i = 0; i < removeCount; i++)
            {
                if (_decodedTexCache.Remove(_invRemoveScratch[i], out var removed) && !removed.SharedWithBatch && removed.Pixels.Length > 0)
                {
                    ReturnTexBuffer(removed.Pixels);
                }
            }
            _invRemoveScratch.Clear();
        }
    }

    private uint[] RentTexBuffer(int length)
    {
        if (_texBufferPool.TryGetValue(length, out var stack) && stack.Count > 0)
            return stack.Pop();
        return new uint[length];
    }

    private void ReturnTexBuffer(uint[] buf)
    {
        if (!_texBufferPool.TryGetValue(buf.Length, out var stack))
        {
            if (_texBufferPool.Count >= 32)
                return;
            stack = new Stack<uint[]>(8);
            _texBufferPool[buf.Length] = stack;
        }
        if (stack.Count < 8)
            stack.Push(buf);
    }

    private GsCachedTexEntry GetOrDecodeTexture(in GsDrawContext dc, bool needHash)
    {
        var psm = dc.Psm;
        bool paletted = IsPalettedPsm(psm);
        var key = new GsTexCacheKey(
            dc.Tbp0, dc.Tbw, psm, dc.Tw, dc.Th,
            paletted ? dc.Cbp : 0,
            paletted ? dc.Cpsm : GsPsm.Psmct32,
            paletted ? dc.Csa : 0,
            psm == GsPsm.Psmct32 ? (byte)0 : dc.Ta0,
            (psm == GsPsm.Psmct32 || psm == GsPsm.Psmct24) ? (byte)0 : dc.Ta1,
            psm == GsPsm.Psmct32 ? false : dc.Aem);

        if (_decodedTexCache.TryGetValue(key, out var entry))
        {
            if (needHash && !entry.HasHash)
            {
                entry.Hash64 = ComputePixelsHash64(entry.Pixels, dc.Tw * dc.Th);
                entry.HasHash = true;
            }
            return entry;
        }

        if (_decodedTexCache.Count >= 1024)
            _decodedTexCache.Clear();

        int w = dc.Tw;
        int h = dc.Th;
        int len = w * h;
        ComputeBlockRange(dc.Tbp0, dc.Tbw, psm, w, h, out int startBlock, out int endBlock);

        if (_deferredH2lUploads.Count > 0)
        {
            int effTbw = psm is GsPsm.Psmt4 or GsPsm.Psmt8 ? Math.Max(128, dc.Tbw) : dc.Tbw;
            for (int i = _deferredH2lUploads.Count - 1; i >= 0; i--)
            {
                var d = _deferredH2lUploads[i];
                if (!BlocksOverlap(d.StartBlock, d.EndBlock, startBlock, endBlock))
                    continue;

                int effDbw = d.Dpsm is GsPsm.Psmt4 or GsPsm.Psmt8 ? Math.Max(128, d.Dbw) : d.Dbw;
                bool exactMatch = d.Dbp == dc.Tbp0 && effDbw == effTbw && d.Dpsm == psm && d.Rrw == w && d.Rrh == h
                    && (!paletted || dc.ClutTable != null);
                if (exactMatch)
                {
                    var rawKey = new RawTexDecodeKey(
                        d.RawHash64,
                        paletted ? _cachedClutHash64[dc.Context] : 0ul,
                        psm, w, h,
                        psm == GsPsm.Psmct32 ? (byte)0 : dc.Ta0,
                        (psm == GsPsm.Psmct32 || psm == GsPsm.Psmct24) ? (byte)0 : dc.Ta1,
                        psm == GsPsm.Psmct32 ? false : dc.Aem);

                    if (_rawTexDecodeCache.TryGetValue(rawKey, out var rawHit))
                    {
                        H2lDedupHits++;
                        entry = new GsCachedTexEntry
                        {
                            StartBlock = startBlock,
                            EndBlock = endBlock,
                            ClutStartBlock = paletted ? dc.Cbp : 0,
                            ClutEndBlock = paletted ? dc.Cbp + 3 : -1,
                            Hash64 = rawHit.DstHash64,
                            HasHash = true,
                            SharedWithBatch = true,
                            Pixels = rawHit.Pixels
                        };
                        _decodedTexCache[key] = entry;
                        _softTriInvalidatedFbp = -1;
                        return entry;
                    }

                    H2lDedupMisses++;
                    var decodedPixels = new uint[len];
                    DecodeLinearRawToPixels(d.RawBytes, d.ByteLen, psm, w, h, decodedPixels, dc.ClutTable, dc.Ta0, dc.Ta1, dc.Aem);
                    ulong decodedHash = ComputePixelsHash64(decodedPixels, len);
                    if (_rawTexDecodeCache.Count >= MaxRawTexDecodeCacheEntries && _rawTexDecodeFifo.Count > 0)
                    {
                        _rawTexDecodeCache.Remove(_rawTexDecodeFifo.Dequeue());
                    }
                    _rawTexDecodeCache[rawKey] = new RawTexDecodeEntry
                    {
                        DstHash64 = decodedHash,
                        Pixels = decodedPixels
                    };
                    _rawTexDecodeFifo.Enqueue(rawKey);

                    entry = new GsCachedTexEntry
                    {
                        StartBlock = startBlock,
                        EndBlock = endBlock,
                        ClutStartBlock = paletted ? dc.Cbp : 0,
                        ClutEndBlock = paletted ? dc.Cbp + 3 : -1,
                        Hash64 = decodedHash,
                        HasHash = true,
                        SharedWithBatch = true,
                        Pixels = decodedPixels
                    };
                    _decodedTexCache[key] = entry;
                    _softTriInvalidatedFbp = -1;
                    return entry;
                }

                // Overlapping but non-exact deferred upload: materialize overlapping uploads in chronological order.
                FlushDeferredH2lRange(startBlock, endBlock);
                break;
            }
        }

        if (_deferredVramBakes.Count > 0)
        {
            FlushDeferredSpriteVramRange(startBlock, endBlock);
        }
        var pixels = RentTexBuffer(len);

        if (psm == GsPsm.Psmct32)
        {
            DecodePsmct32Fast(Vram, dc.Tbp0, dc.Tbw, w, h, pixels);
        }
        else if (psm == GsPsm.Psmt8 && dc.ClutTable != null)
        {
            DecodePsmt8Fast(Vram, dc.Tbp0, dc.Tbw, w, h, pixels, dc.ClutTable);
        }
        else if (psm == GsPsm.Psmt4 && dc.ClutTable != null)
        {
            DecodePsmt4Fast(Vram, dc.Tbp0, dc.Tbw, w, h, pixels, dc.ClutTable);
        }
        else if (psm is GsPsm.Psmct16 or GsPsm.Psmct16S)
        {
            DecodePsmct16Fast(Vram, dc.Tbp0, dc.Tbw, w, h, pixels, psm == GsPsm.Psmct16S, dc.Ta0, dc.Ta1, dc.Aem);
        }
        else
        {
            for (int ty = 0; ty < h; ty++)
            {
                int row = ty * w;
                for (int tx = 0; tx < w; tx++)
                {
                    pixels[row + tx] = ReadSingleTexelFromVram(in dc, tx, ty);
                }
            }
        }

        ulong hash = needHash ? ComputePixelsHash64(pixels, len) : 0ul;
        entry = new GsCachedTexEntry
        {
            StartBlock = startBlock,
            EndBlock = endBlock,
            ClutStartBlock = paletted ? dc.Cbp : 0,
            ClutEndBlock = paletted ? dc.Cbp + 3 : -1,
            Hash64 = hash,
            HasHash = needHash,
            Pixels = pixels
        };
        _decodedTexCache[key] = entry;
        _softTriInvalidatedFbp = -1;
        return entry;
    }

    private static unsafe ulong ComputePixelsHash64(uint[] pixels, int count)
    {
        uint crc0 = 0x12345678u;
        uint crc1 = 0x9ABCDEF0u;
        fixed (uint* p = pixels)
        {
            ulong* p64 = (ulong*)p;
            int count64 = count >> 1;
            int i = 0;
            for (; i + 3 < count64; i += 4)
            {
                crc0 = BitOperations.Crc32C(crc0, p64[i]);
                crc1 = BitOperations.Crc32C(crc1, p64[i + 1]);
                crc0 = BitOperations.Crc32C(crc0, p64[i + 2]);
                crc1 = BitOperations.Crc32C(crc1, p64[i + 3]);
            }
            for (; i < count64; i++)
            {
                crc0 = BitOperations.Crc32C(crc0, p64[i]);
            }
            if ((count & 1) != 0)
            {
                crc1 = BitOperations.Crc32C(crc1, p[count - 1]);
            }
        }
        return ((ulong)crc1 << 32) | crc0;
    }

    private static unsafe void DecodeLinearRawToPixels(
        byte[] rawBytes, int byteLen, GsPsm psm, int w, int h, uint[] pixels,
        uint[]? clutTable, byte ta0, byte ta1, bool aem)
    {
        int len = w * h;
        if (psm == GsPsm.Psmct32)
        {
            int copyBytes = Math.Min(byteLen, len * 4);
            Buffer.BlockCopy(rawBytes, 0, pixels, 0, copyBytes);
            return;
        }

        if (psm == GsPsm.Psmt8 && clutTable != null)
        {
            int count = Math.Min(byteLen, len);
            fixed (byte* srcPtr = rawBytes)
            fixed (uint* dstPtr = pixels)
            fixed (uint* clutPtr = clutTable)
            {
                int i = 0;
                for (; i + 3 < count; i += 4)
                {
                    dstPtr[i] = clutPtr[srcPtr[i]];
                    dstPtr[i + 1] = clutPtr[srcPtr[i + 1]];
                    dstPtr[i + 2] = clutPtr[srcPtr[i + 2]];
                    dstPtr[i + 3] = clutPtr[srcPtr[i + 3]];
                }
                for (; i < count; i++)
                {
                    dstPtr[i] = clutPtr[srcPtr[i]];
                }
            }
            return;
        }

        if (psm == GsPsm.Psmt4 && clutTable != null)
        {
            int byteCount = Math.Min(byteLen, len >> 1);
            fixed (byte* srcPtr = rawBytes)
            fixed (uint* dstPtr = pixels)
            fixed (uint* clutPtr = clutTable)
            {
                int outIdx = 0;
                for (int i = 0; i < byteCount; i++, outIdx += 2)
                {
                    byte packed = srcPtr[i];
                    dstPtr[outIdx] = clutPtr[packed & 0x0F];
                    dstPtr[outIdx + 1] = clutPtr[packed >> 4];
                }
            }
            return;
        }

        if (psm is GsPsm.Psmct16 or GsPsm.Psmct16S)
        {
            int count = Math.Min(byteLen >> 1, len);
            fixed (byte* srcBytes = rawBytes)
            fixed (uint* dstPtr = pixels)
            {
                ushort* src16 = (ushort*)srcBytes;
                for (int i = 0; i < count; i++)
                {
                    ushort c16 = src16[i];
                    uint r = (uint)((c16 & 0x001F) << 3);
                    uint g = (uint)(((c16 >> 5) & 0x001F) << 3);
                    uint b = (uint)(((c16 >> 10) & 0x001F) << 3);
                    uint a = (c16 & 0x8000) != 0 ? ta1 : ((aem && (c16 & 0x7FFF) == 0) ? 0u : ta0);
                    dstPtr[i] = r | (g << 8) | (b << 16) | (a << 24);
                }
            }
        }
    }

    private static unsafe void DecodePsmct32Fast(byte[] vram, int tbp0, int tbw, int w, int h, uint[] pixels)
    {
        int pagesPerRow = Math.Max(1, tbw >> 6);
        int baseBytes = tbp0 * 256;
        ref ushort offTbl = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);

        fixed (byte* vramPtr = vram)
        fixed (uint* dstPtr = pixels)
        {
            for (int ty = 0; ty < h; ty++)
            {
                int pageRowBase = baseBytes + (ty >> 5) * pagesPerRow * 8192;
                int offRowBase = (ty & 31) << 6;
                uint* rowDst = dstPtr + ty * w;

                for (int tx = 0; tx < w; tx++)
                {
                    int pageAddr = pageRowBase + ((tx >> 6) << 13);
                    int byteAddr = (pageAddr + Unsafe.Add(ref offTbl, offRowBase + (tx & 63))) & GsSwizzle.VramByteMask;
                    rowDst[tx] = *(uint*)(vramPtr + byteAddr);
                }
            }
        }
    }

    private static unsafe void DecodePsmt8Fast(byte[] vram, int tbp0, int tbw, int w, int h, uint[] pixels, uint[] clutTable)
    {
        int perRow = Math.Max(1, Math.Max(128, tbw) >> 7);
        int baseBytes = tbp0 * 256;
        ref ushort offTbl = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable8Span);

        fixed (byte* vramPtr = vram)
        fixed (uint* dstPtr = pixels)
        fixed (uint* clutPtr = clutTable)
        {
            for (int ty = 0; ty < h; ty++)
            {
                int pageRowBase = baseBytes + (ty >> 6) * perRow * 8192;
                int offRowBase = (ty & 63) << 7;
                uint* rowDst = dstPtr + ty * w;

                for (int tx = 0; tx < w; tx++)
                {
                    int pageAddr = pageRowBase + (tx >> 7) * 8192;
                    int addr = (pageAddr + Unsafe.Add(ref offTbl, offRowBase + (tx & 127))) & GsSwizzle.VramByteMask;
                    rowDst[tx] = clutPtr[vramPtr[addr]];
                }
            }
        }
    }

    private static unsafe void DecodePsmt4Fast(byte[] vram, int tbp0, int tbw, int w, int h, uint[] pixels, uint[] clutTable)
    {
        int perRow = Math.Max(1, Math.Max(128, tbw) >> 7);
        int baseBytes = tbp0 * 256;
        ref ushort offTbl = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable4Span);

        fixed (byte* vramPtr = vram)
        fixed (uint* dstPtr = pixels)
        fixed (uint* clutPtr = clutTable)
        {
            for (int ty = 0; ty < h; ty++)
            {
                int pageRowBase = baseBytes + (ty >> 7) * perRow * 8192;
                int offRowBase = (ty & 127) << 7;
                uint* rowDst = dstPtr + ty * w;

                for (int tx = 0; tx < w; tx++)
                {
                    int pageAddr = pageRowBase + (tx >> 7) * 8192;
                    int nibAddr = (pageAddr << 1) + Unsafe.Add(ref offTbl, offRowBase + (tx & 127));
                    byte b = vramPtr[(nibAddr >> 1) & GsSwizzle.VramByteMask];
                    int idx = (b >> ((nibAddr & 1) << 2)) & 0x0F;
                    rowDst[tx] = clutPtr[idx];
                }
            }
        }
    }

    private static unsafe void DecodePsmct16Fast(byte[] vram, int tbp0, int tbw, int w, int h, uint[] pixels, bool is16S, byte ta0, byte ta1, bool aem)
    {
        int pagesPerRow = Math.Max(1, tbw >> 6);
        ref byte blockTbl = ref MemoryMarshal.GetReference(is16S ? GsSwizzle.BlockTable16S : GsSwizzle.BlockTable16);
        ref byte hwTbl = ref MemoryMarshal.GetReference(GsSwizzle.ColumnHalfwordTable16);

        fixed (byte* vramPtr = vram)
        fixed (uint* dstPtr = pixels)
        {
            for (int ty = 0; ty < h; ty++)
            {
                int pageRowBase = tbp0 + (ty >> 6) * pagesPerRow * 32;
                int byOff = ((ty & 63) >> 3) << 2;
                int pyOff = (ty & 7) << 4;
                uint* rowDst = dstPtr + ty * w;

                for (int tx = 0; tx < w; tx++)
                {
                    int blockInPage = Unsafe.Add(ref blockTbl, byOff + ((tx & 63) >> 4));
                    int blockIdx = (pageRowBase + ((tx >> 6) << 5) + blockInPage) & 0x3FFF;
                    int hwInBlock = Unsafe.Add(ref hwTbl, pyOff + (tx & 15));
                    int byteAddr = (((blockIdx << 7) + hwInBlock) << 1) & GsSwizzle.VramByteMask;
                    ushort c16 = *(ushort*)(vramPtr + byteAddr);
                    uint r = (uint)((c16 & 0x001F) << 3);
                    uint g = (uint)(((c16 >> 5) & 0x001F) << 3);
                    uint b = (uint)(((c16 >> 10) & 0x001F) << 3);
                    uint a = (c16 & 0x8000) != 0 ? ta1 : ((aem && (c16 & 0x7FFF) == 0) ? 0u : ta0);
                    rowDst[tx] = r | (g << 8) | (b << 16) | (a << 24);
                }
            }
        }
    }

    private void RasterizePoint(in GsVertex v0)
    {
        TotalPrimitivesDrawn++;
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);

        int px = (v0.X - ofx) >> 4;
        int py = (v0.Y - ofy) >> 4;
        if (px < scx0 || px > scx1 || py < scy0 || py > scy1)
            return;

        ref readonly var dc = ref BuildDrawContext(ctx, gouraud: false);
        if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
            FlushAllDeferredVram();
        ShadeAndWritePixel(in dc, px, py, v0.Z, v0.R, v0.G, v0.B, v0.A, v0.S, v0.T, v0.Q, v0.U, v0.V);
    }

    private void RasterizeLine(in GsVertex v0, in GsVertex v1)
    {
        TotalPrimitivesDrawn++;
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);
        bool gouraud = ((ActiveAttr >> 3) & 1ul) != 0;

        int x0 = (v0.X - ofx) >> 4;
        int y0 = (v0.Y - ofy) >> 4;
        int x1 = (v1.X - ofx) >> 4;
        int y1 = (v1.Y - ofy) >> 4;

        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int steps = Math.Max(1, Math.Max(dx, dy));

        ref readonly var dc = ref BuildDrawContext(ctx, gouraud);
        if (dc.Tme) TotalTexturedTriangles++; else TotalFlatTriangles++;
        if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
            FlushAllDeferredVram();

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            int px = (int)MathF.Round(x0 + (x1 - x0) * t);
            int py = (int)MathF.Round(y0 + (y1 - y0) * t);

            if (px < scx0 || px > scx1 || py < scy0 || py > scy1)
                continue;

            byte r = gouraud ? (byte)Math.Clamp((int)MathF.Round(v0.R + (v1.R - v0.R) * t), 0, 255) : v1.R;
            byte g = gouraud ? (byte)Math.Clamp((int)MathF.Round(v0.G + (v1.G - v0.G) * t), 0, 255) : v1.G;
            byte b = gouraud ? (byte)Math.Clamp((int)MathF.Round(v0.B + (v1.B - v0.B) * t), 0, 255) : v1.B;
            byte a = gouraud ? (byte)Math.Clamp((int)MathF.Round(v0.A + (v1.A - v0.A) * t), 0, 255) : v1.A;
            uint z = (uint)(v0.Z + (long)(( (double)v1.Z - v0.Z) * t));
            ushort u = (ushort)(v0.U + (v1.U - v0.U) * t);
            ushort v = (ushort)(v0.V + (v1.V - v0.V) * t);
            float sCoord = v0.S + (v1.S - v0.S) * t;
            float tCoord = v0.T + (v1.T - v0.T) * t;
            float qCoord = v0.Q + (v1.Q - v0.Q) * t;

            ShadeAndWritePixel(in dc, px, py, z, r, g, b, a, sCoord, tCoord, qCoord, u, v);
        }
    }

    private void RasterizeSprite(in GsVertex v0, in GsVertex v1, out int maxWrittenX, out int maxWrittenY, out bool cacheUpdatedInPlace)
    {
        maxWrittenX = -1;
        maxWrittenY = -1;
        cacheUpdatedInPlace = false;
        TotalPrimitivesDrawn++;
        TotalSpritesDrawn++;
        var rec = FrameRecorder;
        long t0 = (EnableProfiling || rec != null) ? Stopwatch.GetTimestamp() : 0;
        try
        {
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);

        float fx0 = (v0.X - ofx) / 16.0f;
        float fy0 = (v0.Y - ofy) / 16.0f;
        float fx1 = (v1.X - ofx) / 16.0f;
        float fy1 = (v1.Y - ofy) / 16.0f;

        bool flipX = fx1 < fx0;
        bool flipY = fy1 < fy0;
        float minFx = flipX ? fx1 : fx0;
        float maxFx = flipX ? fx0 : fx1;
        float minFy = flipY ? fy1 : fy0;
        float maxFy = flipY ? fy0 : fy1;

        // On PS2 GS, XYOFFSET already subtracts the 0.5 pixel center offset (e.g. OFX=1728.5, OFY=1824.5),
        // so integer pixel coordinates (px, py) inside [minFx, maxFx) are rasterized.
        int minX = (int)MathF.Ceiling(minFx);
        int maxX = (int)MathF.Ceiling(maxFx) - 1;
        int minY = (int)MathF.Ceiling(minFy);
        int maxY = (int)MathF.Ceiling(maxFy) - 1;

        int clipX0 = Math.Max(minX, scx0);
        int clipX1 = Math.Min(maxX, scx1);
        int clipY0 = Math.Max(minY, scy0);
        int clipY1 = Math.Min(maxY, scy1);

        if (EnableDebugLogging && ((TotalPrimitivesDrawn >= 71 && TotalPrimitivesDrawn <= 90) || (TotalPrimitivesDrawn >= 301 && TotalPrimitivesDrawn <= 320)))
        {
            ulong attr = ActiveAttr;
            bool tme = ((attr >> 4) & 1ul) != 0;
            bool fst = ((attr >> 8) & 1ul) != 0;
            Console.WriteLine($"[RAW SPR #{TotalPrimitivesDrawn}] ctx={ctx} of=({ofx},{ofy}) sc=({scx0},{scy0})-({scx1},{scy1}) clip=[{clipX0}..{clipX1},{clipY0}..{clipY1}] raw0=({v0.X},{v0.Y},{v0.Z}) raw1=({v1.X},{v1.Y},{v1.Z}) v0=({fx0:F1},{fy0:F1},RGBA:0x{v0.R:X2}{v0.G:X2}{v0.B:X2}{v0.A:X2},STQ:({v0.S},{v0.T},{v0.Q}),UV:({v0.U},{v0.V})) v1=({fx1:F1},{fy1:F1},RGBA:0x{v1.R:X2}{v1.G:X2}{v1.B:X2}{v1.A:X2}) TME={tme} FST={fst} TEX0=0x{Tex0[ctx]:X16} TEST=0x{Test[ctx]:X16}");
        }

        if (clipX0 > clipX1 || clipY0 > clipY1)
            return;

        maxWrittenX = clipX1;
        maxWrittenY = clipY1;

        if (EnableDebugLogging && scx0 <= scx1 && scy0 <= scy1 && (TotalPrimitivesDrawn <= 70 || (TotalPrimitivesDrawn >= 301 && _uiTriLogs < 35)))
        {
            if (TotalPrimitivesDrawn >= 301) _uiTriLogs++;
            ulong attr = ActiveAttr;
            bool tme = ((attr >> 4) & 1ul) != 0;
            bool fst = ((attr >> 8) & 1ul) != 0;
            int fbp = (int)(Frame[ctx] & 0x1FFul) * 32;
            Console.WriteLine($"[VALID SCISSOR SPR #{TotalPrimitivesDrawn}] ctx={ctx} FBP=0x{fbp:X} sc=({scx0},{scy0})-({scx1},{scy1}) clip=[{clipX0}..{clipX1},{clipY0}..{clipY1}] v0=({fx0:F1},{fy0:F1},RGBA:0x{v0.R:X2}{v0.G:X2}{v0.B:X2}{v0.A:X2},STQ:({v0.S},{v0.T},{v0.Q}),UV:({v0.U},{v0.V})) v1=({fx1:F1},{fy1:F1},RGBA:0x{v1.R:X2}{v1.G:X2}{v1.B:X2}{v1.A:X2},STQ:({v1.S},{v1.T},{v1.Q}),UV:({v1.U},{v1.V})) TME={tme} FST={fst} TEX0=0x{Tex0[ctx]:X16} TEST=0x{Test[ctx]:X16}");
        }

        ulong spriteAttr = ActiveAttr;
        bool spriteTme = ((spriteAttr >> 4) & 1ul) != 0;
        bool spriteAbe = ((spriteAttr >> 6) & 1ul) != 0;
        ulong spriteTest = Test[ctx];
        bool spriteAte = (spriteTest & 1ul) != 0;
        bool spriteDate = ((spriteTest >> 14) & 1ul) != 0;
        bool spriteZte = ((spriteTest >> 16) & 1ul) != 0;
        int spriteZtst = (int)((spriteTest >> 17) & 3ul);

        if (!spriteTme && !spriteAbe && !spriteAte && !spriteDate && (!spriteZte || spriteZtst == 1))
        {
            ulong frameReg = Frame[ctx];
            int fbpBlock = (int)(frameReg & 0x1FFul) * 32;
            int fbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64;
            var fpsm = (GsPsm)((frameReg >> 24) & 0x3Ful);
            uint fbmsk = (uint)(frameReg >> 32);

            ulong zbufReg = Zbuf[ctx];
            int zbpBlock = (int)(zbufReg & 0x1FFul) * 32;
            var zpsm = (GsPsm)(((zbufReg >> 24) & 0xFul) | 0x30ul);
            bool writeDepth = spriteZte && ((zbufReg >> 32) & 1ul) == 0;

            if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
            {
                ComputeBlockRange(fbpBlock, fbwPixels, fpsm, Math.Max(1, clipX1 + 1), Math.Max(1, clipY1 + 1), out int cfStart, out int cfEnd);
                FlushDeferredVramRange(cfStart, cfEnd);
                if (writeDepth)
                {
                    ComputeBlockRange(zbpBlock, fbwPixels, zpsm, Math.Max(1, clipX1 + 1), Math.Max(1, clipY1 + 1), out int czStart, out int czEnd);
                    FlushDeferredVramRange(czStart, czEnd);
                }
            }
            uint testZ = zpsm switch
            {
                GsPsm.Psmz24 => v1.Z & 0xFFFFFFu,
                GsPsm.Psmz16 or GsPsm.Psmz16S => v1.Z & 0xFFFFu,
                _ => v1.Z
            };

            uint newColor = (uint)(v1.R | (v1.G << 8) | (v1.B << 16) | (v1.A << 24));
            if ((Fba[ctx] & 1ul) != 0)
                newColor |= 0x80000000u;

            int count = (clipX1 - clipX0 + 1) * (clipY1 - clipY0 + 1);
            TotalPixelsShaded += count;
            TotalPixelsWritten += count;
            VramDirty = true;

            if (!writeDepth && fbmsk == 0 && clipX0 == 0 && clipY0 == 0 && (clipX1 + 1) == fbwPixels
                && fpsm is GsPsm.Psmct16 or GsPsm.Psmct16S
                && ((clipX1 + 1) & 63) == 0 && ((clipY1 + 1) & 63) == 0
                && (fbpBlock * 256 + count * 2) <= Vram.Length)
            {
                ushort c16 = GsSwizzle.Pack32To16(newColor);
                MemoryMarshal.Cast<byte, ushort>(Vram.AsSpan(fbpBlock * 256, count * 2)).Fill(c16);
            }
            else if (!writeDepth && fbmsk == 0 && clipX0 == 0 && clipY0 == 0 && (clipX1 + 1) == fbwPixels
                && fpsm == GsPsm.Psmct32
                && ((clipX1 + 1) & 63) == 0 && ((clipY1 + 1) & 31) == 0
                && (fbpBlock * 256 + count * 4) <= Vram.Length)
            {
                MemoryMarshal.Cast<byte, uint>(Vram.AsSpan(fbpBlock * 256, count * 4)).Fill(newColor);
            }
            else if (fpsm == GsPsm.Psmct32 && (!writeDepth || zpsm is GsPsm.Psmz32 or GsPsm.Psmz24))
            {
                int pagesPerRow = Math.Max(1, fbwPixels >> 6);
                int fbBaseBytes = fbpBlock * 256;
                int zbBaseBytes = zbpBlock * 256;
                bool z24 = zpsm == GsPsm.Psmz24;
                ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(Vram);
                ref ushort offTbl32 = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
                ref ushort offTbl32Z = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32ZSpan);

                for (int py = clipY0; py <= clipY1; py++)
                {
                    int rowPageOff = (py >> 5) * pagesPerRow * 8192;
                    int fbPageRowBase = fbBaseBytes + rowPageOff;
                    int zbPageRowBase = zbBaseBytes + rowPageOff;
                    int offRowBase = (py & 31) << 6;

                    for (int px = clipX0; px <= clipX1; px++)
                    {
                        int xPageOff = (px >> 6) << 13;
                        int xIdx = offRowBase + (px & 63);
                        if (writeDepth)
                        {
                            int zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                            if (z24)
                            {
                                uint oldZ = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref vramBase, zAddr));
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, zAddr), (oldZ & 0xFF000000u) | testZ);
                            }
                            else
                            {
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, zAddr), testZ);
                            }
                        }

                        if (fbmsk != 0xFFFFFFFFu)
                        {
                            int fbAddr = (fbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32, xIdx)) & GsSwizzle.VramByteMask;
                            if (fbmsk == 0)
                            {
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, fbAddr), newColor);
                            }
                            else
                            {
                                uint dstColor = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref vramBase, fbAddr));
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, fbAddr), (dstColor & fbmsk) | (newColor & ~fbmsk));
                            }
                        }
                    }
                }
            }
            else
            {
                for (int py = clipY0; py <= clipY1; py++)
                {
                    for (int px = clipX0; px <= clipX1; px++)
                    {
                        if (writeDepth)
                            WriteDepthPixel(zbpBlock, fbwPixels, zpsm, px, py, testZ);

                        if (fbmsk == 0)
                        {
                            WriteFramePixel(fbpBlock, fbwPixels, fpsm, px, py, newColor);
                        }
                        else if (fbmsk != 0xFFFFFFFFu)
                        {
                            uint dstColor = ReadFramePixel(fbpBlock, fbwPixels, fpsm, px, py);
                            WriteFramePixel(fbpBlock, fbwPixels, fpsm, px, py, (dstColor & fbmsk) | (newColor & ~fbmsk));
                        }
                    }
                }
            }
            if (rec != null)
            {
                rec.RecordSoftSprite(
                    new SoftSprBucketKey("ClearFast", fbpBlock, fbwPixels, fpsm, 0, 0, 0, 0, 0, 0, 0, 0, spriteAte, 0, 0, 0, spriteAbe, Alpha[ctx], spriteZte, spriteZtst, !writeDepth, spriteDate, (Fba[ctx] & 1ul) != 0, fbmsk, false, false),
                    clipX0, clipY0, clipX1, clipY1, count, Stopwatch.GetTimestamp() - t0);
            }
            return;
        }

        if (!spriteTme && !spriteAte && !spriteDate && (!spriteZte || spriteZtst == 1)
            && (((Frame[ctx] >> 32) & 0x00FFFFFFul) == 0x00FFFFFFul))
        {
            if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
                FlushAllDeferredVram();
            ulong frameReg = Frame[ctx];
            int fbpBlock = (int)(frameReg & 0x1FFul) * 32;
            int fbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64;
            var fpsm = (GsPsm)((frameReg >> 24) & 0x3Ful);
            uint fbmsk = (uint)(frameReg >> 32);

            ulong zbufReg = Zbuf[ctx];
            int zbpBlock = (int)(zbufReg & 0x1FFul) * 32;
            var zpsm = (GsPsm)(((zbufReg >> 24) & 0xFul) | 0x30ul);
            bool writeDepth = spriteZte && ((zbufReg >> 32) & 1ul) == 0;
            uint testZ = zpsm switch
            {
                GsPsm.Psmz24 => v1.Z & 0xFFFFFFu,
                GsPsm.Psmz16 or GsPsm.Psmz16S => v1.Z & 0xFFFFu,
                _ => v1.Z
            };

            uint newColor = (uint)(v1.A << 24);
            if ((Fba[ctx] & 1ul) != 0)
                newColor |= 0x80000000u;

            int count = (clipX1 - clipX0 + 1) * (clipY1 - clipY0 + 1);
            TotalPixelsShaded += count;
            TotalPixelsWritten += count;
            VramDirty = true;

            if (fpsm == GsPsm.Psmct32 && (!writeDepth || zpsm is GsPsm.Psmz32 or GsPsm.Psmz24))
            {
                int pagesPerRow = Math.Max(1, fbwPixels >> 6);
                int fbBaseBytes = fbpBlock * 256;
                int zbBaseBytes = zbpBlock * 256;
                bool z24 = zpsm == GsPsm.Psmz24;
                ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(Vram);
                ref ushort offTbl32 = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
                ref ushort offTbl32Z = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32ZSpan);

                for (int py = clipY0; py <= clipY1; py++)
                {
                    int rowPageOff = (py >> 5) * pagesPerRow * 8192;
                    int fbPageRowBase = fbBaseBytes + rowPageOff;
                    int zbPageRowBase = zbBaseBytes + rowPageOff;
                    int offRowBase = (py & 31) << 6;

                    for (int px = clipX0; px <= clipX1; px++)
                    {
                        int xPageOff = (px >> 6) << 13;
                        int xIdx = offRowBase + (px & 63);
                        if (writeDepth)
                        {
                            int zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                            if (z24)
                            {
                                uint oldZ = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref vramBase, zAddr));
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, zAddr), (oldZ & 0xFF000000u) | testZ);
                            }
                            else
                            {
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, zAddr), testZ);
                            }
                        }

                        if (fbmsk != 0xFFFFFFFFu)
                        {
                            int fbAddr = (fbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32, xIdx)) & GsSwizzle.VramByteMask;
                            uint dstColor = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref vramBase, fbAddr));
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, fbAddr), (dstColor & fbmsk) | (newColor & ~fbmsk));
                        }
                    }
                }
            }
            else
            {
                for (int py = clipY0; py <= clipY1; py++)
                {
                    for (int px = clipX0; px <= clipX1; px++)
                    {
                        if (writeDepth)
                            WriteDepthPixel(zbpBlock, fbwPixels, zpsm, px, py, testZ);
                        if (fbmsk != 0xFFFFFFFFu)
                        {
                            uint dstColor = ReadFramePixel(fbpBlock, fbwPixels, fpsm, px, py);
                            WriteFramePixel(fbpBlock, fbwPixels, fpsm, px, py, (dstColor & fbmsk) | (newColor & ~fbmsk));
                        }
                    }
                }
            }
            if (rec != null)
            {
                rec.RecordSoftSprite(
                    new SoftSprBucketKey("ClearFast", fbpBlock, fbwPixels, fpsm, 0, 0, 0, 0, 0, 0, 0, 0, spriteAte, 0, 0, 0, spriteAbe, Alpha[ctx], spriteZte, spriteZtst, !writeDepth, spriteDate, (Fba[ctx] & 1ul) != 0, fbmsk, false, false),
                    clipX0, clipY0, clipX1, clipY1, count, Stopwatch.GetTimestamp() - t0);
            }
            return;
        }

        ref readonly var dc = ref BuildDrawContext(ctx, gouraud: false);
        float spanX = Math.Max(1e-4f, maxFx - minFx);
        float spanY = Math.Max(1e-4f, maxFy - minFy);
        int sprPixels = (clipX1 - clipX0 + 1) * (clipY1 - clipY0 + 1);

        if (!dc.Ate && !dc.Date && !dc.Abe && (!dc.Zte || dc.Ztst == 1) && (!dc.Zte || dc.Zmsk)
            && dc.Fbmsk == 0 && dc.Tme && (dc.Tfx == 0 || dc.Tfx == 1)
            && dc.Fpsm == GsPsm.Psmct32 && dc.DecodedTexture != null)
        {
            RasterizeTexturedSpriteFast(
                in dc, in v0, in v1,
                clipX0, clipX1, clipY0, clipY1,
                fx0, fy0, fx1, fy1,
                minFx, maxFx, minFy, maxFy, spanX, spanY, flipX, flipY,
                out cacheUpdatedInPlace);
            if (rec != null)
            {
                rec.RecordSoftSprite(
                    new SoftSprBucketKey("TexFast", dc.FbpBlock, dc.FbwPixels, dc.Fpsm, dc.Tbp0, dc.Tw, dc.Th, dc.Psm, dc.Cbp, dc.Cpsm, dc.Tfx, dc.Tcc, dc.Ate, dc.Atst, dc.Aref, dc.Afail, dc.Abe, Alpha[ctx], dc.Zte, dc.Ztst, dc.Zmsk, dc.Date, dc.Fba, dc.Fbmsk, true, dc.LinearFilter),
                    clipX0, clipY0, clipX1, clipY1, sprPixels, Stopwatch.GetTimestamp() - t0);
            }
            return;
        }

        if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
            FlushAllDeferredVram();

        if (!dc.Date && (!dc.Zte || dc.Zpsm is GsPsm.Psmz24 or GsPsm.Psmz32)
            && dc.Tme && (dc.Tfx == 0 || dc.Tfx == 1)
            && dc.Fpsm == GsPsm.Psmct32 && dc.DecodedTexture != null)
        {
            RasterizeTexturedSpriteBlendedMasked(
                in dc, in v0, in v1,
                clipX0, clipX1, clipY0, clipY1,
                minFx, maxFx, minFy, maxFy, spanX, spanY, flipX, flipY);
            if (rec != null)
            {
                rec.RecordSoftSprite(
                    new SoftSprBucketKey("TexBlendFast", dc.FbpBlock, dc.FbwPixels, dc.Fpsm, dc.Tbp0, dc.Tw, dc.Th, dc.Psm, dc.Cbp, dc.Cpsm, dc.Tfx, dc.Tcc, dc.Ate, dc.Atst, dc.Aref, dc.Afail, dc.Abe, Alpha[ctx], dc.Zte, dc.Ztst, dc.Zmsk, dc.Date, dc.Fba, dc.Fbmsk, true, dc.LinearFilter),
                    clipX0, clipY0, clipX1, clipY1, sprPixels, Stopwatch.GetTimestamp() - t0);
            }
            return;
        }

        if (sprPixels >= 32768 && clipY1 > clipY0)//changed from 4096 to 32768 for testing optimization
            {
            var lv0 = v0;
            var lv1 = v1;
            var ldc = dc;
            Parallel.For(clipY0, clipY1 + 1, py =>
            {
                var rowDc = ldc;
                float ty = Math.Clamp((flipY ? (maxFy - py) : (py - minFy)) / spanY, 0.0f, 1.0f);
                ushort vTex = (ushort)(lv0.V + (lv1.V - lv0.V) * ty);
                float tCoord = lv0.T + (lv1.T - lv0.T) * ty;

                for (int px = clipX0; px <= clipX1; px++)
                {
                    float tx = Math.Clamp((flipX ? (maxFx - px) : (px - minFx)) / spanX, 0.0f, 1.0f);
                    ushort uTex = (ushort)(lv0.U + (lv1.U - lv0.U) * tx);
                    float sCoord = lv0.S + (lv1.S - lv0.S) * tx;

                    ShadeAndWritePixel(in rowDc, px, py, lv1.Z, lv1.R, lv1.G, lv1.B, lv1.A, sCoord, tCoord, lv1.Q, uTex, vTex);
                }
            });
        }
        else
        {
            for (int py = clipY0; py <= clipY1; py++)
            {
                float ty = Math.Clamp((flipY ? (maxFy - py) : (py - minFy)) / spanY, 0.0f, 1.0f);
                ushort vTex = (ushort)(v0.V + (v1.V - v0.V) * ty);
                float tCoord = v0.T + (v1.T - v0.T) * ty;

                for (int px = clipX0; px <= clipX1; px++)
                {
                    float tx = Math.Clamp((flipX ? (maxFx - px) : (px - minFx)) / spanX, 0.0f, 1.0f);
                    ushort uTex = (ushort)(v0.U + (v1.U - v0.U) * tx);
                    float sCoord = v0.S + (v1.S - v0.S) * tx;

                    // Sprites always use flat color and Z from the second vertex (v1)
                    ShadeAndWritePixel(in dc, px, py, v1.Z, v1.R, v1.G, v1.B, v1.A, sCoord, tCoord, v1.Q, uTex, vTex);
                }
            }
        }

        if (rec != null)
        {
            rec.RecordSoftSprite(
                new SoftSprBucketKey("SlowShade", dc.FbpBlock, dc.FbwPixels, dc.Fpsm, dc.Tbp0, dc.Tw, dc.Th, dc.Psm, dc.Cbp, dc.Cpsm, dc.Tfx, dc.Tcc, dc.Ate, dc.Atst, dc.Aref, dc.Afail, dc.Abe, Alpha[ctx], dc.Zte, dc.Ztst, dc.Zmsk, dc.Date, dc.Fba, dc.Fbmsk, dc.DecodedTexture != null, dc.LinearFilter),
                clipX0, clipY0, clipX1, clipY1, sprPixels, Stopwatch.GetTimestamp() - t0);
        }
        }
        finally
        {
            if (t0 != 0)
                TotalRasterizeSpriteTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private unsafe void RasterizeTexturedSpriteBlendedMasked(
        in GsDrawContext dc,
        in GsVertex v0,
        in GsVertex v1,
        int clipX0, int clipX1, int clipY0, int clipY1,
        float minFx, float maxFx, float minFy, float maxFy,
        float spanX, float spanY,
        bool flipX, bool flipY)
    {
        int width = clipX1 - clipX0 + 1;
        int height = clipY1 - clipY0 + 1;
        int count = width * height;
        TotalPixelsShaded += count;
        TotalTexturedPixels += count;

        uint fbmsk = dc.Fbmsk;
        bool zte = dc.Zte;
        int ztst = dc.Ztst;
        bool zmsk = dc.Zmsk;
        bool z24 = dc.Zpsm == GsPsm.Psmz24;
        uint testZ = z24 ? (v1.Z & 0xFFFFFFu) : v1.Z;
        if (zte && ztst == 0)
        {
            TotalPixelsZFailed += count;
            return;
        }
        if (fbmsk == 0xFFFFFFFFu && (!zte || zmsk))
            return;

        uint[] srcPixels = dc.DecodedTexture!;
        int fbpBlock = dc.FbpBlock;
        int zbpBlock = dc.ZbpBlock;
        int fbwPixels = dc.FbwPixels;
        int pagesPerRow = Math.Max(1, fbwPixels >> 6);
        int tw = dc.Tw, th = dc.Th;
        int wms = dc.Wms, wmt = dc.Wmt;
        uint minu = dc.MinU, maxu = dc.MaxU, minv = dc.MinV, maxv = dc.MaxV;
        bool linear = dc.LinearFilter, fst = dc.Fst, decal = dc.Tfx == 1, tcc = dc.Tcc != 0, fba = dc.Fba;
        bool ate = dc.Ate;
        int atst = dc.Atst;
        byte aref = dc.Aref;
        int afail = dc.Afail;
        bool abe = dc.Abe && (fbmsk & 0x00FFFFFFu) != 0x00FFFFFFu;
        int specA = dc.SpecA, specB = dc.SpecB, specC = dc.SpecC, specD = dc.SpecD;
        byte fix = dc.Fix;
        bool colclamp = dc.Colclamp;
        bool isStandardAlphaBlend = abe && specA == 0 && specB == 1 && specC == 0 && specD == 1;
        byte vR = v1.R, vG = v1.G, vB = v1.B, vA = v1.A;
        bool directCopy = decal || (vR == 128 && vG == 128 && vB == 128 && (!tcc || vA == 128));
        uint alphaMask = fba ? 0x80000000u : 0u;
        uint fixedAlpha = (uint)vA << 24;

        float invQ = (!fst && v1.Q != 0.0f) ? (1.0f / v1.Q) : 1.0f;
        float invSpanX = 1.0f / spanX;
        float invSpanY = 1.0f / spanY;
        float dv = v1.V - v0.V, dt = v1.T - v0.T;
        float du = v1.U - v0.U, ds = v1.S - v0.S;

        Span<int> xPageByteOff = width <= 2048 ? stackalloc int[width] : new int[width];
        Span<byte> xPx63 = width <= 2048 ? stackalloc byte[width] : new byte[width];
        for (int dx = 0; dx < width; dx++)
        {
            int px = clipX0 + dx;
            xPageByteOff[dx] = (px >> 6) << 13;
            xPx63[dx] = (byte)(px & 63);
        }

        ref ushort offTbl32 = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
        ref ushort offTbl32Z = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32ZSpan);
        int fbBaseBytes = fbpBlock * 256;
        int zbBaseBytes = zbpBlock * 256;

        Span<int> xTx0 = width <= 2048 ? stackalloc int[width] : new int[width];
        Span<int> xTx1 = width <= 2048 ? stackalloc int[width] : new int[width];
        Span<uint> xUf = width <= 2048 ? stackalloc uint[width] : new uint[width];

        for (int dx = 0; dx < width; dx++)
        {
            int px = clipX0 + dx;
            float tx = Math.Clamp((flipX ? (maxFx - px) : (px - minFx)) * invSpanX, 0.0f, 1.0f);
            if (linear)
            {
                float fU = fst
                    ? (((ushort)(v0.U + du * tx)) / 16.0f) - 0.5f
                    : (v0.S + ds * tx) * invQ * tw - 0.5f;
                int tx0 = (int)MathF.Floor(fU);
                float fracU = fU - tx0;
                int tx0w = ApplyWrapMode(tx0, tw, wms, minu, maxu);
                xTx0[dx] = tx0w;
                if (fracU == 0.0f)
                {
                    xTx1[dx] = tx0w;
                    xUf[dx] = 0;
                }
                else
                {
                    xTx1[dx] = ApplyWrapMode(tx0 + 1, tw, wms, minu, maxu);
                    xUf[dx] = (uint)(fracU * 256.0f);
                }
            }
            else
            {
                int iU = fst
                    ? (((ushort)(v0.U + du * tx)) >> 4)
                    : (int)MathF.Floor((v0.S + ds * tx) * invQ * tw);
                int tx0w = ApplyWrapMode(iU, tw, wms, minu, maxu);
                xTx0[dx] = tx0w;
                xTx1[dx] = tx0w;
                xUf[dx] = 0;
            }
        }

        long writtenCount = 0;
        long zFailCount = 0;

        fixed (byte* vramPtr = Vram)
        fixed (uint* srcPtr = srcPixels)
        {
            for (int py = clipY0; py <= clipY1; py++)
            {
                float ty = Math.Clamp((flipY ? (maxFy - py) : (py - minFy)) * invSpanY, 0.0f, 1.0f);
                int rowPageOff = (py >> 5) * pagesPerRow * 8192;
                int fbPageRowBase = fbBaseBytes + rowPageOff;
                int zbPageRowBase = zbBaseBytes + rowPageOff;
                int offRowBase = (py & 31) << 6;

                uint vf = 0;
                int ty0w, ty1w;
                if (linear)
                {
                    float fV = fst
                        ? (((ushort)(v0.V + dv * ty)) / 16.0f) - 0.5f
                        : (v0.T + dt * ty) * invQ * th - 0.5f;
                    int ty0 = (int)MathF.Floor(fV);
                    float fracV = fV - ty0;
                    vf = (uint)(fracV * 256.0f);
                    ty0w = ApplyWrapMode(ty0, th, wmt, minv, maxv);
                    ty1w = vf == 0 ? ty0w : ApplyWrapMode(ty0 + 1, th, wmt, minv, maxv);
                }
                else
                {
                    int iV = fst
                        ? (((ushort)(v0.V + dv * ty)) >> 4)
                        : (int)MathF.Floor((v0.T + dt * ty) * invQ * th);
                    ty0w = ApplyWrapMode(iV, th, wmt, minv, maxv);
                    ty1w = ty0w;
                }

                uint invVf = 256u - vf;
                uint* row0 = srcPtr + ty0w * tw;
                uint* row1 = srcPtr + ty1w * tw;

                for (int dx = 0; dx < width; dx++)
                {
                    int xPageOff = xPageByteOff[dx];
                    int xIdx = offRowBase + xPx63[dx];
                    int zAddr = -1;
                    uint oldZWord = 0;

                    if (zte && ztst >= 2)
                    {
                        zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                        oldZWord = *(uint*)(vramPtr + zAddr);
                        uint curZVal = z24 ? (oldZWord & 0xFFFFFFu) : oldZWord;
                        bool zPass = ztst == 2 ? (testZ >= curZVal) : (testZ > curZVal);
                        if (!zPass)
                        {
                            zFailCount++;
                            continue;
                        }
                    }

                    uint uf = xUf[dx];
                    int tx0w = xTx0[dx];
                    uint texColor;
                    if (uf == 0 && vf == 0)
                    {
                        texColor = row0[tx0w];
                    }
                    else
                    {
                        int tx1w = xTx1[dx];
                        uint c00 = row0[tx0w];
                        uint c10 = row0[tx1w];
                        uint c01 = row1[tx0w];
                        uint c11 = row1[tx1w];

                        if (c00 == c10 && c00 == c01 && c00 == c11)
                        {
                            texColor = c00;
                        }
                        else
                        {
                            uint invUf = 256u - uf;
                            ulong w00 = (ulong)(invUf * invVf);
                            ulong w10 = (ulong)(uf * invVf);
                            ulong w01 = (ulong)(invUf * vf);
                            ulong w11 = (ulong)(uf * vf);

                            ulong rb = ((c00 & 0xFFul) | ((ulong)(c00 & 0xFF0000u) << 16)) * w00
                                     + ((c10 & 0xFFul) | ((ulong)(c10 & 0xFF0000u) << 16)) * w10
                                     + ((c01 & 0xFFul) | ((ulong)(c01 & 0xFF0000u) << 16)) * w01
                                     + ((c11 & 0xFFul) | ((ulong)(c11 & 0xFF0000u) << 16)) * w11;
                            ulong ga = (((c00 >> 8) & 0xFFul) | ((ulong)(c00 & 0xFF000000u) << 8)) * w00
                                     + (((c10 >> 8) & 0xFFul) | ((ulong)(c10 & 0xFF000000u) << 8)) * w10
                                     + (((c01 >> 8) & 0xFFul) | ((ulong)(c01 & 0xFF000000u) << 8)) * w01
                                     + (((c11 >> 8) & 0xFFul) | ((ulong)(c11 & 0xFF000000u) << 8)) * w11;

                            texColor = (uint)(((rb >> 16) & 0xFFul)
                                | ((ga >> 8) & 0xFF00ul)
                                | ((rb >> 32) & 0xFF0000ul)
                                | ((ga >> 24) & 0xFF000000ul));
                        }
                    }

                    uint outColor;
                    if (directCopy)
                    {
                        outColor = tcc ? texColor : ((texColor & 0x00FFFFFFu) | fixedAlpha);
                    }
                    else
                    {
                        uint tr = Math.Min(255u, ((texColor & 0xFFu) * vR) >> 7);
                        uint tg = Math.Min(255u, (((texColor >> 8) & 0xFFu) * vG) >> 7);
                        uint tb = Math.Min(255u, (((texColor >> 16) & 0xFFu) * vB) >> 7);
                        uint ta = tcc ? Math.Min(255u, (((texColor >> 24) & 0xFFu) * vA) >> 7) : vA;
                        outColor = tr | (tg << 8) | (tb << 16) | (ta << 24);
                    }

                    bool writeColor = true;
                    bool writeDepth = zte && !zmsk;
                    uint pixFbmsk = fbmsk;

                    if (ate)
                    {
                        byte fragA = (byte)(outColor >> 24);
                        bool alphaPass = atst switch
                        {
                            0 => false,
                            1 => true,
                            2 => fragA < aref,
                            3 => fragA <= aref,
                            4 => fragA == aref,
                            5 => fragA >= aref,
                            6 => fragA > aref,
                            7 => fragA != aref,
                            _ => true
                        };

                        if (!alphaPass)
                        {
                            switch (afail)
                            {
                                case 0:
                                    continue;
                                case 1:
                                    writeDepth = false;
                                    break;
                                case 2:
                                    writeColor = false;
                                    break;
                                case 3:
                                    writeDepth = false;
                                    pixFbmsk |= 0xFF000000u;
                                    break;
                            }
                        }
                    }

                    if (writeDepth)
                    {
                        if (zAddr < 0)
                        {
                            zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                            if (z24) oldZWord = *(uint*)(vramPtr + zAddr);
                        }
                        *(uint*)(vramPtr + zAddr) = z24 ? ((oldZWord & 0xFF000000u) | testZ) : testZ;
                    }

                    if (!writeColor || pixFbmsk == 0xFFFFFFFFu)
                        continue;

                    int byteAddr = (fbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32, xIdx)) & GsSwizzle.VramByteMask;
                    uint dstColor = (abe || pixFbmsk != 0) ? *(uint*)(vramPtr + byteAddr) : 0u;

                    if (abe)
                    {
                        byte fragR = (byte)(outColor & 0xFFu);
                        byte fragG = (byte)((outColor >> 8) & 0xFFu);
                        byte fragB = (byte)((outColor >> 16) & 0xFFu);
                        byte fragA = (byte)((outColor >> 24) & 0xFFu);

                        byte dR = (byte)(dstColor & 0xFFu);
                        byte dG = (byte)((dstColor >> 8) & 0xFFu);
                        byte dB = (byte)((dstColor >> 16) & 0xFFu);

                        int outR, outG, outB;
                        if (isStandardAlphaBlend)
                        {
                            outR = (((fragR - dR) * fragA) >> 7) + dR;
                            outG = (((fragG - dG) * fragA) >> 7) + dG;
                            outB = (((fragB - dB) * fragA) >> 7) + dB;
                        }
                        else
                        {
                            byte dA = (byte)((dstColor >> 24) & 0xFFu);
                            int aR = SelectBlendRgb(specA, fragR, dR);
                            int aG = SelectBlendRgb(specA, fragG, dG);
                            int aB = SelectBlendRgb(specA, fragB, dB);

                            int bR = SelectBlendRgb(specB, fragR, dR);
                            int bG = SelectBlendRgb(specB, fragG, dG);
                            int bB = SelectBlendRgb(specB, fragB, dB);

                            int cVal = specC switch { 0 => fragA, 1 => dA, 2 => fix, _ => 0 };

                            int dRVal = SelectBlendRgb(specD, fragR, dR);
                            int dGVal = SelectBlendRgb(specD, fragG, dG);
                            int dBVal = SelectBlendRgb(specD, fragB, dB);

                            outR = (((aR - bR) * cVal) >> 7) + dRVal;
                            int outGVal = (((aG - bG) * cVal) >> 7) + dGVal;
                            int outBVal = (((aB - bB) * cVal) >> 7) + dBVal;
                            outG = outGVal;
                            outB = outBVal;
                        }

                        if (colclamp)
                        {
                            fragR = (byte)(outR < 0 ? 0 : (outR > 255 ? 255 : outR));
                            fragG = (byte)(outG < 0 ? 0 : (outG > 255 ? 255 : outG));
                            fragB = (byte)(outB < 0 ? 0 : (outB > 255 ? 255 : outB));
                        }
                        else
                        {
                            fragR = (byte)(outR & 0xFF);
                            fragG = (byte)(outG & 0xFF);
                            fragB = (byte)(outB & 0xFF);
                        }
                        outColor = (uint)(fragR | (fragG << 8) | (fragB << 16) | (fragA << 24));
                    }

                    outColor |= alphaMask;
                    *(uint*)(vramPtr + byteAddr) = pixFbmsk == 0 ? outColor : ((dstColor & pixFbmsk) | (outColor & ~pixFbmsk));
                    writtenCount++;
                }
            }
        }

        TotalPixelsWritten += writtenCount;
        TotalPixelsZFailed += zFailCount;
        if (writtenCount > 0)
            VramDirty = true;
    }

    private unsafe void RasterizeTexturedSpriteFast(
        in GsDrawContext dc,
        in GsVertex v0,
        in GsVertex v1,
        int clipX0, int clipX1, int clipY0, int clipY1,
        float fx0, float fy0, float fx1, float fy1,
        float minFx, float maxFx, float minFy, float maxFy,
        float spanX, float spanY,
        bool flipX, bool flipY,
        out bool cacheUpdatedInPlace)
    {
        cacheUpdatedInPlace = false;
        int width = clipX1 - clipX0 + 1;
        int height = clipY1 - clipY0 + 1;
        int count = width * height;
        TotalPixelsShaded += count;
        TotalTexturedPixels += count;
        TotalPixelsWritten += count;

        uint[] srcPixels = dc.DecodedTexture!;
        ulong srcHash64 = dc.DecodedTextureHash64;
        int ctx = dc.Context;
        int fbpBlock = dc.FbpBlock;
        int fbwPixels = dc.FbwPixels;
        int pagesPerRow = Math.Max(1, fbwPixels >> 6);
        int tw = dc.Tw, th = dc.Th;
        int wms = dc.Wms, wmt = dc.Wmt;
        uint minu = dc.MinU, maxu = dc.MaxU, minv = dc.MinV, maxv = dc.MaxV;
        bool linear = dc.LinearFilter, fst = dc.Fst, decal = dc.Tfx == 1, tcc = dc.Tcc != 0, fba = dc.Fba;
        byte vR = v1.R, vG = v1.G, vB = v1.B, vA = v1.A;

        bool canCacheDst = clipX0 == 0 && clipY0 == 0 && fbpBlock != 0 && fbpBlock != 0xA00
            && width <= 1024 && height <= 1024
            && (width & (width - 1)) == 0 && (height & (height - 1)) == 0;
        GsTexCacheKey dstKey = default;
        GsSpriteWriteSignature sig = default;
        uint[]? dstPixels = null;
        if (canCacheDst)
        {
            dstKey = new GsTexCacheKey(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height, 0, GsPsm.Psmct32, 0, 0, 0, false);
            byte sigFlags = (byte)((linear ? 1 : 0) | (fst ? 2 : 0) | (decal ? 4 : 0) | (tcc ? 8 : 0) | (fba ? 16 : 0));
            sig = new GsSpriteWriteSignature(
                srcHash64, tw, th,
                clipX1, clipY1,
                BitConverter.SingleToInt32Bits(fx0),
                BitConverter.SingleToInt32Bits(fy0),
                BitConverter.SingleToInt32Bits(fx1),
                BitConverter.SingleToInt32Bits(fy1),
                v0.U, v0.V, v1.U, v1.V,
                BitConverter.SingleToInt32Bits(v0.S),
                BitConverter.SingleToInt32Bits(v0.T),
                BitConverter.SingleToInt32Bits(v1.S),
                BitConverter.SingleToInt32Bits(v1.T),
                BitConverter.SingleToInt32Bits(v1.Q),
                Tex0[ctx] & 0x3FFFFFFFFFul,
                Clamp[ctx],
                vR, vG, vB, vA,
                sigFlags);
        if (_decodedTexCache.TryGetValue(dstKey, out var existingDst) && existingDst.HasWriteSig && existingDst.WriteSig == sig)
            {
                cacheUpdatedInPlace = true;
                return;
            }

            if (_spriteBakeCache.TryGetValue(sig, out var baked))
            {
                ComputeBlockRange(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height, out int hitStartBlock, out int hitEndBlock);
                if (_deferredH2lUploads.Count > 0)
                    FlushDeferredH2lRange(hitStartBlock, hitEndBlock);
                InvalidateDecodedTexturesAt(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height);
                if (!_decodedTexCache.ContainsKey(dstKey) && _decodedTexCache.Count >= 1024)
                    _decodedTexCache.Clear();
                _decodedTexCache[dstKey] = new GsCachedTexEntry
                {
                    StartBlock = hitStartBlock,
                    EndBlock = hitEndBlock,
                    ClutStartBlock = 0,
                    ClutEndBlock = -1,
                    Hash64 = baked.DstHash64,
                    HasHash = true,
                    SharedWithBatch = true,
                    HasWriteSig = true,
                    WriteSig = sig,
                    Pixels = baked.Pixels
                };
                VramDirty = true;
                if (Mode == GsRendererMode.OpenGl)
                {
                    _deferredVramBakes[fbpBlock] = new DeferredSpriteVramWrite(
                        fbpBlock, fbwPixels, width, height, hitStartBlock, hitEndBlock, baked.Pixels);
                }
                else
                {
                    WriteSwizzled32Pixels(Vram, fbpBlock, fbwPixels, width, height, baked.Pixels);
                }
                cacheUpdatedInPlace = true;
                return;
            }

            // Rent destination buffer BEFORE invalidating so a self-overlapping srcPixels buffer cannot be returned and re-rented.
            dstPixels = RentTexBuffer(count);
            _deferredVramBakes.Remove(fbpBlock);
            if (_deferredH2lUploads.Count > 0)
            {
                ComputeBlockRange(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height, out int sBlk, out int eBlk);
                FlushDeferredH2lRange(sBlk, eBlk);
            }
            InvalidateDecodedTexturesAt(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height);
        }
        else if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
        {
            ComputeBlockRange(fbpBlock, fbwPixels, GsPsm.Psmct32, Math.Max(1, clipX1 + 1), Math.Max(1, clipY1 + 1), out int sBlk, out int eBlk);
            FlushDeferredVramRange(sBlk, eBlk);
        }

        VramDirty = true;

        bool directCopy = decal || (vR == 128 && vG == 128 && vB == 128 && (!tcc || vA == 128));
        uint alphaMask = fba ? 0x80000000u : 0u;
        uint fixedAlpha = (uint)vA << 24;

        float invQ = (!fst && v1.Q != 0.0f) ? (1.0f / v1.Q) : 1.0f;
        float invSpanX = 1.0f / spanX;
        float invSpanY = 1.0f / spanY;
        float dv = v1.V - v0.V, dt = v1.T - v0.T;
        float du = v1.U - v0.U, ds = v1.S - v0.S;

        Span<int> xPageByteOff = width <= 2048 ? stackalloc int[width] : new int[width];
        Span<byte> xPx63 = width <= 2048 ? stackalloc byte[width] : new byte[width];
        for (int dx = 0; dx < width; dx++)
        {
            int px = clipX0 + dx;
            xPageByteOff[dx] = (px >> 6) << 13;
            xPx63[dx] = (byte)(px & 63);
        }

        ref ushort offTbl = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
        int baseBytes = fbpBlock * 256;
        bool deferVramWrite = false;

        fixed (byte* vramPtr = Vram)
        fixed (uint* srcPtr = srcPixels)
        fixed (uint* dstPtr = dstPixels)
        {
            if (linear)
            {
                Span<int> xTx0 = width <= 2048 ? stackalloc int[width] : new int[width];
                Span<int> xTx1 = width <= 2048 ? stackalloc int[width] : new int[width];
                Span<uint> xUf = width <= 2048 ? stackalloc uint[width] : new uint[width];
                bool allZeroUf = true;

                for (int dx = 0; dx < width; dx++)
                {
                    int px = clipX0 + dx;
                    float tx = Math.Clamp((flipX ? (maxFx - px) : (px - minFx)) * invSpanX, 0.0f, 1.0f);
                    float fU = fst
                        ? (((ushort)(v0.U + du * tx)) / 16.0f) - 0.5f
                        : (v0.S + ds * tx) * invQ * tw - 0.5f;
                    int tx0 = (int)MathF.Floor(fU);
                    float fracU = fU - tx0;
                    int tx0w = ApplyWrapMode(tx0, tw, wms, minu, maxu);
                    xTx0[dx] = tx0w;
                    if (fracU == 0.0f)
                    {
                        xTx1[dx] = tx0w;
                        xUf[dx] = 0;
                    }
                    else
                    {
                        xTx1[dx] = ApplyWrapMode(tx0 + 1, tw, wms, minu, maxu);
                        uint uf = (uint)(fracU * 256.0f);
                        xUf[dx] = uf;
                        if (uf != 0) allZeroUf = false;
                    }
                }

                for (int py = clipY0; py <= clipY1; py++)
                {
                    float ty = Math.Clamp((flipY ? (maxFy - py) : (py - minFy)) * invSpanY, 0.0f, 1.0f);
                    int pageRowBaseBytes = baseBytes + (py >> 5) * pagesPerRow * 8192;
                    int offRowBase = (py & 31) << 6;
                    uint* dstRow = dstPtr != null ? (dstPtr + (py - clipY0) * width) : null;

                    float fV = fst
                        ? (((ushort)(v0.V + dv * ty)) / 16.0f) - 0.5f
                        : (v0.T + dt * ty) * invQ * th - 0.5f;
                    int ty0 = (int)MathF.Floor(fV);
                    float fracV = fV - ty0;
                    uint vf = (uint)(fracV * 256.0f);
                    uint invVf = 256u - vf;
                    int ty0w = ApplyWrapMode(ty0, th, wmt, minv, maxv);
                    int ty1w = vf == 0 ? ty0w : ApplyWrapMode(ty0 + 1, th, wmt, minv, maxv);
                    uint* row0 = srcPtr + ty0w * tw;
                    uint* row1 = srcPtr + ty1w * tw;

                    if (vf == 0 && allZeroUf)
                    {
                        for (int dx = 0; dx < width; dx++)
                        {
                            uint texColor = row0[xTx0[dx]];
                            uint outColor;
                            if (directCopy)
                            {
                                outColor = tcc ? texColor : ((texColor & 0x00FFFFFFu) | fixedAlpha);
                            }
                            else
                            {
                                uint tr = Math.Min(255u, ((texColor & 0xFFu) * vR) >> 7);
                                uint tg = Math.Min(255u, (((texColor >> 8) & 0xFFu) * vG) >> 7);
                                uint tb = Math.Min(255u, (((texColor >> 16) & 0xFFu) * vB) >> 7);
                                uint ta = tcc ? Math.Min(255u, (((texColor >> 24) & 0xFFu) * vA) >> 7) : vA;
                                outColor = tr | (tg << 8) | (tb << 16) | (ta << 24);
                            }
                            outColor |= alphaMask;

                            if (!deferVramWrite)
                            {
                                int byteAddr = (pageRowBaseBytes + xPageByteOff[dx] + Unsafe.Add(ref offTbl, offRowBase + xPx63[dx])) & GsSwizzle.VramByteMask;
                                *(uint*)(vramPtr + byteAddr) = outColor;
                            }
                            if (dstRow != null) dstRow[dx] = outColor;
                        }
                    }
                    else
                    {
                        for (int dx = 0; dx < width; dx++)
                        {
                            uint uf = xUf[dx];
                            int tx0w = xTx0[dx];
                            uint texColor;
                            if (uf == 0 && vf == 0)
                            {
                                texColor = row0[tx0w];
                            }
                            else
                            {
                                int tx1w = xTx1[dx];
                                uint c00 = row0[tx0w];
                                uint c10 = row0[tx1w];
                                uint c01 = row1[tx0w];
                                uint c11 = row1[tx1w];

                                if (c00 == c10 && c00 == c01 && c00 == c11)
                                {
                                    texColor = c00;
                                }
                                else
                                {
                                    uint invUf = 256u - uf;
                                    ulong w00 = (ulong)(invUf * invVf);
                                    ulong w10 = (ulong)(uf * invVf);
                                    ulong w01 = (ulong)(invUf * vf);
                                    ulong w11 = (ulong)(uf * vf);

                                    ulong rb = ((c00 & 0xFFul) | ((ulong)(c00 & 0xFF0000u) << 16)) * w00
                                             + ((c10 & 0xFFul) | ((ulong)(c10 & 0xFF0000u) << 16)) * w10
                                             + ((c01 & 0xFFul) | ((ulong)(c01 & 0xFF0000u) << 16)) * w01
                                             + ((c11 & 0xFFul) | ((ulong)(c11 & 0xFF0000u) << 16)) * w11;
                                    ulong ga = (((c00 >> 8) & 0xFFul) | ((ulong)(c00 & 0xFF000000u) << 8)) * w00
                                             + (((c10 >> 8) & 0xFFul) | ((ulong)(c10 & 0xFF000000u) << 8)) * w10
                                             + (((c01 >> 8) & 0xFFul) | ((ulong)(c01 & 0xFF000000u) << 8)) * w01
                                             + (((c11 >> 8) & 0xFFul) | ((ulong)(c11 & 0xFF000000u) << 8)) * w11;

                                    texColor = (uint)(((rb >> 16) & 0xFFul)
                                        | ((ga >> 8) & 0xFF00ul)
                                        | ((rb >> 32) & 0xFF0000ul)
                                        | ((ga >> 24) & 0xFF000000ul));
                                }
                            }

                            uint outColor;
                            if (directCopy)
                            {
                                outColor = tcc ? texColor : ((texColor & 0x00FFFFFFu) | fixedAlpha);
                            }
                            else
                            {
                                uint tr = Math.Min(255u, ((texColor & 0xFFu) * vR) >> 7);
                                uint tg = Math.Min(255u, (((texColor >> 8) & 0xFFu) * vG) >> 7);
                                uint tb = Math.Min(255u, (((texColor >> 16) & 0xFFu) * vB) >> 7);
                                uint ta = tcc ? Math.Min(255u, (((texColor >> 24) & 0xFFu) * vA) >> 7) : vA;
                                outColor = tr | (tg << 8) | (tb << 16) | (ta << 24);
                            }
                            outColor |= alphaMask;

                            if (!deferVramWrite)
                            {
                                int byteAddr = (pageRowBaseBytes + xPageByteOff[dx] + Unsafe.Add(ref offTbl, offRowBase + xPx63[dx])) & GsSwizzle.VramByteMask;
                                *(uint*)(vramPtr + byteAddr) = outColor;
                            }
                            if (dstRow != null) dstRow[dx] = outColor;
                        }
                    }
                }
            }
            else
            {
                Span<int> xIu = width <= 2048 ? stackalloc int[width] : new int[width];
                for (int dx = 0; dx < width; dx++)
                {
                    int px = clipX0 + dx;
                    float tx = Math.Clamp((flipX ? (maxFx - px) : (px - minFx)) * invSpanX, 0.0f, 1.0f);
                    int iU = fst
                        ? (((ushort)(v0.U + du * tx)) >> 4)
                        : (int)MathF.Floor((v0.S + ds * tx) * invQ * tw);
                    xIu[dx] = ApplyWrapMode(iU, tw, wms, minu, maxu);
                }

                for (int py = clipY0; py <= clipY1; py++)
                {
                    float ty = Math.Clamp((flipY ? (maxFy - py) : (py - minFy)) * invSpanY, 0.0f, 1.0f);
                    int pageRowBaseBytes = baseBytes + (py >> 5) * pagesPerRow * 8192;
                    int offRowBase = (py & 31) << 6;
                    uint* dstRow = dstPtr != null ? (dstPtr + (py - clipY0) * width) : null;

                    int iV = fst
                        ? (((ushort)(v0.V + dv * ty)) >> 4)
                        : (int)MathF.Floor((v0.T + dt * ty) * invQ * th);
                    iV = ApplyWrapMode(iV, th, wmt, minv, maxv);
                    uint* row0 = srcPtr + iV * tw;

                    for (int dx = 0; dx < width; dx++)
                    {
                        uint texColor = row0[xIu[dx]];
                        uint outColor;
                        if (directCopy)
                        {
                            outColor = tcc ? texColor : ((texColor & 0x00FFFFFFu) | fixedAlpha);
                        }
                        else
                        {
                            uint tr = Math.Min(255u, ((texColor & 0xFFu) * vR) >> 7);
                            uint tg = Math.Min(255u, (((texColor >> 8) & 0xFFu) * vG) >> 7);
                            uint tb = Math.Min(255u, (((texColor >> 16) & 0xFFu) * vB) >> 7);
                            uint ta = tcc ? Math.Min(255u, (((texColor >> 24) & 0xFFu) * vA) >> 7) : vA;
                            outColor = tr | (tg << 8) | (tb << 16) | (ta << 24);
                        }
                        outColor |= alphaMask;

                        if (!deferVramWrite)
                        {
                            int byteAddr = (pageRowBaseBytes + xPageByteOff[dx] + Unsafe.Add(ref offTbl, offRowBase + xPx63[dx])) & GsSwizzle.VramByteMask;
                            *(uint*)(vramPtr + byteAddr) = outColor;
                        }
                        if (dstRow != null) dstRow[dx] = outColor;
                    }
                }
            }
        }

        if (canCacheDst && dstPixels != null)
        {
            ulong dstHash = ComputeSigHash64(in sig);
            ComputeBlockRange(fbpBlock, fbwPixels, GsPsm.Psmct32, width, height, out int startBlock, out int endBlock);
            if (deferVramWrite)
            {
                _deferredVramBakes[fbpBlock] = new DeferredSpriteVramWrite(
                    fbpBlock, fbwPixels, width, height, startBlock, endBlock, dstPixels);
            }
            if (!_spriteBakeCache.ContainsKey(sig))
            {
                if (_spriteBakeCache.Count >= MaxSpriteBakeCacheEntries && _spriteBakeFifo.Count > 0)
                {
                    var oldest = _spriteBakeFifo.Dequeue();
                    _spriteBakeCache.Remove(oldest);
                }
                _spriteBakeCache[sig] = new GsBakedSpriteEntry
                {
                    DstHash64 = dstHash,
                    Pixels = dstPixels
                };
                _spriteBakeFifo.Enqueue(sig);
            }
            if (!_decodedTexCache.ContainsKey(dstKey) && _decodedTexCache.Count >= 1024)
                _decodedTexCache.Clear();
            _decodedTexCache[dstKey] = new GsCachedTexEntry
            {
                StartBlock = startBlock,
                EndBlock = endBlock,
                ClutStartBlock = 0,
                ClutEndBlock = -1,
                Hash64 = dstHash,
                HasHash = true,
                SharedWithBatch = true,
                HasWriteSig = true,
                WriteSig = sig,
                Pixels = dstPixels
            };
            cacheUpdatedInPlace = true;
        }
    }

    private void FlushAllDeferredVram()
    {
        if (_deferredH2lUploads.Count > 0)
            FlushAllDeferredH2l();
        if (_deferredVramBakes.Count > 0)
            FlushAllDeferredSpriteVram();
    }

    private void FlushDeferredVramRange(int startBlock, int endBlock)
    {
        if (_deferredH2lUploads.Count > 0)
            FlushDeferredH2lRange(startBlock, endBlock);
        if (_deferredVramBakes.Count > 0)
            FlushDeferredSpriteVramRange(startBlock, endBlock);
    }

    private void ClearDeferredH2l()
    {
        for (int i = 0; i < _deferredH2lUploads.Count; i++)
        {
            ArrayPool<byte>.Shared.Return(_deferredH2lUploads[i].RawBytes);
        }
        _deferredH2lUploads.Clear();
    }

    private void FlushAllDeferredH2l()
    {
        for (int i = 0; i < _deferredH2lUploads.Count; i++)
        {
            var d = _deferredH2lUploads[i];
            WriteDeferredH2lToVram(in d);
            ArrayPool<byte>.Shared.Return(d.RawBytes);
        }
        _deferredH2lUploads.Clear();
    }

    private void FlushDeferredH2lRange(int startBlock, int endBlock)
    {
        int count = _deferredH2lUploads.Count;
        if (count == 0)
            return;
        int writeIdx = 0;
        for (int i = 0; i < count; i++)
        {
            var d = _deferredH2lUploads[i];
            if (BlocksOverlap(d.StartBlock, d.EndBlock, startBlock, endBlock))
            {
                WriteDeferredH2lToVram(in d);
                ArrayPool<byte>.Shared.Return(d.RawBytes);
            }
            else
            {
                if (writeIdx != i)
                    _deferredH2lUploads[writeIdx] = d;
                writeIdx++;
            }
        }
        if (writeIdx < count)
        {
            _deferredH2lUploads.RemoveRange(writeIdx, count - writeIdx);
        }
    }

    private void WriteDeferredH2lToVram(in DeferredH2lWrite d)
    {
        int px = 0, py = 0;
        var span = d.RawBytes.AsSpan(0, d.ByteLen);
        if (d.Dpsm == GsPsm.Psmct32)
        {
            GsSwizzle.WriteRect32(Vram, d.Dbp, d.Dbw, 0, 0, d.Rrw, d.Rrh, isZ: false, span, ref px, ref py);
        }
        else if (d.Dpsm is GsPsm.Psmct16 or GsPsm.Psmct16S)
        {
            GsSwizzle.WriteRect16(Vram, d.Dbp, d.Dbw, 0, 0, d.Rrw, d.Rrh, is16S: d.Dpsm == GsPsm.Psmct16S, isZ: false, span, ref px, ref py);
        }
        else if (d.Dpsm == GsPsm.Psmt8)
        {
            GsSwizzle.WriteRect8(Vram, d.Dbp, d.Dbw, 0, 0, d.Rrw, d.Rrh, span, ref px, ref py);
        }
        else if (d.Dpsm == GsPsm.Psmt4)
        {
            GsSwizzle.WriteRect4(Vram, d.Dbp, d.Dbw, 0, 0, d.Rrw, d.Rrh, span, ref px, ref py);
        }
    }

    private void FlushAllDeferredSpriteVram()
    {
        if (_deferredVramBakes.Count == 0)
            return;
        foreach (var kv in _deferredVramBakes)
        {
            var d = kv.Value;
            WriteSwizzled32Pixels(Vram, d.FbpBlock, d.FbwPixels, d.Width, d.Height, d.Pixels);
        }
        _deferredVramBakes.Clear();
    }

    private void FlushDeferredSpriteVramRange(int startBlock, int endBlock)
    {
        if (_deferredVramBakes.Count == 0)
            return;
        List<int>? flushed = null;
        foreach (var kv in _deferredVramBakes)
        {
            var d = kv.Value;
            if (BlocksOverlap(d.StartBlock, d.EndBlock, startBlock, endBlock))
            {
                WriteSwizzled32Pixels(Vram, d.FbpBlock, d.FbwPixels, d.Width, d.Height, d.Pixels);
                flushed ??= new List<int>(2);
                flushed.Add(kv.Key);
            }
        }
        if (flushed != null)
        {
            foreach (int k in flushed)
                _deferredVramBakes.Remove(k);
        }
    }

    private static unsafe void WriteSwizzled32Pixels(byte[] vram, int fbpBlock, int fbwPixels, int width, int height, uint[] pixels)
    {
        int pagesPerRow = Math.Max(1, fbwPixels >> 6);
        int baseBytes = fbpBlock * 256;
        Span<int> xPageByteOff = width <= 2048 ? stackalloc int[width] : new int[width];
        Span<byte> xPx63 = width <= 2048 ? stackalloc byte[width] : new byte[width];
        for (int dx = 0; dx < width; dx++)
        {
            xPageByteOff[dx] = (dx >> 6) << 13;
            xPx63[dx] = (byte)(dx & 63);
        }

        ref ushort offTbl = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
        fixed (byte* vramPtr = vram)
        fixed (uint* srcPtr = pixels)
        {
            for (int py = 0; py < height; py++)
            {
                int pageRowBaseBytes = baseBytes + (py >> 5) * pagesPerRow * 8192;
                int offRowBase = (py & 31) << 6;
                uint* srcRow = srcPtr + py * width;
                for (int dx = 0; dx < width; dx++)
                {
                    int byteAddr = (pageRowBaseBytes + xPageByteOff[dx] + Unsafe.Add(ref offTbl, offRowBase + xPx63[dx])) & GsSwizzle.VramByteMask;
                    *(uint*)(vramPtr + byteAddr) = srcRow[dx];
                }
            }
        }
    }

    private static ulong ComputeSigHash64(in GsSpriteWriteSignature sig)
    {
        ulong h = 14695981039346656037ul;
        const ulong prime = 1099511628211ul;
        h ^= 0x5350525349473634ul; h *= prime;
        h ^= sig.SrcHash64; h *= prime;
        h ^= (uint)sig.SrcTw; h *= prime;
        h ^= (uint)sig.SrcTh; h *= prime;
        h ^= (uint)sig.ClipX1; h *= prime;
        h ^= (uint)sig.ClipY1; h *= prime;
        h ^= (uint)sig.Fx0Bits; h *= prime;
        h ^= (uint)sig.Fy0Bits; h *= prime;
        h ^= (uint)sig.Fx1Bits; h *= prime;
        h ^= (uint)sig.Fy1Bits; h *= prime;
        h ^= sig.U0; h *= prime;
        h ^= sig.V0; h *= prime;
        h ^= sig.U1; h *= prime;
        h ^= sig.V1; h *= prime;
        h ^= (uint)sig.S0Bits; h *= prime;
        h ^= (uint)sig.T0Bits; h *= prime;
        h ^= (uint)sig.S1Bits; h *= prime;
        h ^= (uint)sig.T1Bits; h *= prime;
        h ^= (uint)sig.Q1Bits; h *= prime;
        h ^= sig.Tex0Mask; h *= prime;
        h ^= sig.ClampReg; h *= prime;
        h ^= (uint)(sig.VR | (sig.VG << 8) | (sig.VB << 16) | (sig.VA << 24)); h *= prime;
        h ^= sig.Flags; h *= prime;
        return h;
    }

    private void RasterizeTriangle(in GsVertex v0, in GsVertex v1, in GsVertex v2)
    {
        TotalPrimitivesDrawn++;
        TotalTrianglesDrawn++;
        var rec = FrameRecorder;
        long t0 = (EnableProfiling || rec != null) ? Stopwatch.GetTimestamp() : 0;
        try
        {
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);
        bool gouraud = ((ActiveAttr >> 3) & 1ul) != 0;

        float x0 = (v0.X - ofx) / 16.0f;
        float y0 = (v0.Y - ofy) / 16.0f;
        float x1 = (v1.X - ofx) / 16.0f;
        float y1 = (v1.Y - ofy) / 16.0f;
        float x2 = (v2.X - ofx) / 16.0f;
        float y2 = (v2.Y - ofy) / 16.0f;

        float area = EdgeFunc(x0, y0, x1, y1, x2, y2);
        if (MathF.Abs(area) < 1e-5f)
            return;

        float invArea = 1.0f / area;

        ref readonly var dc = ref BuildDrawContext(ctx, gouraud);
        if (dc.Tme) TotalTexturedTriangles++; else TotalFlatTriangles++;

        int minX = Math.Max(0, Math.Max(scx0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))));
        int maxX = Math.Min(dc.FbwPixels - 1, Math.Min(scx1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2)))));
        int minY = Math.Max(0, Math.Max(scy0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))));
        int maxY = Math.Min(1023, Math.Min(scy1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2)))));

        if (minX > maxX || minY > maxY)
            return;

        if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
        {
            if (dc.Tme && dc.DecodedTexture == null)
            {
                FlushAllDeferredVram();
            }
            else
            {
                ComputeBlockRange(dc.FbpBlock, dc.FbwPixels, dc.Fpsm, Math.Max(1, maxX + 1), Math.Max(1, maxY + 1), out int tfStart, out int tfEnd);
                FlushDeferredVramRange(tfStart, tfEnd);
                if (dc.Zte)
                {
                    ComputeBlockRange(dc.ZbpBlock, dc.FbwPixels, dc.Zpsm, Math.Max(1, maxX + 1), Math.Max(1, maxY + 1), out int tzStart, out int tzEnd);
                    FlushDeferredVramRange(tzStart, tzEnd);
                }
            }
        }

        // Fast-path cull for 100% transparent alpha-blended triangles (e.g. invisible SWF bounds quads)
        bool allZeroAlpha = gouraud ? (v0.A == 0 && v1.A == 0 && v2.A == 0) : (v2.A == 0);
        if (allZeroAlpha)
        {
            if (dc.Abe && (Alpha[ctx] & 0xFFul) == 0x44ul && (!dc.Tme || dc.Tcc == 0 || dc.Tfx == 0) && (dc.Fbmsk & 0x80000000u) != 0)
            {
                return;
            }
        }

        if (EnableDebugLogging && scx0 <= scx1 && scy0 <= scy1 && (TotalPrimitivesDrawn <= 70 || (TotalPrimitivesDrawn >= 301 && _uiTriLogs < 35 && (TotalPrimitivesDrawn <= 306 || v0.A > 0))))
        {
            _uiTriLogs++;
            int fbp = (int)(Frame[ctx] & 0x1FFul) * 32;
            Console.WriteLine($"[VALID SCISSOR TRI #{TotalPrimitivesDrawn}] ctx={ctx} FBP=0x{fbp:X} bounds=[{minX}..{maxX},{minY}..{maxY}] v0=({x0:F1},{y0:F1},RGBA:0x{v0.R:X2}{v0.G:X2}{v0.B:X2}{v0.A:X2}) v1=({x1:F1},{y1:F1},A:0x{v1.A:X2}) v2=({x2:F1},{y2:F1},A:0x{v2.A:X2}) TME={dc.Tme} FST={dc.Fst} TEX0=0x{Tex0[ctx]:X16} ALPHA=0x{Alpha[ctx]:X16} TEST=0x{Test[ctx]:X16}");
        }

        float dw0Dx = (y1 - y2) * invArea;
        float dw0Dy = (x2 - x1) * invArea;
        float dw1Dx = (y2 - y0) * invArea;
        float dw1Dy = (x0 - x2) * invArea;

        double dzDx = (double)dw0Dx * ((double)v0.Z - (double)v2.Z) + (double)dw1Dx * ((double)v1.Z - (double)v2.Z);

        byte flatR = v2.R;
        byte flatG = v2.G;
        byte flatB = v2.B;
        byte flatA = v2.A;

        float drDx = 0f, dgDx = 0f, dbDx = 0f, daDx = 0f;
        if (gouraud)
        {
            drDx = dw0Dx * ((float)v0.R - (float)v2.R) + dw1Dx * ((float)v1.R - (float)v2.R);
            dgDx = dw0Dx * ((float)v0.G - (float)v2.G) + dw1Dx * ((float)v1.G - (float)v2.G);
            dbDx = dw0Dx * ((float)v0.B - (float)v2.B) + dw1Dx * ((float)v1.B - (float)v2.B);
            daDx = dw0Dx * ((float)v0.A - (float)v2.A) + dw1Dx * ((float)v1.A - (float)v2.A);
        }

        bool tme = dc.Tme;
        bool fst = dc.Fst;

        float duDx = 0f, dvDx = 0f;
        float dsDx = 0f, dtDx = 0f, dqDx = 0f;
        if (tme)
        {
            if (fst)
            {
                duDx = dw0Dx * ((float)v0.U - (float)v2.U) + dw1Dx * ((float)v1.U - (float)v2.U);
                dvDx = dw0Dx * ((float)v0.V - (float)v2.V) + dw1Dx * ((float)v1.V - (float)v2.V);
            }
            else
            {
                dsDx = dw0Dx * (v0.S - v2.S) + dw1Dx * (v1.S - v2.S);
                dtDx = dw0Dx * (v0.T - v2.T) + dw1Dx * (v1.T - v2.T);
                dqDx = dw0Dx * (v0.Q - v2.Q) + dw1Dx * (v1.Q - v2.Q);
            }
        }

        float startX = minX + 0.5f;
        bool canFast32 = dc.Fpsm == GsPsm.Psmct32
            && (!dc.Zte || dc.Zpsm is GsPsm.Psmz24 or GsPsm.Psmz32)
            && !dc.Date
            && (!tme || dc.DecodedTexture != null)
            && !EnableDebugLogging;

        if (canFast32)
        {
            RasterizeTriangleFast32(
                in dc, in v0, in v1, in v2,
                x0, y0, x1, y1, x2, y2, invArea,
                minX, maxX, minY, maxY, startX,
                gouraud, tme, fst,
                dw0Dx, dw0Dy, dw1Dx, dw1Dy, dzDx,
                drDx, dgDx, dbDx, daDx,
                duDx, dvDx, dsDx, dtDx, dqDx,
                flatR, flatG, flatB, flatA);
        }
        else if (!tme && dc.Fpsm is GsPsm.Psmct16 or GsPsm.Psmct16S
            && (!dc.Zte || dc.Ztst == 1) && (!dc.Zte || dc.Zmsk)
            && !dc.Ate && !dc.Date && !EnableDebugLogging)
        {
            RasterizeFlatTriangleFast16(
                in dc,
                x0, y0, x1, y1, x2, y2, invArea,
                minX, maxX, minY, maxY, startX,
                gouraud, dw0Dx, dw0Dy, dw1Dx, dw1Dy,
                drDx, dgDx, dbDx, daDx,
                in v0, in v1, in v2,
                flatR, flatG, flatB, flatA);
        }
        else
        {
        for (int py = minY; py <= maxY; py++)
        {
            float sampleY = py + 0.5f;
            float w0 = EdgeFunc(x1, y1, x2, y2, startX, sampleY) * invArea;
            float w1 = EdgeFunc(x2, y2, x0, y0, startX, sampleY) * invArea;
            float w2 = 1.0f - w0 - w1;

            double curZ = (double)w0 * v0.Z + (double)w1 * v1.Z + (double)w2 * v2.Z;

            float curR = 0f, curG = 0f, curB = 0f, curA = 0f;
            if (gouraud)
            {
                curR = w0 * v0.R + w1 * v1.R + w2 * v2.R;
                curG = w0 * v0.G + w1 * v1.G + w2 * v2.G;
                curB = w0 * v0.B + w1 * v1.B + w2 * v2.B;
                curA = w0 * v0.A + w1 * v1.A + w2 * v2.A;
            }

            float curU = 0f, curV = 0f;
            float curS = 0f, curT = 0f, curQ = 1f;
            if (tme)
            {
                if (fst)
                {
                    curU = w0 * v0.U + w1 * v1.U + w2 * v2.U;
                    curV = w0 * v0.V + w1 * v1.V + w2 * v2.V;
                }
                else
                {
                    curS = w0 * v0.S + w1 * v1.S + w2 * v2.S;
                    curT = w0 * v0.T + w1 * v1.T + w2 * v2.T;
                    curQ = w0 * v0.Q + w1 * v1.Q + w2 * v2.Q;
                }
            }

            for (int px = minX; px <= maxX; px++)
            {
                w2 = 1.0f - w0 - w1;
                if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                {
                    byte r, g, b, a;
                    if (gouraud)
                    {
                        r = (byte)Math.Clamp((int)(curR + 0.5f), 0, 255);
                        g = (byte)Math.Clamp((int)(curG + 0.5f), 0, 255);
                        b = (byte)Math.Clamp((int)(curB + 0.5f), 0, 255);
                        a = (byte)Math.Clamp((int)(curA + 0.5f), 0, 255);
                    }
                    else
                    {
                        r = flatR;
                        g = flatG;
                        b = flatB;
                        a = flatA;
                    }

                    uint z = curZ <= 0.0 ? 0u : (curZ >= uint.MaxValue ? uint.MaxValue : (uint)(curZ + 0.5));

                    ushort u = 0, v = 0;
                    float s = 0f, t = 0f, q = 1f;
                    if (tme)
                    {
                        if (fst)
                        {
                            u = curU <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curU + 0.5f));
                            v = curV <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curV + 0.5f));
                        }
                        else
                        {
                            s = curS;
                            t = curT;
                            q = curQ;
                        }
                    }

                    ShadeAndWritePixel(in dc, px, py, z, r, g, b, a, s, t, q, u, v);
                }

                w0 += dw0Dx;
                w1 += dw1Dx;
                curZ += dzDx;
                if (gouraud)
                {
                    curR += drDx;
                    curG += dgDx;
                    curB += dbDx;
                    curA += daDx;
                }
                if (tme)
                {
                    if (fst)
                    {
                        curU += duDx;
                        curV += dvDx;
                    }
                    else
                    {
                        curS += dsDx;
                        curT += dtDx;
                        curQ += dqDx;
                    }
                }
            }
        }
        }
        if (rec != null)
        {
            rec.RecordSoftTriangle(new SoftTriBucketKey(dc.FbpBlock, dc.Fpsm, dc.Tbp0, dc.Tme, dc.Abe, dc.Ate, dc.Zte), Stopwatch.GetTimestamp() - t0);
        }
        }
        finally
        {
            if (t0 != 0)
                TotalRasterizeTriangleTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private unsafe void RasterizeTriangleFast32(
        in GsDrawContext dc,
        in GsVertex v0,
        in GsVertex v1,
        in GsVertex v2,
        float x0, float y0, float x1, float y1, float x2, float y2, float invArea,
        int minX, int maxX, int minY, int maxY, float startX,
        bool gouraud, bool tme, bool fst,
        float dw0Dx, float dw0Dy, float dw1Dx, float dw1Dy, double dzDx,
        float drDx, float dgDx, float dbDx, float daDx,
        float duDx, float dvDx, float dsDx, float dtDx, float dqDx,
        byte flatR, byte flatG, byte flatB, byte flatA)
    {
        int fbpBlock = dc.FbpBlock;
        int fbwPixels = dc.FbwPixels;
        int pagesPerRow = Math.Max(1, fbwPixels >> 6);
        int fbBaseBytes = fbpBlock * 256;
        uint fbmsk = dc.Fbmsk;

        int zbpBlock = dc.ZbpBlock;
        int zbBaseBytes = zbpBlock * 256;
        var zpsm = dc.Zpsm;
        bool z24 = zpsm == GsPsm.Psmz24;
        bool zmsk = dc.Zmsk;
        bool zte = dc.Zte;
        int ztst = dc.Ztst;
        bool canEarlyZ = dc.CanEarlyZ;

        bool ate = dc.Ate;
        int atst = dc.Atst;
        byte aref = dc.Aref;
        int afail = dc.Afail;

        bool abe = dc.Abe;
        int specA = dc.SpecA, specB = dc.SpecB, specC = dc.SpecC, specD = dc.SpecD;
        byte fix = dc.Fix;
        bool colclamp = dc.Colclamp;
        bool fba = dc.Fba;
        bool isStandardAlphaBlend = abe && specA == 0 && specB == 1 && specC == 0 && specD == 1;

        int tw = dc.Tw, th = dc.Th;
        int twMask = tw - 1, thMask = th - 1;
        int wms = dc.Wms, wmt = dc.Wmt;
        bool repeatWrap = wms == 0 && wmt == 0;
        uint minu = dc.MinU, maxu = dc.MaxU, minv = dc.MinV, maxv = dc.MaxV;
        bool linearFilter = dc.LinearFilter;
        int tfx = dc.Tfx, tcc = dc.Tcc;
        bool constQ = !fst && v0.Q == v1.Q && v1.Q == v2.Q;
        float fixedInvQ = (constQ && v0.Q != 0.0f) ? (1.0f / v0.Q) : 1.0f;
        float dsTwDx = dsDx * tw;
        float dtThDx = dtDx * th;
        long dzDxFixed = (long)(dzDx * 65536.0);
        int drDxFixed = (int)(drDx * 65536.0f);
        int dgDxFixed = (int)(dgDx * 65536.0f);
        int dbDxFixed = (int)(dbDx * 65536.0f);
        int daDxFixed = (int)(daDx * 65536.0f);
        float dw2Dx = -(dw0Dx + dw1Dx);
        float invDw0Dx = MathF.Abs(dw0Dx) > 1e-9f ? (1.0f / dw0Dx) : 0.0f;
        float invDw1Dx = MathF.Abs(dw1Dx) > 1e-9f ? (1.0f / dw1Dx) : 0.0f;
        float invDw2Dx = MathF.Abs(dw2Dx) > 1e-9f ? (1.0f / dw2Dx) : 0.0f;

        long shadedCount = 0;
        long writtenCount = 0;
        long zFailCount = 0;

        ref ushort offTbl32 = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32Span);
        ref ushort offTbl32Z = ref MemoryMarshal.GetReference(GsSwizzle.OffsetTable32ZSpan);

        float startY = minY + 0.5f;
        float rowW0 = EdgeFunc(x1, y1, x2, y2, startX, startY) * invArea;
        float rowW1 = EdgeFunc(x2, y2, x0, y0, startX, startY) * invArea;

        fixed (byte* vramPtr = Vram)
        fixed (uint* texPtr = dc.DecodedTexture)
        {
            for (int py = minY; py <= maxY; py++, rowW0 += dw0Dy, rowW1 += dw1Dy)
            {
                float w0 = rowW0;
                float w1 = rowW1;
                float w2 = 1.0f - w0 - w1;

                int rowMinX = minX;
                int rowMaxX = maxX;
                if (w0 < -1e-4f)
                {
                    if (dw0Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w0) * invDw0Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw0Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w0) * invDw0Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (w1 < -1e-4f)
                {
                    if (dw1Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w1) * invDw1Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw1Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w1) * invDw1Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (w2 < -1e-4f)
                {
                    if (dw2Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w2) * invDw2Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw2Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w2) * invDw2Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (rowMinX > rowMaxX)
                    continue;

                if (rowMinX > minX)
                {
                    int skip = rowMinX - minX;
                    w0 += dw0Dx * skip;
                    w1 += dw1Dx * skip;
                    w2 = 1.0f - w0 - w1;
                }

                while (rowMinX <= rowMaxX && (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f))
                {
                    rowMinX++;
                    w0 += dw0Dx;
                    w1 += dw1Dx;
                    w2 = 1.0f - w0 - w1;
                }
                if (rowMinX > rowMaxX)
                    continue;

                int spanLen = rowMaxX - rowMinX;
                if (spanLen > 0)
                {
                    float w0End = w0 + dw0Dx * spanLen;
                    float w1End = w1 + dw1Dx * spanLen;
                    float w2End = 1.0f - w0End - w1End;
                    while (rowMaxX >= rowMinX && (w0End < -1e-4f || w1End < -1e-4f || w2End < -1e-4f))
                    {
                        rowMaxX--;
                        w0End -= dw0Dx;
                        w1End -= dw1Dx;
                        w2End = 1.0f - w0End - w1End;
                    }
                    if (rowMinX > rowMaxX)
                        continue;
                }

                double curZ = (double)w0 * v0.Z + (double)w1 * v1.Z + (double)w2 * v2.Z;
                long curZFixed = (long)(curZ * 65536.0) + 32768L;

                int curRFixed = 0, curGFixed = 0, curBFixed = 0, curAFixed = 0;
                if (gouraud)
                {
                    curRFixed = (int)((w0 * v0.R + w1 * v1.R + w2 * v2.R) * 65536.0f) + 32768;
                    curGFixed = (int)((w0 * v0.G + w1 * v1.G + w2 * v2.G) * 65536.0f) + 32768;
                    curBFixed = (int)((w0 * v0.B + w1 * v1.B + w2 * v2.B) * 65536.0f) + 32768;
                    curAFixed = (int)((w0 * v0.A + w1 * v1.A + w2 * v2.A) * 65536.0f) + 32768;
                }

                float curU = 0f, curV = 0f, curSTw = 0f, curTTh = 0f, curQ = 1f;
                if (tme)
                {
                    if (fst)
                    {
                        curU = w0 * v0.U + w1 * v1.U + w2 * v2.U;
                        curV = w0 * v0.V + w1 * v1.V + w2 * v2.V;
                    }
                    else
                    {
                        curSTw = (w0 * v0.S + w1 * v1.S + w2 * v2.S) * tw;
                        curTTh = (w0 * v0.T + w1 * v1.T + w2 * v2.T) * th;
                        curQ = constQ ? v0.Q : (w0 * v0.Q + w1 * v1.Q + w2 * v2.Q);
                    }
                }

                int rowPageOff = (py >> 5) * pagesPerRow * 8192;
                int fbPageRowBase = fbBaseBytes + rowPageOff;
                int zbPageRowBase = zbBaseBytes + rowPageOff;
                int offRowBase = (py & 31) << 6;
                shadedCount += rowMaxX - rowMinX + 1;

                for (int px = rowMinX; px <= rowMaxX; px++)
                {
                    {
                        long zInt = curZFixed >> 16;
                        uint z = (ulong)zInt <= uint.MaxValue ? (uint)zInt : (zInt < 0 ? 0u : uint.MaxValue);
                        uint testZ = z24 ? (z & 0xFFFFFFu) : z;
                        int xPageOff = (px >> 6) << 13;
                        int xIdx = offRowBase + (px & 63);
                        int zAddr = -1;
                        uint oldZWord = 0;

                        if (canEarlyZ)
                        {
                            if (ztst == 0)
                            {
                                zFailCount++;
                                goto NextPixel;
                            }
                            if (ztst >= 2)
                            {
                                zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                                oldZWord = *(uint*)(vramPtr + zAddr);
                                uint curZVal = z24 ? (oldZWord & 0xFFFFFFu) : oldZWord;
                                bool zPass = ztst == 2 ? (testZ >= curZVal) : (testZ > curZVal);
                                if (!zPass)
                                {
                                    zFailCount++;
                                    goto NextPixel;
                                }
                            }
                        }

                        byte vR, vG, vB, vA;
                        if (gouraud)
                        {
                            int ir = curRFixed >> 16; vR = (byte)((uint)ir <= 255u ? ir : (ir < 0 ? 0 : 255));
                            int ig = curGFixed >> 16; vG = (byte)((uint)ig <= 255u ? ig : (ig < 0 ? 0 : 255));
                            int ib = curBFixed >> 16; vB = (byte)((uint)ib <= 255u ? ib : (ib < 0 ? 0 : 255));
                            int ia = curAFixed >> 16; vA = (byte)((uint)ia <= 255u ? ia : (ia < 0 ? 0 : 255));
                        }
                        else
                        {
                            vR = flatR; vG = flatG; vB = flatB; vA = flatA;
                        }

                        byte fragR = vR, fragG = vG, fragB = vB, fragA = vA;

                        if (tme)
                        {
                            uint texColor;
                            if (linearFilter)
                            {
                                float fU, fV;
                                if (fst)
                                {
                                    ushort u = curU <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curU + 0.5f));
                                    ushort v = curV <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curV + 0.5f));
                                    fU = (u * 0.0625f) - 0.5f;
                                    fV = (v * 0.0625f) - 0.5f;
                                }
                                else
                                {
                                    float invQ = constQ ? fixedInvQ : (curQ != 0.0f ? (1.0f / curQ) : 1.0f);
                                    fU = curSTw * invQ - 0.5f;
                                    fV = curTTh * invQ - 0.5f;
                                }

                                int tx0 = (int)fU; if (fU < tx0) tx0--;
                                int ty0 = (int)fV; if (fV < ty0) ty0--;
                                float fracU = fU - tx0;
                                float fracV = fV - ty0;

                                int tx0w, ty0w;
                                if (repeatWrap)
                                {
                                    tx0w = tx0 & twMask;
                                    ty0w = ty0 & thMask;
                                }
                                else
                                {
                                    tx0w = wms == 0 ? (tx0 & twMask) : (wms == 1 ? (tx0 < 0 ? 0 : (tx0 > twMask ? twMask : tx0)) : ApplyWrapMode(tx0, tw, wms, minu, maxu));
                                    ty0w = wmt == 0 ? (ty0 & thMask) : (wmt == 1 ? (ty0 < 0 ? 0 : (ty0 > thMask ? thMask : ty0)) : ApplyWrapMode(ty0, th, wmt, minv, maxv));
                                }

                                uint c00 = texPtr[ty0w * tw + tx0w];
                                if (fracU == 0.0f && fracV == 0.0f)
                                {
                                    texColor = c00;
                                }
                                else
                                {
                                    int tx1 = tx0 + 1;
                                    int ty1 = ty0 + 1;
                                    int tx1w, ty1w;
                                    if (repeatWrap)
                                    {
                                        tx1w = tx1 & twMask;
                                        ty1w = ty1 & thMask;
                                    }
                                    else
                                    {
                                        tx1w = wms == 0 ? (tx1 & twMask) : (wms == 1 ? (tx1 < 0 ? 0 : (tx1 > twMask ? twMask : tx1)) : ApplyWrapMode(tx1, tw, wms, minu, maxu));
                                        ty1w = wmt == 0 ? (ty1 & thMask) : (wmt == 1 ? (ty1 < 0 ? 0 : (ty1 > thMask ? thMask : ty1)) : ApplyWrapMode(ty1, th, wmt, minv, maxv));
                                    }

                                    uint c10 = texPtr[ty0w * tw + tx1w];
                                    uint c01 = texPtr[ty1w * tw + tx0w];
                                    uint c11 = texPtr[ty1w * tw + tx1w];

                                    if (c00 == c10 && c00 == c01 && c00 == c11)
                                    {
                                        texColor = c00;
                                    }
                                    else
                                    {
                                        uint uf = (uint)(fracU * 256.0f);
                                        uint vf = (uint)(fracV * 256.0f);
                                        uint invUf = 256u - uf;
                                        uint invVf = 256u - vf;

                                        ulong w00 = (ulong)(invUf * invVf);
                                        ulong w10 = (ulong)(uf * invVf);
                                        ulong w01 = (ulong)(invUf * vf);
                                        ulong w11 = (ulong)(uf * vf);

                                        ulong rb = ((c00 & 0xFFul) | ((ulong)(c00 & 0xFF0000u) << 16)) * w00
                                                 + ((c10 & 0xFFul) | ((ulong)(c10 & 0xFF0000u) << 16)) * w10
                                                 + ((c01 & 0xFFul) | ((ulong)(c01 & 0xFF0000u) << 16)) * w01
                                                 + ((c11 & 0xFFul) | ((ulong)(c11 & 0xFF0000u) << 16)) * w11;
                                        ulong ga = (((c00 >> 8) & 0xFFul) | ((ulong)(c00 & 0xFF000000u) << 8)) * w00
                                                 + (((c10 >> 8) & 0xFFul) | ((ulong)(c10 & 0xFF000000u) << 8)) * w10
                                                 + (((c01 >> 8) & 0xFFul) | ((ulong)(c01 & 0xFF000000u) << 8)) * w01
                                                 + (((c11 >> 8) & 0xFFul) | ((ulong)(c11 & 0xFF000000u) << 8)) * w11;

                                        texColor = (uint)(((rb >> 16) & 0xFFul)
                                            | ((ga >> 8) & 0xFF00ul)
                                            | ((rb >> 32) & 0xFF0000ul)
                                            | ((ga >> 24) & 0xFF000000ul));
                                    }
                                }
                            }
                            else
                            {
                                int tx, ty;
                                if (fst)
                                {
                                    ushort u = curU <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curU + 0.5f));
                                    ushort v = curV <= 0f ? (ushort)0 : (ushort)Math.Min(65535, (int)(curV + 0.5f));
                                    tx = u >> 4;
                                    ty = v >> 4;
                                }
                                else
                                {
                                    float invQ = constQ ? fixedInvQ : (curQ != 0.0f ? (1.0f / curQ) : 1.0f);
                                    float fU = curSTw * invQ;
                                    float fV = curTTh * invQ;
                                    tx = (int)fU; if (fU < tx) tx--;
                                    ty = (int)fV; if (fV < ty) ty--;
                                }

                                if (repeatWrap)
                                {
                                    tx &= twMask;
                                    ty &= thMask;
                                }
                                else
                                {
                                    tx = wms == 0 ? (tx & twMask) : (wms == 1 ? (tx < 0 ? 0 : (tx > twMask ? twMask : tx)) : ApplyWrapMode(tx, tw, wms, minu, maxu));
                                    ty = wmt == 0 ? (ty & thMask) : (wmt == 1 ? (ty < 0 ? 0 : (ty > thMask ? thMask : ty)) : ApplyWrapMode(ty, th, wmt, minv, maxv));
                                }
                                texColor = texPtr[ty * tw + tx];
                            }

                            byte tR = (byte)(texColor & 0xFFu);
                            byte tG = (byte)((texColor >> 8) & 0xFFu);
                            byte tB = (byte)((texColor >> 16) & 0xFFu);
                            byte tA = (byte)((texColor >> 24) & 0xFFu);

                            switch (tfx)
                            {
                                case 0:
                                    fragR = (byte)Math.Min(255, (tR * vR) >> 7);
                                    fragG = (byte)Math.Min(255, (tG * vG) >> 7);
                                    fragB = (byte)Math.Min(255, (tB * vB) >> 7);
                                    fragA = tcc != 0 ? (byte)Math.Min(255, (tA * vA) >> 7) : vA;
                                    break;
                                case 1:
                                    fragR = tR; fragG = tG; fragB = tB;
                                    fragA = tcc != 0 ? tA : vA;
                                    break;
                                case 2:
                                    fragR = (byte)Math.Min(255, ((tR * vR) >> 7) + vA);
                                    fragG = (byte)Math.Min(255, ((tG * vG) >> 7) + vA);
                                    fragB = (byte)Math.Min(255, ((tB * vB) >> 7) + vA);
                                    fragA = tcc != 0 ? (byte)Math.Min(255, tA + vA) : vA;
                                    break;
                                case 3:
                                    fragR = (byte)Math.Min(255, ((tR * vR) >> 7) + vA);
                                    fragG = (byte)Math.Min(255, ((tG * vG) >> 7) + vA);
                                    fragB = (byte)Math.Min(255, ((tB * vB) >> 7) + vA);
                                    fragA = tcc != 0 ? tA : vA;
                                    break;
                            }
                        }

                        bool writeColor = true;
                        bool writeDepth = !zmsk;
                        uint pixFbmsk = fbmsk;

                        if (ate)
                        {
                            bool alphaPass = atst switch
                            {
                                0 => false,
                                1 => true,
                                2 => fragA < aref,
                                3 => fragA <= aref,
                                4 => fragA == aref,
                                5 => fragA >= aref,
                                6 => fragA > aref,
                                7 => fragA != aref,
                                _ => true
                            };

                            if (!alphaPass)
                            {
                                switch (afail)
                                {
                                    case 0:
                                        goto NextPixel;
                                    case 1:
                                        writeDepth = false;
                                        break;
                                    case 2:
                                        writeColor = false;
                                        break;
                                    case 3:
                                        writeDepth = false;
                                        pixFbmsk |= 0xFF000000u;
                                        break;
                                }
                            }
                        }

                        if (!canEarlyZ && zte)
                        {
                            if (ztst == 0)
                            {
                                zFailCount++;
                                goto NextPixel;
                            }
                            if (ztst >= 2)
                            {
                                zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                                oldZWord = *(uint*)(vramPtr + zAddr);
                                uint curZVal = z24 ? (oldZWord & 0xFFFFFFu) : oldZWord;
                                bool zPass = ztst == 2 ? (testZ >= curZVal) : (testZ > curZVal);
                                if (!zPass)
                                {
                                    zFailCount++;
                                    goto NextPixel;
                                }
                            }
                        }

                        if (writeDepth && zte)
                        {
                            if (zAddr < 0)
                            {
                                zAddr = (zbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32Z, xIdx)) & GsSwizzle.VramByteMask;
                                if (z24) oldZWord = *(uint*)(vramPtr + zAddr);
                            }
                            *(uint*)(vramPtr + zAddr) = z24 ? ((oldZWord & 0xFF000000u) | testZ) : testZ;
                        }

                        if (!writeColor)
                            goto NextPixel;

                        int fbAddr = (fbPageRowBase + xPageOff + Unsafe.Add(ref offTbl32, xIdx)) & GsSwizzle.VramByteMask;
                        uint dstColor = (abe || pixFbmsk != 0) ? *(uint*)(vramPtr + fbAddr) : 0u;

                        if (abe)
                        {
                            byte dR = (byte)(dstColor & 0xFFu);
                            byte dG = (byte)((dstColor >> 8) & 0xFFu);
                            byte dB = (byte)((dstColor >> 16) & 0xFFu);

                            int outR, outG, outB;
                            if (isStandardAlphaBlend)
                            {
                                outR = (((fragR - dR) * fragA) >> 7) + dR;
                                outG = (((fragG - dG) * fragA) >> 7) + dG;
                                outB = (((fragB - dB) * fragA) >> 7) + dB;
                            }
                            else
                            {
                                byte dA = (byte)((dstColor >> 24) & 0xFFu);
                                int aR = SelectBlendRgb(specA, fragR, dR);
                                int aG = SelectBlendRgb(specA, fragG, dG);
                                int aB = SelectBlendRgb(specA, fragB, dB);
                                int bR = SelectBlendRgb(specB, fragR, dR);
                                int bG = SelectBlendRgb(specB, fragG, dG);
                                int bB = SelectBlendRgb(specB, fragB, dB);
                                int cVal = specC switch { 0 => fragA, 1 => dA, 2 => fix, _ => 0 };
                                int dRVal = SelectBlendRgb(specD, fragR, dR);
                                int dGVal = SelectBlendRgb(specD, fragG, dG);
                                int dBVal = SelectBlendRgb(specD, fragB, dB);

                                outR = (((aR - bR) * cVal) >> 7) + dRVal;
                                outG = (((aG - bG) * cVal) >> 7) + dGVal;
                                outB = (((aB - bB) * cVal) >> 7) + dBVal;
                            }

                            if (colclamp)
                            {
                                fragR = (byte)(outR < 0 ? 0 : (outR > 255 ? 255 : outR));
                                fragG = (byte)(outG < 0 ? 0 : (outG > 255 ? 255 : outG));
                                fragB = (byte)(outB < 0 ? 0 : (outB > 255 ? 255 : outB));
                            }
                            else
                            {
                                fragR = (byte)(outR & 0xFF);
                                fragG = (byte)(outG & 0xFF);
                                fragB = (byte)(outB & 0xFF);
                            }
                        }

                        uint newColor = (uint)(fragR | (fragG << 8) | (fragB << 16) | (fragA << 24));
                        if (fba)
                            newColor |= 0x80000000u;

                        *(uint*)(vramPtr + fbAddr) = pixFbmsk == 0 ? newColor : ((dstColor & pixFbmsk) | (newColor & ~pixFbmsk));
                        writtenCount++;
                    }

                NextPixel:
                    curZFixed += dzDxFixed;
                    if (gouraud)
                    {
                        curRFixed += drDxFixed;
                        curGFixed += dgDxFixed;
                        curBFixed += dbDxFixed;
                        curAFixed += daDxFixed;
                    }
                    if (tme)
                    {
                        if (fst)
                        {
                            curU += duDx;
                            curV += dvDx;
                        }
                        else
                        {
                            curSTw += dsTwDx;
                            curTTh += dtThDx;
                            curQ += dqDx;
                        }
                    }
                }
            }
        }

        TotalPixelsShaded += shadedCount;
        if (tme) TotalTexturedPixels += shadedCount; else TotalFlatPixels += shadedCount;
        TotalPixelsWritten += writtenCount;
        TotalPixelsZFailed += zFailCount;
        if (writtenCount > 0)
            VramDirty = true;
    }

    private unsafe void RasterizeFlatTriangleFast16(
        in GsDrawContext dc,
        float x0, float y0, float x1, float y1, float x2, float y2, float invArea,
        int minX, int maxX, int minY, int maxY, float startX,
        bool gouraud, float dw0Dx, float dw0Dy, float dw1Dx, float dw1Dy,
        float drDx, float dgDx, float dbDx, float daDx,
        in GsVertex v0, in GsVertex v1, in GsVertex v2,
        byte flatR, byte flatG, byte flatB, byte flatA)
    {
        int fbpBlock = dc.FbpBlock;
        int fbwPixels = dc.FbwPixels;
        int pagesPerRow = Math.Max(1, fbwPixels >> 6);
        bool is16S = dc.Fpsm == GsPsm.Psmct16S;
        uint fbmsk = dc.Fbmsk;
        bool abe = dc.Abe;
        int specA = dc.SpecA, specB = dc.SpecB, specC = dc.SpecC, specD = dc.SpecD;
        byte fix = dc.Fix;
        bool colclamp = dc.Colclamp;
        bool fba = dc.Fba;

        ref byte blockTbl = ref MemoryMarshal.GetReference(is16S ? GsSwizzle.BlockTable16S : GsSwizzle.BlockTable16);
        ref byte hwTbl = ref MemoryMarshal.GetReference(GsSwizzle.ColumnHalfwordTable16);
        float dw2Dx = -(dw0Dx + dw1Dx);
        float invDw0Dx = MathF.Abs(dw0Dx) > 1e-9f ? (1.0f / dw0Dx) : 0.0f;
        float invDw1Dx = MathF.Abs(dw1Dx) > 1e-9f ? (1.0f / dw1Dx) : 0.0f;
        float invDw2Dx = MathF.Abs(dw2Dx) > 1e-9f ? (1.0f / dw2Dx) : 0.0f;

        long shadedCount = 0;
        long writtenCount = 0;

        float startY = minY + 0.5f;
        float rowW0 = EdgeFunc(x1, y1, x2, y2, startX, startY) * invArea;
        float rowW1 = EdgeFunc(x2, y2, x0, y0, startX, startY) * invArea;

        fixed (byte* vramPtr = Vram)
        {
            for (int py = minY; py <= maxY; py++, rowW0 += dw0Dy, rowW1 += dw1Dy)
            {
                float w0 = rowW0;
                float w1 = rowW1;
                float w2 = 1.0f - w0 - w1;

                int rowMinX = minX;
                int rowMaxX = maxX;
                if (w0 < -1e-4f)
                {
                    if (dw0Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w0) * invDw0Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw0Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w0) * invDw0Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (w1 < -1e-4f)
                {
                    if (dw1Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w1) * invDw1Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw1Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w1) * invDw1Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (w2 < -1e-4f)
                {
                    if (dw2Dx <= 1e-9f) continue;
                    int enter = minX + (int)MathF.Ceiling((-1e-4f - w2) * invDw2Dx);
                    if (enter > rowMinX) rowMinX = enter;
                }
                else if (dw2Dx < -1e-9f)
                {
                    int exit = minX + (int)MathF.Floor((-1e-4f - w2) * invDw2Dx);
                    if (exit < rowMaxX) rowMaxX = exit;
                }

                if (rowMinX > rowMaxX)
                    continue;

                if (rowMinX > minX)
                {
                    int skip = rowMinX - minX;
                    w0 += dw0Dx * skip;
                    w1 += dw1Dx * skip;
                    w2 = 1.0f - w0 - w1;
                }

                float curR = 0f, curG = 0f, curB = 0f, curA = 0f;
                if (gouraud)
                {
                    curR = w0 * v0.R + w1 * v1.R + w2 * v2.R;
                    curG = w0 * v0.G + w1 * v1.G + w2 * v2.G;
                    curB = w0 * v0.B + w1 * v1.B + w2 * v2.B;
                    curA = w0 * v0.A + w1 * v1.A + w2 * v2.A;
                }

                int pageRowBase = fbpBlock + (py >> 6) * pagesPerRow * 32;
                int byOff = ((py & 63) >> 3) << 2;
                int pyOff = (py & 7) << 4;
                bool enteredSpan = false;

                for (int px = rowMinX; px <= rowMaxX; px++)
                {
                    w2 = 1.0f - w0 - w1;
                    if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                    {
                        enteredSpan = true;
                        shadedCount++;

                        byte fragR, fragG, fragB, fragA;
                        if (gouraud)
                        {
                            int ir = (int)(curR + 0.5f); fragR = (byte)(ir < 0 ? 0 : (ir > 255 ? 255 : ir));
                            int ig = (int)(curG + 0.5f); fragG = (byte)(ig < 0 ? 0 : (ig > 255 ? 255 : ig));
                            int ib = (int)(curB + 0.5f); fragB = (byte)(ib < 0 ? 0 : (ib > 255 ? 255 : ib));
                            int ia = (int)(curA + 0.5f); fragA = (byte)(ia < 0 ? 0 : (ia > 255 ? 255 : ia));
                        }
                        else
                        {
                            fragR = flatR; fragG = flatG; fragB = flatB; fragA = flatA;
                        }

                        int blockInPage = Unsafe.Add(ref blockTbl, byOff + ((px & 63) >> 4));
                        int blockIdx = (pageRowBase + ((px >> 6) << 5) + blockInPage) & 0x3FFF;
                        int hwInBlock = Unsafe.Add(ref hwTbl, pyOff + (px & 15));
                        int byteAddr = (((blockIdx << 7) + hwInBlock) << 1) & GsSwizzle.VramByteMask;

                        uint dstColor = 0u;
                        if (abe || fbmsk != 0)
                        {
                            ushort c16 = *(ushort*)(vramPtr + byteAddr);
                            dstColor = GsSwizzle.Expand16To32(c16);
                        }

                        if (abe)
                        {
                            byte dR = (byte)(dstColor & 0xFFu);
                            byte dG = (byte)((dstColor >> 8) & 0xFFu);
                            byte dB = (byte)((dstColor >> 16) & 0xFFu);
                            byte dA = (byte)((dstColor >> 24) & 0xFFu);

                            int aR = SelectBlendRgb(specA, fragR, dR);
                            int aG = SelectBlendRgb(specA, fragG, dG);
                            int aB = SelectBlendRgb(specA, fragB, dB);
                            int bR = SelectBlendRgb(specB, fragR, dR);
                            int bG = SelectBlendRgb(specB, fragG, dG);
                            int bB = SelectBlendRgb(specB, fragB, dB);
                            int cVal = specC switch { 0 => fragA, 1 => dA, 2 => fix, _ => 0 };
                            int dRVal = SelectBlendRgb(specD, fragR, dR);
                            int dGVal = SelectBlendRgb(specD, fragG, dG);
                            int dBVal = SelectBlendRgb(specD, fragB, dB);

                            int outR = (((aR - bR) * cVal) >> 7) + dRVal;
                            int outG = (((aG - bG) * cVal) >> 7) + dGVal;
                            int outB = (((aB - bB) * cVal) >> 7) + dBVal;

                            if (colclamp)
                            {
                                fragR = (byte)(outR < 0 ? 0 : (outR > 255 ? 255 : outR));
                                fragG = (byte)(outG < 0 ? 0 : (outG > 255 ? 255 : outG));
                                fragB = (byte)(outB < 0 ? 0 : (outB > 255 ? 255 : outB));
                            }
                            else
                            {
                                fragR = (byte)(outR & 0xFF);
                                fragG = (byte)(outG & 0xFF);
                                fragB = (byte)(outB & 0xFF);
                            }
                        }

                        uint newColor = (uint)(fragR | (fragG << 8) | (fragB << 16) | (fragA << 24));
                        if (fba)
                            newColor |= 0x80000000u;

                        uint maskedColor = fbmsk == 0 ? newColor : ((dstColor & fbmsk) | (newColor & ~fbmsk));
                        *(ushort*)(vramPtr + byteAddr) = GsSwizzle.Pack32To16(maskedColor);
                        writtenCount++;
                    }
                    else if (enteredSpan)
                    {
                        break;
                    }

                    w0 += dw0Dx;
                    w1 += dw1Dx;
                    if (gouraud)
                    {
                        curR += drDx;
                        curG += dgDx;
                        curB += dbDx;
                        curA += daDx;
                    }
                }
            }
        }

        TotalPixelsShaded += shadedCount;
        TotalFlatPixels += shadedCount;
        TotalPixelsWritten += writtenCount;
        if (writtenCount > 0)
            VramDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EdgeFunc(float ax, float ay, float bx, float by, float px, float py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private void ShadeAndWritePixel(
        in GsDrawContext dc,
        int px,
        int py,
        uint z,
        byte vR,
        byte vG,
        byte vB,
        byte vA,
        float s,
        float t,
        float q,
        ushort u,
        ushort v)
    {
        TotalPixelsShaded++;
        if (dc.Tme) TotalTexturedPixels++; else TotalFlatPixels++;
        bool tme = dc.Tme;
        bool abe = dc.Abe;

        // Framebuffer & Z-buffer setup
        int fbpBlock = dc.FbpBlock;
        int fbwPixels = dc.FbwPixels;
        var fpsm = dc.Fpsm;
        uint fbmsk = dc.Fbmsk;

        int zbpBlock = dc.ZbpBlock;
        var zpsm = dc.Zpsm;
        bool zmsk = dc.Zmsk;

        // TEST_1/2 evaluation
        bool ate = dc.Ate;
        int atst = dc.Atst;
        byte aref = dc.Aref;
        int afail = dc.Afail;
        bool date = dc.Date;
        int datm = dc.Datm;
        bool zte = dc.Zte;
        int ztst = dc.Ztst;

        uint dstColor = 0u;
        bool dstColorRead = false;
        if (date)
        {
            dstColor = ReadFramePixel(fbpBlock, fbwPixels, fpsm, px, py);
            dstColorRead = true;
            int dstAlphaBit = (int)((dstColor >> 31) & 1u);
            if (dstAlphaBit != datm)
                return;
        }

        uint testZ = 0u;
        bool zTested = false;
        if (dc.CanEarlyZ)
        {
            testZ = zpsm switch
            {
                GsPsm.Psmz24 => z & 0xFFFFFFu,
                GsPsm.Psmz16 or GsPsm.Psmz16S => z & 0xFFFFu,
                _ => z
            };

            bool zPass;
            uint curZ = 0;
            if (ztst == 1)
            {
                zPass = true; // ALWAYS
            }
            else if (ztst == 0)
            {
                zPass = false; // NEVER
            }
            else
            {
                curZ = ReadDepthPixel(zbpBlock, fbwPixels, zpsm, px, py);
                zPass = ztst switch
                {
                    2 => testZ >= curZ, // GEQUAL
                    3 => testZ > curZ,  // GREATER
                    _ => true
                };
            }

            if (!zPass)
            {
                if (CurrentFrame == 800)
                {
                    Interlocked.Increment(ref Frame800TotalZFail);
                    if ((px == 50 && py == 350) || (px == 550 && py == 100) || (px == 320 && py == 240))
                    {
                        Console.WriteLine($"[F800 EARLY-ZFAIL ({px},{py})] pId={_frame800TotalPrims} src={_currentGifSource} testZ=0x{testZ:X} curZ=0x{curZ:X} ztst={ztst} TBP0=0x{dc.Tbp0:X}");
                    }
                    else if (Interlocked.Increment(ref Frame800ZFailLogs) <= 25)
                    {
                        Console.WriteLine($"[F800 EARLY-Z FAIL] src={_currentGifSource} px={px} py={py} testZ=0x{testZ:X8} ({testZ}) curZ=0x{curZ:X8} ({curZ}) ztst={ztst} zbpBlock=0x{zbpBlock:X} zpsm={zpsm} zmsk={zmsk}");
                    }
                }
                TotalPixelsZFailed++;
                return;
            }

            if (CurrentFrame == 800)
            {
                Interlocked.Increment(ref Frame800TotalZPass);
            }

            zTested = true;
        }

        byte fragR = vR;
        byte fragG = vG;
        byte fragB = vB;
        byte fragA = vA;

        if (tme)
        {
            uint texColor = SampleTexture(in dc, s, t, q, u, v);
            byte tR = (byte)(texColor & 0xFFu);
            byte tG = (byte)((texColor >> 8) & 0xFFu);
            byte tB = (byte)((texColor >> 16) & 0xFFu);
            byte tA = (byte)((texColor >> 24) & 0xFFu);

            int tcc = dc.Tcc;
            int tfx = dc.Tfx;

            switch (tfx)
            {
                case 0: // Modulate: C = (Ct * Cf) >> 7
                    fragR = (byte)Math.Min(255, (tR * vR) >> 7);
                    fragG = (byte)Math.Min(255, (tG * vG) >> 7);
                    fragB = (byte)Math.Min(255, (tB * vB) >> 7);
                    fragA = tcc != 0 ? (byte)Math.Min(255, (tA * vA) >> 7) : vA;
                    break;

                case 1: // Decal: C = Ct
                    fragR = tR;
                    fragG = tG;
                    fragB = tB;
                    fragA = tcc != 0 ? tA : vA;
                    break;

                case 2: // Highlight: C = ((Ct * Cf) >> 7) + Af
                    fragR = (byte)Math.Min(255, ((tR * vR) >> 7) + vA);
                    fragG = (byte)Math.Min(255, ((tG * vG) >> 7) + vA);
                    fragB = (byte)Math.Min(255, ((tB * vB) >> 7) + vA);
                    fragA = tcc != 0 ? (byte)Math.Min(255, tA + vA) : vA;
                    break;

                case 3: // Highlight2: C = ((Ct * Cf) >> 7) + Af, A = At (if TCC=1)
                    fragR = (byte)Math.Min(255, ((tR * vR) >> 7) + vA);
                    fragG = (byte)Math.Min(255, ((tG * vG) >> 7) + vA);
                    fragB = (byte)Math.Min(255, ((tB * vB) >> 7) + vA);
                    fragA = tcc != 0 ? tA : vA;
                    break;
            }
            if (EnableDebugLogging && TotalPrimitivesDrawn >= 301 && _uiShadeLogs < 25 && vA > 0 && texColor != 0)
            {
                _uiShadeLogs++;
                Console.WriteLine($"[UI TEXEL SAMPLE #{TotalPrimitivesDrawn}] p=({px},{py}) vRGBA=0x{vR:X2}{vG:X2}{vB:X2}{vA:X2} texColor=0x{texColor:X8} (tA=0x{tA:X2}) frag=0x{fragR:X2}{fragG:X2}{fragB:X2}{fragA:X2} tcc={tcc} tfx={tfx} TEX0=0x{Tex0[dc.Context]:X16}");
            }
        }

        bool writeColor = true;
        bool writeDepth = !zmsk;

        if (ate)
        {
            bool alphaPass = atst switch
            {
                0 => false,           // NEVER
                1 => true,            // ALWAYS
                2 => fragA < aref,    // LESS
                3 => fragA <= aref,   // LEQUAL
                4 => fragA == aref,   // EQUAL
                5 => fragA >= aref,   // GEQUAL
                6 => fragA > aref,    // GREATER
                7 => fragA != aref,   // NOTEQUAL
                _ => true
            };

            if (!alphaPass)
            {
                switch (afail)
                {
                    case 0: // KEEP: discard neither FB nor Z
                        return;
                    case 1: // FB_ONLY: update FB, do not update Z
                        writeDepth = false;
                        break;
                    case 2: // ZB_ONLY: update Z, do not update FB
                        writeColor = false;
                        break;
                    case 3: // RGB_ONLY: update RGB, do not update A or Z
                        writeDepth = false;
                        fbmsk |= 0xFF000000u;
                        break;
                }
            }
        }

        // Depth test (ZTE) if not already tested via Early-Z
        if (!zTested && zte)
        {
            testZ = zpsm switch
            {
                GsPsm.Psmz24 => z & 0xFFFFFFu,
                GsPsm.Psmz16 or GsPsm.Psmz16S => z & 0xFFFFu,
                _ => z
            };

            bool zPass;
            uint curZ = 0;
            if (ztst == 1)
            {
                zPass = true; // ALWAYS — no need to read existing depth pixel
            }
            else if (ztst == 0)
            {
                zPass = false; // NEVER
            }
            else
            {
                curZ = ReadDepthPixel(zbpBlock, fbwPixels, zpsm, px, py);
                zPass = ztst switch
                {
                    2 => testZ >= curZ, // GEQUAL
                    3 => testZ > curZ,  // GREATER
                    _ => true
                };
            }

            if (!zPass)
            {
                if (CurrentFrame == 800)
                {
                    Interlocked.Increment(ref Frame800TotalZFail);
                    if ((px == 50 && py == 350) || (px == 550 && py == 100) || (px == 320 && py == 240))
                    {
                        Console.WriteLine($"[F800 LATE-ZFAIL ({px},{py})] pId={_frame800TotalPrims} src={_currentGifSource} testZ=0x{testZ:X} curZ=0x{curZ:X} ztst={ztst} TBP0=0x{dc.Tbp0:X}");
                    }
                    else if (Interlocked.Increment(ref Frame800ZFailLogs) <= 25)
                    {
                        Console.WriteLine($"[F800 LATE-Z FAIL] src={_currentGifSource} px={px} py={py} testZ=0x{testZ:X8} ({testZ}) curZ=0x{curZ:X8} ({curZ}) ztst={ztst} zbpBlock=0x{zbpBlock:X} zpsm={zpsm} zmsk={zmsk}");
                    }
                }
                TotalPixelsZFailed++;
                return;
            }

            if (CurrentFrame == 800)
            {
                Interlocked.Increment(ref Frame800TotalZPass);
            }
        }

        if (writeDepth && zte)
        {
            WriteDepthPixel(zbpBlock, fbwPixels, zpsm, px, py, testZ);
        }

        if (!writeColor)
            return;

        // Read existing destination pixel only if needed for Alpha Blending or FBMSK (and not already read by DATE)
        if (!dstColorRead && (abe || fbmsk != 0))
            dstColor = ReadFramePixel(fbpBlock, fbwPixels, fpsm, px, py);

        // Alpha blending: (((A - B) * C) >> 7) + D
        if (abe)
        {
            int specA = dc.SpecA;
            int specB = dc.SpecB;
            int specC = dc.SpecC;
            int specD = dc.SpecD;
            byte fix = dc.Fix;

            byte dR = (byte)(dstColor & 0xFFu);
            byte dG = (byte)((dstColor >> 8) & 0xFFu);
            byte dB = (byte)((dstColor >> 16) & 0xFFu);
            byte dA = (byte)((dstColor >> 24) & 0xFFu);

            int aR = SelectBlendRgb(specA, fragR, dR);
            int aG = SelectBlendRgb(specA, fragG, dG);
            int aB = SelectBlendRgb(specA, fragB, dB);

            int bR = SelectBlendRgb(specB, fragR, dR);
            int bG = SelectBlendRgb(specB, fragG, dG);
            int bB = SelectBlendRgb(specB, fragB, dB);

            int cVal = specC switch
            {
                0 => fragA,
                1 => dA,
                2 => fix,
                _ => 0
            };

            int dRVal = SelectBlendRgb(specD, fragR, dR);
            int dGVal = SelectBlendRgb(specD, fragG, dG);
            int dBVal = SelectBlendRgb(specD, fragB, dB);

            int outR = (((aR - bR) * cVal) >> 7) + dRVal;
            int outG = (((aG - bG) * cVal) >> 7) + dGVal;
            int outB = (((aB - bB) * cVal) >> 7) + dBVal;

            if (dc.Colclamp)
            {
                fragR = (byte)Math.Clamp(outR, 0, 255);
                fragG = (byte)Math.Clamp(outG, 0, 255);
                fragB = (byte)Math.Clamp(outB, 0, 255);
            }
            else
            {
                fragR = (byte)(outR & 0xFF);
                fragG = (byte)(outG & 0xFF);
                fragB = (byte)(outB & 0xFF);
            }
        }

        uint newColor = (uint)(fragR | (fragG << 8) | (fragB << 16) | (fragA << 24));
        if (dc.Fba)
        {
            newColor |= 0x80000000u;
        }

        // Apply FBMSK (1 bits preserve old destination bits, 0 bits write newColor bits)
        uint maskedColor = (dstColor & fbmsk) | (newColor & ~fbmsk);
        if (CurrentFrame == 800)
        {
            Interlocked.Increment(ref Frame800TotalColorWritten);
            if (abe && fragA == 0) Interlocked.Increment(ref Frame800TotalZeroAlpha);
            if ((px == 50 && py == 350) || (px == 550 && py == 100) || (px == 320 && py == 240))
            {
                Console.WriteLine($"[F800 PIXEL ({px},{py})] pId={_frame800TotalPrims} src={_currentGifSource} zPass={zTested} fragRGBA=0x{fragR:X2}{fragG:X2}{fragB:X2}{fragA:X2} dst=0x{dstColor:X8} masked=0x{maskedColor:X8} FBP=0x{fbpBlock:X} ABE={abe} testZ={testZ} TBP0=0x{dc.Tbp0:X} psm={dc.Psm}");
            }
            else if (Interlocked.Increment(ref Frame800ZPassLogs) <= 25)
            {
                Console.WriteLine($"[F800 WRITE PIXEL] src={_currentGifSource} px={px} py={py} vRGBA=0x{vR:X2}{vG:X2}{vB:X2}{vA:X2} fragRGBA=0x{fragR:X2}{fragG:X2}{fragB:X2}{fragA:X2} dst=0x{dstColor:X8} masked=0x{maskedColor:X8} FBP=0x{fbpBlock:X} ABE={abe} testZ={testZ} TBP0=0x{dc.Tbp0:X}");
            }
        }
        if (EnableDebugLogging && ((TotalPrimitivesDrawn <= 70 && ((px == 0 && py == 0) || (px == 320 && py == 224))) ||
            (TotalPrimitivesDrawn >= 301 && _uiShadeLogs < 25 && vA > 0 && (maskedColor & 0x00FFFFFFu) != 0)))
        {
            if (TotalPrimitivesDrawn >= 301) _uiShadeLogs++;
            Console.WriteLine($"[SHADE SAMPLE #{TotalPrimitivesDrawn}] p=({px},{py}) vRGBA=0x{vR:X2}{vG:X2}{vB:X2}{vA:X2} dstColor=0x{dstColor:X8} frag=0x{fragR:X2}{fragG:X2}{fragB:X2}{fragA:X2} new=0x{newColor:X8} masked=0x{maskedColor:X8} FBP=0x{fbpBlock:X} FPSM={fpsm} FBMSK=0x{fbmsk:X8}");
        }
        WriteFramePixel(fbpBlock, fbwPixels, fpsm, px, py, maskedColor);
        TotalPixelsWritten++;
        VramDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectBlendRgb(int spec, byte cs, byte cd) => spec switch
    {
        0 => cs,
        1 => cd,
        _ => 0
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadSingleTexel(in GsDrawContext dc, int tx, int ty)
    {
        if (dc.DecodedTexture != null)
            return dc.DecodedTexture[ty * dc.Tw + tx];
        return ReadSingleTexelFromVram(in dc, tx, ty);
    }

    private uint ReadSingleTexelFromVram(in GsDrawContext dc, int tx, int ty)
    {
        int tbp0 = dc.Tbp0;
        int tbw = dc.Tbw;
        var psm = dc.Psm;
        byte ta0 = dc.Ta0;
        byte ta1 = dc.Ta1;
        bool aem = dc.Aem;

        switch (psm)
        {
            case GsPsm.Psmct32:
                return GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty);

            case GsPsm.Psmct24:
            {
                uint c24 = GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty) & 0x00FFFFFFu;
                uint a = (aem && c24 == 0) ? 0u : ta0;
                return c24 | (a << 24);
            }

            case GsPsm.Psmct16:
            case GsPsm.Psmct16S:
            {
                ushort c16 = GsSwizzle.ReadPixel16(Vram, tbp0, tbw, tx, ty, is16S: psm == GsPsm.Psmct16S);
                return GsSwizzle.Expand16To32(c16, ta0, ta1, aem);
            }

            case GsPsm.Psmt8:
            {
                byte idx = GsSwizzle.ReadTexel8(Vram, tbp0, Math.Max(128, tbw), tx, ty);
                return dc.ClutTable != null
                    ? dc.ClutTable[idx]
                    : GsSwizzle.LookupClutCsm1(Vram, dc.Cbp, dc.Cpsm, dc.Csa, idx, is8Bit: true, ta0, ta1, aem);
            }

            case GsPsm.Psmt8H:
            {
                byte idx = (byte)(GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty) >> 24);
                return dc.ClutTable != null
                    ? dc.ClutTable[idx]
                    : GsSwizzle.LookupClutCsm1(Vram, dc.Cbp, dc.Cpsm, dc.Csa, idx, is8Bit: true, ta0, ta1, aem);
            }

            case GsPsm.Psmt4:
            {
                byte idx = GsSwizzle.ReadTexel4(Vram, tbp0, Math.Max(128, tbw), tx, ty);
                return dc.ClutTable != null
                    ? dc.ClutTable[idx & 0x0F]
                    : GsSwizzle.LookupClutCsm1(Vram, dc.Cbp, dc.Cpsm, dc.Csa, idx, is8Bit: false, ta0, ta1, aem);
            }

            case GsPsm.Psmt4HL:
            {
                byte idx = (byte)((GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty) >> 24) & 0x0Fu);
                return dc.ClutTable != null
                    ? dc.ClutTable[idx & 0x0F]
                    : GsSwizzle.LookupClutCsm1(Vram, dc.Cbp, dc.Cpsm, dc.Csa, idx, is8Bit: false, ta0, ta1, aem);
            }

            case GsPsm.Psmt4HH:
            {
                byte idx = (byte)((GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty) >> 28) & 0x0Fu);
                return dc.ClutTable != null
                    ? dc.ClutTable[idx & 0x0F]
                    : GsSwizzle.LookupClutCsm1(Vram, dc.Cbp, dc.Cpsm, dc.Csa, idx, is8Bit: false, ta0, ta1, aem);
            }

            default:
                return GsSwizzle.ReadPixel32(Vram, tbp0, tbw, tx, ty);
        }
    }

    private uint SampleTexture(in GsDrawContext dc, float s, float t, float q, ushort u, ushort v)
    {
        int tw = dc.Tw;
        int th = dc.Th;

        int wms = dc.Wms;
        int wmt = dc.Wmt;
        uint minu = dc.MinU;
        uint maxu = dc.MaxU;
        uint minv = dc.MinV;
        uint maxv = dc.MaxV;

        bool linearFilter = dc.LinearFilter;
        bool fst = dc.Fst;

        if (linearFilter)
        {
            float fU, fV;
            if (fst)
            {
                fU = (u / 16.0f) - 0.5f;
                fV = (v / 16.0f) - 0.5f;
            }
            else
            {
                float invQ = q != 0.0f ? (1.0f / q) : 1.0f;
                fU = s * invQ * tw - 0.5f;
                fV = t * invQ * th - 0.5f;
            }

            int tx0 = (int)MathF.Floor(fU);
            int ty0 = (int)MathF.Floor(fV);
            int tx1 = tx0 + 1;
            int ty1 = ty0 + 1;

            float fracU = fU - tx0;
            float fracV = fV - ty0;

            tx0 = ApplyWrapMode(tx0, tw, wms, minu, maxu);
            ty0 = ApplyWrapMode(ty0, th, wmt, minv, maxv);

            uint c00 = ReadSingleTexel(in dc, tx0, ty0);
            if (fracU == 0.0f && fracV == 0.0f)
                return c00;

            tx1 = ApplyWrapMode(tx1, tw, wms, minu, maxu);
            ty1 = ApplyWrapMode(ty1, th, wmt, minv, maxv);

            uint c10 = ReadSingleTexel(in dc, tx1, ty0);
            uint c01 = ReadSingleTexel(in dc, tx0, ty1);
            uint c11 = ReadSingleTexel(in dc, tx1, ty1);

            uint uf = (uint)(fracU * 256.0f);
            uint vf = (uint)(fracV * 256.0f);
            uint invUf = 256u - uf;
            uint invVf = 256u - vf;

            uint w00 = invUf * invVf;
            uint w10 = uf * invVf;
            uint w01 = invUf * vf;
            uint w11 = uf * vf;

            uint r = ((c00 & 0xFFu) * w00 + (c10 & 0xFFu) * w10 + (c01 & 0xFFu) * w01 + (c11 & 0xFFu) * w11) >> 16;
            uint g = (((c00 >> 8) & 0xFFu) * w00 + ((c10 >> 8) & 0xFFu) * w10 + ((c01 >> 8) & 0xFFu) * w01 + ((c11 >> 8) & 0xFFu) * w11) >> 16;
            uint b = (((c00 >> 16) & 0xFFu) * w00 + ((c10 >> 16) & 0xFFu) * w10 + ((c01 >> 16) & 0xFFu) * w01 + ((c11 >> 16) & 0xFFu) * w11) >> 16;
            uint a = (((c00 >> 24) & 0xFFu) * w00 + ((c10 >> 24) & 0xFFu) * w10 + ((c01 >> 24) & 0xFFu) * w01 + ((c11 >> 24) & 0xFFu) * w11) >> 16;

            return r | (g << 8) | (b << 16) | (a << 24);
        }

        int tx, ty;
        if (fst)
        {
            tx = u >> 4;
            ty = v >> 4;
        }
        else
        {
            float invQ = q != 0.0f ? (1.0f / q) : 1.0f;
            tx = (int)MathF.Floor(s * invQ * tw);
            ty = (int)MathF.Floor(t * invQ * th);
        }

        tx = ApplyWrapMode(tx, tw, wms, minu, maxu);
        ty = ApplyWrapMode(ty, th, wmt, minv, maxv);

        return ReadSingleTexel(in dc, tx, ty);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplyWrapMode(int coord, int size, int mode, uint min, uint max)
    {
        return mode switch
        {
            0 => coord & (size - 1),
            1 => Math.Clamp(coord, 0, size - 1),
            2 => Math.Clamp(coord, (int)min, (int)max),
            _ => (int)(((uint)coord & min) | max)
        };
    }

    public uint ReadFramePixel(int fbpBlock, int fbwPixels, GsPsm fpsm, int x, int y)
    {
        if (fpsm is GsPsm.Psmct16 or GsPsm.Psmct16S)
        {
            ushort c16 = GsSwizzle.ReadPixel16(Vram, fbpBlock, fbwPixels, x, y, is16S: fpsm == GsPsm.Psmct16S);
            return GsSwizzle.Expand16To32(c16);
        }
        uint c32 = GsSwizzle.ReadPixel32(Vram, fbpBlock, fbwPixels, x, y);
        return fpsm == GsPsm.Psmct24 ? (c32 | 0x80000000u) : c32;
    }

    public void WriteFramePixel(int fbpBlock, int fbwPixels, GsPsm fpsm, int x, int y, uint c32)
    {
        if (fpsm is GsPsm.Psmct16 or GsPsm.Psmct16S)
        {
            GsSwizzle.WritePixel16(Vram, fbpBlock, fbwPixels, x, y, GsSwizzle.Pack32To16(c32), is16S: fpsm == GsPsm.Psmct16S);
        }
        else if (fpsm == GsPsm.Psmct24)
        {
            int addr = GsSwizzle.GetPixelByteAddress32(fbpBlock, fbwPixels, x, y);
            uint old = BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(addr, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan(addr, 4), (old & 0xFF000000u) | (c32 & 0x00FFFFFFu));
        }
        else
        {
            GsSwizzle.WritePixel32(Vram, fbpBlock, fbwPixels, x, y, c32);
        }
    }

    public uint ReadDepthPixel(int zbpBlock, int fbwPixels, GsPsm zpsm, int x, int y)
    {
        if (zpsm is GsPsm.Psmz16 or GsPsm.Psmz16S)
            return GsSwizzle.ReadPixel16(Vram, zbpBlock, fbwPixels, x, y, is16S: zpsm == GsPsm.Psmz16S, isZ: true);
        uint z32 = GsSwizzle.ReadPixel32(Vram, zbpBlock, fbwPixels, x, y, isZ: true);
        return zpsm == GsPsm.Psmz24 ? (z32 & 0x00FFFFFFu) : z32;
    }

    public void WriteDepthPixel(int zbpBlock, int fbwPixels, GsPsm zpsm, int x, int y, uint z)
    {
        if (zpsm is GsPsm.Psmz16 or GsPsm.Psmz16S)
        {
            GsSwizzle.WritePixel16(Vram, zbpBlock, fbwPixels, x, y, (ushort)z, is16S: zpsm == GsPsm.Psmz16S, isZ: true);
        }
        else if (zpsm == GsPsm.Psmz24)
        {
            int addr = GsSwizzle.GetPixelByteAddress32(zbpBlock, fbwPixels, x, y, isZ: true);
            uint old = BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(addr, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan(addr, 4), (old & 0xFF000000u) | (z & 0x00FFFFFFu));
        }
        else
        {
            GsSwizzle.WritePixel32(Vram, zbpBlock, fbwPixels, x, y, z, isZ: true);
        }
    }

    public void ClearDepthBuffer(int zbpBlock, int fbwPixels, int width, int height, GsPsm zpsm, uint clearZ)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WriteDepthPixel(zbpBlock, fbwPixels, zpsm, x, y, clearZ);
            }
        }
    }

    public void ClearFrameBuffer(int fbpBlock, int fbwPixels, int width, int height, GsPsm fpsm, uint clearColor)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                WriteFramePixel(fbpBlock, fbwPixels, fpsm, x, y, clearColor);
            }
        }
    }

    /// <summary>
    /// Unswizzles the active Context 1 framebuffer from 4 MB GS VRAM into a linear RGBA8888 pixel buffer.
    /// Converts PS2 alpha (0x80 = 1.0) to PC alpha (0xFF = 1.0).
    /// </summary>
    public void ExtractFrameBufferRgba32(Span<uint> outputPixels, int width, int height, int context = 0)
    {
        if (_deferredVramBakes.Count > 0 || _deferredH2lUploads.Count > 0)
            FlushAllDeferredVram();
        ulong frameReg = Frame[context];
        int fbpBlock = (int)(frameReg & 0x1FFul) * 32;
        int fbwPixels = Math.Max(1, (int)((frameReg >> 16) & 0x3Ful)) * 64;
        var fpsm = (GsPsm)((frameReg >> 24) & 0x3Ful);

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                uint c = ReadFramePixel(fbpBlock, fbwPixels, fpsm, x, y);
                uint a = Math.Min(255u, ((c >> 24) & 0xFFu) * 2u);
                outputPixels[rowBase + x] = (c & 0x00FFFFFFu) | (a << 24);
            }
        }
    }
}
