using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Castaway.GsRenderer.Gs;

public enum GsPsm : byte
{
    Psmct32  = 0x00,
    Psmct24  = 0x01,
    Psmct16  = 0x02,
    Psmct16S = 0x0A,
    Psmt8    = 0x13,
    Psmt4    = 0x14,
    Psmt8H   = 0x1B,
    Psmt4HL  = 0x24,
    Psmt4HH  = 0x2C,
    Psmz32   = 0x30,
    Psmz24   = 0x31,
    Psmz16   = 0x32,
    Psmz16S  = 0x3A
}

/// <summary>
/// Hardware-accurate PS2 Graphics Synthesizer (GS) 4 MB VRAM page/block/column/pixel swizzling
/// and CSM1 CLUT palette lookup routines.
/// </summary>
public static class GsSwizzle
{
    public const int VramByteSize = 4 * 1024 * 1024;
    public const int VramByteMask = VramByteSize - 1;
    public const int VramWordCount = VramByteSize / 4;
    public const int VramWordMask = VramWordCount - 1;

    // 32-bit / 8-bit block order within a page (8x4 blocks)
    public static ReadOnlySpan<byte> BlockTable32 =>
    [
         0,  1,  4,  5, 16, 17, 20, 21,
         2,  3,  6,  7, 18, 19, 22, 23,
         8,  9, 12, 13, 24, 25, 28, 29,
        10, 11, 14, 15, 26, 27, 30, 31
    ];

    // 16-bit / 4-bit block order within a page (4x8 blocks)
    public static ReadOnlySpan<byte> BlockTable16 =>
    [
         0,  2,  8, 10,
         1,  3,  9, 11,
         4,  6, 12, 14,
         5,  7, 13, 15,
        16, 18, 24, 26,
        17, 19, 25, 27,
        20, 22, 28, 30,
        21, 23, 29, 31
    ];

    // 16S block order within a page (8x4 blocks)
    public static ReadOnlySpan<byte> BlockTable16S =>
    [
         0,  2, 16, 18,
         1,  3, 17, 19,
         8, 10, 24, 26,
         9, 11, 25, 27,
         4,  6, 20, 22,
         5,  7, 21, 23,
        12, 14, 28, 30,
        13, 15, 29, 31
    ];

    // Z16 block order within a page (8x4 blocks)
    public static ReadOnlySpan<byte> BlockTableZ16 =>
    [
        24, 26, 16, 18,
        25, 27, 17, 19,
        28, 30, 20, 22,
        29, 31, 21, 23,
         8, 10,  0,  2,
         9, 11,  1,  3,
        12, 14,  4,  6,
        13, 15,  5,  7
    ];

    // Z16S block order within a page (8x4 blocks)
    public static ReadOnlySpan<byte> BlockTableZ16S =>
    [
        24, 26,  8, 10,
        25, 27,  9, 11,
        16, 18,  0,  2,
        17, 19,  1,  3,
        28, 30, 12, 14,
        29, 31, 13, 15,
        20, 22,  4,  6,
        21, 23,  5,  7
    ];

    // 32-bit word offset within an 8x8 block (64 words = 256 bytes)
    public static ReadOnlySpan<byte> ColumnWordTable32 =>
    [
         0,  1,  4,  5,  8,  9, 12, 13,
         2,  3,  6,  7, 10, 11, 14, 15,
        16, 17, 20, 21, 24, 25, 28, 29,
        18, 19, 22, 23, 26, 27, 30, 31,
        32, 33, 36, 37, 40, 41, 44, 45,
        34, 35, 38, 39, 42, 43, 46, 47,
        48, 49, 52, 53, 56, 57, 60, 61,
        50, 51, 54, 55, 58, 59, 62, 63
    ];

