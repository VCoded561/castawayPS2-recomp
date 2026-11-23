using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.OpenAL;

namespace Castaway.Runtime;

/// <summary>
/// HLE SPU2 48-voice ADPCM/PCM synthesizer + SCEE MultiStream (SID 0x00012345 / stream.irx) IOP service,
/// outputting 48 kHz Stereo16 via Silk.NET.OpenAL (with deterministic stepping in headless/test mode).
/// </summary>
public sealed unsafe class Spu2AudioService : IDisposable
{
    public const int Spu2RamSize = 2 * 1024 * 1024; // 2 MB SPU2 RAM
    public const int IopRamSize = 8 * 1024 * 1024;  // 8 MB virtual IOP RAM (heap starts at 0x00100000)
    public const int AdpcmBlockBytes = 16;
    public const int AdpcmSamplesPerBlock = 28;
    public const int OutputSampleRate = 48000;
    public const int VoiceCount = 48;
    public const int MaxStreamSlots = 48;
    public const uint InitialFreeSpuRamAddr = 0x00005010u;

    private const int OpenAlBufferCount = 4;
    private const int OpenAlMixFrames = 1024; // ~21.33 ms per stereo buffer at 48 kHz
    private const int StreamReadChunkBytes = 32768;

    // Sony PS1/PS2 SPU2 ADPCM IIR filter coefficients (scaled by 64)
    private static readonly int[] FilterPos = [0, 60, 115, 98, 122];
    private static readonly int[] FilterNeg = [0, 0, -52, -55, -60];

    public delegate int GuestStreamFileRead(uint fileHandle, long offset, Span<byte> destination);

    private struct Spu2Voice
    {
        public bool Active;
        public bool Stopping;
        public uint StartAddr;
        public uint PendingStartAddr; // set by cmd=28 (Voice SSA)
        public uint LoopAddr;
        public uint CurrentAddr;
        public int Hist1;
        public int Hist2;
        public short[]? DecodedBlock;
        public bool BlockLoaded;
        public byte CurrentBlockFlags;
        public double SamplePos;
        public ushort Pitch;         // 0x1000 = 48000 Hz
        public short VolL;
        public short VolR;
        public short TargetVolL;
        public short TargetVolR;
        public short VolRampStep;
        public ushort Adsr1;
        public ushort Adsr2;
        public float Envelope;
        public bool EffectsEnabled;

        public short[]? GetBlockBuffer() => DecodedBlock ??= new short[AdpcmSamplesPerBlock];
    }

    private sealed class StreamChannelDecoder
    {
        public byte[] Buffer = new byte[StreamReadChunkBytes];
        public int BufferValidBytes;
        public int BufferReadPos;
        public long InterleaveOffsetInChunk;
        public int Hist1;
        public int Hist2;
        public readonly short[] DecodedBlock = new short[AdpcmSamplesPerBlock];
        public bool BlockLoaded;
        public double SamplePos;
    }

    private sealed class MultiStreamSlot
    {
        public bool Active;
        public bool PendingStart;
        public uint FileHandle;
        public long BaseOffset;
        public long TotalPayloadBytes;
        public long BytesReadFromFile;
        public int Channels = 1;
        public int SampleRate = 24000;
        public int Interleave = 0x4000;
        public int SpuChLeft = 46;
        public int SpuChRight = 47;
        public int ParentSlot = -1;
        public uint EndOffsetBytes;
        public double MixSampleRemainder;
        public ulong ElapsedMixSamples;
        public bool Finished;
        public readonly StreamChannelDecoder Left = new();
        public readonly StreamChannelDecoder Right = new();

        public void Reset()
        {
            Active = false;
            PendingStart = false;
            FileHandle = 0;
            BaseOffset = 0;
            TotalPayloadBytes = 0;
            BytesReadFromFile = 0;
            Channels = 1;
            SampleRate = 24000;
            Interleave = 0x4000;
            SpuChLeft = 46;
            SpuChRight = 47;
            ParentSlot = -1;
            EndOffsetBytes = 0;
            MixSampleRemainder = 0;
            ElapsedMixSamples = 0;
            Finished = false;
            ResetDecoder(Left);
            ResetDecoder(Right);
        }

        private static void ResetDecoder(StreamChannelDecoder d)
        {
            d.BufferValidBytes = 0;
            d.BufferReadPos = 0;
            d.InterleaveOffsetInChunk = 0;
            d.Hist1 = 0;
            d.Hist2 = 0;
            d.BlockLoaded = false;
            d.SamplePos = 0;
        }
    }

    private readonly object _mixLock = new();
    private readonly byte[] _spu2Ram = new byte[Spu2RamSize];
    private readonly byte[] _iopRam = new byte[IopRamSize];
    private readonly Dictionary<int, ushort> _regs = new();
    private readonly Dictionary<int, uint> _addrs = new();
    private readonly Spu2Voice[] _voices = new Spu2Voice[VoiceCount];
    private readonly MultiStreamSlot[] _streams = new MultiStreamSlot[MaxStreamSlots];
    private readonly Queue<short[]> _moviePcmQueue = new();
    private short[]? _currentMoviePcmChunk;
    private int _currentMoviePcmOffset;
    private int _moviePcmChannels = 2;

    private uint _freeSpuRamAddr = InitialFreeSpuRamAddr;
    private int _maxStreamLimit = MaxStreamSlots;
    private short _masterVolL = 0x3FFF;
    private short _masterVolR = 0x3FFF;
    private int _traceLogCount;
    private uint _eaStreamTick;
    private uint _heartbeatEeAddr;
    private uint _heartbeatSeq;

    // Pending MultiStream batch file/transfer descriptors (cmd=41, cmd=49, cmd=77)
    private uint _pendingFileHandle;
    private uint _pendingFileOffset;
    private uint _pendingFileSize;
    private uint _pendingEeDstAddr;
    private uint _lastHeaderOffset;
    private uint _lastHeaderFileHandle;
    private int _lastHeaderChannels = 1;
    private int _lastHeaderSampleRate = 24000;
    private int _lastHeaderInterleave = 0x4000;

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _streamingSource;
    private bool _openAlAvailable;
    private bool _winMmAvailable;
    private nint _waveOutHandle;
    private Thread? _mixerThread;
    private volatile bool _stopMixerThread;

    public bool IsOpenAlInitialized => _openAlAvailable;
    public bool IsWinMmInitialized => _winMmAvailable;
    public bool IsRealtimeAudioActive => _openAlAvailable || _winMmAvailable;
    public bool EnableTraceLogging { get; set; } = Environment.GetEnvironmentVariable("CASTAWAY_AUDIO_LOG") == "1";
    public GuestStreamFileRead? StreamFileReader { get; set; }
    public byte[] Spu2Ram => _spu2Ram;
    public byte[] IopRam => _iopRam;
    public uint FreeSpuRamAddr => _freeSpuRamAddr;
    public uint HeartbeatEeAddr => _heartbeatEeAddr;

