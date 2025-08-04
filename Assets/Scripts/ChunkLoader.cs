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
    public class ChunkLoader : MonoBehaviour
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

        private Texture2D Atlas;
        private Material AtlasMaterial;

        private NativeArray<BlockType> BlockTypes;

        private struct LoadedChunk
        {
            public Chunk chunk;
            public GameObject go;
            public Mesh mesh;
        }

        private List<LoadedChunk> Chunks = new();
        private NativeParallelHashMap<int3, Chunk> ChunkMap;

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

            if (Atlas == null)
            {
                CreateBlockTypesAtlas();
            }

            GenerateBlocks generateBlocks;
            GenerateBlocksGPU generateBlocksGPU;
            CreateMeshPoints createMesh;
            using (new TimerScope("buffers", timers))
            {
                generateBlocks = new GenerateBlocks(Allocator.Temp);
                generateBlocksGPU = new GenerateBlocksGPU(Allocator.Temp);
                createMesh = new CreateMeshPoints(Allocator.Temp);
            }

            using (new TimerScope("create", timers))
            {
                if (Chunks.Count == 0)
                {
                    ChunkMap = new(1024, Allocator.Persistent);
                    CreateChunks();
                }
            }

            if (_useGPUCompute && GenerateBlocksGPU.IsTypeSupported(_generator))
            {
                GenerateChunksOnGPU(Chunks, ref generateBlocksGPU, _generator, timers);
            }
            else if (_useMultipleThreads)
            {
                GenerateChunksMultithreaded(Chunks, _generator, timers);
            }
            else
            {
                GenerateChunks(Chunks, ref generateBlocks, _generator, timers);
            }

            CreateChunkMeshes(Chunks, ref createMesh, timers);

            var totalMs = totalTime.ResultToString();
            if (!_isDirty)
                Debug.Log(Invariant($"Loaded {Chunks.Count} chunks in {totalMs}. {GetLoadedChunkStats()}, {timers}"));
            _isDirty = false;

            generateBlocks.Dispose();
            generateBlocksGPU.Dispose();
            createMesh.Dispose();
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

            DestroyImmediate(Atlas);
            BlockTypes.Dispose();
        }

        private void CreateBlockTypesAtlas()
        {
            // Fit max 256x256 of 16x16 textures
            var atlas = new Texture2D(16 * 256, 16 * 256, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point
            };

            var rects = atlas.PackTextures(_blockTextures, 0, 16 * 256);

            BlockTypes = new(rects.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < BlockTypes.Length; i++)
            {
                BlockTypes[i] = new()
                {
                    TexAtlasRect = rects[i],
                };
                // Debug.Log($"Packed {_blockTextures[i]} into {BlockTypes[i].TexAtlasRect}");
            }

            Atlas = atlas;
            AtlasMaterial = _chunkPrefab.GetComponent<Renderer>().material;
            AtlasMaterial.mainTexture = atlas;
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

        private void GenerateChunks(List<LoadedChunk> chunks, ref GenerateBlocks generateBlocks, in GenerateBlocks.Params p, TimerResults timers)
        {
            using (new TimerScope("gen", timers))
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    var chunk = chunks[i];
                    GenerateBlocks.Run(ref chunk.chunk, ref generateBlocks, p);
                    chunks[i] = chunk;
                    ChunkMap[chunk.chunk.Position] = chunk.chunk;
                }
            }
        }

        private void GenerateChunksMultithreaded(List<LoadedChunk> chunks, GenerateBlocks.Params p, TimerResults timers)
        {
            const int threads = 8;

            using (new TimerScope("gen", timers))
            {
                var tasks = new Task[threads];

                for (int i = 0; i < threads; i++)
                {
                    int count = chunks.Count / threads;
                    int startIndex = i * count;
                    tasks[i] = Task.Run(() =>
                    {
                        var buffers = new GenerateBlocks(Allocator.TempJob);
                        for (int i = startIndex; i < startIndex + count; i++)
                        {
                            var chunk = chunks[i];
                            GenerateBlocks.Run(ref chunk.chunk, ref buffers, p);
                            chunks[i] = chunk;
                            ChunkMap[chunk.chunk.Position] = chunk.chunk;
                        }
                        buffers.Dispose();
                    });
                }

                Task.WaitAll(tasks);
            }
        }

        private void GenerateChunksOnGPU(List<LoadedChunk> chunks, ref GenerateBlocksGPU generateBlocks, in GenerateBlocks.Params p, TimerResults timers)
        {
            var chunksArray = new NativeArray<Chunk>(chunks.Count, Allocator.Temp);

            for (int i = 0; i < chunks.Count; i++)
            {
                chunksArray[i] = chunks[i].chunk;
            }

            GenerateBlocksGPU.Run(ref chunksArray, ref generateBlocks, p, _procGenShader, timers);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                chunk.chunk = chunksArray[i];
                chunks[i] = chunk;

                ChunkMap[chunk.chunk.Position] = chunk.chunk;
            }
        }

        private void CreateChunkMeshes(List<LoadedChunk> chunks, ref CreateMeshPoints createMesh, TimerResults timers)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i] = CreateChunkMesh(chunks[i], ref createMesh, timers);
            }
        }

        private LoadedChunk CreateChunkMesh(LoadedChunk chunk, ref CreateMeshPoints createMesh, TimerResults timers)
        {
            using (new TimerScope("mesh", timers))
            {
                CreateMeshPoints.Run(chunk.chunk, ChunkMap, BlockTypes, ref createMesh, _createMesh);
            }

            Mesh.MeshDataArray dataArray = default;

            using (new TimerScope("meshdata", timers))
            {
                if (createMesh.VertexCount > 0)
                {
                    dataArray = Mesh.AllocateWritableMeshData(1);
                    var meshData = dataArray[0];
                    CreateMeshPoints.SetMeshData(createMesh, ref meshData);
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
                    // go.transform.localScale = (float3)(Chunk.Size * 255 / 128f);
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
}
