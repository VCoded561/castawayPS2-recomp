using System.Buffers.Binary;
using Castaway.GsRenderer.Gs;

namespace Castaway.Host;

public static class VramDumper
{
    public static void DumpRawVramBmp(string path, byte[] vram, int width = 640, int height = 2048)
    {
        int pixelCount = width * height;
        int imageBytes = pixelCount * 4;
        byte[] bmp = new byte[54 + imageBytes];

        // BITMAPFILEHEADER
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), 54 + imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 54);

        // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -height); // top-down
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1); // planes
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32); // 32 bpp
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), imageBytes);

        int vramWords = vram.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int outOff = 54 + i * 4;
            if (i < vramWords)
            {
                int vOff = i * 4;
                bmp[outOff + 0] = vram[vOff + 2]; // B
                bmp[outOff + 1] = vram[vOff + 1]; // G
                bmp[outOff + 2] = vram[vOff + 0]; // R
                bmp[outOff + 3] = 0xFF;           // A forced to 255
            }
            else
            {
                bmp[outOff + 3] = 0xFF;
            }
        }

        try
        {
            File.WriteAllBytes(path, bmp);
            Console.WriteLine($"[VRAM Dump] Written {width}x{height} linear VRAM BMP to {path}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[VRAM Dump] Skipped locked file {path}: {ex.Message}");
        }
    }

    public static void DumpUnswizzledBmp(string path, byte[] vram, int fbpBlock, int fbwPixels, GsPsm psm = GsPsm.Psmct32, int width = 640, int height = 448)
    {
        int pixelCount = width * height;
        int imageBytes = pixelCount * 4;
        byte[] bmp = new byte[54 + imageBytes];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), 54 + imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 54);

        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), imageBytes);

        int outOff = 54;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint pixel;
                if (psm is GsPsm.Psmct16 or GsPsm.Psmct16S)
                {
                    ushort c16 = GsSwizzle.ReadPixel16(vram, fbpBlock, fbwPixels, x, y, is16S: psm == GsPsm.Psmct16S);
                    pixel = GsSwizzle.Expand16To32(c16);
                }
                else
                {
                    pixel = GsSwizzle.ReadPixel32(vram, fbpBlock, fbwPixels, x, y);
                }

                byte r = (byte)(pixel & 0xFFu);
                byte g = (byte)((pixel >> 8) & 0xFFu);
                byte b = (byte)((pixel >> 16) & 0xFFu);

                bmp[outOff + 0] = b;
                bmp[outOff + 1] = g;
                bmp[outOff + 2] = r;
                bmp[outOff + 3] = 0xFF;
                outOff += 4;
            }
        }

        try
        {
            File.WriteAllBytes(path, bmp);
            Console.WriteLine($"[VRAM Dump] Written unswizzled {width}x{height} (FBP=0x{fbpBlock:X}, PSM={psm}) to {path}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[VRAM Dump] Skipped locked file {path}: {ex.Message}");
        }
    }
}
