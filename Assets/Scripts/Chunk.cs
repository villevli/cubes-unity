using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Cubes
{
    public static class BlockType
    {
        public const byte Air = 0;
        public const byte Stone = 1;
    }

    public struct Chunk : IDisposable
    {
        /// <summary>
        /// Each chunk contains Size^3 blocks.
        /// </summary>
        public const int Size = 16;

        // TODO: To support many block types and states, add a palette of block states like described in https://minecraft.wiki/w/Chunk_format
        /// <summary>
        /// Block data in this chunk.
        /// </summary>
        public NativeArray<byte> Blocks;

        /// <summary>
        /// Position in chunks from the world origin.
        /// </summary>
        public int3 Position;

        public Chunk(int3 position, Allocator allocator)
        {
            Position = position;
            Blocks = new(Size * Size * Size, allocator, NativeArrayOptions.ClearMemory);
        }

        public void Dispose()
        {
            Blocks.Dispose();
        }

        public override readonly string ToString()
        {
            return $"{Position.x},{Position.y},{Position.z}";
        }

        /// <summary>
        /// Get a block at the local <paramref name="pos"/> in the chunk.
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public readonly ref byte GetBlock(int3 pos)
        {
            return ref Blocks.AsSpan()[pos.y * Size * Size + pos.z * Size + pos.x];
        }
    }
}
