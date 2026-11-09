using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Castaway.Core;

/// <summary>
/// 128-bit Emotion Engine General Purpose Register (GPR).
/// Provides zero-copy views as Vector128, 64-bit doublewords, and 32-bit words.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Gpr128
{
    [FieldOffset(0)] public Vector128<uint> Vec;
    [FieldOffset(0)] public Vector128<int> VecS32;
    [FieldOffset(0)] public Vector128<float> VecF32;

    [FieldOffset(0)] public ulong U64_0;
    [FieldOffset(8)] public ulong U64_1;

    [FieldOffset(0)] public long S64_0;
    [FieldOffset(8)] public long S64_1;

    [FieldOffset(0)] public uint U32_0;
    [FieldOffset(4)] public uint U32_1;
    [FieldOffset(8)] public uint U32_2;
    [FieldOffset(12)] public uint U32_3;

    [FieldOffset(0)] public int S32_0;
    [FieldOffset(4)] public int S32_1;
    [FieldOffset(8)] public int S32_2;
    [FieldOffset(12)] public int S32_3;
}

[InlineArray(32)]
public struct GprFile
{
    private Gpr128 _element0;
}

[InlineArray(32)]
public struct FpuRegFile
{
    private float _element0;
}

[InlineArray(32)]
public struct Cop0RegFile
{
    private uint _element0;
}

[InlineArray(32)]
public struct Vu0VecFile
{
    private Vector128<float> _element0;
}

[InlineArray(16)]
public struct Vu0IntFile
{
    private ushort _element0;
}

/// <summary>
/// Complete Emotion Engine (R5900 + COP0 + COP1 FPU + COP2 VU0 Macro) CPU context.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CpuContext
{
    public const int RamSize = 32 * 1024 * 1024;
    public const uint RamMask = 0x01FFFFFFu;
    public const int ScratchpadSize = 16 * 1024;
    public const uint ScratchpadMask = 0x00003FFFu;

    // 32 x 128-bit GPRs (512 bytes). GPR[0] ($zero) is hardwired to 0.
    public GprFile GPR;

    // Multiply/Divide 64-bit accumulators (Pipeline 0 & MMI Pipeline 1)
    public ulong HI;
    public ulong LO;
    public ulong HI1;
    public ulong LO1;

    // Shift Amount register (for QFSRV / MFSA / MTSA / MTSAB / MTSAH)
    public uint SA;

    // Program Counter & Control state
    public uint PC;
    public uint BranchPC;
    public byte InDelaySlot;
    public byte LLBit;
    public uint LLAddr;

    // COP1 (FPU) state
    public FpuRegFile F;
    public float FAcc;
    public uint Fcr0;
    public uint Fcr31;

    // COP0 (System Control) state
    public Cop0RegFile Cop0;

    // COP2 (VU0 Macro Mode) state
    public Vu0VecFile VF;
    public Vector128<float> Vu0Acc;
    public Vu0IntFile VI;
    public float Vu0Q;
    public float Vu0I;
    public Vector128<float> Vu0R;
    public uint Vu0Status;
    public uint Vu0MacFlags;
    public uint Vu0ClipFlags;
    public uint Vu0Tpc;
    public uint Vu0Cmsar0;
    public uint Vu0Fbrst;
    public uint Vu0VpuStat;
    public uint Vu0Cmsar1;

    // Fast-path pointers for 16 KiB Scratchpad (0x70000000) and 4 KiB VU0 Data Memory
    public byte* Scratchpad;
    public byte* Vu0DataMem;

    public void Initialize(byte* scratchpadPtr = null, byte* vu0DataPtr = null)
    {
        this = default;
        Scratchpad = scratchpadPtr;
        Vu0DataMem = vu0DataPtr;
        // VF[0] is hardwired to (0.0f, 0.0f, 0.0f, 1.0f) on VU0
        VF[0] = Vector128.Create(0.0f, 0.0f, 0.0f, 1.0f);
        // COP0 PRId = Emotion Engine (0x00002E20)
        Cop0[15] = 0x00002E20u;
        Cop0[1] = 47u; // Random
        // COP0 Status: CU0|CU1|CU2 (0x70000000) | EIE (0x10000) | IM2/IM3 (0x0C00) | IE (0x1)
        Cop0[12] = 0x70010C01u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint GetU32(int reg) => reg == 0 ? 0u : GPR[reg].U32_0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int GetS32(int reg) => reg == 0 ? 0 : GPR[reg].S32_0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetU64(int reg) => reg == 0 ? 0UL : GPR[reg].U64_0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long GetS64(int reg) => reg == 0 ? 0L : GPR[reg].S64_0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector128<uint> GetVec(int reg) => reg == 0 ? Vector128<uint>.Zero : GPR[reg].Vec;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetS32(int reg, int val)
    {
        if (reg != 0)
            GPR[reg].S64_0 = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetU32(int reg, uint val)
    {
        if (reg != 0)
            GPR[reg].S64_0 = (int)val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetZ32(int reg, uint val)
    {
        if (reg != 0)
            GPR[reg].U64_0 = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetU64(int reg, ulong val)
    {
        if (reg != 0)
            GPR[reg].U64_0 = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetS64(int reg, long val)
    {
        if (reg != 0)
            GPR[reg].S64_0 = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVec(int reg, Vector128<uint> val)
    {
        if (reg != 0)
            GPR[reg].Vec = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint GetFpuBits(int fReg) => BitConverter.SingleToUInt32Bits(F[fReg]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFpuBits(int fReg, uint bits) => F[fReg] = BitConverter.UInt32BitsToSingle(bits);

    public bool Cop1Condition
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => (Fcr31 & 0x00800000u) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Fcr31 = value ? (Fcr31 | 0x00800000u) : (Fcr31 & ~0x00800000u);
    }

    public readonly bool Cop0Condition
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector128<float> ReadVf(int reg) => reg == 0 ? Vector128.Create(0.0f, 0.0f, 0.0f, 1.0f) : VF[reg];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort ReadVi(int reg) => reg == 0 ? (ushort)0 : VI[reg];

    public readonly uint ComputeGprCrc32()
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 1; i < 32; i++)
        {
            crc = System.Numerics.BitOperations.Crc32C(crc, GPR[i].U64_0);
            crc = System.Numerics.BitOperations.Crc32C(crc, GPR[i].U64_1);
        }
        crc = System.Numerics.BitOperations.Crc32C(crc, HI);
        crc = System.Numerics.BitOperations.Crc32C(crc, LO);
        return ~crc;
    }

    public readonly uint ComputeCop1Crc32()
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < 32; i++)
        {
            crc = System.Numerics.BitOperations.Crc32C(crc, GetFpuBits(i));
        }
        crc = System.Numerics.BitOperations.Crc32C(crc, BitConverter.SingleToUInt32Bits(FAcc));
        crc = System.Numerics.BitOperations.Crc32C(crc, Fcr31);
        return ~crc;
    }
}