    // 16-bit halfword offset within a 16x8 block (128 halfwords = 256 bytes)
    public static ReadOnlySpan<byte> ColumnHalfwordTable16 =>
    [
          0,   1,   4,   5,   8,   9,  12,  13,  16,  17,  20,  21,  24,  25,  28,  29,
          2,   3,   6,   7,  10,  11,  14,  15,  18,  19,  22,  23,  26,  27,  30,  31,
         32,  33,  36,  37,  40,  41,  44,  45,  48,  49,  52,  53,  56,  57,  60,  61,
         34,  35,  38,  39,  42,  43,  46,  47,  50,  51,  54,  55,  58,  59,  62,  63,
         64,  65,  68,  69,  72,  73,  76,  77,  80,  81,  84,  85,  88,  89,  92,  93,
         66,  67,  70,  71,  74,  75,  78,  79,  82,  83,  86,  87,  90,  91,  94,  95,
         96,  97, 100, 101, 104, 105, 108, 109, 112, 113, 116, 117, 120, 121, 124, 125,
         98,  99, 102, 103, 106, 107, 110, 111, 114, 115, 118, 119, 122, 123, 126, 127
    ];

    // 32-bit / 8-bit block order within a page (4 rows of 8 blocks)
    private static readonly int[,] BlockTable32_2D =
    {
        {0, 1, 4, 5, 16, 17, 20, 21},
        {2, 3, 6, 7, 18, 19, 22, 23},
        {8, 9, 12, 13, 24, 25, 28, 29},
        {10, 11, 14, 15, 26, 27, 30, 31}
    };

    // 16-bit / 4-bit block order within a page (8 rows of 4 blocks)
    private static readonly int[,] BlockTable16_2D =
    {
        {0, 2, 8, 10},
        {1, 3, 9, 11},
        {4, 6, 12, 14},
        {5, 7, 13, 15},
        {16, 18, 24, 26},
        {17, 19, 25, 27},
        {20, 22, 28, 30},
        {21, 23, 29, 31}
    };

    // ColumnWord: 2 tables of 2 rows of 8 words
    private static readonly int[,,] ColumnWord =
    {
        {
            {0, 1, 4, 5, 8, 9, 12, 13},
            {2, 3, 6, 7, 10, 11, 14, 15}
        },
        {
            {8, 9, 12, 13, 0, 1, 4, 5},
            {10, 11, 14, 15, 2, 3, 6, 7}
        }
    };

    // Precomputed hardware-accurate offset tables for 8-bit (64x128) and 4-bit (128x128) page swizzling
    private static readonly ushort[] OffsetTable8 = new ushort[64 * 128];
    private static readonly ushort[] OffsetTable4 = new ushort[128 * 128];

    static GsSwizzle()
    {
        // Build PSMT8 offset table (64 rows x 128 columns)
        for (int y = 0; y < 64; ++y)
        {
            for (int x = 0; x < 128; ++x)
            {
                int block = BlockTable32_2D[(y / 16) & 3, (x / 16) & 7];
                int wx = x % 16, wy = y % 16;
                int column = wy / 4;
                wy %= 4;
                int table = (wy & 2) >> 1;
                int byteOffset = ((wx & 8) >> 2) + ((wy & 2) >> 1);
                table ^= (y / 4) & 1;
                OffsetTable8[(y << 7) + x] = (ushort)(block * 256 + column * 64 + ColumnWord[table, wy & 1, wx & 7] * 4 + byteOffset);
            }
        }

        // Build PSMT4 offset table (128 rows x 128 columns)
        for (int y = 0; y < 128; ++y)
        {
            for (int x = 0; x < 128; ++x)
            {
                int block = BlockTable16_2D[(y / 16) & 7, (x / 32) & 3];
                int wx = x % 32, wy = y % 16;
                int column = wy / 4;
                wy %= 4;
                int shift = (wx & 0x18) + ((wy & 2) << 1);
                int nibble = shift / 4;
                int table = (wy & 2) >> 1;
                table ^= column & 1;
                OffsetTable4[(y << 7) + x] = (ushort)(((column * 64 + block * 256 + ColumnWord[table, wy & 1, wx & 7] * 4) * 2) + nibble);
            }
        }
    }

    /// <summary>
    /// Computes the byte offset in 4 MB GS VRAM for a 32-bit pixel (PSMCT32 / PSMCT24 / PSMZ32 / PSMZ24 / PSMT8H / PSMT4HL / PSMT4HH).
    /// <paramref name="baseBlock"/> is in 256-byte block units (0..16383), <paramref name="bwPixels"/> is buffer width in pixels (multiple of 64).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress32(int baseBlock, int bwPixels, int x, int y)
    {
        int pagesPerRow = Math.Max(1, bwPixels >> 6);
        int pageX = x >> 6;
        int pageY = y >> 5;
        int pageBlockBase = baseBlock + (pageY * pagesPerRow + pageX) * 32;

        int bx = (x & 63) >> 3;
        int by = (y & 31) >> 3;
        int blockInPage = BlockTable32[(by << 3) + bx];
        int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;

        int px = x & 7;
        int py = y & 7;
        int wordInBlock = ColumnWordTable32[(py << 3) + px];

        return (((blockIdx << 6) + wordInBlock) << 2) & VramByteMask;
    }

