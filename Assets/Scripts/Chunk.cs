using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Cubes
{
    [Serializable]
    public struct BlockType
    {
        public Rect TexAtlasRect;

        public const int Air = 0;
        public const int Stone = 1;
    }

    public struct Chunk : IDisposable
    {
        /// <summary>
        /// Each chunk contains Size^3 blocks.
        /// </summary>
        public const int Size = 16;

        // To support many block types and states, use a palette of block states like described in https://minecraft.wiki/w/Chunk_format

        /// <summary>
        /// Block data in this chunk. Contains indices into the <see cref="Palette"/> array.
        /// </summary>
        public NativeArray<byte> Blocks;

        /// <summary>
        /// Block types and states contained in this chunk. If it contains a single element then the <see cref="Blocks"/> array is not needed.
        /// </summary>
        public NativeArray<int> Palette;

        /// <summary>
        /// Position in chunks from the world origin.
        /// </summary>
        public int3 Position;

        /// <summary>
        /// Has the chunks's data been loaded.
        /// </summary>
        public bool IsLoaded => Palette.IsCreated;

        public Chunk(int3 position)
        {
            Position = position;
            Blocks = default;
            Palette = default;
        }

        public void Dispose()
        {
            Blocks.Dispose();
            Palette.Dispose();
        }

        public override readonly string ToString()
        {
            return $"{Position.x},{Position.y},{Position.z}";
        }

        /// <summary>
        /// Get a reference to the block at the local <paramref name="pos"/> in the chunk.
        /// </summary>
        /// <param name="blocks"></param>
        /// <param name="pos"></param>
        /// <returns></returns>
        public static ref byte GetBlock(in Span<byte> blocks, in int3 pos)
        {
            return ref GetBlock(blocks, pos.x, pos.y, pos.z);
        }
        public static ref byte GetBlock(in Span<byte> blocks, int x, int y, int z)
        {
            return ref blocks[y * Size * Size + z * Size + x];
        }

        /// <summary>
        /// Get the block at the local <paramref name="pos"/> in the chunk.
        /// </summary>
        /// <param name="blocks"></param>
        /// <param name="pos"></param>
        /// <returns></returns>
        public static byte GetBlock(in ReadOnlySpan<byte> blocks, in int3 pos)
        {
            return GetBlock(blocks, pos.x, pos.y, pos.z);
        }
        public static byte GetBlock(in ReadOnlySpan<byte> blocks, int x, int y, int z)
        {
            return blocks[y * Size * Size + z * Size + x];
        }
    }
}
