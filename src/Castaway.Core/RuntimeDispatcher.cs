using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Castaway.Core;

public unsafe delegate void HookDelegate(CpuContext* ctx, byte* ram);
public unsafe delegate void SyscallHandlerDelegate(CpuContext* ctx, byte* ram, uint code);
public unsafe delegate uint MmioRead32Delegate(CpuContext* ctx, byte* ram, uint vaddr);
public unsafe delegate void MmioWrite32Delegate(CpuContext* ctx, byte* ram, uint vaddr, uint value);
public unsafe delegate Vector128<uint> MmioRead128Delegate(CpuContext* ctx, byte* ram, uint vaddr);
public unsafe delegate void MmioWrite128Delegate(CpuContext* ctx, byte* ram, uint vaddr, Vector128<uint> value);
public unsafe delegate void TraceEventDelegate(uint guestPc, uint eventType, CpuContext* ctx);

/// <summary>
/// Central dispatch hub for static recompilation function table lookups,
/// Phase 3 kernel syscalls &amp; MMIO, Phase 5 binary trace-diffing, and Phase 6 address hooks.
/// </summary>
public static unsafe class RuntimeDispatcher
{
    // Direct lookup table for .text (0x00100000..0x005FFFFF -> 5 MB / 4 = 1,310,720 instruction slots)
    private const uint TextBase = 0x00100000u;
    private const uint TextLimit = 0x00600000u;
    private const uint TextSize = TextLimit - TextBase;
    private static readonly nint[] FastTable = new nint[TextSize >> 2];
    // Two-level L1-friendly hook lookup: 640-byte page bitset (1 KiB pages) gates the 160 KiB exact bitset.
    private static readonly ulong[] HookPageBits = new ulong[((TextSize >> 10) + 63) >> 6];
    private static readonly ulong[] HookBitTable = new ulong[((TextSize >> 2) + 63) >> 6];
    private static readonly Dictionary<uint, nint> FallbackTable = new();
    private static readonly Dictionary<uint, HookDelegate> Hooks = new();
    [ThreadStatic] private static uint _activeHookBypassPc;

    public static bool SlowPathActive;
    public static bool TracingActive;
    public static bool SamplingActive;
    // 0=EE, 1=DmaVif1, 2=Vu1, 3=XgKickGif, 4=DmaGif, 5=DmaOther, 6=Syscall, 7=Audio
    public static volatile int ActiveProfilePhase;
    public static SyscallHandlerDelegate? SyscallHandler;
    public static MmioRead32Delegate? CustomMmioRead32;
    public static MmioWrite32Delegate? CustomMmioWrite32;
    public static MmioRead128Delegate? CustomMmioRead128;
    public static MmioWrite128Delegate? CustomMmioWrite128;
    public static TraceEventDelegate? TraceHandler;

    public static uint CurrentDmaPc;
    public static uint CurrentDmaRa;
    public static uint CurrentDmaAddr;

    // Fast memory mapped pointers for VU0 (4K/4K) and VU1 (16K/16K)
    public static byte* Vu0Micro;
    public static byte* Vu0Data;
    public static byte* Vu1Micro;
    public static byte* Vu1Data;

    public static void RegisterFunction(uint vaddr, delegate*<CpuContext*, byte*, void> fnPtr)
    {
        if (vaddr >= TextBase && vaddr < TextLimit && (vaddr & 3u) == 0)
        {
            FastTable[(vaddr - TextBase) >> 2] = (nint)fnPtr;
        }
        else
        {
            FallbackTable[vaddr] = (nint)fnPtr;
        }
    }

    public static void RegisterHook(uint vaddr, HookDelegate hook)
    {
        Hooks[vaddr] = hook;
        uint off = vaddr - TextBase;
        if (off < TextSize && (off & 3u) == 0)
        {
            uint page = off >> 10;
            HookPageBits[page >> 6] |= 1UL << (int)page;
            uint idx = off >> 2;
            HookBitTable[idx >> 6] |= 1UL << (int)idx;
        }
        SlowPathActive = Hooks.Count > 0 || TraceHandler != null;
    }

    public static void ClearHooks()
    {
        Hooks.Clear();
        Array.Clear(HookPageBits);
        Array.Clear(HookBitTable);
        SlowPathActive = TraceHandler != null;
    }

