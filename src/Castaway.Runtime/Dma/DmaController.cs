using System.Buffers.Binary;
using Castaway.Core;

namespace Castaway.Runtime;

public enum DmaChannelId : int
{
    Vif0    = 0,
    Vif1    = 1,
    Gif     = 2,
    FromIpu = 3,
    ToIpu   = 4,
    Sif0    = 5,
    Sif1    = 6,
    Sif2    = 7,
    FromSpr = 8,
    ToSpr   = 9
}

public enum DmaSourceTagId : int
{
    Refe = 0, // Transfer QWC from ADDR, then end
    Cnt  = 1, // Transfer QWC following tag, next tag at end of data
    Next = 2, // Transfer QWC following tag, next tag at ADDR
    Ref  = 3, // Transfer QWC from ADDR, next tag follows current tag
    Refs = 4, // Transfer QWC from ADDR (stall control), next tag follows current tag
    Call = 5, // Transfer QWC following tag, push next tag to ASR, jump to ADDR
    Ret  = 6, // Transfer QWC following tag, pop next tag from ASR (or end if ASR empty)
    End  = 7  // Transfer QWC following tag, then end
}

public sealed class DmaChannelRegisters
{
    public DmaChannelId ChannelId { get; init; }
    public uint Chcr { get; set; }
    public uint Madr { get; set; }
    public uint Qwc { get; set; }
    public uint Tadr { get; set; }
    public uint Asr0 { get; set; }
    public uint Asr1 { get; set; }
    public uint Sadr { get; set; }
}

public sealed unsafe class DmaController
{
    public const int ChannelCount = 10;
    public const int QuadwordBytes = 16;

    private readonly DmaChannelRegisters[] _channels = new DmaChannelRegisters[ChannelCount];
    private readonly EeKernel _kernel;

    public uint Ctrl { get; set; } = 0x00000001u; // DMAE enabled by default
    public uint Pcr { get; set; }
    public uint Sqwc { get; set; } // bits 0..7 = SQWC (Skip QWC), bits 16..23 = TQWC (Transfer QWC)
    public uint Rbsr { get; set; }
    public uint Rbor { get; set; }
    public uint Stadr { get; set; }
    public uint Enabler { get; set; } = 0x00001201u;

    // Pluggable hardware consumers for Phase 4 (Castaway.GsRenderer) & SIF
    public Action<ReadOnlySpan<byte>>? OnVif0Data { get; set; }
    public delegate int Vif1ReadHandler(Span<byte> dest);
    public Action<ReadOnlySpan<byte>>? OnVif1Data { get; set; }
    public Vif1ReadHandler? OnVif1ReadData { get; set; }
    public Action<ReadOnlySpan<byte>>? OnGifData { get; set; }
    public Action<ReadOnlySpan<byte>>? OnSif1Data { get; set; }

    // Metrics for verification & debugging
    public static bool CaptureDebugPackets { get; set; } = false;
    public long TotalQuadwordsTransferred { get; private set; }
    private readonly List<byte> _lastTransferredPacket = new();
    public IReadOnlyList<byte> LastTransferredPacket => _lastTransferredPacket;

    public DmaController(EeKernel kernel)
    {
        _kernel = kernel;
        for (int i = 0; i < ChannelCount; i++)
        {
            _channels[i] = new DmaChannelRegisters { ChannelId = (DmaChannelId)i };
        }
    }

    public DmaChannelRegisters GetChannel(DmaChannelId id) => _channels[(int)id];
    public DmaChannelRegisters GetChannel(int index) => _channels[Math.Clamp(index, 0, ChannelCount - 1)];

    public static int TryMapBaseToChannelIndex(uint baseAddr) => baseAddr switch
    {
        0x10008000u => 0, // VIF0
        0x10009000u => 1, // VIF1
        0x1000A000u => 2, // GIF
        0x1000B000u => 3, // fromIPU
        0x1000B400u => 4, // toIPU
        0x1000C000u => 5, // SIF0
        0x1000C400u => 6, // SIF1
        0x1000C800u => 7, // SIF2
        0x1000D000u => 8, // fromSPR
        0x1000D400u => 9, // toSPR
        _ => -1
    };

    public uint ReadChannelRegister(int channelIdx, uint regOffset)
    {
        var ch = _channels[channelIdx];
        return regOffset switch
        {
            0x00 => ch.Chcr,
            0x10 => ch.Madr,
            0x20 => ch.Qwc,
            0x30 => ch.Tadr,
            0x40 => ch.Asr0,
            0x50 => ch.Asr1,
            0x80 => ch.Sadr,
            _ => 0u
        };
    }

