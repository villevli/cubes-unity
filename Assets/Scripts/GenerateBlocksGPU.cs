using System;
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
    /// Procedurally generate the blocks in a chunk. Allocates required buffers.
    /// Uses GPU compute shaders.
    /// </summary>
    [BurstCompile]
    public struct GenerateBlocksGPU : IDisposable
    {
        const int size = Chunk.Size;

        public const int MaxChunksPerDispatch = 4096;
        private ComputeBuffer ChunkMinCBuffer;
        private ComputeBuffer ResultCBuffer;
        private NativeArray<int> ResultReadbackBuffer;

        public GenerateBlocksGPU(Allocator allocator)
        {
            ChunkMinCBuffer = new(MaxChunksPerDispatch, 3 * 4, ComputeBufferType.Default, ComputeBufferMode.SubUpdates);
            // A block is one byte so we store 4 blocks per buffer element.
            ResultCBuffer = new(MaxChunksPerDispatch * size * size * size / 4, 4);
            ResultReadbackBuffer = new(ResultCBuffer.count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        public void Dispose()
        {
            ChunkMinCBuffer.Dispose();
            ResultCBuffer.Dispose();
            ResultReadbackBuffer.Dispose();
        }

        public static bool IsTypeSupported(in GenerateBlocks.Params p)
        {
            return GetKernelName(p) != null;
        }

        private static string GetKernelName(in GenerateBlocks.Params p)
        {
            if (p.Type == GenerateBlocks.Type.PerlinNoise3D)
                return "ProcGenPerlin3D";
            if (p.Type == GenerateBlocks.Type.SimplexNoise3D)
                return "ProcGenSimplex3D";
            if (p.Type == GenerateBlocks.Type.CustomTerrain)
                return "ProcGenCustomTerrain";
            return null;
        }

        private static GenerateBlocks.Params AdjustDefaultParams(GenerateBlocks.Params p)
        {
            if (p.Type == GenerateBlocks.Type.PerlinNoise3D)
            {
                p.Scale *= 0.05f;
            }
            else if (p.Type == GenerateBlocks.Type.SimplexNoise3D)
            {
                p.Scale *= 0.025f;
                p.Scale2 *= 0.5f;
            }
            return p;
        }

        public static void Run(in NativeArray<Chunk> chunks, ref GenerateBlocksGPU buffers, GenerateBlocks.Params p, ComputeShader shader, in ChunkDataPool pool, TimerResults timers)
        {
            Dispatch(chunks, ref buffers, p, shader, timers);

            using (new TimerScope("readback", timers))
            {
                var readback = AsyncGPUReadback.RequestIntoNativeArray(ref buffers.ResultReadbackBuffer, buffers.ResultCBuffer, chunks.Length * size * size * size, 0);
                readback.WaitForCompletion();
            }

            using (new TimerScope("copy", timers))
            {
                CopyResultToChunks(chunks, pool, buffers.ResultReadbackBuffer);
            }
        }

        public static async Awaitable RunAsync(NativeArray<Chunk> chunks, GenerateBlocksGPU buffers, GenerateBlocks.Params p, ComputeShader shader, ChunkDataPool pool, CancellationToken cancellationToken)
        {
            Profiler.BeginSample("Dispatch");
            Dispatch(chunks, ref buffers, p, shader, null);
            Profiler.EndSample();

            await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref buffers.ResultReadbackBuffer, buffers.ResultCBuffer, chunks.Length * size * size * size, 0);
            if (cancellationToken.IsCancellationRequested)
                return;

            Profiler.BeginSample("CopyResultToChunks");
            CopyResultToChunks(chunks, pool, buffers.ResultReadbackBuffer);
            Profiler.EndSample();
        }

        public static void Dispatch(in NativeArray<Chunk> chunks, ref GenerateBlocksGPU buffers, GenerateBlocks.Params p, ComputeShader shader, TimerResults timers)
        {
            using (new TimerScope("write", timers))
            {
                var chunkMinDst = buffers.ChunkMinCBuffer.BeginWrite<int3>(0, chunks.Length);
                for (int i = 0; i < chunks.Length; i++)
                {
                    chunkMinDst[i] = chunks[i].Position * size;
                }
                buffers.ChunkMinCBuffer.EndWrite<int3>(chunks.Length);
            }

            using (new TimerScope("dispatch", timers))
            {
                p = AdjustDefaultParams(p);
                int kernelIndex = shader.FindKernel(GetKernelName(p));
                shader.SetBuffer(kernelIndex, "Result", buffers.ResultCBuffer);
                shader.SetBuffer(kernelIndex, "ChunkMinBuf", buffers.ChunkMinCBuffer);
                shader.SetVector("Offset", new float4(p.Offset, 0));
                shader.SetVector("Scale", new float4(p.Scale, 0));
                shader.SetFloat("Offset2", p.Offset2);
                shader.SetFloat("Scale2", p.Scale2);

                shader.Dispatch(kernelIndex, chunks.Length, 1, 2);
            }
        }

        [BurstCompile]
        private static void CopyResultToChunks(in NativeArray<Chunk> chunks, in ChunkDataPool pool, in NativeArray<int> result)
        {
            var cspan = chunks.AsSpan();
            var fullResult = result.Reinterpret<byte>(4);
            for (int i = 0; i < cspan.Length; i++)
            {
                ref var chunk = ref cspan[i];
                var len = size * size * size;
                var chunkResult = fullResult.GetSubArray(i * len, len);

                var blocks = chunkResult.AsReadOnlySpan();
                bool hasMultipleBlockStates = false;
                byte blockType = blocks[0];
                for (int j = 1; j < blocks.Length; j++)
                {
                    if (blocks[j] != blockType)
                    {
                        hasMultipleBlockStates = true;
                        break;
                    }
                }

                int blockStateCount = hasMultipleBlockStates ? 3 : 1;

                if (!chunk.Palette.IsCreated || chunk.Palette.Length != blockStateCount)
                {
                    chunk.Palette.Dispose();
                    chunk.Palette = new(blockStateCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }

                if (blockStateCount > 1)
                {
                    chunk.Palette[0] = 0;
                    chunk.Palette[1] = 1;
                    chunk.Palette[2] = 2;

                    if (!chunk.Blocks.IsCreated)
                    {
                        chunk.Blocks = pool.AllocateChunkBlocks();
                    }
                    chunkResult.CopyTo(chunk.Blocks);
                }
                else
                {
                    chunk.Palette[0] = blockType;

                    pool.DeallocateChunkBlocks(chunk.Blocks);
                    chunk.Blocks = default;
                }
            }
        }
    }
}
