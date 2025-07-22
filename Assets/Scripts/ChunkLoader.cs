using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static System.FormattableString;

namespace Cubes
{
    public class ChunkLoader : MonoBehaviour
    {
        [SerializeField]
        private int3 _viewDistance = 8;
        [SerializeField]
        private GameObject _chunkPrefab;

        private struct LoadedChunk
        {
            public Chunk chunk;
            public GameObject go;
            public Mesh mesh;
        }

        private List<LoadedChunk> Chunks = new();

        private void OnEnable()
        {
            Application.targetFrameRate = 60;

            Load();
        }

        private void OnDisable()
        {
            Unload();
        }

        private void OnGUI()
        {
            if (GUILayout.Button("Reload"))
            {
                Unload();
                Load();
            }
            if (GUILayout.Button("Load"))
            {
                Load();
            }
        }

        private void Load()
        {
            TimerResults timers = new();
            var totalTime = new TimerScope("total", null);

            GenerateBlocks generateBlocks;
            CreateMesh createMesh;
            using (new TimerScope("buffers", timers))
            {
                generateBlocks = new GenerateBlocks(Allocator.Temp);
                createMesh = new CreateMesh(Allocator.Temp);
            }

            using (new TimerScope("create", timers))
            {
                if (Chunks.Count == 0)
                {
                    CreateChunks();
                }
            }

            for (int i = 0; i < Chunks.Count; i++)
            {
                Chunks[i] = GenerateChunk(Chunks[i], ref generateBlocks, timers);
            }

            for (int i = 0; i < Chunks.Count; i++)
            {
                Chunks[i] = CreateChunkMesh(Chunks[i], ref createMesh, timers);
            }

            var totalMs = totalTime.ResultToString();
            Debug.Log(Invariant($"Loaded {Chunks.Count} chunks in {totalMs}. {timers}"));
        }

        private void Unload()
        {
            foreach (var chunk in Chunks)
            {
                DestroyImmediate(chunk.mesh);
                DestroyImmediate(chunk.go);
                chunk.chunk.Dispose();
            }
            Chunks.Clear();
        }

        private void CreateChunks()
        {
            int3 viewDist = _viewDistance;

            for (int y = -viewDist.y; y < viewDist.y; y++)
            {
                for (int z = -viewDist.z; z < viewDist.z; z++)
                {
                    for (int x = -viewDist.x; x < viewDist.x; x++)
                    {
                        // TODO: Do not allocate block data at this point. Only when known if chunk has more than one block type
                        Chunks.Add(new()
                        {
                            chunk = new Chunk(new(x, y, z), Allocator.Persistent)
                        });
                    }
                }
            }
        }

        private LoadedChunk GenerateChunk(LoadedChunk chunk, ref GenerateBlocks generateBlocks, TimerResults timers)
        {
            using (new TimerScope("gen", timers))
            {
                GenerateBlocks.Run(ref chunk.chunk, ref generateBlocks);
            }
            return chunk;
        }

        private LoadedChunk CreateChunkMesh(LoadedChunk chunk, ref CreateMesh createMesh, TimerResults timers)
        {
            using (new TimerScope("mesh", timers))
            {
                CreateMesh.Run(chunk.chunk, ref createMesh);
            }

            var dataArray = Mesh.AllocateWritableMeshData(1);
            var meshData = dataArray[0];

            using (new TimerScope("meshdata", timers))
            {
                CreateMesh.SetMeshData(createMesh, ref meshData);
            }

            Mesh mesh = chunk.mesh;

            using (new TimerScope("newmesh", timers))
            {
                if (meshData.vertexCount == 0)
                {
                    DestroyImmediate(mesh);
                    mesh = null;
                }
                else if (mesh == null)
                {
                    mesh = new();
                    mesh.name = "Chunk";
                }
                chunk.mesh = mesh;
            }
            using (new TimerScope("apply", timers))
            {
                if (mesh is not null)
                {
                    Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                }
                else
                {
                    dataArray.Dispose();
                }
            }
            // using (new TimerScope("normals", timers))
            // {
            //     if (mesh is not null)
            //     {
            //         mesh.RecalculateNormals();
            //     }
            // }
            using (new TimerScope("bounds", timers))
            {
                if (mesh is not null)
                {
                    mesh.bounds = mesh.GetSubMesh(0).bounds;
                }
            }

            GameObject go = chunk.go;

            using (new TimerScope("go", timers))
            {
                if (mesh is null)
                {
                    DestroyImmediate(go);
                    go = null;
                }
                else if (go == null)
                {
                    go = Instantiate(_chunkPrefab);
                    go.name = "Chunk" + chunk.chunk;
                }
                chunk.go = go;
            }
            using (new TimerScope("pos", timers))
            {
                if (go is not null)
                {
                    go.transform.position = (float3)(chunk.chunk.Position * Chunk.Size);
                }
            }
            using (new TimerScope("filter", timers))
            {
                if (go is not null)
                {
                    go.GetComponent<MeshFilter>().mesh = mesh;
                }
            }
            return chunk;
        }
    }

    /// <summary>
    /// Procedurally generate the blocks in a chunk. Allocates required buffers.
    /// Can be reused within a thread to process multiple chunks.
    /// </summary>
    [BurstCompile]
    public struct GenerateBlocks : IDisposable
    {
        const int size = Chunk.Size;

