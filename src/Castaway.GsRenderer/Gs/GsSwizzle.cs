using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    // Precomputed hardware-accurate offset tables for 32-bit (32x64), 32Z (32x64), 16-bit (64x64), 8-bit (64x128), and 4-bit (128x128) page swizzling
    private static readonly ushort[] OffsetTable32 = new ushort[32 * 64];
    private static readonly ushort[] OffsetTable32Z = new ushort[32 * 64];
    private static readonly ushort[] OffsetTable16 = new ushort[64 * 64];
    private static readonly ushort[] OffsetTable16S = new ushort[64 * 64];
    private static readonly ushort[] OffsetTableZ16 = new ushort[64 * 64];
    private static readonly ushort[] OffsetTableZ16S = new ushort[64 * 64];
    private static readonly ushort[] OffsetTable8 = new ushort[64 * 128];
    private static readonly ushort[] OffsetTable4 = new ushort[128 * 128];

    internal static ReadOnlySpan<ushort> OffsetTable32Span => OffsetTable32;
    internal static ReadOnlySpan<ushort> OffsetTable32ZSpan => OffsetTable32Z;
    internal static ReadOnlySpan<ushort> OffsetTable16Span => OffsetTable16;
    internal static ReadOnlySpan<ushort> OffsetTable16SSpan => OffsetTable16S;
    internal static ReadOnlySpan<ushort> OffsetTableZ16Span => OffsetTableZ16;
    internal static ReadOnlySpan<ushort> OffsetTableZ16SSpan => OffsetTableZ16S;
    internal static ReadOnlySpan<ushort> OffsetTable8Span => OffsetTable8;
    internal static ReadOnlySpan<ushort> OffsetTable4Span => OffsetTable4;

    static GsSwizzle()
    {
        // Build PSMCT32 & PSMZ32/24 offset tables (32 rows x 64 columns, byte offset inside an 8 KiB page)
        for (int y = 0; y < 32; ++y)
        {
            int by = (y >> 3) << 3;
            int py = (y & 7) << 3;
            for (int x = 0; x < 64; ++x)
            {
                int blockInPage = BlockTable32[by + (x >> 3)];
                int wordInBlock = ColumnWordTable32[py + (x & 7)];
                OffsetTable32[(y << 6) | x] = (ushort)(((blockInPage << 6) + wordInBlock) << 2);
                OffsetTable32Z[(y << 6) | x] = (ushort)((((blockInPage ^ 0x18) << 6) + wordInBlock) << 2);
            }
        }
        // Build PSMCT16 / PSMCT16S / PSMZ16 / PSMZ16S offset tables (64 rows x 64 columns, byte offset inside an 8 KiB page)
        for (int y = 0; y < 64; ++y)
        {
            int by = (y >> 3) << 2;
            int py = (y & 7) << 4;
            for (int x = 0; x < 64; ++x)
            {
                int blkIdx = by + (x >> 4);
                int hwInBlock = ColumnHalfwordTable16[py + (x & 15)];
                int hwBytes = hwInBlock << 1;
                int idx = (y << 6) | x;
                OffsetTable16[idx] = (ushort)((BlockTable16[blkIdx] << 8) + hwBytes);
                OffsetTable16S[idx] = (ushort)((BlockTable16S[blkIdx] << 8) + hwBytes);
                OffsetTableZ16[idx] = (ushort)((BlockTableZ16[blkIdx] << 8) + hwBytes);
                OffsetTableZ16S[idx] = (ushort)((BlockTableZ16S[blkIdx] << 8) + hwBytes);
            }
        }
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
    /// When <paramref name="isZ"/> is true, applies PS2 GS hardware swizzle32Z (blockAddressXor = 0x18).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress32(int baseBlock, int bwPixels, int x, int y, bool isZ = false)
    {
        int pagesPerRow = Math.Max(1, bwPixels >> 6);
        int pageAddr = baseBlock * 256 + ((x >> 6) + (y >> 5) * pagesPerRow) * 8192;
        int off = isZ ? OffsetTable32Z[((y & 31) << 6) | (x & 63)] : OffsetTable32[((y & 31) << 6) | (x & 63)];
        return (pageAddr + off) & VramByteMask;
    }

    /// <summary>
    /// Computes the byte offset in 4 MB GS VRAM for a 16-bit pixel (PSMCT16 / PSMCT16S / PSMZ16 / PSMZ16S).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetPixelByteAddress16(int baseBlock, int bwPixels, int x, int y, bool is16S = false, bool isZ = false)
    {
        int pagesPerRow = Math.Max(1, bwPixels >> 6);
        int pageAddr = (baseBlock << 8) + (((y >> 6) * pagesPerRow + (x >> 6)) << 13);
        int idx = ((y & 63) << 6) | (x & 63);
        int off = isZ
            ? (is16S ? OffsetTableZ16S[idx] : OffsetTableZ16[idx])
            : (is16S ? OffsetTable16S[idx] : OffsetTable16[idx]);
        return (pageAddr + off) & VramByteMask;
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
    public static uint ReadPixel32(ReadOnlySpan<byte> vram, int baseBlock, int bwPixels, int x, int y, bool isZ = false)
    {
        int addr = GetPixelByteAddress32(baseBlock, bwPixels, x, y, isZ);
        return BinaryPrimitives.ReadUInt32LittleEndian(vram.Slice(addr, 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePixel32(Span<byte> vram, int baseBlock, int bwPixels, int x, int y, uint value, bool isZ = false)
    {
        int addr = GetPixelByteAddress32(baseBlock, bwPixels, x, y, isZ);
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

    public static int WriteRect32(byte[] vram, int baseBlock, int dbwPixels, int dsax, int dsay, int rrw, int rrh, bool isZ, ReadOnlySpan<byte> buf, ref int trxPixelX, ref int trxPixelY)
    {
        int pagesPerRow = Math.Max(1, dbwPixels >> 6);
        int consumed = 0;
        int bufLen = buf.Length;
        int px = trxPixelX;
        int py = trxPixelY;
        ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(vram);
        ref byte srcBase = ref MemoryMarshal.GetReference(buf);

        ref ushort tblBase = ref MemoryMarshal.GetArrayDataReference(isZ ? OffsetTable32Z : OffsetTable32);
        while (py < rrh && consumed + 4 <= bufLen)
        {
            int y = dsay + py;
            int rowPageBase = baseBlock * 256 + (y >> 5) * pagesPerRow * 8192;
            ref ushort rowTbl = ref Unsafe.Add(ref tblBase, (y & 31) << 6);
            int count = Math.Min(rrw - px, (bufLen - consumed) >> 2);
            int x = dsax + px;
            for (int i = 0; i < count; i++, x++)
            {
                int addr = (rowPageBase + ((x >> 6) << 13) + Unsafe.Add(ref rowTbl, x & 63)) & VramByteMask;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, addr), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcBase, consumed + (i << 2))));
            }
            consumed += count << 2;
            px += count;
            if (px >= rrw)
            {
                px = 0;
                py++;
            }
        }

        trxPixelX = px;
        trxPixelY = py;
        return consumed;
    }

    public static int WriteRect16(byte[] vram, int baseBlock, int dbwPixels, int dsax, int dsay, int rrw, int rrh, bool is16S, bool isZ, ReadOnlySpan<byte> buf, ref int trxPixelX, ref int trxPixelY)
    {
        int pagesPerRow = Math.Max(1, dbwPixels >> 6);
        int consumed = 0;
        int bufLen = buf.Length;
        int px = trxPixelX;
        int py = trxPixelY;
        ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(vram);
        ref byte srcBase = ref MemoryMarshal.GetReference(buf);
        ref byte blkTblBase = ref MemoryMarshal.GetReference(isZ
            ? (is16S ? BlockTableZ16S : BlockTableZ16)
            : (is16S ? BlockTable16S : BlockTable16));
        ref byte colTblBase = ref MemoryMarshal.GetReference(ColumnHalfwordTable16);

        while (py < rrh && consumed + 2 <= bufLen)
        {
            int y = dsay + py;
            int pageRowBase = baseBlock + (y >> 6) * pagesPerRow * 32;
            ref byte blkRow = ref Unsafe.Add(ref blkTblBase, ((y & 63) >> 3) << 2);
            ref byte colRow = ref Unsafe.Add(ref colTblBase, (y & 7) << 4);
            int count = Math.Min(rrw - px, (bufLen - consumed) >> 1);
            int x = dsax + px;
            for (int i = 0; i < count; i++, x++)
            {
                int pageBlockBase = pageRowBase + (x >> 6) * 32;
                int blockInPage = Unsafe.Add(ref blkRow, (x & 63) >> 4);
                int blockIdx = (pageBlockBase + blockInPage) & 0x3FFF;
                int hwInBlock = Unsafe.Add(ref colRow, x & 15);
                int addr = (((blockIdx << 7) + hwInBlock) << 1) & VramByteMask;
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref vramBase, addr), Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref srcBase, consumed + (i << 1))));
            }
            consumed += count << 1;
            px += count;
            if (px >= rrw)
            {
                px = 0;
                py++;
            }
        }

        trxPixelX = px;
        trxPixelY = py;
        return consumed;
    }

    public static int WriteRect8(byte[] vram, int baseBlock, int dbwPixels, int dsax, int dsay, int rrw, int rrh, ReadOnlySpan<byte> buf, ref int trxPixelX, ref int trxPixelY)
    {
        int bw = Math.Max(128, dbwPixels);
        int perRow = Math.Max(1, bw >> 7);
        int consumed = 0;
        int bufLen = buf.Length;
        int px = trxPixelX;
        int py = trxPixelY;
        ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(vram);
        ref byte srcBase = ref MemoryMarshal.GetReference(buf);
        ref ushort tblBase = ref MemoryMarshal.GetArrayDataReference(OffsetTable8);

        while (py < rrh && consumed < bufLen)
        {
            int y = dsay + py;
            int rowPageBase = baseBlock * 256 + (y >> 6) * perRow * 8192;
            ref ushort rowTbl = ref Unsafe.Add(ref tblBase, (y & 63) << 7);
            int count = Math.Min(rrw - px, bufLen - consumed);
            int x = dsax + px;
            for (int i = 0; i < count; i++, x++)
            {
                int addr = (rowPageBase + ((x >> 7) << 13) + Unsafe.Add(ref rowTbl, x & 127)) & VramByteMask;
                Unsafe.Add(ref vramBase, addr) = Unsafe.Add(ref srcBase, consumed + i);
            }
            consumed += count;
            px += count;
            if (px >= rrw)
            {
                px = 0;
                py++;
            }
        }

        trxPixelX = px;
        trxPixelY = py;
        return consumed;
    }

    public static int WriteRect4(byte[] vram, int baseBlock, int dbwPixels, int dsax, int dsay, int rrw, int rrh, ReadOnlySpan<byte> buf, ref int trxPixelX, ref int trxPixelY)
    {
        int bw = Math.Max(128, dbwPixels);
        int perRow = Math.Max(1, bw >> 7);
        int consumed = 0;
        int bufLen = buf.Length;
        int px = trxPixelX;
        int py = trxPixelY;
        ref byte vramBase = ref MemoryMarshal.GetArrayDataReference(vram);
        ref byte srcBase = ref MemoryMarshal.GetReference(buf);
        ref ushort tblBase = ref MemoryMarshal.GetArrayDataReference(OffsetTable4);

        while (py < rrh && consumed < bufLen)
        {
            int y = dsay + py;
            int rowPageBase = baseBlock * 256 + (y >> 7) * perRow * 8192;
            ref ushort rowTbl = ref Unsafe.Add(ref tblBase, (y & 127) << 7);

            while (px + 1 < rrw && consumed < bufLen)
            {
                byte packed = Unsafe.Add(ref srcBase, consumed++);
                int x0 = dsax + px;
                int nib0 = ((rowPageBase + ((x0 >> 7) << 13)) << 1) + Unsafe.Add(ref rowTbl, x0 & 127);
                ref byte b0 = ref Unsafe.Add(ref vramBase, (nib0 >> 1) & VramByteMask);
                b0 = (nib0 & 1) == 0 ? (byte)((b0 & 0xF0) | (packed & 0x0F)) : (byte)((b0 & 0x0F) | ((packed & 0x0F) << 4));

                int x1 = x0 + 1;
                int nib1 = ((rowPageBase + ((x1 >> 7) << 13)) << 1) + Unsafe.Add(ref rowTbl, x1 & 127);
                ref byte b1 = ref Unsafe.Add(ref vramBase, (nib1 >> 1) & VramByteMask);
                b1 = (nib1 & 1) == 0 ? (byte)((b1 & 0xF0) | (packed >> 4)) : (byte)((b1 & 0x0F) | (packed & 0xF0));

                px += 2;
            }

            if (px >= rrw)
            {
                px = 0;
                py++;
                continue;
            }

            if (consumed < bufLen)
            {
                byte packed = Unsafe.Add(ref srcBase, consumed++);
                WriteTexel4(vram, baseBlock, bw, dsax + px, dsay + py, (byte)(packed & 0x0F));
                px++;
                while (px >= rrw)
                {
                    px -= rrw;
                    py++;
                }
                if (py < rrh)
                {
                    WriteTexel4(vram, baseBlock, bw, dsax + px, dsay + py, (byte)((packed >> 4) & 0x0F));
                    px++;
                    while (px >= rrw)
                    {
                        px -= rrw;
                        py++;
                    }
                }
            }
        }

        trxPixelX = px;
        trxPixelY = py;
        return consumed;
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
