using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cubes
{
    /// <summary>
    /// Create a mesh for a chunk. Allocates required buffers.
    /// Can be reused within a thread to process multiple chunks.
    /// Creates a point mesh with vertex data to support a geometry shader that renders the points as cubes.
    /// </summary>
    [BurstCompile]
    public struct CreateMeshPoints : IDisposable
    {
        private NativeArray<Vertex> VertexBuffer;
        private NativeArray<ushort> IndexBuffer;
        public int VertexCount;
        public int IndexCount;

        private struct Vertex
        {
            public float3 position;
            public float4 uvRect; // TEXCOORD0
            public float2 sides; // TEXCOORD1
        }

        public CreateMeshPoints(Allocator allocator)
        {
            VertexBuffer = new NativeArray<Vertex>(4096, allocator, NativeArrayOptions.UninitializedMemory);
            IndexBuffer = new NativeArray<ushort>(4096, allocator, NativeArrayOptions.UninitializedMemory);
            VertexCount = 0;
            IndexCount = 0;
        }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }

        private enum Sides
        {
            Down = 1,
            Up = 2,
            South = 4,
            North = 8,
            West = 16,
            East = 32,
        }

        [BurstCompile]
        public static void Run(in Chunk chunk, in NativeParallelHashMap<int3, Chunk> chunks, in NativeArray<BlockType> blockTypes, ref CreateMeshPoints buffers, in CreateMesh.Params p)
        {
            var verts = buffers.VertexBuffer;
            var indices = buffers.IndexBuffer;
            int vertCount = 0;

            if (chunk.Palette.Length == 0 || (chunk.Palette.Length == 1 && chunk.Palette[0] == BlockType.Air))
            {
                buffers.VertexCount = vertCount;
                buffers.IndexCount = vertCount;
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

            static void AddPoint(ref NativeArray<Vertex> verts, ref NativeArray<ushort> indices, ref int count, int x, int y, int z, Sides sides, in BlockType type)
            {
                indices[count] = (ushort)count;
                verts[count] = new()
                {
                    position = new(x, y, z),
                    uvRect = new(type.TexAtlasRect.min, type.TexAtlasRect.max),
                    sides = new(math.asfloat((int)sides), 0),
                };
                count++;
            }

            static bool IsBlockOpaque(in int block)
            {
                return block != BlockType.Air;
            }
            static bool IsOpaque(in ReadOnlySpan<byte> blocks, in ReadOnlySpan<int> palette, int x, int y, int z)
            {
                return IsBlockOpaque(GetBlockType(blocks, palette, x, y, z));
            }
            static bool IsNeighborOpaque(in Chunk chunk, int x, int y, int z, in CreateMesh.Params p)
            {
                if (!chunk.IsLoaded)
                    return !p.AddBorderWalls;
                if (chunk.Palette.Length == 1)
                    return IsBlockOpaque(chunk.Palette[0]);
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
                            AddPoint(ref verts, ref indices, ref vertCount, x, 0, z, Sides.Down, type);
                        }
                        // up y+
                        if (!IsNeighborOpaque(chunkUp, x, 0, z, p))
                        {
                            AddPoint(ref verts, ref indices, ref vertCount, x, size, z, Sides.Down, type);
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
                            AddPoint(ref verts, ref indices, ref vertCount, x, y, 0, Sides.South, type);
                        }
                        // north z+
                        if (!IsNeighborOpaque(chunkNorth, x, y, 0, p))
                        {
                            AddPoint(ref verts, ref indices, ref vertCount, x, y, size, Sides.North, type);
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
                            AddPoint(ref verts, ref indices, ref vertCount, 0, y, z, Sides.West, type);
                        }
                        // east x+
                        if (!IsNeighborOpaque(chunkEast, 0, y, z, p))
                        {
                            AddPoint(ref verts, ref indices, ref vertCount, size, y, z, Sides.East, type);
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

                            Sides sides = 0;

                            // down y-
                            if (y > 0 ? !IsOpaque(blocks, palette, x, y - 1, z) : !IsNeighborOpaque(chunkDown, x, size - 1, z, p))
                            {
                                sides |= Sides.Down;
                            }
                            // up y+
                            if (y < size - 1 ? !IsOpaque(blocks, palette, x, y + 1, z) : !IsNeighborOpaque(chunkUp, x, 0, z, p))
                            {
                                sides |= Sides.Up;
                            }
                            // south z-
                            if (z > 0 ? !IsOpaque(blocks, palette, x, y, z - 1) : !IsNeighborOpaque(chunkSouth, x, y, size - 1, p))
                            {
                                sides |= Sides.South;
                            }
                            // north z+
                            if (z < size - 1 ? !IsOpaque(blocks, palette, x, y, z + 1) : !IsNeighborOpaque(chunkNorth, x, y, 0, p))
                            {
                                sides |= Sides.North;
                            }
                            // west x-
                            if (x > 0 ? !IsOpaque(blocks, palette, x - 1, y, z) : !IsNeighborOpaque(chunkWest, size - 1, y, z, p))
                            {
                                sides |= Sides.West;
                            }
                            // east x+
                            if (x < size - 1 ? !IsOpaque(blocks, palette, x + 1, y, z) : !IsNeighborOpaque(chunkEast, 0, y, z, p))
                            {
                                sides |= Sides.East;
                            }

                            if (sides != 0)
                            {
                                AddPoint(ref verts, ref indices, ref vertCount, x, y, z, sides, type);
                            }
                        }
                    }
                }
            }

            buffers.VertexCount = vertCount;
            buffers.IndexCount = vertCount;
        }

        [BurstCompile]
        public static void SetMeshData(in CreateMeshPoints buffers, ref Mesh.MeshData meshData)
        {
            var vbp = new NativeArray<VertexAttributeDescriptor>(3, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            vbp[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
            vbp[1] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4);
            vbp[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2);
            meshData.SetVertexBufferParams(buffers.VertexCount, vbp);

            var pos = meshData.GetVertexData<Vertex>();
            buffers.VertexBuffer.GetSubArray(0, buffers.VertexCount).CopyTo(pos);

            meshData.SetIndexBufferParams(buffers.IndexCount, IndexFormat.UInt16);
            var ib = meshData.GetIndexData<ushort>();
            buffers.IndexBuffer.GetSubArray(0, buffers.IndexCount).CopyTo(ib);

            meshData.subMeshCount = 1;
            var smd = new SubMeshDescriptor(0, ib.Length)
            {
                topology = MeshTopology.Points,
                bounds = new((float3)(Chunk.Size / 2), (float3)Chunk.Size)
            };
            meshData.SetSubMesh(0, smd, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }
    }
}
