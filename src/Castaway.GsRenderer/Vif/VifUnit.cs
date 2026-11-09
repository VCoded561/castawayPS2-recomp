using System.Buffers.Binary;
using System.Diagnostics;

namespace Castaway.GsRenderer.Vif;

/// <summary>
/// Sony PS2 Vector Interface (VIF0 / VIF1) command processor and data unpacker.
/// Supports STCYCL, OFFSET, BASE, ITOP, STMOD, MSKPATH3, MARK, FLUSHE/FLUSH/FLUSHA,
/// MSCAL/MSCALF/MSCNT, STMASK, STROW, STCOL, MPG, DIRECT/DIRECTHL, and all 13 UNPACK formats.
/// </summary>
public sealed class VifUnit
{
    public bool IsVif1 { get; }
    public byte[] MicroMem { get; }
    public byte[] DataMem { get; }
    public int MicroMemMask { get; }
    public int DataMemMask { get; }
    public int DataMemQwordMask { get; }

    // VIF Registers
    public uint Stat { get; set; }
    public uint Fbrst { get; set; }
    public uint Err { get; set; }
    public uint Mark { get; set; }
    public uint Cycle { get; set; } = 0x0101u; // Default CL=1, WL=1
    public uint Mode { get; set; }
    public byte Num { get; set; }
    public uint Mask { get; set; }
    public uint Code { get; set; }
    public ushort Itops { get; set; }
    public ushort Base { get; set; }
    public ushort Ofst { get; set; }
    public ushort Tops { get; set; }
    public ushort Itop { get; set; }
    public ushort Top { get; set; }
    public bool Dbf { get; set; }
    public bool Path3Masked { get; set; }

    public readonly uint[] Row = new uint[4];
    public readonly uint[] Col = new uint[4];

    // Hardware callbacks
    public Action<ushort, bool>? OnMicroProgramCall { get; set; } // (startByteAddr, isContinue)
    public Action<ReadOnlySpan<byte>>? OnDirectGifData { get; set; }
    public Action? OnFlush { get; set; }
    public Action? OnMicroMemoryModified { get; set; }

    // Streaming FIFO buffer for multi-word commands split across DMA tags
    private readonly List<byte> _fifo = new(4096);

    // Diagnostics
    public long TotalCommandsProcessed { get; private set; }
    public long TotalUnpackQuadwordsWritten { get; private set; }
    public long TotalMpgBytesUploaded { get; private set; }
    public long TotalProcessingTicks { get; set; }
    public long TotalTransfers { get; set; }

    public VifUnit(bool isVif1, byte[]? microMem = null, byte[]? dataMem = null)
    {
        IsVif1 = isVif1;
        int microSize = isVif1 ? 16 * 1024 : 4 * 1024;
        int dataSize = isVif1 ? 16 * 1024 : 4 * 1024;

        MicroMem = microMem ?? new byte[microSize];
        DataMem = dataMem ?? new byte[dataSize];
        MicroMemMask = microSize - 1;
        DataMemMask = dataSize - 1;
        DataMemQwordMask = (dataSize >> 4) - 1;
    }

    public byte Cl => (byte)(Cycle & 0xFFu);
    public byte Wl => (byte)((Cycle >> 8) & 0xFFu);

    public void Reset()
    {
        Stat = 0;
        Fbrst = 0;
        Err = 0;
        Mark = 0;
        Cycle = 0x0101u;
        Mode = 0;
        Num = 0;
        Mask = 0;
        Code = 0;
        Itops = 0;
        Base = 0;
        Ofst = 0;
        Tops = 0;
        Itop = 0;
        Top = 0;
        Dbf = false;
        Path3Masked = false;
        Array.Clear(Row);
        Array.Clear(Col);
        _fifo.Clear();
    }

