using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Castaway.Core;
using Castaway.ElfTools.Commands;
using Castaway.ElfTools.Elf;
using Castaway.Generated;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Castaway.ElfTools.Tests;

public class ElfToolsTests
{
    [Fact]
    public void ElfReader_LoadsSlus21664_Correctly()
    {
        string path = Ps2ElfReader.FindDefaultElfPath();
        var reader = new Ps2ElfReader(path);

        Assert.Equal(0x00100008u, reader.EntryPoint);
        Assert.NotNull(reader.TextSection);
        Assert.Equal(0x00100000u, reader.TextSection.VAddr);
        Assert.True(reader.TextSection.Size > 0x300000);
        Assert.NotNull(reader.RodataSection);
        Assert.NotNull(reader.DataSection);
        Assert.NotNull(reader.BssSection);

        // Verify first instruction at 0x00100008 is lui $v0, 0x0051 (0x3C020051)
        uint firstWord = reader.ReadUInt32(0x00100008);
        Assert.Equal(0x3C020051u, firstWord);

    }

    [Fact]
    public void R5900Decoder_DecodesBasicAndMmiInstructions()
    {
        // 0x3C020051: lui $v0, 0x0051
        var insn1 = R5900Decoder.Decode(0x3C020051u, 0x00100008);
        Assert.Equal(R5900Op.Lui, insn1.Op);
        Assert.Equal(2u, insn1.Rt);
        Assert.Equal(0x0051u, insn1.Uimm);

        // 0x7C400000: sq $zero, 0($v0) -> MMI 128-bit store
        var insn2 = R5900Decoder.Decode(0x7C400000u, 0x00100020);
        Assert.Equal(R5900Op.Sq, insn2.Op);
        Assert.True(insn2.IsStore);

        // 0x00000000: nop
        var insn3 = R5900Decoder.Decode(0x00000000u, 0x00100000);
        Assert.Equal("nop", insn3.ToString());

        // 0x0000000C: syscall
        var insn4 = R5900Decoder.Decode(0x0000000Cu, 0x00100060);
        Assert.Equal(R5900Op.Syscall, insn4.Op);

        // 0x03E00008: jr $ra
        var insn5 = R5900Decoder.Decode(0x03E00008u, 0x00100080);
        Assert.Equal(R5900Op.Jr, insn5.Op);
        Assert.True(insn5.IsReturn);
        Assert.True(insn5.HasDelaySlot);
    }

    [Fact]
    public void TraceDiff_DetectsSynthetic1BitCrcMismatch()
    {
        string tempPcsx2 = Path.GetTempFileName();
        string tempHost = Path.GetTempFileName();

        try
        {
            var recordsPcsx2 = new List<TraceRecord>();
            var recordsHost = new List<TraceRecord>();

            // 5 matching records
            for (uint i = 0; i < 5; i++)
            {
                var r = new TraceRecord
                {
                    GuestPc = 0x00100008 + i * 4,
                    EventType = 0,
                    GprCrc32 = 0x12345678 + i,
                    Cop1Crc32 = 0x9ABCDEF0 + i
                };
                recordsPcsx2.Add(r);
                recordsHost.Add(r);
            }

            // Record 5 matches, Record 6 has 1-bit mismatch in GPR CRC
            var mismatchPcsx2 = new TraceRecord
            {
                GuestPc = 0x00100020,
                EventType = 0,
                GprCrc32 = 0xAAAAAAAA,
                Cop1Crc32 = 0x55555555
            };
            var mismatchHost = new TraceRecord
            {
                GuestPc = 0x00100020,
                EventType = 0,
                GprCrc32 = 0xAAAAAAAA ^ 0x00000001, // 1-bit flip
                Cop1Crc32 = 0x55555555
            };
            recordsPcsx2.Add(mismatchPcsx2);
            recordsHost.Add(mismatchHost);

            int size = Marshal.SizeOf<TraceRecord>();
            byte[] bytesPcsx2 = new byte[recordsPcsx2.Count * size];
            byte[] bytesHost = new byte[recordsHost.Count * size];

            for (int i = 0; i < recordsPcsx2.Count; i++)
            {
                var rP = recordsPcsx2[i];
                var rH = recordsHost[i];
                MemoryMarshal.Write(bytesPcsx2.AsSpan(i * size, size), in rP);
                MemoryMarshal.Write(bytesHost.AsSpan(i * size, size), in rH);
            }

            File.WriteAllBytes(tempPcsx2, bytesPcsx2);
            File.WriteAllBytes(tempHost, bytesHost);

            int exitCode = TraceDiffCommand.CompareTraces(tempPcsx2, tempHost);
            Assert.Equal(2, exitCode); // Divergence code 2
        }
        finally
        {
            if (File.Exists(tempPcsx2)) File.Delete(tempPcsx2);
            if (File.Exists(tempHost)) File.Delete(tempHost);
        }
    }

