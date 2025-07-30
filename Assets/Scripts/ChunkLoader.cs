using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
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
        private int _viewDistance = 8;
        [SerializeField]
        private GameObject _chunkPrefab;
        [SerializeField]
        private GenerateBlocks.Params _generator = GenerateBlocks.Params.Default;

        private bool _isDirty;

        private struct LoadedChunk
        {
            public Chunk chunk;
            public GameObject go;
            public Mesh mesh;
        }

        private List<LoadedChunk> Chunks = new();
        private NativeParallelHashMap<int3, Chunk> ChunkMap;

        private void OnEnable()
        {
            Application.targetFrameRate = 60;
            _isDirty = false;

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

        private void OnValidate()
        {
            _isDirty = true;
        }

        private void Start()
        {
            SlowUpdate(destroyCancellationToken);
        }

        private async void SlowUpdate(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (isActiveAndEnabled && _isDirty)
                    Load();

                await Awaitable.WaitForSecondsAsync(0.1f);
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
                    ChunkMap = new(1024, Allocator.Persistent);
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
            if (!_isDirty)
                Debug.Log(Invariant($"Loaded {Chunks.Count} chunks in {totalMs}. {GetLoadedChunkStats()}, {timers}"));
            _isDirty = false;
        }

        private string GetLoadedChunkStats()
        {
            long blocks = 0;
            long meshes = 0;
            long verts = 0;
            long indices = 0;
            foreach (var chunk in Chunks)
            {
                blocks += chunk.chunk.Blocks.Length;
                if (chunk.mesh != null)
                {
                    meshes++;
                    verts += chunk.mesh.vertexCount;
                    indices += chunk.mesh.GetIndexCount(0);
                }
            }
            return $"{blocks} blocks, {meshes} meshes, {verts} verts, {indices} indices";
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
            ChunkMap.Dispose();
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
                        // Do not allocate block data at this point. Only when known if chunk has more than one block type
                        var chunk = new Chunk(new(x, y, z));
                        Chunks.Add(new()
                        {
                            chunk = chunk
                        });
                        ChunkMap.Add(chunk.Position, chunk);
                    }
                }
            }
        }

        private LoadedChunk GenerateChunk(LoadedChunk chunk, ref GenerateBlocks generateBlocks, TimerResults timers)
        {
            using (new TimerScope("gen", timers))
            {
                GenerateBlocks.Run(ref chunk.chunk, ref generateBlocks, _generator);
                ChunkMap[chunk.chunk.Position] = chunk.chunk;
            }
            return chunk;
        }

        private LoadedChunk CreateChunkMesh(LoadedChunk chunk, ref CreateMesh createMesh, TimerResults timers)
        {
            using (new TimerScope("mesh", timers))
            {
                CreateMesh.Run(chunk.chunk, ChunkMap, ref createMesh);
            }

            Mesh.MeshDataArray dataArray = default;

            using (new TimerScope("meshdata", timers))
            {
                if (createMesh.VertexCount > 0)
                {
                    dataArray = Mesh.AllocateWritableMeshData(1);
                    var meshData = dataArray[0];
                    CreateMesh.SetMeshData(createMesh, ref meshData);
                }
            }

            Mesh mesh = chunk.mesh;

            using (new TimerScope("newmesh", timers))
            {
                if (createMesh.VertexCount == 0)
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
                    go.transform.localScale = (float3)(Chunk.Size * 255 / 128f);
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

        public enum Type
        {
            Flat,
            Plane,
            SimplexNoise2D,
            PerlinNoise2D,
            SimplexNoise3D,
            PerlinNoise3D
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

            // If more than one block was used, use full palette so we don't have to modify the block data
            if (blockStateCount > 1)
                blockStateCount = palette.Length;

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
                chunk.Palette[0] = palette[blockBuffer[0]];

                chunk.Blocks.Dispose();
                chunk.Blocks = default;
            }
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
        public int VertexCount;
        public int IndexCount;

        private struct Vertex
        {
            public byte4 position;
            public sbyte4 normal;
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
        public static void Run(in Chunk chunk, in NativeParallelHashMap<int3, Chunk> chunks, ref CreateMesh buffers)
        {
            var verts = buffers.VertexBuffer;
            var indices = buffers.IndexBuffer;
            int vertCount = 0;
            int indexCount = 0;

            if (chunk.Palette.Length == 0 || (chunk.Palette.Length == 1 && chunk.Palette[0] == BlockType.Air))
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

            static void AddVertex(ref NativeArray<Vertex> verts, ref int count, int x, int y, int z, in sbyte4 normal)
            {
                verts[count++] = new()
                {
                    position = new(
                        (byte)(x * (128 / size)),
                        (byte)(y * (128 / size)),
                        (byte)(z * (128 / size))
                    // TODO: pack the normal in the w byte, use custom shader
                    ),
                    normal = normal
                };
            }

            sbyte4 down = new(0, -128, 0);
            sbyte4 up = new(0, 127, 0);
            sbyte4 south = new(0, 0, -128);
            sbyte4 north = new(0, 0, 127);
            sbyte4 west = new(-128, 0, 0);
            sbyte4 east = new(127, 0, 0);

            static bool IsBlockOpaque(in int block)
            {
                return block != BlockType.Air;
            }
            static bool IsOpaque(in ReadOnlySpan<byte> blocks, in ReadOnlySpan<int> palette, int x, int y, int z)
            {
                return IsBlockOpaque(GetBlockType(blocks, palette, x, y, z));
            }
            static bool IsNeighborOpaque(in Chunk chunk, int x, int y, int z)
            {
                // If neighboring chunk is not loaded, assume it's transparent so we'll see a wall if looking from outside
                if (!chunk.IsLoaded)
                    return false;
                if (chunk.Palette.Length == 1)
                    return IsBlockOpaque(chunk.Palette[0]);
                return IsOpaque(chunk.Blocks, chunk.Palette, x, y, z);
            }

            if (chunk.Palette.Length == 1)
            {
                // Iterate only the edges of the chunk when the chunk has only one block type
                for (int z = 0; z < size; z++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // down y-
                        if (!IsNeighborOpaque(chunkDown, x, size - 1, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, 0, z + 0, down);
                            AddVertex(ref verts, ref vertCount, x + 1, 0, z + 0, down);
                            AddVertex(ref verts, ref vertCount, x + 1, 0, z + 1, down);
                            AddVertex(ref verts, ref vertCount, x + 0, 0, z + 1, down);
                        }
                        // up y+
                        if (!IsNeighborOpaque(chunkUp, x, 0, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, size, z + 0, up);
                            AddVertex(ref verts, ref vertCount, x + 0, size, z + 1, up);
                            AddVertex(ref verts, ref vertCount, x + 1, size, z + 1, up);
                            AddVertex(ref verts, ref vertCount, x + 1, size, z + 0, up);
                        }
                    }
                }

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // south z-
                        if (!IsNeighborOpaque(chunkSouth, x, y, size - 1))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, 0, south);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, 0, south);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, 0, south);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, 0, south);
                        }
                        // north z+
                        if (!IsNeighborOpaque(chunkNorth, x, y, 0))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 0, size, north);
                            AddVertex(ref verts, ref vertCount, x + 1, y + 1, size, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 1, size, north);
                            AddVertex(ref verts, ref vertCount, x + 0, y + 0, size, north);
                        }
                    }
                }

                for (int y = 0; y < size; y++)
                {
                    for (int z = 0; z < size; z++)
                    {
                        // west x-
                        if (!IsNeighborOpaque(chunkWest, size - 1, y, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, 0, y + 0, z + 1, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 1, z + 1, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 1, z + 0, west);
                            AddVertex(ref verts, ref vertCount, 0, y + 0, z + 0, west);
                        }
                        // east x+
                        if (!IsNeighborOpaque(chunkEast, 0, y, z))
                        {
                            AddIndices(ref indices, ref indexCount, vertCount);
                            AddVertex(ref verts, ref vertCount, size, y + 0, z + 0, east);
                            AddVertex(ref verts, ref vertCount, size, y + 1, z + 0, east);
                            AddVertex(ref verts, ref vertCount, size, y + 1, z + 1, east);
                            AddVertex(ref verts, ref vertCount, size, y + 0, z + 1, east);
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

                            // Only add faces where the neighboring block is transparent
                            // IsNeighborOpaque checks in the neighboring chunk when at the edge of this chunk

                            // down y-
                            if (y > 0 ? !IsOpaque(blocks, palette, x, y - 1, z) : !IsNeighborOpaque(chunkDown, x, size - 1, z))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, down);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, down);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, down);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, down);
                            }
                            // up y+
                            if (y < size - 1 ? !IsOpaque(blocks, palette, x, y + 1, z) : !IsNeighborOpaque(chunkUp, x, 0, z))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, up);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, up);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, up);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, up);
                            }
                            // south z-
                            if (z > 0 ? !IsOpaque(blocks, palette, x, y, z - 1) : !IsNeighborOpaque(chunkSouth, x, y, size - 1))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, south);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, south);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 0, south);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 0, south);
                            }
                            // north z+
                            if (z < size - 1 ? !IsOpaque(blocks, palette, x, y, z + 1) : !IsNeighborOpaque(chunkNorth, x, y, 0))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 0, z + 1, north);
                                AddVertex(ref verts, ref vertCount, x + 1, y + 1, z + 1, north);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, north);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, north);
                            }
                            // west x-
                            if (x > 0 ? !IsOpaque(blocks, palette, x - 1, y, z) : !IsNeighborOpaque(chunkWest, size - 1, y, z))
                            {
                                AddIndices(ref indices, ref indexCount, vertCount);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 1, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 1, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 1, z + 0, west);
                                AddVertex(ref verts, ref vertCount, x + 0, y + 0, z + 0, west);
                            }
                            // east x+
                            if (x < size - 1 ? !IsOpaque(blocks, palette, x + 1, y, z) : !IsNeighborOpaque(chunkEast, 0, y, z))
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
            }

            buffers.VertexCount = vertCount;
            buffers.IndexCount = indexCount;
        }

        [BurstCompile]
        public static void SetMeshData(in CreateMesh buffers, ref Mesh.MeshData meshData)
        {
            var vbp = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            vbp[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.UNorm8, 4);
            vbp[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4);
            meshData.SetVertexBufferParams(buffers.VertexCount, vbp);

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