    public void WriteChannelRegister(CpuContext* ctx, byte* ram, int channelIdx, uint regOffset, uint value)
    {
        var ch = _channels[channelIdx];
        switch (regOffset)
        {
            case 0x00: // Dn_CHCR
                ch.Chcr = value;
                if ((value & 0x100u) != 0) // STR (bit 8) = 1 -> Start DMA transfer
                {
                    ExecuteChannelTransfer(ctx, ram, ch);
                }
                break;
            case 0x10:
                ch.Madr = value;
                break;
            case 0x20:
                ch.Qwc = value & 0xFFFFu;
                break;
            case 0x30:
                ch.Tadr = value;
                break;
            case 0x40:
                ch.Asr0 = value;
                break;
            case 0x50:
                ch.Asr1 = value;
                break;
            case 0x80:
                ch.Sadr = value & 0x3FF0u;
                break;
        }
    }

    public void ExecuteChannelTransfer(CpuContext* ctx, byte* ram, DmaChannelRegisters ch)
    {
        _lastTransferredPacket.Clear();

        uint mode = (ch.Chcr >> 2) & 0x3u;
        switch (mode)
        {
            case 0: // Normal Mode
                ExecuteNormalTransfer(ctx, ram, ch);
                break;
            case 1: // Chain Mode (Source Chain / Dest Chain)
                ExecuteChainTransfer(ctx, ram, ch);
                break;
            case 2: // Interleave Mode
            case 3:
                ExecuteInterleaveTransfer(ctx, ram, ch);
                break;
        }

        // Clear STR bit (bit 8) when transfer completes synchronously
        ch.Chcr &= ~0x100u;

        // Fire DMAC interrupt bit for this channel
        _kernel.FireDmacInterrupt(ctx, ram, (int)ch.ChannelId);
    }

    private void ExecuteNormalTransfer(CpuContext* ctx, byte* ram, DmaChannelRegisters ch)
    {
        uint qwc = ch.Qwc & 0xFFFFu;
        if (qwc == 0)
            return;

        if (ch.ChannelId == DmaChannelId.FromSpr)
        {
            TransferScratchpadToMainRam(ctx, ram, ch.Sadr, ch.Madr, qwc);
            ch.Sadr = (ch.Sadr + qwc * QuadwordBytes) & 0x3FF0u;
            ch.Madr += qwc * QuadwordBytes;
            ch.Qwc = 0;
            TotalQuadwordsTransferred += qwc;
            return;
        }

        if (ch.ChannelId == DmaChannelId.ToSpr)
        {
            TransferMainRamToScratchpad(ctx, ram, ch.Madr, ch.Sadr, qwc);
            ch.Madr += qwc * QuadwordBytes;
            ch.Sadr = (ch.Sadr + qwc * QuadwordBytes) & 0x3FF0u;
            ch.Qwc = 0;
            TotalQuadwordsTransferred += qwc;
            return;
        }

        if (ch.ChannelId == DmaChannelId.Vif1 && (ch.Chcr & 1u) == 0 && OnVif1ReadData != null)
        {
            uint byteCount = qwc * QuadwordBytes;
            uint addr = ch.Madr & Ps2Memory.RamMask;
            if (addr + byteCount <= Ps2Memory.RamMask + 1u)
            {
                OnVif1ReadData(new Span<byte>(ram + addr, (int)byteCount));
                Console.WriteLine($"[DMA VIF1 READ] MADR=0x{ch.Madr:X8} QWC={qwc} bytes={byteCount}");
            }
            ch.Madr += byteCount;
            ch.Qwc = 0;
            TotalQuadwordsTransferred += qwc;
            return;
        }

        ch.Madr = TransferBlockToPeripheral(ctx, ram, ch.ChannelId, ch.Madr, qwc);
        ch.Qwc = 0;
    }

