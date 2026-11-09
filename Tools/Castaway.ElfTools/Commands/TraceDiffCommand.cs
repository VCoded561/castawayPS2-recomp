using System.Runtime.InteropServices;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TraceRecord
{
    public uint GuestPc;
    public uint EventType;
    public uint GprCrc32;
    public uint Cop1Crc32;

    public string EventTypeName => EventType switch
    {
        0 => "FuncEnter",
        1 => "FuncExit",
        2 => "Syscall",
        3 => "DmaKick",
        _ => $"Event({EventType})"
    };

    public override string ToString() =>
        $"PC: 0x{GuestPc:X8} | {EventTypeName,-9} | GPR_CRC: 0x{GprCrc32:X8} | COP1_CRC: 0x{Cop1Crc32:X8}";
}

public static class TraceDiffCommand
{
    public static int Run(string[] args)
    {
        string? pcsx2Path = null;
        string? hostPath = null;
        string? elfPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "--pcsx2" || arg == "-p") && i + 1 < args.Length)
            {
                pcsx2Path = args[++i];
            }
            else if ((arg == "--host" || arg == "-h") && i + 1 < args.Length)
            {
                hostPath = args[++i];
            }
            else if (arg == "--elf" && i + 1 < args.Length)
            {
                elfPath = args[++i];
            }
        }

        if (string.IsNullOrEmpty(pcsx2Path) || string.IsNullOrEmpty(hostPath))
        {
            Console.WriteLine("Error: Missing --pcsx2 <path> and/or --host <path>.");
            return 1;
        }

        if (!File.Exists(pcsx2Path))
        {
            Console.WriteLine($"Error: File not found: {pcsx2Path}");
            return 1;
        }

        if (!File.Exists(hostPath))
        {
            Console.WriteLine($"Error: File not found: {hostPath}");
            return 1;
        }

        return CompareTraces(pcsx2Path, hostPath, elfPath);
    }

    public static int CompareTraces(string pcsx2Path, string hostPath, string? elfPath = null)
    {
        int recordSize = Marshal.SizeOf<TraceRecord>();
        byte[] bufPcsx2 = new byte[recordSize * 4096];
        byte[] bufHost = new byte[recordSize * 4096];

        using var fsPcsx2 = new FileStream(pcsx2Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fsHost = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        long totalRecordsPcsx2 = fsPcsx2.Length / recordSize;
        long totalRecordsHost = fsHost.Length / recordSize;

        Console.WriteLine($"Comparing traces: PCSX2 ({totalRecordsPcsx2} records) vs Host ({totalRecordsHost} records)...");

        // Maintain ring buffer of last 16 records
        Queue<TraceRecord> history = new(16);
        long recordIndex = 0;

        while (true)
        {
            int readPcsx2 = fsPcsx2.Read(bufPcsx2, 0, bufPcsx2.Length);
            int readHost = fsHost.Read(bufHost, 0, bufHost.Length);

            if (readPcsx2 == 0 && readHost == 0)
            {
                Console.WriteLine($"[PASS] Traces match 100%! Evaluated {recordIndex} records without divergence.");
                return 0;
            }

            int countPcsx2 = readPcsx2 / recordSize;
            int countHost = readHost / recordSize;
            int minCount = Math.Min(countPcsx2, countHost);

            for (int i = 0; i < minCount; i++)
            {
                var recP = MemoryMarshal.Read<TraceRecord>(bufPcsx2.AsSpan(i * recordSize, recordSize));
                var recH = MemoryMarshal.Read<TraceRecord>(bufHost.AsSpan(i * recordSize, recordSize));

                if (recP.GuestPc != recH.GuestPc ||
                    recP.EventType != recH.EventType ||
                    recP.GprCrc32 != recH.GprCrc32 ||
                    recP.Cop1Crc32 != recH.Cop1Crc32)
                {
                    // Divergence found!
                    Console.WriteLine();
                    Console.WriteLine($"================ DIVERGENCE DETECTED AT RECORD #{recordIndex} ================");
                    Console.WriteLine("Recent matching history (last 10 records):");
                    var historyList = history.ToList();
                    int startIdx = Math.Max(0, historyList.Count - 10);
                    for (int h = startIdx; h < historyList.Count; h++)
                    {
                        Console.WriteLine($"  [#{recordIndex - (historyList.Count - h)}] {historyList[h]}");
                    }

                    Console.WriteLine();
                    Console.WriteLine("Diverging record:");
                    Console.WriteLine($"  PCSX2: {recP}");
                    Console.WriteLine($"  Host:  {recH}");

                    string reason = "";
                    if (recP.GuestPc != recH.GuestPc) reason = "PC Mismatch (Branch / Jump divergence)";
                    else if (recP.GprCrc32 != recH.GprCrc32) reason = "GPR State Divergence (ALU / Load / Math mismatch)";
                    else if (recP.Cop1Crc32 != recH.Cop1Crc32) reason = "COP1 State Divergence (FPU calculation / rounding / NaN clamp)";
                    else reason = "Event Type Mismatch";

                    Console.WriteLine($"Reason: {reason}");
                    Console.WriteLine();

                    // Automatically invoke InspectFunc on the diverging PC
                    uint badPc = recP.GuestPc;
                    Console.WriteLine($"--- Disassembly of diverging function at 0x{badPc:X8} ---");
                    try
                    {
                        InspectFuncCommand.Run(new[] { "--addr", $"0x{badPc:X8}", "--count", "30", "--with-csharp" });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Could not disassemble 0x{badPc:X8}: {ex.Message}]");
                    }

                    return 2;
                }

                if (history.Count >= 16) history.Dequeue();
                history.Enqueue(recP);
                recordIndex++;
            }

            if (readPcsx2 != readHost)
            {
                Console.WriteLine($"[FAIL] Trace length truncated: PCSX2 read {readPcsx2} bytes, Host read {readHost} bytes at record #{recordIndex}.");
                return 3;
            }
        }
    }
}
