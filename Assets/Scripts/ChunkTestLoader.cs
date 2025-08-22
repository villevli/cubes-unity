using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static System.FormattableString;

namespace Cubes
{
    /// <summary>
    /// Loads a fixed number of chunks around the origin for profiling and testing.
    /// </summary>
    public class ChunkTestLoader : MonoBehaviour
    {
        [SerializeField]
        private int _viewDistance = 8;
        [SerializeField]
        private GameObject _chunkPrefab;
        [SerializeField]
        private GenerateBlocks.Params _generator = GenerateBlocks.Params.Default;

        [SerializeField]
        private bool _useMultipleThreads = true;

        [SerializeField]
        private bool _useGPUCompute = true;
        [SerializeField]
        private ComputeShader _procGenShader;

        [SerializeField]
        private CreateMesh.Params _createMesh = CreateMesh.Params.Default;

        // TODO: Support separate texture per cube side for each block type
        [SerializeField]
        private Texture2D[] _blockTextures = { null, null };

        private Texture2D _atlasTex;
        private Material _atlasMaterial;

        private NativeArray<BlockType> _blockTypes;

        private struct RenderedChunk
        {
            public GameObject go;
            public Mesh mesh;
        }

        private Dictionary<int3, RenderedChunk> _renderedChunks = new();
        private NativeParallelHashMap<int3, Chunk> _chunkMap;

        private bool _isDirty;

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

