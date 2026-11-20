using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Castaway.Core;

/// <summary>
/// Sony PS2 Emotion Engine COP2 (VU0 Macro Mode) SIMD instructions.
/// </summary>
public static unsafe class Ps2Vu0
{
    // destMask is 4-bit XYZW mask where bit 3=X (lane 0), bit 2=Y (lane 1), bit 1=Z (lane 2), bit 0=W (lane 3).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> BlendMask(Vector128<float> oldVal, Vector128<float> newVal, uint destMask)
    {
        if (Sse41.IsSupported)
        {
            return (destMask & 0xFu) switch
            {
                0x0u => oldVal,
                0x1u => Sse41.Blend(oldVal, newVal, 0x8),
                0x2u => Sse41.Blend(oldVal, newVal, 0x4),
                0x3u => Sse41.Blend(oldVal, newVal, 0xC),
                0x4u => Sse41.Blend(oldVal, newVal, 0x2),
                0x5u => Sse41.Blend(oldVal, newVal, 0xA),
                0x6u => Sse41.Blend(oldVal, newVal, 0x6),
                0x7u => Sse41.Blend(oldVal, newVal, 0xE),
                0x8u => Sse41.Blend(oldVal, newVal, 0x1),
                0x9u => Sse41.Blend(oldVal, newVal, 0x9),
                0xAu => Sse41.Blend(oldVal, newVal, 0x5),
                0xBu => Sse41.Blend(oldVal, newVal, 0xD),
                0xCu => Sse41.Blend(oldVal, newVal, 0x3),
                0xDu => Sse41.Blend(oldVal, newVal, 0xB),
                0xEu => Sse41.Blend(oldVal, newVal, 0x7),
                _ => newVal,
            };
        }

        if (destMask == 0xFu) return newVal;
        if (destMask == 0u) return oldVal;
        Vector128<int> mask = Vector128.Create(
            (destMask & 0x8u) != 0 ? -1 : 0,
            (destMask & 0x4u) != 0 ? -1 : 0,
            (destMask & 0x2u) != 0 ? -1 : 0,
            (destMask & 0x1u) != 0 ? -1 : 0);
        return Vector128.ConditionalSelect(mask.AsSingle(), newVal, oldVal);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> BroadcastBc(Vector128<float> v, uint bc)
    {
        if (Avx.IsSupported)
        {
            return (bc & 3u) switch
            {
                0u => Avx.Permute(v, 0x00),
                1u => Avx.Permute(v, 0x55),
                2u => Avx.Permute(v, 0xAA),
                _ => Avx.Permute(v, 0xFF),
            };
        }
        if (Sse.IsSupported)
        {
            return (bc & 3u) switch
            {
                0u => Sse.Shuffle(v, v, 0x00),
                1u => Sse.Shuffle(v, v, 0x55),
                2u => Sse.Shuffle(v, v, 0xAA),
                _ => Sse.Shuffle(v, v, 0xFF),
            };
        }
        return (bc & 3u) switch
        {
            0u => Vector128.Shuffle(v, Vector128<int>.Zero),
            1u => Vector128.Shuffle(v, Vector128<int>.One),
            2u => Vector128.Shuffle(v, Vector128.Create(2)),
            _ => Vector128.Shuffle(v, Vector128.Create(3)),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetVf(CpuContext* ctx, int vfd, Vector128<float> newVal, uint destMask)
    {
        if (vfd == 0) return; // VF0 is hardwired to (0, 0, 0, 1)
        ctx->VF[vfd] = BlendMask(ctx->VF[vfd], newVal, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetAcc(CpuContext* ctx, Vector128<float> newVal, uint destMask)
    {
        ctx->Vu0Acc = BlendMask(ctx->Vu0Acc, newVal, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetVi(CpuContext* ctx, int vid, ushort val)
    {
        if (vid != 0 && vid < 16)
            ctx->VI[vid] = val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Cfc2(CpuContext* ctx, int reg)
    {
        if (reg < 16)
            return reg == 0 ? 0u : ctx->VI[reg];
        return reg switch
        {
            16 => ctx->Vu0Status,
            17 => ctx->Vu0MacFlags,
            18 => ctx->Vu0ClipFlags & 0x00FFFFFFu,
            20 => ctx->Vu0R.AsUInt32().GetElement(0),
            21 => BitConverter.SingleToUInt32Bits(ctx->Vu0I),
            22 => BitConverter.SingleToUInt32Bits(ctx->Vu0Q),
            26 => ctx->Vu0Tpc >> 3,
            27 => ctx->Vu0Cmsar0,
            28 => ctx->Vu0Fbrst,
            29 => ctx->Vu0VpuStat,
            31 => ctx->Vu0Cmsar1,
            _ => 0u
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ctc2(CpuContext* ctx, int reg, uint val)
    {
        if (reg < 16)
        {
            if (reg != 0)
                ctx->VI[reg] = (ushort)val;
            return;
        }
        switch (reg)
        {
            case 16: ctx->Vu0Status = val & 0xFFFFu; break;
            case 18: ctx->Vu0ClipFlags = val & 0x00FFFFFFu; break;
            case 20: ctx->Vu0R = Vector128.Create(val).AsSingle(); break;
            case 21: ctx->Vu0I = BitConverter.UInt32BitsToSingle(val); break;
            case 22: ctx->Vu0Q = BitConverter.UInt32BitsToSingle(val); break;
            case 27: ctx->Vu0Cmsar0 = val; break;
            case 28: ctx->Vu0Fbrst = val & 0x00000C0Cu; break;
            case 31: ctx->Vu0Cmsar1 = val; break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> VecOrderedKey(Vector128<float> x)
    {
        Vector128<int> bits = x.AsInt32();
        Vector128<int> mag = bits & Vector128.Create(0x7FFFFFFF);
        Vector128<int> nonDenorm = ~Vector128.LessThan(mag, Vector128.Create(0x00800000));
        mag &= nonDenorm;
        Vector128<int> sign = Vector128.ShiftRightArithmetic(bits, 31);
        return (mag ^ sign) - sign;
    }

    // Vector Max / Min (using PS2 float ordering: ToOrderedKey(a) >=/<= ToOrderedKey(b) ? a : b)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecMax(Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> pickB = Vector128.LessThan(VecOrderedKey(a), VecOrderedKey(b)).AsSingle();
        return Vector128.ConditionalSelect(pickB, b, a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecMin(Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> pickB = Vector128.LessThan(VecOrderedKey(b), VecOrderedKey(a)).AsSingle();
        return Vector128.ConditionalSelect(pickB, b, a);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecMaxSlow(Vector128<float> a, Vector128<float> b) => VecMax(a, b);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecMinSlow(Vector128<float> a, Vector128<float> b) => VecMin(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vopmula(CpuContext* ctx, int vfs, int vft, uint destMask)
    {
        var fs = ctx->VF[vfs];
        var ft = ctx->VF[vft];
        var fsYzx = Vector128.Create(fs.GetElement(1), fs.GetElement(2), fs.GetElement(0), fs.GetElement(3));
        var ftZxy = Vector128.Create(ft.GetElement(2), ft.GetElement(0), ft.GetElement(1), ft.GetElement(3));
        SetAcc(ctx, Ps2Float.VecMul(fsYzx, ftZxy), destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vopmsub(CpuContext* ctx, int vfd, int vfs, int vft, uint destMask)
    {
        var fs = ctx->VF[vfs];
        var ft = ctx->VF[vft];
        var fsYzx = Vector128.Create(fs.GetElement(1), fs.GetElement(2), fs.GetElement(0), fs.GetElement(3));
        var ftZxy = Vector128.Create(ft.GetElement(2), ft.GetElement(0), ft.GetElement(1), ft.GetElement(3));
        var prod = Ps2Float.VecMul(fsYzx, ftZxy);
        SetVf(ctx, vfd, Ps2Float.VecSub(ctx->Vu0Acc, prod), destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vitof(CpuContext* ctx, int vft, int vfs, int shift, uint destMask)
    {
        var s = ctx->VF[vfs].AsInt32();
        float scale = shift == 0 ? 1.0f : (1.0f / (1 << shift));
        var res = Vector128.Create(
            s.GetElement(0) * scale,
            s.GetElement(1) * scale,
            s.GetElement(2) * scale,
            s.GetElement(3) * scale);
        SetVf(ctx, vft, res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vftoi(CpuContext* ctx, int vft, int vfs, int shift, uint destMask)
    {
        var s = ctx->VF[vfs];
        float scale = shift == 0 ? 1.0f : (float)(1 << shift);
        var res = Vector128.Create(
            Ps2Float.CvtWS(s.GetElement(0) * scale),
            Ps2Float.CvtWS(s.GetElement(1) * scale),
            Ps2Float.CvtWS(s.GetElement(2) * scale),
            Ps2Float.CvtWS(s.GetElement(3) * scale)).AsSingle();
        SetVf(ctx, vft, res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vabs(CpuContext* ctx, int vft, int vfs, uint destMask)
    {
        var res = (ctx->VF[vfs].AsUInt32() & Vector128.Create(0x7FFFFFFFu)).AsSingle();
        SetVf(ctx, vft, res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmove(CpuContext* ctx, int vft, int vfs, uint destMask)
    {
        SetVf(ctx, vft, ctx->VF[vfs], destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmr32(CpuContext* ctx, int vft, int vfs, uint destMask)
    {
        var s = ctx->VF[vfs];
        var res = Vector128.Create(s.GetElement(1), s.GetElement(2), s.GetElement(3), s.GetElement(0));
        SetVf(ctx, vft, res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vclip(CpuContext* ctx, int vfs, int vft)
    {
        var fs = ctx->VF[vfs];
        float w = MathF.Abs(ctx->VF[vft].GetElement(3));
        uint flags = 0u;
        if (fs.GetElement(0) > w) flags |= 0x01u;
        if (fs.GetElement(0) < -w) flags |= 0x02u;
        if (fs.GetElement(1) > w) flags |= 0x04u;
        if (fs.GetElement(1) < -w) flags |= 0x08u;
        if (fs.GetElement(2) > w) flags |= 0x10u;
        if (fs.GetElement(2) < -w) flags |= 0x20u;
        ctx->Vu0ClipFlags = ((ctx->Vu0ClipFlags << 6) | flags) & 0x00FFFFFFu;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vdiv(CpuContext* ctx, int vfs, uint fsf, int vft, uint ftf)
    {
        float num = ctx->VF[vfs].GetElement((int)(fsf & 3u));
        float den = ctx->VF[vft].GetElement((int)(ftf & 3u));
        ctx->Vu0Q = Ps2Float.Div(num, den);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vsqrt(CpuContext* ctx, int vft, uint ftf)
    {
        float val = ctx->VF[vft].GetElement((int)(ftf & 3u));
        ctx->Vu0Q = Ps2Float.Sqrt(val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vrsqrt(CpuContext* ctx, int vfs, uint fsf, int vft, uint ftf)
    {
        float num = ctx->VF[vfs].GetElement((int)(fsf & 3u));
        float den = ctx->VF[vft].GetElement((int)(ftf & 3u));
        ctx->Vu0Q = Ps2Float.Rsqrt(num, den);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmtir(CpuContext* ctx, int vit, int vfs, uint fsf)
    {
        uint bits = ctx->VF[vfs].AsUInt32().GetElement((int)(fsf & 3u));
        SetVi(ctx, vit, (ushort)bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vmfir(CpuContext* ctx, int vft, int vis, uint destMask)
    {
        int ext = (short)(vis == 0 ? 0 : ctx->VI[vis]);
        var res = Vector128.Create(ext).AsSingle();
        SetVf(ctx, vft, res, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vlqi(CpuContext* ctx, byte* ram, int vft, int vis, uint destMask)
    {
        ushort idx = vis == 0 ? (ushort)0 : ctx->VI[vis];
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        Vector128<float> mem = ctx->Vu0DataMem != null
            ? Unsafe.ReadUnaligned<Vector128<float>>(ctx->Vu0DataMem + addr)
            : Ps2Memory.Read128(ctx, ram, 0x11004000u + addr).AsSingle();
        SetVf(ctx, vft, mem, destMask);
        SetVi(ctx, vis, (ushort)((idx + 1) & 0x3FF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vsqi(CpuContext* ctx, byte* ram, int vfs, int vit, uint destMask)
    {
        ushort idx = vit == 0 ? (ushort)0 : ctx->VI[vit];
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        if (ctx->Vu0DataMem != null)
        {
            var old = Unsafe.ReadUnaligned<Vector128<float>>(ctx->Vu0DataMem + addr);
            Unsafe.WriteUnaligned(ctx->Vu0DataMem + addr, BlendMask(old, ctx->VF[vfs], destMask));
        }
        else
        {
            var old = Ps2Memory.Read128(ctx, ram, 0x11004000u + addr).AsSingle();
            Ps2Memory.Write128(ctx, ram, 0x11004000u + addr, BlendMask(old, ctx->VF[vfs], destMask).AsUInt32());
        }
        SetVi(ctx, vit, (ushort)((idx + 1) & 0x3FF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vlqd(CpuContext* ctx, byte* ram, int vft, int vis, uint destMask)
    {
        ushort idx = (ushort)(((vis == 0 ? 0 : ctx->VI[vis]) - 1) & 0x3FF);
        SetVi(ctx, vis, idx);
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        Vector128<float> mem = ctx->Vu0DataMem != null
            ? Unsafe.ReadUnaligned<Vector128<float>>(ctx->Vu0DataMem + addr)
            : Ps2Memory.Read128(ctx, ram, 0x11004000u + addr).AsSingle();
        SetVf(ctx, vft, mem, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vsqd(CpuContext* ctx, byte* ram, int vfs, int vit, uint destMask)
    {
        ushort idx = (ushort)(((vit == 0 ? 0 : ctx->VI[vit]) - 1) & 0x3FF);
        SetVi(ctx, vit, idx);
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        if (ctx->Vu0DataMem != null)
        {
            var old = Unsafe.ReadUnaligned<Vector128<float>>(ctx->Vu0DataMem + addr);
            Unsafe.WriteUnaligned(ctx->Vu0DataMem + addr, BlendMask(old, ctx->VF[vfs], destMask));
        }
        else
        {
            var old = Ps2Memory.Read128(ctx, ram, 0x11004000u + addr).AsSingle();
            Ps2Memory.Write128(ctx, ram, 0x11004000u + addr, BlendMask(old, ctx->VF[vfs], destMask).AsUInt32());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vilwr(CpuContext* ctx, byte* ram, int vit, int vis, uint destMask)
    {
        ushort idx = vis == 0 ? (ushort)0 : ctx->VI[vis];
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        int lane = (destMask & 0x8u) != 0 ? 0 : (destMask & 0x4u) != 0 ? 1 : (destMask & 0x2u) != 0 ? 2 : 3;
        uint val = ctx->Vu0DataMem != null
            ? Unsafe.ReadUnaligned<uint>(ctx->Vu0DataMem + addr + lane * 4)
            : Ps2Memory.Read32(ctx, ram, 0x11004000u + addr + (uint)(lane * 4));
        SetVi(ctx, vit, (ushort)val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Viswr(CpuContext* ctx, byte* ram, int vit, int vis, uint destMask)
    {
        ushort idx = vis == 0 ? (ushort)0 : ctx->VI[vis];
        uint addr = ((uint)(idx & 0xFFu)) << 4;
        uint val = vit == 0 ? 0u : ctx->VI[vit];
        for (int lane = 0; lane < 4; lane++)
        {
            if ((destMask & (1u << (3 - lane))) != 0)
            {
                if (ctx->Vu0DataMem != null)
                    Unsafe.WriteUnaligned(ctx->Vu0DataMem + addr + lane * 4, val);
                else
                    Ps2Memory.Write32(ctx, ram, 0x11004000u + addr + (uint)(lane * 4), val);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vrinit(CpuContext* ctx, int vfs, uint fsf)
    {
        uint bits = ctx->VF[vfs].AsUInt32().GetElement((int)(fsf & 3u));
        uint r = 0x3F800000u | (bits & 0x007FFFFFu);
        ctx->Vu0R = Vector128.Create(r).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vrget(CpuContext* ctx, int vft, uint destMask)
    {
        SetVf(ctx, vft, ctx->Vu0R, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vrnext(CpuContext* ctx, int vft, uint destMask)
    {
        uint r = ctx->Vu0R.AsUInt32().GetElement(0);
        uint x = (r >> 4) & 1u;
        uint y = (r >> 22) & 1u;
        uint next = 0x3F800000u | (((r << 1) ^ (x ^ y)) & 0x007FFFFFu);
        ctx->Vu0R = Vector128.Create(next).AsSingle();
        SetVf(ctx, vft, ctx->Vu0R, destMask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Vrxor(CpuContext* ctx, int vfs, uint fsf)
    {
        uint bits = ctx->VF[vfs].AsUInt32().GetElement((int)(fsf & 3u)) & 0x007FFFFFu;
        uint r = ctx->Vu0R.AsUInt32().GetElement(0);
        ctx->Vu0R = Vector128.Create(0x3F800000u | ((r ^ bits) & 0x007FFFFFu)).AsSingle();
    }
}
