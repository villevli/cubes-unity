using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Cubes
{
    /// <summary>
    /// Loads chunks in a radius around the camera.
    /// Updates loaded chunks when the camera moves.
    /// Uses background threads to keep the framerate smooth.
    /// </summary>
    public class ChunkLoader : MonoBehaviour
    {
        [SerializeField]
        private int _viewDistance = 8;

        [SerializeField]
        private GenerateBlocks.Params _generatorOptions = GenerateBlocks.Params.Default;
        [SerializeField]
        private bool _useGPUCompute = true;
        [SerializeField]
        private ComputeShader _procGenShader;

        [SerializeField]
        private CreateMesh.Params _createMeshOptions = CreateMesh.Params.Default;
        // TODO: Support separate texture per cube side for each block type
        [SerializeField]
        private Texture2D[] _blockTextures = { null, null };
        [SerializeField]
        private GameObject _chunkPrefab;

        private Texture2D _atlas;
        private Material _atlasMaterial;

        private NativeArray<BlockType> _blockTypes;

        private struct RenderedChunk
        {
            public GameObject go;
            public Mesh mesh;
        }

        private Dictionary<int3, RenderedChunk> _renderedChunks = new();
        private NativeParallelHashMap<int3, Chunk> _chunkMap;

        private CancellationTokenSource _cts;

        private int3 _lastChunkPos = int.MinValue;

        public int TrackedChunkCount => _chunkMap.Count();
        public int LoadedChunkCount { get; private set; }
        public long BlocksInMemoryCount { get; private set; }
        public int MeshCount { get; private set; }
        public long MeshMemoryUsedBytes { get; private set; }

        public int LastChunksLoadedCount { get; private set; }
        public int LastChunksRenderedCount { get; private set; }
        public float LastChunkUpdateDurationMs { get; private set; }

        private void OnEnable()
        {
            // Application.targetFrameRate = 60;

            Init();
        }

        private void OnDisable()
        {
            Deinit();
        }

        private void Start()
        {
        }

        private void Update()
        {
            var camPos = Camera.main.transform.position;
            int3 currentChunkPos = (int3)math.floor((float3)camPos / Chunk.Size);
            if (!_lastChunkPos.Equals(currentChunkPos))
            {
                _lastChunkPos = currentChunkPos;
                UpdateChunksInRange(currentChunkPos, _cts.Token);
            }
        }

        private void Init()
        {
            CreateBlockTypesAtlas();

            _chunkMap = new(1024, Allocator.Persistent);

            _cts ??= new();
        }

        private void Deinit()
        {
            Unload();

            _chunkMap.Dispose();

            DestroyImmediate(_atlas);
            _blockTypes.Dispose();
        }

        public void Unload()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = new();

            foreach (var kv in _renderedChunks)
            {
                var chunk = kv.Value;
                DestroyImmediate(chunk.mesh);
                DestroyImmediate(chunk.go);
            }
            _renderedChunks.Clear();

            foreach (var item in _chunkMap)
            {
                item.Value.Dispose();
            }
            _chunkMap.Clear();
            LoadedChunkCount = 0;
            BlocksInMemoryCount = 0;

            _lastChunkPos = int.MinValue;
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

            _atlas = atlas;
            _atlasMaterial = _chunkPrefab.GetComponent<Renderer>().material;
            _atlasMaterial.mainTexture = atlas;
        }

        private async void UpdateChunksInRange(int3 currentChunkPos, CancellationToken cancellationToken) => await UpdateChunksInRangeAsync(currentChunkPos, cancellationToken);

        private async Awaitable UpdateChunksInRangeAsync(int3 currentChunkPos, CancellationToken cancellationToken)
        {
            long startTs = Stopwatch.GetTimestamp();

            Profiler.BeginSample("FindChunksToLoad");
            int3 viewDist = _viewDistance;
            int maxChunksInView = viewDist.x * viewDist.y * viewDist.z * 8;

            // TODO: Unload chunks that are out of range

            // FIXME: Use persistent alloc. There is no guarantee that this will take less than 4 frames
            var chunksToLoadBuf = new NativeArray<Chunk>(maxChunksInView, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            int chunksToLoadCount = 0;
            var chunksToRenderBuf = new NativeArray<Chunk>(maxChunksInView * 2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            int chunksToRenderCount = 0;

            void CheckNeighbor(int3 p)
            {
                if (_chunkMap.TryGetValue(p, out var nChunk) && nChunk.IsLoaded && !nChunk.IsPendingUpdate)
                    chunksToRenderBuf[chunksToRenderCount++] = nChunk;
            }

            for (int y = -viewDist.y; y < viewDist.y; y++)
            {
                for (int z = -viewDist.z; z < viewDist.z; z++)
                {
                    for (int x = -viewDist.x; x < viewDist.x; x++)
                    {
                        var p = currentChunkPos + new int3(x, y, z);

                        if (!_chunkMap.TryGetValue(p, out var chunk))
                            chunk = new Chunk(p);

                        if (!chunk.IsLoaded && !chunk.IsPendingUpdate)
                        {
                            chunk.IsPendingUpdate = true;
                            _chunkMap[p] = chunk;
                            chunksToLoadBuf[chunksToLoadCount++] = chunk;

                            // Update the mesh of neighboring chunks if they have been loaded already
                            CheckNeighbor(p + new int3(-1, 0, 0));
                            CheckNeighbor(p + new int3(1, 0, 0));
                            CheckNeighbor(p + new int3(0, -1, 0));
                            CheckNeighbor(p + new int3(0, 1, 0));
                            CheckNeighbor(p + new int3(0, 0, -1));
                            CheckNeighbor(p + new int3(0, 0, 1));
                        }
                    }
                }
            }
            Profiler.EndSample();

            if (chunksToLoadCount > 0)
            {
                var chunksToLoad = chunksToLoadBuf.GetSubArray(0, chunksToLoadCount);
                // Debug.Log($"Loading {chunksToLoad.Length} chunks around {currentChunkPos}");

                await GenerateChunksAsync(chunksToLoad, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    chunksToLoadBuf.Dispose();
                    chunksToRenderBuf.Dispose();
                    return;
                }

                Profiler.BeginSample("MarkLoadedChunks");
                for (int i = 0; i < chunksToLoad.Length; i++)
                {
                    var chunk = chunksToLoad[i];
                    chunk.IsPendingUpdate = false;
                    _chunkMap[chunk.Position] = chunk;
                    chunksToLoad[i] = chunk;
                    chunksToRenderBuf[chunksToRenderCount++] = chunk;

                    LoadedChunkCount += chunk.IsLoaded ? 1 : 0;
                    BlocksInMemoryCount += chunk.Blocks.Length;
                }
                Profiler.EndSample();

                Profiler.BeginSample("CreateChunkMeshes");
                var chunksToRender = chunksToRenderBuf.GetSubArray(0, chunksToRenderCount);
                CreateChunkMeshes(chunksToRender, cancellationToken);
                Profiler.EndSample();
            }

            chunksToLoadBuf.Dispose();
            chunksToRenderBuf.Dispose();

            LastChunksLoadedCount = chunksToLoadCount;
            LastChunksRenderedCount = chunksToRenderCount;
            LastChunkUpdateDurationMs = (float)TimeSpan.FromTicks(Stopwatch.GetTimestamp() - startTs).TotalMilliseconds;
        }

        private Awaitable GenerateChunksAsync(NativeArray<Chunk> chunks, CancellationToken cancellationToken)
        {
            if (_useGPUCompute && GenerateBlocksGPU.IsTypeSupported(_generatorOptions))
            {
                return GenerateChunksOnGPUAsync(chunks, _generatorOptions, cancellationToken);
            }
            else
            {
                return GenerateChunksCPUAsync(chunks, _generatorOptions, cancellationToken);
            }
        }

        private async Awaitable GenerateChunksOnGPUAsync(NativeArray<Chunk> chunks, GenerateBlocks.Params p, CancellationToken cancellationToken)
        {
            var buffers = new GenerateBlocksGPU(Allocator.TempJob);
            int generated = 0;
            while (!cancellationToken.IsCancellationRequested && generated < chunks.Length)
            {
                Profiler.BeginSample("GenerateBlocksGPU");
                var toGenerate = chunks.GetSubArray(generated, math.min(chunks.Length - generated, GenerateBlocksGPU.MaxChunksPerDispatch));
                var runAsync = GenerateBlocksGPU.RunAsync(toGenerate, buffers, p, _procGenShader);
                Profiler.EndSample();
                await runAsync;
                generated += toGenerate.Length;
            }
            buffers.Dispose();
        }

        private async Awaitable GenerateChunksCPUAsync(NativeArray<Chunk> chunks, GenerateBlocks.Params p, CancellationToken cancellationToken)
        {
            // TODO: Use multiple background threads
            await Awaitable.BackgroundThreadAsync();
            if (cancellationToken.IsCancellationRequested)
                return;

            var buffers = new GenerateBlocks(Allocator.TempJob);
            GenerateChunksCPU(chunks, buffers, p);
            buffers.Dispose();
            // await Awaitable.MainThreadAsync();
        }

        private void GenerateChunksCPU(NativeArray<Chunk> chunks, GenerateBlocks buffers, GenerateBlocks.Params p)
        {
            var span = chunks.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                ref var chunk = ref span[i];
                GenerateBlocks.Run(ref chunk, ref buffers, p);
            }
        }

        private void CreateChunkMeshes(NativeArray<Chunk> chunks, CancellationToken cancellationToken)
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                CreateChunkMesh(chunks[i], cancellationToken);
            }
        }

        private async void CreateChunkMesh(Chunk chunk, CancellationToken cancellationToken) => await CreateChunkMeshAsync(chunk, cancellationToken);

        private async Awaitable CreateChunkMeshAsync(Chunk chunk, CancellationToken cancellationToken)
        {
            Mesh.MeshDataArray dataArray = default;
            bool needsMesh = false;

            if (CreateMesh.NeedsMesh(chunk))
            {
                dataArray = Mesh.AllocateWritableMeshData(1);

                // TODO: Process multiple chunks instead of separate background task for every chunk

                await Awaitable.BackgroundThreadAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    dataArray.Dispose();
                    return;
                }

                Profiler.BeginSample("CreateMesh");
                var buffers = new CreateMesh(Allocator.TempJob);

                CreateMesh.Run(chunk, _chunkMap, _blockTypes, ref buffers, _createMeshOptions);

                needsMesh = buffers.VertexCount > 0;

                if (needsMesh)
                {
                    var meshData = dataArray[0];
                    CreateMesh.SetMeshData(buffers, ref meshData);
                }

                buffers.Dispose();
                Profiler.EndSample();

                await Awaitable.MainThreadAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    dataArray.Dispose();
                    return;
                }

                if (!needsMesh)
                {
                    dataArray.Dispose();
                }
            }

            Profiler.BeginSample("ApplyMesh");
            if (!_renderedChunks.TryGetValue(chunk.Position, out var rchunk))
                rchunk = new();

            Mesh mesh = rchunk.mesh;

            if (mesh is not null)
            {
                MeshCount--;
                MeshMemoryUsedBytes -= GetSizeOfMesh(mesh);
            }

            if (!needsMesh)
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

            if (mesh is not null)
            {
                Mesh.ApplyAndDisposeWritableMeshData(dataArray, mesh, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

                mesh.bounds = mesh.GetSubMesh(0).bounds;

                MeshCount++;
                MeshMemoryUsedBytes += GetSizeOfMesh(mesh);
            }

            GameObject go = rchunk.go;

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

            if (go is not null)
            {
                go.transform.position = (float3)(chunk.Position * Chunk.Size);
                go.transform.localScale = (float3)(Chunk.Size * 255 / 128f);
                go.GetComponent<MeshFilter>().mesh = mesh;
            }

            _renderedChunks[chunk.Position] = rchunk;
            Profiler.EndSample();
        }

        private static long GetSizeOfMesh(Mesh mesh)
        {
            return mesh is null ? 0 :
                   mesh.vertexCount * CreateMesh.SizeOfVertex
                 + mesh.GetIndexCount(0) * CreateMesh.SizeOfIndex;
        }
    }
}
