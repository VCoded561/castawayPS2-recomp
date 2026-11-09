using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Castaway.Core;

/// <summary>
/// Sony PS2 Emotion Engine 128-bit Multimedia Instructions (MMI, MMI0, MMI1, MMI2, MMI3).
/// </summary>
public static unsafe class Ps2Mmi
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CombineHiLo(ulong hi, ulong lo) => ((hi & 0xFFFFFFFFUL) << 32) | (lo & 0xFFFFFFFFUL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignExt32(uint v) => (ulong)(long)(int)v;

    // =========================================================================
    // Pipeline 0 & 1 Scalar Multiply-Accumulate (MADD, MADDU, MSUB, MSUBU, MADD1, MADDU1)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Madd(CpuContext* ctx, int rd, int rs, int rt)
    {
        long acc = (long)CombineHiLo(ctx->HI, ctx->LO);
        long prod = (long)ctx->GetS32(rs) * ctx->GetS32(rt);
        long res = acc + prod;
        ctx->LO = SignExt32((uint)res);
        ctx->HI = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Maddu(CpuContext* ctx, int rd, int rs, int rt)
    {
        ulong acc = CombineHiLo(ctx->HI, ctx->LO);
        ulong prod = (ulong)ctx->GetU32(rs) * ctx->GetU32(rt);
        ulong res = acc + prod;
        ctx->LO = SignExt32((uint)res);
        ctx->HI = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Msub(CpuContext* ctx, int rd, int rs, int rt)
    {
        long acc = (long)CombineHiLo(ctx->HI, ctx->LO);
        long prod = (long)ctx->GetS32(rs) * ctx->GetS32(rt);
        long res = acc - prod;
        ctx->LO = SignExt32((uint)res);
        ctx->HI = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Msubu(CpuContext* ctx, int rd, int rs, int rt)
    {
        ulong acc = CombineHiLo(ctx->HI, ctx->LO);
        ulong prod = (ulong)ctx->GetU32(rs) * ctx->GetU32(rt);
        ulong res = acc - prod;
        ctx->LO = SignExt32((uint)res);
        ctx->HI = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Madd1(CpuContext* ctx, int rd, int rs, int rt)
    {
        long acc = (long)CombineHiLo(ctx->HI1, ctx->LO1);
        long prod = (long)ctx->GetS32(rs) * ctx->GetS32(rt);
        long res = acc + prod;
        ctx->LO1 = SignExt32((uint)res);
        ctx->HI1 = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Maddu1(CpuContext* ctx, int rd, int rs, int rt)
    {
        ulong acc = CombineHiLo(ctx->HI1, ctx->LO1);
        ulong prod = (ulong)ctx->GetU32(rs) * ctx->GetU32(rt);
        ulong res = acc + prod;
        ctx->LO1 = SignExt32((uint)res);
        ctx->HI1 = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mult1(CpuContext* ctx, int rd, int rs, int rt)
    {
        long res = (long)ctx->GetS32(rs) * ctx->GetS32(rt);
        ctx->LO1 = SignExt32((uint)res);
        ctx->HI1 = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Multu1(CpuContext* ctx, int rd, int rs, int rt)
    {
        ulong res = (ulong)ctx->GetU32(rs) * ctx->GetU32(rt);
        ctx->LO1 = SignExt32((uint)res);
        ctx->HI1 = SignExt32((uint)(res >> 32));
        ctx->SetS32(rd, (int)res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Div1(CpuContext* ctx, int rs, int rt)
    {
        int dividend = ctx->GetS32(rs);
        int divisor = ctx->GetS32(rt);
        if (divisor == 0)
        {
            ctx->LO1 = dividend < 0 ? 1UL : 0xFFFFFFFFFFFFFFFFUL;
            ctx->HI1 = (ulong)(long)dividend;
        }
        else if (divisor == -1 && dividend == int.MinValue)
        {
            ctx->LO1 = unchecked((ulong)(long)int.MinValue);
            ctx->HI1 = 0UL;
        }
        else
        {
            ctx->LO1 = (ulong)(long)(dividend / divisor);
            ctx->HI1 = (ulong)(long)(dividend % divisor);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Divu1(CpuContext* ctx, int rs, int rt)
    {
        uint dividend = ctx->GetU32(rs);
        uint divisor = ctx->GetU32(rt);
        if (divisor == 0)
        {
            ctx->LO1 = 0xFFFFFFFFFFFFFFFFUL;
            ctx->HI1 = SignExt32(dividend);
        }
        else
        {
            ctx->LO1 = SignExt32(dividend / divisor);
            ctx->HI1 = SignExt32(dividend % divisor);
        }
    }

    // =========================================================================
    // PLZCW — Parallel Leading Zero/One Count Word
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Plzcw32(uint x)
    {
        if (x == 0u || x == 0xFFFFFFFFu)
            return 31u;
        if ((x & 0x80000000u) != 0)
            x = ~x;
        return (uint)BitOperations.LeadingZeroCount(x) - 1u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Plzcw(CpuContext* ctx, int rd, int rs)
    {
        if (rd == 0) return;
        ulong v = ctx->GetU64(rs);
        uint lo = Plzcw32((uint)v);
        uint hi = Plzcw32((uint)(v >> 32));
        ctx->SetU64(rd, ((ulong)hi << 32) | lo);
    }

    // =========================================================================
    // Packed Shifts (PSLLH, PSRLH, PSRAH, PSLLW, PSRLW, PSRAW, PSLLVW, PSRLVW, PSRAVW, QFSRV)
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psllh(Vector128<uint> rt, int sa) => Vector128.ShiftLeft(rt.AsUInt16(), sa & 0xF).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psrlh(Vector128<uint> rt, int sa) => Vector128.ShiftRightLogical(rt.AsUInt16(), sa & 0xF).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psrah(Vector128<uint> rt, int sa) => Vector128.ShiftRightArithmetic(rt.AsInt16(), sa & 0xF).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psllw(Vector128<uint> rt, int sa) => Vector128.ShiftLeft(rt, sa & 0x1F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psrlw(Vector128<uint> rt, int sa) => Vector128.ShiftRightLogical(rt, sa & 0x1F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psraw(Vector128<uint> rt, int sa) => Vector128.ShiftRightArithmetic(rt.AsInt32(), sa & 0x1F).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psllvw(Vector128<uint> rs, Vector128<uint> rt)
    {
        ulong lo = (ulong)(int)(rt.GetElement(0) << (int)(rs.GetElement(0) & 0x1Fu));
        ulong hi = (ulong)(int)(rt.GetElement(2) << (int)(rs.GetElement(2) & 0x1Fu));
        return Vector128.Create(lo, hi).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psrlvw(Vector128<uint> rs, Vector128<uint> rt)
    {
        ulong lo = (ulong)(int)(rt.GetElement(0) >> (int)(rs.GetElement(0) & 0x1Fu));
        ulong hi = (ulong)(int)(rt.GetElement(2) >> (int)(rs.GetElement(2) & 0x1Fu));
        return Vector128.Create(lo, hi).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psravw(Vector128<uint> rs, Vector128<uint> rt)
    {
        Vector128<int> rts = rt.AsInt32();
        ulong lo = (ulong)(long)(rts.GetElement(0) >> (int)(rs.GetElement(0) & 0x1Fu));
        ulong hi = (ulong)(long)(rts.GetElement(2) >> (int)(rs.GetElement(2) & 0x1Fu));
        return Vector128.Create(lo, hi).AsUInt32();
    }

    public static Vector128<uint> Qfsrv(Vector128<uint> rs, Vector128<uint> rt, uint saBits)
    {
        int sa = (int)(saBits & 0x7Fu);
        if (sa == 0) return rt;
        Vector128<ulong> rsu = rs.AsUInt64();
        Vector128<ulong> rtu = rt.AsUInt64();
        ulong w0 = rtu.GetElement(0);
        ulong w1 = rtu.GetElement(1);
        ulong w2 = rsu.GetElement(0);
        ulong w3 = rsu.GetElement(1);

        ulong out0, out1;
        if (sa < 64)
        {
            out0 = (w0 >> sa) | (w1 << (64 - sa));
            out1 = (w1 >> sa) | (w2 << (64 - sa));
        }
        else if (sa == 64)
        {
            out0 = w1;
            out1 = w2;
        }
        else
        {
            int s = sa - 64;
            out0 = (w1 >> s) | (w2 << (64 - s));
            out1 = (w2 >> s) | (w3 << (64 - s));
        }
        return Vector128.Create(out0, out1).AsUInt32();
    }

    // =========================================================================
    // MMI0 & MMI1 Packed Arithmetic, Compare, Min/Max, Pack/Extend
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddw(Vector128<uint> a, Vector128<uint> b) => (a.AsInt32() + b.AsInt32()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubw(Vector128<uint> a, Vector128<uint> b) => (a.AsInt32() - b.AsInt32()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddh(Vector128<uint> a, Vector128<uint> b) => (a.AsInt16() + b.AsInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubh(Vector128<uint> a, Vector128<uint> b) => (a.AsInt16() - b.AsInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddb(Vector128<uint> a, Vector128<uint> b) => (a.AsSByte() + b.AsSByte()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubb(Vector128<uint> a, Vector128<uint> b) => (a.AsSByte() - b.AsSByte()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcgtw(Vector128<uint> a, Vector128<uint> b) => Vector128.GreaterThan(a.AsInt32(), b.AsInt32()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcgth(Vector128<uint> a, Vector128<uint> b) => Vector128.GreaterThan(a.AsInt16(), b.AsInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcgtb(Vector128<uint> a, Vector128<uint> b) => Vector128.GreaterThan(a.AsSByte(), b.AsSByte()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pceqw(Vector128<uint> a, Vector128<uint> b) => Vector128.Equals(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pceqh(Vector128<uint> a, Vector128<uint> b) => Vector128.Equals(a.AsUInt16(), b.AsUInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pceqb(Vector128<uint> a, Vector128<uint> b) => Vector128.Equals(a.AsByte(), b.AsByte()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pmaxw(Vector128<uint> a, Vector128<uint> b) => Vector128.Max(a.AsInt32(), b.AsInt32()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pminw(Vector128<uint> a, Vector128<uint> b) => Vector128.Min(a.AsInt32(), b.AsInt32()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pmaxh(Vector128<uint> a, Vector128<uint> b) => Vector128.Max(a.AsInt16(), b.AsInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pminh(Vector128<uint> a, Vector128<uint> b) => Vector128.Min(a.AsInt16(), b.AsInt16()).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pabsw(Vector128<uint> a)
    {
        var s = a.AsInt32();
        return Vector128.Create(
            s.GetElement(0) == int.MinValue ? int.MaxValue : Math.Abs(s.GetElement(0)),
            s.GetElement(1) == int.MinValue ? int.MaxValue : Math.Abs(s.GetElement(1)),
            s.GetElement(2) == int.MinValue ? int.MaxValue : Math.Abs(s.GetElement(2)),
            s.GetElement(3) == int.MinValue ? int.MaxValue : Math.Abs(s.GetElement(3))).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pabsh(Vector128<uint> a)
    {
        var s = a.AsInt16();
        Span<short> res = stackalloc short[8];
        for (int i = 0; i < 8; i++)
        {
            short v = s.GetElement(i);
            res[i] = v == short.MinValue ? short.MaxValue : Math.Abs(v);
        }
        return Vector128.Create<short>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddsw(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsInt32();
        var sb = b.AsInt32();
        return Vector128.Create(
            (int)Math.Clamp((long)sa.GetElement(0) + sb.GetElement(0), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(1) + sb.GetElement(1), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(2) + sb.GetElement(2), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(3) + sb.GetElement(3), int.MinValue, int.MaxValue)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubsw(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsInt32();
        var sb = b.AsInt32();
        return Vector128.Create(
            (int)Math.Clamp((long)sa.GetElement(0) - sb.GetElement(0), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(1) - sb.GetElement(1), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(2) - sb.GetElement(2), int.MinValue, int.MaxValue),
            (int)Math.Clamp((long)sa.GetElement(3) - sb.GetElement(3), int.MinValue, int.MaxValue)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddsh(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsInt16();
        var sb = b.AsInt16();
        Span<short> res = stackalloc short[8];
        for (int i = 0; i < 8; i++)
            res[i] = (short)Math.Clamp(sa.GetElement(i) + sb.GetElement(i), short.MinValue, short.MaxValue);
        return Vector128.Create<short>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubsh(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsInt16();
        var sb = b.AsInt16();
        Span<short> res = stackalloc short[8];
        for (int i = 0; i < 8; i++)
            res[i] = (short)Math.Clamp(sa.GetElement(i) - sb.GetElement(i), short.MinValue, short.MaxValue);
        return Vector128.Create<short>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddsb(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsSByte();
        var sb = b.AsSByte();
        Span<sbyte> res = stackalloc sbyte[16];
        for (int i = 0; i < 16; i++)
            res[i] = (sbyte)Math.Clamp(sa.GetElement(i) + sb.GetElement(i), sbyte.MinValue, sbyte.MaxValue);
        return Vector128.Create<sbyte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubsb(Vector128<uint> a, Vector128<uint> b)
    {
        var sa = a.AsSByte();
        var sb = b.AsSByte();
        Span<sbyte> res = stackalloc sbyte[16];
        for (int i = 0; i < 16; i++)
            res[i] = (sbyte)Math.Clamp(sa.GetElement(i) - sb.GetElement(i), sbyte.MinValue, sbyte.MaxValue);
        return Vector128.Create<sbyte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Padduw(Vector128<uint> a, Vector128<uint> b)
    {
        return Vector128.Create(
            (uint)Math.Min((ulong)a.GetElement(0) + b.GetElement(0), uint.MaxValue),
            (uint)Math.Min((ulong)a.GetElement(1) + b.GetElement(1), uint.MaxValue),
            (uint)Math.Min((ulong)a.GetElement(2) + b.GetElement(2), uint.MaxValue),
            (uint)Math.Min((ulong)a.GetElement(3) + b.GetElement(3), uint.MaxValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubuw(Vector128<uint> a, Vector128<uint> b)
    {
        return Vector128.Create(
            a.GetElement(0) > b.GetElement(0) ? a.GetElement(0) - b.GetElement(0) : 0u,
            a.GetElement(1) > b.GetElement(1) ? a.GetElement(1) - b.GetElement(1) : 0u,
            a.GetElement(2) > b.GetElement(2) ? a.GetElement(2) - b.GetElement(2) : 0u,
            a.GetElement(3) > b.GetElement(3) ? a.GetElement(3) - b.GetElement(3) : 0u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Padduh(Vector128<uint> a, Vector128<uint> b)
    {
        var ua = a.AsUInt16();
        var ub = b.AsUInt16();
        Span<ushort> res = stackalloc ushort[8];
        for (int i = 0; i < 8; i++)
            res[i] = (ushort)Math.Min(ua.GetElement(i) + ub.GetElement(i), ushort.MaxValue);
        return Vector128.Create<ushort>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubuh(Vector128<uint> a, Vector128<uint> b)
    {
        var ua = a.AsUInt16();
        var ub = b.AsUInt16();
        Span<ushort> res = stackalloc ushort[8];
        for (int i = 0; i < 8; i++)
            res[i] = (ushort)Math.Max(ua.GetElement(i) - ub.GetElement(i), 0);
        return Vector128.Create<ushort>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Paddub(Vector128<uint> a, Vector128<uint> b)
    {
        var ua = a.AsByte();
        var ub = b.AsByte();
        Span<byte> res = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            res[i] = (byte)Math.Min(ua.GetElement(i) + ub.GetElement(i), byte.MaxValue);
        return Vector128.Create<byte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Psubub(Vector128<uint> a, Vector128<uint> b)
    {
        var ua = a.AsByte();
        var ub = b.AsByte();
        Span<byte> res = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            res[i] = (byte)Math.Max(ua.GetElement(i) - ub.GetElement(i), 0);
        return Vector128.Create<byte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Padsbh(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsInt16();
        var b = rt.AsInt16();
        return Vector128.Create(
            (short)(a.GetElement(0) - b.GetElement(0)),
            (short)(a.GetElement(1) - b.GetElement(1)),
            (short)(a.GetElement(2) - b.GetElement(2)),
            (short)(a.GetElement(3) - b.GetElement(3)),
            (short)(a.GetElement(4) + b.GetElement(4)),
            (short)(a.GetElement(5) + b.GetElement(5)),
            (short)(a.GetElement(6) + b.GetElement(6)),
            (short)(a.GetElement(7) + b.GetElement(7))).AsUInt32();
    }

    // Extend / Pack operations (Note: PS2 PEXTL* interleaves rt in even lanes and rs in odd lanes)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextlw(Vector128<uint> rs, Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(0), rs.GetElement(0), rt.GetElement(1), rs.GetElement(1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextuw(Vector128<uint> rs, Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(2), rs.GetElement(2), rt.GetElement(3), rs.GetElement(3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextlh(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsUInt16();
        var b = rt.AsUInt16();
        return Vector128.Create(
            b.GetElement(0), a.GetElement(0), b.GetElement(1), a.GetElement(1),
            b.GetElement(2), a.GetElement(2), b.GetElement(3), a.GetElement(3)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextuh(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsUInt16();
        var b = rt.AsUInt16();
        return Vector128.Create(
            b.GetElement(4), a.GetElement(4), b.GetElement(5), a.GetElement(5),
            b.GetElement(6), a.GetElement(6), b.GetElement(7), a.GetElement(7)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextlb(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsByte();
        var b = rt.AsByte();
        Span<byte> res = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            res[i * 2] = b.GetElement(i);
            res[i * 2 + 1] = a.GetElement(i);
        }
        return Vector128.Create<byte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pextub(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsByte();
        var b = rt.AsByte();
        Span<byte> res = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            res[i * 2] = b.GetElement(i + 8);
            res[i * 2 + 1] = a.GetElement(i + 8);
        }
        return Vector128.Create<byte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ppacw(Vector128<uint> rs, Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(0), rt.GetElement(2), rs.GetElement(0), rs.GetElement(2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ppach(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsUInt16();
        var b = rt.AsUInt16();
        return Vector128.Create(
            b.GetElement(0), b.GetElement(2), b.GetElement(4), b.GetElement(6),
            a.GetElement(0), a.GetElement(2), a.GetElement(4), a.GetElement(6)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ppacb(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsByte();
        var b = rt.AsByte();
        Span<byte> res = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            res[i] = b.GetElement(i * 2);
            res[i + 8] = a.GetElement(i * 2);
        }
        return Vector128.Create<byte>(res).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pext5(Vector128<uint> rt)
    {
        Span<uint> res = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            uint v = rt.GetElement(i);
            res[i] = ((v & 0x001Fu) << 3) | ((v & 0x03E0u) << 6) | ((v & 0x7C00u) << 9) | ((v & 0x8000u) << 16);
        }
        return Vector128.Create<uint>(res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Ppac5(Vector128<uint> rt)
    {
        Span<uint> res = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
        {
            uint v = rt.GetElement(i);
            res[i] = ((v >> 3) & 0x001Fu) | ((v >> 6) & 0x03E0u) | ((v >> 9) & 0x7C00u) | ((v >> 16) & 0x8000u);
        }
        return Vector128.Create<uint>(res);
    }

    // =========================================================================
    // MMI2 & MMI3 Permutation, Logical, Parallel Multiply/Divide, HI/LO Moves
    // =========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pinth(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsUInt16();
        var b = rt.AsUInt16();
        return Vector128.Create(
            b.GetElement(0), a.GetElement(4), b.GetElement(1), a.GetElement(5),
            b.GetElement(2), a.GetElement(6), b.GetElement(3), a.GetElement(7)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pinteh(Vector128<uint> rs, Vector128<uint> rt)
    {
        var a = rs.AsUInt16();
        var b = rt.AsUInt16();
        return Vector128.Create(
            b.GetElement(0), a.GetElement(0), b.GetElement(2), a.GetElement(2),
            b.GetElement(4), a.GetElement(4), b.GetElement(6), a.GetElement(6)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcpyld(Vector128<uint> rs, Vector128<uint> rt)
        => Vector128.Create(rt.AsUInt64().GetElement(0), rs.AsUInt64().GetElement(0)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcpyud(Vector128<uint> rs, Vector128<uint> rt)
        => Vector128.Create(rs.AsUInt64().GetElement(1), rt.AsUInt64().GetElement(1)).AsUInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pcpyh(Vector128<uint> rt)
    {
        var h = rt.AsUInt16();
        ushort h0 = h.GetElement(0);
        ushort h4 = h.GetElement(4);
        return Vector128.Create(h0, h0, h0, h0, h4, h4, h4, h4).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pexeh(Vector128<uint> rt)
    {
        var h = rt.AsUInt16();
        return Vector128.Create(
            h.GetElement(2), h.GetElement(1), h.GetElement(0), h.GetElement(3),
            h.GetElement(6), h.GetElement(5), h.GetElement(4), h.GetElement(7)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pexch(Vector128<uint> rt)
    {
        var h = rt.AsUInt16();
        return Vector128.Create(
            h.GetElement(0), h.GetElement(2), h.GetElement(1), h.GetElement(3),
            h.GetElement(4), h.GetElement(6), h.GetElement(5), h.GetElement(7)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Prevh(Vector128<uint> rt)
    {
        var h = rt.AsUInt16();
        return Vector128.Create(
            h.GetElement(3), h.GetElement(2), h.GetElement(1), h.GetElement(0),
            h.GetElement(7), h.GetElement(6), h.GetElement(5), h.GetElement(4)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pexew(Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(2), rt.GetElement(1), rt.GetElement(0), rt.GetElement(3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Pexcw(Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(0), rt.GetElement(2), rt.GetElement(1), rt.GetElement(3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> Prot3w(Vector128<uint> rt)
        => Vector128.Create(rt.GetElement(1), rt.GetElement(2), rt.GetElement(0), rt.GetElement(3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmfhi(CpuContext* ctx, int rd) => ctx->SetVec(rd, Vector128.Create(ctx->HI, ctx->HI1).AsUInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmflo(CpuContext* ctx, int rd) => ctx->SetVec(rd, Vector128.Create(ctx->LO, ctx->LO1).AsUInt32());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmthi(CpuContext* ctx, int rs)
    {
        var v = ctx->GetVec(rs).AsUInt64();
        ctx->HI = v.GetElement(0);
        ctx->HI1 = v.GetElement(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmtlo(CpuContext* ctx, int rs)
    {
        var v = ctx->GetVec(rs).AsUInt64();
        ctx->LO = v.GetElement(0);
        ctx->LO1 = v.GetElement(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmfhl(CpuContext* ctx, int rd, uint sub)
    {
        if (rd == 0) return;
        uint lo0 = (uint)ctx->LO;
        uint hi0 = (uint)ctx->HI;
        uint lo1 = (uint)ctx->LO1;
        uint hi1 = (uint)ctx->HI1;

        Vector128<uint> res = sub switch
        {
            0 => Vector128.Create(lo0, hi0, lo1, hi1), // PMFHL.LW
            1 => Vector128.Create((uint)(ctx->LO >> 32), (uint)(ctx->HI >> 32), (uint)(ctx->LO1 >> 32), (uint)(ctx->HI1 >> 32)), // PMFHL.UW
            2 => Vector128.Create(
                (ulong)(int)Math.Clamp((long)CombineHiLo(ctx->HI, ctx->LO), int.MinValue, int.MaxValue),
                (ulong)(int)Math.Clamp((long)CombineHiLo(ctx->HI1, ctx->LO1), int.MinValue, int.MaxValue)).AsUInt32(), // PMFHL.SLW
            3 => Vector128.Create(
                (ushort)lo0, (ushort)(lo0 >> 16), (ushort)hi0, (ushort)(hi0 >> 16),
                (ushort)lo1, (ushort)(lo1 >> 16), (ushort)hi1, (ushort)(hi1 >> 16)).AsUInt32(), // PMFHL.LH
            _ => Vector128.Create(
                (short)Math.Clamp((int)lo0, short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)(lo0 >> 16), short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)hi0, short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)(hi0 >> 16), short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)lo1, short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)(lo1 >> 16), short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)hi1, short.MinValue, short.MaxValue),
                (short)Math.Clamp((int)(hi1 >> 16), short.MinValue, short.MaxValue)).AsUInt32() // PMFHL.SH
        };
        ctx->SetVec(rd, res);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Pmthl(CpuContext* ctx, int rs)
    {
        var v = ctx->GetVec(rs);
        ctx->LO = SignExt32(v.GetElement(0));
        ctx->HI = SignExt32(v.GetElement(1));
        ctx->LO1 = SignExt32(v.GetElement(2));
        ctx->HI1 = SignExt32(v.GetElement(3));
    }

    public static void Pmultw(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt32();
        var b = ctx->GetVec(rt).AsInt32();
        long p0 = (long)a.GetElement(0) * b.GetElement(0);
        long p1 = (long)a.GetElement(2) * b.GetElement(2);
        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)(p0 >> 32));
        ctx->LO1 = SignExt32((uint)p1);
        ctx->HI1 = SignExt32((uint)(p1 >> 32));
        ctx->SetVec(rd, Vector128.Create((ulong)p0, (ulong)p1).AsUInt32());
    }

    public static void Pmultuw(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs);
        var b = ctx->GetVec(rt);
        ulong p0 = (ulong)a.GetElement(0) * b.GetElement(0);
        ulong p1 = (ulong)a.GetElement(2) * b.GetElement(2);
        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)(p0 >> 32));
        ctx->LO1 = SignExt32((uint)p1);
        ctx->HI1 = SignExt32((uint)(p1 >> 32));
        ctx->SetVec(rd, Vector128.Create(p0, p1).AsUInt32());
    }

    public static void Pmaddw(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt32();
        var b = ctx->GetVec(rt).AsInt32();
        long p0 = (long)CombineHiLo(ctx->HI, ctx->LO) + (long)a.GetElement(0) * b.GetElement(0);
        long p1 = (long)CombineHiLo(ctx->HI1, ctx->LO1) + (long)a.GetElement(2) * b.GetElement(2);
        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)(p0 >> 32));
        ctx->LO1 = SignExt32((uint)p1);
        ctx->HI1 = SignExt32((uint)(p1 >> 32));
        ctx->SetVec(rd, Vector128.Create((ulong)p0, (ulong)p1).AsUInt32());
    }

    public static void Pmadduw(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs);
        var b = ctx->GetVec(rt);
        ulong p0 = CombineHiLo(ctx->HI, ctx->LO) + (ulong)a.GetElement(0) * b.GetElement(0);
        ulong p1 = CombineHiLo(ctx->HI1, ctx->LO1) + (ulong)a.GetElement(2) * b.GetElement(2);
        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)(p0 >> 32));
        ctx->LO1 = SignExt32((uint)p1);
        ctx->HI1 = SignExt32((uint)(p1 >> 32));
        ctx->SetVec(rd, Vector128.Create(p0, p1).AsUInt32());
    }

    public static void Pmsubw(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt32();
        var b = ctx->GetVec(rt).AsInt32();
        long p0 = (long)CombineHiLo(ctx->HI, ctx->LO) - (long)a.GetElement(0) * b.GetElement(0);
        long p1 = (long)CombineHiLo(ctx->HI1, ctx->LO1) - (long)a.GetElement(2) * b.GetElement(2);
        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)(p0 >> 32));
        ctx->LO1 = SignExt32((uint)p1);
        ctx->HI1 = SignExt32((uint)(p1 >> 32));
        ctx->SetVec(rd, Vector128.Create((ulong)p0, (ulong)p1).AsUInt32());
    }

    public static void Pdivw(CpuContext* ctx, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt32();
        var b = ctx->GetVec(rt).AsInt32();

        int a0 = a.GetElement(0), b0 = b.GetElement(0);
        if (b0 == 0) { ctx->LO = a0 < 0 ? 1UL : 0xFFFFFFFFFFFFFFFFUL; ctx->HI = (ulong)(long)a0; }
        else if (b0 == -1 && a0 == int.MinValue) { ctx->LO = unchecked((ulong)(long)int.MinValue); ctx->HI = 0UL; }
        else { ctx->LO = (ulong)(long)(a0 / b0); ctx->HI = (ulong)(long)(a0 % b0); }

        int a2 = a.GetElement(2), b2 = b.GetElement(2);
        if (b2 == 0) { ctx->LO1 = a2 < 0 ? 1UL : 0xFFFFFFFFFFFFFFFFUL; ctx->HI1 = (ulong)(long)a2; }
        else if (b2 == -1 && a2 == int.MinValue) { ctx->LO1 = unchecked((ulong)(long)int.MinValue); ctx->HI1 = 0UL; }
        else { ctx->LO1 = (ulong)(long)(a2 / b2); ctx->HI1 = (ulong)(long)(a2 % b2); }
    }

    public static void Pdivuw(CpuContext* ctx, int rs, int rt)
    {
        var a = ctx->GetVec(rs);
        var b = ctx->GetVec(rt);

        uint a0 = a.GetElement(0), b0 = b.GetElement(0);
        if (b0 == 0) { ctx->LO = 0xFFFFFFFFFFFFFFFFUL; ctx->HI = SignExt32(a0); }
        else { ctx->LO = SignExt32(a0 / b0); ctx->HI = SignExt32(a0 % b0); }

        uint a2 = a.GetElement(2), b2 = b.GetElement(2);
        if (b2 == 0) { ctx->LO1 = 0xFFFFFFFFFFFFFFFFUL; ctx->HI1 = SignExt32(a2); }
        else { ctx->LO1 = SignExt32(a2 / b2); ctx->HI1 = SignExt32(a2 % b2); }
    }

    public static void Pdivbw(CpuContext* ctx, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt32();
        int div = (short)ctx->GetVec(rt).AsInt16().GetElement(0);
        Span<int> q = stackalloc int[4];
        Span<int> r = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            int num = a.GetElement(i);
            if (div == 0) { q[i] = num < 0 ? 1 : -1; r[i] = num; }
            else if (div == -1 && num == int.MinValue) { q[i] = int.MinValue; r[i] = 0; }
            else { q[i] = num / div; r[i] = num % div; }
        }
        ctx->LO = (ulong)(uint)q[0] | ((ulong)(uint)q[1] << 32);
        ctx->HI = (ulong)(uint)r[0] | ((ulong)(uint)r[1] << 32);
        ctx->LO1 = (ulong)(uint)q[2] | ((ulong)(uint)q[3] << 32);
        ctx->HI1 = (ulong)(uint)r[2] | ((ulong)(uint)r[3] << 32);
    }

    public static void Pmulth(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt16();
        var b = ctx->GetVec(rt).AsInt16();
        Span<int> p = stackalloc int[8];
        for (int i = 0; i < 8; i++)
            p[i] = a.GetElement(i) * b.GetElement(i);

        ctx->LO = (ulong)(uint)p[0] | ((ulong)(uint)p[1] << 32);
        ctx->HI = (ulong)(uint)p[2] | ((ulong)(uint)p[3] << 32);
        ctx->LO1 = (ulong)(uint)p[4] | ((ulong)(uint)p[5] << 32);
        ctx->HI1 = (ulong)(uint)p[6] | ((ulong)(uint)p[7] << 32);
        ctx->SetVec(rd, Vector128.Create(p[0], p[2], p[4], p[6]).AsUInt32());
    }

    public static void Pmaddh(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt16();
        var b = ctx->GetVec(rt).AsInt16();
        int p0 = (int)ctx->LO + a.GetElement(0) * b.GetElement(0);
        int p1 = (int)(ctx->LO >> 32) + a.GetElement(1) * b.GetElement(1);
        int p2 = (int)ctx->HI + a.GetElement(2) * b.GetElement(2);
        int p3 = (int)(ctx->HI >> 32) + a.GetElement(3) * b.GetElement(3);
        int p4 = (int)ctx->LO1 + a.GetElement(4) * b.GetElement(4);
        int p5 = (int)(ctx->LO1 >> 32) + a.GetElement(5) * b.GetElement(5);
        int p6 = (int)ctx->HI1 + a.GetElement(6) * b.GetElement(6);
        int p7 = (int)(ctx->HI1 >> 32) + a.GetElement(7) * b.GetElement(7);

        ctx->LO = (ulong)(uint)p0 | ((ulong)(uint)p1 << 32);
        ctx->HI = (ulong)(uint)p2 | ((ulong)(uint)p3 << 32);
        ctx->LO1 = (ulong)(uint)p4 | ((ulong)(uint)p5 << 32);
        ctx->HI1 = (ulong)(uint)p6 | ((ulong)(uint)p7 << 32);
        ctx->SetVec(rd, Vector128.Create(p0, p2, p4, p6).AsUInt32());
    }

    public static void Pmsubh(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt16();
        var b = ctx->GetVec(rt).AsInt16();
        int p0 = (int)ctx->LO - a.GetElement(0) * b.GetElement(0);
        int p1 = (int)(ctx->LO >> 32) - a.GetElement(1) * b.GetElement(1);
        int p2 = (int)ctx->HI - a.GetElement(2) * b.GetElement(2);
        int p3 = (int)(ctx->HI >> 32) - a.GetElement(3) * b.GetElement(3);
        int p4 = (int)ctx->LO1 - a.GetElement(4) * b.GetElement(4);
        int p5 = (int)(ctx->LO1 >> 32) - a.GetElement(5) * b.GetElement(5);
        int p6 = (int)ctx->HI1 - a.GetElement(6) * b.GetElement(6);
        int p7 = (int)(ctx->HI1 >> 32) - a.GetElement(7) * b.GetElement(7);

        ctx->LO = (ulong)(uint)p0 | ((ulong)(uint)p1 << 32);
        ctx->HI = (ulong)(uint)p2 | ((ulong)(uint)p3 << 32);
        ctx->LO1 = (ulong)(uint)p4 | ((ulong)(uint)p5 << 32);
        ctx->HI1 = (ulong)(uint)p6 | ((ulong)(uint)p7 << 32);
        ctx->SetVec(rd, Vector128.Create(p0, p2, p4, p6).AsUInt32());
    }

    public static void Phmadh(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt16();
        var b = ctx->GetVec(rt).AsInt16();
        int p0 = a.GetElement(0) * b.GetElement(0) + a.GetElement(1) * b.GetElement(1);
        int p1 = a.GetElement(2) * b.GetElement(2) + a.GetElement(3) * b.GetElement(3);
        int p2 = a.GetElement(4) * b.GetElement(4) + a.GetElement(5) * b.GetElement(5);
        int p3 = a.GetElement(6) * b.GetElement(6) + a.GetElement(7) * b.GetElement(7);

        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)p1);
        ctx->LO1 = SignExt32((uint)p2);
        ctx->HI1 = SignExt32((uint)p3);
        ctx->SetVec(rd, Vector128.Create(p0, p1, p2, p3).AsUInt32());
    }

    public static void Phmsbh(CpuContext* ctx, int rd, int rs, int rt)
    {
        var a = ctx->GetVec(rs).AsInt16();
        var b = ctx->GetVec(rt).AsInt16();
        int p0 = a.GetElement(1) * b.GetElement(1) - a.GetElement(0) * b.GetElement(0);
        int p1 = a.GetElement(3) * b.GetElement(3) - a.GetElement(2) * b.GetElement(2);
        int p2 = a.GetElement(5) * b.GetElement(5) - a.GetElement(4) * b.GetElement(4);
        int p3 = a.GetElement(7) * b.GetElement(7) - a.GetElement(6) * b.GetElement(6);

        ctx->LO = SignExt32((uint)p0);
        ctx->HI = SignExt32((uint)p1);
        ctx->LO1 = SignExt32((uint)p2);
        ctx->HI1 = SignExt32((uint)p3);
        ctx->SetVec(rd, Vector128.Create(p0, p1, p2, p3).AsUInt32());
    }
}