    public static void SetTraceHandler(TraceEventDelegate? handler)
    {
        TraceHandler = handler;
        TracingActive = handler != null;
        SlowPathActive = Hooks.Count > 0 || TraceHandler != null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasHook(uint pc)
    {
        uint off = pc - TextBase;
        if (off < TextSize)
        {
            uint page = off >> 10;
            if ((HookPageBits[page >> 6] & (1UL << (int)page)) == 0)
                return false;
            if ((off & 3u) != 0)
                return false;
            uint idx = off >> 2;
            return (HookBitTable[idx >> 6] & (1UL << (int)idx)) != 0;
        }
        return HasHookFallback(pc);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasHookFallback(uint pc) => Hooks.ContainsKey(pc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static delegate*<CpuContext*, byte*, void> Lookup(uint vaddr)
    {
        uint off = vaddr - TextBase;
        if (off < TextSize && (off & 3u) == 0)
        {
            nint ptr = FastTable[off >> 2];
            if (ptr != 0)
                return (delegate*<CpuContext*, byte*, void>)ptr;
        }
        return LookupFallback(vaddr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static delegate*<CpuContext*, byte*, void> LookupFallback(uint vaddr)
        => FallbackTable.TryGetValue(vaddr, out nint fb)
            ? (delegate*<CpuContext*, byte*, void>)fb
            : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CheckEntry(uint pc, CpuContext* ctx, byte* ram)
    {
        ctx->PC = pc;
        return SlowPathActive && (TracingActive || HasHook(pc)) && CheckEntrySlow(pc, ctx, ram);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CheckEntrySlow(uint pc, CpuContext* ctx, byte* ram)
    {
        TraceHandler?.Invoke(pc, 0u, ctx); // 0 = FuncEnter
        if (_activeHookBypassPc != pc && HasHook(pc) && Hooks.TryGetValue(pc, out var hook))
        {
            uint prev = _activeHookBypassPc;
            _activeHookBypassPc = pc;
            try
            {
                hook(ctx, ram);
            }
            finally
            {
                _activeHookBypassPc = prev;
            }
            TraceHandler?.Invoke(pc, 1u, ctx); // 1 = FuncExit
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnReturn(CpuContext* ctx, uint returnTarget)
    {
        if (TracingActive)
            TraceHandler?.Invoke(ctx->PC, 1u, ctx); // 1 = FuncExit
        ctx->PC = returnTarget;
    }

    private static int _missingDispatchLogs;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Call(uint targetPc, CpuContext* ctx, byte* ram)
    {
        ctx->PC = targetPc;
        if (targetPc == 0u) return;
        var fn = Lookup(targetPc);
        if (fn != null)
        {
            fn(ctx, ram);
            return;
        }
        CallMissing(targetPc, ctx, ram, isJump: false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Jump(uint targetPc, CpuContext* ctx, byte* ram)
    {
        ctx->PC = targetPc;
        if (targetPc == 0u) return;
        var fn = Lookup(targetPc);
        if (fn != null)
        {
            fn(ctx, ram);
            return;
        }
        CallMissing(targetPc, ctx, ram, isJump: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallMissing(uint targetPc, CpuContext* ctx, byte* ram, bool isJump)
    {
        if (_missingDispatchLogs < 50)
        {
            _missingDispatchLogs++;
            string kind = isJump ? "JUMP" : "CALL";
            Console.WriteLine($"[DISPATCHER MISSING {kind} #{_missingDispatchLogs}] targetPc=0x{targetPc:X8} RA=0x{(ctx != null ? ctx->GetU32(31) : 0):X8} a0=0x{(ctx != null ? ctx->GetU32(4) : 0):X8}");
        }
        if (SlowPathActive)
        {
            CheckEntrySlow(targetPc, ctx, ram);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CallIndirect(uint targetPc, CpuContext* ctx, byte* ram) => Call(targetPc, ctx, ram);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void JumpIndirect(uint targetPc, CpuContext* ctx, byte* ram) => Jump(targetPc, ctx, ram);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Syscall(CpuContext* ctx, byte* ram, uint code)
    {
        TraceHandler?.Invoke(ctx->PC, 2u, ctx); // 2 = Syscall
        SyscallHandler?.Invoke(ctx, ram, code);
    }

    public static void Break(CpuContext* ctx, byte* ram, uint code)
    {
    }

    public static void Trap(CpuContext* ctx, byte* ram)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Eret(CpuContext* ctx)
    {
        if ((ctx->Cop0[12] & 0x4u) != 0)
        {
            ctx->PC = ctx->Cop0[30]; // ErrorEPC
            ctx->Cop0[12] &= ~0x4u;
        }
        else
        {
            ctx->PC = ctx->Cop0[14]; // EPC
            ctx->Cop0[12] &= ~0x2u;
        }
        ctx->LLBit = 0;
    }

    public static void Vcallms(CpuContext* ctx, byte* ram, uint byteAddr)
    {
        ctx->Vu0Tpc = byteAddr;
    }

    public static void Vcallmsr(CpuContext* ctx, byte* ram, int viReg)
    {
        uint byteAddr = ((uint)(viReg == 0 ? 0 : ctx->VI[viReg]) & 0x1FFu) << 3;
        ctx->Vu0Tpc = byteAddr;
    }

    // =========================================================================
    // Hardware MMIO Fallbacks (Overridden by Castaway.Runtime in Phase 3)
    // =========================================================================

    public static byte MmioRead8(CpuContext* ctx, byte* ram, uint vaddr)
        => (byte)(MmioRead32(ctx, ram, vaddr & ~3u) >> ((int)(vaddr & 3u) * 8));

    public static ushort MmioRead16(CpuContext* ctx, byte* ram, uint vaddr)
        => (ushort)(MmioRead32(ctx, ram, vaddr & ~3u) >> ((int)(vaddr & 2u) * 8));

    public static uint MmioRead32(CpuContext* ctx, byte* ram, uint vaddr)
        => CustomMmioRead32 != null ? CustomMmioRead32(ctx, ram, vaddr) : 0u;

    public static ulong MmioRead64(CpuContext* ctx, byte* ram, uint vaddr)
    {
        uint lo = MmioRead32(ctx, ram, vaddr);
        uint hi = MmioRead32(ctx, ram, vaddr + 4u);
        return ((ulong)hi << 32) | lo;
    }

    public static Vector128<uint> MmioRead128(CpuContext* ctx, byte* ram, uint vaddr)
    {
        if (CustomMmioRead128 != null)
            return CustomMmioRead128(ctx, ram, vaddr);
        return Vector128.Create(
            MmioRead32(ctx, ram, vaddr),
            MmioRead32(ctx, ram, vaddr + 4u),
            MmioRead32(ctx, ram, vaddr + 8u),
            MmioRead32(ctx, ram, vaddr + 12u));
    }

    public static void MmioWrite8(CpuContext* ctx, byte* ram, uint vaddr, byte value)
        => MmioWrite32(ctx, ram, vaddr, value);

    public static void MmioWrite16(CpuContext* ctx, byte* ram, uint vaddr, ushort value)
        => MmioWrite32(ctx, ram, vaddr, value);

    public static void MmioWrite32(CpuContext* ctx, byte* ram, uint vaddr, uint value)
        => CustomMmioWrite32?.Invoke(ctx, ram, vaddr, value);

    public static void MmioWrite64(CpuContext* ctx, byte* ram, uint vaddr, ulong value)
    {
        MmioWrite32(ctx, ram, vaddr, (uint)value);
        MmioWrite32(ctx, ram, vaddr + 4u, (uint)(value >> 32));
    }

    public static void MmioWrite128(CpuContext* ctx, byte* ram, uint vaddr, Vector128<uint> value)
    {
        if (CustomMmioWrite128 != null)
        {
            CustomMmioWrite128(ctx, ram, vaddr, value);
            return;
        }
        MmioWrite32(ctx, ram, vaddr, value.GetElement(0));
        MmioWrite32(ctx, ram, vaddr + 4u, value.GetElement(1));
        MmioWrite32(ctx, ram, vaddr + 8u, value.GetElement(2));
        MmioWrite32(ctx, ram, vaddr + 12u, value.GetElement(3));
    }
}
