using System.Buffers.Binary;
using Silk.NET.OpenAL;

namespace Castaway.Runtime;

public sealed unsafe class Spu2AudioService : IDisposable
{
    public const int Spu2RamSize = 2 * 1024 * 1024; // 2 MB SPU2 RAM
    public const int AdpcmBlockBytes = 16;
    public const int AdpcmSamplesPerBlock = 28;

    // Sony PS1/PS2 SPU2 ADPCM IIR filter coefficients (scaled by 64)
    private static readonly int[] FilterPos = [0, 60, 115, 98, 122];
    private static readonly int[] FilterNeg = [0, 0, -52, -55, -60];

    private readonly byte[] _spu2Ram = new byte[Spu2RamSize];
    private readonly Dictionary<int, ushort> _regs = new();
    private readonly Dictionary<int, uint> _addrs = new();

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _streamingSource;
    private bool _openAlAvailable;

    public bool IsOpenAlInitialized => _openAlAvailable;
    public byte[] Spu2Ram => _spu2Ram;

    public Spu2AudioService(bool tryInitOpenAl = true)
    {
        if (tryInitOpenAl)
        {
            TryInitializeOpenAl();
        }
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
            _openAlAvailable = true;
        }
        catch
        {
            _openAlAvailable = false;
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
                // Sign-extend 4-bit nibble to 16-bit and shift
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

    public void QueuePcmMono16(ReadOnlySpan<short> samples, int sampleRate = 48000)
    {
        if (!_openAlAvailable || _al == null || samples.IsEmpty)
            return;

        try
        {
            // Unqueue processed buffers first
            _al.GetSourceProperty(_streamingSource, GetSourceInteger.BuffersProcessed, out int processed);
            while (processed-- > 0)
            {
                uint oldBuf = 0;
                _al.SourceUnqueueBuffers(_streamingSource, 1, &oldBuf);
                if (oldBuf != 0)
                    _al.DeleteBuffer(oldBuf);
            }

            uint buffer = _al.GenBuffer();
            fixed (short* ptr = samples)
            {
                _al.BufferData(buffer, BufferFormat.Mono16, ptr, samples.Length * sizeof(short), sampleRate);
            }
            _al.SourceQueueBuffers(_streamingSource, 1, &buffer);

            _al.GetSourceProperty(_streamingSource, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state != SourceState.Playing)
            {
                _al.SourcePlay(_streamingSource);
            }
        }
        catch
        {
            // Ignore transient audio device errors in headless/CI environments
        }
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
                    ret = (int)_addrs.GetValueOrDefault(entry, 0x00005010u);
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
                        new ReadOnlySpan<byte>(ram + phys, copyLen).CopyTo(_spu2Ram.AsSpan((int)spuAddr, copyLen));
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

    private uint _eaStreamTick;

    /// <summary>
    /// Handles EA Audio Stream IRX RPC (SID 0x00012345, bound at 0x00464848, called at 0x0046703C).
    /// </summary>
    public int HandleEaStreamRpc(uint rpcNum, byte* ram, uint sendBufAddr, int sendSize, uint recvBufAddr, int recvSize)
    {
        if (recvBufAddr != 0 && recvSize >= 8)
        {
            uint recvPhys = recvBufAddr & 0x01FFFFFFu;
            Span<byte> recv = new Span<byte>(ram + recvPhys, Math.Max(8, recvSize));
            if (sendBufAddr != 0 && sendSize > 0 && sendSize <= recvSize)
            {
                // Preserve shared command/status header if sendBuf != recvBuf
                uint sendPhys = sendBufAddr & 0x01FFFFFFu;
                if (sendPhys != recvPhys)
                {
                    new ReadOnlySpan<byte>(ram + sendPhys, Math.Min(sendSize, recvSize)).CopyTo(recv);
                }
            }

            // 0x00466488 (the RPC endFunc callback) reads:
            //   a3 = *(int*)(recv + 0)
            //   v0 = *(uint*)(recv + (a3 * 9 + 1) * 4)
            // and compares v0 with *(uint*)0x004A469C, skipping the entire callback if equal!
            // Set a3 = 0 and advance v0 (recv[4..8]) on every RPC so 0x00466488 always runs.
            uint tick = ++_eaStreamTick;
            if (tick == 0)
                tick = ++_eaStreamTick;
            BinaryPrimitives.WriteInt32LittleEndian(recv[0..4], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(recv[4..8], tick);
        }

        // Also transition any pending EA audio stream state slots at 0x005AAF48..0x005AAF77
        // from pending/starting (1, 2, 4, 5) to active/ready (0) so SOUND_GetStreamInfo (0x00468558)
        // reports 17($sp) == 0 immediately instead of spinning 30x in DelayThread(33333) at 0x0042F620.
        if (ram != null)
        {
            const uint streamStateBase = 0x005AAF48u;
            for (uint i = 0; i < 48; i++)
            {
                byte st = ram[streamStateBase + i];
                if (st == 1 || st == 2 || st == 4 || st == 5)
                {
                    ram[streamStateBase + i] = 0;
                }
            }
        }

        return 0;
    }

    public void Dispose()
    {
        if (_openAlAvailable && _al != null && _alc != null)
        {
            try
            {
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
    }
}
