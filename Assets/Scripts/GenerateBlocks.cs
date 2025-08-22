using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Cubes
{
    /// <summary>
    /// Procedurally generate the blocks in a chunk. Allocates required buffers.
    /// Can be reused within a thread to process multiple chunks.
    /// </summary>
    [BurstCompile]
    public struct GenerateBlocks : IDisposable
    {
        const int size = Chunk.Size;

        public enum Type
        {
            Flat,
            Plane,
            SimplexNoise2D,
            PerlinNoise2D,
            SimplexNoise3D,
            PerlinNoise3D,
            CustomTerrain,
        }

        [Serializable]
        public struct Params
        {
            public Type Type;
            public float3 Offset;
            public float3 Scale;
            public float Offset2;
            public float Scale2;

            public static readonly Params Default = new()
            {
                Type = Type.Flat,
                Offset = 0,
                Scale = 1,
                Offset2 = 0,
                Scale2 = 1
            };
        }

        private NativeArray<byte> BlockBuffer;

        public GenerateBlocks(Allocator allocator)
        {
            BlockBuffer = new(size * size * size, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public void Dispose()
        {
            BlockBuffer.Dispose();
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        public static void Run(ref Chunk chunk, ref GenerateBlocks buffers, in Params p)
        {
            var blocks = buffers.BlockBuffer.AsSpan();

            Span<int> palette = stackalloc int[] {
                BlockType.Air,
                BlockType.Stone
            };

            Span<int> paletteCounts = stackalloc int[palette.Length];
            paletteCounts.Clear();
            int3 chunkMin = chunk.Position * size;

            switch (p.Type)
            {
                case Type.Flat:
                    GenerateFlat(p, blocks, chunkMin, paletteCounts);
                    break;
                case Type.Plane:
                    GeneratePlane(p, blocks, chunkMin, paletteCounts);
                    break;
                case Type.SimplexNoise2D:
                    GenerateSimplexNoise2D(p, blocks, chunkMin, paletteCounts);
                    break;
                case Type.PerlinNoise2D:
                    GeneratePerlinNoise2D(p, blocks, chunkMin, paletteCounts);
                    break;
                case Type.SimplexNoise3D:
                    GenerateSimplexNoise3D(p, blocks, chunkMin, paletteCounts);
                    break;
                case Type.PerlinNoise3D:
                    GeneratePerlinNoise3D(p, blocks, chunkMin, paletteCounts);
                    break;
            }

            UpdateChunkData(ref chunk, buffers.BlockBuffer, palette, paletteCounts);
        }

        private static void GenerateFlat(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale;

            for (int y = 0; y < size; y++)
            {
                var ay = (y + offset.y) * scale.y;
                var paletteIndex = -ay > 0 ? 1 : 0;
                paletteCounts[paletteIndex] += size * size;
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        private static void GeneratePlane(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    var xz = (new int2(x, z) + offset.xz) * scale.xz;
                    var val = xz.x + xz.y;

                    for (int y = 0; y < size; y++)
                    {
                        var ay = (y + offset.y) * scale.y;
                        var paletteIndex = val - ay > 0 ? 1 : 0;
                        paletteCounts[paletteIndex]++;
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        private static void GenerateSimplexNoise2D(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale * 0.025f;
            var offset2 = p.Offset2;
            var scale2 = p.Scale2 * 0.5f;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    var xz = (new int2(x, z) + offset.xz) * scale.xz;
                    var val = (noise.snoise(xz) + offset2) * scale2;

                    for (int y = 0; y < size; y++)
                    {
                        var ay = (y + offset.y) * scale.y;
                        var paletteIndex = val - ay > 0 ? 1 : 0;
                        paletteCounts[paletteIndex]++;
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        private static void GeneratePerlinNoise2D(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale * 0.05f;
            var offset2 = p.Offset2;
            var scale2 = p.Scale2;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    var xz = (new int2(x, z) + offset.xz) * scale.xz;
                    var val = (noise.cnoise(xz) + offset2) * scale2;

                    for (int y = 0; y < size; y++)
                    {
                        var ay = (y + offset.y) * scale.y;
                        var paletteIndex = val - ay > 0 ? 1 : 0;
                        paletteCounts[paletteIndex]++;
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        private static void GenerateSimplexNoise3D(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale * 0.025f;
            var offset2 = p.Offset2;
            var scale2 = p.Scale2 * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var xyz = (new int3(x, y, z) + offset) * scale;
                        var val = (noise.snoise(xyz) + offset2) * scale2;

                        var paletteIndex = val - xyz.y > 0 ? 1 : 0;
                        paletteCounts[paletteIndex]++;
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        private static void GeneratePerlinNoise3D(in Params p, in Span<byte> blocks, in int3 chunkMin, in Span<int> paletteCounts)
        {
            var offset = chunkMin - p.Offset;
            var scale = p.Scale * 0.05f;
            var offset2 = p.Offset2;
            var scale2 = p.Scale2;

            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var xyz = (new int3(x, y, z) + offset) * scale;
                        var val = (noise.cnoise(xyz) + offset2) * scale2;

                        var paletteIndex = val - xyz.y > 0 ? 1 : 0;
                        paletteCounts[paletteIndex]++;
                        Chunk.GetBlock(blocks, x, y, z) = (byte)paletteIndex;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateChunkData(ref Chunk chunk, in NativeArray<byte> blockBuffer, in Span<int> palette, in Span<int> paletteCounts)
        {
            int blockStateCount = 0;
            for (int i = 0; i < paletteCounts.Length; i++)
            {
                if (paletteCounts[i] > 0)
                    blockStateCount++;
            }

            int firstPaletteIdx = 0;

            // If more than one block was used, use full palette so we don't have to modify the block data
            if (blockStateCount > 1)
            {
                blockStateCount = palette.Length;
            }
            // No blocks generated? Ensure we still always have a palette of length 1
            else if (blockStateCount == 0)
            {
                blockStateCount = 1;
            }
            else
            {
                firstPaletteIdx = blockBuffer[0];
            }

            if (!chunk.Palette.IsCreated || chunk.Palette.Length != blockStateCount)
            {
                chunk.Palette.Dispose();
                chunk.Palette = new(blockStateCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            if (blockStateCount > 1)
            {
                for (int i = 0; i < blockStateCount; i++)
                {
                    chunk.Palette[i] = palette[i];
                }

                if (!chunk.Blocks.IsCreated)
                {
                    chunk.Blocks = new(size * size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
                blockBuffer.CopyTo(chunk.Blocks);
            }
            else
            {
                chunk.Palette[0] = palette[firstPaletteIdx];

                chunk.Blocks.Dispose();
                chunk.Blocks = default;
            }
        }
    }
}