    /// <summary>
    /// Computes the byte offset in 4 MB GS VRAM for a 16-bit pixel (PSMCT16 / PSMCT16S / PSMZ16 / PSMZ16S).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress16(int baseBlock, int bwPixels, int x, int y, bool is16S = false, bool isZ = false)
    {
        int pagesPerRow = Math.Max(1, bwPixels >> 6);
        int pageX = x >> 6;
        int pageY = y >> 6;
        int pageBlockBase = baseBlock + (pageY * pagesPerRow + pageX) * 32;

        int bx = (x & 63) >> 4;
        int by = (y & 63) >> 3;
        int blockInPage = isZ
            ? (is16S ? BlockTableZ16S[(by << 2) + bx] : BlockTableZ16[(by << 2) + bx])
            : (is16S ? BlockTable16S[(by << 2) + bx] : BlockTable16[(by << 2) + bx]);
        int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;

        int px = x & 15;
        int py = y & 7;
        int halfwordInBlock = ColumnHalfwordTable16[(py << 4) + px];

        return (((blockIdx << 7) + halfwordInBlock) << 1) & VramByteMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress16S(int baseBlock, int bwPixels, int x, int y)
        => GetPixelByteAddress16(baseBlock, bwPixels, x, y, is16S: true);

    /// <summary>
    /// Computes the byte offset in 4 MB GS VRAM for an 8-bit texel (PSMT8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress8(int baseBlock, int bwPixels, int x, int y)
    {
        int perRow = Math.Max(1, bwPixels >> 7);
        int pageAddr = baseBlock * 256 + ((x >> 7) + (y >> 6) * perRow) * 8192;
        int off = OffsetTable8[((y & 63) << 7) + (x & 127)];
        return (pageAddr + off) & VramByteMask;
    }

    /// <summary>
    /// Computes the nibble offset (byteAddr * 2 + (lowNibble ? 0 : 1)) in 4 MB GS VRAM for a 4-bit texel (PSMT4).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelNibbleAddress4(int baseBlock, int bwPixels, int x, int y)
    {
        int perRow = Math.Max(1, bwPixels >> 7);
        int pageAddr = baseBlock * 256 + ((x >> 7) + (y >> 7) * perRow) * 8192;
        int nibbleOff = OffsetTable4[((y & 127) << 7) + (x & 127)];
        return (pageAddr << 1) + nibbleOff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadPixel32(ReadOnlySpan<byte> vram, int baseBlock, int bwPixels, int x, int y)
    {
        int addr = GetPixelByteAddress32(baseBlock, bwPixels, x, y);
        return BinaryPrimitives.ReadUInt32LittleEndian(vram.Slice(addr, 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixel32(Span<byte> vram, int baseBlock, int bwPixels, int x, int y, uint value)
    {
        int addr = GetPixelByteAddress32(baseBlock, bwPixels, x, y);
        BinaryPrimitives.WriteUInt32LittleEndian(vram.Slice(addr, 4), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadPixel16(ReadOnlySpan<byte> vram, int baseBlock, int bwPixels, int x, int y, bool is16S = false, bool isZ = false)
    {
        int addr = GetPixelByteAddress16(baseBlock, bwPixels, x, y, is16S, isZ);
        return BinaryPrimitives.ReadUInt16LittleEndian(vram.Slice(addr, 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixel16(Span<byte> vram, int baseBlock, int bwPixels, int x, int y, ushort value, bool is16S = false, bool isZ = false)
    {
        int addr = GetPixelByteAddress16(baseBlock, bwPixels, x, y, is16S, isZ);
        BinaryPrimitives.WriteUInt16LittleEndian(vram.Slice(addr, 2), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadTexel8(ReadOnlySpan<byte> vram, int baseBlock, int bwPixels, int x, int y)
    {
        int addr = GetPixelByteAddress8(baseBlock, bwPixels, x, y);
        return vram[addr];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTexel8(Span<byte> vram, int baseBlock, int bwPixels, int x, int y, byte value)
    {
        int addr = GetPixelByteAddress8(baseBlock, bwPixels, x, y);
        vram[addr] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ReadTexel4(ReadOnlySpan<byte> vram, int baseBlock, int bwPixels, int x, int y)
    {
        int nibAddr = GetPixelNibbleAddress4(baseBlock, bwPixels, x, y);
        byte b = vram[(nibAddr >> 1) & VramByteMask];
        return (nibAddr & 1) == 0 ? (byte)(b & 0x0F) : (byte)((b >> 4) & 0x0F);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTexel4(Span<byte> vram, int baseBlock, int bwPixels, int x, int y, byte value)
    {
        int nibAddr = GetPixelNibbleAddress4(baseBlock, bwPixels, x, y);
        int byteAddr = (nibAddr >> 1) & VramByteMask;
        byte b = vram[byteAddr];
        if ((nibAddr & 1) == 0)
            vram[byteAddr] = (byte)((b & 0xF0) | (value & 0x0F));
        else
            vram[byteAddr] = (byte)((b & 0x0F) | ((value & 0x0F) << 4));
    }

    /// <summary>
    /// Converts a 16-bit PS2 ABGR1555 pixel to 32-bit RGBA8888 using TEXA (TA0, TA1, AEM).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Expand16To32(ushort c16, byte ta0 = 0x00, byte ta1 = 0x80, bool aem = false)
    {
        uint r = (uint)((c16 & 0x001F) << 3);
        uint g = (uint)(((c16 >> 5) & 0x001F) << 3);
        uint b = (uint)(((c16 >> 10) & 0x001F) << 3);
        uint a;
        if ((c16 & 0x8000) != 0)
            a = ta1;
        else if (aem && (c16 & 0x7FFF) == 0)
            a = 0;
        else
            a = ta0;
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    /// <summary>
    /// Packs a 32-bit RGBA8888 pixel into 16-bit PS2 ABGR1555.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Pack32To16(uint c32)
    {
        int r = (int)((c32 & 0xFFu) >> 3);
        int g = (int)(((c32 >> 8) & 0xFFu) >> 3);
        int b = (int)(((c32 >> 16) & 0xFFu) >> 3);
        int a = ((c32 >> 24) & 0xFFu) >= 0x40u ? 1 : 0;
        return (ushort)(r | (g << 5) | (b << 10) | (a << 15));
    }

    /// <summary>
    /// Looks up a CSM1 CLUT entry for an 8-bit or 4-bit indexed texture in GS VRAM.
    /// In 8-bit mode (256 entries), PS2 GS hardware swaps entries 8..15 and 16..23 in each 32-color group.
    /// </summary>
    public static uint LookupClutCsm1(
        ReadOnlySpan<byte> vram,
        int cbpBlock,
        GsPsm cpsm,
        int csaOffset,
        byte index,
        bool is8Bit,
        byte ta0 = 0x00,
        byte ta1 = 0x80,
        bool aem = false)
    {
        int effectiveIdx = is8Bit ? index : ((csaOffset * 16 + (index & 0x0F)) & 0x1FF);

        // Each 256 entries in PSMCT32 spans 4 blocks (16x16 pixels).
        int cbp = cbpBlock + (effectiveIdx >> 8) * 4;
        int idx256 = effectiveIdx & 0xFF;

        // In CSM1 mode, 256-entry CLUT maps 8x2 tiles across 16x16 pixels in VRAM:
        // cx = (idx & 7) | ((idx & 16) >> 1), cy = ((idx & 8) >> 3) | ((idx & 0xE0) >> 4)
        int cx = (idx256 & 7) | ((idx256 & 16) >> 1);
        int cy = ((idx256 & 8) >> 3) | ((idx256 & 0xE0) >> 4);

        if (cpsm is GsPsm.Psmct32 or GsPsm.Psmct24)
        {
            return ReadPixel32(vram, cbp, 64, cx, cy);
        }
        else
        {
            ushort c16 = ReadPixel16(vram, cbp, 64, cx, cy, is16S: cpsm == GsPsm.Psmct16S);
            return Expand16To32(c16, ta0, ta1, aem);
        }
    }
}
