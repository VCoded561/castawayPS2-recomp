using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Castaway.GsRenderer.Vu1;

/// <summary>
/// Bit-exact AVX2 2-vertex-wide fast loops invoked by CRC-verified AOT VU1 microprograms
/// (Base1 slot_434 lighting, Ovl4 slot_728 static transform, Ovl4 slot_1369 skinned transform).
/// Each helper processes pairs = (count - 1) >> 1 vertices (leaving 1-2 trailing vertices for the
/// exact scalar epilogue so all final VF/VI/ACC/Q/P/MAC registers remain 100% bit-exact).
/// </summary>
internal static class Vu1AotFastLoops
{
    private static readonly Vector256<float> Vf0Mr32_256 = Vector256.Create(0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f);
    private static readonly Vector256<float> Itof12Scale256 = Vector256.Create(1.0f / 4096.0f);
    private static readonly Vector256<float> Ftoi4Scale256 = Vector256.Create(16.0f);
    private static readonly Vector256<int> IntMin256 = Vector256.Create(int.MinValue);
    private static readonly Vector256<uint> SignMask256 = Vector256.Create(0x80000000u);
    private static readonly Vector256<uint> ExpMask256 = Vector256.Create(0x7F800000u);
    private static readonly Vector256<uint> SanMaskE256 = Vector256.Create(
        0x7F800000u, 0x7F800000u, 0x7F800000u, 0u,
        0x7F800000u, 0x7F800000u, 0x7F800000u, 0u);
    private static readonly Vector256<uint> SafeHuge256 = Vector256.Create(0x7F7FFFFFu);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ExecFtoi0_256(Vector256<float> fs)
    {
        Vector256<int> ints = Avx.ConvertToVector256Int32WithTruncation(fs);
        Vector256<int> isIndef = Vector256.Equals(ints, IntMin256);
        Vector256<int> posOverflow = Vector256.AndNot(isIndef, Avx2.ShiftRightArithmetic(fs.AsInt32(), 31));
        return (ints + posOverflow).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> ExecFtoi4_256(Vector256<float> fs)
    {
        Vector256<float> scaled = fs * Ftoi4Scale256;
        Vector256<int> ints = Avx.ConvertToVector256Int32WithTruncation(scaled);
        Vector256<int> isIndef = Vector256.Equals(ints, IntMin256);
        Vector256<int> posOverflow = Vector256.AndNot(isIndef, Avx2.ShiftRightArithmetic(scaled.AsInt32(), 31));
        return (ints + posOverflow).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> SanitizeFloatMaskE_256(Vector256<float> v)
    {
        Vector256<uint> u = v.AsUInt32();
        Vector256<uint> isExp255 = Vector256.Equals(u & SanMaskE256, ExpMask256);
        return Vector256.ConditionalSelect(isExp255, (u & SignMask256) | SafeHuge256, u).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Recip256Broadcast(Vector256<float> proj256)
    {
        float w0 = proj256.GetElement(3);
        float w1 = proj256.GetElement(7);
        uint ub0 = BitConverter.SingleToUInt32Bits(w0);
        uint ub1 = BitConverter.SingleToUInt32Bits(w1);
        if (((ub0 & 0x7F800000u) - 0x00800000u) < 0x7F000000u &&
            ((ub1 & 0x7F800000u) - 0x00800000u) < 0x7F000000u)
        {
            Vector256<float> q = Avx.Divide(Vector256<float>.One, Avx.Permute(proj256, 0xFF));
            uint ur0 = q.AsUInt32().GetElement(0);
            uint ur1 = q.AsUInt32().GetElement(4);
            if (((ur0 & 0x7F800000u) - 0x00800000u) < 0x7F000000u &&
                ((ur1 & 0x7F800000u) - 0x00800000u) < 0x7F000000u)
                return q;
        }
        return Vector256.Create(
            Vector128.Create(Vu1State.VuDivFast(1.0f, w0)),
            Vector128.Create(Vu1State.VuDivFast(1.0f, w1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Rsqrt256Broadcast(Vector256<float> dot256)
    {
        float d0 = dot256.GetElement(0);
        float d1 = dot256.GetElement(4);
        uint ub0 = BitConverter.SingleToUInt32Bits(d0);
        uint ub1 = BitConverter.SingleToUInt32Bits(d1);
        if (((ub0 & 0x7F800000u) - 0x00800000u) < 0x7F000000u &&
            ((ub1 & 0x7F800000u) - 0x00800000u) < 0x7F000000u)
        {
            Vector256<float> absDot = Avx.AndNot(SignMask256.AsSingle(), dot256);
            Vector256<float> p = Avx.Divide(Vector256<float>.One, Avx.Sqrt(absDot));
            uint ur0 = p.AsUInt32().GetElement(0);
            uint ur1 = p.AsUInt32().GetElement(4);
            if (((ur0 & 0x7F800000u) - 0x00800000u) < 0x7F000000u &&
                ((ur1 & 0x7F800000u) - 0x00800000u) < 0x7F000000u)
                return p;
        }
        return Vector256.Create(
            Vector128.Create(Vu1State.VuRsqrtFast(1.0f, d0)),
            Vector128.Create(Vu1State.VuRsqrtFast(1.0f, d1)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort MacWFromBits(uint bits)
        => (bits & 0x7F800000u) == 0u ? (ushort)0x01 : (ushort)((bits >> 27) & 0x10u);

    /// <summary>
    /// Base1 slot_434: 3-directional + ambient lighting loop (18 bundles/vertex).
    /// Runs pairs = (count - 1) >> 1 pairs in AVX2, leaving 1-2 vertices for the exact scalar epilogue.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void RunLightingLoop_434(
        ref byte dm,
        Vector128<float> vf1, Vector128<float> vf2, Vector128<float> vf3, Vector128<float> vf4,
        Vector128<float> vf5, Vector128<float> vf6, Vector128<float> vf7, Vector128<float> vf8,
        Vector128<float> vf9,
        ref ushort vi10, ref ushort vi11, ref ushort vi12, ushort vi13, ushort vi14,
        ref int steps)
    {
        if (!Avx2.IsSupported)
            return;

        int count = (short)vi12;
        int pairs = (count - 1) >> 1;
        if (pairs <= 0)
            return;

        int verts = pairs << 1;
        if (steps + verts * 18 >= 65536)
            return;

        int stepIn = (short)vi14;
        int stepOut = (short)vi13;
        if (stepIn <= 0 || stepOut <= 0)
            return;

        int inStart = vi10 & 0x3FF;
        int outStart = vi11 & 0x3FF;
        int inEnd = inStart + (verts - 1) * stepIn;
        int outEnd = outStart + (verts - 1) * stepOut;
        if (inEnd >= 1024 || outEnd >= 1024)
            return;
        // Ensure input and output ranges do not overlap
        if (inStart <= outEnd && outStart <= inEnd)
            return;

        Vector256<float> m1 = Vector256.Create(vf1, vf1);
        Vector256<float> m2 = Vector256.Create(vf2, vf2);
        Vector256<float> m3 = Vector256.Create(vf3, vf3);
        Vector256<float> m4 = Vector256.Create(vf4, vf4);
        Vector256<float> m5 = Vector256.Create(vf5, vf5);
        Vector256<float> m6 = Vector256.Create(vf6, vf6);
        Vector256<float> m7 = Vector256.Create(vf7, vf7);
        Vector256<float> m8 = Vector256.Create(vf8, vf8);
        Vector256<float> m9 = Vector256.Create(vf9, vf9);
        Vector256<float> zero = Vector256<float>.Zero;
        Vector256<float> one = Vector256<float>.One;

        int strideInBytes = stepIn << 4;
        int strideOutBytes = stepOut << 4;
        int strideIn2 = strideInBytes << 1;
        int strideOut2 = strideOutBytes << 1;

        ref byte pIn = ref Unsafe.Add(ref dm, inStart << 4);
        ref byte pOut = ref Unsafe.Add(ref dm, outStart << 4);

        for (int i = 0; i < pairs; i++)
        {
            Vector128<float> n0 = Unsafe.As<byte, Vector128<float>>(ref pIn);
            Vector128<float> n1 = Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pIn, strideInBytes));
            Vector256<float> v10 = Vector256.Create(n0, n1);

            Vector256<float> acc = m2 * Avx.Permute(v10, 0x00);
            acc = acc + (m3 * Avx.Permute(v10, 0x55));
            Vector256<float> v11 = acc + (m4 * Avx.Permute(v10, 0xAA));
            v11 = Avx.Min(Avx.Max(v11, zero), one);

            Vector256<float> v10F = Avx.ConvertToVector256Single(v10.AsInt32());
            Vector256<float> alphaScale = Avx.Permute(v10F, 0xFF);

            acc = m1 + (m5 * Avx.Permute(v11, 0x00));
            acc = acc + (m6 * Avx.Permute(v11, 0x55));
            acc = acc + (m7 * Avx.Permute(v11, 0xAA));
            v11 = acc + (m8 * Avx.Permute(v11, 0xFF));

            v11 = Avx.Blend(v11, v11 * alphaScale, 0x77);
            v11 = Avx.Min(v11, m9);
            v11 = ExecFtoi0_256(v11);

            Unsafe.As<byte, Vector128<float>>(ref pOut) = v11.GetLower();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pOut, strideOutBytes)) = v11.GetUpper();

            pIn = ref Unsafe.Add(ref pIn, strideIn2);
            pOut = ref Unsafe.Add(ref pOut, strideOut2);
        }

        vi10 = unchecked((ushort)(vi10 + verts * stepIn));
        vi11 = unchecked((ushort)(vi11 + verts * stepOut));
        vi12 = unchecked((ushort)(vi12 - verts));
        steps += verts * 18;
    }

    /// <summary>
    /// Ovl4 slot_728: Static mesh vertex transform + projection + UV divide (34 bundles/vertex).
    /// Runs pairs = (count - 1) >> 1 pairs in AVX2, leaving 1-2 vertices for the exact scalar epilogue.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void RunStaticTransformLoop_728(
        ref byte dm,
        Vector128<float> vf1, Vector128<float> vf2, Vector128<float> vf3, Vector128<float> vf4,
        Vector128<float> vf5, Vector128<float> vf6, Vector128<float> vf7, Vector128<float> vf8,
        Vector128<float> vf9, Vector128<float> vf10, Vector128<float> vf11, Vector128<float> vf12,
        Vector128<float> vf13, Vector128<float> vf14,
        ref ushort vi1, ref ushort vi3, ref ushort vi4, ref ushort vi5, ref ushort vi6,
        ref int steps)
    {
        if (!Avx2.IsSupported)
            return;

        int count = (short)vi1;
        int pairs = (count - 1) >> 1;
        if (pairs <= 0)
            return;

        int verts = pairs << 1;
        if (steps + verts * 34 >= 65536)
            return;

        int inStart = vi3 & 0x3FF;
        int outStart = vi4 & 0x3FF;
        int inEnd = inStart + verts * 5 - 1;
        int outFirst = outStart + 3;
        int outEnd = outStart + verts * 3 + 2;
        // Also writes dm[outStart + 4 + k*3] which is inside [outFirst .. outEnd]
        if (inEnd >= 1024 || outEnd >= 1024)
            return;
        if (inStart <= outEnd && outFirst <= inEnd)
            return;

        Vector256<float> m1 = Vector256.Create(vf1, vf1);
        Vector256<float> m2 = Vector256.Create(vf2, vf2);
        Vector256<float> m3 = Vector256.Create(vf3, vf3);
        Vector256<float> m4 = Vector256.Create(vf4, vf4);
        Vector256<float> m5 = Vector256.Create(vf5, vf5);
        Vector256<float> m6 = Vector256.Create(vf6, vf6);
        Vector256<float> m7 = Vector256.Create(vf7, vf7);
        Vector256<float> m8 = Vector256.Create(vf8, vf8);
        Vector256<float> m9 = Vector256.Create(vf9, vf9);
        Vector256<float> m10 = Vector256.Create(vf10, vf10);
        Vector256<float> m11 = Vector256.Create(vf11, vf11);
        Vector256<float> m13 = Vector256.Create(vf13, vf13);
        Vector256<float> m14 = Vector256.Create(vf14, vf14);
        Vector256<float> one = Vector256<float>.One;

        ref byte pIn = ref Unsafe.Add(ref dm, inStart << 4);
        ref byte pOut = ref Unsafe.Add(ref dm, outFirst << 4); // vi4_after for v0

        ushort curVi5 = vi5;
        ushort curVi6 = vi6;

        for (int i = 0; i < pairs; i++)
        {
            ref byte pIn1 = ref Unsafe.Add(ref pIn, 80);
            ref byte pOut1 = ref Unsafe.Add(ref pOut, 48);

            ushort flag0 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref pIn, 12));
            ushort flag1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref pIn1, 12));

            Vector256<float> pos = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref pIn),
                Unsafe.As<byte, Vector128<int>>(ref pIn1)));
            Vector256<float> norm = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn, 16)),
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn1, 16))));
            Vector256<float> st = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn, 32)),
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn1, 32)))) * Itof12Scale256;

            // World pos = vf5*x + vf6*y + vf7*z + vf8
            Vector256<float> wPos = m5 * Avx.Permute(pos, 0x00);
            wPos = wPos + (m6 * Avx.Permute(pos, 0x55));
            wPos = wPos + (m7 * Avx.Permute(pos, 0xAA));
            wPos = wPos + m8;

            // World norm = vf9*nx + vf10*ny + vf11*nz
            Vector256<float> wNorm = m9 * Avx.Permute(norm, 0x00);
            wNorm = wNorm + (m10 * Avx.Permute(norm, 0x55));
            wNorm = wNorm + (m11 * Avx.Permute(norm, 0xAA));

            // Store worldPos.xyz, worldNorm.xyz, st_itof12
            Vector128<float> wp0 = wPos.GetLower();
            Vector128<float> wp1 = wPos.GetUpper();
            Unsafe.WriteUnaligned(ref pIn, wp0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 8), wp0.GetElement(2));
            Unsafe.WriteUnaligned(ref pIn1, wp1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 8), wp1.GetElement(2));

            Vector128<float> wn0 = wNorm.GetLower();
            Vector128<float> wn1 = wNorm.GetUpper();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 16), wn0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 24), wn0.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 16), wn1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 24), wn1.GetElement(2));

            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pIn, 32)) = st.GetLower();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pIn1, 32)) = st.GetUpper();

            // Projection = vf1*wpx + vf2*wpy + vf3*wpz + vf4
            Vector256<float> proj = m1 * Avx.Permute(wPos, 0x00);
            proj = proj + (m2 * Avx.Permute(wPos, 0x55));
            proj = proj + (m3 * Avx.Permute(wPos, 0xAA));
            proj = proj + m4;

            Vector256<float> q256 = Recip256Broadcast(proj);
            Vector256<float> projW1 = Avx.Blend(proj, one, 0x88);
            Vector256<float> ndc = projW1 * q256;

            ushort mac0 = MacWFromBits(ndc.AsUInt32().GetElement(3));
            ushort mac1 = MacWFromBits(ndc.AsUInt32().GetElement(7));

            Vector256<float> xyz = ExecFtoi4_256(Avx.Min(Avx.Max(ndc, m13), m14));
            Vector256<float> stq = Avx.Blend(st, Vf0Mr32_256, 0xCC) * q256;

            // ADC chain vertex 0
            curVi5 = (ushort)(curVi5 | flag0);
            ushort prevVi6_0 = curVi6;
            curVi6 = unchecked((ushort)((mac0 & 17u) + 32767u));
            ushort vi8_0 = (ushort)(curVi5 | curVi6);
            curVi5 = (ushort)(prevVi6_0 | curVi6);

            // ADC chain vertex 1
            curVi5 = (ushort)(curVi5 | flag1);
            ushort prevVi6_1 = curVi6;
            curVi6 = unchecked((ushort)((mac1 & 17u) + 32767u));
            ushort vi8_1 = (ushort)(curVi5 | curVi6);
            curVi5 = (ushort)(prevVi6_1 | curVi6);

            // Write outputs (STQ at +0, vf12 at +16, XYZF2 at +32, ADC at +44)
            Unsafe.As<byte, Vector128<float>>(ref pOut) = stq.GetLower();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pOut, 16)) = vf12;
            Vector128<float> xyz0 = xyz.GetLower();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 32), xyz0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 40), xyz0.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 44), (uint)vi8_0);

            Unsafe.As<byte, Vector128<float>>(ref pOut1) = stq.GetUpper();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pOut1, 16)) = vf12;
            Vector128<float> xyz1 = xyz.GetUpper();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 32), xyz1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 40), xyz1.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 44), (uint)vi8_1);

            pIn = ref Unsafe.Add(ref pIn, 160);
            pOut = ref Unsafe.Add(ref pOut, 96);
        }

        vi1 = unchecked((ushort)(vi1 - verts));
        vi3 = unchecked((ushort)(vi3 + verts * 5));
        vi4 = unchecked((ushort)(vi4 + verts * 3));
        vi5 = curVi5;
        vi6 = curVi6;
        steps += verts * 34;
    }

    /// <summary>
    /// Ovl4 slot_1369: 4-bone skinned vertex transform + normal normalization + projection (56 bundles/vertex).
    /// Runs pairs = (count - 1) >> 1 pairs in AVX2, leaving 1-2 vertices for the exact scalar epilogue.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void RunSkinnedTransformLoop_1369(
        ref byte dm,
        in Vector128<float> vf1, in Vector128<float> vf2, in Vector128<float> vf3, in Vector128<float> vf4,
        in Vector128<float> vf5, in Vector128<float> vf6, in Vector128<float> vf7, in Vector128<float> vf8,
        in Vector128<float> vf9, in Vector128<float> vf10, in Vector128<float> vf11, in Vector128<float> vf12,
        in Vector128<float> vf13, in Vector128<float> vf14, in Vector128<float> vf15, in Vector128<float> vf16,
        float weightScale,
        in Vector128<float> vf18, in Vector128<float> vf19, in Vector128<float> vf20, in Vector128<float> vf21,
        in Vector128<float> vf22, in Vector128<float> vf23, in Vector128<float> vf24,
        ushort vi5,
        ref ushort vi1, ref ushort vi3, ref ushort vi4, ref ushort vi6, ref ushort vi7,
        ref int steps)
    {
        if (!Avx2.IsSupported)
            return;

        int count = (short)vi1;
        int pairs = (count - 1) >> 1;
        if (pairs <= 0)
            return;

        int verts = pairs << 1;
        if (steps + verts * 56 >= 65536)
            return;

        int inStart = vi4 & 0x3FF;
        int outStart = vi3 & 0x3FF;
        int inEnd = inStart + verts * 5 - 1;
        int outFirst = outStart + 3;
        int outEnd = outStart + verts * 3 + 2;
        // Normal matrices are read at dm[(flag & vi5) + 6 .. 8]; require inStart/outFirst above normal matrix region
        int maxNormQ = (vi5 & 0x3FF) + 8;
        if (inEnd >= 1024 || outEnd >= 1024 || maxNormQ >= 1024)
            return;
        if (inStart <= outEnd && outFirst <= inEnd)
            return;
        if (inStart <= maxNormQ || outFirst <= maxNormQ)
            return;

        Vector256<float> b00 = Vector256.Create(vf1, vf1);
        Vector256<float> b01 = Vector256.Create(vf2, vf2);
        Vector256<float> b02 = Vector256.Create(vf3, vf3);
        Vector256<float> b03 = Vector256.Create(vf4, vf4);
        Vector256<float> b10 = Vector256.Create(vf5, vf5);
        Vector256<float> b11 = Vector256.Create(vf6, vf6);
        Vector256<float> b12 = Vector256.Create(vf7, vf7);
        Vector256<float> b13 = Vector256.Create(vf8, vf8);
        Vector256<float> b20 = Vector256.Create(vf9, vf9);
        Vector256<float> b21 = Vector256.Create(vf10, vf10);
        Vector256<float> b22 = Vector256.Create(vf11, vf11);
        Vector256<float> b23 = Vector256.Create(vf12, vf12);
        Vector256<float> b30 = Vector256.Create(vf13, vf13);
        Vector256<float> b31 = Vector256.Create(vf14, vf14);
        Vector256<float> b32 = Vector256.Create(vf15, vf15);
        Vector256<float> b33 = Vector256.Create(vf16, vf16);

        Vector256<float> p0 = Vector256.Create(vf18, vf18);
        Vector256<float> p1 = Vector256.Create(vf19, vf19);
        Vector256<float> p2 = Vector256.Create(vf20, vf20);
        Vector256<float> p3 = Vector256.Create(vf21, vf21);
        Vector256<float> minC = Vector256.Create(vf22, vf22);
        Vector256<float> maxC = Vector256.Create(vf23, vf23);
        Vector256<float> wScale256 = Vector256.Create(weightScale);
        Vector256<float> one = Vector256<float>.One;
        Vector128<float> colConst = vf24;

        ref byte pIn = ref Unsafe.Add(ref dm, inStart << 4);
        ref byte pOut = ref Unsafe.Add(ref dm, outFirst << 4);

        ushort curVi6 = vi6;
        ushort curVi7 = vi7;

        for (int i = 0; i < pairs; i++)
        {
            ref byte pIn1 = ref Unsafe.Add(ref pIn, 80);
            ref byte pOut1 = ref Unsafe.Add(ref pOut, 48);

            ushort flag0 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref pIn, 12));
            ushort flag1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref pIn1, 12));

            Vector256<float> pos = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref pIn),
                Unsafe.As<byte, Vector128<int>>(ref pIn1)));
            Vector256<float> weights = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn, 64)),
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn1, 64)))) * wScale256;
            Vector256<float> norm = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn, 16)),
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn1, 16))));

            Vector256<float> px = Avx.Permute(pos, 0x00);
            Vector256<float> py = Avx.Permute(pos, 0x55);
            Vector256<float> pz = Avx.Permute(pos, 0xAA);

            // 4-bone skinned world position
            Vector256<float> t0 = ((b00 * px + b01 * py) + b02 * pz) + b03;
            Vector256<float> t1 = ((b10 * px + b11 * py) + b12 * pz) + b13;
            Vector256<float> t2 = ((b20 * px + b21 * py) + b22 * pz) + b23;
            Vector256<float> t3 = ((b30 * px + b31 * py) + b32 * pz) + b33;

            Vector256<float> wPos = t0 * Avx.Permute(weights, 0x00);
            wPos = wPos + (t1 * Avx.Permute(weights, 0x55));
            wPos = wPos + (t2 * Avx.Permute(weights, 0xAA));
            wPos = wPos + (t3 * Avx.Permute(weights, 0xFF));

            // Load per-vertex normal matrix from dm[(flag & vi5) + 6..8]
            int nBase0 = ((flag0 & vi5) + 6) << 4;
            int nBase1 = ((flag1 & vi5) + 6) << 4;
            ref byte nm0 = ref Unsafe.Add(ref dm, nBase0);
            ref byte nm1 = ref Unsafe.Add(ref dm, nBase1);
            Vector256<float> nmR0 = Vector256.Create(
                Unsafe.As<byte, Vector128<float>>(ref nm0),
                Unsafe.As<byte, Vector128<float>>(ref nm1));
            Vector256<float> nmR1 = Vector256.Create(
                Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref nm0, 16)),
                Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref nm1, 16)));
            Vector256<float> nmR2 = SanitizeFloatMaskE_256(Vector256.Create(
                Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref nm0, 32)),
                Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref nm1, 32))));

            Vector256<float> wNorm = nmR0 * Avx.Permute(norm, 0x00);
            wNorm = wNorm + (nmR1 * Avx.Permute(norm, 0x55));
            wNorm = wNorm + (nmR2 * Avx.Permute(norm, 0xAA));

            // Normalize wNorm with parallel 2-wide rsqrt(x*x + y*y + z*z)
            Vector256<float> nSq = wNorm * wNorm;
            Vector256<float> dot256 = (Avx.Permute(nSq, 0x00) + Avx.Permute(nSq, 0x55)) + Avx.Permute(nSq, 0xAA);
            Vector256<float> invLen = Rsqrt256Broadcast(dot256);
            wNorm = wNorm * invLen;

            // Load & convert ST
            Vector256<float> st = Avx.ConvertToVector256Single(Vector256.Create(
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn, 32)),
                Unsafe.As<byte, Vector128<int>>(ref Unsafe.Add(ref pIn1, 32)))) * Itof12Scale256;

            // Store worldPos.xyz, worldNorm.xyz, st_itof12 back to input buffer
            Vector128<float> wp0 = wPos.GetLower();
            Vector128<float> wp1 = wPos.GetUpper();
            Unsafe.WriteUnaligned(ref pIn, wp0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 8), wp0.GetElement(2));
            Unsafe.WriteUnaligned(ref pIn1, wp1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 8), wp1.GetElement(2));

            Vector128<float> wn0 = wNorm.GetLower();
            Vector128<float> wn1 = wNorm.GetUpper();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 16), wn0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn, 24), wn0.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 16), wn1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pIn1, 24), wn1.GetElement(2));

            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pIn, 32)) = st.GetLower();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pIn1, 32)) = st.GetUpper();

            // Projection
            Vector256<float> proj = p0 * Avx.Permute(wPos, 0x00);
            proj = proj + (p1 * Avx.Permute(wPos, 0x55));
            proj = proj + (p2 * Avx.Permute(wPos, 0xAA));
            proj = proj + p3;

            Vector256<float> q256 = Recip256Broadcast(proj);
            Vector256<float> projW1 = Avx.Blend(proj, one, 0x88);
            Vector256<float> ndc = projW1 * q256;

            ushort mac0 = MacWFromBits(ndc.AsUInt32().GetElement(3));
            ushort mac1 = MacWFromBits(ndc.AsUInt32().GetElement(7));

            Vector256<float> xyz = ExecFtoi4_256(Avx.Min(Avx.Max(ndc, minC), maxC));
            Vector256<float> stq = Avx.Blend(st, Vf0Mr32_256, 0xCC) * q256;

            // ADC chain vertex 0 (vi6/vi7/vi9)
            curVi6 = (ushort)(curVi6 | flag0);
            ushort prevVi7_0 = curVi7;
            curVi7 = unchecked((ushort)((mac0 & 17u) + 32767u));
            ushort vi9_0 = (ushort)(curVi6 | curVi7);
            curVi6 = (ushort)(prevVi7_0 | curVi7);

            // ADC chain vertex 1
            curVi6 = (ushort)(curVi6 | flag1);
            ushort prevVi7_1 = curVi7;
            curVi7 = unchecked((ushort)((mac1 & 17u) + 32767u));
            ushort vi9_1 = (ushort)(curVi6 | curVi7);
            curVi6 = (ushort)(prevVi7_1 | curVi7);

            // Write outputs (STQ at +0, vf24 at +16, XYZF2 at +32, ADC at +44)
            Unsafe.As<byte, Vector128<float>>(ref pOut) = stq.GetLower();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pOut, 16)) = colConst;
            Vector128<float> xyz0 = xyz.GetLower();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 32), xyz0.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 40), xyz0.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut, 44), (uint)vi9_0);

            Unsafe.As<byte, Vector128<float>>(ref pOut1) = stq.GetUpper();
            Unsafe.As<byte, Vector128<float>>(ref Unsafe.Add(ref pOut1, 16)) = colConst;
            Vector128<float> xyz1 = xyz.GetUpper();
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 32), xyz1.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 40), xyz1.GetElement(2));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref pOut1, 44), (uint)vi9_1);

            pIn = ref Unsafe.Add(ref pIn, 160);
            pOut = ref Unsafe.Add(ref pOut, 96);
        }

        vi1 = unchecked((ushort)(vi1 - verts));
        vi4 = unchecked((ushort)(vi4 + verts * 5));
        vi3 = unchecked((ushort)(vi3 + verts * 3));
        vi6 = curVi6;
        vi7 = curVi7;
        steps += verts * 56;
    }
}