                await Awaitable.WaitForSecondsAsync(0.02f);
            }
        }

        private void Load()
        {
            TimerResults timers = new();
            var totalTime = new TimerScope("total", null);

            if (_atlasTex == null)
            {
                CreateBlockTypesAtlas();
            }

            if (!_chunkMap.IsCreated)
            {
                _chunkMap = new(4096, Allocator.Persistent);
            }

            NativeArray<Chunk> chunksBuf;
            GenerateBlocks generateBlocks;
            GenerateBlocksGPU generateBlocksGPU;
            CreateMesh createMesh;
            using (new TimerScope("buffers", timers))
            {
                chunksBuf = new(4096, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                generateBlocks = new GenerateBlocks(Allocator.Temp);
                generateBlocksGPU = new GenerateBlocksGPU(Allocator.Temp);
                createMesh = new CreateMesh(Allocator.Temp);
            }

            NativeArray<Chunk> chunks;
            using (new TimerScope("create", timers))
            {
                int chunkCount = 0;
                CreateChunks(_viewDistance, ref chunksBuf, ref chunkCount);
                chunks = chunksBuf.GetSubArray(0, chunkCount);
            }

            if (_useGPUCompute && GenerateBlocksGPU.IsTypeSupported(_generator))
            {
                GenerateChunksOnGPU(chunks, ref generateBlocksGPU, _generator, timers);
            }
            else if (_useMultipleThreads)
            {
                GenerateChunksMultithreaded(chunks, _generator, timers);
            }
            else
            {
                GenerateChunks(chunks, ref generateBlocks, _generator, timers);
            }

            using (new TimerScope("fill", timers))
            {
                CullChunks.CalculateConnectedFaces(chunks);
            }

            using (new TimerScope("map", timers))
            {
                Span<Chunk> span = chunks;
                for (int i = 0; i < span.Length; i++)
                {
                    ref var chunk = ref span[i];
                    _chunkMap[chunk.Position] = chunk;
                }
            }

            CreateChunkMeshes(chunks, ref createMesh, timers);

            var totalMs = totalTime.ResultToString();
            if (!_isDirty)
                Debug.Log(Invariant($"Loaded {chunks.Length} chunks in {totalMs}. {GetLoadedChunkStats(chunks)}, {timers}"));
            _isDirty = false;

            chunksBuf.Dispose();
            generateBlocks.Dispose();
            generateBlocksGPU.Dispose();
            createMesh.Dispose();
        }

        private string GetLoadedChunkStats(in NativeArray<Chunk> chunks)
        {
            long blocks = 0;
            long meshes = 0;
            long verts = 0;
            long indices = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                blocks += chunks[i].Blocks.Length;
                if (_renderedChunks.TryGetValue(chunks[i].Position, out var rchunk) && rchunk.mesh is not null)
                {
                    meshes++;
                    verts += rchunk.mesh.vertexCount;
                    indices += rchunk.mesh.GetIndexCount(0);
                }
            }
            return $"{blocks} blocks, {meshes} meshes, {verts} verts, {indices} indices";
        }

        private void Unload()
        {
            foreach (var chunk in _renderedChunks)
            {
                var rchunk = chunk.Value;
                DestroyImmediate(rchunk.mesh);
                DestroyImmediate(rchunk.go);
            }
            _renderedChunks.Clear();

            foreach (var item in _chunkMap)
            {
                item.Value.Dispose();
            }
            _chunkMap.Dispose();

            DestroyImmediate(_atlasTex);
            _blockTypes.Dispose();
        }

        private void CreateBlockTypesAtlas()
        {
            // Fit max 256x256 of 16x16 textures
            var atlas = new Texture2D(16 * 256, 16 * 256, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point
            };

            var rects = atlas.PackTextures(_blockTextures, 0, 16 * 256);

            _blockTypes = new(rects.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < _blockTypes.Length; i++)
            {
                _blockTypes[i] = new()
                {
                    TexAtlasRect = rects[i],
                };
                // Debug.Log($"Packed {_blockTextures[i]} into {BlockTypes[i].TexAtlasRect}");
            }

            _atlasTex = atlas;
            _atlasMaterial = _chunkPrefab.GetComponent<Renderer>().material;
            _atlasMaterial.mainTexture = atlas;
        }

        private void CreateChunks(in int viewDistance, ref NativeArray<Chunk> result, ref int count)
        {
            int3 viewDist = viewDistance;
            count = 0;
            for (int y = -viewDist.y; y < viewDist.y; y++)
            {
                for (int z = -viewDist.z; z < viewDist.z; z++)
                {
                    for (int x = -viewDist.x; x < viewDist.x; x++)
                    {
                        var p = new int3(x, y, z);
                        if (!_chunkMap.TryGetValue(p, out var chunk))
                        {
                            chunk = new Chunk(p);
                            _chunkMap.Add(p, chunk);
                        }

                        result[count++] = chunk;
                    }
                }
            }
        }

        private void GenerateChunks(in NativeArray<Chunk> chunks, ref GenerateBlocks generateBlocks, in GenerateBlocks.Params p, TimerResults timers)
        {
            using (new TimerScope("gen", timers))
            {
                Span<Chunk> span = chunks;
                for (int i = 0; i < span.Length; i++)
                {
                    GenerateBlocks.Run(ref span[i], ref generateBlocks, p);
                }
            }
        }

        private void GenerateChunksMultithreaded(in NativeArray<Chunk> chunks, GenerateBlocks.Params p, TimerResults timers)
        {
            const int threads = 8;

            using (new TimerScope("gen", timers))
            {
                var tasks = new Task[threads];

                for (int i = 0; i < threads; i++)
                {
                    int count = chunks.Length / threads;
                    int startIndex = i * count;
                    var tchunks = chunks;
                    tasks[i] = Task.Run(() =>
                    {
                        var buffers = new GenerateBlocks(Allocator.TempJob);
                        Span<Chunk> span = tchunks;
                        for (int i = startIndex; i < startIndex + count; i++)
                        {
                            GenerateBlocks.Run(ref span[i], ref buffers, p);
                        }
                        buffers.Dispose();
                    });
                }

                Task.WaitAll(tasks);
            }
        }

        private void GenerateChunksOnGPU(in NativeArray<Chunk> chunks, ref GenerateBlocksGPU generateBlocks, in GenerateBlocks.Params p, TimerResults timers)
        {
            GenerateBlocksGPU.Run(chunks, ref generateBlocks, p, _procGenShader, timers);
        }

        private void CreateChunkMeshes(in NativeArray<Chunk> chunks, ref CreateMesh createMesh, TimerResults timers)
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                CreateChunkMesh(chunks[i], ref createMesh, timers);
            }
        }

        private void CreateChunkMesh(in Chunk chunk, ref CreateMesh createMesh, TimerResults timers)
        {
            using (new TimerScope("mesh", timers))
            {
                CreateMesh.Run(chunk, _chunkMap, _blockTypes, ref createMesh, _createMesh);
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

            if (!_renderedChunks.TryGetValue(chunk.Position, out var rchunk))
                rchunk = new();

            Mesh mesh = rchunk.mesh;

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
                rchunk.mesh = mesh;
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

            GameObject go = rchunk.go;

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
                    go.name = "Chunk" + chunk;
                }
                rchunk.go = go;
            }
            using (new TimerScope("pos", timers))
            {
                if (go is not null)
                {
                    go.transform.position = (float3)(chunk.Position * Chunk.Size);
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

            _renderedChunks[chunk.Position] = rchunk;
        }
    }
}