    private void ExecuteInterleaveTransfer(CpuContext* ctx, byte* ram, DmaChannelRegisters ch)
    {
        uint totalTransferQwc = ch.Qwc & 0xFFFFu;
        if (totalTransferQwc == 0)
            return;

        uint sqwc = Sqwc & 0xFFu;         // Skip QWC
        uint tqwc = (Sqwc >> 16) & 0xFFu; // Transfer QWC per slice
        if (tqwc == 0)
            tqwc = totalTransferQwc;

        uint remaining = totalTransferQwc;
        uint curMadr = ch.Madr;
        uint curSadr = ch.Sadr;

        while (remaining > 0)
        {
            uint sliceQwc = Math.Min(remaining, tqwc);

            if (ch.ChannelId == DmaChannelId.FromSpr)
            {
                TransferScratchpadToMainRam(ctx, ram, curSadr, curMadr, sliceQwc);
                curSadr = (curSadr + sliceQwc * QuadwordBytes) & 0x3FF0u;
                curMadr += (sliceQwc + sqwc) * QuadwordBytes;
                TotalQuadwordsTransferred += sliceQwc;
            }
            else if (ch.ChannelId == DmaChannelId.ToSpr)
            {
                TransferMainRamToScratchpad(ctx, ram, curMadr, curSadr, sliceQwc);
                curMadr += (sliceQwc + sqwc) * QuadwordBytes;
                curSadr = (curSadr + sliceQwc * QuadwordBytes) & 0x3FF0u;
                TotalQuadwordsTransferred += sliceQwc;
            }
            else
            {
                curMadr = TransferBlockToPeripheral(ctx, ram, ch.ChannelId, curMadr, sliceQwc);
                curMadr += sqwc * QuadwordBytes;
            }

            remaining -= sliceQwc;
        }

        ch.Madr = curMadr;
        ch.Sadr = curSadr;
        ch.Qwc = 0;
    }

    private void ExecuteChainTransfer(CpuContext* ctx, byte* ram, DmaChannelRegisters ch)
    {
        bool tte = (ch.Chcr & 0x40u) != 0;  // Bit 6: Tag Transfer Enable (transfers upper 64 bits of 128-bit DMAtag)
        bool tie = (ch.Chcr & 0x80u) != 0;  // Bit 7: Tag Interrupt Enable
        int asp = (int)((ch.Chcr >> 4) & 0x3u); // Bits 4..5: Address Stack Pointer (0, 1, or 2)

        int safetyCounter = 0;
        const int maxTags = 65536;

        // If QWC > 0 on entry, transfer initial payload first
        if ((ch.Qwc & 0xFFFFu) > 0)
        {
            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, ch.Qwc & 0xFFFFu);
            ch.Qwc = 0;
        }

