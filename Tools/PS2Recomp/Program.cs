using System.Diagnostics;
using Castaway.ElfTools.Elf;

namespace PS2Recomp;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            string? elfPath = null;
            string? tomlPath = null;
            string? outDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--elf" when i + 1 < args.Length:
                        elfPath = args[++i];
                        break;
                    case "--toml" when i + 1 < args.Length:
                        tomlPath = args[++i];
                        break;
                    case "--out" when i + 1 < args.Length:
                        outDir = args[++i];
                        break;
                }
            }

            elfPath = Ps2ElfReader.FindDefaultElfPath(elfPath);
            string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(elfPath)!, "..", ".."));
            tomlPath ??= Path.Combine(repoRoot, "castaway_functions.toml");
            outDir ??= Path.Combine(repoRoot, "src", "Castaway.Generated");

            var sw = Stopwatch.StartNew();
            Console.WriteLine($"[PS2Recomp] Loading ELF: {elfPath}");
            Console.WriteLine($"[PS2Recomp] Loading Function Map: {tomlPath}");

            var elf = new Ps2ElfReader(elfPath);
            var generator = new CSharpGenerator(elf, tomlPath);

            Console.WriteLine($"[PS2Recomp] Parsed {generator.Functions.Count} functions and {generator.JumpTables.Count} jump tables.");
            Console.WriteLine($"[PS2Recomp] Emitting C# shards to: {outDir}");

            int shardCount = generator.GenerateAll(outDir);
            sw.Stop();

            Console.WriteLine($"[PS2Recomp] Emitted {generator.Functions.Count} functions across {shardCount} shards + FunctionTable.cs in {sw.ElapsedMilliseconds} ms.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[PS2Recomp Error] {ex}");
            Console.ResetColor();
            return 1;
        }
    }
}
