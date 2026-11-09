using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Castaway.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BinaryTraceRecord
{
    public uint GuestPc;
    public uint EventType; // 0 = FuncEnter, 1 = FuncExit, 2 = Syscall, 3 = DmaKick
    public uint GprCrc32;
    public uint Cop1Crc32;
}

/// <summary>
/// Phase 5 high-speed binary trace recorder writing packed 16-byte records
/// ([uint GuestPc, uint EventType, uint GprCrc32, uint Cop1Crc32]) compatible with
/// Castaway.ElfTools trace-diff.
/// </summary>
public sealed unsafe class BinaryTraceRecorder : IDisposable
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly FileStream _stream;
    private readonly long _maxRecords;
    private readonly object _sync = new();
    private readonly byte[] _buffer = new byte[16 * 4096];
    private int _bufferPos;
    private long _recordCount;
    private bool _disposed;

    public string FilePath { get; }
    public long RecordCount => Interlocked.Read(ref _recordCount);

    public BinaryTraceRecorder(string filePath, long maxRecords = 500_000)
    {
        FilePath = filePath;
        _maxRecords = maxRecords;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
    }

    public void Attach()
    {
        RuntimeDispatcher.SetTraceHandler(OnTraceEvent);
    }

    public void Detach()
    {
        if (RuntimeDispatcher.TraceHandler == OnTraceEvent)
        {
            RuntimeDispatcher.SetTraceHandler(null);
        }
        Flush();
    }

    public void OnTraceEvent(uint guestPc, uint eventType, CpuContext* ctx)
    {
        if (_disposed) return;
        long current = Interlocked.Read(ref _recordCount);
        if (_maxRecords > 0 && current >= _maxRecords) return;

        // Ensure GPR[0] is zeroed before hashing
        ctx->GPR[0] = default;

        var gprSpan = new ReadOnlySpan<byte>(&ctx->GPR, 32 * 16);
        var cop1Span = new ReadOnlySpan<byte>(&ctx->F, 32 * 4);

        var rec = new BinaryTraceRecord
        {
            GuestPc = guestPc,
            EventType = eventType,
            GprCrc32 = ComputeCrc32(gprSpan),
            Cop1Crc32 = ComputeCrc32(cop1Span)
        };

        lock (_sync)
        {
            if (_disposed || (_maxRecords > 0 && _recordCount >= _maxRecords))
                return;

            MemoryMarshal.Write(_buffer.AsSpan(_bufferPos, 16), in rec);
            _bufferPos += 16;
            _recordCount++;

            if (_bufferPos >= _buffer.Length)
            {
                _stream.Write(_buffer, 0, _bufferPos);
                _bufferPos = 0;
            }
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_bufferPos > 0)
            {
                _stream.Write(_buffer, 0, _bufferPos);
                _bufferPos = 0;
            }
            _stream.Flush();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        uint[] table = CrcTable;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte index = (byte)((crc & 0xFFu) ^ bytes[i]);
            crc = (crc >> 8) ^ table[index];
        }
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        const uint polynomial = 0xEDB88320u;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 8; j > 0; j--)
            {
                if ((crc & 1u) != 0)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        lock (_sync)
        {
            _disposed = true;
            _stream.Dispose();
        }
    }
}