        bool endChain = false;
        while (!endChain && safetyCounter++ < maxTags)
        {
            ReadOnlySpan<byte> tagSpan = GetDmaPointerSpan(ctx, ram, ch.Tadr, QuadwordBytes);
            ulong tagLo = BinaryPrimitives.ReadUInt64LittleEndian(tagSpan[0..8]);

            uint qwc = (uint)(tagLo & 0xFFFFul);
            var tagId = (DmaSourceTagId)((tagLo >> 28) & 0x7ul);
            bool irq = ((tagLo >> 31) & 0x1ul) != 0;
            uint addr = (uint)((tagLo >> 32) & 0xFFFFFFFFul);

            // Update CHCR upper 16 bits with tag bits 16..31
            ch.Chcr = (ch.Chcr & 0x0000FFFFu) | (uint)(tagLo & 0xFFFF0000ul);

            // If TTE is set on VIF0/VIF1, forward the upper 8 bytes of the 128-bit tag.
            // For inline-payload tags (CNT/NEXT/CALL/RET/END) with qwc > 0, coalesce tagSpan[8..16]
            // and the immediately following payload into a single contiguous span so VIF skips staging.
            bool isInlinePayloadTag = tagId is DmaSourceTagId.Cnt
                or DmaSourceTagId.Next
                or DmaSourceTagId.Call
                or DmaSourceTagId.Ret
                or DmaSourceTagId.End;

            bool coalescedTtePayload = false;
            if (tte && (ch.ChannelId == DmaChannelId.Vif0 || ch.ChannelId == DmaChannelId.Vif1))
            {
                if (isInlinePayloadTag && qwc > 0)
                {
                    int payloadBytes = checked((int)(qwc * QuadwordBytes));
                    ReadOnlySpan<byte> combined = GetDmaPointerSpan(ctx, ram, ch.Tadr, QuadwordBytes + payloadBytes);
                    if (combined.Length == QuadwordBytes + payloadBytes)
                    {
                        uint payloadAddr = ch.Tadr + QuadwordBytes;
                        RuntimeDispatcher.CurrentDmaPc = ctx != null ? ctx->PC : 0;
                        RuntimeDispatcher.CurrentDmaRa = ctx != null ? ctx->GetU32(31) : 0;
                        RuntimeDispatcher.CurrentDmaAddr = payloadAddr;

                        DispatchToPeripheral(ch.ChannelId, combined.Slice(8));
                        TotalQuadwordsTransferred += qwc;
                        ch.Madr = payloadAddr + (uint)payloadBytes;
                        coalescedTtePayload = true;
                    }
                    else
                    {
                        DispatchToPeripheral(ch.ChannelId, tagSpan[8..16]);
                    }
                }
                else
                {
                    DispatchToPeripheral(ch.ChannelId, tagSpan[8..16]);
                }
            }

            switch (tagId)
            {
                case DmaSourceTagId.Refe: // 0: Transfer QWC from ADDR, then end
                    ch.Madr = addr;
                    ch.Tadr += QuadwordBytes;
                    if (qwc > 0)
                        ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    endChain = true;
                    break;

                case DmaSourceTagId.Cnt: // 1: Transfer QWC following tag, next tag at end of data
                    if (!coalescedTtePayload)
                    {
                        ch.Madr = ch.Tadr + QuadwordBytes;
                        if (qwc > 0)
                            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    }
                    ch.Tadr = ch.Madr;
                    break;

                case DmaSourceTagId.Next: // 2: Transfer QWC following tag, next tag = ADDR
                    if (!coalescedTtePayload)
                    {
                        ch.Madr = ch.Tadr + QuadwordBytes;
                        if (qwc > 0)
                            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    }
                    ch.Tadr = addr;
                    break;

                case DmaSourceTagId.Ref:  // 3: Transfer QWC from ADDR, next tag = TADR + 16
                case DmaSourceTagId.Refs: // 4: Same as REF with stall control
                    ch.Madr = addr;
                    ch.Tadr += QuadwordBytes;
                    if (qwc > 0)
                        ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    break;

                case DmaSourceTagId.Call: // 5: Transfer QWC following tag, push next addr to ASR0/ASR1, TADR = ADDR
                {
                    if (!coalescedTtePayload)
                    {
                        ch.Madr = ch.Tadr + QuadwordBytes;
                        if (qwc > 0)
                            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    }

                    uint returnTadr = ch.Madr;
                    if (asp == 0)
                    {
                        ch.Asr0 = returnTadr;
                        asp = 1;
                    }
                    else if (asp == 1)
                    {
                        ch.Asr1 = returnTadr;
                        asp = 2;
                    }
                    ch.Tadr = addr;
                    break;
                }

                case DmaSourceTagId.Ret: // 6: Transfer QWC following tag, pop TADR from ASR1/ASR0 (or end if stack empty)
                {
                    if (!coalescedTtePayload)
                    {
                        ch.Madr = ch.Tadr + QuadwordBytes;
                        if (qwc > 0)
                            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    }

                    if (asp == 2)
                    {
                        ch.Tadr = ch.Asr1;
                        asp = 1;
                    }
                    else if (asp == 1)
                    {
                        ch.Tadr = ch.Asr0;
                        asp = 0;
                    }
                    else
                    {
                        ch.Tadr = ch.Madr;
                        endChain = true;
                    }
                    break;
                }

                case DmaSourceTagId.End: // 7: Transfer QWC following tag, then end
                    if (!coalescedTtePayload)
                    {
                        ch.Madr = ch.Tadr + QuadwordBytes;
                        if (qwc > 0)
                            ch.Madr = TransferPayloadForChannel(ctx, ram, ch, ch.Madr, qwc);
                    }
                    ch.Tadr = ch.Madr;
                    endChain = true;
                    break;
            }

            ch.Qwc = 0;

            if (tie && irq)
            {
                endChain = true;
            }
        }

