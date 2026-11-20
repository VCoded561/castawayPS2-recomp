using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Castaway.Core;

/// <summary>
/// PS2 Emotion Engine COP1 (FPU) &amp; VU0 Macro floating-point semantics.
/// Enforces PS2 non-IEEE rules:
/// - No NaN or Infinity (0x7F800000..0x7FFFFFFF clamps/saturates to max float 0x7FFFFFFF / +-float.MaxValue).
/// - Denormals (exponent 0) flush to +-0.0f.
/// - Round-toward-zero (MathF.Truncate) on float-to-int conversions (CVT.W.S / VFTOI*).
/// </summary>
public readonly struct Ps2Float
{
    public const uint MaxBits = 0x7FFFFFFFu;
    public const uint IeeeMaxNormalBits = 0x7F7FFFFFu;
    public const uint ExpMask = 0x7F800000u;
    public const uint SignMask = 0x80000000u;
    public const uint MagMask = 0x7FFFFFFFu;

    public readonly uint Bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Ps2Float(uint bits) => Bits = ClampBits(bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Ps2Float(float value) => Bits = ClampBits(BitConverter.SingleToUInt32Bits(value));

    public float Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.UInt32BitsToSingle(Bits);
    }

    /// <summary>
    /// Clamps IEEE Infinity/NaN (0x7F800000..0x7FFFFFFF) to PS2 max float (0x7FFFFFFF)
    /// and flushes denormals (exponent == 0) to signed zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ClampBits(uint bits)
    {
        uint exp = bits & ExpMask;
        if (exp == 0u)
            return bits & SignMask;
        if (exp == ExpMask)
            return (bits & SignMask) | MaxBits;
        return bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x);
        uint exp = b & ExpMask;
        if (exp == 0u)
            return BitConverter.UInt32BitsToSingle(b & SignMask);
        if (exp == ExpMask)
            return BitConverter.UInt32BitsToSingle((b & SignMask) | MaxBits);
        return x;
    }

    /// <summary>
    /// Converts a PS2 float (including exponent 255 values) to double for exact intermediate arithmetic.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToDouble(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x);
        uint exp = b & ExpMask;
        if (exp == 0u)
            return (b & SignMask) != 0 ? -0.0 : 0.0;
        if (exp == ExpMask)
            return (double)BitConverter.UInt32BitsToSingle(b - 0x00800000u) * 2.0;
        return x;
    }

    /// <summary>
    /// Converts a double back to a PS2 float, saturating >= 2^129 to +-0x7FFFFFFF and flushing &lt; 2^-126 to +-0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FromDouble(double d)
    {
        if (double.IsNaN(d))
            return BitConverter.UInt32BitsToSingle(MaxBits);

        uint sign = double.IsNegative(d) ? SignMask : 0u;
        double mag = Math.Abs(d);

        // 2^129 = 6.8056473384187693e+38
        const double TwoPow129 = 680564733841876926926749214863536422912.0;
        // 2^128 = 3.4028236692093846e+38
        const double TwoPow128 = 340282366920938463463374607431768211456.0;
        // 2^-126 = 1.1754943508222875e-38
        const double TwoPowMinus126 = 1.175494350822287507968736537222245677818665556772087521508751706e-38;

        if (mag >= TwoPow129)
            return BitConverter.UInt32BitsToSingle(sign | MaxBits);
        if (mag < TwoPowMinus126)
            return BitConverter.UInt32BitsToSingle(sign);
        if (mag >= TwoPow128)
        {
            uint b = BitConverter.SingleToUInt32Bits((float)(d * 0.5));
            if ((b & ExpMask) == ExpMask)
                return BitConverter.UInt32BitsToSingle(sign | MaxBits);
            return BitConverter.UInt32BitsToSingle(b + 0x00800000u);
        }
        return (float)d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNormal(float r)
    {
        return (BitConverter.SingleToUInt32Bits(r) * 2u + 0x01000000u) >= 0x02000000u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSpecial(float x)
    {
        uint t = BitConverter.SingleToUInt32Bits(x) & MagMask;
        return t != 0u && (t - 0x00800000u) >= 0x7F000000u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllNormal(float a, float b, float r)
    {
        uint sa = BitConverter.SingleToUInt32Bits(a) * 2u + 0x01000000u;
        uint sb = BitConverter.SingleToUInt32Bits(b) * 2u + 0x01000000u;
        uint sr = BitConverter.SingleToUInt32Bits(r) * 2u + 0x01000000u;
        uint lowest = sa < sb ? sa : sb;
        if (sr < lowest) lowest = sr;
        return lowest >= 0x02000000u;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Add(float a, float b)
    {
        float r = a + b;
        return float.IsFinite(r) ? r : AddSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float AddSlow(float a, float b) => FromDouble(ToDouble(a) + ToDouble(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sub(float a, float b)
    {
        float r = a - b;
        return float.IsFinite(r) ? r : SubSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float SubSlow(float a, float b) => FromDouble(ToDouble(a) - ToDouble(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Mul(float a, float b)
    {
        float r = a * b;
        return float.IsFinite(r) ? r : MulSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float MulSlow(float a, float b) => FromDouble(ToDouble(a) * ToDouble(b));

    private const float MinNormalFloat = 1.17549435e-38f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Div(float a, float b)
    {
        uint ub = BitConverter.SingleToUInt32Bits(b) & MagMask;
        if (ub - 0x00800000u < 0x7F000000u)
        {
            uint ua = BitConverter.SingleToUInt32Bits(a) & MagMask;
            if (ua == 0u || ua - 0x00800000u < 0x7F000000u)
            {
                float r = a / b;
                uint ur = BitConverter.SingleToUInt32Bits(r) & MagMask;
                if (ur == 0u || ur - 0x00800000u < 0x7F000000u)
                    return r;
            }
        }
        return DivSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float DivSlow(float a, float b)
    {
        uint ua = BitConverter.SingleToUInt32Bits(a);
        uint ub = BitConverter.SingleToUInt32Bits(b);
        if ((ub & ExpMask) == 0u)
            return BitConverter.UInt32BitsToSingle(((ua ^ ub) & SignMask) | MaxBits);
        return FromDouble(ToDouble(a) / ToDouble(b));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float a)
    {
        uint ua = BitConverter.SingleToUInt32Bits(a);
        if (ua - 0x00800000u < 0x7F000000u)
            return MathF.Sqrt(a);
        return SqrtSlow(a);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float SqrtSlow(float a)
    {
        uint ua = BitConverter.SingleToUInt32Bits(a);
        if ((ua & ExpMask) == 0u)
            return 0.0f;
        if ((ua & ExpMask) == ExpMask)
            return FromDouble(Math.Sqrt(Math.Abs(ToDouble(a))));
        return MathF.Sqrt(MathF.Abs(a));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rsqrt(float a, float b)
    {
        uint ub = BitConverter.SingleToUInt32Bits(b);
        if (ub - 0x00800000u < 0x7F000000u)
        {
            uint ua = BitConverter.SingleToUInt32Bits(a) & MagMask;
            if (ua == 0u || ua - 0x00800000u < 0x7F000000u)
            {
                float r = a / MathF.Sqrt(b);
                uint ur = BitConverter.SingleToUInt32Bits(r) & MagMask;
                if (ur == 0u || ur - 0x00800000u < 0x7F000000u)
                    return r;
            }
        }
        return RsqrtSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float RsqrtSlow(float a, float b)
    {
        uint ub = BitConverter.SingleToUInt32Bits(b);
        if ((ub & ExpMask) == 0u)
        {
            uint ua = BitConverter.SingleToUInt32Bits(a);
            return BitConverter.UInt32BitsToSingle((ua & SignMask) | MaxBits);
        }
        float root = Sqrt(b);
        return Div(a, root);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Abs(float a)
    {
        uint b = BitConverter.SingleToUInt32Bits(a) & MagMask;
        if ((b & ExpMask) == 0u) return 0.0f;
        return BitConverter.UInt32BitsToSingle(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Neg(float a)
    {
        uint b = BitConverter.SingleToUInt32Bits(a);
        if ((b & ExpMask) == 0u) return BitConverter.UInt32BitsToSingle((b ^ SignMask) & SignMask);
        return BitConverter.UInt32BitsToSingle(b ^ SignMask);
    }

    /// <summary>
    /// Converts PS2 float bits to a signed comparable integer (sign-magnitude with denormal flush).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToOrderedKey(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x);
        if ((b & ExpMask) == 0u)
            return 0;
        int mag = (int)(b & MagMask);
        return (b & SignMask) != 0 ? -mag : mag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Max(float a, float b)
    {
        if (MathF.Abs(a) >= MinNormalFloat || MathF.Abs(b) >= MinNormalFloat)
        {
            if (a > b) return a;
            if (a < b) return b;
            if (a == b) return a;
        }
        else if (MathF.Abs(a) < MinNormalFloat && MathF.Abs(b) < MinNormalFloat)
        {
            return a;
        }
        return MaxSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float MaxSlow(float a, float b) => ToOrderedKey(a) >= ToOrderedKey(b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Min(float a, float b)
    {
        if (MathF.Abs(a) >= MinNormalFloat || MathF.Abs(b) >= MinNormalFloat)
        {
            if (a < b) return a;
            if (a > b) return b;
            if (a == b) return a;
        }
        else if (MathF.Abs(a) < MinNormalFloat && MathF.Abs(b) < MinNormalFloat)
        {
            return a;
        }
        return MinSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float MinSlow(float a, float b) => ToOrderedKey(a) <= ToOrderedKey(b) ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareEq(float a, float b)
    {
        if (MathF.Abs(a) >= MinNormalFloat || MathF.Abs(b) >= MinNormalFloat)
        {
            if (a == b) return true;
            if (a < b || a > b) return false;
        }
        else if (MathF.Abs(a) < MinNormalFloat && MathF.Abs(b) < MinNormalFloat)
        {
            return true;
        }
        return CompareEqSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CompareEqSlow(float a, float b) => ToOrderedKey(a) == ToOrderedKey(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareLt(float a, float b)
    {
        if (MathF.Abs(a) >= MinNormalFloat || MathF.Abs(b) >= MinNormalFloat)
        {
            if (a < b) return true;
            if (a >= b) return false;
        }
        else if (MathF.Abs(a) < MinNormalFloat && MathF.Abs(b) < MinNormalFloat)
        {
            return false;
        }
        return CompareLtSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CompareLtSlow(float a, float b) => ToOrderedKey(a) < ToOrderedKey(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareLe(float a, float b)
    {
        if (MathF.Abs(a) >= MinNormalFloat || MathF.Abs(b) >= MinNormalFloat)
        {
            if (a <= b) return true;
            if (a > b) return false;
        }
        else if (MathF.Abs(a) < MinNormalFloat && MathF.Abs(b) < MinNormalFloat)
        {
            return true;
        }
        return CompareLeSlow(a, b);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CompareLeSlow(float a, float b) => ToOrderedKey(a) <= ToOrderedKey(b);

    /// <summary>
    /// PS2 CVT.W.S / TRUNC.W.S: Always rounds toward zero (MathF.Truncate) and clamps to [int.MinValue, int.MaxValue].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CvtWS(float x)
    {
        if (x > -2147483648.0f && x < 2147483648.0f)
            return (int)x;
        return CvtWSSlow(x);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CvtWSSlow(float x)
    {
        uint b = BitConverter.SingleToUInt32Bits(x);
        uint exp = b & ExpMask;
        if (exp == 0u)
            return 0;
        if (exp == ExpMask)
            return (b & SignMask) != 0 ? int.MinValue : int.MaxValue;

        float t = MathF.Truncate(x);
        if (t >= 2147483648.0f)
            return int.MaxValue;
        if (t <= -2147483648.0f)
            return int.MinValue;
        return (int)t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CvtSW(int x) => (float)x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecAdd(Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> r = a + b;
        return VecFinish(a, b, r, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecSub(Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> r = a - b;
        return VecFinish(a, b, r, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecMul(Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> r = a * b;
        return VecFinish(a, b, r, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecMadd(Vector128<float> acc, Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> r = acc + (a * b);
        Vector128<uint> expMask = Vector128.Create(ExpMask);
        if (Vector128.EqualsAny(r.AsUInt32() & expMask, expMask))
            return VecMaddSlow(acc, a, b);
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> VecMsub(Vector128<float> acc, Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> r = acc - (a * b);
        Vector128<uint> expMask = Vector128.Create(ExpMask);
        if (Vector128.EqualsAny(r.AsUInt32() & expMask, expMask))
            return VecMsubSlow(acc, a, b);
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecAddSlow(Vector128<float> a, Vector128<float> b) => VecSlow(a, b, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecSubSlow(Vector128<float> a, Vector128<float> b) => VecSlow(a, b, 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecMulSlow(Vector128<float> a, Vector128<float> b) => VecSlow(a, b, 2);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecMaddSlow(Vector128<float> acc, Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> prod = VecSlow(a, b, 2);
        return VecSlow(acc, prod, 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector128<float> VecMsubSlow(Vector128<float> acc, Vector128<float> a, Vector128<float> b)
    {
        Vector128<float> prod = VecSlow(a, b, 2);
        return VecSlow(acc, prod, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> VecFinish(Vector128<float> a, Vector128<float> b, Vector128<float> r, int op)
    {
        Vector128<uint> expMask = Vector128.Create(ExpMask);
        Vector128<uint> rExp = r.AsUInt32() & expMask;
        if (Vector128.EqualsAny(rExp, expMask))
        {
            return VecSlow(a, b, op);
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector128<float> VecSlow(Vector128<float> a, Vector128<float> b, int op)
    {
        return op switch
        {
            0 => Vector128.Create(
                Add(a.GetElement(0), b.GetElement(0)),
                Add(a.GetElement(1), b.GetElement(1)),
                Add(a.GetElement(2), b.GetElement(2)),
                Add(a.GetElement(3), b.GetElement(3))),
            1 => Vector128.Create(
                Sub(a.GetElement(0), b.GetElement(0)),
                Sub(a.GetElement(1), b.GetElement(1)),
                Sub(a.GetElement(2), b.GetElement(2)),
                Sub(a.GetElement(3), b.GetElement(3))),
            _ => Vector128.Create(
                Mul(a.GetElement(0), b.GetElement(0)),
                Mul(a.GetElement(1), b.GetElement(1)),
                Mul(a.GetElement(2), b.GetElement(2)),
                Mul(a.GetElement(3), b.GetElement(3)))
        };
    }
}
