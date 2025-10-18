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
    }
}
