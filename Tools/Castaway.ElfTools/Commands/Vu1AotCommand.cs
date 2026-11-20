using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Castaway.ElfTools.Elf;

namespace Castaway.ElfTools.Commands;

public static class Vu1AotCommand
{
    private enum UpperOp : byte
    {
        Nop = 0,
        AddBc, SubBc, MaddBc, MsubBc, MaxBc, MinBc, MulBc,
        MulQ, MaxI, MulI, MinI,
        AddQ, MaddQ, AddI, MaddI, SubQ, MsubQ, SubI, MsubI,
        Add, Madd, Mul, Max, Sub, Msub, Opmsub, Min,
        AddABc, SubABc, MaddABc, MsubABc, MulABc,
        Itof0, Itof4, Itof12, Itof15,
        Ftoi0, Ftoi4, Ftoi12, Ftoi15,
        MulAq, Abs, MulAi, Clip,
        AddAq, MaddAq, AddAi, MaddAi, SubAq, MsubAq, SubAi, MsubAi,
        AddA, MaddA, MulA, SubA, MsubA, Opmula,
        Unknown
    }

    private enum LowerOp : byte
    {
        Nop = 0,
        Lq, Sq, Ilw, Isw, Iaddiu, Isubiu,
        Fceq, Fcset, Fcand, Fcor, Fsset, Fsand, Fsor, Fmeq, Fmand, Fmor, Fcget,
        Iadd, Isub, Iaddi, Iand, Ior,
        Move, Mr32, Lqi, Sqi, Lqd, Sqd,
        Div, Sqrt, Rsqrt, WaitQ,
        Mtir, Mfir, Ilwr, Iswr,
        Rnext, Rget, Rinit, Rxor,
        Mfp, Xtop, Xitop, Xgkick,
        Esadd, Ersadd, Eleng, Erleng, Eatanxy, Eatanxz, Esum, Ercpr, Esqrt, Ersqrt, WaitP, Esin, Eatan, Eexp
    }

    private enum BranchOp : byte
    {
        None = 0,
        B, Bal, Jr, Jalr, Ibeq, Ibne, Ibltz, Ibgtz, Iblez, Ibgez
    }

    private struct DecodedSlot
    {
        public UpperOp UpperOp;
        public LowerOp LowerOp;
        public BranchOp BranchOp;
        public byte Fd;
        public byte Fs;
        public byte Ft;
        public byte Dest;
        public byte Bc;
        public byte It;
        public byte Is;
        public byte Id;
        public byte LowerDest;
        public byte Fsf;
        public byte Ftf;
        public short Imm11;
        public ushort Uimm15;
        public ushort BranchTargetSlot;
        public uint Imm24;
        public uint LoiBits;
        public bool IsImm;
        public bool IsE;
        public bool Valid;
    }

    private sealed class VuRegion
    {
        public string Name { get; init; } = "";
        public int BitIndex { get; init; }
        public int StartByteAddr { get; init; }
        public int EndByteAddr => StartByteAddr + Bytes.Length;
        public int StartSlot => StartByteAddr >> 3;
        public int SlotCount => Bytes.Length >> 3;
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public uint Crc32 { get; init; }
        public bool IsOverlay => StartByteAddr >= 0x15E0;
    }

    private sealed class VuImageSpec
    {
        public string Name { get; init; } = "";
        public int? OverlayIndex { get; init; } // 1..4 if overlay, null if base-only
        public byte[] MicroMem { get; init; } = new byte[16384];
        public int[] SlotOwnerBit { get; init; } = new int[2048];
        public DecodedSlot[] Slots { get; init; } = new DecodedSlot[2048];
        public List<int> CandidateEntrySlots { get; init; } = new();
    }

    private sealed class SubInfo
    {
        public string MethodName { get; init; } = "";
        public int StartSlot { get; init; }
        public int LinkReg { get; init; }
        public bool UsesFlags { get; init; }
        public uint RegionMask { get; init; }
        public List<int> Nodes { get; init; } = new();
    }

    private sealed class EntryInfo
    {
        public ushort StartByteAddr { get; init; }
        public uint RegionMask { get; init; }
        public int? OverlayIndex { get; init; }
        public string MethodName { get; init; } = "";
    }

    // Known hot/observed RenderWare VU1 entry points in SLUS_216.64 (plus all auto-discovered entries)
    private static readonly int[] KnownEntryByteAddrs =
    {
        0x0008, 0x0010, 0x0108, 0x0298, 0x03C0, 0x0438, 0x04C8, 0x04F8, 0x0548, 0x0598, 0x05E8, 0x06B0, 0x0728, 0x07A8, 0x07F0, 0x0878,
        0x0898, 0x0D20,
        0x15E0, 0x15E8, 0x16F8, 0x1820, 0x1CE8, 0x2410, 0x2650, 0x29A8, 0x2C88, 0x2E20, 0x3288, 0x33B0
    };

    [ThreadStatic]
    private static int _efuRemainCycles;

    private struct MethodRegUsage
    {
        public uint UsedVf;
        public uint WrittenVf;
        public ushort UsedVi;
        public ushort WrittenVi;
        public bool UsesQ;
        public bool WritesQ;
        public bool UsesI;
        public bool WritesI;
    }

    [ThreadStatic]
    private static MethodRegUsage _curUsage;
    [ThreadStatic]
    private static bool _useLocalRegs;
    [ThreadStatic]
    private static byte _curAccLiveAfter;
    [ThreadStatic]
    private static byte _curUpperVfLiveAfter;
    [ThreadStatic]
    private static byte _curLowerVfLiveAfter;
    [ThreadStatic]
    private static byte _curLqSanitizeMask;
    [ThreadStatic]
    private static bool _curUsesStatusFlags;

