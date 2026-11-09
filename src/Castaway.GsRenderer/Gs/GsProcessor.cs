using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Castaway.Core;

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
    private int _vramTransferRevision;
    private readonly int[] _cachedClutVramRevision = { -1, -1 };

    // Diagnostics & Profiling
    public bool EnableProfiling { get; set; } = false;
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
        Zbuf[0] = 0x00000001310000DCul;
        Test[0] = 0x0000000000050000ul;

        // Context 2 defaults (FBW=10 / 640px, PSMCT32, FBP=0)
        Frame[1] = 0x00000000000A0000ul;
        Xyoffset[1] = (2048ul << 4) | ((2048ul << 4) << 32);
        Scissor[1] = 0x01DF_0000_027F_0000ul;
        Zbuf[1] = 0x00000001310000DCul;
        Test[1] = 0x0000000000050000ul;
    }

    public ulong ActiveAttr => (Prmodecont & 1ul) != 0 ? Prim : Prmode;
    public int ActiveContext => (int)((ActiveAttr >> 9) & 1ul);
    public GsPrimitiveType ActivePrimType => (GsPrimitiveType)(Prim & 0x7ul);

    /// <summary>
    /// Parses and executes a stream of 128-bit GIF packets (from Path 1 XGKICK, Path 2 VIF1 DIRECT, or Path 3 GIF DMA).
    /// </summary>
    public void SubmitGifPacket(ReadOnlySpan<byte> data, string source = "")
    {
        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
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
            while (_pendingRegIdx < _pendingTotalRegs && offset + 16 <= totalBytes)
            {
                int regDesc = (int)((_pendingTagHi >> ((_pendingRegIdx % _pendingNreg) * 4)) & 0xFul);
                ExecutePackedDescriptor(regDesc, data.Slice(offset, 16));
                offset += 16;
                _pendingRegIdx++;
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
            while (_pendingRegIdx < _pendingTotalRegs && offset + 16 <= totalBytes)
            {
                ulong val0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                ulong val1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
                offset += 16;

                int desc0 = (int)((_pendingTagHi >> ((_pendingRegIdx % _pendingNreg) * 4)) & 0xFul);
                WriteInternalRegister((byte)desc0, val0);
                _pendingRegIdx++;

                if (_pendingRegIdx < _pendingTotalRegs)
                {
                    int desc1 = (int)((_pendingTagHi >> ((_pendingRegIdx % _pendingNreg) * 4)) & 0xFul);
                    WriteInternalRegister((byte)desc1, val1);
                    _pendingRegIdx++;
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
                    int totalRegs = nloop * nreg;
                    int regIdx = 0;
                    while (regIdx < totalRegs && offset + 16 <= totalBytes)
                    {
                        int regDesc = (int)((tagHi >> ((regIdx % nreg) * 4)) & 0xFul);
                        ExecutePackedDescriptor(regDesc, data.Slice(offset, 16));
                        offset += 16;
                        regIdx++;
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
                    while (regIdx < totalRegs && offset + 16 <= totalBytes)
                    {
                        ulong val0 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                        ulong val1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8, 8));
                        offset += 16;

                        int desc0 = (int)((tagHi >> ((regIdx % nreg) * 4)) & 0xFul);
                        WriteInternalRegister((byte)desc0, val0);
                        regIdx++;

                        if (regIdx < totalRegs)
                        {
                            int desc1 = (int)((tagHi >> ((regIdx % nreg) * 4)) & 0xFul);
                            WriteInternalRegister((byte)desc1, val1);
                            regIdx++;
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
            if (EnableProfiling)
                TotalSubmitGifTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private void ExecutePackedDescriptor(int desc, ReadOnlySpan<byte> qword)
    {
        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(qword.Slice(0, 8));
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(qword.Slice(8, 8));

        switch (desc)
        {
            case 0x0: // PRIM
                WriteInternalRegister(0x00, lo & 0x7FFul);
                break;

            case 0x1: // RGBAQ (R=bits 0..7, G=bits 32..39, B=bits 64..71, A=bits 96..103)
                _curR = qword[0];
                _curG = qword[4];
                _curB = qword[8];
                _curA = qword[12];
                Rgbaq = (uint)_curR | ((ulong)_curG << 8) | ((ulong)_curB << 16) | ((ulong)_curA << 24)
                      | ((ulong)BitConverter.SingleToUInt32Bits(_curQ) << 32);
                break;

            case 0x2: // ST (S=word0, T=word1, Q=word2)
            {
                uint sBits = BinaryPrimitives.ReadUInt32LittleEndian(qword.Slice(0, 4));
                uint tBits = BinaryPrimitives.ReadUInt32LittleEndian(qword.Slice(4, 4));
                uint qBits = BinaryPrimitives.ReadUInt32LittleEndian(qword.Slice(8, 4));
                _curS = BitConverter.UInt32BitsToSingle(sBits);
                _curT = BitConverter.UInt32BitsToSingle(tBits);
                float rawQ = BitConverter.UInt32BitsToSingle(qBits);
                _curQ = float.IsNaN(rawQ) || float.IsInfinity(rawQ) || rawQ == 0.0f ? 1.0f : rawQ;
                St = lo;
                break;
            }

            case 0x3: // UV (U=bits 0..13, V=bits 32..45)
                _curU = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(0, 2)) & 0x3FFFu);
                _curV = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(4, 2)) & 0x3FFFu);
                Uv = (ulong)_curU | ((ulong)_curV << 16);
                break;

            case 0x4: // XYZF2 (X=bits 0..15, Y=bits 32..47, Z=bits 68..91, F=bits 100..107, ADC=bit 111)
            {
                ushort x = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(0, 2));
                ushort y = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(4, 2));
                uint z = (uint)((hi >> 4) & 0xFFFFFFul);
                byte f = (byte)((hi >> 36) & 0xFFul);
                bool adc = ((hi >> 47) & 1ul) != 0;
                _curF = f;
                VertexKick(x, y, z, adc);
                break;
            }

            case 0x5: // XYZ2 (X=bits 0..15, Y=bits 32..47, Z=bits 64..95, ADC=bit 111)
            {
                ushort x = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(0, 2));
                ushort y = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(4, 2));
                uint z = BinaryPrimitives.ReadUInt32LittleEndian(qword.Slice(8, 4));
                bool adc = ((hi >> 47) & 1ul) != 0;
                VertexKick(x, y, z, adc);
                break;
            }

            case 0x6: // TEX0_1
                WriteInternalRegister(0x06, lo);
                break;

            case 0x7: // TEX0_2
                WriteInternalRegister(0x07, lo);
                break;

            case 0x8: // CLAMP_1
                WriteInternalRegister(0x08, lo);
                break;

            case 0x9: // CLAMP_2
                WriteInternalRegister(0x09, lo);
                break;

            case 0xA: // FOG (F=bits 100..107)
                _curF = (byte)((hi >> 36) & 0xFFul);
                break;

            case 0xC: // XYZF3 (Vertex kick without drawing)
            {
                ushort x = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(0, 2));
                ushort y = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(4, 2));
                uint z = (uint)((hi >> 4) & 0xFFFFFFul);
                _curF = (byte)((hi >> 36) & 0xFFul);
                VertexKick(x, y, z, disableDraw: true);
                break;
            }

            case 0xD: // XYZ3 (Vertex kick without drawing)
            {
                ushort x = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(0, 2));
                ushort y = BinaryPrimitives.ReadUInt16LittleEndian(qword.Slice(4, 2));
                uint z = BinaryPrimitives.ReadUInt32LittleEndian(qword.Slice(8, 4));
                VertexKick(x, y, z, disableDraw: true);
                break;
            }

            case 0xE: // A+D (Data = lo 64 bits, Addr = byte 8)
                WriteInternalRegister((byte)(hi & 0x7Ful), lo);
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
                Prim = value & 0x7FFul;
                _vtxCount = 0; // Writing PRIM resets the vertex queue
                break;

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
                Tex0[ctx] = value;
                int cld = (int)((value >> 61) & 0x7ul);
                int cbp = (int)((value >> 37) & 0x3FFFul);
                var cpsm = (GsPsm)((value >> 51) & 0xFul);
                if (cld != 0 || _activeCbp == 0)
                {
                    _activeCbp = cbp;
                    _activeCpsm = cpsm;
                }
                break;
            }
            case 0x08: Clamp[0] = value; break;
            case 0x09: Clamp[1] = value; break;
            case 0x0A: _curF = (byte)(value >> 56); break;

            case 0x0C: // XYZF3
                _curF = (byte)(value >> 56);
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)((value >> 32) & 0xFFFFFFu), disableDraw: true);
                break;

            case 0x0D: // XYZ3
                VertexKick((ushort)(value & 0xFFFFu), (ushort)((value >> 16) & 0xFFFFu), (uint)(value >> 32), disableDraw: true);
                break;

            case 0x14: Tex1[0] = value; break;
            case 0x15: Tex1[1] = value; break;
            case 0x16: Tex2[0] = value; UpdateTex0FromTex2(0, value); break;
            case 0x17: Tex2[1] = value; UpdateTex0FromTex2(1, value); break;
            case 0x18: Xyoffset[0] = value; break;
            case 0x19: Xyoffset[1] = value; break;
            case 0x1A: Prmodecont = value & 1ul; break;
            case 0x1B: Prmode = value; break;
            case 0x1C: Texclut = value; break;
            case 0x22: Scanmsk = value; break;
            case 0x3B: Texa = value; break;
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
            case 0x42: Alpha[0] = value; break;
            case 0x43: Alpha[1] = value; break;
            case 0x45: Dthe = value; break;
            case 0x46: Colclamp = value; break;
            case 0x47: Test[0] = value; break;
            case 0x48: Test[1] = value; break;
            case 0x49: Pabe = value; break;
            case 0x4A: Fba[0] = value; break;
            case 0x4B: Fba[1] = value; break;
            case 0x4C:
            case 0x4D:
            {
                int ctxIdx = regAddr - 0x4C;
                ulong oldVal = Frame[ctxIdx];
                Frame[ctxIdx] = value;
                if (EnableDebugLogging && value != oldVal && TotalVramTransfers < 30)
                {
                    int fbp = (int)(value & 0x1FFul);
                    int fbw = (int)((value >> 16) & 0x3Ful);
                    int psm = (int)((value >> 24) & 0x3Ful);
                    uint fbmsk = (uint)(value >> 32);
                    Console.WriteLine($"[GS FRAME_{ctxIdx+1}] value=0x{value:X16} (FBP=0x{fbp:X}/block 0x{fbp*32:X}, FBW={fbw*64}px, PSM=0x{psm:X2}/{(GsPsm)psm}, FBMSK=0x{fbmsk:X8}) from {_currentGifSource} | DMA PC=0x{RuntimeDispatcher.CurrentDmaPc:X8} RA=0x{RuntimeDispatcher.CurrentDmaRa:X8}");
                }
                break;
            }
            case 0x4E: Zbuf[0] = value; break;
            case 0x4F: Zbuf[1] = value; break;
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
            case 0x61: Finish = value; break;
            case 0x62: Label = value; break;
        }
    }

    private void UpdateTex0FromTex2(int ctx, ulong tex2)
    {
        // TEX2 updates PSM (bits 20..25) and CLUT fields (bits 37..63) of TEX0
        const ulong mask = (0x3Ful << 20) | (0x7FFFFFFul << 37);
        Tex0[ctx] = (Tex0[ctx] & ~mask) | (tex2 & mask);

        int cld = (int)((tex2 >> 61) & 0x7ul);
        int cbp = (int)((tex2 >> 37) & 0x3FFFul);
        var cpsm = (GsPsm)((tex2 >> 51) & 0xFul);
        if (cld != 0 || _activeCbp == 0)
        {
            _activeCbp = cbp;
            _activeCpsm = cpsm;
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
        }
        else if (dir == 2) // Local-to-Local
        {
            _trxActive = false;
            long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
            ExecuteLocalToLocalTransfer();
            if (EnableProfiling) TotalVramTransferTicks += Stopwatch.GetTimestamp() - t0;
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

        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
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

        if (EnableDebugLogging && (TotalVramTransfers <= 15 || (TotalVramTransfers >= 180 && TotalVramTransfers <= 205)))
        {
            int nonZero = 0;
            foreach (byte b in data) if (b != 0) nonZero++;
            Console.WriteLine($"[GS TRX DATA #{TotalVramTransfers}] bytes={data.Length} nonZero={nonZero} DBP=0x{dbp:X} ({dbp*256/1024}KB) DPSM={dpsm} POS=({dsax},{dsay}) SIZE=({rrw}x{rrh})");
        }

        bool usedTrxBuffer = _trxBuffer.Count > 0;
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
        int consumed = 0;

        while (_trxPixelY < rrh)
        {
            int dstX = dsax + _trxPixelX;
            int dstY = dsay + _trxPixelY;

            if (dpsm is GsPsm.Psmct32 or GsPsm.Psmz32)
            {
                if (consumed + 4 > buf.Length) break;
                uint c32 = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(consumed, 4));
                GsSwizzle.WritePixel32(Vram, dbp, dbw, dstX, dstY, c32);
                consumed += 4;
                AdvanceTrxPixel(rrw, rrh, 1);
            }
            else if (dpsm is GsPsm.Psmct24 or GsPsm.Psmz24)
            {
                if (consumed + 3 > buf.Length) break;
                uint c24 = (uint)(buf[consumed] | (buf[consumed + 1] << 8) | (buf[consumed + 2] << 16));
                int addr = GsSwizzle.GetPixelByteAddress32(dbp, dbw, dstX, dstY);
                uint old32 = BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(addr, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan(addr, 4), (old32 & 0xFF000000u) | c24);
                consumed += 3;
                AdvanceTrxPixel(rrw, rrh, 1);
            }
            else if (dpsm is GsPsm.Psmct16 or GsPsm.Psmct16S or GsPsm.Psmz16 or GsPsm.Psmz16S)
            {
                if (consumed + 2 > buf.Length) break;
                ushort c16 = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(consumed, 2));
                GsSwizzle.WritePixel16(Vram, dbp, dbw, dstX, dstY, c16, is16S: dpsm is GsPsm.Psmct16S or GsPsm.Psmz16S, isZ: dpsm is GsPsm.Psmz16 or GsPsm.Psmz16S);
                consumed += 2;
                AdvanceTrxPixel(rrw, rrh, 1);
            }
            else if (dpsm == GsPsm.Psmt8)
            {
                if (consumed + 1 > buf.Length) break;
                GsSwizzle.WriteTexel8(Vram, dbp, Math.Max(128, dbw), dstX, dstY, buf[consumed]);
                consumed += 1;
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
            else if (dpsm == GsPsm.Psmt4)
            {
                if (consumed + 1 > buf.Length) break;
                byte packed = buf[consumed++];
                GsSwizzle.WriteTexel4(Vram, dbp, Math.Max(128, dbw), dstX, dstY, (byte)(packed & 0x0F));
                AdvanceTrxPixel(rrw, rrh, 1);
                if (_trxPixelY < rrh)
                {
                    int dstX2 = dsax + _trxPixelX;
                    int dstY2 = dsay + _trxPixelY;
                    GsSwizzle.WriteTexel4(Vram, dbp, Math.Max(128, dbw), dstX2, dstY2, (byte)((packed >> 4) & 0x0F));
                    AdvanceTrxPixel(rrw, rrh, 1);
                }
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

        if (consumed > 0)
        {
            if (usedTrxBuffer)
                _trxBuffer.RemoveRange(0, consumed);
            VramDirty = true;
            _vramTransferRevision++;
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
        }
        finally
        {
            if (EnableProfiling) TotalVramTransferTicks += Stopwatch.GetTimestamp() - t0;
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

    private void ExecuteLocalToLocalTransfer()
    {
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
        _vramTransferRevision++;
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
                if (disableDraw)
                {
                    _vtxCount = 0;
                    break;
                }
                _vtxQueue[_vtxCount++] = vtx;
                if (_vtxCount == 2)
                {
                    _vtxCount = 0;
                    SubmitSprite(_vtxQueue[0], _vtxQueue[1]);
                }
                break;
        }
    }

    private void SubmitTriangle(in GsVertex v0, in GsVertex v1, in GsVertex v2)
    {
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);

        float fx0 = (v0.X - ofx) / 16.0f;
        float fy0 = (v0.Y - ofy) / 16.0f;
        float fx1 = (v1.X - ofx) / 16.0f;
        float fy1 = (v1.Y - ofy) / 16.0f;
        float fx2 = (v2.X - ofx) / 16.0f;
        float fy2 = (v2.Y - ofy) / 16.0f;

        bool gouraud = ((ActiveAttr >> 3) & 1ul) != 0;
        var dc = BuildDrawContext(ctx, gouraud);

        if (Mode == GsRendererMode.OpenGl && ActiveBatch != null)
        {
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
                R = r0 / 128.0f, G = grn0 / 128.0f, B = b0 / 128.0f, A = a0 / 128.0f,
                S = v0.S, T = v0.T, Q = v0.Q,
                U = v0.U / 16.0f, V = v0.V / 16.0f
            };
            var vtx1 = new GsGlVertex
            {
                X = fx1, Y = fy1, Z = fz1, W = 1.0f,
                R = r1 / 128.0f, G = grn1 / 128.0f, B = b1 / 128.0f, A = a1 / 128.0f,
                S = v1.S, T = v1.T, Q = v1.Q,
                U = v1.U / 16.0f, V = v1.V / 16.0f
            };
            var vtx2 = new GsGlVertex
            {
                X = fx2, Y = fy2, Z = fz2, W = 1.0f,
                R = v2.R / 128.0f, G = v2.G / 128.0f, B = v2.B / 128.0f, A = v2.A / 128.0f,
                S = v2.S, T = v2.T, Q = v2.Q,
                U = v2.U / 16.0f, V = v2.V / 16.0f
            };

            ActiveBatch.AddTriangle(in dc, scx0, scy0, scx1, scy1, ofx, ofy, in vtx0, in vtx1, in vtx2);
            return;
        }

        RasterizeTriangle(v0, v1, v2);
    }

    private void SubmitSprite(in GsVertex v0, in GsVertex v1)
    {
        RasterizeSprite(v0, v1);
    }

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
    private GsDrawContext BuildDrawContext(int ctx, bool gouraud)
    {
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

            bool clutDirty = _cachedClutVramRevision[ctx] != _vramTransferRevision
                || _cachedClutCbp[ctx] != cbp
                || _cachedClutCpsm[ctx] != cpsm
                || _cachedClutCsa[ctx] != csa
                || _cachedClutTa0[ctx] != ta0
                || _cachedClutTa1[ctx] != ta1
                || _cachedClutAem[ctx] != aem
                || _cachedClutIs8Bit[ctx] != is8Bit;

            if (clutDirty)
            {
                uint[] cache = _clutCache[ctx];
                if (is8Bit)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        cache[i] = GsSwizzle.LookupClutCsm1(Vram, cbp, cpsm, csa, (byte)i, is8Bit: true, ta0, ta1, aem);
                    }
                }
                else
                {
                    for (int i = 0; i < 16; i++)
                    {
                        cache[i] = GsSwizzle.LookupClutCsm1(Vram, cbp, cpsm, csa, (byte)i, is8Bit: false, ta0, ta1, aem);
                    }
                }

                _cachedClutVramRevision[ctx] = _vramTransferRevision;
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

        return new GsDrawContext
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
            ClutTable = clutTable
        };
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

        var dc = BuildDrawContext(ctx, gouraud: false);
        ShadeAndWritePixel(ref dc, px, py, v0.Z, v0.R, v0.G, v0.B, v0.A, v0.S, v0.T, v0.Q, v0.U, v0.V);
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

        var dc = BuildDrawContext(ctx, gouraud);
        if (dc.Tme) TotalTexturedTriangles++; else TotalFlatTriangles++;

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

            ShadeAndWritePixel(ref dc, px, py, z, r, g, b, a, sCoord, tCoord, qCoord, u, v);
        }
    }

    private void RasterizeSprite(in GsVertex v0, in GsVertex v1)
    {
        TotalPrimitivesDrawn++;
        TotalSpritesDrawn++;
        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
        try
        {
        int ctx = ActiveContext;
        var (ofx, ofy, scx0, scy0, scx1, scy1) = GetViewportBounds(ctx);

        float fx0 = (v0.X - ofx) / 16.0f;
        float fy0 = (v0.Y - ofy) / 16.0f;
        float fx1 = (v1.X - ofx) / 16.0f;
        float fy1 = (v1.Y - ofy) / 16.0f;

        float minFx = MathF.Min(fx0, fx1);
        float maxFx = MathF.Max(fx0, fx1);
        float minFy = MathF.Min(fy0, fy1);
        float maxFy = MathF.Max(fy0, fy1);

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
            return;
        }

        var dc = BuildDrawContext(ctx, gouraud: false);
        float spanX = Math.Max(1e-4f, maxFx - minFx);
        float spanY = Math.Max(1e-4f, maxFy - minFy);

        for (int py = clipY0; py <= clipY1; py++)
        {
            float ty = Math.Clamp((py - minFy) / spanY, 0.0f, 1.0f);
            ushort vTex = (ushort)(v0.V + (v1.V - v0.V) * ty);
            float tCoord = v0.T + (v1.T - v0.T) * ty;

            for (int px = clipX0; px <= clipX1; px++)
            {
                float tx = Math.Clamp((px - minFx) / spanX, 0.0f, 1.0f);
                ushort uTex = (ushort)(v0.U + (v1.U - v0.U) * tx);
                float sCoord = v0.S + (v1.S - v0.S) * tx;

                // Sprites always use flat color and Z from the second vertex (v1)
                ShadeAndWritePixel(ref dc, px, py, v1.Z, v1.R, v1.G, v1.B, v1.A, sCoord, tCoord, v1.Q, uTex, vTex);
            }
        }
        }
        finally
        {
            if (EnableProfiling)
                TotalRasterizeSpriteTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    private void RasterizeTriangle(in GsVertex v0, in GsVertex v1, in GsVertex v2)
    {
        TotalPrimitivesDrawn++;
        TotalTrianglesDrawn++;
        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
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

        var dc = BuildDrawContext(ctx, gouraud);
        if (dc.Tme) TotalTexturedTriangles++; else TotalFlatTriangles++;

        int minX = Math.Max(0, Math.Max(scx0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))));
        int maxX = Math.Min(dc.FbwPixels - 1, Math.Min(scx1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2)))));
        int minY = Math.Max(0, Math.Max(scy0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))));
        int maxY = Math.Min(1023, Math.Min(scy1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2)))));

        if (minX > maxX || minY > maxY)
            return;

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

                    ShadeAndWritePixel(ref dc, px, py, z, r, g, b, a, s, t, q, u, v);
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
        finally
        {
            if (EnableProfiling)
                TotalRasterizeTriangleTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EdgeFunc(float ax, float ay, float bx, float by, float px, float py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    private void ShadeAndWritePixel(
        ref GsDrawContext dc,
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
                uint curZ = ReadDepthPixel(zbpBlock, fbwPixels, zpsm, px, py);
                zPass = ztst switch
                {
                    2 => testZ >= curZ, // GEQUAL
                    3 => testZ > curZ,  // GREATER
                    _ => true
                };
            }

            if (!zPass)
            {
                TotalPixelsZFailed++;
                return;
            }

            zTested = true;
        }

        byte fragR = vR;
        byte fragG = vG;
        byte fragB = vB;
        byte fragA = vA;

        if (tme)
        {
            uint texColor = SampleTexture(ref dc, s, t, q, u, v);
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
                uint curZ = ReadDepthPixel(zbpBlock, fbwPixels, zpsm, px, py);
                zPass = ztst switch
                {
                    2 => testZ >= curZ, // GEQUAL
                    3 => testZ > curZ,  // GREATER
                    _ => true
                };
            }

            if (!zPass)
            {
                TotalPixelsZFailed++;
                return;
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

    private uint ReadSingleTexel(ref GsDrawContext dc, int tx, int ty)
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

    private uint SampleTexture(ref GsDrawContext dc, float s, float t, float q, ushort u, ushort v)
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

            uint c00 = ReadSingleTexel(ref dc, tx0, ty0);
            if (fracU == 0.0f && fracV == 0.0f)
                return c00;

            tx1 = ApplyWrapMode(tx1, tw, wms, minu, maxu);
            ty1 = ApplyWrapMode(ty1, th, wmt, minv, maxv);

            uint c10 = ReadSingleTexel(ref dc, tx1, ty0);
            uint c01 = ReadSingleTexel(ref dc, tx0, ty1);
            uint c11 = ReadSingleTexel(ref dc, tx1, ty1);

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

        return ReadSingleTexel(ref dc, tx, ty);
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
        uint z32 = GsSwizzle.ReadPixel32(Vram, zbpBlock, fbwPixels, x, y);
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
            int addr = GsSwizzle.GetPixelByteAddress32(zbpBlock, fbwPixels, x, y);
            uint old = BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(addr, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(Vram.AsSpan(addr, 4), (old & 0xFF000000u) | (z & 0x00FFFFFFu));
        }
        else
        {
            GsSwizzle.WritePixel32(Vram, zbpBlock, fbwPixels, x, y, z);
        }
    }

    /// <summary>
    /// Unswizzles the active Context 1 framebuffer from 4 MB GS VRAM into a linear RGBA8888 pixel buffer.
    /// Converts PS2 alpha (0x80 = 1.0) to PC alpha (0xFF = 1.0).
    /// </summary>
    public void ExtractFrameBufferRgba32(Span<uint> outputPixels, int width, int height, int context = 0)
    {
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