    [Fact]
    public void CallTree_ExecutesWithoutInfiniteLoops()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        int result = CallTreeCommand.Run(new[] { "--addr", "0x00100008", "--depth", "3", "--elf", elfPath });
        Assert.Equal(0, result);
    }

    [Fact]
    public void SliceFlow_ExecutesAndInferTypes()
    {
        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        int result = SliceFlowCommand.Run(new[] { "--addr", "0x00100700", "--reg", "a0", "--depth", "2", "--elf", elfPath });
        Assert.Equal(0, result);
    }

    [Fact]
    public void CastawayFunctionsToml_IsFullyGeneratedAndValid()
    {
        string? tomlPath = null;
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "castaway_functions.toml");
            if (File.Exists(candidate))
            {
                tomlPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(tomlPath);
        var info = new FileInfo(tomlPath);
        Assert.True(info.Length > 1_000_000, "castaway_functions.toml should be comprehensive (>1MB)");

        bool foundEntryPoint = false;
        bool foundJumpTables = false;
        bool foundSyscalls = false;

        foreach (var line in File.ReadLines(tomlPath))
        {
            if (line.Contains("start = \"0x00100008\"")) foundEntryPoint = true;
            if (line.Contains("[[jump_tables]]")) foundJumpTables = true;
            if (line.Contains("[[syscalls]]")) foundSyscalls = true;
            if (foundEntryPoint && foundJumpTables && foundSyscalls) break;
        }

        Assert.True(foundEntryPoint, "Must find ELF entry point 0x00100008");
        Assert.True(foundJumpTables, "Must contain jump tables");
        Assert.True(foundSyscalls, "Must contain syscalls");
    }

    [Fact]
    public unsafe void CpuContext_HardwiresZeroRegisterAndSupports128BitMmi()
    {
        CpuContext ctx = default;
        ctx.Initialize();

        // Writing to $zero (GPR[0]) via any width must be ignored
        ctx.SetS32(0, -12345);
        ctx.SetU64(0, 0xDEADBEEFCAFEBABEUL);
        ctx.SetVec(0, Vector128.Create(1u, 2u, 3u, 4u));
        Assert.Equal(0u, ctx.GetU32(0));
        Assert.Equal(0UL, ctx.GetU64(0));
        Assert.Equal(Vector128<uint>.Zero, ctx.GetVec(0));

        // 32-bit sign extension into 64-bit lower doubleword (MIPS III / R5900 rule)
        ctx.SetS32(2, -1);
        Assert.Equal(0xFFFFFFFFu, ctx.GetU32(2));
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, ctx.GetU64(2));

        // 128-bit MMI packed add word (PADDW)
        var v1 = Vector128.Create(10u, 20u, 30u, 40u);
        var v2 = Vector128.Create(1u, 2u, 3u, 4u);
        ctx.SetVec(4, v1);
        ctx.SetVec(5, v2);
        ctx.SetVec(6, Ps2Mmi.Paddw(ctx.GetVec(4), ctx.GetVec(5)));
        Assert.Equal(Vector128.Create(11u, 22u, 33u, 44u), ctx.GetVec(6));
    }

    [Fact]
    public void Ps2Float_ClampsNaNInfinityAndTruncatesTowardZero()
    {
        // PS2 clamps IEEE +Infinity (0x7F800000) and NaN (0x7FC00000) to 0x7FFFFFFF
        Assert.Equal(0x7FFFFFFFu, Ps2Float.ClampBits(0x7F800000u));
        Assert.Equal(0x7FFFFFFFu, Ps2Float.ClampBits(0x7FC00001u));
        Assert.Equal(0xFFFFFFFFu, Ps2Float.ClampBits(0xFF800000u));

        // Denormals (exponent == 0) flush to signed zero
        Assert.Equal(0x00000000u, Ps2Float.ClampBits(0x00000001u));
        Assert.Equal(0x80000000u, Ps2Float.ClampBits(0x80400000u));

        // CVT.W.S always rounds toward zero (MathF.Truncate)
        Assert.Equal(1, Ps2Float.CvtWS(1.9f));
        Assert.Equal(-1, Ps2Float.CvtWS(-1.9f));
        Assert.Equal(int.MaxValue, Ps2Float.CvtWS(1e30f));
        Assert.Equal(int.MinValue, Ps2Float.CvtWS(-1e30f));
    }

    [Fact]
    public unsafe void Ps2Memory_UnalignedLoadStoreAndScratchpad_AreBitExact()
    {
        byte[] ramManaged = new byte[1024];
        byte[] scratchManaged = new byte[CpuContext.ScratchpadSize];

        fixed (byte* ram = ramManaged)
        fixed (byte* sp = scratchManaged)
        {
            CpuContext ctx = default;
            ctx.Initialize(sp);

            // 1. Scratchpad fast-path (0x70000000..0x70003FFF)
            Ps2Memory.Write32(&ctx, ram, 0x70000100u, 0xCAFEBABEu);
            Assert.Equal(0xCAFEBABEu, Ps2Memory.Read32(&ctx, ram, 0x70000100u));
            Assert.Equal(0u, Ps2Memory.Read32(&ctx, ram, 0x00000100u));

            // 2. Unaligned 32-bit SWL + SWR followed by LWL + LWR at every byte alignment (1, 2, 3)
            for (uint align = 0; align < 4; align++)
            {
                uint addr = 0x100u + align;
                uint testWord = 0x11223344u;
                ctx.SetU32(8, testWord);

                // MIPS unaligned store sequence: SWL rt, 3(addr); SWR rt, 0(addr)
                Ps2Memory.StoreWordLeft(&ctx, ram, 8, addr + 3u);
                Ps2Memory.StoreWordRight(&ctx, ram, 8, addr);

                // MIPS unaligned load sequence: LWL rt, 3(addr); LWR rt, 0(addr)
                ctx.SetU64(9, 0xAAAAAAAAAAAAAAAAUL);
                Ps2Memory.LoadWordLeft(&ctx, ram, 9, addr + 3u);
                Ps2Memory.LoadWordRight(&ctx, ram, 9, addr);

                Assert.Equal(testWord, ctx.GetU32(9));
            }

            // 3. Unaligned 64-bit SDL + SDR followed by LDL + LDR at every byte alignment (0..7)
            for (uint align = 0; align < 8; align++)
            {
                uint addr = 0x200u + align;
                ulong testDword = 0x0123456789ABCDEFUL;
                ctx.SetU64(10, testDword);

                // MIPS unaligned 64-bit store sequence: SDL rt, 7(addr); SDR rt, 0(addr)
                Ps2Memory.StoreDoubleLeft(&ctx, ram, 10, addr + 7u);
                Ps2Memory.StoreDoubleRight(&ctx, ram, 10, addr);

                // MIPS unaligned 64-bit load sequence: LDL rt, 7(addr); LDR rt, 0(addr)
                ctx.SetU64(11, 0x5555555555555555UL);
                Ps2Memory.LoadDoubleLeft(&ctx, ram, 11, addr + 7u);
                Ps2Memory.LoadDoubleRight(&ctx, ram, 11, addr);

                Assert.Equal(testDword, ctx.GetU64(11));
            }
        }
    }

    [Fact]
    public unsafe void RecompiledFunctions_RegistersAllAndInspectFuncShowsCSharp()
    {
        RecompiledFunctions.RegisterAll();
        Assert.Equal(14213, RecompiledFunctions.TotalFunctionCount);
        Assert.True(RuntimeDispatcher.Lookup(0x00100008u) != null);

        string elfPath = Ps2ElfReader.FindDefaultElfPath();
        int res = InspectFuncCommand.Run(new[] { "--addr", "0x00100008", "--count", "20", "--with-csharp", "--elf", elfPath });
        Assert.Equal(0, res);
    }
}