    /// <summary>
    /// Feeds raw VIF stream bytes from DMA Channel 0/1 or VIF FIFO MMIO.
    /// Fast-paths direct zero-copy parsing when FIFO is empty (99.9% of transfers).
    /// </summary>
    public void ProcessData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        long t0 = Stopwatch.GetTimestamp();
        try
        {
        if (_fifo.Count == 0)
        {
            int offset = 0;
            int totalLen = data.Length & ~3;

            while (offset + 4 <= totalLen)
            {
                uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                byte rawCmd = (byte)(vifCode >> 24);
                byte cmd = (byte)(rawCmd & 0x7Fu); // Strip interrupt bit (bit 31)
                byte num = (byte)((vifCode >> 16) & 0xFFu);
                ushort imm = (ushort)(vifCode & 0xFFFFu);

                int payloadBytesNeeded = GetRequiredPayloadBytes(cmd, num, imm);
                if (payloadBytesNeeded < 0)
                {
                    offset += 4;
                    continue;
                }

                if (offset + 4 + payloadBytesNeeded > totalLen)
                {
                    break;
                }

                Code = vifCode;
                Num = num;
                TotalCommandsProcessed++;

                ReadOnlySpan<byte> payload = data.Slice(offset + 4, payloadBytesNeeded);
                ExecuteCommand(cmd, num, imm, payload);

                offset += 4 + payloadBytesNeeded;
            }

            if (offset < data.Length)
            {
                var remainder = data.Slice(offset);
                foreach (byte b in remainder)
                    _fifo.Add(b);
            }
            return;
        }

        foreach (byte b in data)
            _fifo.Add(b);

        DrainFifo();
        }
        finally
        {
            TotalProcessingTicks += Stopwatch.GetTimestamp() - t0;
            TotalTransfers++;
        }
    }

    private void DrainFifo()
    {
        int offset = 0;
        int totalLen = _fifo.Count & ~3; // Process 32-bit aligned words

        ReadOnlySpan<byte> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fifo);

        while (offset + 4 <= totalLen)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            byte rawCmd = (byte)(vifCode >> 24);
            byte cmd = (byte)(rawCmd & 0x7Fu); // Strip interrupt bit (bit 31)
            byte num = (byte)((vifCode >> 16) & 0xFFu);
            ushort imm = (ushort)(vifCode & 0xFFFFu);

            int payloadBytesNeeded = GetRequiredPayloadBytes(cmd, num, imm);
            if (payloadBytesNeeded < 0)
            {
                // Unknown/reserved VIF command: treat as single-word NOP to prevent stall
                offset += 4;
                continue;
            }

            if (offset + 4 + payloadBytesNeeded > totalLen)
            {
                // Wait for remaining payload words in subsequent DMA transfers
                break;
            }

            Code = vifCode;
            Num = num;
            TotalCommandsProcessed++;

            ReadOnlySpan<byte> payload = span.Slice(offset + 4, payloadBytesNeeded);
            ExecuteCommand(cmd, num, imm, payload);

            offset += 4 + payloadBytesNeeded;
        }

        if (offset > 0)
        {
            if (offset >= _fifo.Count)
                _fifo.Clear();
            else
                _fifo.RemoveRange(0, offset);
        }
    }

    private int GetRequiredPayloadBytes(byte cmd, byte num, ushort imm)
    {
        if ((cmd & 0x60) == 0x60)
        {
            int dstVectors = num == 0 ? 256 : num;
            int srcVectors = ComputeUnpackSourceVectorCount(dstVectors);
            int vn = (cmd >> 2) & 0x3; // 0..3 -> 1..4 components
            int vl = cmd & 0x3;        // 0=32b, 1=16b, 2=8b, 3=5b (V4-5)
            int bitsPerVector = (vn == 3 && vl == 3)
                ? 16
                : (vn + 1) * (32 >> vl);
            int totalBytes = (srcVectors * bitsPerVector + 7) >> 3;
            return (totalBytes + 3) & ~3; // 32-bit word aligned
        }

        return cmd switch
        {
            0x00 => 0, // NOP
            0x01 => 0, // STCYCL
            0x02 => 0, // OFFSET
            0x03 => 0, // BASE
            0x04 => 0, // ITOP
            0x05 => 0, // STMOD
            0x06 => 0, // MSKPATH3
            0x07 => 0, // MARK
            0x10 => 0, // FLUSHE
            0x11 => 0, // FLUSH
            0x13 => 0, // FLUSHA
            0x14 => 0, // MSCAL
            0x15 => 0, // MSCALF
            0x17 => 0, // MSCNT
            0x20 => 4, // STMASK (1 word)
            0x30 => 16, // STROW (4 words)
            0x31 => 16, // STCOL (4 words)
            0x4A => (num == 0 ? 256 : num) * 8, // MPG (num * 64-bit instructions)
            0x50 or 0x51 => (imm == 0 ? 65536 : imm) * 16, // DIRECT / DIRECTHL (imm * 128-bit qwords)
            _ => -1
        };
    }

    private int ComputeUnpackSourceVectorCount(int dstVectors)
    {
        int cl = Math.Max(1, (int)Cl);
        int wl = Math.Max(1, (int)Wl);
        if (cl >= wl)
        {
            // Skipping write mode: every destination vector comes from the source stream
            return dstVectors;
        }
        else
        {
            // Filling write mode (CL < WL): each cycle of WL destination vectors consumes only CL source vectors
            int fullCycles = dstVectors / wl;
            int rem = dstVectors % wl;
            return fullCycles * cl + Math.Min(rem, cl);
        }
    }

    private void ExecuteCommand(byte cmd, byte num, ushort imm, ReadOnlySpan<byte> payload)
    {
        if ((cmd & 0x60) == 0x60)
        {
            ExecuteUnpack(cmd, num, imm, payload);
            return;
        }

        switch (cmd)
        {
            case 0x00: // NOP
                break;

            case 0x01: // STCYCL
                Cycle = imm;
                break;

            case 0x02: // OFFSET (VIF1 only)
                if (IsVif1)
                {
                    Ofst = (ushort)(imm & 0x3FFu);
                    Dbf = false;
                    Stat &= ~0x80u;
                    Tops = Base;
                }
                break;

            case 0x03: // BASE (VIF1 only)
                if (IsVif1)
                {
                    Base = (ushort)(imm & 0x3FFu);
                    Tops = (ushort)((Base + (Dbf ? Ofst : 0)) & 0x3FFu);
                }
                break;

            case 0x04: // ITOP
                Itops = (ushort)(imm & 0x3FFu);
                break;

            case 0x05: // STMOD
                Mode = (uint)(imm & 0x3u);
                break;

            case 0x06: // MSKPATH3 (VIF1 only)
                if (IsVif1)
                {
                    Path3Masked = (imm & 0x8000u) != 0;
                }
                break;

            case 0x07: // MARK
                Mark = imm;
                Stat |= 0x40u;
                break;

            case 0x10: // FLUSHE
            case 0x11: // FLUSH
            case 0x13: // FLUSHA
                OnFlush?.Invoke();
                break;

            case 0x14: // MSCAL
            case 0x15: // MSCALF
                TriggerMicroProgram((ushort)((imm * 8) & MicroMemMask), isContinue: false);
                break;

            case 0x17: // MSCNT
                TriggerMicroProgram(0, isContinue: true);
                break;

            case 0x20: // STMASK
                Mask = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
                break;

            case 0x30: // STROW
                for (int i = 0; i < 4; i++)
                    Row[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i * 4, 4));
                break;

            case 0x31: // STCOL
                for (int i = 0; i < 4; i++)
                    Col[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(i * 4, 4));
                break;

            case 0x4A: // MPG
            {
                int destAddr = (imm * 8) & MicroMemMask;
                for (int i = 0; i < payload.Length; i++)
                {
                    MicroMem[(destAddr + i) & MicroMemMask] = payload[i];
                }
                TotalMpgBytesUploaded += payload.Length;
                OnMicroMemoryModified?.Invoke();
                break;
            }

            case 0x50: // DIRECT
            case 0x51: // DIRECTHL
                if (IsVif1)
                {
                    OnDirectGifData?.Invoke(payload);
                }
                break;
        }
    }

    private void TriggerMicroProgram(ushort startByteAddr, bool isContinue)
    {
        Itop = Itops;
        if (IsVif1)
        {
            Top = Tops;
            Dbf = !Dbf;
            if (Dbf)
                Stat |= 0x80u;
            else
                Stat &= ~0x80u;
            Tops = (ushort)((Base + (Dbf ? Ofst : 0)) & 0x3FFu);
        }

        OnMicroProgramCall?.Invoke(startByteAddr, isContinue);
    }

    private void ExecuteUnpack(byte cmd, byte num, ushort imm, ReadOnlySpan<byte> payload)
    {
        bool useMask = (cmd & 0x10) != 0;
        int vn = (cmd >> 2) & 0x3; // 0=S, 1=V2, 2=V3, 3=V4
        int vl = cmd & 0x3;        // 0=32, 1=16, 2=8, 3=5 (V4-5)

        bool flg = (imm & 0x8000u) != 0;
        bool usn = (imm & 0x4000u) != 0;
        int dstQwordAddr = imm & 0x3FF;
        if (flg && IsVif1)
        {
            dstQwordAddr = (dstQwordAddr + Tops) & DataMemQwordMask;
        }
        else
        {
            dstQwordAddr &= DataMemQwordMask;
        }

        int dstVectors = num == 0 ? 256 : num;
        int cl = Math.Max(1, (int)Cl);
        int wl = Math.Max(1, (int)Wl);

        int srcBitOffset = 0;
        int cycleStep = 0;
        int writtenVectors = 0;

        Span<uint> rawVec = stackalloc uint[4];

        while (writtenVectors < dstVectors)
        {
            bool isDataWrite = cl >= wl || cycleStep < cl;

            rawVec.Clear();
            if (isDataWrite)
            {
                ReadSourceVector(payload, ref srcBitOffset, vn, vl, usn, rawVec);
            }

            WriteUnpackedVectorToVuMem(dstQwordAddr, cycleStep, rawVec, vn, vl, useMask, !isDataWrite);

            dstQwordAddr = (dstQwordAddr + 1) & DataMemQwordMask;
            writtenVectors++;
            TotalUnpackQuadwordsWritten++;
            cycleStep++;

            if (cl >= wl)
            {
                // Skipping Write (CL >= WL): after WL writes, skip (CL - WL) quadwords
                if (cycleStep >= wl)
                {
                    dstQwordAddr = (dstQwordAddr + (cl - wl)) & DataMemQwordMask;
                    cycleStep = 0;
                }
            }
            else
            {
                // Filling Write (CL < WL): cycle resets after WL writes
                if (cycleStep >= wl)
                {
                    cycleStep = 0;
                }
            }
        }
    }

    private static void ReadSourceVector(
        ReadOnlySpan<byte> payload,
        ref int bitOffset,
        int vn,
        int vl,
        bool usn,
        Span<uint> outVec)
    {
        int byteIdx = bitOffset >> 3;

        if (vn == 3 && vl == 3)
        {
            // V4-5 (16-bit RGB5A1 packed color)
            ushort v = byteIdx + 2 <= payload.Length
                ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(byteIdx, 2))
                : (ushort)0;
            outVec[0] = (uint)((v & 0x001F) << 3);
            outVec[1] = (uint)(((v >> 5) & 0x001F) << 3);
            outVec[2] = (uint)(((v >> 10) & 0x001F) << 3);
            outVec[3] = (v & 0x8000) != 0 ? 0x80u : 0x00u;
            bitOffset += 16;
            return;
        }

        int compCount = vn + 1;
        for (int c = 0; c < compCount; c++)
        {
            int curByte = bitOffset >> 3;
            uint val = 0;
            switch (vl)
            {
                case 0: // 32-bit
                    if (curByte + 4 <= payload.Length)
                        val = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(curByte, 4));
                    bitOffset += 32;
                    break;
                case 1: // 16-bit
                    if (curByte + 2 <= payload.Length)
                    {
                        ushort u16 = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(curByte, 2));
                        val = usn ? u16 : unchecked((uint)(int)(short)u16);
                    }
                    bitOffset += 16;
                    break;
                case 2: // 8-bit
                    if (curByte < payload.Length)
                    {
                        byte u8 = payload[curByte];
                        val = usn ? u8 : unchecked((uint)(int)(sbyte)u8);
                    }
                    bitOffset += 8;
                    break;
            }
            outVec[c] = val;
        }

        // For S-* (vn == 0), broadcast X to Y, Z, W
        if (vn == 0)
        {
            outVec[1] = outVec[0];
            outVec[2] = outVec[0];
            outVec[3] = outVec[0];
        }
    }

    private void WriteUnpackedVectorToVuMem(
        int dstQwordAddr,
        int cycleStep,
        ReadOnlySpan<uint> rawVec,
        int vn,
        int vl,
        bool useMask,
        bool isFillingCycle)
    {
        int byteAddr = (dstQwordAddr << 4) & DataMemMask;
        int clampedCycle = Math.Min(cycleStep, 3);
        int compCount = vn + 1;

        for (int comp = 0; comp < 4; comp++)
        {
            uint maskBits = 0;
            if (useMask)
            {
                int shift = (clampedCycle * 4 + comp) * 2;
                maskBits = (Mask >> shift) & 0x3u;
            }
            else if (isFillingCycle)
            {
                // Filling cycle without mask defaults to Row register
                maskBits = 1;
            }

            if (maskBits == 3 || (!useMask && !isFillingCycle && comp >= compCount))
            {
                // Write protect or unfilled component without mask: preserve existing lane in VU DataMem untouched
                continue;
            }

            uint finalVal;
            if (maskBits == 1)
            {
                finalVal = Row[comp];
            }
            else if (maskBits == 2)
            {
                finalVal = Col[clampedCycle];
            }
            else
            {
                uint v = rawVec[comp];
                // Apply STMOD addition mode (not applied to V4-5)
                if (!(vn == 3 && vl == 3))
                {
                    switch (Mode & 0x3u)
                    {
                        case 0: // Normal
                            break;
                        case 1: // Offset mode: Data + R
                            v = unchecked(v + Row[comp]);
                            break;
                        case 2: // Difference mode: Data + R, and R = Data + R
                            v = unchecked(v + Row[comp]);
                            Row[comp] = v;
                            break;
                        case 3: // Load R mode: R = Data
                            Row[comp] = v;
                            break;
                    }
                }
                finalVal = v;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(DataMem.AsSpan(byteAddr + comp * 4, 4), finalVal);
        }
    }
}
