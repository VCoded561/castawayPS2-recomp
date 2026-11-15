using Castaway.ElfTools.Commands;

namespace Castaway.ElfTools;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string verb = args[0].ToLowerInvariant();
        string[] verbArgs = args.Skip(1).ToArray();

        try
        {
            return verb switch
            {
                "inspect-func" or "inspect" => InspectFuncCommand.Run(verbArgs),
                "find-xrefs" or "xrefs" => FindXrefsCommand.Run(verbArgs),
                "scan-tables" or "tables" => ScanTablesCommand.Run(verbArgs),
                "trace-diff" or "diff" => TraceDiffCommand.Run(verbArgs),
                "scan-vu1" or "extract-vu1" or "vu1" => VifVu1ScannerCommand.Run(verbArgs),
                "aot-vu1" => Vu1AotCommand.Run(verbArgs),
                "call-tree" or "tree" => CallTreeCommand.Run(verbArgs),
                "slice-flow" or "slice" => SliceFlowCommand.Run(verbArgs),
                "dump-mem" => DumpMem(verbArgs),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => HandleUnknownVerb(verb)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[ElfTools Error] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"Castaway.ElfTools — PS2 SLUS_216.64 Zero-Ghidra Static Inspection Suite

Usage:
  dotnet run --project Tools/Castaway.ElfTools -- <command> [options]

Commands:
  inspect-func  Disassemble R5900/MMI/COP1/VU0 at address alongside emitted C#
                --addr <hex> [--count 45] [--with-csharp] [--max-lines 80]
  find-xrefs    Scan for 32-bit addresses, LUI references, strings, or floats
                [--string <str>] [--target <hex>] [--float <val>] [--imm <hex>]
  scan-tables   Recover jump tables, function bounds, and inventory EE syscalls
                [--out <toml>] [--report-syscalls]
  trace-diff    Compare binary ring-buffer traces (PCSX2 vs Castaway.Host)
                --pcsx2 <trace.bin> --host <trace.bin>
  scan-vu1      Extract and hash embedded VU1 microprograms to JSON manifest
                [--out <json>]
  call-tree     Walk N-level caller/callee hierarchy and summarize strings/syscalls
                --addr <hex> [--depth 3] [--up]
  slice-flow    Track pointer register ($a0) across functions to infer struct fields
                --addr <hex> [--reg a0] [--depth 3]
");
        return 0;
    }

    private static int DumpMem(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: dump-mem <addr> [count]");
            return 1;
        }
        uint addr = InspectFuncCommand.ParseHex(args[0]);
        int count = args.Length > 1 && int.TryParse(args[1], out int c) ? c : 16;
        var r = new Castaway.ElfTools.Elf.Ps2ElfReader(Castaway.ElfTools.Elf.Ps2ElfReader.FindDefaultElfPath());
        for (int i = 0; i < count; i++)
        {
            uint cur = addr + (uint)(i * 4);
            if (!r.IsVAddrValid(cur)) break;
            uint val = r.ReadUInt32(cur);
            Console.WriteLine($"+0x{i * 4:X2} (0x{cur:X8}): 0x{val:X8}");
        }
        return 0;
    }

    private static int HandleUnknownVerb(string verb)
    {
        Console.Error.WriteLine($"Unknown command: '{verb}'. Run with --help for available commands.");
        return 1;
    }
}