        private NativeArray<byte> BlockBuffer;

        public GenerateBlocks(Allocator allocator)
        {
            BlockBuffer = new(size * size * size, allocator, NativeArrayOptions.UninitializedMemory);
        }

        public void Dispose()
        {
            BlockBuffer.Dispose();
        }

        [BurstCompile]
        public static void Run(ref Chunk chunk, ref GenerateBlocks buffers)
        {
            var blocks = buffers.BlockBuffer.AsSpan();
            static ref byte GetBlock(in Span<byte> blocks, int x, int y, int z)
            {
                return ref blocks[y * size * size + z * size + x];
            }

            int3 chunkMin = chunk.Position * size;
            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        GetBlock(blocks, x, y, z) = chunkMin.y + y < 2 ? BlockType.Stone : BlockType.Air;
                    }
                }
            }

            buffers.BlockBuffer.CopyTo(chunk.Blocks);
        }
    }

    /// <summary>
    /// Create a mesh for a chunk. Allocates required buffers.
    /// Can be reused within a thread to process multiple chunks.
    /// </summary>
    [BurstCompile]
    public struct CreateMesh : IDisposable
    {
        private NativeArray<Vertex> VertexBuffer;
        private NativeArray<ushort> IndexBuffer;
        private int VertexCount;
        private int IndexCount;

        private struct Vertex
        {
            public float3 pos;
            public float3 normal;
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

        [BurstCompile]
        public static void Run(in Chunk chunk, ref CreateMesh buffers)
        {
            var verts = buffers.VertexBuffer;
            var indices = buffers.IndexBuffer;
            int vertCount = 0;
            int indexCount = 0;

            var blocks = chunk.Blocks.AsReadOnlySpan();
            const int size = Chunk.Size;
            static byte GetBlock(in ReadOnlySpan<byte> blocks, int x, int y, int z)
            {
                return blocks[y * size * size + z * size + x];
            }

            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        byte block = GetBlock(blocks, x, y, z);

                        if (block == BlockType.Air)
                            continue;

                        // Skip side if neighbor block is solid (non see-through)
                        static bool IsNeighborSolid(in ReadOnlySpan<byte> blocks, int x, int y, int z)
                        {
                            // TODO: Check neighboring chunk if at the edge
                            if (y < 0 || y >= Chunk.Size)
                                return false;
                            if (z < 0 || z >= Chunk.Size)
                                return false;
                            if (x < 0 || x >= Chunk.Size)
                                return false;
                            return GetBlock(blocks, x, y, z) != BlockType.Air;
                        }

                        static void AddIndices(ref NativeArray<ushort> indices, ref int count, int vi)
                        {
                            indices[count++] = (ushort)(vi + 0);
                            indices[count++] = (ushort)(vi + 1);
                            indices[count++] = (ushort)(vi + 2);
                            indices[count++] = (ushort)(vi + 2);
                            indices[count++] = (ushort)(vi + 3);
                            indices[count++] = (ushort)(vi + 0);
                        }

                        static void AddVertex(ref NativeArray<Vertex> verts, ref int count, float x, float y, float z, in float3 normal)
                        {
                            verts[count++] = new()
                            {
                                pos = new(x, y, z),
                                normal = normal
                            };
                        }

                        float3 up = new(0, 1, 0);
                        float3 north = new(0, 0, 1);
                        float3 east = new(1, 0, 0);

                        // down y-
                        if (!IsNeighborSolid(blocks, x, y - 1, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, -up);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, -up);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, -up);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, -up);
                        }
                        // up y+
                        if (!IsNeighborSolid(blocks, x, y + 1, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, up);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, up);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, up);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, up);
                        }
                        // south z-
                        if (!IsNeighborSolid(blocks, x, y, z - 1))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, -north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, -north);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, -north);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, -north);
                        }
                        // north z+
                        if (!IsNeighborSolid(blocks, x, y, z + 1))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, north);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, north);
                        }
                        // west x-
                        if (!IsNeighborSolid(blocks, x - 1, y, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, -east);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, -east);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, -east);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, -east);
                        }
                        // east x+
                        if (!IsNeighborSolid(blocks, x + 1, y, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, east);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, east);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, east);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, east);
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
            var vbp = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            vbp[0] = new VertexAttributeDescriptor(VertexAttribute.Position);
            vbp[1] = new VertexAttributeDescriptor(VertexAttribute.Normal);
            meshData.SetVertexBufferParams(buffers.VertexCount, vbp);

            var pos = meshData.GetVertexData<Vertex>();
            // TODO: Why is CopyTo slow here?
            buffers.VertexBuffer.Slice(0, buffers.VertexCount).CopyTo(pos);

            meshData.SetIndexBufferParams(buffers.IndexCount, IndexFormat.UInt16);
            var ib = meshData.GetIndexData<ushort>();
            buffers.IndexBuffer.Slice(0, buffers.IndexCount).CopyTo(ib);

            meshData.subMeshCount = 1;
            var smd = new SubMeshDescriptor(0, ib.Length) { bounds = new((float3)(Chunk.Size / 2), (float3)Chunk.Size) };
            meshData.SetSubMesh(0, smd, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }
    }
}
