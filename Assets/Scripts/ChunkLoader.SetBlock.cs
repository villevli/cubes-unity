using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Cubes
{
    public partial class ChunkLoader
    {
        public int3 GetChunkPos(int3 blockPos) => blockPos / Chunk.Size - (int3)(blockPos < 0);
        public int3 GetChunkLocalPos(int3 blockPos, int3 chunkPos) => blockPos - chunkPos * Chunk.Size;

        /// <summary>
        /// Set a block at <paramref name="position"/> to <paramref name="blockType"/> and update the mesh.
        /// </summary>
        public async Awaitable SetBlockAsync(int3 position, int blockType)
        {
            var cancellationToken = _cts.Token;
            if (_chunkUpdateAcs != null)
            {
                await _chunkUpdateAcs.Awaitable;
                if (cancellationToken.IsCancellationRequested)
                    return;
            }

            if (!SetBlock(position, blockType))
                return;

            // Rebuild mesh. Also 0-3 neighboring chunks depending if touching the edges
            var renderChunks = GetChunksSeenByBlock(position, Allocator.Persistent);
            // Debug.Log($"Set block at {position} to {blockType}. Rebuilding mesh of {renderChunks.Length} chunks");
            try
            {
                _isUpdatingChunks = true;
                await CreateChunkMeshesBatchedAsync(renderChunks, cancellationToken);
            }
            finally
            {
                _isUpdatingChunks = false;
                renderChunks.Dispose();
            }
        }

        private NativeArray<Chunk> GetChunksSeenByBlock(int3 position, Allocator allocator)
        {
            var chunkPos = GetChunkPos(position);
            var localBlock = GetChunkLocalPos(position, chunkPos);

            Span<Chunk> chunks = stackalloc Chunk[4];
            int chunksAdded = 0;

            if (_chunkMap.TryGetValue(chunkPos, out var chunk) && chunk.IsLoaded)
                chunks[chunksAdded++] = chunk;

            Chunk nChunk;
            if (localBlock.x == 0 && _chunkMap.TryGetValue(chunkPos + new int3(-1, 0, 0), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;
            else if (localBlock.x == Chunk.Size - 1 && _chunkMap.TryGetValue(chunkPos + new int3(1, 0, 0), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;

            if (localBlock.y == 0 && _chunkMap.TryGetValue(chunkPos + new int3(0, -1, 0), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;
            else if (localBlock.y == Chunk.Size - 1 && _chunkMap.TryGetValue(chunkPos + new int3(0, 1, 0), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;

            if (localBlock.z == 0 && _chunkMap.TryGetValue(chunkPos + new int3(0, 0, -1), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;
            else if (localBlock.z == Chunk.Size - 1 && _chunkMap.TryGetValue(chunkPos + new int3(0, 0, 1), out nChunk) && nChunk.IsLoaded)
                chunks[chunksAdded++] = nChunk;

            var array = new NativeArray<Chunk>(chunksAdded, allocator);
            for (int i = 0; i < chunksAdded; i++)
                array[i] = chunks[i];
            return array;
        }

        // Does not update mesh, only the chunk data
        private bool SetBlock(int3 position, int blockType)
        {
            var chunkPos = GetChunkPos(position);
            if (!_chunkMap.TryGetValue(chunkPos, out var chunk) || !chunk.IsLoaded)
            {
                Debug.LogError($"Failed to set block at {position}. Chunk {chunkPos} not loaded");
                return false;
            }

            if (chunk.Palette.Length == 1 && chunk.Palette[0] == blockType)
            {
                return false;
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
                // Debug.LogWarning($"Allocated blocks for chunk {chunkPos} because it no longer has a single block type");
                chunk.Blocks = new(Chunk.BlockCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                changed = true;
            }

            ref var block = ref Chunk.GetBlock(chunk.Blocks, GetChunkLocalPos(position, chunkPos));
            if (block == (byte)paletteIndex)
            {
                Debug.Assert(!changed);
                return false;
            }

            block = (byte)paletteIndex;

            if (_cullChunks)
            {
                var chunks = new NativeArray<Chunk>(1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                chunks[0] = chunk;
                CullChunks.CalculateConnectedFaces(chunks);
                chunk = chunks[0];
            }

            _chunkMap[chunkPos] = chunk;
            return true;
        }
    }
}
