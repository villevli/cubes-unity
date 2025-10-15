using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Burst;
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
    [BurstCompile]
    public class ChunkLoader : MonoBehaviour
    {
        [SerializeField]
        private int _viewDistance = 8;

        [SerializeField]
        private GenerateBlocks.Params _generatorOptions = GenerateBlocks.Params.Default;
        [SerializeField]
        private bool _useGPUCompute = true;
        [SerializeField]
        [Range(1, GenerateBlocksGPU.MaxChunksPerDispatch)]
        private int _chunksPerDispatch = 512;
        [SerializeField]
        private ComputeShader _procGenShader;

        [SerializeField]
        private CreateMesh.Params _createMeshOptions = CreateMesh.Params.Default;
        // TODO: Support separate texture per cube side for each block type
        [SerializeField]
        private Texture2D[] _blockTextures = { null, null };
        [SerializeField]
        private GameObject _chunkPrefab;

        [SerializeField]
        private bool _cullChunks = true;
        [SerializeField]
        private bool _useGameObjects = false;

        private Texture2D _atlas;
        private Material _atlasMaterial;

        private NativeArray<BlockType> _blockTypes;

        private struct RenderedChunk
        {
            public UnityObjectRef<GameObject> go;
            public UnityObjectRef<Mesh> mesh;
            public long meshSizeBytes;
            public Matrix4x4 objectToWorld;
        }

        private NativeHashMap<int3, RenderedChunk> _renderedChunks;
        private NativeParallelHashMap<int3, Chunk> _chunkMap;
        private NativeParallelHashMap<int3, RenderableChunk> _renderMap;
        private DataPool<UnityObjectRef<Mesh>, MeshFactory> _meshPool;

        readonly struct MeshFactory : IDataFactory<UnityObjectRef<Mesh>>
        {
            public UnityObjectRef<Mesh> Allocate() => new Mesh();
            public void Free(in UnityObjectRef<Mesh> value) => DestroyImmediate(value);
        }

        private NativeArray<CullResult> _visibleChunks;

        private CancellationTokenSource _cts;
        private int _backgroundTaskCount = 0;

        private int3 _lastChunkPos = int.MinValue;
        private bool _isUpdatingChunks = false;

        public int TrackedChunkCount => _chunkMap.IsCreated ? _chunkMap.Count() : 0;
        public int LoadedChunkCount => _stats.LoadedChunkCount;
        public long BlocksInMemoryCount => _stats.BlocksInMemoryCount;
        public int MeshCount => _stats.MeshCount;
        public long MeshMemoryUsedBytes => _stats.MeshMemoryUsedBytes;

        private Stats _stats;

        private struct Stats
        {
            public int LoadedChunkCount;
            public long BlocksInMemoryCount;
            public int MeshCount;
            public long MeshMemoryUsedBytes;
        }

        public int LastChunksLoadedCount { get; private set; }
        public int LastChunksRenderedCount { get; private set; }
        public float LastChunkUpdateDurationMs { get; private set; }

        public int VisibleChunks { get; private set; }

        private void OnEnable()
        {
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
            if (!_lastChunkPos.Equals(currentChunkPos) && !_isUpdatingChunks)
            {
                _lastChunkPos = currentChunkPos;
                Profiler.BeginSample("UpdateChunksInRange");
                UpdateChunksInRange(currentChunkPos, _cts.Token);
                Profiler.EndSample();
            }

            if (_cullChunks)
            {
                Profiler.BeginSample("FindVisibleChunks");
                VisibleChunks = CullChunks.FindVisibleChunks(ref _visibleChunks, _renderMap, Camera.main, _viewDistance);
                Profiler.EndSample();
            }
            else
            {
                VisibleChunks = 0;
            }

            if (!_useGameObjects)
            {
                Profiler.BeginSample("SubmitVisibleMeshes");
                // FIXME: This is not practical due to the overhead. Use BatchRenderGroup?
                // FIXME: Shadows don't work properly. Shadow map rendering should be culled separately
                RenderParams rparams = new(_atlasMaterial)
                {
                    instanceID = GetInstanceID(),
                    receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.Off
                };
                if (_cullChunks)
                {
                    var visibleSpan = _visibleChunks.AsReadOnlySpan()[..VisibleChunks];
                    for (int i = 0; i < visibleSpan.Length; i++)
                    {
                        if (_renderedChunks.TryGetValue(visibleSpan[i].Pos, out var rchunk) && rchunk.mesh != default)
                            Graphics.RenderMesh(rparams, rchunk.mesh, 0, rchunk.objectToWorld);
                    }
                }
                else
                {
                    foreach (var item in _renderedChunks)
                    {
                        var rchunk = item.Value;
                        if (rchunk.mesh != default)
                            Graphics.RenderMesh(rparams, rchunk.mesh, 0, rchunk.objectToWorld);
                    }
                }
                Profiler.EndSample();
            }
        }

        private void OnDrawGizmos()
        {
            Gizmos.matrix = Matrix4x4.TRS((float3)(Chunk.Size / 2), Quaternion.identity, (float3)Chunk.Size);

            var cam = Camera.current;
            var cPos = (int3)math.floor((float3)cam.transform.position / Chunk.Size);
            Gizmos.color = Color.grey;
            Gizmos.DrawWireCube((float3)cPos, (float3)1);

            Gizmos.color = Color.green;
            for (int i = 0; i < VisibleChunks; i++)
            {
                var c = _visibleChunks[i];
                Gizmos.DrawLine((float3)(c.Pos + Chunk.FaceNormal(c.CameFromFace)), (float3)c.Pos);

                // Gizmos.DrawWireCube((float3)c.Pos, (float3)1);
            }
        }

        private void Init()
        {
            CreateBlockTypesAtlas();

            _chunkMap = new(1024, Allocator.Persistent);
            _renderedChunks = new(1024, Allocator.Persistent);
            _renderMap = new(1024, Allocator.Persistent);
            _meshPool = new(Allocator.Persistent);
            _visibleChunks = new(100000, Allocator.Persistent);

            _cts ??= new();
            UnityEngine.Debug.Assert(_backgroundTaskCount == 0);
        }

        private void Deinit()
        {
            Unload();

            _chunkMap.Dispose();
            _renderedChunks.Dispose();
            _renderMap.Dispose();
            _meshPool.Dispose();
            _visibleChunks.Dispose();

            DestroyImmediate(_atlas);
            _blockTypes.Dispose();
        }

        public void Unload()
        {
            Profiler.BeginSample("Unload");
            _cts.Cancel();
            _cts.Dispose();
            _cts = new();

            WaitBackgroundTasks();

            foreach (var kv in _renderedChunks)
            {
                var chunk = kv.Value;
                DestroyImmediate(chunk.mesh);
                DestroyImmediate(chunk.go);
            }
            _renderedChunks.Clear();
            _renderMap.Clear();
            _stats.MeshCount = 0;
            _stats.MeshMemoryUsedBytes = 0;

            _meshPool.Clear();

            foreach (var item in _chunkMap)
            {
                item.Value.Dispose();
            }
            _chunkMap.Clear();
            _stats.LoadedChunkCount = 0;
            _stats.BlocksInMemoryCount = 0;

            VisibleChunks = 0;

            _lastChunkPos = int.MinValue;
            Profiler.EndSample();
        }

        private void WaitBackgroundTasks()
        {
            while (_backgroundTaskCount > 0) { }
        }

        private struct BackgroundTaskScope : IDisposable
        {
            private ChunkLoader target;

            public BackgroundTaskScope(ChunkLoader target)
            {
                this.target = target;
                Interlocked.Increment(ref target._backgroundTaskCount);
            }

            public void Dispose()
            {
                if (target == null)
                    return;
                Interlocked.Decrement(ref target._backgroundTaskCount);
                target = null;
            }
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

        private async void UpdateChunksInRange(int3 currentChunkPos, CancellationToken cancellationToken)
        {
            if (_isUpdatingChunks)
                throw new InvalidOperationException("_isUpdatingChunks must be false");
            try
            {
                _isUpdatingChunks = true;
                await UpdateChunksInRangeAsync(currentChunkPos, cancellationToken);
            }
            finally
            {
                _isUpdatingChunks = false;
            }
        }

        private async Awaitable UpdateChunksInRangeAsync(int3 currentChunkPos, CancellationToken cancellationToken)
        {
            long startTs = Stopwatch.GetTimestamp();

            int3 viewDist = _viewDistance;
            int maxChunksInView = viewDist.x * viewDist.y * viewDist.z * 8;

            Profiler.BeginSample("AllocateBuffers");
            var chunksToLoadBuf = new NativeArray<Chunk>(maxChunksInView, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int chunksToLoadCount = 0;
            var chunksToRenderBuf = new NativeArray<Chunk>(maxChunksInView * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int chunksToRenderCount = 0;
            Profiler.EndSample();

            await Awaitable.BackgroundThreadAsync();
            using (new BackgroundTaskScope(this))
            {
                Profiler.BeginSample("ResetChunkFlags");
                ResetChunkFlags(ref _chunkMap);
                Profiler.EndSample();

                Profiler.BeginSample("FindChunksToLoad");
                FindChunksToLoad(ref _chunkMap, currentChunkPos, viewDist, ref chunksToLoadBuf, ref chunksToLoadCount, ref chunksToRenderBuf, ref chunksToRenderCount);
                Profiler.EndSample();
            }
            await Awaitable.MainThreadAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                chunksToLoadBuf.Dispose();
                chunksToRenderBuf.Dispose();
                return;
            }

            Profiler.BeginSample("UnloadOutOfRangeChunks");
            UnloadOutOfRangeChunks();
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

                    _stats.LoadedChunkCount += chunk.IsLoaded ? 1 : 0;
                    _stats.BlocksInMemoryCount += chunk.Blocks.Length;
                }
                Profiler.EndSample();

                var chunksToRender = chunksToRenderBuf.GetSubArray(0, chunksToRenderCount);
                await CreateChunkMeshesBatchedAsync(chunksToRender, cancellationToken);
            }

            chunksToLoadBuf.Dispose();
            chunksToRenderBuf.Dispose();

            LastChunksLoadedCount = chunksToLoadCount;
            LastChunksRenderedCount = chunksToRenderCount;
            LastChunkUpdateDurationMs = (float)TimeSpan.FromTicks(Stopwatch.GetTimestamp() - startTs).TotalMilliseconds;
        }

        [BurstCompile]
        private static void ResetChunkFlags(ref NativeParallelHashMap<int3, Chunk> chunkMap)
        {
            foreach (var item in chunkMap)
            {
                item.Value.IsInViewDistance = false;
            }
        }

        [BurstCompile]
        private static void FindChunksToLoad(
            ref NativeParallelHashMap<int3, Chunk> chunkMap, in int3 currentChunkPos, in int3 viewDist,
            ref NativeArray<Chunk> chunksToLoad, ref int chunksToLoadCount,
            ref NativeArray<Chunk> chunksToRender, ref int chunksToRenderCount
        )
        {
            var _chunkMap = chunkMap;
            var _chunksToRender = chunksToRender;
            int renderCount = 0;

            void CheckNeighbor(int3 p)
            {
                if (_chunkMap.TryGetValue(p, out var nChunk) && nChunk.IsLoaded && !nChunk.IsPendingUpdate)
                    _chunksToRender[renderCount++] = nChunk;
            }

            for (int y = -viewDist.y; y < viewDist.y; y++)
            {
                for (int z = -viewDist.z; z < viewDist.z; z++)
                {
                    for (int x = -viewDist.x; x < viewDist.x; x++)
                    {
                        var p = currentChunkPos + new int3(x, y, z);

                        if (!chunkMap.TryGetValue(p, out var chunk))
                            chunk = new Chunk(p);

                        chunk.IsInViewDistance = true;

                        if (!chunk.IsLoaded && !chunk.IsPendingUpdate)
                        {
                            chunk.IsPendingUpdate = true;
                            chunksToLoad[chunksToLoadCount++] = chunk;

                            // Update the mesh of neighboring chunks if they have been loaded already
                            CheckNeighbor(p + new int3(-1, 0, 0));
                            CheckNeighbor(p + new int3(1, 0, 0));
                            CheckNeighbor(p + new int3(0, -1, 0));
                            CheckNeighbor(p + new int3(0, 1, 0));
                            CheckNeighbor(p + new int3(0, 0, -1));
                            CheckNeighbor(p + new int3(0, 0, 1));
                        }

                        chunkMap[p] = chunk;
                    }
                }
            }

            chunksToRenderCount = renderCount;
        }

        private void UnloadOutOfRangeChunks()
        {
            var removedRChunks = _useGameObjects ? new NativeArray<RenderedChunk>(_chunkMap.Count(), Allocator.Temp) : default;
            int removedRChunksCount = 0;

            UnloadOutOfRangeChunks(_chunkMap, _renderedChunks, _renderMap, _meshPool,
                ref _stats,
                ref removedRChunks, ref removedRChunksCount);

            if (removedRChunks.IsCreated)
            {
                foreach (var rchunk in removedRChunks.GetSubArray(0, removedRChunksCount))
                {
                    DestroyImmediate(rchunk.go);
                }
                removedRChunks.Dispose();
            }
        }

        [BurstCompile]
        private static void UnloadOutOfRangeChunks(
            in NativeParallelHashMap<int3, Chunk> chunkMap,
            in NativeHashMap<int3, RenderedChunk> renderedChunks,
            in NativeParallelHashMap<int3, RenderableChunk> renderMap,
            in DataPool<UnityObjectRef<Mesh>, MeshFactory> meshPool,
            ref Stats stats,
            ref NativeArray<RenderedChunk> removedRChunks, ref int removedRChunksCount
            )
        {
            var toRemove = new NativeArray<int3>(chunkMap.Count(), Allocator.Temp);
            int toRemoveCount = 0;

            foreach (var item in chunkMap)
            {
                var pos = item.Key;
                ref var chunk = ref item.Value;

                if (chunk.IsInViewDistance)
                    continue;

                if (renderedChunks.TryGetValue(pos, out var rchunk))
                {
                    if (rchunk.mesh != default)
                    {
                        stats.MeshCount--;
                        stats.MeshMemoryUsedBytes -= rchunk.meshSizeBytes;

                        meshPool.Free(rchunk.mesh);
                        rchunk.mesh = default;
                        rchunk.meshSizeBytes = 0;
                    }
                    renderedChunks.Remove(pos);

                    if (removedRChunks.IsCreated)
                        removedRChunks[removedRChunksCount++] = rchunk;
                }

                renderMap.Remove(pos);

                stats.LoadedChunkCount -= chunk.IsLoaded ? 1 : 0;
                stats.BlocksInMemoryCount -= chunk.Blocks.Length;
                chunk.Dispose();

                toRemove[toRemoveCount++] = pos;
            }

            foreach (var item in toRemove.GetSubArray(0, toRemoveCount))
            {
                chunkMap.Remove(item);
            }
            toRemove.Dispose();
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
            var chunksPerDispatch = math.clamp(_chunksPerDispatch, 1, GenerateBlocksGPU.MaxChunksPerDispatch);
            var buffers = new GenerateBlocksGPU(Allocator.Persistent);
            var connectedFacesTasks = new List<Awaitable>();
            int generated = 0;
            while (!cancellationToken.IsCancellationRequested && generated < chunks.Length)
            {
                Profiler.BeginSample("GenerateBlocksGPU");
                var toGenerate = chunks.GetSubArray(generated, math.min(chunks.Length - generated, chunksPerDispatch));
                var runAsync = GenerateBlocksGPU.RunAsync(toGenerate, buffers, p, _procGenShader, cancellationToken);
                Profiler.EndSample();
                await runAsync;
                if (_cullChunks)
                    connectedFacesTasks.Add(CalculateConnectedFacesAsync(toGenerate));
                generated += toGenerate.Length;
            }
            buffers.Dispose();

            foreach (var item in connectedFacesTasks)
            {
                await item;
            }
        }

        private async Awaitable GenerateChunksCPUAsync(NativeArray<Chunk> chunks, GenerateBlocks.Params p, CancellationToken cancellationToken)
        {
            // TODO: Use multiple background threads
            await Awaitable.BackgroundThreadAsync();
            using (new BackgroundTaskScope(this))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var buffers = new GenerateBlocks(Allocator.Persistent);
                GenerateChunksCPU(chunks, buffers, p);
                buffers.Dispose();
            }

            if (_cullChunks)
                await CalculateConnectedFacesAsync(chunks);
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

        private async Awaitable CalculateConnectedFacesAsync(NativeArray<Chunk> chunks)
        {
            await Awaitable.BackgroundThreadAsync();
            using (new BackgroundTaskScope(this))
            {
                Profiler.BeginSample("CalculateConnectedFaces");
                CullChunks.CalculateConnectedFaces(chunks);
                Profiler.EndSample();
            }
        }

        // Each batch can potentially use a different background thread to speed up the process
        private async Awaitable CreateChunkMeshesBatchedAsync(NativeArray<Chunk> chunks, CancellationToken cancellationToken)
        {
            Profiler.BeginSample("CreateChunkMeshes");
            int batchSize = math.max(8, chunks.Length / 8);
            var processed = 0;
            var tasks = new List<Awaitable>();
            while (!cancellationToken.IsCancellationRequested && processed < chunks.Length)
            {
                var batch = chunks.GetSubArray(processed, math.min(chunks.Length - processed, batchSize));
                tasks.Add(CreateChunkMeshesAsync(batch, cancellationToken));
                processed += batch.Length;
            }
            Profiler.EndSample();
            foreach (var task in tasks)
            {
                await task;
            }
        }

        private class TimeSlicer
        {
            public TimeSpan Threshold;

            public int FrameNumber;
            public long Timestamp;

            public async Awaitable NextAsync()
            {
                while (FrameNumber == Time.frameCount && TimeSpan.FromTicks(Stopwatch.GetTimestamp() - Timestamp) > Threshold)
                    await Awaitable.NextFrameAsync();
                var frameNum = Time.frameCount;
                if (FrameNumber != frameNum)
                {
                    FrameNumber = frameNum;
                    Timestamp = Stopwatch.GetTimestamp();
                }
            }
        }

        private TimeSlicer _meshAllocTimeSlicer = new() { Threshold = TimeSpan.FromMilliseconds(1) };

        private async Awaitable CreateChunkMeshesAsync(NativeArray<Chunk> chunks, CancellationToken cancellationToken)
        {
            Mesh.MeshDataArray dataArray = default;

            Profiler.BeginSample("AllocateBuffer");
            NativeArray<Chunk> meshChunksBuf = new(chunks.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            Profiler.EndSample();
            int meshCount = 0;

            Profiler.BeginSample("CollectMeshChunks");
            CollectMeshChunks(chunks, ref meshChunksBuf, ref meshCount);
            Profiler.EndSample();

            var meshChunks = meshChunksBuf.GetSubArray(0, meshCount);
            if (meshChunks.Length > 0)
            {
                Profiler.BeginSample("AllocateWritableMeshData");
                dataArray = Mesh.AllocateWritableMeshData(meshChunks.Length);
                Profiler.EndSample();

                await Awaitable.BackgroundThreadAsync();
                using (new BackgroundTaskScope(this))
                {
                    Profiler.BeginSample("CreateMeshes");
                    CreateMeshes(meshChunks, dataArray, _chunkMap, _blockTypes, _createMeshOptions);
                    Profiler.EndSample();
                }
                await Awaitable.MainThreadAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    dataArray.Dispose();
                    meshChunksBuf.Dispose();
                    return;
                }

                await _meshAllocTimeSlicer.NextAsync();
            }

            Profiler.BeginSample("AllocateMeshes");
            var meshes = new Mesh[meshChunks.Length];
            AllocateMeshes(meshChunks, meshes);
            Profiler.EndSample();

            meshChunksBuf.Dispose();

            if (meshes.Length > 0)
            {
                Profiler.BeginSample("ApplyAndDisposeWritableMeshData");
                Mesh.ApplyAndDisposeWritableMeshData(dataArray, meshes, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
                Profiler.EndSample();
            }

            Profiler.BeginSample("ApplyMeshes");
            ApplyMeshes(chunks);
            Profiler.EndSample();
        }

        private void CollectMeshChunks(in NativeArray<Chunk> chunks, ref NativeArray<Chunk> meshChunksBuf, ref int meshCount)
        {
            CollectMeshChunks(chunks, ref meshChunksBuf, ref meshCount, ref _renderedChunks, ref _renderMap, _meshPool, ref _stats);
        }

        [BurstCompile]
        private static void CollectMeshChunks(in NativeArray<Chunk> chunks,
            ref NativeArray<Chunk> meshChunksBuf, ref int meshCount,
            ref NativeHashMap<int3, RenderedChunk> renderedChunks,
            ref NativeParallelHashMap<int3, RenderableChunk> renderMap,
            in DataPool<UnityObjectRef<Mesh>, MeshFactory> meshPool,
            ref Stats stats
        )
        {
            foreach (ref var chunk in chunks.AsSpan())
            {
                if (CreateMesh.NeedsMesh(chunk))
                {
                    meshChunksBuf[meshCount++] = chunk;
                }
                else
                {
                    // Release possible existing mesh
                    if (renderedChunks.TryGetValue(chunk.Position, out var rchunk) && rchunk.mesh != default)
                    {
                        stats.MeshCount--;
                        stats.MeshMemoryUsedBytes -= rchunk.meshSizeBytes;

                        meshPool.Free(rchunk.mesh);
                        rchunk.mesh = default;
                        rchunk.meshSizeBytes = 0;
                        renderedChunks[chunk.Position] = rchunk;
                        renderMap[chunk.Position] = new()
                        {
                            MeshId = default,
                            ConnectedFaces = ~0,
                        };
                    }
                }
            }
        }

        [BurstCompile]
        private static void CreateMeshes(in NativeArray<Chunk> meshChunks, in Mesh.MeshDataArray dataArray,
            in NativeParallelHashMap<int3, Chunk> chunkMap, in NativeArray<BlockType> blockTypes, in CreateMesh.Params p)
        {
            var buffers = new CreateMesh(Allocator.Persistent);
            for (int i = 0; i < meshChunks.Length; i++)
            {
                CreateMesh.Run(meshChunks[i], chunkMap, blockTypes, ref buffers, p);

                if (buffers.VertexCount > 0)
                {
                    var meshData = dataArray[i];
                    CreateMesh.SetMeshData(buffers, ref meshData);
                }
            }
            buffers.Dispose();
        }

        private void AllocateMeshes(in NativeArray<Chunk> meshChunks, Mesh[] meshes)
        {
            var meshChunksSpan = meshChunks.AsSpan();
            for (int i = 0; i < meshChunksSpan.Length; i++)
            {
                ref var chunk = ref meshChunksSpan[i];

                if (!_renderedChunks.TryGetValue(chunk.Position, out var rchunk))
                    rchunk = new();

                if (rchunk.mesh != default)
                {
                    _stats.MeshCount--;
                    _stats.MeshMemoryUsedBytes -= rchunk.meshSizeBytes;
                }

                Mesh mesh = rchunk.mesh;
                if (mesh == null)
                {
                    Profiler.BeginSample("NewMesh");
                    mesh = rchunk.mesh = _meshPool.Allocate();
                    mesh.name = "Chunk";
                    rchunk.meshSizeBytes = GetSizeOfMesh(mesh);
                    Profiler.EndSample();
                }
                meshes[i] = mesh;
                _renderedChunks[chunk.Position] = rchunk;

                Profiler.BeginSample("UpdateRenderHashMap");
                _renderMap[chunk.Position] = new()
                {
                    MeshId = rchunk.mesh,
                    ConnectedFaces = chunk.ConnectedFaces,
                };
                Profiler.EndSample();
            }
        }

        private void ApplyMeshes(in NativeArray<Chunk> chunks)
        {
            foreach (ref var chunk in chunks.AsSpan())
            {
                if (!_renderedChunks.TryGetValue(chunk.Position, out var rchunk))
                    rchunk = new();

                GameObject go = rchunk.go;
                Mesh mesh = rchunk.mesh;

                if (mesh?.vertexCount == 0)
                {
                    _meshPool.Free(rchunk.mesh);
                    rchunk.mesh = default;
                    rchunk.meshSizeBytes = 0;
                }

                if (rchunk.mesh == default)
                {
                    DestroyImmediate(go);
                    go = null;
                }
                else
                {
                    mesh.bounds = mesh.GetSubMesh(0).bounds;

                    rchunk.meshSizeBytes = GetSizeOfMesh(mesh);

                    _stats.MeshCount++;
                    _stats.MeshMemoryUsedBytes += rchunk.meshSizeBytes;

                    if (!_useGameObjects)
                    {
                        rchunk.objectToWorld = Matrix4x4.TRS(
                            (float3)(chunk.Position * Chunk.Size),
                            Quaternion.identity,
                            (float3)(Chunk.Size * 255 / 128f));
                    }
                    else if (go == null)
                    {
                        Profiler.BeginSample("InstantiatePrefab");
                        go = Instantiate(_chunkPrefab);
                        Profiler.EndSample();

                        Profiler.BeginSample("SetChunkName");
                        go.name = "Chunk" + chunk;
                        Profiler.EndSample();
                    }
                }
                rchunk.go = go;

                if (go is not null)
                {
                    Profiler.BeginSample("SetMeshTransform");
                    go.transform.position = (float3)(chunk.Position * Chunk.Size);
                    go.transform.localScale = (float3)(Chunk.Size * 255 / 128f);
                    go.GetComponent<MeshFilter>().mesh = rchunk.mesh;
                    Profiler.EndSample();
                }

                _renderedChunks[chunk.Position] = rchunk;
                Profiler.BeginSample("UpdateRenderHashMap");
                _renderMap[chunk.Position] = new()
                {
                    MeshId = rchunk.mesh,
                    ConnectedFaces = chunk.ConnectedFaces,
                };
                Profiler.EndSample();
            }
        }

        private static long GetSizeOfMesh(Mesh mesh)
        {
            if (mesh is null)
                return 0;
            long size = mesh.vertexCount * CreateMesh.SizeOfVertex;
            for (int i = 0; i < mesh.subMeshCount; i++)
                size += mesh.GetIndexCount(i) * CreateMesh.SizeOfIndex;
            return size;
        }
    }
}
