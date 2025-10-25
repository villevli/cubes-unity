using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Cubes
{
    public static class Math
    {
        // Floor and ceil divide using integers to avoid floating point errors

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DivFloor(int a, int b) => (a < 0) ? -1 + (a + 1) / b : (a / b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DivCeil(int a, int b) => (a > 0) ? 1 + (a - 1) / b : (a / b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 DivFloor(int2 a, int2 b) => new(DivFloor(a.x, b.x), DivFloor(a.y, b.y));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int2 DivCeil(int2 a, int2 b) => new(DivCeil(a.x, b.x), DivCeil(a.y, b.y));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 DivFloor(int3 a, int3 b) => new(DivFloor(a.x, b.x), DivFloor(a.y, b.y), DivFloor(a.z, b.z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 DivCeil(int3 a, int3 b) => new(DivCeil(a.x, b.x), DivCeil(a.y, b.y), DivCeil(a.z, b.z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int4 DivFloor(int4 a, int4 b) => new(DivFloor(a.x, b.x), DivFloor(a.y, b.y), DivFloor(a.z, b.z), DivFloor(a.w, b.w));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int4 DivCeil(int4 a, int4 b) => new(DivCeil(a.x, b.x), DivCeil(a.y, b.y), DivCeil(a.z, b.z), DivCeil(a.w, b.w));

        /// <summary>Next float that is greater than <paramref name="x"/></summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float incrfloat(float x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(-1, 1, x > 0)),
                   float.Epsilon, x == 0);
        }
        /// <inheritdoc cref="incrfloat"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 incrfloat(float2 x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(-1, 1, x > 0)),
                   float.Epsilon, x == 0);
        }
        /// <inheritdoc cref="incrfloat"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 incrfloat(float3 x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(-1, 1, x > 0)),
                   float.Epsilon, x == 0);
        }

        /// <summary>Next float that is less than <paramref name="x"/></summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float decrfloat(float x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(1, -1, x > 0)),
                  -float.Epsilon, x == 0);
        }
        /// <inheritdoc cref="decrfloat"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 decrfloat(float2 x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(1, -1, x > 0)),
                  -float.Epsilon, x == 0);
        }
        /// <inheritdoc cref="decrfloat"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 decrfloat(float3 x)
        {
            return math.select(
                   math.asfloat(math.asint(x) + math.select(1, -1, x > 0)),
                  -float.Epsilon, x == 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float copysign(float mag, float sign) => math.abs(mag) * math.sign(sign);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 copysign(float2 mag, float2 sign) => math.abs(mag) * math.sign(sign);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 copysign(float3 mag, float3 sign) => math.abs(mag) * math.sign(sign);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4 copysign(float4 mag, float4 sign) => math.abs(mag) * math.sign(sign);
    }
}
