using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    public Action<int, int>? OnMicroMemoryRangeModified { get; set; }

    // Flat staging buffer for split VIF commands (e.g. TTE=1 4-byte UNPACK headers across REF tags)
    private byte[] _staging = new byte[4096];
    private int _stagingLen;

    [InlineArray(4)]
    private struct CycleMaskArray
    {
        private CycleMaskInfo _element0;
    }

    private struct CycleMaskInfo
    {
        public Vector128<uint> DataMask;
        public Vector128<uint> RowMask;
        public Vector128<uint> ColMask;
        public Vector128<uint> WpMask;
        public Vector128<uint> ColVec;
        public Vector128<uint> StaticRowCol;
        public bool HasWp;
    }

    // Diagnostics
    public bool EnableProfiling { get; set; }
    public long TotalCommandsProcessed { get; private set; }
    public long TotalUnpackQuadwordsWritten { get; private set; }
    public long TotalMpgBytesUploaded { get; private set; }
    public long TotalProcessingTicks { get; set; }
    public long TotalTransfers { get; set; }
    public long TotalBytesReceived { get; private set; }
    public long TotalUnpackFastCalls { get; private set; }
    public long TotalUnpackSlowCalls { get; private set; }
    public long TotalStagedFastCalls { get; private set; }
    public long TotalStagedSlowCalls { get; private set; }
    public long TotalStagedBytesCopied { get; private set; }
    public long TotalUnknownCmdWords { get; private set; }
    public long TotalMpgCalls { get; private set; }
    public long TotalUnpackTicks { get; private set; }
    public long TotalMicroCallbackTicks { get; private set; }
    public long TotalDirectGifTicks { get; private set; }

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
        _stagingLen = 0;
    }

    private void EnsureStagingCapacity(int needed)
    {
        if (needed > _staging.Length)
        {
            Array.Resize(ref _staging, Math.Max(_staging.Length * 2, needed));
        }
    }

    /// <summary>
    /// Feeds raw VIF stream bytes from DMA Channel 0/1 or VIF FIFO MMIO.
    /// Fast-paths direct zero-copy parsing when staging is empty, and stitches split
    /// 4-byte TTE command headers directly against incoming payload spans without copying payloads.
    /// </summary>
    public void ProcessData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        TotalBytesReceived += data.Length;
        long t0 = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
        try
        {
            int dataOffset = 0;
            if (_stagingLen > 0)
            {
                if (!ProcessStagedPrefix(data, ref dataOffset))
                    return;
            }

            if (dataOffset < data.Length)
            {
                ReadOnlySpan<byte> remaining = data.Slice(dataOffset);
                int offset = 0;
                int totalLen = remaining.Length & ~3;

                while (offset + 4 <= totalLen)
                {
                    uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(remaining.Slice(offset, 4));
                    if (vifCode == 0u)
                    {
                        Code = 0u;
                        Num = 0;
                        TotalCommandsProcessed++;
                        offset += 4;
                        continue;
                    }
                    byte rawCmd = (byte)(vifCode >> 24);
                    byte cmd = (byte)(rawCmd & 0x7Fu); // Strip interrupt bit (bit 31)
                    byte num = (byte)((vifCode >> 16) & 0xFFu);
                    ushort imm = (ushort)(vifCode & 0xFFFFu);

                    int payloadBytesNeeded = GetRequiredPayloadBytes(cmd, num, imm);
                    if (payloadBytesNeeded < 0)
                    {
                        TotalUnknownCmdWords++;
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

                    ReadOnlySpan<byte> payload = remaining.Slice(offset + 4, payloadBytesNeeded);
                    ExecuteCommand(cmd, num, imm, payload);

                    offset += 4 + payloadBytesNeeded;
                }

                if (offset < remaining.Length)
                {
                    ReadOnlySpan<byte> tail = remaining.Slice(offset);
                    EnsureStagingCapacity(tail.Length);
                    tail.CopyTo(_staging.AsSpan(0, tail.Length));
                    _stagingLen = tail.Length;
                    TotalStagedBytesCopied += tail.Length;
                }
            }
        }
        finally
        {
            if (t0 != 0)
                TotalProcessingTicks += Stopwatch.GetTimestamp() - t0;
            TotalTransfers++;
        }
    }

    private bool ProcessStagedPrefix(ReadOnlySpan<byte> data, ref int dataOffset)
    {
        while (_stagingLen > 0)
        {
            if (_stagingLen < 4)
            {
                int needHeader = 4 - _stagingLen;
                int avail = data.Length - dataOffset;
                if (avail < needHeader)
                {
                    if (avail > 0)
                    {
                        EnsureStagingCapacity(_stagingLen + avail);
                        data.Slice(dataOffset, avail).CopyTo(_staging.AsSpan(_stagingLen));
                        _stagingLen += avail;
                        TotalStagedBytesCopied += avail;
                        dataOffset += avail;
                    }
                    return false;
                }

                EnsureStagingCapacity(4);
                data.Slice(dataOffset, needHeader).CopyTo(_staging.AsSpan(_stagingLen));
                TotalStagedBytesCopied += needHeader;
                _stagingLen = 4;
                dataOffset += needHeader;
            }

            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(_staging.AsSpan(0, 4));
            byte rawCmd = (byte)(vifCode >> 24);
            byte cmd = (byte)(rawCmd & 0x7Fu); // Strip interrupt bit (bit 31)
            byte num = (byte)((vifCode >> 16) & 0xFFu);
            ushort imm = (ushort)(vifCode & 0xFFFFu);

            int payloadBytesNeeded = GetRequiredPayloadBytes(cmd, num, imm);
            if (payloadBytesNeeded < 0)
            {
                TotalUnknownCmdWords++;
                // Unknown/reserved VIF command: discard 4-byte word and continue
                _stagingLen -= 4;
                if (_stagingLen > 0)
                {
                    Buffer.BlockCopy(_staging, 4, _staging, 0, _stagingLen);
                }
                continue;
            }

            // Zero-copy fast path when only the 4-byte VIFcode header was staged (e.g. TTE=1 REF tag)
            if (_stagingLen == 4 && data.Length - dataOffset >= payloadBytesNeeded)
            {
                TotalStagedFastCalls++;
                Code = vifCode;
                Num = num;
                TotalCommandsProcessed++;

                ReadOnlySpan<byte> payload = data.Slice(dataOffset, payloadBytesNeeded);
                ExecuteCommand(cmd, num, imm, payload);

                dataOffset += payloadBytesNeeded;
                _stagingLen = 0;
                return true;
            }

            int totalCmdBytes = 4 + payloadBytesNeeded;
            int stillNeeded = totalCmdBytes - _stagingLen;
            int remAvail = data.Length - dataOffset;

            if (remAvail < stillNeeded)
            {
                if (remAvail > 0)
                {
                    EnsureStagingCapacity(_stagingLen + remAvail);
                    data.Slice(dataOffset, remAvail).CopyTo(_staging.AsSpan(_stagingLen));
                    _stagingLen += remAvail;
                    TotalStagedBytesCopied += remAvail;
                    dataOffset += remAvail;
                }
                return false;
            }

            if (stillNeeded > 0)
            {
                EnsureStagingCapacity(totalCmdBytes);
                data.Slice(dataOffset, stillNeeded).CopyTo(_staging.AsSpan(_stagingLen));
                TotalStagedBytesCopied += stillNeeded;
                dataOffset += stillNeeded;
            }

            TotalStagedSlowCalls++;
            Code = vifCode;
            Num = num;
            TotalCommandsProcessed++;

            ExecuteCommand(cmd, num, imm, _staging.AsSpan(4, payloadBytesNeeded));
            _stagingLen = 0;
            return true;
        }

        return true;
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
                TotalMpgCalls++;
                int destAddr = (imm * 8) & MicroMemMask;
                int len = payload.Length;
                if (len > 0)
                {
                    TotalMpgBytesUploaded += len;
                    if (destAddr + len <= MicroMem.Length && payload.SequenceEqual(MicroMem.AsSpan(destAddr, len)))
                    {
                        break;
                    }
                    int first = Math.Min(len, MicroMem.Length - destAddr);
                    payload.Slice(0, first).CopyTo(MicroMem.AsSpan(destAddr, first));
                    int remain = len - first;
                    int pos = 0;
                    while (remain > 0)
                    {
                        int chunk = Math.Min(remain, MicroMem.Length);
                        payload.Slice(len - remain, chunk).CopyTo(MicroMem.AsSpan(pos, chunk));
                        remain -= chunk;
                    }
                    OnMicroMemoryRangeModified?.Invoke(destAddr, len);
                }
                OnMicroMemoryModified?.Invoke();
                break;
            }

            case 0x50: // DIRECT
            case 0x51: // DIRECTHL
                if (IsVif1 && OnDirectGifData != null)
                {
                    long td = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
                    OnDirectGifData(payload);
                    if (td != 0)
                        TotalDirectGifTicks += Stopwatch.GetTimestamp() - td;
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

        if (OnMicroProgramCall != null)
        {
            long tm = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
            OnMicroProgramCall(startByteAddr, isContinue);
            if (tm != 0)
                TotalMicroCallbackTicks += Stopwatch.GetTimestamp() - tm;
        }
    }

    private void ExecuteUnpack(byte cmd, byte num, ushort imm, ReadOnlySpan<byte> payload)
    {
        long tu = EnableProfiling ? Stopwatch.GetTimestamp() : 0;
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

        if (!(vl == 3 && vn != 3))
        {
            int bytesPerVec = vl == 3 ? 2 : ((vn + 1) << (2 - vl));
            int srcVecsNeeded = cl >= wl
                ? dstVectors
                : (dstVectors / wl) * cl + Math.Min(dstVectors % wl, cl);

            if (srcVecsNeeded * bytesPerVec <= payload.Length)
            {
                TotalUnpackFastCalls++;
                uint effectiveMode = (vn == 3 && vl == 3) ? 0u : (Mode & 0x3u);
                ExecuteUnpackFast(payload, dstQwordAddr, dstVectors, cl, wl, vn, vl, usn, useMask, effectiveMode);
                if (tu != 0)
                    TotalUnpackTicks += Stopwatch.GetTimestamp() - tu;
                return;
            }
        }

        TotalUnpackSlowCalls++;

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
        if (tu != 0)
            TotalUnpackTicks += Stopwatch.GetTimestamp() - tu;
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
        else if (vn == 1) // For V2-* (vn == 1), mirror v1 v0 v1 v0 (PS2 hardware / PCSX2 behavior)
        {
            outVec[2] = outVec[0];
            outVec[3] = outVec[1];
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

            if (maskBits == 3)
            {
                // Write protect: preserve existing lane in VU DataMem untouched
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

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DataMem), byteAddr + comp * 4), finalVal);
        }
    }

    private void ExecuteUnpackFast(
        ReadOnlySpan<byte> payload,
        int dstQwordAddr,
        int dstVectors,
        int cl,
        int wl,
        int vn,
        int vl,
        bool usn,
        bool useMask,
        uint effectiveMode)
    {
        ref byte srcBase = ref MemoryMarshal.GetReference(payload);
        ref byte dmBase = ref MemoryMarshal.GetArrayDataReference(DataMem);
        int qwordMask = DataMemQwordMask;
        int formatKey = (vn << 2) | vl;
        int srcOffset = 0;
        int curDst = dstQwordAddr;
        int cycleStep = 0;
        bool isSkipping = cl >= wl;
        int stepSkip = cl - wl;

        // Ultra-fast direct unmasked normal-mode path without DataMem wraparound
        if (!useMask && effectiveMode == 0 && isSkipping && dstVectors > 0 && (cl == wl || wl == 1))
        {
            int dstStrideQw = cl == wl ? 1 : cl;
            int lastQw = curDst + (dstVectors - 1) * dstStrideQw;
            if ((uint)lastQw <= (uint)qwordMask)
            {
                int dstStrideBytes = dstStrideQw << 4;
                ref byte d = ref Unsafe.Add(ref dmBase, curDst << 4);
                ref byte s = ref srcBase;
                switch (formatKey)
                {
                    case 0xC: // V4-32
                        if (dstStrideQw == 1)
                        {
                            Unsafe.CopyBlockUnaligned(ref d, ref s, (uint)(dstVectors << 4));
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                Unsafe.WriteUnaligned(ref d, Unsafe.ReadUnaligned<Vector128<uint>>(ref s));
                                s = ref Unsafe.Add(ref s, 16);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0x8: // V3-32
                        for (int i = 0; i < dstVectors; i++)
                        {
                            Unsafe.WriteUnaligned(ref d, Vector128.Create(
                                Unsafe.ReadUnaligned<uint>(ref s),
                                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, 4)),
                                Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref s, 8)),
                                0u));
                            s = ref Unsafe.Add(ref s, 12);
                            d = ref Unsafe.Add(ref d, dstStrideBytes);
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0xD: // V4-16
                        if (usn)
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                ulong raw64 = Unsafe.ReadUnaligned<ulong>(ref s);
                                Unsafe.WriteUnaligned(ref d, Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw64).AsUInt16()));
                                s = ref Unsafe.Add(ref s, 8);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                ulong raw64 = Unsafe.ReadUnaligned<ulong>(ref s);
                                Unsafe.WriteUnaligned(ref d, Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw64).AsInt16()).AsUInt32());
                                s = ref Unsafe.Add(ref s, 8);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0x9: // V3-16
                        if (usn)
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(
                                    (uint)Unsafe.ReadUnaligned<ushort>(ref s),
                                    (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, 2)),
                                    (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, 4)),
                                    0u));
                                s = ref Unsafe.Add(ref s, 6);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(
                                    unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref s)),
                                    unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref s, 2))),
                                    unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref s, 4))),
                                    0u));
                                s = ref Unsafe.Add(ref s, 6);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0x5: // V2-16
                        if (usn)
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                uint x = Unsafe.ReadUnaligned<ushort>(ref s);
                                uint y = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, 2));
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(x, y, x, y));
                                s = ref Unsafe.Add(ref s, 4);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                uint x = unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref s));
                                uint y = unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref s, 2)));
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(x, y, x, y));
                                s = ref Unsafe.Add(ref s, 4);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0xE: // V4-8
                        if (usn)
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                uint raw32 = Unsafe.ReadUnaligned<uint>(ref s);
                                Unsafe.WriteUnaligned(ref d, Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw32).AsByte())));
                                s = ref Unsafe.Add(ref s, 4);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                uint raw32 = Unsafe.ReadUnaligned<uint>(ref s);
                                Unsafe.WriteUnaligned(ref d, Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw32).AsSByte())).AsUInt32());
                                s = ref Unsafe.Add(ref s, 4);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                    case 0xA: // V3-8
                        if (usn)
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(
                                    (uint)s,
                                    (uint)Unsafe.Add(ref s, 1),
                                    (uint)Unsafe.Add(ref s, 2),
                                    0u));
                                s = ref Unsafe.Add(ref s, 3);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < dstVectors; i++)
                            {
                                Unsafe.WriteUnaligned(ref d, Vector128.Create(
                                    unchecked((uint)(int)(sbyte)s),
                                    unchecked((uint)(int)(sbyte)Unsafe.Add(ref s, 1)),
                                    unchecked((uint)(int)(sbyte)Unsafe.Add(ref s, 2)),
                                    0u));
                                s = ref Unsafe.Add(ref s, 3);
                                d = ref Unsafe.Add(ref d, dstStrideBytes);
                            }
                        }
                        TotalUnpackQuadwordsWritten += dstVectors;
                        return;
                }
            }
        }

        bool needRow = !isSkipping || useMask || effectiveMode != 0;
        Vector128<uint> rowVec = needRow
            ? Unsafe.ReadUnaligned<Vector128<uint>>(ref Unsafe.As<uint, byte>(ref MemoryMarshal.GetArrayDataReference(Row)))
            : default;

        Unsafe.SkipInit(out CycleMaskArray cycleMasks);
        if (useMask)
        {
            uint maskReg = Mask;
            int cycleCount = Math.Min(wl, 4);
            for (int c = 0; c < cycleCount; c++)
            {
                uint cycleByte = (maskReg >> (c * 8)) & 0xFFu;
                uint m0 = cycleByte & 0x3u;
                uint m1 = (cycleByte >> 2) & 0x3u;
                uint m2 = (cycleByte >> 4) & 0x3u;
                uint m3 = (cycleByte >> 6) & 0x3u;

                Vector128<uint> dataM = Vector128.Create(
                    m0 == 0 ? 0xFFFFFFFFu : 0u,
                    m1 == 0 ? 0xFFFFFFFFu : 0u,
                    m2 == 0 ? 0xFFFFFFFFu : 0u,
                    m3 == 0 ? 0xFFFFFFFFu : 0u);
                Vector128<uint> rowM = Vector128.Create(
                    m0 == 1 ? 0xFFFFFFFFu : 0u,
                    m1 == 1 ? 0xFFFFFFFFu : 0u,
                    m2 == 1 ? 0xFFFFFFFFu : 0u,
                    m3 == 1 ? 0xFFFFFFFFu : 0u);
                Vector128<uint> colM = Vector128.Create(
                    m0 == 2 ? 0xFFFFFFFFu : 0u,
                    m1 == 2 ? 0xFFFFFFFFu : 0u,
                    m2 == 2 ? 0xFFFFFFFFu : 0u,
                    m3 == 2 ? 0xFFFFFFFFu : 0u);
                Vector128<uint> wpM = Vector128.Create(
                    m0 == 3 ? 0xFFFFFFFFu : 0u,
                    m1 == 3 ? 0xFFFFFFFFu : 0u,
                    m2 == 3 ? 0xFFFFFFFFu : 0u,
                    m3 == 3 ? 0xFFFFFFFFu : 0u);
                Vector128<uint> colV = Vector128.Create(Col[c]);

                cycleMasks[c] = new CycleMaskInfo
                {
                    DataMask = dataM,
                    RowMask = rowM,
                    ColMask = colM,
                    WpMask = wpM,
                    ColVec = colV,
                    StaticRowCol = (rowVec & rowM) | (colV & colM),
                    HasWp = (m0 == 3) | (m1 == 3) | (m2 == 3) | (m3 == 3),
                };
            }
        }

        for (int i = 0; i < dstVectors; i++)
        {
            bool isDataCycle = isSkipping || cycleStep < cl;
            Vector128<uint> vec;
            if (isDataCycle)
            {
                ref byte p = ref Unsafe.Add(ref srcBase, srcOffset);
                switch (formatKey)
                {
                    case 0xC: // V4-32
                        vec = Unsafe.ReadUnaligned<Vector128<uint>>(ref p);
                        srcOffset += 16;
                        break;
                    case 0x8: // V3-32
                        vec = Vector128.Create(
                            Unsafe.ReadUnaligned<uint>(ref p),
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 4)),
                            Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 8)),
                            0u);
                        srcOffset += 12;
                        break;
                    case 0x4: // V2-32
                    {
                        uint x = Unsafe.ReadUnaligned<uint>(ref p);
                        uint y = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, 4));
                        vec = Vector128.Create(x, y, x, y);
                        srcOffset += 8;
                        break;
                    }
                    case 0x0: // S-32
                        vec = Vector128.Create(Unsafe.ReadUnaligned<uint>(ref p));
                        srcOffset += 4;
                        break;
                    case 0xD: // V4-16
                    {
                        ulong raw64 = Unsafe.ReadUnaligned<ulong>(ref p);
                        vec = usn
                            ? Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw64).AsUInt16())
                            : Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw64).AsInt16()).AsUInt32();
                        srcOffset += 8;
                        break;
                    }
                    case 0x9: // V3-16
                        if (usn)
                        {
                            vec = Vector128.Create(
                                (uint)Unsafe.ReadUnaligned<ushort>(ref p),
                                (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, 2)),
                                (uint)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, 4)),
                                0u);
                        }
                        else
                        {
                            vec = Vector128.Create(
                                unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref p)),
                                unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref p, 2))),
                                unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref p, 4))),
                                0u);
                        }
                        srcOffset += 6;
                        break;
                    case 0x5: // V2-16
                    {
                        uint x = usn ? Unsafe.ReadUnaligned<ushort>(ref p) : unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref p));
                        uint y = usn ? Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, 2)) : unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref p, 2)));
                        vec = Vector128.Create(x, y, x, y);
                        srcOffset += 4;
                        break;
                    }
                    case 0x1: // S-16
                        vec = Vector128.Create(usn
                            ? (uint)Unsafe.ReadUnaligned<ushort>(ref p)
                            : unchecked((uint)(int)Unsafe.ReadUnaligned<short>(ref p)));
                        srcOffset += 2;
                        break;
                    case 0xE: // V4-8
                    {
                        uint raw32 = Unsafe.ReadUnaligned<uint>(ref p);
                        vec = usn
                            ? Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw32).AsByte()))
                            : Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw32).AsSByte())).AsUInt32();
                        srcOffset += 4;
                        break;
                    }
                    case 0xA: // V3-8
                        if (usn)
                        {
                            vec = Vector128.Create(
                                (uint)p,
                                (uint)Unsafe.Add(ref p, 1),
                                (uint)Unsafe.Add(ref p, 2),
                                0u);
                        }
                        else
                        {
                            vec = Vector128.Create(
                                unchecked((uint)(int)(sbyte)p),
                                unchecked((uint)(int)(sbyte)Unsafe.Add(ref p, 1)),
                                unchecked((uint)(int)(sbyte)Unsafe.Add(ref p, 2)),
                                0u);
                        }
                        srcOffset += 3;
                        break;
                    case 0x6: // V2-8
                    {
                        uint x = usn ? p : unchecked((uint)(int)(sbyte)p);
                        uint y = usn ? Unsafe.Add(ref p, 1) : unchecked((uint)(int)(sbyte)Unsafe.Add(ref p, 1));
                        vec = Vector128.Create(x, y, x, y);
                        srcOffset += 2;
                        break;
                    }
                    case 0x2: // S-8
                        vec = Vector128.Create(usn ? (uint)p : unchecked((uint)(int)(sbyte)p));
                        srcOffset += 1;
                        break;
                    case 0xF: // V4-5
                    {
                        ushort v = Unsafe.ReadUnaligned<ushort>(ref p);
                        vec = Vector128.Create(
                            (uint)((v & 0x001F) << 3),
                            (uint)(((v >> 5) & 0x001F) << 3),
                            (uint)(((v >> 10) & 0x001F) << 3),
                            (v & 0x8000) != 0 ? 0x80u : 0x00u);
                        srcOffset += 2;
                        break;
                    }
                    default:
                        vec = default;
                        break;
                }
            }
            else
            {
                vec = Vector128<uint>.Zero;
            }

            ref byte dstRef = ref Unsafe.Add(ref dmBase, (curDst << 4) & DataMemMask);
            Vector128<uint> outVec;

            if (!useMask)
            {
                if (!isDataCycle)
                {
                    outVec = rowVec;
                }
                else
                {
                    switch (effectiveMode)
                    {
                        case 0:
                            outVec = vec;
                            break;
                        case 1:
                            outVec = vec + rowVec;
                            break;
                        case 2:
                            outVec = vec + rowVec;
                            rowVec = outVec;
                            break;
                        default: // 3: Load R mode (R = Data)
                            outVec = vec;
                            rowVec = vec;
                            break;
                    }
                }
            }
            else
            {
                ref readonly CycleMaskInfo cm = ref cycleMasks[cycleStep < 3 ? cycleStep : 3];
                switch (effectiveMode)
                {
                    case 0:
                        outVec = (vec & cm.DataMask) | cm.StaticRowCol;
                        break;
                    case 1:
                        outVec = ((vec + rowVec) & cm.DataMask) | cm.StaticRowCol;
                        break;
                    case 2:
                    {
                        Vector128<uint> sum = vec + rowVec;
                        rowVec = Vector128.ConditionalSelect(cm.DataMask, sum, rowVec);
                        outVec = (sum & cm.DataMask) | (rowVec & cm.RowMask) | (cm.ColVec & cm.ColMask);
                        break;
                    }
                    default: // 3: Load R mode (R = Data)
                        rowVec = Vector128.ConditionalSelect(cm.DataMask, vec, rowVec);
                        outVec = (vec & cm.DataMask) | (rowVec & cm.RowMask) | (cm.ColVec & cm.ColMask);
                        break;
                }

                if (cm.HasWp)
                {
                    outVec |= Unsafe.ReadUnaligned<Vector128<uint>>(ref dstRef) & cm.WpMask;
                }
            }

            Unsafe.WriteUnaligned(ref dstRef, outVec);

            curDst = (curDst + 1) & qwordMask;
            cycleStep++;
            if (isSkipping)
            {
                if (cycleStep >= wl)
                {
                    curDst = (curDst + stepSkip) & qwordMask;
                    cycleStep = 0;
                }
            }
            else if (cycleStep >= wl)
            {
                cycleStep = 0;
            }
        }

        if (effectiveMode >= 2)
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref MemoryMarshal.GetArrayDataReference(Row)), rowVec);
        }

        TotalUnpackQuadwordsWritten += dstVectors;
    }
}
