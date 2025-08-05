using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
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

        private const int MaxChunksPerDispatch = 4096;
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

        public static void Run(ref NativeArray<Chunk> chunks, ref GenerateBlocksGPU buffers, GenerateBlocks.Params p, ComputeShader shader, TimerResults timers)
        {
            var chunkMinCBuf = buffers.ChunkMinCBuffer;
            var resultCBuf = buffers.ResultCBuffer;

            using (new TimerScope("write", timers))
            {
                var chunkMinDst = chunkMinCBuf.BeginWrite<int3>(0, chunks.Length);
                for (int i = 0; i < chunks.Length; i++)
                {
                    chunkMinDst[i] = chunks[i].Position * size;
                }
                chunkMinCBuf.EndWrite<int3>(chunks.Length);
            }

            using (new TimerScope("dispatch", timers))
            {
                p = AdjustDefaultParams(p);
                int kernelIndex = shader.FindKernel(GetKernelName(p));
                shader.SetBuffer(kernelIndex, "Result", resultCBuf);
                shader.SetBuffer(kernelIndex, "ChunkMinBuf", chunkMinCBuf);
                shader.SetVector("Offset", new float4(p.Offset, 0));
                shader.SetVector("Scale", new float4(p.Scale, 0));
                shader.SetFloat("Offset2", p.Offset2);
                shader.SetFloat("Scale2", p.Scale2);

                shader.Dispatch(kernelIndex, chunks.Length, 1, 2);
            }

            var result = buffers.ResultReadbackBuffer;

            using (new TimerScope("readback", timers))
            {
                var readback = AsyncGPUReadback.RequestIntoNativeArray(ref result, resultCBuf, chunks.Length * size * size * size, 0);
                readback.WaitForCompletion();
            }

            using (new TimerScope("copy", timers))
            {
                CopyResultToChunks(ref chunks, result);
            }
        }

        [BurstCompile]
        private static void CopyResultToChunks(ref NativeArray<Chunk> chunks, in NativeArray<int> result)
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
                        chunk.Blocks = new(size * size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    }
                    chunkResult.CopyTo(chunk.Blocks);
                }
                else
                {
                    chunk.Palette[0] = blockType;

                    chunk.Blocks.Dispose();
                    chunk.Blocks = default;
                }
            }
        }
    }
}