        // Write back updated ASP bits (bits 4..5)
        ch.Chcr = (ch.Chcr & ~0x30u) | ((uint)(asp & 0x3) << 4);
    }

    private uint TransferPayloadForChannel(CpuContext* ctx, byte* ram, DmaChannelRegisters ch, uint addr, uint qwc)
    {
        if (ch.ChannelId == DmaChannelId.FromSpr)
        {
            TransferScratchpadToMainRam(ctx, ram, ch.Sadr, addr, qwc);
            ch.Sadr = (ch.Sadr + qwc * QuadwordBytes) & 0x3FF0u;
            TotalQuadwordsTransferred += qwc;
            return addr + qwc * QuadwordBytes;
        }

        if (ch.ChannelId == DmaChannelId.ToSpr)
        {
            TransferMainRamToScratchpad(ctx, ram, addr, ch.Sadr, qwc);
            ch.Sadr = (ch.Sadr + qwc * QuadwordBytes) & 0x3FF0u;
            TotalQuadwordsTransferred += qwc;
            return addr + qwc * QuadwordBytes;
        }

        return TransferBlockToPeripheral(ctx, ram, ch.ChannelId, addr, qwc);
    }

    private uint TransferBlockToPeripheral(CpuContext* ctx, byte* ram, DmaChannelId channelId, uint startAddr, uint qwc)
    {
        RuntimeDispatcher.CurrentDmaPc = ctx != null ? ctx->PC : 0;
        RuntimeDispatcher.CurrentDmaRa = ctx != null ? ctx->GetU32(31) : 0;
        RuntimeDispatcher.CurrentDmaAddr = startAddr;

        int byteLen = checked((int)(qwc * QuadwordBytes));
        ReadOnlySpan<byte> payload = GetDmaPointerSpan(ctx, ram, startAddr, byteLen);

        DispatchToPeripheral(channelId, payload);
        TotalQuadwordsTransferred += qwc;
        return startAddr + (uint)byteLen;
    }

    public void DispatchToPeripheral(DmaChannelId channelId, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        if (CaptureDebugPackets && _lastTransferredPacket.Count < 1024 * 1024)
        {
            foreach (byte b in data)
                _lastTransferredPacket.Add(b);
        }

        switch (channelId)
        {
            case DmaChannelId.Vif0:
                OnVif0Data?.Invoke(data);
                break;
            case DmaChannelId.Vif1:
                OnVif1Data?.Invoke(data);
                break;
            case DmaChannelId.Gif:
                OnGifData?.Invoke(data);
                break;
            case DmaChannelId.Sif1:
                OnSif1Data?.Invoke(data);
                break;
        }
    }

    private static void TransferScratchpadToMainRam(CpuContext* ctx, byte* ram, uint sadr, uint madr, uint qwc)
    {
        if (ctx == null || ctx->Scratchpad == null)
            return;

        int bytes = checked((int)(qwc * QuadwordBytes));
        uint sprOff = sadr & 0x3FF0u;
        uint ramOff = madr & 0x01FFFFFFu;
        int safeBytes = (int)Math.Min(bytes, Math.Min(CpuContext.ScratchpadSize - sprOff, CpuContext.RamSize - ramOff));
        if (safeBytes > 0)
        {
            var src = new ReadOnlySpan<byte>(ctx->Scratchpad + sprOff, safeBytes);
            src.CopyTo(new Span<byte>(ram + ramOff, safeBytes));
        }
    }

    private static void TransferMainRamToScratchpad(CpuContext* ctx, byte* ram, uint madr, uint sadr, uint qwc)
    {
        if (ctx == null || ctx->Scratchpad == null)
            return;

        int bytes = checked((int)(qwc * QuadwordBytes));
        uint ramOff = madr & 0x01FFFFFFu;
        uint sprOff = sadr & 0x3FF0u;
        int safeBytes = (int)Math.Min(bytes, Math.Min(CpuContext.RamSize - ramOff, CpuContext.ScratchpadSize - sprOff));
        if (safeBytes > 0)
        {
            new ReadOnlySpan<byte>(ram + ramOff, safeBytes).CopyTo(new Span<byte>(ctx->Scratchpad + sprOff, safeBytes));
        }
    }

    /// <summary>
    /// Resolves a DMA address (supporting both bit-31 / 0x70000000 Scratchpad addresses and 32MB main RAM).
    /// </summary>
    public static ReadOnlySpan<byte> GetDmaPointerSpan(CpuContext* ctx, byte* ram, uint dmacAddr, int byteCount)
    {
        bool isSpr = ((dmacAddr & 0x70000000u) == 0x70000000u) || (dmacAddr >= 0x80000000u && dmacAddr < 0x80004000u);
        if (isSpr && ctx != null && ctx->Scratchpad != null)
        {
            uint sprOff = dmacAddr & 0x3FF0u;
            int len = Math.Min(byteCount, (int)(CpuContext.ScratchpadSize - sprOff));
            return new ReadOnlySpan<byte>(ctx->Scratchpad + sprOff, Math.Max(0, len));
        }
        else
        {
            uint phys = dmacAddr & 0x01FFFFFFu;
            int len = Math.Min(byteCount, (int)(CpuContext.RamSize - phys));
            return new ReadOnlySpan<byte>(ram + phys, Math.Max(0, len));
        }
    }
}