    public void AdvanceHeartbeat(byte* ram)
    {
        uint addr = _heartbeatEeAddr;
        if (ram != null && addr != 0)
        {
            uint seq = unchecked(++_heartbeatSeq);
            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(ram + (addr & 0x01FFFFFFu), 4), seq);
        }
    }

    public Spu2AudioService(bool tryInitOpenAl = true)
    {
        for (int i = 0; i < MaxStreamSlots; i++)
            _streams[i] = new MultiStreamSlot();

        if (tryInitOpenAl)
        {
            TryInitializeOpenAl();
            if (!_openAlAvailable && OperatingSystem.IsWindows())
            {
                TryInitializeWinMm();
            }
        }
    }

    public bool IsVoiceActive(int ch)
    {
        if ((uint)ch >= VoiceCount)
            return false;
        lock (_mixLock)
            return _voices[ch].Active;
    }

    public bool IsStreamActive(int slot)
    {
        if ((uint)slot >= MaxStreamSlots)
            return false;
        lock (_mixLock)
            return _streams[slot].Active;
    }

    public void WriteIopRam(uint iopAddr, ReadOnlySpan<byte> data)
    {
        uint phys = iopAddr & 0x007FFFFFu;
        int copyLen = (int)Math.Min((uint)data.Length, (uint)IopRamSize - phys);
        if (copyLen > 0)
        {
            data[..copyLen].CopyTo(_iopRam.AsSpan((int)phys, copyLen));
            if (EnableTraceLogging && _traceLogCount < 400)
            {
                _traceLogCount++;
                Console.WriteLine($"[IOP DMA] dst=0x{iopAddr:X8} len={copyLen}");
            }
        }
    }

    public ReadOnlySpan<byte> ReadIopRam(uint iopAddr, int length)
    {
        if (length <= 0)
            return ReadOnlySpan<byte>.Empty;
        uint phys = iopAddr & 0x007FFFFFu;
        int avail = (int)Math.Min((uint)length, (uint)IopRamSize - phys);
        return _iopRam.AsSpan((int)phys, avail);
    }

    private void TryInitializeOpenAl()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);

            _device = _alc.OpenDevice(null);
            if (_device == null)
                return;

            _context = _alc.CreateContext(_device, null);
            if (_context == null)
            {
                _alc.CloseDevice(_device);
                _device = null;
                return;
            }

            _alc.MakeContextCurrent(_context);
            _streamingSource = _al.GenSource();
            _alc.MakeContextCurrent(null);

            _openAlAvailable = true;
            _stopMixerThread = false;
            _mixerThread = new Thread(MixerThreadMain)
            {
                IsBackground = true,
                Name = "Castaway.OpenAlMixer"
            };
            _mixerThread.Start();
            Console.WriteLine($"[Audio] Silk.NET.OpenAL initialized ({OutputSampleRate} Hz Stereo16)");
        }
        catch (Exception ex)
        {
            _openAlAvailable = false;
            Console.WriteLine($"[Audio] OpenAL unavailable ({ex.GetType().Name}: {ex.Message}); trying Windows waveOut fallback...");
        }
    }

    private void TryInitializeWinMm()
    {
        try
        {
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = 2,
                nSamplesPerSec = (uint)OutputSampleRate,
                nAvgBytesPerSec = (uint)(OutputSampleRate * 4),
                nBlockAlign = 4,
                wBitsPerSample = 16,
                cbSize = 0
            };

            uint res = WaveOutOpen(out nint hWaveOut, 0xFFFFFFFFu, in fmt, 0, 0, 0);
            if (res != 0 || hWaveOut == 0)
            {
                Console.WriteLine($"[Audio] Windows waveOutOpen failed (mmresult={res}); running headless mixer.");
                return;
            }

            _waveOutHandle = hWaveOut;
            _winMmAvailable = true;
            _stopMixerThread = false;
            _mixerThread = new Thread(WinMmMixerThreadMain)
            {
                IsBackground = true,
                Name = "Castaway.WinMmMixer"
            };
            _mixerThread.Start();
            Console.WriteLine($"[Audio] Windows waveOut initialized ({OutputSampleRate} Hz Stereo16)");
        }
        catch (Exception ex)
        {
            _winMmAvailable = false;
            Console.WriteLine($"[Audio] Windows waveOut initialization failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void WinMmMixerThreadMain()
    {
        nint hwo = _waveOutHandle;
        if (hwo == 0)
            return;

        const uint WhdrDone = 0x00000001u;
        const uint WhdrPrepared = 0x00000002u;
        int sampleCount = OpenAlMixFrames * 2;
        uint byteCount = (uint)(sampleCount * sizeof(short));
        uint hdrSize = (uint)sizeof(WaveHdr);

        var buffers = new short[OpenAlBufferCount][];
        var pins = new GCHandle[OpenAlBufferCount];
        WaveHdr* hdrs = (WaveHdr*)NativeMemory.AllocZeroed((nuint)OpenAlBufferCount, (nuint)sizeof(WaveHdr));

        try
        {
            for (int i = 0; i < OpenAlBufferCount; i++)
            {
                buffers[i] = new short[sampleCount];
                pins[i] = GCHandle.Alloc(buffers[i], GCHandleType.Pinned);

                hdrs[i].lpData = pins[i].AddrOfPinnedObject();
                hdrs[i].dwBufferLength = byteCount;

                StepMixSamples(OpenAlMixFrames, buffers[i]);
                WaveOutPrepareHeader(hwo, &hdrs[i], hdrSize);
                WaveOutWrite(hwo, &hdrs[i], hdrSize);
            }

            int traceBufs = 0;
            short traceMaxAbs = 0;
            while (!_stopMixerThread)
            {
                bool submittedAny = false;
                for (int i = 0; i < OpenAlBufferCount && !_stopMixerThread; i++)
                {
                    if ((hdrs[i].dwFlags & WhdrDone) != 0)
                    {
                        WaveOutUnprepareHeader(hwo, &hdrs[i], hdrSize);
                        hdrs[i].dwFlags = 0;
                        hdrs[i].dwLoops = 0;
                        StepMixSamples(OpenAlMixFrames, buffers[i]);
                        if (EnableTraceLogging)
                        {
                            foreach (short s in buffers[i])
                            {
                                short a = s == short.MinValue ? short.MaxValue : Math.Abs(s);
                                if (a > traceMaxAbs) traceMaxAbs = a;
                            }
                            if (++traceBufs >= 45)
                            {
                                Console.WriteLine($"[Audio WaveOut] 1s peak={traceMaxAbs}");
                                traceBufs = 0;
                                traceMaxAbs = 0;
                            }
                        }
                        WaveOutPrepareHeader(hwo, &hdrs[i], hdrSize);
                        WaveOutWrite(hwo, &hdrs[i], hdrSize);
                        submittedAny = true;
                    }
                }

                if (!submittedAny)
                    Thread.Sleep(4);
            }

            WaveOutReset(hwo);
            for (int i = 0; i < OpenAlBufferCount; i++)
            {
                if ((hdrs[i].dwFlags & WhdrPrepared) != 0)
                    WaveOutUnprepareHeader(hwo, &hdrs[i], hdrSize);
            }
        }
        catch
        {
            // Ignore shutdown errors in WinMM mixer thread
        }
        finally
        {
            for (int i = 0; i < OpenAlBufferCount; i++)
            {
                if (pins[i].IsAllocated)
                    pins[i].Free();
            }
            NativeMemory.Free(hdrs);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nuint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nuint reserved;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern uint WaveOutOpen(out nint phwo, uint uDeviceID, in WaveFormatEx pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern uint WaveOutPrepareHeader(nint hwo, WaveHdr* pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern uint WaveOutWrite(nint hwo, WaveHdr* pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern uint WaveOutUnprepareHeader(nint hwo, WaveHdr* pwh, uint cbwh);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern uint WaveOutReset(nint hwo);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern uint WaveOutClose(nint hwo);

    private void MixerThreadMain()
    {
        if (_alc == null || _al == null || _context == null)
            return;

        try
        {
            _alc.MakeContextCurrent(_context);
        }
        catch
        {
            return;
        }

        uint[] buffers = new uint[OpenAlBufferCount];
        short[] mixBuf = new short[OpenAlMixFrames * 2];

        try
        {
            for (int i = 0; i < OpenAlBufferCount; i++)
            {
                uint bufId = _al.GenBuffer();
                buffers[i] = bufId;
                StepMixSamples(OpenAlMixFrames, mixBuf);
                fixed (short* ptr = mixBuf)
                {
                    _al.BufferData(bufId, BufferFormat.Stereo16, ptr, mixBuf.Length * sizeof(short), OutputSampleRate);
                }
                _al.SourceQueueBuffers(_streamingSource, 1, &bufId);
            }

            _al.SourcePlay(_streamingSource);

            while (!_stopMixerThread)
            {
                _al.GetSourceProperty(_streamingSource, GetSourceInteger.BuffersProcessed, out int processed);
                if (processed > 0)
                {
                    while (processed-- > 0 && !_stopMixerThread)
                    {
                        uint bufId = 0;
                        _al.SourceUnqueueBuffers(_streamingSource, 1, &bufId);
                        if (bufId != 0)
                        {
                            StepMixSamples(OpenAlMixFrames, mixBuf);
                            fixed (short* ptr = mixBuf)
                            {
                                _al.BufferData(bufId, BufferFormat.Stereo16, ptr, mixBuf.Length * sizeof(short), OutputSampleRate);
                            }
                            _al.SourceQueueBuffers(_streamingSource, 1, &bufId);
                        }
                    }

                    _al.GetSourceProperty(_streamingSource, GetSourceInteger.SourceState, out int state);
                    if ((SourceState)state != SourceState.Playing && !_stopMixerThread)
                        _al.SourcePlay(_streamingSource);
                }
                else
                {
                    Thread.Sleep(4);
                }
            }

            _al.SourceStop(_streamingSource);
            _al.GetSourceProperty(_streamingSource, GetSourceInteger.BuffersQueued, out int queued);
            while (queued-- > 0)
            {
                uint bufId = 0;
                _al.SourceUnqueueBuffers(_streamingSource, 1, &bufId);
                if (bufId != 0)
                    _al.DeleteBuffer(bufId);
            }
            _alc.MakeContextCurrent(null);
        }
        catch
        {
            // Ignore shutdown errors in OpenAL mixer thread
        }
    }

    /// <summary>
    /// Decodes a single 16-byte Sony SPU2 / VAG ADPCM block into 28 16-bit signed PCM samples.
    /// Updates hist1 (s[n-1]) and hist2 (s[n-2]) in-place.
    /// </summary>
    public static byte DecodeAdpcmBlock(ReadOnlySpan<byte> block16, Span<short> outSamples28, ref int hist1, ref int hist2)
    {
        if (block16.Length < AdpcmBlockBytes || outSamples28.Length < AdpcmSamplesPerBlock)
            throw new ArgumentException("SPU2 ADPCM block requires 16 input bytes and 28 output samples.");

        byte shiftPredict = block16[0];
        byte flags = block16[1];

        int shift = shiftPredict & 0x0F;
        if (shift > 12) shift = 9;
        int predict = (shiftPredict >> 4) & 0x07;
        if (predict > 4) predict = 0;

        int f0 = FilterPos[predict];
        int f1 = FilterNeg[predict];

        int sampleIndex = 0;
        for (int i = 2; i < 16; i++)
        {
            byte b = block16[i];

            for (int nibbleIdx = 0; nibbleIdx < 2; nibbleIdx++)
            {
                int nibble = (nibbleIdx == 0) ? (b & 0x0F) : ((b >> 4) & 0x0F);
                int s = (short)(nibble << 12) >> shift;
                s += ((hist1 * f0) + (hist2 * f1) + 32) >> 6;
                short clamped = (short)Math.Clamp(s, short.MinValue, short.MaxValue);

                outSamples28[sampleIndex++] = clamped;
                hist2 = hist1;
                hist1 = clamped;
            }
        }

        return flags;
    }

    /// <summary>
    /// Decodes a contiguous stream of 16-byte SPU2 ADPCM blocks into 16-bit PCM samples.
    /// Stops at end of span or when loop-end flag (bit 0 of flags byte) is encountered if stopOnEndFlag is true.
    /// </summary>
    public static short[] DecodeAdpcmStream(ReadOnlySpan<byte> adpcmBytes, bool stopOnEndFlag = false)
    {
        int blockCount = adpcmBytes.Length / AdpcmBlockBytes;
        if (blockCount <= 0)
            return [];

        List<short> pcm = new(blockCount * AdpcmSamplesPerBlock);
        Span<short> blockSamples = stackalloc short[AdpcmSamplesPerBlock];
        int h1 = 0, h2 = 0;

        for (int b = 0; b < blockCount; b++)
        {
            byte flags = DecodeAdpcmBlock(adpcmBytes.Slice(b * AdpcmBlockBytes, AdpcmBlockBytes), blockSamples, ref h1, ref h2);
            for (int i = 0; i < AdpcmSamplesPerBlock; i++)
                pcm.Add(blockSamples[i]);

            if (stopOnEndFlag && (flags & 0x01) != 0)
                break;
        }

        return pcm.ToArray();
    }

    public void QueuePcmMono16(ReadOnlySpan<short> samples, int sampleRate = OutputSampleRate)
    {
        if (samples.IsEmpty)
            return;

        // Enqueue mono samples promoted cleanly into the Movie/PCM stereo queue so both
        // unit tests and the OpenAL streaming mixer handle PCM without fighting over _streamingSource.
        short[] stereo = new short[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            stereo[i * 2] = samples[i];
            stereo[i * 2 + 1] = samples[i];
        }
        lock (_mixLock)
        {
            _moviePcmQueue.Enqueue(stereo);
        }
    }

    /// <summary>
    /// Deterministically advances the 48-voice SPU2 hardware synthesizer, MultiStream decoders,
    /// and Movie PCM mixer by <paramref name="frames"/> 48 kHz stereo frames.
    /// </summary>
    public void StepMixSamples(int frames, Span<short> interleavedStereoOut = default)
    {
        if (frames <= 0)
            return;

        Span<int> mixAccum = frames <= 2048
            ? stackalloc int[frames * 2]
            : new int[frames * 2];
        mixAccum.Clear();

        lock (_mixLock)
        {
            float masterL = Math.Clamp(_masterVolL / 16384.0f, -1.0f, 1.0f);
            float masterR = Math.Clamp(_masterVolR / 16384.0f, -1.0f, 1.0f);

            MixHardwareVoicesLocked(frames, mixAccum);
            MixActiveStreamsLocked(frames, mixAccum);
            MixMoviePcmLocked(frames, mixAccum);

            if (!interleavedStereoOut.IsEmpty)
            {
                int outFrames = Math.Min(frames, interleavedStereoOut.Length / 2);
                for (int i = 0; i < outFrames; i++)
                {
                    int l = (int)(mixAccum[i * 2] * masterL);
                    int r = (int)(mixAccum[i * 2 + 1] * masterR);
                    interleavedStereoOut[i * 2] = (short)Math.Clamp(l, short.MinValue, short.MaxValue);
                    interleavedStereoOut[i * 2 + 1] = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
                }
            }
        }
    }

    private void MixHardwareVoicesLocked(int frames, Span<int> mixAccum)
    {
        for (int ch = 0; ch < VoiceCount; ch++)
        {
            ref Spu2Voice v = ref _voices[ch];
            if (!v.Active)
                continue;

            double step = Math.Clamp((v.Pitch == 0 ? 0x1000 : v.Pitch) / 4096.0, 0.01, 8.0);
            short[] block = v.GetBlockBuffer()!;

            for (int f = 0; f < frames; f++)
            {
                if (!EnsureVoiceBlockLoaded(ref v, block))
                {
                    v.Active = false;
                    break;
                }

                // Smooth volume ramping (cmd=96)
                StepVolumeRamp(ref v.VolL, v.TargetVolL, v.VolRampStep);
                StepVolumeRamp(ref v.VolR, v.TargetVolR, v.VolRampStep);

                if (v.Stopping)
                {
                    v.Envelope -= 0.005f; // ~4 ms release ramp
                    if (v.Envelope <= 0f)
                    {
                        v.Envelope = 0f;
                        v.Active = false;
                        break;
                    }
                }
                else if (v.Envelope < 1.0f)
                {
                    v.Envelope = Math.Min(1.0f, v.Envelope + 0.02f);
                }

                int sIdx = Math.Clamp((int)v.SamplePos, 0, AdpcmSamplesPerBlock - 1);
                float sample = block[sIdx];
                if (sIdx + 1 < AdpcmSamplesPerBlock)
                {
                    float frac = (float)(v.SamplePos - sIdx);
                    sample += (block[sIdx + 1] - sample) * frac;
                }

                float gainL = (v.VolL / 16384.0f) * v.Envelope;
                float gainR = (v.VolR / 16384.0f) * v.Envelope;
                mixAccum[f * 2] += (int)(sample * gainL);
                mixAccum[f * 2 + 1] += (int)(sample * gainR);

                v.SamplePos += step;
                if (v.SamplePos >= AdpcmSamplesPerBlock)
                {
                    int safety = 0;
                    while (v.SamplePos >= AdpcmSamplesPerBlock && v.Active && safety++ < 16)
                    {
                        v.SamplePos -= AdpcmSamplesPerBlock;
                        AdvanceVoiceBlockAddress(ref v);
                        if (v.Active && !EnsureVoiceBlockLoaded(ref v, block))
                            v.Active = false;
                    }
                }
            }
        }
    }

    private bool EnsureVoiceBlockLoaded(ref Spu2Voice v, short[] outBlock)
    {
        if (v.BlockLoaded)
            return true;

        uint addr = v.CurrentAddr & 0x001FFFF0u;
        if (addr + AdpcmBlockBytes > Spu2RamSize)
            return false;

        byte flags = DecodeAdpcmBlock(_spu2Ram.AsSpan((int)addr, AdpcmBlockBytes), outBlock, ref v.Hist1, ref v.Hist2);
        v.CurrentBlockFlags = flags;
        v.BlockLoaded = true;

        if ((flags & 0x04) != 0) // Loop Start
            v.LoopAddr = addr;

        return true;
    }

    private static void AdvanceVoiceBlockAddress(ref Spu2Voice v)
    {
        if ((v.CurrentBlockFlags & 0x01) != 0) // Loop End
        {
            if ((v.CurrentBlockFlags & 0x02) != 0) // Repeat from LoopAddr
            {
                v.CurrentAddr = v.LoopAddr & 0x001FFFF0u;
                v.BlockLoaded = false;
                return;
            }

            v.Active = false;
            v.BlockLoaded = false;
            return;
        }

        v.CurrentAddr = (v.CurrentAddr + AdpcmBlockBytes) & 0x001FFFF0u;
        v.BlockLoaded = false;
    }

    private void MixActiveStreamsLocked(int frames, Span<int> mixAccum)
    {
        for (int s = 0; s < MaxStreamSlots; s++)
        {
            MultiStreamSlot slot = _streams[s];
            if (!slot.Active)
                continue;

            double step = Math.Clamp((double)slot.SampleRate / OutputSampleRate, 0.05, 4.0);
            int chL = Math.Clamp(slot.SpuChLeft, 0, VoiceCount - 1);
            int chR = Math.Clamp(slot.SpuChRight, 0, VoiceCount - 1);

            for (int f = 0; f < frames; f++)
            {
                if (!slot.Active)
                    break;

                slot.ElapsedMixSamples++;

                StepVolumeRamp(ref _voices[chL].VolL, _voices[chL].TargetVolL, _voices[chL].VolRampStep);
                StepVolumeRamp(ref _voices[chL].VolR, _voices[chL].TargetVolR, _voices[chL].VolRampStep);
                if (slot.Channels >= 2 && chR != chL)
                {
                    StepVolumeRamp(ref _voices[chR].VolL, _voices[chR].TargetVolL, _voices[chR].VolRampStep);
                    StepVolumeRamp(ref _voices[chR].VolR, _voices[chR].TargetVolR, _voices[chR].VolRampStep);
                }

                if (!ReadStreamSample(slot, slot.Left, 0, step, out float leftSample))
                {
                    slot.Active = false;
                    slot.Finished = true;
                    break;
                }

                float lVolL = _voices[chL].VolL / 16384.0f;
                if (slot.Channels <= 1)
                {
                    float lVolR = _voices[chL].VolR / 16384.0f;
                    mixAccum[f * 2] += (int)(leftSample * lVolL);
                    mixAccum[f * 2 + 1] += (int)(leftSample * lVolR);
                }
                else
                {
                    // SLUS_216.64 (0x00430080 / 0x004391C8) sends the same (volL, volR) pair to both
                    // chL and chR of a 2-channel stereo stream. Keep left/right channels separated
                    // instead of summing both channels into both speakers (mono collapse + 2x gain).
                    mixAccum[f * 2] += (int)(leftSample * lVolL);
                    if (ReadStreamSample(slot, slot.Right, 1, step, out float rightSample))
                    {
                        float rVolR = _voices[chR].VolR / 16384.0f;
                        mixAccum[f * 2 + 1] += (int)(rightSample * rVolR);
                    }
                }
            }
        }
    }

    private bool ReadStreamSample(MultiStreamSlot slot, StreamChannelDecoder dec, int channelIdx, double step, out float sample)
    {
        sample = 0f;
        int safety = 0;
        while (!dec.BlockLoaded || dec.SamplePos >= AdpcmSamplesPerBlock)
        {
            if (dec.BlockLoaded)
            {
                dec.SamplePos -= AdpcmSamplesPerBlock;
                dec.BlockLoaded = false;
            }

            if (safety++ > 16)
                return false;

            if (!RefillDecoderIfNeeded(slot, dec, channelIdx))
                return false;

            ReadOnlySpan<byte> block16 = dec.Buffer.AsSpan(dec.BufferReadPos, AdpcmBlockBytes);
            dec.BufferReadPos += AdpcmBlockBytes;
            byte flags = DecodeAdpcmBlock(block16, dec.DecodedBlock, ref dec.Hist1, ref dec.Hist2);
            dec.BlockLoaded = true;

            if ((flags & 0x01) != 0 && (flags & 0x02) == 0 && dec.BufferReadPos >= dec.BufferValidBytes && slot.BytesReadFromFile >= slot.TotalPayloadBytes)
            {
                // Natural ADPCM stream end at end of payload
                break;
            }
        }

        int sIdx = Math.Clamp((int)dec.SamplePos, 0, AdpcmSamplesPerBlock - 1);
        float s0 = dec.DecodedBlock[sIdx];
        if (sIdx + 1 < AdpcmSamplesPerBlock)
        {
            float frac = (float)(dec.SamplePos - sIdx);
            s0 += (dec.DecodedBlock[sIdx + 1] - s0) * frac;
        }
        dec.SamplePos += step;
        sample = s0;
        return true;
    }

    private bool RefillDecoderIfNeeded(MultiStreamSlot slot, StreamChannelDecoder dec, int channelIdx)
    {
        if (dec.BufferReadPos + AdpcmBlockBytes <= dec.BufferValidBytes)
            return true;

        if (StreamFileReader == null)
            return false;

        long remaining = slot.TotalPayloadBytes > 0
            ? slot.TotalPayloadBytes - dec.InterleaveOffsetInChunk
            : long.MaxValue;
        if (remaining < AdpcmBlockBytes)
            return false;

        long filePos;
        int chunkToRead;
        if (slot.Channels <= 1)
        {
            filePos = slot.BaseOffset + dec.InterleaveOffsetInChunk;
            chunkToRead = (int)Math.Min(dec.Buffer.Length, remaining);
            dec.InterleaveOffsetInChunk += chunkToRead;
        }
        else
        {
            int interleave = Math.Max(AdpcmBlockBytes, slot.Interleave);
            long blockPairIndex = dec.InterleaveOffsetInChunk / interleave;
            long offsetInsideInterleave = dec.InterleaveOffsetInChunk % interleave;
            filePos = slot.BaseOffset + (blockPairIndex * slot.Channels + channelIdx) * interleave + offsetInsideInterleave;
            long leftInInterleave = interleave - offsetInsideInterleave;
            chunkToRead = (int)Math.Min(Math.Min(dec.Buffer.Length, leftInInterleave), remaining);
            dec.InterleaveOffsetInChunk += chunkToRead;
        }

        chunkToRead = (chunkToRead / AdpcmBlockBytes) * AdpcmBlockBytes;
        if (chunkToRead < AdpcmBlockBytes)
            return false;

        int read = StreamFileReader(slot.FileHandle, filePos, dec.Buffer.AsSpan(0, chunkToRead));
        read = (read / AdpcmBlockBytes) * AdpcmBlockBytes;
        if (read < AdpcmBlockBytes)
            return false;

        dec.BufferValidBytes = read;
        dec.BufferReadPos = 0;
        slot.BytesReadFromFile = Math.Max(slot.BytesReadFromFile, dec.InterleaveOffsetInChunk);
        return true;
    }

    private void MixMoviePcmLocked(int frames, Span<int> mixAccum)
    {
        for (int f = 0; f < frames; f++)
        {
            while (_currentMoviePcmChunk == null || _currentMoviePcmOffset + 2 > _currentMoviePcmChunk.Length)
            {
                if (_moviePcmQueue.Count == 0)
                {
                    _currentMoviePcmChunk = null;
                    _currentMoviePcmOffset = 0;
                    return;
                }
                _currentMoviePcmChunk = _moviePcmQueue.Dequeue();
                _currentMoviePcmOffset = 0;
            }

            mixAccum[f * 2] += _currentMoviePcmChunk[_currentMoviePcmOffset++];
            mixAccum[f * 2 + 1] += _currentMoviePcmChunk[_currentMoviePcmOffset++];
        }
    }

    /// <summary>
    /// Decodes a PS2 SPU2 / EA MultiStream 15-bit volume halfword into in-phase linear gain [0, 16383].
    /// Values 0x0000..0x3FFF are positive (+0..+16383); values 0x4000..0x7FFF are Dolby Pro Logic II
    /// inverted-phase rear-surround volumes encoded by 0x0024B8B0 / 0x004154F0 / 0x0046AE28 as
    /// (0x7FFF - magnitude), where 0x7FFF is attenuated zero (-0) and 0x4000 is full rear magnitude (16383).
    /// Returning positive magnitude cleanly downmixes Pro Logic II to PC stereo without 180° interaural
    /// phase cancellation or hollow rear-hemisphere directionality.
    /// </summary>
    private static short DecodeSpu2Vol(ushort raw)
    {
        int signed16 = (short)raw;
        if (signed16 < 0)
            return (short)Math.Min(-signed16, 0x3FFF);
        if (signed16 >= 0x4000)
            return (short)(0x7FFF - signed16);
        return (short)signed16;
    }

    private static void StepVolumeRamp(ref short current, short target, short step)
    {
        if (current == target)
            return;
        int delta = Math.Max((int)step, 64);
        if (current < target)
            current = (short)Math.Min(target, current + delta);
        else
            current = (short)Math.Max(target, current - delta);
    }

    public void Reset()
    {
        lock (_mixLock)
        {
            Array.Clear(_spu2Ram);
            Array.Clear(_iopRam);
            _regs.Clear();
            _addrs.Clear();
            Array.Clear(_voices);
            for (int i = 0; i < MaxStreamSlots; i++)
                _streams[i].Reset();
            _moviePcmQueue.Clear();
            _currentMoviePcmChunk = null;
            _currentMoviePcmOffset = 0;
            _freeSpuRamAddr = InitialFreeSpuRamAddr;
            _masterVolL = 0x3FFF;
            _masterVolR = 0x3FFF;
            _pendingFileHandle = 0;
            _pendingFileOffset = 0;
            _pendingFileSize = 0;
            _pendingEeDstAddr = 0;
        }
    }

    public void WriteRegister16(int regOffset, ushort value)
    {
        _regs[regOffset] = value;
    }

    public ushort ReadRegister16(int regOffset) => _regs.GetValueOrDefault(regOffset, (ushort)0);

    public void WriteAddress32(int regOffset, uint value)
    {
        _addrs[regOffset] = value;
    }

    public int HandleLibSdRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        Span<byte> send = sendBufAddr != 0
            ? new Span<byte>(ram + (sendBufAddr & 0x01FFFFFFu), Math.Max(16, sendSize))
            : Span<byte>.Empty;
        Span<byte> recv = recvBufAddr != 0
            ? new Span<byte>(ram + (recvBufAddr & 0x01FFFFFFu), Math.Max(16, recvSize))
            : Span<byte>.Empty;

        uint cmd = rpcNum & 0xFF00;
        int ret = 0;

        switch (cmd)
        {
            case 0x0100: // SetParam
                if (send.Length >= 8)
                {
                    int entry = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    ushort val = BinaryPrimitives.ReadUInt16LittleEndian(send[4..6]);
                    _regs[entry] = val;
                }
                break;

            case 0x0200: // GetParam
                if (send.Length >= 4)
                {
                    int entry = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    ret = _regs.GetValueOrDefault(entry, (ushort)0);
                }
                break;

            case 0x0500: // SetAddr
                if (send.Length >= 8)
                {
                    int entry = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(send[4..8]);
                    _addrs[entry] = val;
                }
                break;

            case 0x0600: // GetAddr
                if (send.Length >= 4)
                {
                    int entry = BinaryPrimitives.ReadInt32LittleEndian(send[0..4]);
                    ret = (int)_addrs.GetValueOrDefault(entry, InitialFreeSpuRamAddr);
                }
                break;

            case 0x0900: // VoiceTrans
                if (send.Length >= 16)
                {
                    uint memAddr = BinaryPrimitives.ReadUInt32LittleEndian(send[4..8]);
                    uint spuAddr = BinaryPrimitives.ReadUInt32LittleEndian(send[8..12]) % Spu2RamSize;
                    int size = BinaryPrimitives.ReadInt32LittleEndian(send[12..16]);
                    uint phys = memAddr & 0x01FFFFFFu;
                    int copyLen = (int)Math.Min(Math.Max(0, size), Spu2RamSize - spuAddr);
                    if (copyLen > 0 && phys + (uint)copyLen <= 32 * 1024 * 1024)
                    {
                        lock (_mixLock)
                        {
                            new ReadOnlySpan<byte>(ram + phys, copyLen).CopyTo(_spu2Ram.AsSpan((int)spuAddr, copyLen));
                        }
                    }
                    ret = size;
                }
                break;

            default:
                ret = 0;
                break;
        }

        if (recv.Length >= 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], ret);
        }

        return 0;
    }

    /// <summary>
    /// Handles EA Audio Stream / MultiStream IRX RPC (SID 0x00012345, bound at 0x00464848, called at 0x0046703C).
    /// </summary>
    public int HandleEaStreamRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        if (ram != null && sendBufAddr != 0 && sendSize >= 4)
        {
            uint sendPhys = sendBufAddr & 0x01FFFFFFu;
            int maxBytes = (int)Math.Min((uint)sendSize, (uint)Castaway.Core.CpuContext.RamSize - sendPhys);
            if (maxBytes >= 4)
            {
                ReadOnlySpan<byte> send = new ReadOnlySpan<byte>(ram + sendPhys, maxBytes);
                ProcessEaStreamBatch(ram, rpcNum, send);
            }
        }

        if (recvBufAddr != 0 && recvSize >= 8)
        {
            uint recvPhys = recvBufAddr & 0x01FFFFFFu;
            int outLen = (int)Math.Min((uint)Math.Max(256, recvSize), (uint)Castaway.Core.CpuContext.RamSize - recvPhys);
            Span<byte> recv = new Span<byte>(ram + recvPhys, outLen);
            BuildStatusPacket(recv);
        }

        return 0;
    }

    private void BuildStatusPacket(Span<byte> recv)
    {
        recv.Clear();

        uint tick = ++_eaStreamTick;
        if (tick == 0)
            tick = ++_eaStreamTick;

        lock (_mixLock)
        {
            int maxEntries = Math.Max(0, (recv.Length - 64 * 4 - 4) / 36);
            int entryCount = 0;

            for (int slotIdx = 0; slotIdx < MaxStreamSlots && entryCount < maxEntries; slotIdx++)
            {
                MultiStreamSlot s = _streams[slotIdx];
                if (!s.Active && !s.Finished)
                    continue;

                int off = 4 + entryCount * 36;
                uint spuCh = (uint)(s.SpuChLeft & 0x3F);
                uint stateBits = s.Active ? 1u : 0u;
                uint w0 = (spuCh << 10) | ((uint)(slotIdx & 0x3F) << 4) | (stateBits << 2);

                uint remaining = s.TotalPayloadBytes > s.BytesReadFromFile
                    ? (uint)Math.Min(0x00FFFFFFL, s.TotalPayloadBytes - s.BytesReadFromFile)
                    : 0u;
                uint total = (uint)Math.Min(uint.MaxValue, Math.Max(0L, s.TotalPayloadBytes));
                uint w3 = s.Active ? (0x80000000u | 1u) : 0u; // byte[0] == 0 marks slot finished
                uint chPair = (uint)((s.SpuChRight & 0xFFFF) | ((s.SpuChLeft & 0xFFFF) << 16));
                uint elapsedTicks = (uint)(s.ElapsedMixSamples / (OutputSampleRate / 60));

                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 0, 4), w0);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 4, 4), remaining);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 8, 4), total);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 12, 4), w3);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 16, 4), (uint)s.BytesReadFromFile);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 20, 4), (uint)s.BytesReadFromFile);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 28, 4), chPair);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(off + 32, 4), elapsedTicks);

                if (s.Finished)
                    s.Finished = false;

                entryCount++;
            }

            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], entryCount);

            int footerByte = (entryCount * 9) * 4;
            if (footerByte + 36 <= recv.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(footerByte + 4, 4), tick);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(footerByte + 16, 4), _freeSpuRamAddr);

                // 3 x uint32 packing 2-bit status for all 48 SPU2 channels (MSB-first: ch%16 at 30 - 2*(ch%16))
                Span<uint> voiceMasks = stackalloc uint[3];
                voiceMasks.Clear();
                for (int ch = 0; ch < VoiceCount; ch++)
                {
                    bool voiceBusy = _voices[ch].Active || IsChannelUsedByStreamLocked(ch);
                    if (voiceBusy)
                    {
                        int wordIdx = ch >> 4;
                        int shift = 30 - 2 * (ch & 15);
                        voiceMasks[wordIdx] |= (1u << shift);
                    }
                }

                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(footerByte + 24, 4), voiceMasks[0]);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(footerByte + 28, 4), voiceMasks[1]);
                BinaryPrimitives.WriteUInt32LittleEndian(recv.Slice(footerByte + 32, 4), voiceMasks[2]);
            }
        }
    }

    private bool IsChannelUsedByStreamLocked(int ch)
    {
        for (int i = 0; i < MaxStreamSlots; i++)
        {
            if (_streams[i].Active && (_streams[i].SpuChLeft == ch || (_streams[i].Channels >= 2 && _streams[i].SpuChRight == ch)))
                return true;
        }
        return false;
    }

    private void ProcessEaStreamBatch(byte* ram, uint rpcNum, ReadOnlySpan<byte> send)
    {
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(send[0..2]);
        ushort numCmds = BinaryPrimitives.ReadUInt16LittleEndian(send[2..4]);

        if (magic != 0x0046)
        {
            if (EnableTraceLogging && _traceLogCount < 400)
            {
                _traceLogCount++;
                Console.WriteLine($"[EA_STREAM] rpc=0x{rpcNum:X2} non-0x46 header magic=0x{magic:X4} w1=0x{numCmds:X4} len={send.Length}");
            }
            return;
        }

        int off = 4;
        for (int i = 0; i < numCmds && off + 4 <= send.Length; i++)
        {
            ushort cmdId = BinaryPrimitives.ReadUInt16LittleEndian(send.Slice(off, 2));
            ushort argHalfwords = BinaryPrimitives.ReadUInt16LittleEndian(send.Slice(off + 2, 2));
            off += 4;

            int argBytes = argHalfwords * 2;
            if (off + argBytes > send.Length)
                break;

            ReadOnlySpan<byte> argsBytes = send.Slice(off, argBytes);
            off += argBytes;

            if (EnableTraceLogging && cmdId != 9 && _traceLogCount < 400)
            {
                _traceLogCount++;
                var sb = new StringBuilder();
                sb.Append($"[EA_STREAM] rpc=0x{rpcNum:X2} cmd={cmdId} hw={argHalfwords} args=[");
                for (int h = 0; h < argHalfwords; h++)
                {
                    if (h > 0) sb.Append(' ');
                    ushort val = BinaryPrimitives.ReadUInt16LittleEndian(argsBytes.Slice(h * 2, 2));
                    sb.Append($"0x{val:X4}");
                }
                sb.Append(']');
                Console.WriteLine(sb.ToString());
            }

            DispatchMultiStreamCommand(ram, cmdId, argsBytes, argHalfwords);
        }
    }

    private void DispatchMultiStreamCommand(byte* ram, ushort cmdId, ReadOnlySpan<byte> argsBytes, int hwCount)
    {
        static ushort ArgU16(ReadOnlySpan<byte> b, int idx) =>
            (idx * 2 + 2 <= b.Length) ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(idx * 2, 2)) : (ushort)0;

        static uint ArgU32(ReadOnlySpan<byte> b, int idx) =>
            ((uint)ArgU16(b, idx) << 16) | ArgU16(b, idx + 1);

        lock (_mixLock)
        {
            switch (cmdId)
            {
                case 0:  // SOUND_PlaySFX(spuAddr32, ch, volL, volR, pitch, adsr)
                case 57: // SOUND_PlaySFXOffset(spuAddr32, ch, volL, volR, pitch, adsr, offset)
                {
                    if (hwCount < 7) break;
                    uint spuAddr = ArgU32(argsBytes, 0);
                    int ch = ArgU16(argsBytes, 2);
                    if ((uint)ch >= VoiceCount) break;

                    short volL = DecodeSpu2Vol(ArgU16(argsBytes, 3));
                    short volR = DecodeSpu2Vol(ArgU16(argsBytes, 4));
                    ushort pitch = ArgU16(argsBytes, 5);
                    ushort adsr = ArgU16(argsBytes, 6);
                    uint offsetBytes = (cmdId == 57 && hwCount >= 8) ? (uint)ArgU16(argsBytes, 7) : 0u;

                    ref Spu2Voice v = ref _voices[ch];
                    // SLUS_216.64 passes small channel/voice handle in args[0..1] after cmd=28 sets PendingStartAddr.
                    uint start = spuAddr >= InitialFreeSpuRamAddr ? spuAddr : v.PendingStartAddr;
                    start = (start + offsetBytes) & 0x001FFFF0u;
                    v.StartAddr = start;
                    v.LoopAddr = start;
                    v.CurrentAddr = start;
                    v.VolL = volL;
                    v.VolR = volR;
                    v.TargetVolL = volL;
                    v.TargetVolR = volR;
                    v.VolRampStep = 0x3FFF;
                    v.Pitch = pitch == 0 ? (ushort)0x1000 : pitch;
                    v.Adsr1 = adsr;
                    v.Hist1 = 0;
                    v.Hist2 = 0;
                    v.SamplePos = 0;
                    v.BlockLoaded = false;
                    v.Stopping = false;
                    v.Envelope = 1.0f;
                    v.Active = true;
                    break;
                }

                case 1: // SOUND_PlayStream(fileHandle32, slot_and_ch, volL, volR, pitch, mode, extra)
                {
                    if (hwCount < 7) break;
                    uint fileHandle = ArgU32(argsBytes, 0);
                    ushort packed = ArgU16(argsBytes, 2);
                    int slotIdx = (packed >> 8) & 0xFF;
                    int spuCh = packed & 0xFF;
                    short volL = DecodeSpu2Vol(ArgU16(argsBytes, 3));
                    short volR = DecodeSpu2Vol(ArgU16(argsBytes, 4));
                    ushort pitch = ArgU16(argsBytes, 5);
                    ushort mode = ArgU16(argsBytes, 6);
                    HandlePlayStreamCommand(ram, slotIdx, spuCh, fileHandle, volL, volR, pitch, mode);
                    break;
                }

                case 2: // SetVoiceVol(ch, volL, volR)
                {
                    if (hwCount < 3) break;
                    int ch = ArgU16(argsBytes, 0);
                    if ((uint)ch >= VoiceCount) break;
                    short volL = DecodeSpu2Vol(ArgU16(argsBytes, 1));
                    short volR = DecodeSpu2Vol(ArgU16(argsBytes, 2));
                    _voices[ch].VolL = volL;
                    _voices[ch].VolR = volR;
                    _voices[ch].TargetVolL = volL;
                    _voices[ch].TargetVolR = volR;
                    break;
                }

                case 3: // SetVoicePitch(ch, pitch)
                {
                    if (hwCount < 2) break;
                    int ch = ArgU16(argsBytes, 0);
                    if ((uint)ch >= VoiceCount) break;
                    _voices[ch].Pitch = ArgU16(argsBytes, 1);
                    break;
                }

                case 4:  // KeyOff(ch)
                case 40: // Release/Clear voice channel callback (ch as 32-bit int; NOT a stream slot!)
                {
                    if (hwCount < 1) break;
                    int ch = cmdId == 40 && hwCount >= 2
                        ? (int)ArgU32(argsBytes, 0)
                        : ArgU16(argsBytes, 0);
                    if ((uint)ch < VoiceCount && _voices[ch].Active)
                        _voices[ch].Stopping = true;
                    break;
                }

                case 5: // Stop / Close stream(slot)
                {
                    if (hwCount < 1) break;
                    int slotIdx = ArgU16(argsBytes, 0);
                    if ((uint)slotIdx < MaxStreamSlots && _streams[slotIdx].Active)
                    {
                        _streams[slotIdx].Active = false;
                        _streams[slotIdx].Finished = true;
                    }
                    break;
                }

                case 14: // SOUND_ResizeSPUBuffer(addr32)
                {
                    if (hwCount >= 2)
                        _freeSpuRamAddr = ArgU32(argsBytes, 0);
                    break;
                }

                case 16: // SOUND_SetMaxStreamLimit(limit)
                {
                    if (hwCount >= 1)
                        _maxStreamLimit = Math.Clamp((int)ArgU16(argsBytes, 0), 1, MaxStreamSlots);
                    break;
                }

                case 28: // SetVoiceStartAddr(ch32, spuAddr32)
                {
                    if (hwCount < 4) break;
                    uint ch = ArgU32(argsBytes, 0);
                    uint spuAddr = ArgU32(argsBytes, 2);
                    if (ch < VoiceCount)
                    {
                        _voices[ch].PendingStartAddr = spuAddr & 0x001FFFF0u;
                    }
                    break;
                }

                case 41: // SOUND_SetStreamSource(fileHandle32, offset32, size32)
                {
                    if (hwCount < 6) break;
                    _pendingFileHandle = ArgU32(argsBytes, 0);
                    _pendingFileOffset = ArgU32(argsBytes, 2);
                    _pendingFileSize = ArgU32(argsBytes, 4);
                    break;
                }

                case 43: // Link / SetStreamChannels or child association
                {
                    if (hwCount < 2) break;
                    short s = (short)ArgU16(argsBytes, 0);
                    if ((uint)s < MaxStreamSlots && hwCount >= 4)
                    {
                        _streams[s].SpuChLeft = Math.Clamp((int)ArgU16(argsBytes, 2), 0, VoiceCount - 1);
                        _streams[s].SpuChRight = Math.Clamp((int)ArgU16(argsBytes, 3), 0, VoiceCount - 1);
                    }
                    break;
                }

                case 44: // SOUND_SetStreamStereoChannel(slot_and_ch1, ch2, mode, volL, volR, extra32)
                {
                    if (hwCount < 2) break;
                    ushort packed = ArgU16(argsBytes, 0);
                    int slotIdx = (packed >> 8) & 0xFF;
                    int ch1 = packed & 0xFF;
                    int ch2 = ArgU16(argsBytes, 1) & 0xFF;
                    if ((uint)slotIdx < MaxStreamSlots)
                    {
                        if ((uint)ch1 < VoiceCount) _streams[slotIdx].SpuChLeft = ch1;
                        if ((uint)ch2 < VoiceCount)
                        {
                            _streams[slotIdx].SpuChRight = ch2;
                            if (ch2 != ch1 && _streams[slotIdx].Channels < 2)
                                _streams[slotIdx].Channels = 2;
                            if (hwCount >= 5)
                            {
                                short volL2 = DecodeSpu2Vol(ArgU16(argsBytes, 3));
                                short volR2 = DecodeSpu2Vol(ArgU16(argsBytes, 4));
                                _voices[ch2].VolL = volL2;
                                _voices[ch2].VolR = volR2;
                                _voices[ch2].TargetVolL = volL2;
                                _voices[ch2].TargetVolR = volR2;
                            }
                        }
                    }
                    break;
                }

                case 49: // SOUND_SetTransferEeAddr(eeAddr32)
                {
                    if (hwCount >= 2)
                        _pendingEeDstAddr = ArgU32(argsBytes, 0);
                    break;
                }

                case 70: // SOUND_TransferIOPToSPU(iopSrc32, sizeOrSpu32) or 2x16-bit voice/fx param
                case 72: // SOUND_TransferIOPToSPU2(...)
                {
                    if (hwCount >= 4)
                    {
                        uint iopAddr = ArgU32(argsBytes, 0);
                        uint sizeOrSpu = ArgU32(argsBytes, 2);
                        HandleIopToSpuTransferLocked(iopAddr, sizeOrSpu);
                    }
                    else if (hwCount >= 2)
                    {
                        uint iopAddr = ArgU16(argsBytes, 0);
                        uint sizeOrSpu = ArgU16(argsBytes, 1);
                        if (iopAddr >= 0x1000u && sizeOrSpu > 0)
                            HandleIopToSpuTransferLocked(iopAddr, sizeOrSpu);
                    }
                    break;
                }

                case 73: // SOUND_RegisterEeHeartbeat(eeAddr32)
                {
                    if (hwCount >= 2)
                    {
                        _heartbeatEeAddr = ArgU32(argsBytes, 0);
                        AdvanceHeartbeat(ram);
                    }
                    break;
                }

                case 77: // SOUND_InitPCMPlayBackNoStream(size32, mode16)
                {
                    if (hwCount >= 3)
                    {
                        ushort mode = ArgU16(argsBytes, 2);
                        _moviePcmChannels = mode == 0 ? 1 : 2;
                    }
                    break;
                }

                case 96: // SOUND_SetChannelVolumeSmooth(ch, volL, volR)
                {
                    if (hwCount < 3) break;
                    int ch = ArgU16(argsBytes, 0);
                    if ((uint)ch >= VoiceCount) break;
                    _voices[ch].TargetVolL = DecodeSpu2Vol(ArgU16(argsBytes, 1));
                    _voices[ch].TargetVolR = DecodeSpu2Vol(ArgU16(argsBytes, 2));
                    _voices[ch].VolRampStep = 256;
                    break;
                }

                case 99: // SetStreamEndOffset(slot, endOffset32)
                {
                    if (hwCount < 3) break;
                    int slotIdx = ArgU16(argsBytes, 0);
                    if ((uint)slotIdx < MaxStreamSlots)
                        _streams[slotIdx].EndOffsetBytes = ArgU32(argsBytes, 1);
                    break;
                }

                case 101: // SLUS_216.64 SOUND_DisableEffects(chStart, chEnd) when args < 48; legacy SetMasterVolume otherwise
                {
                    if (hwCount >= 2)
                    {
                        int a0 = ArgU16(argsBytes, 0);
                        int a1 = ArgU16(argsBytes, 1);
                        if (a0 < VoiceCount && a1 < VoiceCount && a0 <= a1)
                        {
                            for (int ch = a0; ch <= a1; ch++)
                                _voices[ch].EffectsEnabled = false;
                        }
                        else
                        {
                            _masterVolL = DecodeSpu2Vol((ushort)a0);
                            _masterVolR = DecodeSpu2Vol((ushort)a1);
                        }
                    }
                    break;
                }

                case 102: // SOUND_EnableEffects(chStart, chEnd)
                {
                    if (hwCount >= 2)
                    {
                        int c0 = ArgU16(argsBytes, 0);
                        int c1 = ArgU16(argsBytes, 1);
                        for (int ch = Math.Max(0, c0); ch <= Math.Min(VoiceCount - 1, c1); ch++)
                            _voices[ch].EffectsEnabled = true;
                    }
                    break;
                }
            }
        }
    }

    private void HandlePlayStreamCommand(byte* ram, int slotIdx, int spuCh, uint fileHandle, short volL, short volR, ushort pitch, ushort mode)
    {
        // Mode 126 with an active cmd=49 EE destination address is a synchronous/async
        // stream file -> EE RAM DMA read (used by 0x0042F4E8 to load 4 KB MIH stream headers).
        if (mode == 126 && _pendingEeDstAddr != 0)
        {
            if (ram != null && StreamFileReader != null && _pendingFileSize > 0)
            {
                uint eePhys = _pendingEeDstAddr & 0x01FFFFFFu;
                int maxCopy = (int)Math.Min(_pendingFileSize, (uint)Castaway.Core.CpuContext.RamSize - eePhys);
                if (maxCopy > 0)
                {
                    Span<byte> dst = new Span<byte>(ram + eePhys, maxCopy);
                    uint fh = fileHandle != 0 ? fileHandle : _pendingFileHandle;
                    int read = StreamFileReader(fh, _pendingFileOffset, dst);
                    if (read >= 20)
                    {
                        uint hdrSize = BinaryPrimitives.ReadUInt32LittleEndian(dst[0..4]);
                        uint ch = BinaryPrimitives.ReadUInt32LittleEndian(dst[8..12]);
                        uint sr = BinaryPrimitives.ReadUInt32LittleEndian(dst[12..16]);
                        uint interleave = BinaryPrimitives.ReadUInt32LittleEndian(dst[16..20]);
                        if (hdrSize == 64 && (ch == 1 || ch == 2) && sr >= 8000 && sr <= 48000)
                        {
                            _lastHeaderFileHandle = fh;
                            _lastHeaderOffset = _pendingFileOffset;
                            _lastHeaderChannels = (int)ch;
                            _lastHeaderSampleRate = (int)sr;
                            _lastHeaderInterleave = interleave > 0 ? (int)interleave : 0x4000;
                        }
                    }
                }
            }
            _pendingEeDstAddr = 0;
            return;
        }

        if ((uint)slotIdx >= MaxStreamSlots || _pendingFileSize == 0)
            return;

        uint activeFh = fileHandle != 0 ? fileHandle : _pendingFileHandle;
        MultiStreamSlot slot = _streams[slotIdx];
        slot.Reset();
        slot.FileHandle = activeFh;
        slot.BaseOffset = _pendingFileOffset;
        slot.TotalPayloadBytes = _pendingFileSize;
        slot.SpuChLeft = (uint)spuCh < VoiceCount ? spuCh : 46;
        slot.SpuChRight = slot.SpuChLeft + 1 < VoiceCount ? slot.SpuChLeft + 1 : slot.SpuChLeft;

        // Parse MIH header if cached from previous cmd=49 header read or located 64 bytes before payload
        if (_lastHeaderFileHandle == activeFh &&
            _pendingFileOffset >= _lastHeaderOffset &&
            _pendingFileOffset - _lastHeaderOffset <= 256)
        {
            slot.Channels = _lastHeaderChannels;
            slot.SampleRate = _lastHeaderSampleRate;
            slot.Interleave = _lastHeaderInterleave;
        }
        else if (StreamFileReader != null && _pendingFileOffset >= 64)
        {
            Span<byte> hdr = stackalloc byte[20];
            if (StreamFileReader(activeFh, _pendingFileOffset - 64, hdr) == 20 &&
                BinaryPrimitives.ReadUInt32LittleEndian(hdr[0..4]) == 64)
            {
                uint ch = BinaryPrimitives.ReadUInt32LittleEndian(hdr[8..12]);
                uint sr = BinaryPrimitives.ReadUInt32LittleEndian(hdr[12..16]);
                uint interleave = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..20]);
                if ((ch == 1 || ch == 2) && sr >= 8000 && sr <= 48000)
                {
                    slot.Channels = (int)ch;
                    slot.SampleRate = (int)sr;
                    slot.Interleave = interleave > 0 ? (int)interleave : 0x4000;
                }
            }
        }
        else if (pitch > 0)
        {
            int srFromPitch = (int)Math.Clamp((long)pitch * OutputSampleRate / 0x1000L, 8000, 48000);
            slot.SampleRate = srFromPitch;
        }

        _voices[slot.SpuChLeft].VolL = volL;
        _voices[slot.SpuChLeft].VolR = volR;
        _voices[slot.SpuChLeft].TargetVolL = volL;
        _voices[slot.SpuChLeft].TargetVolR = volR;
        if (slot.Channels >= 2)
        {
            _voices[slot.SpuChRight].VolL = 0;
            _voices[slot.SpuChRight].VolR = volR != 0 ? volR : volL;
            _voices[slot.SpuChRight].TargetVolL = 0;
            _voices[slot.SpuChRight].TargetVolR = _voices[slot.SpuChRight].VolR;
        }

        slot.Active = true;
    }

    private void HandleIopToSpuTransferLocked(uint iopAddr, uint sizeOrSpuAddr)
    {
        if (iopAddr == 0 || sizeOrSpuAddr == 0)
            return;

        uint physIop = iopAddr & 0x007FFFFFu;
        int len = (int)Math.Min(sizeOrSpuAddr, (uint)IopRamSize - physIop);
        if (len <= 0)
            return;

        uint spuDst = _freeSpuRamAddr & 0x001FFFF0u;
        int spuCopy = (int)Math.Min((uint)len, (uint)Spu2RamSize - spuDst);
        if (spuCopy > 0)
        {
            _iopRam.AsSpan((int)physIop, spuCopy).CopyTo(_spu2Ram.AsSpan((int)spuDst, spuCopy));
            _freeSpuRamAddr = Math.Min((uint)Spu2RamSize, spuDst + ((uint)(spuCopy + 15) & ~15u));
        }
    }

    public void Dispose()
    {
        _stopMixerThread = true;
        if (_mixerThread != null && _mixerThread.IsAlive)
        {
            _mixerThread.Join(300);
        }

        if (_openAlAvailable && _al != null && _alc != null)
        {
            try
            {
                _alc.MakeContextCurrent(_context);
                if (_streamingSource != 0)
                    _al.DeleteSource(_streamingSource);
                _alc.MakeContextCurrent(null);
                if (_context != null)
                    _alc.DestroyContext(_context);
                if (_device != null)
                    _alc.CloseDevice(_device);
            }
            catch
            {
                // Ignore shutdown errors
            }
            _openAlAvailable = false;
        }

        if (_winMmAvailable && _waveOutHandle != 0)
        {
            try
            {
                WaveOutClose(_waveOutHandle);
            }
            catch
            {
                // Ignore shutdown errors
            }
            _waveOutHandle = 0;
            _winMmAvailable = false;
        }
    }
}
