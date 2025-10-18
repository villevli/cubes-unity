using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Cubes
{
    public partial class ChunkLoader
    {
        public static int3 GetChunkPos(int3 blockPos) => Math.DivFloor(blockPos, Chunk.Size);
        public static int3 GetChunkPosCeil(int3 blockPos) => Math.DivCeil(blockPos, Chunk.Size);
        public static int3 GetChunkLocalPos(int3 blockPos, int3 chunkPos) => blockPos - chunkPos * Chunk.Size;

        /// <summary>
        /// Set a block(s) inside the box defined by <paramref name="position"/> and <paramref name="size"/> to <paramref name="blockType"/> and update the mesh.
        /// </summary>
        public async Awaitable SetBlockAsync(int3 position, int3 size, int blockType)
        {
            var cancellationToken = _cts.Token;
            if (_chunkUpdateAcs != null)
            {
                await _chunkUpdateAcs.Awaitable;
                if (cancellationToken.IsCancellationRequested)
                    return;
            }

            if (!SetBlock(position, size, blockType))
                return;

            // Rebuild meshes. Also neighboring chunks depending if touching the edges
            NativeArray<Chunk> renderChunksBuf = default;
            var renderChunksCount = GetChunksSeenByBlock(position, size, Allocator.Persistent, ref renderChunksBuf, _chunkMap);
            var renderChunks = renderChunksBuf.GetSubArray(0, renderChunksCount);
            Debug.Log($"Set blocks at {position} size {size} to {blockType}. Rebuilding mesh of {renderChunks.Length} chunks");
            try
            {
                // FIXME: Breaks if SetBlockAsync is called again
                _isUpdatingChunks = true;
                await CreateChunkMeshesBatchedAsync(renderChunks, cancellationToken);
            }
            finally
            {
                _isUpdatingChunks = false;
                renderChunksBuf.Dispose();
            }
        }

        // Does not update mesh, only the chunk data
        private bool SetBlock(int3 position, int3 size, int blockType)
        {
            return SetBlock(position, size, blockType, ref _chunkMap, _cullChunks);
        }

        [BurstCompile]
        private static bool SetBlock(in int3 position, in int3 size, int blockType,
            ref NativeParallelHashMap<int3, Chunk> chunkMap, bool cullChunks)
        {
            if (math.any(size < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(size), "must be positive");
            }

            var minBlock = position;
            var maxBlock = position + size;
            var minChunk = GetChunkPos(minBlock);
            var maxChunk = GetChunkPosCeil(maxBlock);

            var maxNumChunks = maxChunk - minChunk;
            var chunksBuf = new NativeArray<Chunk>(maxNumChunks.x * maxNumChunks.y * maxNumChunks.z, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var chunksCount = 0;

            // Debug.Log($"min {minChunk} max {maxChunk} maxChunks {maxNumChunks}");

            for (int chunkY = minChunk.y; chunkY < maxChunk.y; chunkY++)
            {
                for (int chunkZ = minChunk.z; chunkZ < maxChunk.z; chunkZ++)
                {
                    for (int chunkX = minChunk.x; chunkX < maxChunk.x; chunkX++)
                    {
                        var chunkPos = new int3(chunkX, chunkY, chunkZ);
                        if (!chunkMap.TryGetValue(chunkPos, out var chunk) || !chunk.IsLoaded)
                        {
                            // TODO: generate chunk if needed
                            Debug.LogError($"Failed to set blocks in chunk {chunkPos}. Chunk not loaded");
                            continue;
                        }

                        if (!SetBlocks(ref chunk, minBlock, maxBlock, blockType))
                            continue;

                        // Debug.Log($"Set blocks in {chunk.Position} to {blockType}.");

                        chunksBuf[chunksCount++] = chunk;
                    }
                }
            }

            var chunks = chunksBuf.GetSubArray(0, chunksCount);
            if (cullChunks)
                CullChunks.CalculateConnectedFaces(chunks);

            foreach (ref var chunk in chunks.AsSpan())
            {
                chunkMap[chunk.Position] = chunk;
            }
            chunksBuf.Dispose();
            return chunksCount > 0;
        }

        [BurstCompile]
        private static bool SetBlocks(ref Chunk chunk, in int3 minBlock, in int3 maxBlock, int blockType)
        {
            if (chunk.Palette.Length == 1 && chunk.Palette[0] == blockType)
            {
                return false;
            }

            var minBlockLocal = math.clamp(GetChunkLocalPos(minBlock, chunk.Position), 0, Chunk.Size);
            var maxBlockLocal = math.clamp(GetChunkLocalPos(maxBlock, chunk.Position), 0, Chunk.Size);

            // If chunk is completely covered, set single element Palette and free the Blocks
            if (minBlockLocal.Equals(0) && maxBlockLocal.Equals(Chunk.Size))
            {
                chunk.Palette.Dispose();
                chunk.Palette = new(1, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                chunk.Palette[0] = blockType;
                chunk.Blocks.Dispose();
                return true;
            }

            int paletteIndex = -1;
            for (int i = 0; i < chunk.Palette.Length; i++)
            {
                if (chunk.Palette[i] == blockType)
                {
                    paletteIndex = i;
                    break;
                }
            }

            bool changed = false;
            if (paletteIndex == -1)
            {
                chunk.Palette.ResizeArray(chunk.Palette.Length + 1);
                paletteIndex = chunk.Palette.Length - 1;
                chunk.Palette[paletteIndex] = blockType;
                changed = true;
            }
            if (!chunk.Blocks.IsCreated)
            {
                // Debug.LogWarning($"Allocated blocks for chunk {chunk.Position} because it no longer has a single block type");
                chunk.Blocks = new(Chunk.BlockCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                changed = true;
            }

            for (int y = minBlockLocal.y; y < maxBlockLocal.y; y++)
            {
                for (int z = minBlockLocal.z; z < maxBlockLocal.z; z++)
                {
                    for (int x = minBlockLocal.x; x < maxBlockLocal.x; x++)
                    {
                        ref var block = ref Chunk.GetBlock(chunk.Blocks, x, y, z);
                        changed |= block != paletteIndex;
                        block = (byte)paletteIndex;
                    }
                }
            }

            return changed;
        }

        // TODO: Could probably run this as part of the SetBlock function where we already iterate the chunks to reduce map lookups
        [BurstCompile]
        private static int GetChunksSeenByBlock(in int3 position, in int3 size,
            Allocator allocator, ref NativeArray<Chunk> chunks,
            in NativeParallelHashMap<int3, Chunk> chunkMap)
        {
            var minBlock = position;
            var maxBlock = position + size;
            var minChunk = GetChunkPos(minBlock);
            var maxChunk = GetChunkPosCeil(maxBlock);

            var maxNumChunks = maxChunk - minChunk + 2;
            chunks = new NativeArray<Chunk>(maxNumChunks.x * maxNumChunks.y * maxNumChunks.z, allocator, NativeArrayOptions.UninitializedMemory);
            int chunksAdded = 0;

            for (int chunkY = minChunk.y; chunkY < maxChunk.y; chunkY++)
            {
                for (int chunkZ = minChunk.z; chunkZ < maxChunk.z; chunkZ++)
                {
                    for (int chunkX = minChunk.x; chunkX < maxChunk.x; chunkX++)
                    {
                        var chunkPos = new int3(chunkX, chunkY, chunkZ);
                        if (!chunkMap.TryGetValue(chunkPos, out var chunk) || !chunk.IsLoaded)
                        {
                            continue;
                        }

                        chunks[chunksAdded++] = chunk;

                        // Check neighbors
                        var minBlockLocal = math.clamp(GetChunkLocalPos(minBlock, chunkPos), 0, Chunk.Size);
                        var maxBlockLocal = math.clamp(GetChunkLocalPos(maxBlock, chunkPos), 0, Chunk.Size);

                        Chunk nChunk;
                        if (chunkPos.x == minChunk.x && minBlockLocal.x == 0
                         && chunkMap.TryGetValue(chunkPos + new int3(-1, 0, 0), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }
                        else if (chunkPos.x == maxChunk.x - 1 && maxBlockLocal.x == Chunk.Size
                              && chunkMap.TryGetValue(chunkPos + new int3(1, 0, 0), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }

                        if (chunkPos.y == minChunk.y && minBlockLocal.y == 0
                         && chunkMap.TryGetValue(chunkPos + new int3(0, -1, 0), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }
                        else if (chunkPos.y == maxChunk.y - 1 && maxBlockLocal.y == Chunk.Size
                              && chunkMap.TryGetValue(chunkPos + new int3(0, 1, 0), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }

                        if (chunkPos.z == minChunk.z && minBlockLocal.z == 0
                         && chunkMap.TryGetValue(chunkPos + new int3(0, 0, -1), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }
                        else if (chunkPos.z == maxChunk.z - 1 && maxBlockLocal.z == Chunk.Size
                              && chunkMap.TryGetValue(chunkPos + new int3(0, 0, 1), out nChunk) && nChunk.IsLoaded)
                        {
                            chunks[chunksAdded++] = nChunk;
                        }
                    }
                }
            }

            return chunksAdded;
        }
    }
}
