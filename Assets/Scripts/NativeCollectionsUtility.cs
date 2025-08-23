using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Cubes
{
    public static class NativeCollectionsUtility
    {
        // Bypasses the "Jobs can only create Temp memory" error

        /// <summary>
        /// Allocates a new NativeArray with less safety checks.
        /// Allows to allocate persistent memory inside jobs.
        /// Does not clear the memory. If needed, call <see cref="ClearMemory"/>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="length"></param>
        /// <param name="allocator"></param>
        /// <returns></returns>
        public unsafe static NativeArray<T> CreateUnsafeNativeArray<T>(int length, Allocator allocator) where T : unmanaged
        {
            long size = (long)UnsafeUtility.SizeOf<T>() * (long)length;
            void* buffer = UnsafeUtility.MallocTracked(size, UnsafeUtility.AlignOf<T>(), allocator, 0);
            var nativeArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(buffer, length, allocator);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref nativeArray,
                (allocator == Allocator.Temp) ? AtomicSafetyHandle.GetTempMemoryHandle() : AtomicSafetyHandle.Create());
#endif
            return nativeArray;
        }

        public unsafe static void ClearMemory<T>(in NativeArray<T> nativeArray) where T : unmanaged
        {
            void* buffer = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(nativeArray);
            int length = nativeArray.Length;
            UnsafeUtility.MemClear(buffer, (long)length * (long)UnsafeUtility.SizeOf<T>());
        }

        public unsafe static void ClearMemory<T>(in Span<T> data) where T : unmanaged
        {
            int length = data.Length;
            fixed (T* buffer = data)
            {
                UnsafeUtility.MemClear(buffer, (long)length * (long)UnsafeUtility.SizeOf<T>());
            }
        }
    }
}
