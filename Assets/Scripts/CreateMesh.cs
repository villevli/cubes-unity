using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cubes
{
    /// <summary>
    /// Create a mesh for a chunk. Allocates required buffers.
    /// Can be reused within a thread to process multiple chunks.
    /// </summary>
    [BurstCompile]
    public struct CreateMesh : IDisposable
    {
        private NativeArray<Vertex> VertexBuffer;
        private NativeArray<ushort> IndexBuffer;
        public int VertexCount;
        public int IndexCount;

        public static readonly int SizeOfVertex = UnsafeUtility.SizeOf<Vertex>();
        public static readonly int SizeOfIndex = sizeof(ushort);

        private struct Vertex
        {
            public byte4 position;
            public sbyte4 normal;
            public float2 uv;
        }

        private struct byte4
        {
            public byte x, y, z, w;

            public byte4(byte x, byte y, byte z, byte w = 0)
            {
                this.x = x; this.y = y; this.z = z; this.w = w;
            }
        }

        private struct sbyte4
        {
            public sbyte x, y, z, w;

            public sbyte4(sbyte x, sbyte y, sbyte z, sbyte w = 0)
            {
                this.x = x; this.y = y; this.z = z; this.w = w;
            }
        }

        [Serializable]
        public struct Params
        {
            [Tooltip("If neighboring chunk is not loaded, add a wall when looking from outside")]
            [MarshalAs(UnmanagedType.U1)]
            public bool AddBorderWalls;

            public static readonly Params Default = new()
            {
                AddBorderWalls = true,
            };
        }

        public CreateMesh(Allocator allocator)
        {
            VertexBuffer = new NativeArray<Vertex>(32768, allocator, NativeArrayOptions.UninitializedMemory);
            IndexBuffer = new NativeArray<ushort>(65536, allocator, NativeArrayOptions.UninitializedMemory);
            VertexCount = 0;
            IndexCount = 0;
        }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }

        public static bool NeedsMesh(in Chunk chunk)
        {
            return chunk.Palette.Length > 0 && (chunk.Palette.Length != 1 || chunk.Palette[0] != BlockType.Air);
        }

        [BurstCompile]
        public static void Run(in Chunk chunk, in NativeParallelHashMap<int3, Chunk> chunks, in NativeArray<BlockType> blockTypes, ref CreateMesh buffers, in Params p)
        {
            var verts = buffers.VertexBuffer;
            var indices = buffers.IndexBuffer;
            int vertCount = 0;
            int indexCount = 0;

            if (!NeedsMesh(chunk))
            {
                buffers.VertexCount = vertCount;
                buffers.IndexCount = indexCount;
                return;
            }

            const int size = Chunk.Size;

            static int GetBlockType(in ReadOnlySpan<byte> blocks, in ReadOnlySpan<int> palette, int x, int y, int z)
            {
                return palette[Chunk.GetBlock(blocks, x, y, z)];
            }

            static Chunk GetNeighborChunk(in int3 chunkPos, in NativeParallelHashMap<int3, Chunk> chunks)
            {
                if (chunks.TryGetValue(chunkPos, out var chunkDown))
                    return chunkDown;
                return default;
            }

            var chunkDown = GetNeighborChunk(chunk.Position + new int3(0, -1, 0), chunks);
            var chunkUp = GetNeighborChunk(chunk.Position + new int3(0, 1, 0), chunks);
            var chunkSouth = GetNeighborChunk(chunk.Position + new int3(0, 0, -1), chunks);
            var chunkNorth = GetNeighborChunk(chunk.Position + new int3(0, 0, 1), chunks);
            var chunkWest = GetNeighborChunk(chunk.Position + new int3(-1, 0, 0), chunks);
            var chunkEast = GetNeighborChunk(chunk.Position + new int3(1, 0, 0), chunks);

            static void AddIndices(ref NativeArray<ushort> indices, ref int count, int vi)
            {
                indices[count++] = (ushort)(vi + 0);
                indices[count++] = (ushort)(vi + 1);
                indices[count++] = (ushort)(vi + 2);
                indices[count++] = (ushort)(vi + 2);
                indices[count++] = (ushort)(vi + 3);
                indices[count++] = (ushort)(vi + 0);
            }

            static float2 GetUV(in BlockType type, int corner)
            {
                var uvr = type.TexAtlasRect;
                return corner switch
                {
                    0 => uvr.min,
                    1 => new(uvr.xMin, uvr.yMax),
                    2 => uvr.max,
                    3 => new(uvr.xMax, uvr.yMin),
                    _ => 0,
                };
            }

            static void AddVertex(ref NativeArray<Vertex> verts, ref int count, int x, int y, int z, int corner, in BlockType type, in sbyte4 normal)
            {
                verts[count++] = new()
                {
                    position = new(
                        (byte)(x * (128 / size)),
                        (byte)(y * (128 / size)),
                        (byte)(z * (128 / size))
                    // TODO: pack the normal in the w byte, use custom shader
                    ),
                    normal = normal,
                    uv = GetUV(type, corner)
                };
            }

            sbyte4 down = new(0, -128, 0);
            sbyte4 up = new(0, 127, 0);
            sbyte4 south = new(0, 0, -128);
            sbyte4 north = new(0, 0, 127);
            sbyte4 west = new(-128, 0, 0);
            sbyte4 east = new(127, 0, 0);

            static bool IsOpaque(in ReadOnlySpan<byte> blocks, in ReadOnlySpan<int> palette, int x, int y, int z)
            {
                return BlockType.IsOpaque(GetBlockType(blocks, palette, x, y, z));
            }
            static bool IsNeighborOpaque(in Chunk chunk, int x, int y, int z, in Params p)
            {
                if (!chunk.IsLoaded)
                    return !p.AddBorderWalls;
                if (chunk.Palette.Length == 1)
                    return BlockType.IsOpaque(chunk.Palette[0]);
                return IsOpaque(chunk.Blocks, chunk.Palette, x, y, z);
            }

            var types = blockTypes.AsReadOnlySpan();

            if (chunk.Palette.Length == 1)
            {
                BlockType type = types[chunk.Palette[0]];

                // Iterate only the edges of the chunk when the chunk has only one block type
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // down y-
                        if (!IsNeighborOpaque(chunkDown, x, size - 1, z, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, 0, z + 0, 0, type, down);
                            AddVertex(ref verts, ref vertCount, x + 1, 0, z + 0, 1, type, down);
                            AddVertex(ref verts, ref vertCount, x + 1, 0, z + 1, 2, type, down);
                            AddVertex(ref verts, ref vertCount, x + 0, 0, z + 1, 3, type, down);
                        }
                        // up y+
                        if (!IsNeighborOpaque(chunkUp, x, 0, z, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, size, z + 0, 0, type, up);
                            AddVertex(ref verts, ref vertCount, x + 0, size, z + 1, 1, type, up);
                            AddVertex(ref verts, ref vertCount, x + 1, size, z + 1, 2, type, up);
                            AddVertex(ref verts, ref vertCount, x + 1, size, z + 0, 3, type, up);
                        }
                    }
                }

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // south z-
                        if (!IsNeighborOpaque(chunkSouth, x, y, size - 1, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, 0, 0, type, south);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, 0, 1, type, south);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, 0, 2, type, south);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, 0, 3, type, south);
                        }
                        // north z+
                        if (!IsNeighborOpaque(chunkNorth, x, y, 0, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, size, 0, type, north);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, size, 1, type, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, size, 2, type, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, size, 3, type, north);
                        }
                    }
                }

                for (int y = 0; y < size; y++)
                {
                    for (int z = 0; z < size; z++)
                    {
                        // west x-
                        if (!IsNeighborOpaque(chunkWest, size - 1, y, z, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, 0, y + 0, z + 1, 0, type, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 1, z + 1, 1, type, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 1, z + 0, 2, type, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 0, z + 0, 3, type, west);
                        }
                        // east x+
                        if (!IsNeighborOpaque(chunkEast, 0, y, z, p))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, size, y + 0, z + 0, 0, type, east);
                            AddVertex(ref verts, ref vertCount, size, y + 1, z + 0, 1, type, east);
                            AddVertex(ref verts, ref vertCount, size, y + 1, z + 1, 2, type, east);
                            AddVertex(ref verts, ref vertCount, size, y + 0, z + 1, 3, type, east);
                        }
                    }
                }
            }
            else
            {
                var blocks = chunk.Blocks.AsReadOnlySpan();
                var palette = chunk.Palette.AsReadOnlySpan();
                for (int y = 0; y < size; y++)
                {
                    for (int z = 0; z < size; z++)
                    {
                        for (int x = 0; x < size; x++)
                        {
                            int block = GetBlockType(blocks, palette, x, y, z);

                            if (block == BlockType.Air)
                                continue;

                            BlockType type = types[block];

                            // Only add faces where the neighboring block is transparent
                            // IsNeighborOpaque checks in the neighboring chunk when at the edge of this chunk

                            // down y-
                            if (y > 0 ? !IsOpaque(blocks, palette, x, y - 1, z) : !IsNeighborOpaque(chunkDown, x, size - 1, z, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, 0, type, down);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, 1, type, down);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, 2, type, down);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, 3, type, down);
                            }
                            // up y+
                            if (y < size - 1 ? !IsOpaque(blocks, palette, x, y + 1, z) : !IsNeighborOpaque(chunkUp, x, 0, z, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, 0, type, up);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, 1, type, up);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, 2, type, up);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, 3, type, up);
                            }
                            // south z-
                            if (z > 0 ? !IsOpaque(blocks, palette, x, y, z - 1) : !IsNeighborOpaque(chunkSouth, x, y, size - 1, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, 0, type, south);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, 1, type, south);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, 2, type, south);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, 3, type, south);
                            }
                            // north z+
                            if (z < size - 1 ? !IsOpaque(blocks, palette, x, y, z + 1) : !IsNeighborOpaque(chunkNorth, x, y, 0, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, 0, type, north);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, 1, type, north);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, 2, type, north);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, 3, type, north);
                            }
                            // west x-
                            if (x > 0 ? !IsOpaque(blocks, palette, x - 1, y, z) : !IsNeighborOpaque(chunkWest, size - 1, y, z, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, 0, type, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, 1, type, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, 2, type, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, 3, type, west);
                            }
                            // east x+
                            if (x < size - 1 ? !IsOpaque(blocks, palette, x + 1, y, z) : !IsNeighborOpaque(chunkEast, 0, y, z, p))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, 0, type, east);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, 1, type, east);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, 2, type, east);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, 3, type, east);
                            }
                        }
                    }
                }
            }

            buffers.VertexCount = vertCount;
            buffers.IndexCount = indexCount;
        }

        [BurstCompile]
        public static void SetMeshData(in CreateMesh buffers, ref Mesh.MeshData meshData)
        {
            var vbp = new NativeArray<VertexAttributeDescriptor>(3, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            vbp[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.UNorm8, 4);
            vbp[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4);
            vbp[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);
            meshData.SetVertexBufferParams(buffers.VertexCount, vbp);
            vbp.Dispose();

            var pos = meshData.GetVertexData<Vertex>();
            buffers.VertexBuffer.GetSubArray(0, buffers.VertexCount).CopyTo(pos);

            meshData.SetIndexBufferParams(buffers.IndexCount, IndexFormat.UInt16);
            var ib = meshData.GetIndexData<ushort>();
            buffers.IndexBuffer.GetSubArray(0, buffers.IndexCount).CopyTo(ib);

            meshData.subMeshCount = 1;
            var smd = new SubMeshDescriptor(0, ib.Length) { bounds = new((float3)(64 / 255f), (float3)(128 / 255f)) };
            meshData.SetSubMesh(0, smd, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }
    }
}
