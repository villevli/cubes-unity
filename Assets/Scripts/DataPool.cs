using System;
using Unity.Collections;

namespace Cubes
{
    public interface IDataFactory<T> where T : unmanaged
    {
        public T Allocate();
        public void Free(in T value);
    }

    // TODO: Make thread safe

    public readonly struct DataPool<TData, TFactory> : IDisposable
        where TData : unmanaged
        where TFactory : unmanaged, IDataFactory<TData>
    {
        public readonly NativeList<TData> Buffer;
        public readonly TFactory Factory;

        public DataPool(Allocator allocator, TFactory factory = default)
        {
            Buffer = new(allocator);
            Factory = factory;
        }

        public void Dispose()
        {
            Clear();
            Buffer.Dispose();
        }

        public TData Allocate()
        {
            if (Buffer.Length > 0)
            {
                var res = Buffer[^1];
                Buffer.RemoveAt(Buffer.Length - 1);
                return res;
            }
            return Factory.Allocate();
        }

        public void Free(in TData value)
        {
            Buffer.Add(value);
        }

        public void Clear()
        {
            foreach (var item in Buffer)
            {
                Factory.Free(item);
            }
            Buffer.Clear();
        }
    }
}
