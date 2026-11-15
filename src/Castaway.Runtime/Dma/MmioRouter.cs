using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using Castaway.Core;

namespace Castaway.Runtime;

public sealed unsafe class MmioRouter
{
    /// <summary>EE Timer 1 input frequency under /256 prescaler (147.456 MHz / 256).</summary>
    public const uint Timer1TicksPerSecond = 576_000u;
    /// <summary>Fixed per-frame advance at 60 Hz (16.667 ms) for deterministic/trace runs.</summary>
    public const uint FixedTimerTicksPerFrame = 9_600u;
    /// <summary>Max real-time frame timer advance (66.67 ms / 15 FPS floor, matching SLUS_216.64 sub_001BF008).</summary>
    public const uint MaxRealTimeTimerTicksPerFrame = 38_400u;

    private readonly EeKernel _kernel;
    private readonly DmaController _dma;
    private readonly uint[] _timerCount = new uint[4];
    private readonly uint[] _timerMode = new uint[4];
    private readonly uint[] _timerComp = new uint[4];
    private readonly uint[] _timerHold = new uint[4];

    private Func<long> _timestampProvider = Stopwatch.GetTimestamp;
    private long _timestampFrequency = Stopwatch.Frequency;
    private bool _hasRealTimeBaseline;
    private long _lastTimerHostTick;
    private long _realTimeTickRemainderNum;

    /// <summary>
    /// When true, <see cref="StepFrameTimers"/> advances EE timers proportionally to real elapsed
    /// host wall-clock time (clamped to 66.67 ms), eliminating slow motion when running at 30 FPS or
    /// during heavy frames. Keep false for deterministic tests and binary trace comparisons.
    /// </summary>
    public bool UseRealTimeTimers { get; set; }

    /// <summary>Timer ticks applied during the most recent <see cref="StepFrameTimers"/> call.</summary>
    public uint LastFrameTimerTicks { get; private set; }

    private uint _sifMscom;
    private uint _sifSmcom;
    private uint _sifMsflg;
    private uint _sifSmflg = 0x00070000u; // SIF2 + CMD + RPC initialized ready flags
    private uint _sifCtrl;

    private ulong _gsPmode;
    private ulong _gsDispfb1;
    private ulong _gsDisplay1;
    private ulong _gsDispfb2;
    private ulong _gsDisplay2;
    private ulong _gsBgcolor;
    private ulong _gsCsr = 0x00000008u; // VSINT + FIFO empty ready (toggles FIELD bit 13 on read)

    public ulong GsPmode => _gsPmode;
    public ulong GsDispfb1 => _gsDispfb1;
    public ulong GsDisplay1 => _gsDisplay1;
    public ulong GsDispfb2 => _gsDispfb2;
    public ulong GsDisplay2 => _gsDisplay2;
    public ulong GsBgcolor => _gsBgcolor;
    public ulong GsCsr => _gsCsr;
    public void SetGsCsrFinish() => _gsCsr |= 0x0002ul;

    public CpuContext* ActiveContext { get; set; }
    public byte* ActiveRam { get; set; }

    public MmioRouter(EeKernel kernel, DmaController dma)
    {
        _kernel = kernel;
        _dma = dma;
        _kernel.Mmio = this;
    }

