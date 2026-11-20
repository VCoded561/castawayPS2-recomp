using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Castaway.Core;

/// <summary>
/// Hardware-accurate PS2 memory access &amp; unaligned byte-merge helpers.
/// - Masks physical user-space virtual addresses with &amp; 0x01FFFFFFu (32 MB EE RAM).
/// - Fast-paths Scratchpad RAM (0x70000000..0x70003FFF) via ctx-&gt;Scratchpad.
/// - Routes hardware MMIO (0x10000000..0x1200FFFF) to RuntimeDispatcher.
/// - Implements exact MIPS R5900 unaligned load/store byte merges (LWL/LWR/SWL/SWR/LDL/LDR/SDL/SDR).
/// </summary>
public static unsafe class Ps2Memory
{
    public const uint RamMask = 0x01FFFFFFu;
    public const uint ScratchpadBase = 0x70000000u;
    public const uint ScratchpadPageMask = 0xFFFFC000u;
    public const uint ScratchpadOffsetMask = 0x00003FFFu;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsScratchpad(uint vaddr) => (vaddr & ScratchpadPageMask) == ScratchpadBase;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte* GetVuPointer(CpuContext* ctx, uint vaddr)
    {
        uint norm = vaddr & 0x1FFFFFFFu;
        if (norm < 0x11000000u || norm >= 0x11010000u)
            return null;

        uint off = norm - 0x11000000u;
        if (off < 0x4000u)
        {
            return RuntimeDispatcher.Vu0Micro != null ? RuntimeDispatcher.Vu0Micro + (off & 0x0FFFu) : null;
        }
        if (off < 0x8000u)
        {
            byte* vu0Data = ctx != null && ctx->Vu0DataMem != null ? ctx->Vu0DataMem : RuntimeDispatcher.Vu0Data;
            return vu0Data != null ? vu0Data + (off & 0x0FFFu) : null;
        }
        if (off < 0xC000u)
        {
            return RuntimeDispatcher.Vu1Micro != null ? RuntimeDispatcher.Vu1Micro + (off & 0x3FFFu) : null;
        }
        return RuntimeDispatcher.Vu1Data != null ? RuntimeDispatcher.Vu1Data + (off & 0x3FFFu) : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsMmio(uint vaddr)
    {
        uint topNibble = vaddr >> 28;
        if (topNibble is not (0x1u or 0x9u or 0xBu))
            return false;
        uint norm = vaddr & 0x1FFFFFFFu;
        return norm >= 0x10000000u && norm < 0x12010000u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Read8(CpuContext* ctx, byte* ram, uint vaddr)
        => vaddr < 0x10000000u ? ram[vaddr & RamMask] : Read8Slow(ctx, ram, vaddr);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte Read8Slow(CpuContext* ctx, byte* ram, uint vaddr)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
            return ctx->Scratchpad[vaddr & ScratchpadOffsetMask];
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
            return *vuPtr;
        if (IsMmio(vaddr))
            return RuntimeDispatcher.MmioRead8(ctx, ram, vaddr);
        return ram[vaddr & RamMask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Read16(CpuContext* ctx, byte* ram, uint vaddr)
        => vaddr < 0x10000000u ? Unsafe.ReadUnaligned<ushort>(ram + (vaddr & RamMask)) : Read16Slow(ctx, ram, vaddr);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ushort Read16Slow(CpuContext* ctx, byte* ram, uint vaddr)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
            return Unsafe.ReadUnaligned<ushort>(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask));
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
            return Unsafe.ReadUnaligned<ushort>(vuPtr);
        if (IsMmio(vaddr))
            return RuntimeDispatcher.MmioRead16(ctx, ram, vaddr);
        return Unsafe.ReadUnaligned<ushort>(ram + (vaddr & RamMask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(CpuContext* ctx, byte* ram, uint vaddr)
        => vaddr < 0x10000000u ? Unsafe.ReadUnaligned<uint>(ram + (vaddr & RamMask)) : Read32Slow(ctx, ram, vaddr);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint Read32Slow(CpuContext* ctx, byte* ram, uint vaddr)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
            return Unsafe.ReadUnaligned<uint>(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask));
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
            return Unsafe.ReadUnaligned<uint>(vuPtr);
        if (IsMmio(vaddr))
            return RuntimeDispatcher.MmioRead32(ctx, ram, vaddr);
        return Unsafe.ReadUnaligned<uint>(ram + (vaddr & RamMask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ReadF32(CpuContext* ctx, byte* ram, uint vaddr)
        => vaddr < 0x10000000u ? Unsafe.ReadUnaligned<float>(ram + (vaddr & RamMask)) : BitConverter.UInt32BitsToSingle(Read32Slow(ctx, ram, vaddr));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(CpuContext* ctx, byte* ram, uint vaddr)
        => vaddr < 0x10000000u ? Unsafe.ReadUnaligned<ulong>(ram + (vaddr & RamMask)) : Read64Slow(ctx, ram, vaddr);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Read64Slow(CpuContext* ctx, byte* ram, uint vaddr)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
            return Unsafe.ReadUnaligned<ulong>(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask));
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
            return Unsafe.ReadUnaligned<ulong>(vuPtr);
        if (IsMmio(vaddr))
            return RuntimeDispatcher.MmioRead64(ctx, ram, vaddr);
        return Unsafe.ReadUnaligned<ulong>(ram + (vaddr & RamMask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Read128(CpuContext* ctx, byte* ram, uint vaddr)
    {
        uint aligned = vaddr & ~0xFu;
        return aligned < 0x10000000u
            ? Unsafe.ReadUnaligned<Vector128<uint>>(ram + (aligned & RamMask))
            : Read128Slow(ctx, ram, aligned);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<uint> Read128Slow(CpuContext* ctx, byte* ram, uint aligned)
    {
        if (IsScratchpad(aligned) && ctx->Scratchpad != null)
            return Unsafe.ReadUnaligned<Vector128<uint>>(ctx->Scratchpad + (aligned & ScratchpadOffsetMask));
        byte* vuPtr = GetVuPointer(ctx, aligned);
        if (vuPtr != null)
            return Unsafe.ReadUnaligned<Vector128<uint>>(vuPtr);
        if (IsMmio(aligned))
            return RuntimeDispatcher.MmioRead128(ctx, ram, aligned);
        return Unsafe.ReadUnaligned<Vector128<uint>>(ram + (aligned & RamMask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write8(CpuContext* ctx, byte* ram, uint vaddr, byte value)
    {
        if (vaddr < 0x10000000u)
        {
            ram[vaddr & RamMask] = value;
            return;
        }
        Write8Slow(ctx, ram, vaddr, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write8Slow(CpuContext* ctx, byte* ram, uint vaddr, byte value)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
        {
            ctx->Scratchpad[vaddr & ScratchpadOffsetMask] = value;
            return;
        }
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
        {
            *vuPtr = value;
            return;
        }
        if (IsMmio(vaddr))
        {
            RuntimeDispatcher.MmioWrite8(ctx, ram, vaddr, value);
            return;
        }
        ram[vaddr & RamMask] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write16(CpuContext* ctx, byte* ram, uint vaddr, ushort value)
    {
        if (vaddr < 0x10000000u)
        {
            Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
            return;
        }
        Write16Slow(ctx, ram, vaddr, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write16Slow(CpuContext* ctx, byte* ram, uint vaddr, ushort value)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
        {
            Unsafe.WriteUnaligned(vuPtr, value);
            return;
        }
        if (IsMmio(vaddr))
        {
            RuntimeDispatcher.MmioWrite16(ctx, ram, vaddr, value);
            return;
        }
        Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write32(CpuContext* ctx, byte* ram, uint vaddr, uint value)
    {
        if (vaddr < 0x10000000u)
        {
            Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
            return;
        }
        if ((vaddr & ScratchpadPageMask) == ScratchpadBase && ctx != null && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        Write32Slow(ctx, ram, vaddr, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write32Slow(CpuContext* ctx, byte* ram, uint vaddr, uint value)
    {
        if (IsScratchpad(vaddr) && ctx != null && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
        {
            Unsafe.WriteUnaligned(vuPtr, value);
            return;
        }
        if (IsMmio(vaddr))
        {
            RuntimeDispatcher.MmioWrite32(ctx, ram, vaddr, value);
            return;
        }
        Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteF32(CpuContext* ctx, byte* ram, uint vaddr, float value)
    {
        if (vaddr < 0x10000000u)
        {
            Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
            return;
        }
        if ((vaddr & ScratchpadPageMask) == ScratchpadBase && ctx != null && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        Write32Slow(ctx, ram, vaddr, BitConverter.SingleToUInt32Bits(value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write64(CpuContext* ctx, byte* ram, uint vaddr, ulong value)
    {
        if (vaddr < 0x10000000u)
        {
            Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
            return;
        }
        if ((vaddr & ScratchpadPageMask) == ScratchpadBase && ctx != null && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        Write64Slow(ctx, ram, vaddr, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write64Slow(CpuContext* ctx, byte* ram, uint vaddr, ulong value)
    {
        if (IsScratchpad(vaddr) && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (vaddr & ScratchpadOffsetMask), value);
            return;
        }
        byte* vuPtr = GetVuPointer(ctx, vaddr);
        if (vuPtr != null)
        {
            Unsafe.WriteUnaligned(vuPtr, value);
            return;
        }
        if (IsMmio(vaddr))
        {
            RuntimeDispatcher.MmioWrite64(ctx, ram, vaddr, value);
            return;
        }
        Unsafe.WriteUnaligned(ram + (vaddr & RamMask), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write128(CpuContext* ctx, byte* ram, uint vaddr, Vector128<uint> value)
    {
        uint aligned = vaddr & ~0xFu;
        if (aligned < 0x10000000u)
        {
            Unsafe.WriteUnaligned(ram + (aligned & RamMask), value);
            return;
        }
        if ((aligned & ScratchpadPageMask) == ScratchpadBase && ctx != null && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (aligned & ScratchpadOffsetMask), value);
            return;
        }
        Write128Slow(ctx, ram, aligned, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write128Slow(CpuContext* ctx, byte* ram, uint aligned, Vector128<uint> value)
    {
        if (IsScratchpad(aligned) && ctx->Scratchpad != null)
        {
            Unsafe.WriteUnaligned(ctx->Scratchpad + (aligned & ScratchpadOffsetMask), value);
            return;
        }
        byte* vuPtr = GetVuPointer(ctx, aligned);
        if (vuPtr != null)
        {
            Unsafe.WriteUnaligned(vuPtr, value);
            return;
        }
        if (IsMmio(aligned))
        {
            RuntimeDispatcher.MmioWrite128(ctx, ram, aligned, value);
            return;
        }
        Unsafe.WriteUnaligned(ram + (aligned & RamMask), value);
    }

    // =========================================================================
    // Unaligned Load/Store Byte-Merge Helpers (Little-Endian MIPS R5900)
    // =========================================================================

    /// <summary>
    /// LWL: Load Word Left (merges addressed byte and lower bytes into the upper bytes of rt, sign-extending to 64-bit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadWordLeft(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~3u;
        int offset = (int)(addr & 3u);
        uint mem = Read32(ctx, ram, aligned);
        if (rt == 0) return;

        int shift = (3 - offset) << 3;
        uint keepMask = shift == 0 ? 0u : ((1u << shift) - 1u);
        uint merged = (ctx->GPR[rt].U32_0 & keepMask) | (mem << shift);
        ctx->GPR[rt].S64_0 = (int)merged;
    }

    /// <summary>
    /// LWR: Load Word Right (merges addressed byte and upper bytes into the lower bytes of rt).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadWordRight(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~3u;
        int offset = (int)(addr & 3u);
        uint mem = Read32(ctx, ram, aligned);
        if (rt == 0) return;

        int shift = offset << 3;
        uint keepMask = offset == 0 ? 0u : (0xFFFFFFFFu << ((4 - offset) << 3));
        uint merged32 = (ctx->GPR[rt].U32_0 & keepMask) | (mem >> shift);
        if (offset == 0)
        {
            ctx->GPR[rt].S64_0 = (int)merged32;
        }
        else
        {
            ctx->GPR[rt].U64_0 = (ctx->GPR[rt].U64_0 & 0xFFFFFFFF00000000UL) | merged32;
        }
    }

    /// <summary>
    /// SWL: Store Word Left (stores upper bytes of rt into aligned memory word).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreWordLeft(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~3u;
        int offset = (int)(addr & 3u);
        int shift = (3 - offset) << 3;
        uint mask = 0xFFFFFFFFu >> shift;
        uint oldMem = Read32(ctx, ram, aligned);
        uint val = ctx->GetU32(rt);
        uint newMem = (oldMem & ~mask) | ((val >> shift) & mask);
        Write32(ctx, ram, aligned, newMem);
    }

    /// <summary>
    /// SWR: Store Word Right (stores lower bytes of rt into aligned memory word).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreWordRight(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~3u;
        int offset = (int)(addr & 3u);
        int shift = offset << 3;
        uint mask = 0xFFFFFFFFu << shift;
        uint oldMem = Read32(ctx, ram, aligned);
        uint val = ctx->GetU32(rt);
        uint newMem = (oldMem & ~mask) | ((val << shift) & mask);
        Write32(ctx, ram, aligned, newMem);
    }

    /// <summary>
    /// LDL: Load Doubleword Left (merges into upper bytes of 64-bit rt).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadDoubleLeft(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~7u;
        int offset = (int)(addr & 7u);
        ulong mem = Read64(ctx, ram, aligned);
        if (rt == 0) return;

        int shift = (7 - offset) << 3;
        ulong keepMask = shift == 0 ? 0UL : ((1UL << shift) - 1UL);
        ctx->GPR[rt].U64_0 = (ctx->GPR[rt].U64_0 & keepMask) | (mem << shift);
    }

    /// <summary>
    /// LDR: Load Doubleword Right (merges into lower bytes of 64-bit rt).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LoadDoubleRight(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~7u;
        int offset = (int)(addr & 7u);
        ulong mem = Read64(ctx, ram, aligned);
        if (rt == 0) return;

        int shift = offset << 3;
        ulong keepMask = offset == 0 ? 0UL : (0xFFFFFFFFFFFFFFFFUL << ((8 - offset) << 3));
        ctx->GPR[rt].U64_0 = (ctx->GPR[rt].U64_0 & keepMask) | (mem >> shift);
    }

    /// <summary>
    /// SDL: Store Doubleword Left (stores upper bytes of 64-bit rt into aligned memory doubleword).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreDoubleLeft(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~7u;
        int offset = (int)(addr & 7u);
        int shift = (7 - offset) << 3;
        ulong mask = 0xFFFFFFFFFFFFFFFFUL >> shift;
        ulong oldMem = Read64(ctx, ram, aligned);
        ulong val = ctx->GetU64(rt);
        ulong newMem = (oldMem & ~mask) | ((val >> shift) & mask);
        Write64(ctx, ram, aligned, newMem);
    }

    /// <summary>
    /// SDR: Store Doubleword Right (stores lower bytes of 64-bit rt into aligned memory doubleword).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreDoubleRight(CpuContext* ctx, byte* ram, int rt, uint addr)
    {
        uint aligned = addr & ~7u;
        int offset = (int)(addr & 7u);
        int shift = offset << 3;
        ulong mask = 0xFFFFFFFFFFFFFFFFUL << shift;
        ulong oldMem = Read64(ctx, ram, aligned);
        ulong val = ctx->GetU64(rt);
        ulong newMem = (oldMem & ~mask) | ((val << shift) & mask);
        Write64(ctx, ram, aligned, newMem);
    }
}
