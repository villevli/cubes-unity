using System;
using System.Runtime.InteropServices;
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
        public const int BlockCount = Size * Size * Size;

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

        /// <summary>
        /// Is the chunk waiting to be updated.
        /// </summary>
        [MarshalAs(UnmanagedType.U1)]
        public bool IsPendingUpdate;

        [MarshalAs(UnmanagedType.U1)]
        public bool IsInViewDistance;

        /// <summary>
        /// 15 bits that tell which combinations of 2 faces can see each other via non opaque blocks inside the chunk.
        /// 
        /// 0  = down,up
        /// 1  = down,south
        /// 2  = down,north
        /// 3  = down,west
        /// 4  = down,east
        /// 5  = up,south
        /// 6  = up,north
        /// 7  = up,west
        /// 8  = up,east
        /// 9  = south,north
        /// 10 = south,west
        /// 11 = south,east
        /// 12 = north,west
        /// 13 = north,east
        /// 14 = west,east
        /// </summary>
        public short ConnectedFaces;

        /// <summary>
        /// Get the 15 bits for comparing a face againt the ConnectedFaces
        /// </summary>
        /// <param name="face"></param>
        /// <returns></returns>
        public static short FaceBits(int face)
        {
            return face switch
            {
                0 => 0b000000000011111, // down
                1 => 0b000000111100001, // up
                2 => 0b000111000100010, // south
                3 => 0b011001001000100, // north
                4 => 0b101010010001000, // west
                5 => 0b110100100010000, // east
                _ => 0,
            };
        }

        public static short GetConnectedFacesFromSet(short set)
        {
            short result = 0;
            if ((set & 0b000011) == 0b000011) result |= 1 << 0;
            if ((set & 0b000101) == 0b000101) result |= 1 << 1;
            if ((set & 0b001001) == 0b001001) result |= 1 << 2;
            if ((set & 0b010001) == 0b010001) result |= 1 << 3;
            if ((set & 0b100001) == 0b100001) result |= 1 << 4;
            if ((set & 0b000110) == 0b000110) result |= 1 << 5;
            if ((set & 0b001010) == 0b001010) result |= 1 << 6;
            if ((set & 0b010010) == 0b010010) result |= 1 << 7;
            if ((set & 0b100010) == 0b100010) result |= 1 << 8;
            if ((set & 0b001100) == 0b001100) result |= 1 << 9;
            if ((set & 0b010100) == 0b010100) result |= 1 << 10;
            if ((set & 0b100100) == 0b100100) result |= 1 << 11;
            if ((set & 0b011000) == 0b011000) result |= 1 << 12;
            if ((set & 0b101000) == 0b101000) result |= 1 << 13;
            if ((set & 0b110000) == 0b110000) result |= 1 << 14;
            return result;
        }

        public static bool AreFacesConnected(short connectedFaces, int a, int b)
        {
            return (FaceBits(a) & FaceBits(b) & connectedFaces) != 0;
        }

        public static int3 FaceNormal(int face)
        {
            return face switch
            {
                0 => new(0, -1, 0),
                1 => new(0, 1, 0),
                2 => new(0, 0, -1),
                3 => new(0, 0, 1),
                4 => new(-1, 0, 0),
                5 => new(1, 0, 0),
                _ => default,
            };
        }

        public static int OppositeFace(int face) => face % 2 == 0 ? face + 1 : face - 1;

        public Chunk(int3 position)
        {
            Position = position;
            Blocks = default;
            Palette = default;
            IsPendingUpdate = false;
            IsInViewDistance = false;
            ConnectedFaces = ~0;
        }

        public void Dispose()
        {
            Blocks.Dispose();
            Blocks = default;
            Palette.Dispose();
            Palette = default;
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