    private static void ScanSlotUsage(in DecodedSlot s, ref MethodRegUsage u)
    {
        if (s.UpperOp != UpperOp.Nop)
        {
            if (s.Fs != 0) u.UsedVf |= 1u << (s.Fs & 31);
            if (s.Ft != 0) u.UsedVf |= 1u << (s.Ft & 31);
            if (!IsAccOp(s.UpperOp) && s.UpperOp != UpperOp.Clip)
            {
                int outReg = (UsesFtDestUpper(s.UpperOp) ? s.Ft : s.Fd) & 31;
                uint outMask = s.UpperOp == UpperOp.Opmsub ? 0xEu : s.Dest;
                if (outReg != 0 && outMask != 0)
                {
                    u.WrittenVf |= 1u << outReg;
                    if (outMask != 0xFu) u.UsedVf |= 1u << outReg;
                }
            }
            switch (s.UpperOp)
            {
                case UpperOp.MulQ or UpperOp.AddQ or UpperOp.MaddQ or UpperOp.SubQ or UpperOp.MsubQ or
                     UpperOp.MulAq or UpperOp.AddAq or UpperOp.MaddAq or UpperOp.SubAq or UpperOp.MsubAq:
                    u.UsesQ = true;
                    break;
                case UpperOp.MulI or UpperOp.MaxI or UpperOp.MinI or UpperOp.AddI or UpperOp.MaddI or UpperOp.SubI or UpperOp.MsubI or
                     UpperOp.MulAi or UpperOp.AddAi or UpperOp.MaddAi or UpperOp.SubAi or UpperOp.MsubAi:
                    u.UsesI = true;
                    break;
            }
        }

        if (s.IsImm)
        {
            u.WritesI = true;
        }
        else if (s.LowerOp != LowerOp.Nop)
        {
            int isVf = s.Is & 31, itVf = s.It & 31;
            int isVi = s.Is & 15, itVi = s.It & 15, idVi = s.Id & 15;
            switch (s.LowerOp)
            {
                case LowerOp.Lq:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVf != 0 && s.LowerDest != 0) { u.WrittenVf |= 1u << itVf; if (s.LowerDest != 0xFu) u.UsedVf |= 1u << itVf; }
                    break;
                case LowerOp.Sq:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    if (itVi != 0) u.UsedVi |= (ushort)(1u << itVi);
                    break;
                case LowerOp.Lqi or LowerOp.Lqd:
                    if (isVi != 0) { u.UsedVi |= (ushort)(1u << isVi); u.WrittenVi |= (ushort)(1u << isVi); }
                    if (itVf != 0 && s.LowerDest != 0) { u.WrittenVf |= 1u << itVf; if (s.LowerDest != 0xFu) u.UsedVf |= 1u << itVf; }
                    break;
                case LowerOp.Sqi or LowerOp.Sqd:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    if (itVi != 0) { u.UsedVi |= (ushort)(1u << itVi); u.WrittenVi |= (ushort)(1u << itVi); }
                    break;
                case LowerOp.Ilw or LowerOp.Ilwr or LowerOp.Iaddiu or LowerOp.Isubiu or LowerOp.Iaddi or LowerOp.Fmeq or LowerOp.Fmand or LowerOp.Fmor:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVi != 0) u.WrittenVi |= (ushort)(1u << itVi);
                    break;
                case LowerOp.Isw or LowerOp.Iswr:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVi != 0) u.UsedVi |= (ushort)(1u << itVi);
                    break;
                case LowerOp.Fceq or LowerOp.Fcand or LowerOp.Fcor:
                    u.WrittenVi |= 2;
                    break;
                case LowerOp.Fsand or LowerOp.Fsor or LowerOp.Fcget or LowerOp.Xtop or LowerOp.Xitop:
                    if (itVi != 0) u.WrittenVi |= (ushort)(1u << itVi);
                    break;
                case LowerOp.Iadd or LowerOp.Isub or LowerOp.Iand or LowerOp.Ior:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVi != 0) u.UsedVi |= (ushort)(1u << itVi);
                    if (idVi != 0) u.WrittenVi |= (ushort)(1u << idVi);
                    break;
                case LowerOp.Move or LowerOp.Mr32:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    if (itVf != 0 && s.LowerDest != 0) { u.WrittenVf |= 1u << itVf; if (s.LowerDest != 0xFu) u.UsedVf |= 1u << itVf; }
                    break;
                case LowerOp.Div or LowerOp.Rsqrt:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    if (itVf != 0) u.UsedVf |= 1u << itVf;
                    u.WritesQ = true;
                    break;
                case LowerOp.Sqrt:
                    if (itVf != 0) u.UsedVf |= 1u << itVf;
                    u.WritesQ = true;
                    break;
                case LowerOp.Mtir:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    if (itVi != 0) u.WrittenVi |= (ushort)(1u << itVi);
                    break;
                case LowerOp.Mfir:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVf != 0 && s.LowerDest != 0) { u.WrittenVf |= 1u << itVf; if (s.LowerDest != 0xFu) u.UsedVf |= 1u << itVf; }
                    break;
                case LowerOp.Rnext or LowerOp.Rget or LowerOp.Mfp:
                    if (itVf != 0 && s.LowerDest != 0) { u.WrittenVf |= 1u << itVf; if (s.LowerDest != 0xFu) u.UsedVf |= 1u << itVf; }
                    break;
                case LowerOp.Rinit or LowerOp.Rxor or
                     LowerOp.Esadd or LowerOp.Ersadd or LowerOp.Eleng or LowerOp.Erleng or LowerOp.Eatanxy or LowerOp.Eatanxz or
                     LowerOp.Esum or LowerOp.Ercpr or LowerOp.Esqrt or LowerOp.Ersqrt or LowerOp.Esin or LowerOp.Eatan or LowerOp.Eexp:
                    if (isVf != 0) u.UsedVf |= 1u << isVf;
                    break;
                case LowerOp.Xgkick:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    break;
            }
        }

        if (s.BranchOp != BranchOp.None)
        {
            int isVi = s.Is & 15, itVi = s.It & 15;
            switch (s.BranchOp)
            {
                case BranchOp.Ibeq or BranchOp.Ibne:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVi != 0) u.UsedVi |= (ushort)(1u << itVi);
                    break;
                case BranchOp.Ibltz or BranchOp.Ibgtz or BranchOp.Iblez or BranchOp.Ibgez or BranchOp.Jr:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    break;
                case BranchOp.Bal:
                    if (itVi != 0) u.WrittenVi |= (ushort)(1u << itVi);
                    break;
                case BranchOp.Jalr:
                    if (isVi != 0) u.UsedVi |= (ushort)(1u << isVi);
                    if (itVi != 0) u.WrittenVi |= (ushort)(1u << itVi);
                    break;
            }
        }
    }

    private static MethodRegUsage ScanMethodUsage(VuImageSpec img, List<int> nodes)
    {
        MethodRegUsage u = default;
        foreach (int cur in nodes)
        {
            ref readonly var s = ref img.Slots[cur];
            ScanSlotUsage(in s, ref u);
            if (s.IsE || s.BranchOp != BranchOp.None)
            {
                ScanSlotUsage(in img.Slots[(cur + 1) & 0x7FF], ref u);
            }
        }
        u.UsedVf &= ~1u;
        u.WrittenVf &= ~1u;
        u.UsedVi &= 0xFFFE;
        u.WrittenVi &= 0xFFFE;
        return u;
    }

    private static void EmitDeclareAndLoadLocals(in MethodRegUsage u, StringBuilder sb, string pad)
    {
        uint allVf = u.UsedVf | u.WrittenVf;
        for (int r = 1; r < 32; r++)
        {
            if ((allVf & (1u << r)) != 0)
                sb.AppendLine($"{pad}Vector128<float> vf{r} = Unsafe.Add(ref vf, {r});");
        }
        ushort allVi = (ushort)(u.UsedVi | u.WrittenVi);
        for (int r = 1; r < 16; r++)
        {
            if ((allVi & (1u << r)) != 0)
                sb.AppendLine($"{pad}ushort vi{r} = Unsafe.Add(ref vi, {r});");
        }
        if (u.UsesQ || u.WritesQ)
            sb.AppendLine($"{pad}float regQ = vu.Q;");
        if (u.UsesI || u.WritesI)
            sb.AppendLine($"{pad}float regI = vu.I;");
    }

    private static void EmitReloadLocals(in MethodRegUsage u, StringBuilder sb, string pad)
    {
        uint allVf = u.UsedVf | u.WrittenVf;
        for (int r = 1; r < 32; r++)
        {
            if ((allVf & (1u << r)) != 0)
                sb.AppendLine($"{pad}vf{r} = Unsafe.Add(ref vf, {r});");
        }
        ushort allVi = (ushort)(u.UsedVi | u.WrittenVi);
        for (int r = 1; r < 16; r++)
        {
            if ((allVi & (1u << r)) != 0)
                sb.AppendLine($"{pad}vi{r} = Unsafe.Add(ref vi, {r});");
        }
        if (u.UsesQ || u.WritesQ)
            sb.AppendLine($"{pad}regQ = vu.Q;");
        if (u.UsesI || u.WritesI)
            sb.AppendLine($"{pad}regI = vu.I;");
    }

    private static void EmitFlushLocals(in MethodRegUsage u, StringBuilder sb, string pad)
    {
        for (int r = 1; r < 32; r++)
        {
            if ((u.WrittenVf & (1u << r)) != 0)
                sb.AppendLine($"{pad}Unsafe.Add(ref vf, {r}) = vf{r};");
        }
        for (int r = 1; r < 16; r++)
        {
            if ((u.WrittenVi & (1u << r)) != 0)
                sb.AppendLine($"{pad}Unsafe.Add(ref vi, {r}) = vi{r};");
        }
        if (u.WritesQ)
            sb.AppendLine($"{pad}vu.Q = regQ;");
        if (u.WritesI)
            sb.AppendLine($"{pad}vu.I = regI;");
    }

    public static int Run(string[] args)
    {
        string? elfPath = null;
        string outPath = Path.GetFullPath(Path.Combine("src", "Castaway.GsRenderer", "Vu1", "Vu1AotPrograms.g.cs"));

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--elf" && i + 1 < args.Length)
                elfPath = args[++i];
            else if ((args[i] == "--out" || args[i] == "-o") && i + 1 < args.Length)
                outPath = Path.GetFullPath(args[++i]);
        }

        string resolvedElf = Ps2ElfReader.FindDefaultElfPath(elfPath);
        var reader = new Ps2ElfReader(resolvedElf);
        var vutext = reader.FindSection(".vutext") ?? throw new InvalidOperationException(".vutext section not found in ELF.");

        List<VuRegion> regions = ExtractContiguousRegions(reader, vutext);
        if (regions.Count == 0)
            throw new InvalidOperationException("No VU1 MPG regions found in .vutext.");

        var baseRegions = regions.Where(r => !r.IsOverlay).ToList();
        var overlayRegions = regions.Where(r => r.IsOverlay).ToList();

        Console.WriteLine($"[VU1 AOT] Extracted {regions.Count} contiguous regions from .vutext:");
        foreach (var r in regions)
            Console.WriteLine($"  [{r.BitIndex}] {r.Name,-6} [0x{r.StartByteAddr:X4}..0x{r.EndByteAddr:X4}) {r.SlotCount,4} slots CRC=0x{r.Crc32:X8}");

        var sb = new StringBuilder(128 * 1024);
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by Tools/Castaway.ElfTools aot-vu1 from SLUS_216.64 .vutext.");
        sb.AppendLine("// Do NOT edit manually; re-run `Castaway.ElfTools aot-vu1` to regenerate.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using System.Runtime.Intrinsics;");
        sb.AppendLine("using System.Runtime.Intrinsics.X86;");
        sb.AppendLine("using Castaway.Core;");
        sb.AppendLine();
        sb.AppendLine("namespace Castaway.GsRenderer.Vu1;");
        sb.AppendLine();
        sb.AppendLine("internal static class Vu1AotPrograms");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly Vector128<float> Vf0 = Vector128.Create(0.0f, 0.0f, 0.0f, 1.0f);");
        sb.AppendLine("    private static readonly Vector128<float> Vf0Mr32 = Vector128.Create(0.0f, 0.0f, 1.0f, 0.0f);");
        sb.AppendLine("    private static readonly Vector128<uint> ExpMaskVec = Vector128.Create(0x7F800000u);");
        sb.AppendLine("    private static readonly Vector128<uint> SignMaskVec = Vector128.Create(0x80000000u);");
        sb.AppendLine("    private static readonly Vector128<uint> SafeHugeVec = Vector128.Create(0x5DDE0B6Bu); // 1.0e18f: finite, x*0=0, x*x<3.4e38");
        sb.AppendLine("    private static readonly Vector128<uint>[] SanMaskTable =");
        sb.AppendLine("    [");
        for (int m = 0; m < 16; m++)
        {
            uint m0 = (m & 8) != 0 ? 0x7F800000u : 0u;
            uint m1 = (m & 4) != 0 ? 0x7F800000u : 0u;
            uint m2 = (m & 2) != 0 ? 0x7F800000u : 0u;
            uint m3 = (m & 1) != 0 ? 0x7F800000u : 0u;
            sb.AppendLine($"        Vector128.Create(0x{m0:X8}u, 0x{m1:X8}u, 0x{m2:X8}u, 0x{m3:X8}u),");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    // Branchless PS2 exp-255 sanitizer (3 SSE ops, 0 branches, 0 calls): replaces IEEE NaN/Inf bit patterns");
        sb.AppendLine("    // in float DataMem loads with finite +/-1e18f so 0.0f * uninitialized_garbage == 0.0f as on PS2 VU1.");
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> SanitizeFloat(Vector128<float> v)");
        sb.AppendLine("    {");
        sb.AppendLine("        Vector128<uint> u = v.AsUInt32();");
        sb.AppendLine("        Vector128<uint> isExp255 = Vector128.Equals(u & ExpMaskVec, ExpMaskVec);");
        sb.AppendLine("        return Vector128.ConditionalSelect(isExp255, (u & SignMaskVec) | SafeHugeVec, u).AsSingle();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> SanitizeFloatMask(Vector128<float> v, int mask)");
        sb.AppendLine("    {");
        sb.AppendLine("        Vector128<uint> u = v.AsUInt32();");
        sb.AppendLine("        Vector128<uint> isExp255 = Vector128.Equals(u & SanMaskTable[mask], ExpMaskVec);");
        sb.AppendLine("        return Vector128.ConditionalSelect(isExp255, (u & SignMaskVec) | SafeHugeVec, u).AsSingle();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> FmaAdd(Vector128<float> a, Vector128<float> b, Vector128<float> acc)");
        sb.AppendLine("        => acc + (a * b);");
        sb.AppendLine();
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> FmaSub(Vector128<float> a, Vector128<float> b, Vector128<float> acc)");
        sb.AppendLine("        => acc - (a * b);");
        sb.AppendLine();
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> VuMax(Vector128<float> a, Vector128<float> b)");
        sb.AppendLine("        => Sse.IsSupported ? Sse.Max(a, b) : Vector128.Max(a, b);");
        sb.AppendLine();
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    private static Vector128<float> VuMin(Vector128<float> a, Vector128<float> b)");
        sb.AppendLine("        => Sse.IsSupported ? Sse.Min(a, b) : Vector128.Min(a, b);");
        sb.AppendLine();

        foreach (var r in regions)
        {
            int midOff = r.Bytes.Length >= 72 ? 64 : 8;
            int tailOff = r.Bytes.Length >= 16 ? (r.Bytes.Length - 8) : 0;
            ulong headQ0 = BinaryPrimitives.ReadUInt64LittleEndian(r.Bytes.AsSpan(0, 8));
            ulong midQ0 = BinaryPrimitives.ReadUInt64LittleEndian(r.Bytes.AsSpan(midOff, 8));
            ulong tailQ0 = BinaryPrimitives.ReadUInt64LittleEndian(r.Bytes.AsSpan(tailOff, 8));
            sb.AppendLine($"    public const int {r.Name}StartByte = 0x{r.StartByteAddr:X4};");
            sb.AppendLine($"    public const int {r.Name}ByteLength = {r.Bytes.Length};");
            sb.AppendLine($"    public const uint {r.Name}Crc32 = 0x{r.Crc32:X8}u;");
            sb.AppendLine($"    public const int {r.Name}MidOffset = {midOff};");
            sb.AppendLine($"    public const int {r.Name}TailOffset = {tailOff};");
            sb.AppendLine($"    public const ulong {r.Name}HeadQ0 = 0x{headQ0:X16}ul;");
            sb.AppendLine($"    public const ulong {r.Name}MidQ0 = 0x{midQ0:X16}ul;");
            sb.AppendLine($"    public const ulong {r.Name}TailQ0 = 0x{tailQ0:X16}ul;");
        }
        sb.AppendLine();

        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    public static uint GetDirtyRegionMaskForRange(int startByte, int length)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (length <= 0) return 0u;");
        sb.AppendLine("        if (length >= 0x4000) return 0x3Fu;");
        sb.AppendLine("        int s0 = startByte & 0x3FFF;");
        sb.AppendLine("        int e0 = s0 + length;");
        sb.AppendLine("        int s1 = 0, e1 = 0;");
        sb.AppendLine("        if (e0 > 0x4000)");
        sb.AppendLine("        {");
        sb.AppendLine("            s1 = 0;");
        sb.AppendLine("            e1 = e0 - 0x4000;");
        sb.AppendLine("            e0 = 0x4000;");
        sb.AppendLine("        }");
        sb.AppendLine("        uint dirty = 0u;");
        foreach (var r in regions)
        {
            sb.AppendLine($"        if ((s0 < {r.Name}StartByte + {r.Name}ByteLength && e0 > {r.Name}StartByte) || (e1 > {r.Name}StartByte && s1 < {r.Name}StartByte + {r.Name}ByteLength)) dirty |= {1u << r.BitIndex}u;");
        }
        sb.AppendLine("        return dirty;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Emit CheckRegions helper used only when MicroMem was modified by VIF MPG
        sb.AppendLine("    public static uint IdentifyLoadedRegions(byte[] microMem)");
        sb.AppendLine("        => IdentifyLoadedRegions(microMem, 0u, 0xFFFFFFFFu);");
        sb.AppendLine();
        sb.AppendLine("    public static uint IdentifyLoadedRegions(byte[] microMem, uint currentMask, uint dirtyRegionMask)");
        sb.AppendLine("    {");
        sb.AppendLine("        ref byte mm = ref MemoryMarshal.GetArrayDataReference(microMem);");
        sb.AppendLine("        uint mask = currentMask;");
        foreach (var r in regions)
        {
            uint bit = 1u << r.BitIndex;
            sb.AppendLine($"        if ((dirtyRegionMask & 0x{bit:X}u) != 0)");
            sb.AppendLine("        {");
            sb.AppendLine($"            mask &= ~0x{bit:X}u;");
            sb.AppendLine($"            if (Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref mm, {r.Name}StartByte)) == {r.Name}HeadQ0");
            sb.AppendLine($"                && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref mm, {r.Name}StartByte + {r.Name}MidOffset)) == {r.Name}MidQ0");
            sb.AppendLine($"                && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref mm, {r.Name}StartByte + {r.Name}TailOffset)) == {r.Name}TailQ0)");
            sb.AppendLine($"                mask |= 0x{bit:X}u;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return mask;");
        sb.AppendLine("    }");
        sb.AppendLine();

        var allEntries = new List<EntryInfo>();
        var emittedSubs = new Dictionary<string, SubInfo>();
        var seenBaseEntries = new HashSet<int>();

        // 1. Base-only image (all non-overlapping Base regions together)
        var baseImg = BuildImage("Base", null, baseRegions, extraOverlay: null);
        EmitEntriesForImage(baseImg, regions, emittedSubs, allEntries, seenBaseEntries, baseOnly: true, sb);

        // 2. Each Overlay image (paired with Base regions for cross-calls like Ovl3->Base0, Ovl4->Base1)
        for (int i = 0; i < overlayRegions.Count; i++)
        {
            var ovl = overlayRegions[i];
            int ovlIdx = i + 1;
            var ovlImg = BuildImage(ovl.Name, ovlIdx, baseRegions, ovl);
            EmitEntriesForImage(ovlImg, regions, emittedSubs, allEntries, seenBaseEntries, baseOnly: false, sb);
        }

        // Emit fast TryExecute dispatcher
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        sb.AppendLine("    public static bool TryExecute(Vu1State vu, ushort startByteAddr, uint regionMask)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (startByteAddr)");
        sb.AppendLine("        {");
        foreach (var group in allEntries.GroupBy(e => e.StartByteAddr).OrderBy(g => g.Key))
        {
            sb.AppendLine($"            case 0x{group.Key:X4}:");
            foreach (var e in group)
            {
                sb.AppendLine($"                if ((regionMask & 0x{e.RegionMask:X}u) == 0x{e.RegionMask:X}u) {{ {e.MethodName}(vu); return true; }}");
            }
            sb.AppendLine("                return false;");
        }
        sb.AppendLine("            default:");
        sb.AppendLine("                return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Console.WriteLine($"[VU1 AOT] Emitted {allEntries.Count} entry methods and {emittedSubs.Count} subroutines -> {outPath}");
        return 0;
    }

    private static List<VuRegion> ExtractContiguousRegions(Ps2ElfReader reader, ElfSection vutext)
    {
        byte[] raw = reader.GetRawBytes();
        int start = (int)vutext.FileOffset;
        int end = Math.Min(raw.Length, start + (int)vutext.Size) - 8;

        var rawBlocks = new List<(int DstByteAddr, byte[] Payload)>();
        for (int i = start; i <= end; i += 4)
        {
            uint vifCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i, 4));
            byte cmd = (byte)(vifCode >> 24);
            byte num = (byte)(vifCode >> 16);
            ushort addr = (ushort)(vifCode & 0xFFFF);

            if ((cmd & 0x7F) != 0x4A || addr >= 0x0800)
                continue;

            int insnCount = num == 0 ? 256 : num;
            int mpgBytes = insnCount * 8;
            if (i + 4 + mpgBytes > start + (int)vutext.Size)
                continue;

            rawBlocks.Add((addr * 8, raw.AsSpan(i + 4, mpgBytes).ToArray()));
            i += 4 + mpgBytes - 4;
        }

        // Coalesce consecutive contiguous MPG blocks into logical VU1 regions
        var coalesced = new List<(int StartByteAddr, List<byte> Bytes)>();
        foreach (var blk in rawBlocks)
        {
            if (coalesced.Count > 0 && coalesced[^1].StartByteAddr + coalesced[^1].Bytes.Count == blk.DstByteAddr)
            {
                coalesced[^1].Bytes.AddRange(blk.Payload);
            }
            else
            {
                coalesced.Add((blk.DstByteAddr, new List<byte>(blk.Payload)));
            }
        }

        var regions = new List<VuRegion>();
        int baseIdx = 0;
        int ovlIdx = 1;
        for (int i = 0; i < coalesced.Count; i++)
        {
            byte[] bytes = coalesced[i].Bytes.ToArray();
            int addr = coalesced[i].StartByteAddr;
            string name = addr < 0x15E0 ? $"Base{baseIdx++}" : $"Ovl{ovlIdx++}";
            regions.Add(new VuRegion
            {
                Name = name,
                BitIndex = i,
                StartByteAddr = addr,
                Bytes = bytes,
                Crc32 = Crc32.Compute(bytes)
            });
        }
        return regions;
    }

    private static VuImageSpec BuildImage(string name, int? overlayIdx, List<VuRegion> baseRegions, VuRegion? extraOverlay)
    {
        var img = new VuImageSpec { Name = name, OverlayIndex = overlayIdx };
        Array.Fill(img.SlotOwnerBit, -1);

        void MapRegion(VuRegion r)
        {
            Buffer.BlockCopy(r.Bytes, 0, img.MicroMem, r.StartByteAddr, r.Bytes.Length);
            for (int s = r.StartSlot; s < r.StartSlot + r.SlotCount; s++)
                img.SlotOwnerBit[s] = r.BitIndex;
        }

        foreach (var b in baseRegions)
            MapRegion(b);
        if (extraOverlay != null)
            MapRegion(extraOverlay);

        for (int i = 0; i < 2048; i++)
            img.Slots[i] = DecodeSlot(img.MicroMem, i);

        // Discover candidate entries from primary regions
        IEnumerable<VuRegion> primaryRegions = extraOverlay != null ? new[] { extraOverlay } : baseRegions;
        var candidates = new SortedSet<int>();
        foreach (var r in primaryRegions)
        {
            int s0 = r.StartSlot;
            int sEnd = s0 + r.SlotCount;
            candidates.Add(s0);
            if (s0 + 1 < sEnd) candidates.Add(s0 + 1);
            if (s0 + 2 < sEnd && img.Slots[s0].IsE) candidates.Add(s0 + 2);

            for (int s = s0; s < sEnd; s++)
            {
                ref readonly var sl = ref img.Slots[s];
                if (sl.IsE || sl.BranchOp is BranchOp.B or BranchOp.Jr)
                {
                    if (s + 2 < sEnd)
                        candidates.Add(s + 2);
                }
                if (sl.LowerOp is LowerOp.Xtop or LowerOp.Xitop)
                {
                    // Check if previous slot looks like padding/nop or branch delay or another valid start
                    if (s > s0 && (img.Slots[s - 1].IsE || (img.Slots[s - 1].UpperOp == UpperOp.AddBc && img.Slots[s - 1].Fd == 0 && img.Slots[s - 1].Fs == 0 && img.Slots[s - 1].Ft == 0)))
                        candidates.Add(s);
                }
            }
        }

        foreach (int addr in KnownEntryByteAddrs)
        {
            int slot = (addr >> 3) & 0x7FF;
            if (img.SlotOwnerBit[slot] >= 0)
            {
                bool isOvlAddr = addr >= 0x15E0;
                if ((extraOverlay != null && isOvlAddr) || (extraOverlay == null && !isOvlAddr))
                    candidates.Add(slot);
            }
        }

        img.CandidateEntrySlots.AddRange(candidates);
        return img;
    }

    private static void EmitEntriesForImage(
        VuImageSpec img,
        List<VuRegion> allRegions,
        Dictionary<string, SubInfo> emittedSubs,
        List<EntryInfo> allEntries,
        HashSet<int> seenBaseEntries,
        bool baseOnly,
        StringBuilder sb)
    {
        // Identify clean subroutines (BAL targets that return exclusively via JR viK with no E-bit or writes to viK)
        var balCallers = new Dictionary<int, HashSet<int>>(); // targetSlot -> linkRegs used
        foreach (int entrySlot in img.CandidateEntrySlots)
            CollectBalTargets(img, entrySlot, balCallers);

        var cleanSubs = new Dictionary<int, SubInfo>();
        foreach (var (subSlot, linkRegs) in balCallers)
        {
            if (linkRegs.Count != 1) continue;
            int linkReg = linkRegs.First();
            if (linkReg == 0) continue;
            if (TryAnalyzeCleanSubroutine(img, subSlot, linkReg, out var subNodes, out uint subMask, out bool subUsesFlags, out bool allValid))
            {
                if (!allValid) continue;
                int ownerBit = img.SlotOwnerBit[subSlot];
                string ownerName = ownerBit >= 0 ? allRegions[ownerBit].Name : img.Name;
                string subName = $"Sub_{ownerName}_{subSlot * 8:X4}";
                var subInfo = new SubInfo
                {
                    MethodName = subName,
                    StartSlot = subSlot,
                    LinkReg = linkReg,
                    UsesFlags = subUsesFlags,
                    RegionMask = subMask,
                    Nodes = subNodes
                };
                cleanSubs[subSlot] = subInfo;
                if (!emittedSubs.ContainsKey(subName))
                {
                    emittedSubs[subName] = subInfo;
                    EmitSubroutineMethod(img, subInfo, sb);
                }
            }
        }

        foreach (int entrySlot in img.CandidateEntrySlots)
        {
            if (baseOnly && !seenBaseEntries.Add(entrySlot))
                continue;

            if (!TryTraceEntry(img, entrySlot, cleanSubs, out var nodes, out var jrTargetsByReg, out uint regionMask, out bool usesFlags, out bool allValid, out string failReason))
            {
                if (KnownEntryByteAddrs.Contains(entrySlot * 8))
                    Console.WriteLine($"  [SKIP KNOWN] {img.Name} 0x{entrySlot * 8:X4}: {failReason}");
                continue;
            }

            if (!allValid || nodes.Count == 0 || regionMask == 0)
            {
                if (KnownEntryByteAddrs.Contains(entrySlot * 8))
                    Console.WriteLine($"  [SKIP KNOWN] {img.Name} 0x{entrySlot * 8:X4}: allValid={allValid} nodes={nodes.Count} mask=0x{regionMask:X}");
                continue;
            }

            // Skip bogus/data slots (e.g. a trailing padding NOP or unreachable slot with zero instructions and immediate invalid stop, unless known entry)
            bool isKnown = KnownEntryByteAddrs.Contains(entrySlot * 8);
            if (!isKnown && nodes.Count == 1 && img.Slots[entrySlot].UpperOp == UpperOp.Nop && img.Slots[entrySlot].LowerOp == LowerOp.Nop && img.Slots[entrySlot].BranchOp == BranchOp.None && !img.Slots[entrySlot].IsE)
                continue;

            // If this entry only touches Base regions when scanning an Overlay image, skip duplicate
            if (!baseOnly && (regionMask & ~0x0u) != 0)
            {
                bool touchesOverlay = (regionMask & allRegions.Where(r => r.IsOverlay).Aggregate(0u, (a, r) => a | (1u << r.BitIndex))) != 0;
                if (!touchesOverlay)
                    continue;
            }

            string methodName = $"Exec_{img.Name}_{entrySlot * 8:X4}";
            allEntries.Add(new EntryInfo
            {
                StartByteAddr = (ushort)(entrySlot * 8),
                RegionMask = regionMask,
                OverlayIndex = img.OverlayIndex,
                MethodName = methodName
            });

            EmitEntryMethod(img, entrySlot, methodName, nodes, cleanSubs, jrTargetsByReg, usesFlags, sb);
        }
    }

    private static void CollectBalTargets(VuImageSpec img, int startSlot, Dictionary<int, HashSet<int>> balCallers)
    {
        var visited = new bool[2048];
        var stack = new Stack<int>();
        visited[startSlot] = true;
        stack.Push(startSlot);

        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            if (img.SlotOwnerBit[cur] < 0) continue;
            ref readonly var s = ref img.Slots[cur];
            if (!s.Valid) continue;

            if (s.IsE) continue;

            if (s.BranchOp != BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                if (img.Slots[d].IsE) continue;

                if (s.BranchOp == BranchOp.Bal)
                {
                    if (!balCallers.TryGetValue(s.BranchTargetSlot, out var set))
                        balCallers[s.BranchTargetSlot] = set = new HashSet<int>();
                    set.Add(s.It & 15);
                }
                else if (s.BranchOp != BranchOp.Jr && s.BranchOp != BranchOp.Jalr)
                {
                    int t = s.BranchTargetSlot & 0x7FF;
                    if (!visited[t]) { visited[t] = true; stack.Push(t); }
                }

                if (s.BranchOp is not BranchOp.B and not BranchOp.Jr)
                {
                    int fall = (cur + 2) & 0x7FF;
                    if (!visited[fall]) { visited[fall] = true; stack.Push(fall); }
                }
            }
            else
            {
                int next = (cur + 1) & 0x7FF;
                if (!visited[next]) { visited[next] = true; stack.Push(next); }
            }
        }
    }

    private static bool TryAnalyzeCleanSubroutine(
        VuImageSpec img,
        int startSlot,
        int linkReg,
        out List<int> orderedNodes,
        out uint regionMask,
        out bool usesFlags,
        out bool allValid)
    {
        orderedNodes = new List<int>();
        regionMask = 0;
        usesFlags = false;
        allValid = true;

        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        visited.Add(startSlot);
        stack.Push(startSlot);

        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            int owner = img.SlotOwnerBit[cur];
            if (owner < 0) { allValid = false; return false; }
            regionMask |= 1u << owner;

            ref readonly var s = ref img.Slots[cur];
            if (!s.Valid || s.IsE) return false;
            if (SlotReadsFlags(in s)) usesFlags = true;
            if (SlotWritesVi(in s, linkReg)) return false;

            if (s.BranchOp != BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                int dOwner = img.SlotOwnerBit[d];
                if (dOwner < 0) return false;
                regionMask |= 1u << dOwner;
                ref readonly var ds = ref img.Slots[d];
                if (!ds.Valid || ds.IsE) return false;
                if (SlotReadsFlags(in ds)) usesFlags = true;
                if (SlotWritesVi(in ds, linkReg)) return false;

                if (s.BranchOp == BranchOp.Jr)
                {
                    if ((s.Is & 15) != linkReg) return false;
                    continue;
                }
                if (s.BranchOp is BranchOp.Bal or BranchOp.Jalr)
                {
                    return false; // Keep nested BAL/JALR inside inline CFG for simplicity
                }

                int t = s.BranchTargetSlot & 0x7FF;
                if (visited.Add(t)) stack.Push(t);

                if (s.BranchOp != BranchOp.B)
                {
                    int fall = (cur + 2) & 0x7FF;
                    if (visited.Add(fall)) stack.Push(fall);
                }
            }
            else
            {
                int next = (cur + 1) & 0x7FF;
                if (visited.Add(next)) stack.Push(next);
            }
        }

        orderedNodes = visited.OrderBy(x => x).ToList();
        return orderedNodes.Count > 0 && orderedNodes.Count <= 200;
    }

    private static bool TryTraceEntry(
        VuImageSpec img,
        int startSlot,
        Dictionary<int, SubInfo> cleanSubs,
        out List<int> orderedNodes,
        out Dictionary<int, SortedSet<int>> jrTargetsByReg,
        out uint regionMask,
        out bool usesFlags,
        out bool allValid,
        out string failReason)
    {
        orderedNodes = new List<int>();
        var jrTargets = new Dictionary<int, SortedSet<int>>();
        jrTargetsByReg = jrTargets;
        regionMask = 0;
        usesFlags = false;
        allValid = true;
        failReason = "";

        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        var jrRegsUsedList = new List<(int Reg, int Slot)>();
        var jrRegsSeen = new HashSet<int>();
        visited.Add(startSlot);
        stack.Push(startSlot);

        void AddJrTarget(int reg, int targetSlot)
        {
            if (targetSlot < 0 || targetSlot >= 2048 || img.SlotOwnerBit[targetSlot] < 0) return;
            if (!jrTargets.TryGetValue(reg, out var set))
                jrTargets[reg] = set = new SortedSet<int>();
            if (set.Add(targetSlot) && jrRegsSeen.Contains(reg))
            {
                if (visited.Add(targetSlot))
                    stack.Push(targetSlot);
            }
        }

        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            int owner = img.SlotOwnerBit[cur];
            if (owner < 0) { allValid = false; failReason = $"unmapped slot {cur} (0x{cur * 8:X4})"; return false; }
            regionMask |= 1u << owner;

            ref readonly var s = ref img.Slots[cur];
            if (!s.Valid) { allValid = false; failReason = $"invalid op at slot {cur} (0x{cur * 8:X4})"; return false; }
            if (SlotReadsFlags(in s)) usesFlags = true;

            if (s.IsE)
            {
                int d = (cur + 1) & 0x7FF;
                int dOwner = img.SlotOwnerBit[d];
                if (dOwner < 0 || !img.Slots[d].Valid) { allValid = false; failReason = $"invalid/unmapped E-delay slot {d} (0x{d * 8:X4})"; return false; }
                regionMask |= 1u << dOwner;
                if (SlotReadsFlags(in img.Slots[d])) usesFlags = true;
                continue;
            }

            if (s.BranchOp != BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                int dOwner = img.SlotOwnerBit[d];
                if (dOwner < 0 || !img.Slots[d].Valid) { allValid = false; failReason = $"invalid/unmapped branch-delay slot {d} (0x{d * 8:X4})"; return false; }
                regionMask |= 1u << dOwner;
                ref readonly var ds = ref img.Slots[d];
                if (SlotReadsFlags(in ds)) usesFlags = true;

                if (!ds.IsImm && ds.LowerOp == LowerOp.Iaddiu && (ds.Is & 15) == 0 && (ds.It & 15) != 0 && ds.Uimm15 < 2048 && img.SlotOwnerBit[ds.Uimm15] == owner)
                    AddJrTarget(ds.It & 15, ds.Uimm15);
                else if (!ds.IsImm && ds.LowerOp == LowerOp.Iaddi && (ds.Is & 15) == 0 && (ds.It & 15) != 0 && ds.Imm11 >= 0 && ds.Imm11 < 2048 && img.SlotOwnerBit[ds.Imm11] == owner)
                    AddJrTarget(ds.It & 15, ds.Imm11);

                if (s.BranchOp == BranchOp.Bal && cleanSubs.TryGetValue(s.BranchTargetSlot, out var sub))
                {
                    regionMask |= sub.RegionMask;
                    if (sub.UsesFlags) usesFlags = true;
                    if (!ds.IsE)
                    {
                        int fall = (cur + 2) & 0x7FF;
                        if (visited.Add(fall)) stack.Push(fall);
                    }
                    continue;
                }

                if (s.BranchOp == BranchOp.Jr)
                {
                    int r = s.Is & 15;
                    jrRegsUsedList.Add((r, cur));
                    if (jrRegsSeen.Add(r) && jrTargetsByReg.TryGetValue(r, out var existing))
                    {
                        foreach (int t in existing)
                        {
                            if (visited.Add(t)) stack.Push(t);
                        }
                    }
                    continue;
                }

                if (s.BranchOp == BranchOp.Jalr)
                {
                    allValid = false;
                    failReason = $"JALR at slot {cur} (0x{cur * 8:X4})";
                    return false;
                }

                if (s.BranchOp == BranchOp.Bal)
                {
                    AddJrTarget(s.It & 15, (cur + 2) & 0x7FF);
                }

                if (!ds.IsE)
                {
                    int t = s.BranchTargetSlot & 0x7FF;
                    if (visited.Add(t)) stack.Push(t);

                    if (s.BranchOp != BranchOp.B && s.BranchOp != BranchOp.Bal)
                    {
                        int fall = (cur + 2) & 0x7FF;
                        if (visited.Add(fall)) stack.Push(fall);
                    }
                }
            }
            else
            {
                int next = (cur + 1) & 0x7FF;
                if (visited.Add(next)) stack.Push(next);
            }

            if (visited.Count > 1500) { allValid = false; failReason = "visited > 1500"; return false; }
        }

        foreach (var (r, jrSlot) in jrRegsUsedList)
        {
            if (!jrTargetsByReg.TryGetValue(r, out var set) || set.Count == 0)
            {
                allValid = false;
                failReason = $"JR vi{r} at slot {jrSlot} (0x{jrSlot * 8:X4}) with 0 known BAL callers in CFG";
                return false;
            }
        }

        orderedNodes = visited.OrderBy(x => x).ToList();
        return true;
    }

    private static void EmitSubroutineMethod(VuImageSpec img, SubInfo sub, StringBuilder sb)
    {
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveOptimization)]");
        sb.AppendLine($"    private static void {sub.MethodName}(Vu1State vu, ref int steps, ref Vector128<float> acc, bool updateFlags)");
        sb.AppendLine("    {");
        sb.AppendLine("        ref Vector128<float> vf = ref MemoryMarshal.GetArrayDataReference(vu.VF);");
        sb.AppendLine("        ref ushort vi = ref MemoryMarshal.GetArrayDataReference(vu.VI);");
        sb.AppendLine("        ref byte dm = ref MemoryMarshal.GetArrayDataReference(vu.DataMem);");
        sb.AppendLine("        Vector128<float> tmp = default;");
        sb.AppendLine("        ushort idxTmp = 0;");
        sb.AppendLine("        bool btTmp = false;");
        sb.AppendLine("        int jtTmp = 0;");
        sb.AppendLine("        _ = (tmp, idxTmp, btTmp, jtTmp);");

        if (sub.MethodName == "Sub_Base0_0330")
        {
            sb.AppendLine("        int rem0330 = (short)Unsafe.Add(ref vi, 4);");
            sb.AppendLine("        if (rem0330 > 0)");
            sb.AppendLine("        {");
            sb.AppendLine("            int iters = (rem0330 + 2) / 3;");
            sb.AppendLine("            int maxIters = (65536 - steps + 7) >> 3;");
            sb.AppendLine("            int qwords = iters * 3;");
            sb.AppendLine("            int srcQ = Unsafe.Add(ref vi, 2) & 0x3FF;");
            sb.AppendLine("            int dstQ = Unsafe.Add(ref vi, 3) & 0x3FF;");
            sb.AppendLine("            if (iters <= maxIters && srcQ + qwords <= 1024 && dstQ + qwords <= 1024 && (dstQ + qwords <= srcQ || srcQ + qwords <= dstQ))");
            sb.AppendLine("            {");
            sb.AppendLine("                Span<byte> dmSpan = vu.DataMem;");
            sb.AppendLine("                dmSpan.Slice(srcQ << 4, qwords << 4).CopyTo(dmSpan.Slice(dstQ << 4, qwords << 4));");
            sb.AppendLine("                int lastSrc = srcQ + qwords - 3;");
            sb.AppendLine("                Unsafe.Add(ref vf, 1) = Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, lastSrc << 4));");
            sb.AppendLine("                Unsafe.Add(ref vf, 2) = Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, (lastSrc + 1) << 4));");
            sb.AppendLine("                Unsafe.Add(ref vf, 3) = Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, (lastSrc + 2) << 4));");
            sb.AppendLine("                Unsafe.Add(ref vi, 2) = (ushort)((srcQ + qwords) & 0x3FF);");
            sb.AppendLine("                Unsafe.Add(ref vi, 3) = (ushort)((dstQ + qwords) & 0x3FF);");
            sb.AppendLine("                Unsafe.Add(ref vi, 4) = unchecked((ushort)(rem0330 - qwords));");
            sb.AppendLine("                steps += iters * 8 + 2;");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        _curUsage = ScanMethodUsage(img, sub.Nodes);
        _useLocalRegs = true;
        EmitDeclareAndLoadLocals(in _curUsage, sb, "        ");

        var nodeSet = new HashSet<int>(sub.Nodes);
        var labelNeeded = CollectJumpTargets(img, sub.Nodes, null);

        if (sub.StartSlot != sub.Nodes[0])
        {
            labelNeeded.Add(sub.StartSlot);
            sb.AppendLine($"        goto slot_{sub.StartSlot};");
        }

        int indent = 8;
        EmitNodeList(img, sub.Nodes, nodeSet, labelNeeded, null, null, sub.UsesFlags, isSubroutine: true, sub.LinkReg, sb, indent);
        _useLocalRegs = false;
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitEntryMethod(
        VuImageSpec img,
        int startSlot,
        string methodName,
        List<int> nodes,
        Dictionary<int, SubInfo> cleanSubs,
        Dictionary<int, SortedSet<int>> jrTargetsByReg,
        bool usesFlags,
        StringBuilder sb)
    {
        sb.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveOptimization)]");
        sb.AppendLine($"    private static void {methodName}(Vu1State vu)");
        sb.AppendLine("    {");
        sb.AppendLine("        ref Vector128<float> vf = ref MemoryMarshal.GetArrayDataReference(vu.VF);");
        sb.AppendLine("        ref ushort vi = ref MemoryMarshal.GetArrayDataReference(vu.VI);");
        sb.AppendLine("        ref byte dm = ref MemoryMarshal.GetArrayDataReference(vu.DataMem);");
        sb.AppendLine("        Vector128<float> tmp = default;");
        sb.AppendLine("        ushort idxTmp = 0;");
        sb.AppendLine("        bool btTmp = false;");
        sb.AppendLine("        int jtTmp = 0;");
        sb.AppendLine("        _ = (tmp, idxTmp, btTmp, jtTmp);");
        sb.AppendLine("        Vector128<float> acc = vu.ACC;");
        sb.AppendLine("        int steps = 0;");

        _curUsage = ScanMethodUsage(img, nodes);
        _useLocalRegs = true;
        EmitDeclareAndLoadLocals(in _curUsage, sb, "        ");

        var nodeSet = new HashSet<int>(nodes);
        var labelNeeded = CollectJumpTargets(img, nodes, cleanSubs);
        foreach (int cur in nodes)
        {
            ref readonly var s = ref img.Slots[cur];
            if (!s.IsE && s.BranchOp == BranchOp.Jr && jrTargetsByReg.TryGetValue(s.Is & 15, out var set))
            {
                foreach (int t in set)
                    labelNeeded.Add(t);
            }
        }

        if (startSlot != nodes[0])
        {
            labelNeeded.Add(startSlot);
            sb.AppendLine($"        goto slot_{startSlot};");
        }

        EmitNodeList(img, nodes, nodeSet, labelNeeded, cleanSubs, jrTargetsByReg, usesFlags, isSubroutine: false, linkRegFilter: 0, sb, 8);
        _useLocalRegs = false;
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static HashSet<int> CollectJumpTargets(
        VuImageSpec img,
        List<int> nodes,
        Dictionary<int, SubInfo>? cleanSubs)
    {
        var targets = new HashSet<int>();
        for (int idx = 0; idx < nodes.Count; idx++)
        {
            int cur = nodes[idx];
            ref readonly var s = ref img.Slots[cur];
            if (s.IsE) continue;

            if (s.BranchOp != BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                if (img.Slots[d].IsE) continue;

                if (s.BranchOp == BranchOp.Bal && cleanSubs != null && cleanSubs.ContainsKey(s.BranchTargetSlot))
                {
                    int fall = (cur + 2) & 0x7FF;
                    if (idx + 1 >= nodes.Count || nodes[idx + 1] != fall)
                        targets.Add(fall);
                    continue;
                }

                if (s.BranchOp == BranchOp.Jr)
                    continue;

                int t = s.BranchTargetSlot & 0x7FF;
                targets.Add(t);
                if (s.BranchOp is not BranchOp.B and not BranchOp.Bal)
                {
                    int fall = (cur + 2) & 0x7FF;
                    if (idx + 1 >= nodes.Count || nodes[idx + 1] != fall)
                        targets.Add(fall);
                }
            }
            else
            {
                int next = (cur + 1) & 0x7FF;
                if (idx + 1 >= nodes.Count || nodes[idx + 1] != next)
                    targets.Add(next);
            }
        }
        return targets;
    }

    private static void EmitNodeList(
        VuImageSpec img,
        List<int> nodes,
        HashSet<int> nodeSet,
        HashSet<int> labelNeeded,
        Dictionary<int, SubInfo>? cleanSubs,
        Dictionary<int, SortedSet<int>>? jrTargetsByReg,
        bool usesFlags,
        bool isSubroutine,
        int linkRegFilter,
        StringBuilder sb,
        int indent)
    {
        string pad = new string(' ', indent);
        int pendingStepRun = 0;
        bool terminated = false;
        _efuRemainCycles = 0;

        void FlushSteps()
        {
            if (_efuRemainCycles > 0)
            {
                sb.AppendLine($"{pad}vu.P = vu.PendingP;");
                _efuRemainCycles = 0;
            }
            if (pendingStepRun > 0)
            {
                sb.AppendLine($"{pad}steps += {pendingStepRun};");
                pendingStepRun = 0;
            }
        }

        bool usedStepCap = false;
        string StepCapStmt()
        {
            usedStepCap = true;
            return "if (steps >= 65536) goto exit_stepcap;";
        }

        var macProducerSlots = new HashSet<int>();
        void AddMacProducer(int readSlot)
        {
            for (int p = readSlot - 4; p >= 0 && p >= readSlot - 64; p--)
            {
                if (img.SlotOwnerBit[p] < 0 || !img.Slots[p].Valid)
                    break;
                if (UpdatesMacFlagsUpper(img.Slots[p].UpperOp))
                {
                    macProducerSlots.Add(p);
                    return;
                }
            }
        }
        bool methodUsesStatusFlags = false;
        foreach (int r in nodes)
        {
            ref readonly var sr = ref img.Slots[r];
            if (SlotReadsFlags(in sr))
                AddMacProducer(r);
            if (!sr.IsImm && sr.LowerOp is LowerOp.Fsand or LowerOp.Fsor or LowerOp.Fsset)
                methodUsesStatusFlags = true;
            if (sr.IsE || sr.BranchOp != BranchOp.None)
            {
                ref readonly var sd = ref img.Slots[(r + 1) & 0x7FF];
                if (SlotReadsFlags(in sd))
                    AddMacProducer((r + 1) & 0x7FF);
                if (!sd.IsImm && sd.LowerOp is LowerOp.Fsand or LowerOp.Fsor or LowerOp.Fsset)
                    methodUsesStatusFlags = true;
            }
        }
        _curUsesStatusFlags = methodUsesStatusFlags;

        // Backward CFG dataflow for per-lane FP-vs-raw-int usage (used to sanitize exp-255 NaN/Inf on pure-float LQ/LQI/LQD)
        // plus backward basic-block lane liveness for ACC and VF1..VF31.
        var slotToIdx = new Dictionary<int, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++) slotToIdx[nodes[i]] = i;

        static void TransferSlotFpRaw(in DecodedSlot s, byte[] fp, byte[] raw)
        {
            bool hasUpper = s.UpperOp != UpperOp.Nop;
            bool hasLower = !s.IsImm && s.LowerOp != LowerOp.Nop;
            int upperOutReg = 0;
            uint upperOutMask = 0;
            if (hasUpper && !IsAccOp(s.UpperOp) && s.UpperOp != UpperOp.Clip)
            {
                upperOutReg = (UsesFtDestUpper(s.UpperOp) ? s.Ft : s.Fd) & 31;
                upperOutMask = s.UpperOp == UpperOp.Opmsub ? 0xEu : (s.Dest & 0xFu);
            }
            int lowerOutReg = 0;
            uint lowerOutMask = 0;
            if (hasLower && s.LowerOp is LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd or LowerOp.Move or LowerOp.Mr32 or LowerOp.Mfir or LowerOp.Rnext or LowerOp.Rget or LowerOp.Mfp)
            {
                lowerOutReg = s.It & 31;
                lowerOutMask = s.LowerDest & 0xFu;
            }
            bool conflict = upperOutReg != 0 && hasLower && LowerTouchesVf(in s, upperOutReg);

            if (lowerOutReg != 0 && !conflict)
            {
                fp[lowerOutReg] = (byte)(fp[lowerOutReg] & ~lowerOutMask);
                raw[lowerOutReg] = (byte)(raw[lowerOutReg] & ~lowerOutMask);
            }
            if (hasLower)
            {
                switch (s.LowerOp)
                {
                    case LowerOp.Sq or LowerOp.Sqi or LowerOp.Sqd:
                        raw[s.Is & 31] = (byte)(raw[s.Is & 31] | (s.LowerDest & 0xFu));
                        break;
                    case LowerOp.Move:
                        fp[s.Is & 31] = (byte)(fp[s.Is & 31] | (fp[s.It & 31] & (s.LowerDest & 0xFu)));
                        raw[s.Is & 31] = (byte)(raw[s.Is & 31] | (raw[s.It & 31] & (s.LowerDest & 0xFu)));
                        break;
                    case LowerOp.Mr32 or LowerOp.Mtir or LowerOp.Rinit or LowerOp.Rxor:
                        raw[s.Is & 31] = 0xF;
                        break;
                    case LowerOp.Sqrt:
                        fp[s.It & 31] = (byte)(fp[s.It & 31] | (byte)(8 >> (s.Ftf & 3)));
                        break;
                    case LowerOp.Esadd or LowerOp.Ersadd or LowerOp.Eleng or LowerOp.Erleng or
                         LowerOp.Eatanxy or LowerOp.Eatanxz or LowerOp.Esum or LowerOp.Ercpr or
                         LowerOp.Esqrt or LowerOp.Ersqrt or LowerOp.Esin or LowerOp.Eatan or LowerOp.Eexp:
                        fp[s.Is & 31] = 0xF;
                        break;
                    case LowerOp.Div or LowerOp.Rsqrt:
                        fp[s.Is & 31] = (byte)(fp[s.Is & 31] | (byte)(8 >> (s.Fsf & 3)));
                        fp[s.It & 31] = (byte)(fp[s.It & 31] | (byte)(8 >> (s.Ftf & 3)));
                        break;
                }
            }
            if (hasUpper)
            {
                if (upperOutReg != 0 && !conflict)
                {
                    fp[upperOutReg] = (byte)(fp[upperOutReg] & ~upperOutMask);
                    raw[upperOutReg] = (byte)(raw[upperOutReg] & ~upperOutMask);
                }
                if (s.UpperOp is UpperOp.AddBc or UpperOp.SubBc or UpperOp.MaddBc or UpperOp.MsubBc or
                    UpperOp.MaxBc or UpperOp.MinBc or UpperOp.MulBc or
                    UpperOp.AddABc or UpperOp.SubABc or UpperOp.MaddABc or UpperOp.MsubABc or UpperOp.MulABc)
                {
                    fp[s.Fs & 31] = (byte)(fp[s.Fs & 31] | (s.Dest & 0xFu));
                    fp[s.Ft & 31] = (byte)(fp[s.Ft & 31] | (byte)(8 >> (s.Bc & 3)));
                }
                else if (s.UpperOp == UpperOp.Clip)
                {
                    fp[s.Fs & 31] = (byte)(fp[s.Fs & 31] | 0xE);
                    fp[s.Ft & 31] = (byte)(fp[s.Ft & 31] | 0x1);
                }
                else if (s.UpperOp is UpperOp.Opmula or UpperOp.Opmsub)
                {
                    fp[s.Fs & 31] = (byte)(fp[s.Fs & 31] | 0xE);
                    fp[s.Ft & 31] = (byte)(fp[s.Ft & 31] | 0xE);
                }
                else if (s.UpperOp is UpperOp.Itof0 or UpperOp.Itof4 or UpperOp.Itof12 or UpperOp.Itof15)
                {
                    raw[s.Fs & 31] = (byte)(raw[s.Fs & 31] | (s.Dest & 0xFu));
                }
                else if (s.UpperOp is UpperOp.Ftoi0 or UpperOp.Ftoi4 or UpperOp.Ftoi12 or UpperOp.Ftoi15 or
                         UpperOp.Abs or UpperOp.MulQ or UpperOp.AddQ or UpperOp.MaddQ or UpperOp.SubQ or UpperOp.MsubQ or
                         UpperOp.MulI or UpperOp.AddI or UpperOp.MaddI or UpperOp.SubI or UpperOp.MsubI or
                         UpperOp.MaxI or UpperOp.MinI or
                         UpperOp.MulAq or UpperOp.AddAq or UpperOp.MaddAq or UpperOp.SubAq or UpperOp.MsubAq or
                         UpperOp.MulAi or UpperOp.AddAi or UpperOp.MaddAi or UpperOp.SubAi or UpperOp.MsubAi)
                {
                    fp[s.Fs & 31] = (byte)(fp[s.Fs & 31] | (s.Dest & 0xFu));
                }
                else
                {
                    fp[s.Fs & 31] = (byte)(fp[s.Fs & 31] | (s.Dest & 0xFu));
                    fp[s.Ft & 31] = (byte)(fp[s.Ft & 31] | (s.Dest & 0xFu));
                }
            }
            fp[0] = 0;
            raw[0] = 0;
        }

        byte[][] fpIn = new byte[nodes.Count][];
        byte[][] rawIn = new byte[nodes.Count][];
        for (int i = 0; i < nodes.Count; i++) { fpIn[i] = new byte[32]; rawIn[i] = new byte[32]; }
        byte[] fpTmp = new byte[32];
        byte[] rawTmp = new byte[32];
        byte[] lqSanitizeMask = new byte[nodes.Count];
        byte[] lqDelaySanitizeMask = new byte[nodes.Count];

        bool changed = true;
        int iterCap = 32;
        while (changed && iterCap-- > 0)
        {
            changed = false;
            for (int idx = nodes.Count - 1; idx >= 0; idx--)
            {
                int cur = nodes[idx];
                ref readonly var s = ref img.Slots[cur];
                Array.Clear(fpTmp);
                Array.Clear(rawTmp);

                void MergeSucc(int targetSlot)
                {
                    if (slotToIdx.TryGetValue(targetSlot & 0x7FF, out int si))
                    {
                        byte[] sf = fpIn[si], sr = rawIn[si];
                        for (int r = 1; r < 32; r++) { fpTmp[r] |= sf[r]; rawTmp[r] |= sr[r]; }
                    }
                    else
                    {
                        for (int r = 1; r < 32; r++) rawTmp[r] = 0xF;
                    }
                }

                if (s.IsE && s.BranchOp == BranchOp.None)
                {
                    for (int r = 1; r < 32; r++) rawTmp[r] = 0xF; // exit: preserve all live-out registers raw
                }
                else if (s.BranchOp != BranchOp.None)
                {
                    int d = (cur + 1) & 0x7FF;
                    ref readonly var ds = ref img.Slots[d];
                    if (s.IsE || ds.IsE || (s.BranchOp == BranchOp.Jr && isSubroutine))
                    {
                        for (int r = 1; r < 32; r++) rawTmp[r] = 0xF;
                    }
                    else if (s.BranchOp == BranchOp.Jr && jrTargetsByReg != null)
                    {
                        foreach (int t in jrTargetsByReg[s.Is & 15]) MergeSucc(t);
                    }
                    else if (s.BranchOp == BranchOp.Bal && cleanSubs != null && cleanSubs.ContainsKey(s.BranchTargetSlot))
                    {
                        for (int r = 1; r < 32; r++) rawTmp[r] = 0xF;
                    }
                    else
                    {
                        MergeSucc(s.BranchTargetSlot);
                        if (s.BranchOp is not (BranchOp.B or BranchOp.Bal or BranchOp.Jr or BranchOp.Jalr))
                            MergeSucc((cur + 2) & 0x7FF);
                    }
                    if (!ds.IsImm && ds.LowerOp is LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd && (ds.It & 31) != 0)
                    {
                        int lr = ds.It & 31;
                        lqDelaySanitizeMask[idx] = (byte)((ds.LowerDest & 0xFu) & fpTmp[lr] & ~rawTmp[lr]);
                    }
                    TransferSlotFpRaw(in ds, fpTmp, rawTmp);
                }
                else
                {
                    MergeSucc((cur + 1) & 0x7FF);
                }

                if (!s.IsImm && s.LowerOp is LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd && (s.It & 31) != 0)
                {
                    int lr = s.It & 31;
                    lqSanitizeMask[idx] = (byte)((s.LowerDest & 0xFu) & fpTmp[lr] & ~rawTmp[lr]);
                }
                TransferSlotFpRaw(in s, fpTmp, rawTmp);

                byte[] cf = fpIn[idx], cr = rawIn[idx];
                for (int r = 1; r < 32; r++)
                {
                    if (cf[r] != fpTmp[r] || cr[r] != rawTmp[r])
                    {
                        cf[r] = fpTmp[r];
                        cr[r] = rawTmp[r];
                        changed = true;
                    }
                }
            }
        }

        byte[] accLiveAfter = new byte[nodes.Count];
        byte[] upperVfLiveAfter = new byte[nodes.Count];
        byte[] lowerVfLiveAfter = new byte[nodes.Count];
        byte accLive = 0xF;
        Span<byte> vfLive = stackalloc byte[32];
        vfLive.Fill(0xF);

        for (int idx = nodes.Count - 1; idx >= 0; idx--)
        {
            int cur = nodes[idx];
            ref readonly var s = ref img.Slots[cur];
            bool fallthrough = idx + 1 < nodes.Count && nodes[idx + 1] == ((cur + 1) & 0x7FF) && !labelNeeded.Contains(nodes[idx + 1]);
            bool isLinear = !s.IsE && s.BranchOp == BranchOp.None && (s.IsImm || s.LowerOp != LowerOp.Xgkick);

            if (!fallthrough || !isLinear)
            {
                accLive = 0xF;
                vfLive.Fill(0xF);
                accLiveAfter[idx] = 0xF;
                upperVfLiveAfter[idx] = 0xF;
                lowerVfLiveAfter[idx] = 0xF;
                continue;
            }

            bool hasUpper = s.UpperOp != UpperOp.Nop;
            bool hasLower = !s.IsImm && s.LowerOp != LowerOp.Nop;

            int upperOutReg = 0;
            uint upperOutMask = 0;
            if (hasUpper && !IsAccOp(s.UpperOp) && s.UpperOp != UpperOp.Clip)
            {
                upperOutReg = (UsesFtDestUpper(s.UpperOp) ? s.Ft : s.Fd) & 31;
                upperOutMask = s.UpperOp == UpperOp.Opmsub ? 0xEu : (s.Dest & 0xFu);
            }

            int lowerOutReg = 0;
            uint lowerOutMask = 0;
            if (hasLower && s.LowerOp is LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd or LowerOp.Move or LowerOp.Mr32 or LowerOp.Mfir or LowerOp.Rnext or LowerOp.Rget or LowerOp.Mfp)
            {
                lowerOutReg = s.It & 31;
                lowerOutMask = s.LowerDest & 0xFu;
            }

            bool upperLowerConflict = upperOutReg != 0 && hasLower && LowerTouchesVf(in s, upperOutReg);

            accLiveAfter[idx] = accLive;
            lowerVfLiveAfter[idx] = (lowerOutReg != 0 && !upperLowerConflict) ? vfLive[lowerOutReg] : (byte)0xF;

            // Update vfLive backwards through Lower first (Lower executes after Upper in EmitBundle)
            if (lowerOutReg != 0 && !upperLowerConflict)
                vfLive[lowerOutReg] = (byte)(vfLive[lowerOutReg] & ~lowerOutMask);
            if (hasLower)
            {
                switch (s.LowerOp)
                {
                    case LowerOp.Sq or LowerOp.Sqi or LowerOp.Sqd or LowerOp.Move:
                        vfLive[s.Is & 31] = (byte)(vfLive[s.Is & 31] | (s.LowerDest & 0xFu));
                        break;
                    case LowerOp.Mr32 or LowerOp.Mtir or LowerOp.Rinit or LowerOp.Rxor or LowerOp.Sqrt or
                         LowerOp.Esadd or LowerOp.Ersadd or LowerOp.Eleng or LowerOp.Erleng or
                         LowerOp.Eatanxy or LowerOp.Eatanxz or LowerOp.Esum or LowerOp.Ercpr or
                         LowerOp.Esqrt or LowerOp.Ersqrt or LowerOp.Esin or LowerOp.Eatan or LowerOp.Eexp:
                        vfLive[s.Is & 31] = 0xF;
                        vfLive[s.It & 31] = 0xF;
                        break;
                    case LowerOp.Div or LowerOp.Rsqrt:
                        vfLive[s.Is & 31] = (byte)(vfLive[s.Is & 31] | (byte)(8 >> (s.Fsf & 3)));
                        vfLive[s.It & 31] = (byte)(vfLive[s.It & 31] | (byte)(8 >> (s.Ftf & 3)));
                        break;
                }
            }

            upperVfLiveAfter[idx] = (upperOutReg != 0 && !upperLowerConflict) ? vfLive[upperOutReg] : (byte)0xF;

            // Update accLive & vfLive backwards through Upper
            if (hasUpper)
            {
                if (IsAccOp(s.UpperOp))
                {
                    uint wMask = s.UpperOp == UpperOp.Opmula ? 0xEu : (s.Dest & 0xFu);
                    accLive = (byte)(accLive & ~wMask);
                    if (s.UpperOp is UpperOp.MaddA or UpperOp.MaddABc or UpperOp.MaddAi or UpperOp.MaddAq or
                        UpperOp.MsubA or UpperOp.MsubABc or UpperOp.MsubAi or UpperOp.MsubAq)
                    {
                        accLive = (byte)(accLive | wMask);
                    }
                }
                else
                {
                    if (upperOutReg != 0)
                    {
                        if (!upperLowerConflict)
                            vfLive[upperOutReg] = (byte)(vfLive[upperOutReg] & ~upperOutMask);
                        else
                            vfLive[upperOutReg] = 0xF;
                    }
                    if (s.UpperOp is UpperOp.Madd or UpperOp.MaddBc or UpperOp.MaddI or UpperOp.MaddQ or
                        UpperOp.Msub or UpperOp.MsubBc or UpperOp.MsubI or UpperOp.MsubQ)
                    {
                        accLive = (byte)(accLive | (s.Dest & 0xFu));
                    }
                    else if (s.UpperOp == UpperOp.Opmsub)
                    {
                        accLive = (byte)(accLive | 0xE);
                    }
                }

                // Mark Upper source lanes live
                if (s.UpperOp is UpperOp.AddBc or UpperOp.SubBc or UpperOp.MaddBc or UpperOp.MsubBc or
                    UpperOp.MaxBc or UpperOp.MinBc or UpperOp.MulBc or
                    UpperOp.AddABc or UpperOp.SubABc or UpperOp.MaddABc or UpperOp.MsubABc or UpperOp.MulABc)
                {
                    vfLive[s.Fs & 31] = (byte)(vfLive[s.Fs & 31] | (s.Dest & 0xFu));
                    vfLive[s.Ft & 31] = (byte)(vfLive[s.Ft & 31] | (byte)(8 >> (s.Bc & 3)));
                }
                else if (s.UpperOp == UpperOp.Clip)
                {
                    vfLive[s.Fs & 31] = (byte)(vfLive[s.Fs & 31] | 0xE);
                    vfLive[s.Ft & 31] = (byte)(vfLive[s.Ft & 31] | 0x1);
                }
                else if (s.UpperOp is UpperOp.Opmula or UpperOp.Opmsub)
                {
                    vfLive[s.Fs & 31] = (byte)(vfLive[s.Fs & 31] | 0xE);
                    vfLive[s.Ft & 31] = (byte)(vfLive[s.Ft & 31] | 0xE);
                }
                else if (s.UpperOp is UpperOp.Itof0 or UpperOp.Itof4 or UpperOp.Itof12 or UpperOp.Itof15 or
                         UpperOp.Ftoi0 or UpperOp.Ftoi4 or UpperOp.Ftoi12 or UpperOp.Ftoi15 or
                         UpperOp.Abs or UpperOp.MulQ or UpperOp.AddQ or UpperOp.MaddQ or UpperOp.SubQ or UpperOp.MsubQ or
                         UpperOp.MulI or UpperOp.AddI or UpperOp.MaddI or UpperOp.SubI or UpperOp.MsubI or
                         UpperOp.MaxI or UpperOp.MinI or
                         UpperOp.MulAq or UpperOp.AddAq or UpperOp.MaddAq or UpperOp.SubAq or UpperOp.MsubAq or
                         UpperOp.MulAi or UpperOp.AddAi or UpperOp.MaddAi or UpperOp.SubAi or UpperOp.MsubAi)
                {
                    vfLive[s.Fs & 31] = (byte)(vfLive[s.Fs & 31] | (s.Dest & 0xFu));
                }
                else
                {
                    vfLive[s.Fs & 31] = (byte)(vfLive[s.Fs & 31] | (s.Dest & 0xFu));
                    vfLive[s.Ft & 31] = (byte)(vfLive[s.Ft & 31] | (s.Dest & 0xFu));
                }
            }
            vfLive[0] = 0xF;
        }

        _curAccLiveAfter = 0xF;
        _curUpperVfLiveAfter = 0xF;
        _curLowerVfLiveAfter = 0xF;
        _curLqSanitizeMask = 0;

        for (int idx = 0; idx < nodes.Count; idx++)
        {
            int cur = nodes[idx];
            if (labelNeeded.Contains(cur))
            {
                FlushSteps();
                sb.AppendLine($"    slot_{cur}:");
                terminated = false;
            }

            ref readonly var s = ref img.Slots[cur];
            bool curProducesFlags = macProducerSlots.Contains(cur);
            string curFlagsExpr = curProducesFlags ? (isSubroutine ? "updateFlags" : "true") : "false";
            bool? curConstFlags = curProducesFlags ? (isSubroutine ? (bool?)null : true) : false;

            if (s.IsE && s.BranchOp == BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                bool dProducesFlags = macProducerSlots.Contains(d);
                string dFlagsExpr = dProducesFlags ? (isSubroutine ? "updateFlags" : "true") : "false";
                bool? dConstFlags = dProducesFlags ? (isSubroutine ? (bool?)null : true) : false;
                EmitBundle(img, $"{cur}", in s, curFlagsExpr, curConstFlags, sb, pad);
                EmitBundle(img, $"{d}_d{cur}", in img.Slots[d], dFlagsExpr, dConstFlags, sb, pad);
                pendingStepRun += 2;
                FlushSteps();
                int exitAddr = ((cur + 2) & 0x7FF) << 3;
                EmitFlushLocals(in _curUsage, sb, pad);
                sb.AppendLine($"{pad}vu.ACC = acc;");
                sb.AppendLine($"{pad}vu.Tpc = 0x{exitAddr:X4};");
                sb.AppendLine($"{pad}vu.TotalInstructionsExecuted += steps;");
                sb.AppendLine($"{pad}return;");
                terminated = true;
                continue;
            }

            if (s.BranchOp != BranchOp.None)
            {
                int d = (cur + 1) & 0x7FF;
                ref readonly var ds = ref img.Slots[d];
                bool dProducesFlags = macProducerSlots.Contains(d);
                string dFlagsExpr = dProducesFlags ? (isSubroutine ? "updateFlags" : "true") : "false";
                bool? dConstFlags = dProducesFlags ? (isSubroutine ? (bool?)null : true) : false;

                string? condExpr = s.BranchOp switch
                {
                    BranchOp.Ibeq => $"({ReadViExpr(s.It)} == {ReadViExpr(s.Is)})",
                    BranchOp.Ibne => $"({ReadViExpr(s.It)} != {ReadViExpr(s.Is)})",
                    BranchOp.Ibltz => $"((short){ReadViExpr(s.Is)} < 0)",
                    BranchOp.Ibgtz => $"((short){ReadViExpr(s.Is)} > 0)",
                    BranchOp.Iblez => $"((short){ReadViExpr(s.Is)} <= 0)",
                    BranchOp.Ibgez => $"((short){ReadViExpr(s.Is)} >= 0)",
                    _ => null
                };

                string condVar = "btTmp";
                if (condExpr != null)
                    sb.AppendLine($"{pad}{condVar} = {condExpr};");

                string? jrVar = null;
                if (s.BranchOp is BranchOp.Jr or BranchOp.Jalr)
                {
                    jrVar = "jtTmp";
                    sb.AppendLine($"{pad}{jrVar} = {ReadViExpr(s.Is)} & 0x7FF;");
                }

                _curLqSanitizeMask = lqSanitizeMask[idx];
                EmitBundle(img, $"{cur}", in s, curFlagsExpr, curConstFlags, sb, pad);
                _curLqSanitizeMask = lqDelaySanitizeMask[idx];
                EmitBundle(img, $"{d}_d{cur}", in ds, dFlagsExpr, dConstFlags, sb, pad);
                _curLqSanitizeMask = 0;
                pendingStepRun += 2;

                if (s.BranchOp is BranchOp.Bal or BranchOp.Jalr)
                {
                    int lr = s.It & 15;
                    if (lr != 0 && !SlotWritesVi(in ds, lr))
                    {
                        int retSlot = (cur + 2) & 0x7FF;
                        EmitWriteVi(lr, $"{retSlot}", sb, pad);
                    }
                }

                FlushSteps();

                if (s.IsE || ds.IsE)
                {
                    EmitFlushLocals(in _curUsage, sb, pad);
                    sb.AppendLine($"{pad}vu.ACC = acc;");
                    if (condExpr != null)
                        sb.AppendLine($"{pad}vu.Tpc = (ushort)(({condVar} ? {s.BranchTargetSlot} : {(cur + 2) & 0x7FF}) << 3);");
                    else if (jrVar != null)
                        sb.AppendLine($"{pad}vu.Tpc = (ushort)({jrVar} << 3);");
                    else
                        sb.AppendLine($"{pad}vu.Tpc = 0x{(s.BranchTargetSlot << 3):X4};");
                    sb.AppendLine($"{pad}vu.TotalInstructionsExecuted += steps;");
                    sb.AppendLine($"{pad}return;");
                    terminated = true;
                    continue;
                }

                if (s.BranchOp == BranchOp.Jr)
                {
                    if (isSubroutine)
                    {
                        EmitFlushLocals(in _curUsage, sb, pad);
                        sb.AppendLine($"{pad}return;");
                    }
                    else
                    {
                        usedStepCap = true;
                        sb.AppendLine($"{pad}switch ({jrVar})");
                        sb.AppendLine($"{pad}{{");
                        foreach (int t in jrTargetsByReg![s.Is & 15])
                            sb.AppendLine($"{pad}    case {t}: goto slot_{t};");
                        sb.AppendLine($"{pad}    default: goto exit_stepcap;");
                        sb.AppendLine($"{pad}}}");
                    }
                    terminated = true;
                    continue;
                }

                if (s.BranchOp == BranchOp.Bal && cleanSubs != null && cleanSubs.TryGetValue(s.BranchTargetSlot, out var sub))
                {
                    string subFlags = usesFlags ? "true" : "false";
                    EmitFlushLocals(in _curUsage, sb, pad);
                    sb.AppendLine($"{pad}{sub.MethodName}(vu, ref steps, ref acc, {subFlags});");
                    if (isSubroutine)
                        sb.AppendLine($"{pad}if (steps >= 65536) return;");
                    else
                        sb.AppendLine($"{pad}if (steps >= 65536) {{ vu.ACC = acc; vu.TotalInstructionsExecuted += steps; return; }}");
                    EmitReloadLocals(in _curUsage, sb, pad);
                    int fall = (cur + 2) & 0x7FF;
                    if (idx + 1 >= nodes.Count || nodes[idx + 1] != fall)
                    {
                        sb.AppendLine($"{pad}goto slot_{fall};");
                        terminated = true;
                    }
                    else
                    {
                        terminated = false;
                    }
                    continue;
                }

                int targetSlot = s.BranchTargetSlot & 0x7FF;
                if (condExpr == null)
                {
                    if (targetSlot <= cur)
                        sb.AppendLine($"{pad}{StepCapStmt()}");
                    sb.AppendLine($"{pad}goto slot_{targetSlot};");
                    terminated = true;
                }
                else
                {
                    if (targetSlot <= cur)
                        sb.AppendLine($"{pad}if ({condVar}) {{ {StepCapStmt()} goto slot_{targetSlot}; }}");
                    else
                        sb.AppendLine($"{pad}if ({condVar}) goto slot_{targetSlot};");

                    int fall = (cur + 2) & 0x7FF;
                    if (idx + 1 >= nodes.Count || nodes[idx + 1] != fall)
                    {
                        sb.AppendLine($"{pad}goto slot_{fall};");
                        terminated = true;
                    }
                    else
                    {
                        terminated = false;
                    }
                }
                continue;
            }

            // Normal linear slot
            _curAccLiveAfter = accLiveAfter[idx];
            _curUpperVfLiveAfter = upperVfLiveAfter[idx];
            _curLowerVfLiveAfter = lowerVfLiveAfter[idx];
            _curLqSanitizeMask = lqSanitizeMask[idx];
            EmitBundle(img, $"{cur}", in s, curFlagsExpr, curConstFlags, sb, pad);
            _curAccLiveAfter = 0xF;
            _curUpperVfLiveAfter = 0xF;
            _curLowerVfLiveAfter = 0xF;
            _curLqSanitizeMask = 0;
            pendingStepRun++;

            int next = (cur + 1) & 0x7FF;
            if (idx + 1 >= nodes.Count || nodes[idx + 1] != next)
            {
                FlushSteps();
                if (next <= cur)
                    sb.AppendLine($"{pad}{StepCapStmt()}");
                sb.AppendLine($"{pad}goto slot_{next};");
                terminated = true;
            }
            else
            {
                terminated = false;
            }
        }

        if (!terminated)
        {
            FlushSteps();
            EmitFlushLocals(in _curUsage, sb, pad);
            if (!isSubroutine)
            {
                sb.AppendLine($"{pad}vu.ACC = acc;");
                sb.AppendLine($"{pad}vu.TotalInstructionsExecuted += steps;");
            }
            if (usedStepCap)
                sb.AppendLine($"{pad}return;");
        }

        if (usedStepCap)
        {
            sb.AppendLine("    exit_stepcap:");
            EmitFlushLocals(in _curUsage, sb, pad);
            if (!isSubroutine)
            {
                sb.AppendLine($"{pad}vu.ACC = acc;");
                sb.AppendLine($"{pad}vu.TotalInstructionsExecuted += steps;");
            }
            sb.AppendLine($"{pad}return;");
        }
    }

    private static void EmitBundle(
        VuImageSpec img,
        string tag,
        in DecodedSlot s,
        string flagsExpr,
        bool? constFlags,
        StringBuilder sb,
        string pad)
    {
        if (_efuRemainCycles > 0 && --_efuRemainCycles == 0)
            sb.AppendLine($"{pad}vu.P = vu.PendingP;");

        bool hasUpper = s.UpperOp != UpperOp.Nop;
        bool hasLower = !s.IsImm && s.LowerOp != LowerOp.Nop;

        bool upperWritesPendingVf = false;
        int pendingReg = 0;
        uint pendingMask = 0;
        string? pendingTemp = null;

        if (hasUpper)
        {
            if (IsAccOp(s.UpperOp))
            {
                EmitAccUpper(in s, tag, flagsExpr, constFlags, sb, pad);
            }
            else if (s.UpperOp == UpperOp.Clip)
            {
                sb.AppendLine($"{pad}vu.ExecClip({ReadVfExpr(s.Fs)}, {ReadVfExpr(s.Ft)});");
            }
            else
            {
                bool updatesFlags = UpdatesMacFlagsUpper(s.UpperOp);
                int outReg = UsesFtDestUpper(s.UpperOp) ? s.Ft : s.Fd;
                uint outMask = s.UpperOp == UpperOp.Opmsub ? 0xEu : s.Dest;
                string resExpr = BuildUpperResultExpr(in s);

                bool needFlags = updatesFlags && constFlags != false;
                bool isSelfNoOp = !needFlags && outReg != 0 && resExpr == ReadVfExpr(outReg) &&
                    (!hasLower || !LowerWritesVf(in s, outReg));
                bool needResultVar = !isSelfNoOp && (needFlags || outReg != 0);
                if (needResultVar)
                {
                    bool deferWrite = outReg != 0 && outMask != 0 && hasLower && LowerTouchesVf(in s, outReg);
                    byte liveAfter = deferWrite ? (byte)0xF : _curUpperVfLiveAfter;
                    if (!needFlags && !deferWrite && outReg != 0 && outMask != 0)
                    {
                        EmitWriteVf(outReg, resExpr, outMask, sb, pad, liveAfter);
                    }
                    else
                    {
                        sb.AppendLine($"{pad}tmp = {resExpr};");
                        if (updatesFlags)
                        {
                            string flagMethod = _curUsesStatusFlags ? "UpdateMacAndStatusFlags" : "UpdateMacFlagsOnly";
                            if (constFlags == true)
                                sb.AppendLine($"{pad}vu.{flagMethod}(tmp, 0x{outMask:X}u);");
                            else if (constFlags == null)
                                sb.AppendLine($"{pad}if ({flagsExpr}) vu.{flagMethod}(tmp, 0x{outMask:X}u);");
                        }

                        if (outReg != 0 && outMask != 0)
                        {
                            if (deferWrite)
                            {
                                upperWritesPendingVf = true;
                                pendingReg = outReg;
                                pendingMask = outMask;
                                pendingTemp = "tmp";
                            }
                            else
                            {
                                EmitWriteVf(outReg, "tmp", outMask, sb, pad, liveAfter);
                            }
                        }
                    }
                }
            }
        }

        if (hasLower)
        {
            EmitLower(in s, tag, sb, pad);
        }

        if (upperWritesPendingVf && pendingTemp != null)
        {
            EmitWriteVf(pendingReg, pendingTemp, pendingMask, sb, pad);
        }

        if (s.IsImm)
        {
            string iTarget = _useLocalRegs ? "regI" : "vu.I";
            sb.AppendLine($"{pad}{iTarget} = BitConverter.UInt32BitsToSingle(0x{s.LoiBits:X8}u);");
        }
    }

    private static string BroadcastExpr(int reg, int bc)
    {
        if ((reg & 31) == 0)
            return bc == 3 ? "Vector128<float>.One" : "Vector128<float>.Zero";
        return $"Ps2Vu0.BroadcastBc({ReadVfExpr(reg)}, {bc}u)";
    }

    private static string EmitAddBcExpr(int fsReg, int ftReg, int bc)
    {
        string fs = ReadVfExpr(fsReg);
        if ((ftReg & 31) == 0 && bc < 3) return fs;
        return $"({fs} + {BroadcastExpr(ftReg, bc)})";
    }

    private static string EmitSubBcExpr(int fsReg, int ftReg, int bc)
    {
        string fs = ReadVfExpr(fsReg);
        if ((ftReg & 31) == 0 && bc < 3) return fs;
        return $"({fs} - {BroadcastExpr(ftReg, bc)})";
    }

    private static string EmitMulBcExpr(int fsReg, int ftReg, int bc)
    {
        string fs = ReadVfExpr(fsReg);
        if ((ftReg & 31) == 0)
            return bc == 3 ? fs : "Vector128<float>.Zero";
        return $"({fs} * {BroadcastExpr(ftReg, bc)})";
    }

    private static string EmitMaddBcExpr(int fsReg, int ftReg, int bc)
    {
        string fs = ReadVfExpr(fsReg);
        if ((ftReg & 31) == 0)
            return bc == 3 ? $"(acc + {fs})" : "acc";
        return $"FmaAdd({fs}, {BroadcastExpr(ftReg, bc)}, acc)";
    }

    private static string EmitMsubBcExpr(int fsReg, int ftReg, int bc)
    {
        string fs = ReadVfExpr(fsReg);
        if ((ftReg & 31) == 0)
            return bc == 3 ? $"(acc - {fs})" : "acc";
        return $"FmaSub({fs}, {BroadcastExpr(ftReg, bc)}, acc)";
    }

    private static void EmitAccUpper(in DecodedSlot s, string tag, string flagsExpr, bool? constFlags, StringBuilder sb, string pad)
    {
        if (s.UpperOp == UpperOp.Opmula)
        {
            sb.AppendLine($"{pad}vu.ExecOpmulaFast(ref acc, {ReadVfExpr(s.Fs)}, {ReadVfExpr(s.Ft)}, {flagsExpr});");
            return;
        }

        string fs = ReadVfExpr(s.Fs);
        string ft = ReadVfExpr(s.Ft);
        string qExpr = _useLocalRegs ? "regQ" : "vu.Q";
        string iExpr = _useLocalRegs ? "regI" : "vu.I";
        string expr = s.UpperOp switch
        {
            UpperOp.AddABc => EmitAddBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.SubABc => EmitSubBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MaddABc => EmitMaddBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MsubABc => EmitMsubBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MulABc => EmitMulBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MulAq => $"({fs} * Vector128.Create({qExpr}))",
            UpperOp.MulAi => $"({fs} * Vector128.Create({iExpr}))",
            UpperOp.AddAq => $"({fs} + Vector128.Create({qExpr}))",
            UpperOp.MaddAq => $"FmaAdd({fs}, Vector128.Create({qExpr}), acc)",
            UpperOp.AddAi => $"({fs} + Vector128.Create({iExpr}))",
            UpperOp.MaddAi => $"FmaAdd({fs}, Vector128.Create({iExpr}), acc)",
            UpperOp.SubAq => $"({fs} - Vector128.Create({qExpr}))",
            UpperOp.MsubAq => $"FmaSub({fs}, Vector128.Create({qExpr}), acc)",
            UpperOp.SubAi => $"({fs} - Vector128.Create({iExpr}))",
            UpperOp.MsubAi => $"FmaSub({fs}, Vector128.Create({iExpr}), acc)",
            UpperOp.AddA => $"({fs} + {ft})",
            UpperOp.MaddA => $"FmaAdd({fs}, {ft}, acc)",
            UpperOp.MulA => $"({fs} * {ft})",
            UpperOp.SubA => (s.Fs == 0 && s.Ft == 0) ? "Vector128<float>.Zero" : $"({fs} - {ft})",
            UpperOp.MsubA => $"FmaSub({fs}, {ft}, acc)",
            _ => "acc"
        };

        bool canOverwriteAcc = s.Dest == 0xFu || (_curAccLiveAfter & ~s.Dest) == 0;
        if (constFlags == false)
        {
            if (canOverwriteAcc && s.Dest != 0)
                sb.AppendLine($"{pad}acc = {expr};");
            else if (s.Dest != 0)
                sb.AppendLine($"{pad}acc = Ps2Vu0.BlendMask(acc, {expr}, 0x{s.Dest:X}u);");
            return;
        }

        sb.AppendLine($"{pad}tmp = {expr};");
        string flagMethod = _curUsesStatusFlags ? "UpdateMacAndStatusFlags" : "UpdateMacFlagsOnly";
        if (constFlags == true)
            sb.AppendLine($"{pad}vu.{flagMethod}(tmp, 0x{s.Dest:X}u);");
        else if (constFlags == null)
            sb.AppendLine($"{pad}if ({flagsExpr}) vu.{flagMethod}(tmp, 0x{s.Dest:X}u);");

        if (canOverwriteAcc && s.Dest != 0)
            sb.AppendLine($"{pad}acc = tmp;");
        else if (s.Dest != 0)
            sb.AppendLine($"{pad}acc = Ps2Vu0.BlendMask(acc, tmp, 0x{s.Dest:X}u);");
    }

    private static string BuildUpperResultExpr(in DecodedSlot s)
    {
        string fs = ReadVfExpr(s.Fs);
        string ft = ReadVfExpr(s.Ft);
        string qExpr = _useLocalRegs ? "regQ" : "vu.Q";
        string iExpr = _useLocalRegs ? "regI" : "vu.I";
        return s.UpperOp switch
        {
            UpperOp.AddBc => EmitAddBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.SubBc => EmitSubBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MaddBc => EmitMaddBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MsubBc => EmitMsubBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MaxBc => $"VuMax({fs}, {BroadcastExpr(s.Ft, s.Bc)})",
            UpperOp.MinBc => $"VuMin({fs}, {BroadcastExpr(s.Ft, s.Bc)})",
            UpperOp.MulBc => EmitMulBcExpr(s.Fs, s.Ft, s.Bc),
            UpperOp.MulQ => $"({fs} * Vector128.Create({qExpr}))",
            UpperOp.MaxI => $"VuMax({fs}, Vector128.Create({iExpr}))",
            UpperOp.MulI => $"({fs} * Vector128.Create({iExpr}))",
            UpperOp.MinI => $"VuMin({fs}, Vector128.Create({iExpr}))",
            UpperOp.AddQ => $"({fs} + Vector128.Create({qExpr}))",
            UpperOp.MaddQ => $"FmaAdd({fs}, Vector128.Create({qExpr}), acc)",
            UpperOp.AddI => $"({fs} + Vector128.Create({iExpr}))",
            UpperOp.MaddI => $"FmaAdd({fs}, Vector128.Create({iExpr}), acc)",
            UpperOp.SubQ => $"({fs} - Vector128.Create({qExpr}))",
            UpperOp.MsubQ => $"FmaSub({fs}, Vector128.Create({qExpr}), acc)",
            UpperOp.SubI => $"({fs} - Vector128.Create({iExpr}))",
            UpperOp.MsubI => $"FmaSub({fs}, Vector128.Create({iExpr}), acc)",
            UpperOp.Add => $"({fs} + {ft})",
            UpperOp.Madd => $"FmaAdd({fs}, {ft}, acc)",
            UpperOp.Mul => $"({fs} * {ft})",
            UpperOp.Max => s.Fs == s.Ft ? fs : $"VuMax({fs}, {ft})",
            UpperOp.Sub => (s.Fs == 0 && s.Ft == 0) ? "Vector128<float>.Zero" : $"({fs} - {ft})",
            UpperOp.Msub => $"FmaSub({fs}, {ft}, acc)",
            UpperOp.Opmsub => $"Vu1State.ExecOpmsubFast(acc, {fs}, {ft})",
            UpperOp.Min => s.Fs == s.Ft ? fs : $"VuMin({fs}, {ft})",
            UpperOp.Itof0 => $"Vector128.ConvertToSingle({fs}.AsInt32())",
            UpperOp.Itof4 => $"Vu1State.ExecItof({fs}, 4)",
            UpperOp.Itof12 => $"Vu1State.ExecItof({fs}, 12)",
            UpperOp.Itof15 => $"Vu1State.ExecItof({fs}, 15)",
            UpperOp.Ftoi0 => $"Vu1State.ExecFtoi0({fs})",
            UpperOp.Ftoi4 => $"Vu1State.ExecFtoi({fs}, 4)",
            UpperOp.Ftoi12 => $"Vu1State.ExecFtoi({fs}, 12)",
            UpperOp.Ftoi15 => $"Vu1State.ExecFtoi({fs}, 15)",
            UpperOp.Abs => $"Vu1State.ExecAbs({fs})",
            _ => fs
        };
    }

    private static void EmitLower(in DecodedSlot s, string tag, StringBuilder sb, string pad)
    {
        switch (s.LowerOp)
        {
            case LowerOp.Lq:
            {
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, LoadSanitizedQwExpr(AddrExpr(s.Is, s.Imm11), s.LowerDest), s.LowerDest, sb, pad, _curLowerVfLiveAfter);
                break;
            }
            case LowerOp.Sq:
                EmitStoreQw(AddrExpr(s.It, s.Imm11), ReadVfExpr(s.Is), s.LowerDest, tag, sb, pad);
                break;
            case LowerOp.Ilw:
                EmitWriteVi(s.It, LoadInt16DirectExpr(AddrExpr(s.Is, s.Imm11), s.LowerDest), sb, pad);
                break;
            case LowerOp.Isw:
                EmitStoreInt16Direct(AddrExpr(s.Is, s.Imm11), ReadViExpr(s.It), s.LowerDest, sb, pad);
                break;
            case LowerOp.Iaddiu:
                EmitWriteVi(s.It, $"unchecked((ushort)({ReadViExpr(s.Is)} + {s.Uimm15}u))", sb, pad);
                break;
            case LowerOp.Isubiu:
                EmitWriteVi(s.It, $"unchecked((ushort)({ReadViExpr(s.Is)} - {s.Uimm15}u))", sb, pad);
                break;
            case LowerOp.Fceq:
                EmitWriteVi(1, $"(ushort)((vu.ClipFlags & 0xFFFFFFu) == 0x{s.Imm24:X}u ? 1 : 0)", sb, pad);
                break;
            case LowerOp.Fcset:
                sb.AppendLine($"{pad}vu.ClipFlags = 0x{s.Imm24:X}u;");
                break;
            case LowerOp.Fcand:
                EmitWriteVi(1, $"(ushort)((vu.ClipFlags & 0x{s.Imm24:X}u) != 0 ? 1 : 0)", sb, pad);
                break;
            case LowerOp.Fcor:
                EmitWriteVi(1, $"(ushort)(((vu.ClipFlags | 0x{s.Imm24:X}u) & 0xFFFFFFu) == 0xFFFFFFu ? 1 : 0)", sb, pad);
                break;
            case LowerOp.Fsset:
                sb.AppendLine($"{pad}vu.StatusFlags = (ushort)((vu.StatusFlags & 0x03Fu) | 0x{s.Imm24 & 0xFC0u:X}u);");
                break;
            case LowerOp.Fsand:
                EmitWriteVi(s.It, $"(ushort)(vu.StatusFlags & 0x{s.Imm24 & 0xFFFu:X}u)", sb, pad);
                break;
            case LowerOp.Fsor:
                EmitWriteVi(s.It, $"(ushort)((vu.StatusFlags | 0x{s.Imm24 & 0xFFFu:X}u) & 0xFFFu)", sb, pad);
                break;
            case LowerOp.Fmeq:
                EmitWriteVi(s.It, $"(ushort)(vu.MacFlags == {ReadViExpr(s.Is)} ? 1 : 0)", sb, pad);
                break;
            case LowerOp.Fmand:
                EmitWriteVi(s.It, $"(ushort)(vu.MacFlags & {ReadViExpr(s.Is)})", sb, pad);
                break;
            case LowerOp.Fmor:
                EmitWriteVi(s.It, $"(ushort)(vu.MacFlags | {ReadViExpr(s.Is)})", sb, pad);
                break;
            case LowerOp.Fcget:
                EmitWriteVi(s.It, "(ushort)(vu.ClipFlags & 0xFFFu)", sb, pad);
                break;
            case LowerOp.Iadd:
                EmitWriteVi(s.Id, $"unchecked((ushort)({ReadViExpr(s.Is)} + {ReadViExpr(s.It)}))", sb, pad);
                break;
            case LowerOp.Isub:
                EmitWriteVi(s.Id, $"unchecked((ushort)({ReadViExpr(s.Is)} - {ReadViExpr(s.It)}))", sb, pad);
                break;
            case LowerOp.Iaddi:
                EmitWriteVi(s.It, $"unchecked((ushort)({ReadViExpr(s.Is)} + ({s.Imm11})))", sb, pad);
                break;
            case LowerOp.Iand:
                EmitWriteVi(s.Id, $"(ushort)({ReadViExpr(s.Is)} & {ReadViExpr(s.It)})", sb, pad);
                break;
            case LowerOp.Ior:
                EmitWriteVi(s.Id, $"(ushort)({ReadViExpr(s.Is)} | {ReadViExpr(s.It)})", sb, pad);
                break;
            case LowerOp.Move:
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, ReadVfExpr(s.Is), s.LowerDest, sb, pad, _curLowerVfLiveAfter);
                break;
            case LowerOp.Mr32:
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, (s.Is & 31) == 0 ? "Vf0Mr32" : $"Vu1State.ExecMr32({ReadVfExpr(s.Is)})", s.LowerDest, sb, pad, _curLowerVfLiveAfter);
                break;
            case LowerOp.Lqi:
            {
                sb.AppendLine($"{pad}idxTmp = {ReadViExpr(s.Is)};");
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, LoadSanitizedQwExpr("(idxTmp & 0x3FF)", s.LowerDest), s.LowerDest, sb, pad, _curLowerVfLiveAfter);
                EmitWriteVi(s.Is, "(ushort)((idxTmp + 1) & 0x3FF)", sb, pad);
                break;
            }
            case LowerOp.Sqi:
            {
                sb.AppendLine($"{pad}idxTmp = {ReadViExpr(s.It)};");
                EmitStoreQw("(idxTmp & 0x3FF)", ReadVfExpr(s.Is), s.LowerDest, tag, sb, pad);
                EmitWriteVi(s.It, "(ushort)((idxTmp + 1) & 0x3FF)", sb, pad);
                break;
            }
            case LowerOp.Lqd:
            {
                sb.AppendLine($"{pad}idxTmp = (ushort)(({ReadViExpr(s.Is)} - 1) & 0x3FF);");
                EmitWriteVi(s.Is, "idxTmp", sb, pad);
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, LoadSanitizedQwExpr("idxTmp", s.LowerDest), s.LowerDest, sb, pad, _curLowerVfLiveAfter);
                break;
            }
            case LowerOp.Sqd:
            {
                sb.AppendLine($"{pad}idxTmp = (ushort)(({ReadViExpr(s.It)} - 1) & 0x3FF);");
                EmitWriteVi(s.It, "idxTmp", sb, pad);
                EmitStoreQw("idxTmp", ReadVfExpr(s.Is), s.LowerDest, tag, sb, pad);
                break;
            }
            case LowerOp.Div:
            {
                string qTarget = _useLocalRegs ? "regQ" : "vu.Q";
                sb.AppendLine($"{pad}{qTarget} = Vu1State.VuDivFast({ReadVfElemExpr(s.Is, s.Fsf)}, {ReadVfElemExpr(s.It, s.Ftf)});");
                break;
            }
            case LowerOp.Sqrt:
            {
                string qTarget = _useLocalRegs ? "regQ" : "vu.Q";
                sb.AppendLine($"{pad}{qTarget} = Ps2Float.Sqrt({ReadVfElemExpr(s.It, s.Ftf)});");
                break;
            }
            case LowerOp.Rsqrt:
            {
                string qTarget = _useLocalRegs ? "regQ" : "vu.Q";
                sb.AppendLine($"{pad}{qTarget} = Vu1State.VuRsqrtFast({ReadVfElemExpr(s.Is, s.Fsf)}, {ReadVfElemExpr(s.It, s.Ftf)});");
                break;
            }
            case LowerOp.WaitQ:
                break;
            case LowerOp.WaitP:
                if (_efuRemainCycles > 0)
                {
                    sb.AppendLine($"{pad}vu.P = vu.PendingP;");
                    _efuRemainCycles = 0;
                }
                break;
            case LowerOp.Mtir:
                EmitWriteVi(s.It, $"(ushort){ReadVfExpr(s.Is)}.AsUInt32().GetElement({s.Fsf})", sb, pad);
                break;
            case LowerOp.Mfir:
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, $"Vector128.Create((int)(short){ReadViExpr(s.Is)}).AsSingle()", s.LowerDest, sb, pad);
                break;
            case LowerOp.Ilwr:
                EmitWriteVi(s.It, LoadInt16DirectExpr($"({ReadViExpr(s.Is)} & 0x3FF)", s.LowerDest), sb, pad);
                break;
            case LowerOp.Iswr:
                EmitStoreInt16Direct($"({ReadViExpr(s.Is)} & 0x3FF)", ReadViExpr(s.It), s.LowerDest, sb, pad);
                break;
            case LowerOp.Rnext:
                EmitWriteVfOrCall(s.It, "vu.ExecRnext()", s.LowerDest, sb, pad);
                break;
            case LowerOp.Rget:
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, "vu.ExecRget()", s.LowerDest, sb, pad);
                break;
            case LowerOp.Rinit:
                sb.AppendLine($"{pad}vu.ExecRinit({ReadVfExpr(s.Is)}, {s.Fsf});");
                break;
            case LowerOp.Rxor:
                sb.AppendLine($"{pad}vu.ExecRxor({ReadVfExpr(s.Is)}, {s.Fsf});");
                break;
            case LowerOp.Mfp:
                if (s.It != 0 && s.LowerDest != 0)
                    EmitWriteVf(s.It, "Vector128.Create(vu.P)", s.LowerDest, sb, pad);
                break;
            case LowerOp.Xtop:
                EmitWriteVi(s.It, "vu.Top", sb, pad);
                break;
            case LowerOp.Xitop:
                EmitWriteVi(s.It, "vu.Itop", sb, pad);
                break;
            case LowerOp.Xgkick:
                sb.AppendLine($"{pad}vu.ExecXgKickAddr({ReadViExpr(s.Is)});");
                break;
            case LowerOp.Esadd:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2)", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Ersadd:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("Vu1State.VuDivFast(1.0f, v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2))", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Eleng:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("Ps2Float.Sqrt(v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2))", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Erleng:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("Vu1State.VuRsqrtFast(1.0f, v.GetElement(0) * v.GetElement(0) + v.GetElement(1) * v.GetElement(1) + v.GetElement(2) * v.GetElement(2))", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Eatanxy:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("MathF.Atan2(v.GetElement(1), v.GetElement(0))", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Eatanxz:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("MathF.Atan2(v.GetElement(2), v.GetElement(0))", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Esum:
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    var v = {ReadVfExpr(s.Is)};");
                EmitEfuWrite("v.GetElement(0) + v.GetElement(1) + v.GetElement(2) + v.GetElement(3)", sb, pad + "    ");
                sb.AppendLine($"{pad}}}");
                break;
            case LowerOp.Ercpr:
                EmitEfuWrite($"Vu1State.VuDivFast(1.0f, {ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
            case LowerOp.Esqrt:
                EmitEfuWrite($"Ps2Float.Sqrt({ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
            case LowerOp.Ersqrt:
                EmitEfuWrite($"Vu1State.VuRsqrtFast(1.0f, {ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
            case LowerOp.Esin:
                EmitEfuWrite($"MathF.Sin({ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
            case LowerOp.Eatan:
                EmitEfuWrite($"MathF.Atan({ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
            case LowerOp.Eexp:
                EmitEfuWrite($"MathF.Exp(-{ReadVfExpr(s.Is)}.GetElement({s.Fsf}))", sb, pad);
                break;
        }
    }

    private static void EmitEfuWrite(string expr, StringBuilder sb, string pad)
    {
        if (_efuRemainCycles > 0)
        {
            sb.AppendLine($"{pad}vu.P = vu.PendingP;");
            sb.AppendLine($"{pad}vu.PendingP = {expr};");
        }
        else
        {
            sb.AppendLine($"{pad}vu.P = vu.PendingP = {expr};");
        }
        _efuRemainCycles = 12;
    }

    private static string LoadInt16DirectExpr(string addrExpr, uint destMask)
    {
        int lane = (destMask & 8u) != 0 ? 0 : (destMask & 4u) != 0 ? 1 : (destMask & 2u) != 0 ? 2 : 3;
        int laneByte = lane << 2;
        return laneByte == 0
            ? $"Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref dm, ({addrExpr}) << 4))"
            : $"Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref dm, (({addrExpr}) << 4) + {laneByte}))";
    }

    private static void EmitStoreInt16Direct(string addrExpr, string valExpr, uint destMask, StringBuilder sb, string pad)
    {
        if (destMask == 0u) return;
        int bits = System.Numerics.BitOperations.PopCount(destMask & 0xFu);
        if (bits == 1)
        {
            int lane = (destMask & 8u) != 0 ? 0 : (destMask & 4u) != 0 ? 1 : (destMask & 2u) != 0 ? 2 : 3;
            int laneByte = lane << 2;
            string off = laneByte == 0 ? $"({addrExpr}) << 4" : $"(({addrExpr}) << 4) + {laneByte}";
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, {off}), (uint){valExpr});");
            return;
        }
        sb.AppendLine($"{pad}vu.StoreInt16({addrExpr}, {valExpr}, 0x{destMask:X}u);");
    }

    private static string ReadVfExpr(int reg)
    {
        int r = reg & 31;
        if (r == 0) return "Vf0";
        return _useLocalRegs ? $"vf{r}" : $"Unsafe.Add(ref vf, {r})";
    }

    private static string ReadViExpr(int reg)
    {
        int r = reg & 15;
        if (r == 0) return "((ushort)0)";
        return _useLocalRegs ? $"vi{r}" : $"Unsafe.Add(ref vi, {r})";
    }

    private static string AddrExpr(int reg, int imm11)
    {
        int r = reg & 15;
        if (r == 0) return $"0x{(imm11 & 0x3FF):X}";
        if (imm11 == 0) return $"({ReadViExpr(r)} & 0x3FF)";
        return $"(((short){ReadViExpr(r)} + ({imm11})) & 0x3FF)";
    }

    private static string ReadVfElemExpr(int reg, int elem)
    {
        int r = reg & 31;
        if (r == 0) return elem == 3 ? "1.0f" : "0.0f";
        return $"{ReadVfExpr(r)}.GetElement({elem})";
    }

    private static bool LowerWritesVf(in DecodedSlot s, int reg)
    {
        return s.LowerOp is LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd or LowerOp.Move or LowerOp.Mr32
            or LowerOp.Mfir or LowerOp.Rnext or LowerOp.Rget or LowerOp.Mfp
            && (s.It & 31) == reg;
    }

    private static string LoadQwExpr(string addrExpr)
        => $"Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, ({addrExpr}) << 4))";

    private static string LoadSanitizedQwExpr(string addrExpr, uint destMask)
    {
        string raw = LoadQwExpr(addrExpr);
        uint m = (uint)_curLqSanitizeMask & (destMask & 0xFu);
        if (m == 0u) return raw;
        return m == 0xFu ? $"SanitizeFloat({raw})" : $"SanitizeFloatMask({raw}, 0x{m:X})";
    }

    private static void EmitStoreQw(string addrExpr, string valExpr, uint destMask, string tag, StringBuilder sb, string pad)
    {
        uint m = destMask & 0xFu;
        if (m == 0u) return;
        if (m == 0xFu)
        {
            sb.AppendLine($"{pad}Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, ({addrExpr}) << 4)) = {valExpr};");
        }
        else if (m == 0xEu)
        {
            string dstVar = $"sq_{tag}";
            sb.AppendLine($"{pad}{{ ref byte {dstVar} = ref Unsafe.Add(ref dm, ({addrExpr}) << 4); Unsafe.WriteUnaligned(ref {dstVar}, {valExpr}.AsUInt64().GetElement(0)); Unsafe.WriteUnaligned(ref Unsafe.Add(ref {dstVar}, 8), {valExpr}.GetElement(2)); }}");
        }
        else if (m == 0xCu)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, ({addrExpr}) << 4), {valExpr}.AsUInt64().GetElement(0));");
        }
        else if (m == 0x3u)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, (({addrExpr}) << 4) + 8), {valExpr}.AsUInt64().GetElement(1));");
        }
        else if (m == 0x8u)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, ({addrExpr}) << 4), {valExpr}.GetElement(0));");
        }
        else if (m == 0x4u)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, (({addrExpr}) << 4) + 4), {valExpr}.GetElement(1));");
        }
        else if (m == 0x2u)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, (({addrExpr}) << 4) + 8), {valExpr}.GetElement(2));");
        }
        else if (m == 0x1u)
        {
            sb.AppendLine($"{pad}Unsafe.WriteUnaligned(ref Unsafe.Add(ref dm, (({addrExpr}) << 4) + 12), {valExpr}.GetElement(3));");
        }
        else
        {
            string dstVar = $"st_{tag}";
            sb.AppendLine($"{pad}{{ ref Vector128<float> {dstVar} = ref Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref dm, ({addrExpr}) << 4)); {dstVar} = Ps2Vu0.BlendMask({dstVar}, {valExpr}, 0x{m:X}u); }}");
        }
    }

    private static void EmitWriteVf(int reg, string valExpr, uint destMask, StringBuilder sb, string pad, byte liveAfter = 0xF)
    {
        int r = reg & 31;
        if (r == 0 || destMask == 0u) return;
        string target = _useLocalRegs ? $"vf{r}" : $"Unsafe.Add(ref vf, {r})";
        if (valExpr == target) return;
        if (destMask == 0xFu || (liveAfter & ~destMask) == 0)
            sb.AppendLine($"{pad}{target} = {valExpr};");
        else
            sb.AppendLine($"{pad}{target} = Ps2Vu0.BlendMask({target}, {valExpr}, 0x{destMask:X}u);");
    }

    private static void EmitWriteVfOrCall(int reg, string callExpr, uint destMask, StringBuilder sb, string pad)
    {
        int r = reg & 31;
        if (r == 0 || destMask == 0u)
            sb.AppendLine($"{pad}{callExpr};");
        else
            EmitWriteVf(r, callExpr, destMask, sb, pad);
    }

    private static void EmitWriteVi(int reg, string valExpr, StringBuilder sb, string pad)
    {
        int r = reg & 15;
        if (r == 0) return;
        string target = _useLocalRegs ? $"vi{r}" : $"Unsafe.Add(ref vi, {r})";
        sb.AppendLine($"{pad}{target} = {valExpr};");
    }

    private static bool IsAccOp(UpperOp op) => op is
        UpperOp.AddABc or UpperOp.SubABc or UpperOp.MaddABc or UpperOp.MsubABc or UpperOp.MulABc or
        UpperOp.MulAq or UpperOp.MulAi or UpperOp.AddAq or UpperOp.MaddAq or UpperOp.AddAi or UpperOp.MaddAi or
        UpperOp.SubAq or UpperOp.MsubAq or UpperOp.SubAi or UpperOp.MsubAi or UpperOp.AddA or UpperOp.MaddA or
        UpperOp.MulA or UpperOp.SubA or UpperOp.MsubA or UpperOp.Opmula;

    private static bool UsesFtDestUpper(UpperOp op) => op is
        UpperOp.Itof0 or UpperOp.Itof4 or UpperOp.Itof12 or UpperOp.Itof15 or
        UpperOp.Ftoi0 or UpperOp.Ftoi4 or UpperOp.Ftoi12 or UpperOp.Ftoi15 or UpperOp.Abs;

    private static bool UpdatesMacFlagsUpper(UpperOp op) => op is not (
        UpperOp.Nop or
        UpperOp.Itof0 or UpperOp.Itof4 or UpperOp.Itof12 or UpperOp.Itof15 or
        UpperOp.Ftoi0 or UpperOp.Ftoi4 or UpperOp.Ftoi12 or UpperOp.Ftoi15 or
        UpperOp.Abs or UpperOp.Clip or
        UpperOp.Max or UpperOp.MaxBc or UpperOp.MaxI or
        UpperOp.Min or UpperOp.MinBc or UpperOp.MinI);

    private static bool SlotReadsFlags(in DecodedSlot s)
        => !s.IsImm && s.LowerOp is LowerOp.Fsand or LowerOp.Fsor or LowerOp.Fmeq or LowerOp.Fmand or LowerOp.Fmor;

    private static bool LowerTouchesVf(in DecodedSlot s, int reg)
    {
        return s.LowerOp switch
        {
            LowerOp.Lq or LowerOp.Lqi or LowerOp.Lqd or LowerOp.Mfir or LowerOp.Rnext or LowerOp.Rget or LowerOp.Mfp
                => (s.It & 31) == reg,
            LowerOp.Sq or LowerOp.Sqi or LowerOp.Sqd or LowerOp.Mtir or LowerOp.Rinit or LowerOp.Rxor or
            LowerOp.Esadd or LowerOp.Ersadd or LowerOp.Eleng or LowerOp.Erleng or LowerOp.Eatanxy or LowerOp.Eatanxz or
            LowerOp.Esum or LowerOp.Ercpr or LowerOp.Esqrt or LowerOp.Ersqrt or LowerOp.Esin or LowerOp.Eatan or LowerOp.Eexp
                => (s.Is & 31) == reg,
            LowerOp.Move or LowerOp.Mr32 or LowerOp.Div or LowerOp.Rsqrt
                => (s.Is & 31) == reg || (s.It & 31) == reg,
            LowerOp.Sqrt
                => (s.It & 31) == reg,
            _ => false
        };
    }

    private static bool SlotWritesVi(in DecodedSlot s, int reg)
    {
        if (s.IsImm) return false;
        int r = reg & 15;
        if (r == 0) return false;
        return s.LowerOp switch
        {
            LowerOp.Ilw or LowerOp.Iaddiu or LowerOp.Isubiu or LowerOp.Fsand or LowerOp.Fsor or LowerOp.Fmeq or
            LowerOp.Fmand or LowerOp.Fmor or LowerOp.Fcget or LowerOp.Iaddi or LowerOp.Lqd or LowerOp.Lqi
                => (s.It & 15) == r || ((s.LowerOp is LowerOp.Lqi or LowerOp.Lqd) && (s.Is & 15) == r),
            LowerOp.Sqi or LowerOp.Sqd or LowerOp.Mtir or LowerOp.Ilwr or LowerOp.Xtop or LowerOp.Xitop
                => (s.It & 15) == r,
            LowerOp.Iadd or LowerOp.Isub or LowerOp.Iand or LowerOp.Ior
                => (s.Id & 15) == r,
            LowerOp.Fceq or LowerOp.Fcand or LowerOp.Fcor
                => r == 1,
            _ => false
        };
    }

    private static DecodedSlot DecodeSlot(byte[] microMem, int i)
    {
        uint lo = BinaryPrimitives.ReadUInt32LittleEndian(microMem.AsSpan(i * 8, 4));
        uint up = BinaryPrimitives.ReadUInt32LittleEndian(microMem.AsSpan(i * 8 + 4, 4));

        DecodedSlot s = default;
        s.Valid = true;
        s.IsImm = (up & 0x80000000u) != 0;
        s.IsE = (up & 0x40000000u) != 0;
        if (s.IsImm)
            s.LoiBits = lo;

        if ((up & 0x07FFFFFFu) != 0x000002FFu)
        {
            s.Dest = (byte)((up >> 21) & 0xFu);
            s.Ft = (byte)((up >> 16) & 0x1Fu);
            s.Fs = (byte)((up >> 11) & 0x1Fu);
            s.Fd = (byte)((up >> 6) & 0x1Fu);
            uint op = up & 0x3Fu;
            if (op >= 0x3C)
            {
                uint subOp = ((up >> 4) & 0x7Cu) | (up & 0x3u);
                s.Bc = (byte)(up & 3u);
                s.UpperOp = subOp switch
                {
                    0x00 or 0x01 or 0x02 or 0x03 => UpperOp.AddABc,
                    0x04 or 0x05 or 0x06 or 0x07 => UpperOp.SubABc,
                    0x08 or 0x09 or 0x0A or 0x0B => UpperOp.MaddABc,
                    0x0C or 0x0D or 0x0E or 0x0F => UpperOp.MsubABc,
                    0x10 => UpperOp.Itof0,
                    0x11 => UpperOp.Itof4,
                    0x12 => UpperOp.Itof12,
                    0x13 => UpperOp.Itof15,
                    0x14 => UpperOp.Ftoi0,
                    0x15 => UpperOp.Ftoi4,
                    0x16 => UpperOp.Ftoi12,
                    0x17 => UpperOp.Ftoi15,
                    0x18 or 0x19 or 0x1A or 0x1B => UpperOp.MulABc,
                    0x1C => UpperOp.MulAq,
                    0x1D => UpperOp.Abs,
                    0x1E => UpperOp.MulAi,
                    0x1F => UpperOp.Clip,
                    0x20 => UpperOp.AddAq,
                    0x21 => UpperOp.MaddAq,
                    0x22 => UpperOp.AddAi,
                    0x23 => UpperOp.MaddAi,
                    0x24 => UpperOp.SubAq,
                    0x25 => UpperOp.MsubAq,
                    0x26 => UpperOp.SubAi,
                    0x27 => UpperOp.MsubAi,
                    0x28 => UpperOp.AddA,
                    0x29 => UpperOp.MaddA,
                    0x2A => UpperOp.MulA,
                    0x2C => UpperOp.SubA,
                    0x2D => UpperOp.MsubA,
                    0x2E => UpperOp.Opmula,
                    _ => UpperOp.Unknown
                };
            }
            else
            {
                s.Bc = (byte)(op & 3u);
                s.UpperOp = op switch
                {
                    0x00 or 0x01 or 0x02 or 0x03 => UpperOp.AddBc,
                    0x04 or 0x05 or 0x06 or 0x07 => UpperOp.SubBc,
                    0x08 or 0x09 or 0x0A or 0x0B => UpperOp.MaddBc,
                    0x0C or 0x0D or 0x0E or 0x0F => UpperOp.MsubBc,
                    0x10 or 0x11 or 0x12 or 0x13 => UpperOp.MaxBc,
                    0x14 or 0x15 or 0x16 or 0x17 => UpperOp.MinBc,
                    0x18 or 0x19 or 0x1A or 0x1B => UpperOp.MulBc,
                    0x1C => UpperOp.MulQ,
                    0x1D => UpperOp.MaxI,
                    0x1E => UpperOp.MulI,
                    0x1F => UpperOp.MinI,
                    0x20 => UpperOp.AddQ,
                    0x21 => UpperOp.MaddQ,
                    0x22 => UpperOp.AddI,
                    0x23 => UpperOp.MaddI,
                    0x24 => UpperOp.SubQ,
                    0x25 => UpperOp.MsubQ,
                    0x26 => UpperOp.SubI,
                    0x27 => UpperOp.MsubI,
                    0x28 => UpperOp.Add,
                    0x29 => UpperOp.Madd,
                    0x2A => UpperOp.Mul,
                    0x2B => UpperOp.Max,
                    0x2C => UpperOp.Sub,
                    0x2D => UpperOp.Msub,
                    0x2E => UpperOp.Opmsub,
                    0x2F => UpperOp.Min,
                    _ => UpperOp.Unknown
                };
            }
            if (s.UpperOp == UpperOp.Unknown)
                s.Valid = false;
        }

        if (!s.IsImm && lo != 0x8000033Cu && lo != 0u)
        {
            uint top = (lo >> 25) & 0x7Fu;
            s.LowerDest = (byte)((lo >> 21) & 0xFu);
            s.It = (byte)((lo >> 16) & 0x1Fu);
            s.Is = (byte)((lo >> 11) & 0x1Fu);
            s.Id = (byte)((lo >> 6) & 0x1Fu);
            int imm11 = (int)(lo & 0x7FFu);
            if ((imm11 & 0x400) != 0) imm11 |= ~0x7FF;
            s.Imm11 = (short)imm11;
            s.Uimm15 = (ushort)((((lo >> 21) & 0xFu) << 11) | (lo & 0x7FFu));
            s.Imm24 = lo & 0xFFFFFFu;

            if (top >= 0x20 && top <= 0x2F)
            {
                s.BranchTargetSlot = (ushort)((i + 1 + imm11) & 0x7FF);
                s.BranchOp = top switch
                {
                    0x20 => BranchOp.B,
                    0x21 => BranchOp.Bal,
                    0x24 => BranchOp.Jr,
                    0x25 => BranchOp.Jalr,
                    0x28 => BranchOp.Ibeq,
                    0x29 => BranchOp.Ibne,
                    0x2C => BranchOp.Ibltz,
                    0x2D => BranchOp.Ibgtz,
                    0x2E => BranchOp.Iblez,
                    0x2F => BranchOp.Ibgez,
                    _ => BranchOp.None
                };
            }
            else if (top == 0x40)
            {
                uint op = lo & 0x3Fu;
                if (op < 0x3C)
                {
                    int imm5 = s.Id;
                    if ((imm5 & 0x10) != 0) imm5 |= ~0x1F;
                    s.Imm11 = (short)imm5;
                    s.LowerOp = op switch
                    {
                        0x30 => LowerOp.Iadd,
                        0x31 => LowerOp.Isub,
                        0x32 => LowerOp.Iaddi,
                        0x34 => LowerOp.Iand,
                        0x35 => LowerOp.Ior,
                        _ => LowerOp.Nop
                    };
                }
                else
                {
                    uint subOp = ((lo >> 4) & 0x7Cu) | (lo & 0x3u);
                    s.Fsf = (byte)((lo >> 21) & 0x3u);
                    s.Ftf = (byte)((lo >> 23) & 0x3u);
                    s.LowerOp = subOp switch
                    {
                        0x30 => LowerOp.Move,
                        0x31 => LowerOp.Mr32,
                        0x34 => LowerOp.Lqi,
                        0x35 => LowerOp.Sqi,
                        0x36 => LowerOp.Lqd,
                        0x37 => LowerOp.Sqd,
                        0x38 => LowerOp.Div,
                        0x39 => LowerOp.Sqrt,
                        0x3A => LowerOp.Rsqrt,
                        0x3B => LowerOp.WaitQ,
                        0x3C => LowerOp.Mtir,
                        0x3D => LowerOp.Mfir,
                        0x3E => LowerOp.Ilwr,
                        0x3F => LowerOp.Iswr,
                        0x40 => LowerOp.Rnext,
                        0x41 => LowerOp.Rget,
                        0x42 => LowerOp.Rinit,
                        0x43 => LowerOp.Rxor,
                        0x64 => LowerOp.Mfp,
                        0x68 => LowerOp.Xtop,
                        0x69 => LowerOp.Xitop,
                        0x6C => LowerOp.Xgkick,
                        0x70 => LowerOp.Esadd,
                        0x71 => LowerOp.Ersadd,
                        0x72 => LowerOp.Eleng,
                        0x73 => LowerOp.Erleng,
                        0x74 => LowerOp.Eatanxy,
                        0x75 => LowerOp.Eatanxz,
                        0x76 => LowerOp.Esum,
                        0x77 => LowerOp.Ercpr,
                        0x78 => LowerOp.Esqrt,
                        0x79 => LowerOp.Ersqrt,
                        0x7B => LowerOp.WaitP,
                        0x7C => LowerOp.Esin,
                        0x7D => LowerOp.Eatan,
                        0x7E => LowerOp.Eexp,
                        _ => LowerOp.Nop
                    };
                }
            }
            else
            {
                s.LowerOp = top switch
                {
                    0x00 => LowerOp.Lq,
                    0x01 => LowerOp.Sq,
                    0x04 => LowerOp.Ilw,
                    0x05 => LowerOp.Isw,
                    0x08 => LowerOp.Iaddiu,
                    0x09 => LowerOp.Isubiu,
                    0x10 => LowerOp.Fceq,
                    0x11 => LowerOp.Fcset,
                    0x12 => LowerOp.Fcand,
                    0x13 => LowerOp.Fcor,
                    0x14 => LowerOp.Fsset,
                    0x16 => LowerOp.Fsand,
                    0x17 => LowerOp.Fsor,
                    0x18 => LowerOp.Fmeq,
                    0x1A => LowerOp.Fmand,
                    0x1B => LowerOp.Fmor,
                    0x1C => LowerOp.Fcget,
                    _ => LowerOp.Nop
                };
            }
        }

        return s;
    }
}
