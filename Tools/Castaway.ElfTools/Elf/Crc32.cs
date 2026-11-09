namespace Castaway.ElfTools.Elf;

public static class Crc32
{
    private static readonly uint[] Table;

    static Crc32()
    {
        Table = new uint[256];
        const uint polynomial = 0xEDB88320u;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 8; j > 0; j--)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            Table[i] = crc;
        }
    }

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            byte index = (byte)((crc & 0xFF) ^ b);
            crc = (crc >> 8) ^ Table[index];
        }
        return ~crc;
    }
}