    public void SetRealTimeClockSource(Func<long> timestampProvider, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(timestampProvider);
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency), "Timestamp frequency must be positive.");

        _timestampProvider = timestampProvider;
        _timestampFrequency = timestampFrequency;
        _hasRealTimeBaseline = false;
        _lastTimerHostTick = 0;
        _realTimeTickRemainderNum = 0;
    }

    public uint AdvanceTimerCount(int tIdx, uint delta)
    {
        uint next = _timerCount[tIdx] + delta;
        uint wraps = next >> 16;
        _timerCount[tIdx] = next & 0xFFFFu;
        if (wraps != 0 && tIdx == 1 && ActiveRam != null)
        {
            ulong ovf = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(ActiveRam + 0x004A4D08u, 8));
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(ActiveRam + 0x004A4D08u, 8), ovf + wraps);
        }
        return _timerCount[tIdx];
    }

    public void StepFrameTimers(byte* ram)
    {
        if (ram != null)
            ActiveRam = ram;

        uint delta = ComputeFrameTimerTicks();
        LastFrameTimerTicks = delta;
        AdvanceTimerCount(0, delta);
        AdvanceTimerCount(1, delta);
        AdvanceTimerCount(2, delta);
        AdvanceTimerCount(3, delta);
    }

    private uint ComputeFrameTimerTicks()
    {
        if (!UseRealTimeTimers)
        {
            _hasRealTimeBaseline = false;
            _realTimeTickRemainderNum = 0;
            return FixedTimerTicksPerFrame;
        }

        long now = _timestampProvider();
        if (!_hasRealTimeBaseline)
        {
            _hasRealTimeBaseline = true;
            _lastTimerHostTick = now;
            _realTimeTickRemainderNum = 0;
            return FixedTimerTicksPerFrame;
        }

        long elapsedHostTicks = Math.Max(0L, now - _lastTimerHostTick);
        _lastTimerHostTick = now;

        if (elapsedHostTicks >= _timestampFrequency)
        {
            _realTimeTickRemainderNum = 0;
            return MaxRealTimeTimerTicksPerFrame;
        }

        Int128 totalNum = (Int128)elapsedHostTicks * Timer1TicksPerSecond + _realTimeTickRemainderNum;
        Int128 maxNum = (Int128)MaxRealTimeTimerTicksPerFrame * _timestampFrequency;
        if (totalNum >= maxNum)
        {
            _realTimeTickRemainderNum = 0;
            return MaxRealTimeTimerTicksPerFrame;
        }

        uint wholeTicks = (uint)(totalNum / _timestampFrequency);
        _realTimeTickRemainderNum = (long)(totalNum % _timestampFrequency);
        return wholeTicks;
    }

    public uint ReadSifReg(uint reg)
    {
        if ((reg & 0x80000000u) != 0)
            return 1u;

        return (reg & 0xFFu) switch
        {
            0x00 or 0x01 => _sifMscom,
            0x02 => _sifSmcom,
            0x03 => _sifMsflg,
            0x04 => _sifSmflg | 0x00070000u,
            _ => 0x00070000u
        };
    }

    public void WriteSifReg(uint reg, uint val)
    {
        switch (reg & 0xFFu)
        {
            case 0x00:
            case 0x01:
                _sifMscom = val;
                break;
            case 0x02:
                _sifSmcom = val;
                break;
            case 0x03:
                _sifMsflg |= val;
                break;
            case 0x04:
                _sifSmflg &= ~val;
                break;
        }
    }

    public uint Read32(CpuContext* ctx, byte* ram, uint vaddr)
    {
        ActiveContext = ctx;
        ActiveRam = ram;
        return Read32(vaddr);
    }

    public uint Read32(uint vaddr)
    {
        // 0. VU0 / VU1 Memories (0x11000000..0x1100FFFF)
        uint norm = vaddr & 0x1FFFFFFFu;
        if (norm >= 0x11000000u && norm < 0x11010000u)
        {
            uint off = norm - 0x11000000u;
            if (off < 0x4000u && RuntimeDispatcher.Vu0Micro != null)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(RuntimeDispatcher.Vu0Micro + (off & 0x0FFFu));
            if (off >= 0x4000u && off < 0x8000u)
            {
                byte* vu0Data = ActiveContext != null && ActiveContext->Vu0DataMem != null ? ActiveContext->Vu0DataMem : RuntimeDispatcher.Vu0Data;
                if (vu0Data != null)
                    return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(vu0Data + (off & 0x0FFFu));
            }
            if (off >= 0x8000u && off < 0xC000u && RuntimeDispatcher.Vu1Micro != null)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(RuntimeDispatcher.Vu1Micro + (off & 0x3FFFu));
            if (off >= 0xC000u && RuntimeDispatcher.Vu1Data != null)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(RuntimeDispatcher.Vu1Data + (off & 0x3FFFu));
            return 0u;
        }

        // 1. Hardware Timers T0..T3: 0x10000000..0x10001FFF
        if (vaddr >= 0x10000000u && vaddr < 0x10002000u)
        {
            int tIdx = (int)((vaddr - 0x10000000u) >> 11) & 3;
            uint regOff = vaddr & 0xFFu;
            return regOff switch
            {
                0x00 => tIdx == 1 ? _timerCount[1] : AdvanceTimerCount(tIdx, 16u), // T1_COUNT advances in StepFrameTimers
                0x10 => (_timerMode[tIdx] & ~0x0800u) | 0x0400u, // EquF ready bit (Ovff handled via AdvanceTimerCount)
                0x20 => _timerComp[tIdx],
                0x30 => _timerHold[tIdx],
                _ => 0u
            };
        }

        // 2. VIF0 / VIF1 / GIF status registers
        if (vaddr == 0x10003000u || vaddr == 0x10003800u || vaddr == 0x10003020u)
        {
            return 0u; // Not busy / FIFO empty
        }

        // 3. DMAC Channels 0..9: 0x10008000..0x1000D7FF
        if (vaddr >= 0x10008000u && vaddr < 0x1000D800u)
        {
            uint chBase = vaddr & 0xFFFFFC00u;
            int chIdx = DmaController.TryMapBaseToChannelIndex(chBase);
            if (chIdx >= 0)
            {
                return _dma.ReadChannelRegister(chIdx, vaddr & 0xFFu);
            }
        }

        // 4. Global DMAC Registers: 0x1000E000..0x1000F5FF
        return vaddr switch
        {
            0x1000E000u => _dma.Ctrl,
            0x1000E010u => _kernel.DmacStat,
            0x1000E020u => _dma.Pcr,
            0x1000E030u => _dma.Sqwc,
            0x1000E040u => _dma.Rbsr,
            0x1000E050u => _dma.Rbor,
            0x1000E060u => _dma.Stadr,
            0x1000F000u => _kernel.IntcStat |= 0x000Cu, // Auto-assert VBlank Start/End so VSync polling progresses
            0x1000F010u => _kernel.IntcMask,
            0x1000F130u => _timerCount[0] += 16,
            0x1000F200u => _sifMscom,
            0x1000F210u => _sifSmcom,
            0x1000F220u => _sifMsflg,
            0x1000F230u => _sifSmflg | 0x00070000u,
            0x1000F240u => _sifCtrl,
            0x1000F520u => _dma.Enabler,
            0x12001000u => (uint)((_gsCsr ^= 0x2000ul) | 0x0008ul), // GS_CSR low (toggle FIELD bit 13 so both odd/even field polls succeed)
            0x12001004u => (uint)(_gsCsr >> 32),     // GS_CSR high
            _ => 0u
        };
    }

    public void Write32(CpuContext* ctx, byte* ram, uint vaddr, uint value)
    {
        ActiveContext = ctx;
        ActiveRam = ram;
        Write32(vaddr, value);
    }

    public void Write32(uint vaddr, uint value)
    {
        // 0. VU0 / VU1 Memories (0x11000000..0x1100FFFF)
        uint norm = vaddr & 0x1FFFFFFFu;
        if (norm >= 0x11000000u && norm < 0x11010000u)
        {
            uint off = norm - 0x11000000u;
            if (off < 0x4000u && RuntimeDispatcher.Vu0Micro != null)
            {
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(RuntimeDispatcher.Vu0Micro + (off & 0x0FFFu), value);
                return;
            }
            if (off >= 0x4000u && off < 0x8000u)
            {
                byte* vu0Data = ActiveContext != null && ActiveContext->Vu0DataMem != null ? ActiveContext->Vu0DataMem : RuntimeDispatcher.Vu0Data;
                if (vu0Data != null)
                {
                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(vu0Data + (off & 0x0FFFu), value);
                    return;
                }
            }
            if (off >= 0x8000u && off < 0xC000u && RuntimeDispatcher.Vu1Micro != null)
            {
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(RuntimeDispatcher.Vu1Micro + (off & 0x3FFFu), value);
                return;
            }
            if (off >= 0xC000u && RuntimeDispatcher.Vu1Data != null)
            {
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(RuntimeDispatcher.Vu1Data + (off & 0x3FFFu), value);
                return;
            }
            return;
        }

        // 1. Hardware Timers T0..T3
        if (vaddr >= 0x10000000u && vaddr < 0x10002000u)
        {
            int tIdx = (int)((vaddr - 0x10000000u) >> 11) & 3;
            switch (vaddr & 0xFFu)
            {
                case 0x00: _timerCount[tIdx] = value & 0xFFFFu; break;
                case 0x10: _timerMode[tIdx] = value & ~0x0C00u; break;
                case 0x20: _timerComp[tIdx] = value & 0xFFFFu; break;
                case 0x30: _timerHold[tIdx] = value & 0xFFFFu; break;
            }
            return;
        }

        // 2. DMAC Channels 0..9
        if (vaddr >= 0x10008000u && vaddr < 0x1000D800u)
        {
            uint chBase = vaddr & 0xFFFFFC00u;
            int chIdx = DmaController.TryMapBaseToChannelIndex(chBase);
            if (chIdx >= 0 && ActiveRam != null)
            {
                _dma.WriteChannelRegister(ActiveContext, ActiveRam, chIdx, vaddr & 0xFFu, value);
                return;
            }
        }

        // 3. Global DMAC, INTC, SIF, GS Privileged
        switch (vaddr)
        {
            case 0x1000E000u:
                _dma.Ctrl = value;
                break;

            case 0x1000E010u: // D_STAT: lower 16 bits are W1C (write 1 to clear), upper 16 bits are W1I (write 1 to invert mask!)
            {
                uint clearStat = value & 0x0000FFFFu;
                uint invertMask = value & 0xFFFF0000u;
                _kernel.DmacStat = (_kernel.DmacStat & ~clearStat) ^ invertMask;
                break;
            }

            case 0x1000E020u: _dma.Pcr = value; break;
            case 0x1000E030u: _dma.Sqwc = value; break;
            case 0x1000E040u: _dma.Rbsr = value; break;
            case 0x1000E050u: _dma.Rbor = value; break;
            case 0x1000E060u: _dma.Stadr = value; break;

            case 0x1000F000u: // INTC_STAT: write 1 to clear
                _kernel.IntcStat &= ~value;
                break;

            case 0x1000F010u: // INTC_MASK: write 1 to invert mask bit
                _kernel.IntcMask ^= value;
                break;

            case 0x1000F200u: _sifMscom = value; break;
            case 0x1000F210u: _sifSmcom = value; break;
            case 0x1000F220u: _sifMsflg |= value; break;
            case 0x1000F230u: _sifSmflg &= ~value; break;
            case 0x1000F240u: _sifCtrl = value; break;
            case 0x1000F590u: _dma.Enabler = value; break;

            case 0x12000000u: _gsPmode = (_gsPmode & 0xFFFFFFFF00000000ul) | value; break;
            case 0x12000004u: _gsPmode = (_gsPmode & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x12000070u: _gsDispfb1 = (_gsDispfb1 & 0xFFFFFFFF00000000ul) | value; break;
            case 0x12000074u: _gsDispfb1 = (_gsDispfb1 & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x12000080u: _gsDisplay1 = (_gsDisplay1 & 0xFFFFFFFF00000000ul) | value; break;
            case 0x12000084u: _gsDisplay1 = (_gsDisplay1 & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x12000090u: _gsDispfb2 = (_gsDispfb2 & 0xFFFFFFFF00000000ul) | value; break;
            case 0x12000094u: _gsDispfb2 = (_gsDispfb2 & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x120000A0u: _gsDisplay2 = (_gsDisplay2 & 0xFFFFFFFF00000000ul) | value; break;
            case 0x120000A4u: _gsDisplay2 = (_gsDisplay2 & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x120000E0u: _gsBgcolor = (_gsBgcolor & 0xFFFFFFFF00000000ul) | value; break;
            case 0x120000E4u: _gsBgcolor = (_gsBgcolor & 0x00000000FFFFFFFFul) | ((ulong)value << 32); break;
            case 0x12001000u: _gsCsr = (_gsCsr & ~((ulong)value & 0x1Ful)) | 0x0008ul; break;
        }
    }

    public Vector128<uint> Read128(CpuContext* ctx, byte* ram, uint vaddr)
    {
        ActiveContext = ctx;
        ActiveRam = ram;
        return Read128(vaddr);
    }

    private long _vif1FifoReadCount;

    public Vector128<uint> Read128(uint vaddr)
    {
        uint norm = vaddr & 0x1FFFFFFFu;
        if (norm >= 0x10005000u && norm < 0x10005FF0u && _dma.OnVif1ReadData != null)
        {
            Span<byte> qword = stackalloc byte[16];
            _dma.OnVif1ReadData(qword);
            long cnt = Interlocked.Increment(ref _vif1FifoReadCount);
            if (cnt == 1 || cnt % 1024 == 0)
            {
                Console.WriteLine($"[MMIO VIF1_FIFO READ128 #{cnt}] vaddr=0x{vaddr:X8} val0=0x{System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(ref qword[0]):X8}");
            }
            return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<Vector128<uint>>(ref qword[0]);
        }

        uint lo0 = Read32(vaddr);
        uint lo1 = Read32(vaddr + 4);
        uint lo2 = Read32(vaddr + 8);
        uint lo3 = Read32(vaddr + 12);
        return Vector128.Create(lo0, lo1, lo2, lo3);
    }

    public void Write128(CpuContext* ctx, byte* ram, uint vaddr, Vector128<uint> value)
    {
        ActiveContext = ctx;
        ActiveRam = ram;
        Write128(vaddr, value);
    }

    public void Write128(uint vaddr, Vector128<uint> value)
    {
        Span<byte> qword = stackalloc byte[16];
        value.AsByte().CopyTo(qword);

        // Check VIF0_FIFO (0x10004000), VIF1_FIFO (0x10005000), GIF_FIFO (0x10006000)
        if (vaddr >= 0x10004000u && vaddr < 0x10004FF0u)
        {
            _dma.DispatchToPeripheral(DmaChannelId.Vif0, qword);
            return;
        }
        if (vaddr >= 0x10005000u && vaddr < 0x10005FF0u)
        {
            _dma.DispatchToPeripheral(DmaChannelId.Vif1, qword);
            return;
        }
        if (vaddr >= 0x10006000u && vaddr < 0x10006FF0u)
        {
            _dma.DispatchToPeripheral(DmaChannelId.Gif, qword);
            return;
        }

        uint norm = vaddr & 0x1FFFFFFFu;
        if (norm >= 0x11000000u && norm < 0x11010000u)
        {
            uint off = norm - 0x11000000u;
            if (off >= 0xC000u && RuntimeDispatcher.Vu1Data != null)
            {
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(RuntimeDispatcher.Vu1Data + (off & 0x3FFFu), value);
                return;
            }
            if (off >= 0x4000u && off < 0x8000u)
            {
                byte* vu0Data = ActiveContext != null && ActiveContext->Vu0DataMem != null ? ActiveContext->Vu0DataMem : RuntimeDispatcher.Vu0Data;
                if (vu0Data != null)
                {
                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(vu0Data + (off & 0x0FFFu), value);
                    return;
                }
            }
        }

        Write32(vaddr, value.GetElement(0));
    }
}
