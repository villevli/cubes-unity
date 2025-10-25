using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Cubes
{
    using static Math;

    public struct RayHit
    {
        public int BlockType;
        public float Distance;
        public float3 Pos;
        public int3 Normal;
        // Debug
        public int Steps;

        public readonly bool Miss => !Cubes.BlockType.IsSolid(BlockType);
    }

    public partial class ChunkLoader
    {
        /// <summary>
        /// Raycast and stop at the first solid block intersecting the ray.
        /// </summary>
        public bool Raycast(Ray ray, out RayHit hit, float maxDistance)
        {
            return Raycast(ray.origin, ray.direction, out hit, maxDistance, _chunkMap);
        }

        /// <inheritdoc cref="Raycast"/>
        public bool Raycast(in float3 origin, in float3 dir, out RayHit hit, float maxDistance)
        {
            return Raycast(origin, dir, out hit, maxDistance, _chunkMap);
        }

        /// <inheritdoc cref="Raycast"/>
        [BurstCompile]
        public static bool Raycast(in float3 origin, in float3 dir, out RayHit hit, float maxDistance,
            in NativeParallelHashMap<int3, Chunk> chunkMap)
        {
            float3 invDir = 1f / dir;
            float3 pos = origin;
            float distance = 0f;
            float3 sideDist = 0f;
            int material = BlockType.Air;

            int i;
            for (i = 0; i < 1024 && distance <= maxDistance; i++)
            {
                var blockPos = (int3)math.floor(pos);
                var chunkPos = GetChunkPos(blockPos);

                float3 cellMin = blockPos;
                float3 cellSize = 1.0f;

                // TODO: When traversing inside a single chunk, cache the chunk to avoid the map lookup
                if (!chunkMap.TryGetValue(chunkPos, out var chunk) || !chunk.IsLoaded)
                {
                    material = BlockType.Air;
                    // Traverse empty chunk in one step
                    cellMin = chunkPos * Chunk.Size;
                    cellSize = Chunk.Size;
                }
                else if (chunk.Palette.Length == 1)
                {
                    material = chunk.Palette[0];
                    // Traverse empty chunk in one step
                    cellMin = chunkPos * Chunk.Size;
                    cellSize = Chunk.Size;
                }
                else
                {
                    var block = Chunk.GetBlock(chunk.Blocks, GetChunkLocalPos(blockPos, chunkPos));
                    material = chunk.Palette[block];
                }

                if (BlockType.IsSolid(material))
                    break;

                // https://dubiousconst282.github.io/2024/10/03/voxel-ray-tracing/

                // sidePos = dir < 0.0f ? cellMin : cellMin + cellSize;
                float3 sidePos = cellMin + math.step(0.0f, dir) * cellSize;
                sideDist = (sidePos - origin) * invDir;

                distance = math.min(math.min(sideDist.x, sideDist.y), sideDist.z);

                // Make sure intersection pos is inside the correct block
                float3 neighborMin = math.select(cellMin, cellMin + copysign(cellSize, dir), distance == sideDist);
                float3 neighborMax = decrfloat(neighborMin + cellSize);

                pos = math.clamp(origin + dir * distance, neighborMin, neighborMax);
            }

            hit.BlockType = material;
            hit.Distance = distance;
            hit.Pos = pos;

            float tmax = math.min(math.min(sideDist.x, sideDist.y), sideDist.z);
            hit.Normal = math.select(-(int3)math.sign(dir), 0, tmax < sideDist);

            hit.Steps = i;

            return !hit.Miss;
        }
    }
}
