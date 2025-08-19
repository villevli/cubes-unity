using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Cubes
{
    /// <summary>
    /// Contains data needed to cull a chunk for rendering.
    /// </summary>
    public struct RenderableChunk
    {
        public int MeshId;
        public short ConnectedFaces;
    }

    public struct CullResult
    {
        public int3 Pos;
        public int CameFromFace;
    }

    /// <summary>
    /// Cull chunks that are not seen by a camera.
    /// </summary>
    [BurstCompile]
    public struct CullChunks
    {
        // - Occlusion culling because the amount of surfaces in caves are too much to render
        // - Precalculate 15 bits per chunk (for each pair of faces) to tell if a path of non opaque blocks through the chunk connect those faces together (find this using flood fill)
        // - Traverse from the chunk where the camera is to neighboring chunks to collect all chunks that should be rendered
        // - Don't traverse to directions that are towards the camera
        // - Skip chunks outside the camera view frustum

        /// <summary>
        /// Calculates which faces of the chunk can "see each other" and stores the result in the chunk data.
        /// </summary>
        /// <param name="chunks"></param>
        [BurstCompile]
        public static void CalculateConnectedFaces(ref NativeArray<Chunk> chunks)
        {
            // https://tomcc.github.io/2014/08/31/visibility-1.html
            const int size = Chunk.Size;
            NativeArray<byte> filled = new(size * size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeRingQueue<int3> fillQueue = new(size * size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            foreach (ref var chunk in chunks.AsSpan())
            {
                if (chunk.Palette.Length == 1)
                {
                    if (chunk.Palette[0] == BlockType.Air)
                        chunk.ConnectedFaces = ~0;
                    else
                        chunk.ConnectedFaces = 0;
                    continue;
                }

                ReadOnlySpan<byte> blocks = chunk.Blocks;
                short connectedFaces = 0;
                // memclear
                for (int i = 0; i < filled.Length; i++)
                    filled[i] = 0;

                void FloodFill(ReadOnlySpan<byte> blocks, int3 p)
                {
                    bool IsOpaque(byte block)
                    {
                        // TODO: check properly based on the palette of this chunk
                        return block != 0;
                    }

                    bool IsFilled(byte b)
                    {
                        return b != 0;
                    }

                    if (IsFilled(Chunk.GetBlock(filled, p)))
                        return;

                    if (IsOpaque(Chunk.GetBlock(blocks, p)))
                        return;

                    short faceSet = 0;
                    Chunk.GetBlock(filled, p) = 1;
                    fillQueue.Enqueue(p);
                    // Flood fill
                    while (fillQueue.TryDequeue(out var fp))
                    {
                        // Traverse to neighbors
                        for (int face = 0; face < 6; face++)
                        {
                            var np = fp + Chunk.FaceNormal(face);
                            // Check if exiting boundaries
                            if (np.y < 0)
                                faceSet |= 1;
                            else if (np.y >= size)
                                faceSet |= 1 << 1;
                            else if (np.z < 0)
                                faceSet |= 1 << 2;
                            else if (np.z >= size)
                                faceSet |= 1 << 3;
                            else if (np.x < 0)
                                faceSet |= 1 << 4;
                            else if (np.x >= size)
                                faceSet |= 1 << 5;
                            else if (!IsFilled(Chunk.GetBlock(filled, np)) && !IsOpaque(Chunk.GetBlock(blocks, np)))
                            {
                                // Fill
                                Chunk.GetBlock(filled, np) = 1;
                                fillQueue.Enqueue(np);
                            }
                        }
                    }

                    connectedFaces |= Chunk.GetConnectedFacesFromSet(faceSet);
                }

                // Start flood fills at the edges of the chunk

                // down -y
                for (int z = 0; z < size; z++)
                    for (int x = 0; x < size; x++)
                        FloodFill(blocks, new(x, 0, z));
                // up +y
                for (int z = 0; z < size; z++)
                    for (int x = 0; x < size; x++)
                        FloodFill(blocks, new(x, size - 1, z));
                // south -z
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        FloodFill(blocks, new(x, y, 0));
                // north +z
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        FloodFill(blocks, new(x, y, size - 1));
                // west -x
                for (int y = 0; y < size; y++)
                    for (int z = 0; z < size; z++)
                        FloodFill(blocks, new(0, y, z));
                // east +x
                for (int y = 0; y < size; y++)
                    for (int z = 0; z < size; z++)
                        FloodFill(blocks, new(size - 1, y, z));

                chunk.ConnectedFaces = connectedFaces;
            }

            filled.Dispose();
            fillQueue.Dispose();
        }

        private struct Step
        {
            public int3 ChunkPos;
            public RenderableChunk Chunk;
            public sbyte CameFromFace; // down -y, up +y, south -z, north +z, west -x, east +x
        }

        /// <summary>
        /// Finds visible chunks based on the camera pos and frustum planes.
        /// Copies the chunks into the <paramref name="result"/> array and returns the count.
        /// </summary>
        /// <param name="result"></param>
        /// <param name="chunks"></param>
        /// <param name="cameraPos"></param>
        /// <param name="frustumPlanes"></param>
        /// <returns>count of visible chunks</returns>
        public static int FindVisibleChunks(ref NativeArray<CullResult> result, in NativeParallelHashMap<int3, RenderableChunk> chunks, Camera camera, int viewDistance)
        {
            float maxFov = math.max(camera.fieldOfView, Camera.VerticalToHorizontalFieldOfView(camera.fieldOfView, camera.aspect));
            return FindVisibleChunks(ref result, chunks,
                camera.transform.position,
                camera.transform.forward,
                maxFov,
                new(GeometryUtility.CalculateFrustumPlanes(camera), Allocator.Temp),
                viewDistance);
        }

        [BurstCompile]
        public static int FindVisibleChunks(ref NativeArray<CullResult> result, in NativeParallelHashMap<int3, RenderableChunk> chunks,
                                            in float3 cameraPos, in float3 cameraForward, float fov, in NativeArray<Plane> frustumPlanes, int viewDistance)
        {
            // https://tomcc.github.io/2014/08/31/visibility-2.
            int count = 0;
            NativeRingQueue<Step> queue = new(4096 * 2, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            // Find valid traversal directions
            float cosFov = math.cos(math.radians(math.min(90 + fov * (2 / 3f), 180)));
            byte validDirs = 0;
            for (int i = 0; i < 6; i++)
            {
                if (math.dot(Chunk.FaceNormal(i), cameraForward) >= cosFov)
                    validDirs |= (byte)(1 << i);
            }

            // Push camera chunk
            int3 cPos = (int3)math.floor(cameraPos / Chunk.Size);
            chunks.TryGetValue(cPos, out var cChunk);
            queue.Enqueue(new()
            {
                ChunkPos = cPos,
                Chunk = new()
                {
                    MeshId = cChunk.MeshId,
                    ConnectedFaces = ~0
                },
                CameFromFace = 0
            });

            // Continous 3d grid centered around the cPos
            // bit 0 = has chunk been added to result
            // bit 1 = has chunk already passed frustum culling
            // bits 2-7 = have we traversed via each face
            int gridSize = viewDistance * 2;
            int3 gridOffset = cPos - viewDistance;
            NativeArray<byte> grid = new(gridSize * gridSize * gridSize, Allocator.Temp, NativeArrayOptions.ClearMemory);
            Span<byte> gridSpan = grid.AsSpan();

            int GetIdx(in int3 p) => p.y * gridSize * gridSize + p.z * gridSize + p.x;

            int maxSearch = 64 * 64 * 64;
            int iters = 0;

            // Search
            while (queue.TryDequeue(out var step) && iters++ < maxSearch)
            {
                int3 pos = step.ChunkPos;
                var chunk = step.Chunk;
                ref var bits = ref gridSpan[GetIdx(pos - gridOffset)];

                // Add result if not added yet
                if ((bits & 0x1) == 0 && chunk.MeshId != 0)
                {
                    result[count++] = new()
                    {
                        Pos = pos,
                        CameFromFace = step.CameFromFace
                    };
                    if (count >= result.Length)
                        break;
                }
                // Mark result added
                bits |= 1;

                if (chunk.ConnectedFaces == 0)
                    continue;

                for (int face = 0; face < 6; face++)
                {
                    if (((1 << face) & validDirs) == 0)
                        continue;

                    if (!Chunk.AreFacesConnected(chunk.ConnectedFaces, step.CameFromFace, face))
                        continue;

                    // Already traversed via this face
                    if ((bits >> (face + 2) & 0x1) == 1)
                        continue;

                    // Mark this chunk face traversed
                    bits |= (byte)(1 << (face + 2));

                    var neighborPos = pos + Chunk.FaceNormal(face);

                    int neighborIdx = GetIdx(neighborPos - gridOffset);
                    // Outside grid (axis aligned view distance)
                    if (neighborIdx < 0 || neighborIdx >= gridSpan.Length)
                        continue;

                    ref var neighborBits = ref gridSpan[neighborIdx];

                    // Skip if all faces already traversed
                    if (((neighborBits >> 2) & validDirs) == validDirs)
                    {
                        continue;
                    }

                    // Frustum cull
                    // Check bits in case already passed frustum cull before
                    if ((neighborBits >> 1 & 0x1) == 0 && !TestPlanesAABB(frustumPlanes, new()
                    {
                        center = (float3)(neighborPos * Chunk.Size + Chunk.Size / 2),
                        extents = (float3)(Chunk.Size / 2)
                    }))
                    {
                        continue;
                    }

                    // Mark this chunk passed frustum culling
                    neighborBits |= (byte)(1 << 1);

                    int cameFromFace = Chunk.OppositeFace(face);

                    if (!chunks.TryGetValue(neighborPos, out var neighborChunk))
                    {
                        neighborChunk = new()
                        {
                            MeshId = 0,
                            // Really these should be fully transparent chunks
                            // but setting to 0 prevents the search from going down over the edge of loaded chunks
                            ConnectedFaces = 0
                        };
                    }

                    Step neighborStep = new()
                    {
                        ChunkPos = neighborPos,
                        Chunk = neighborChunk,
                        CameFromFace = (sbyte)cameFromFace
                    };
                    queue.Enqueue(neighborStep);
                }
            }

            // Debug.Log($"{iters}");

            queue.Dispose();
            grid.Dispose();
            return count;
        }

        public static bool TestPlanesAABB(in ReadOnlySpan<Plane> planes, in Bounds bounds)
        {
            for (int i = 0; i < planes.Length; i++)
            {
                Plane plane = planes[i];
                float3 normal_sign = math.sign(plane.normal);
                float3 test_point = (float3)(bounds.center) + (bounds.extents * normal_sign);

                float dot = math.dot(test_point, plane.normal);
                if (dot + plane.distance < 0)
                    return false;
            }

            return true;
        }
    }
}
