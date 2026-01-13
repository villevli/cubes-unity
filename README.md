# Cubes

Cube based procedural world in Unity. Inspired by Minecraft.

![A cave](https://github.com/user-attachments/assets/feddac16-8afd-4d09-a7a1-403eec8b0f70)

- Procedural terrain made from blocks. Includes noise caves under ground
- Generated in 16x16x16 chunks
- Hidden surfaces are culled to make rendering very fast even with larger view distances
- Using the burst compiler for all heavy calculations to make it many times faster
- Using background threads to make traversal in the world smooth even when loading or generating chunks
- Procedural generation with perlin noises is done in a GPU compute shader. 4096 chunks (16.7 million blocks) generates in under 20 milliseconds on RTX 3070
- Raycasting to find block surfaces
- Break and place blocks

## Planned features
- Save and load worlds
- Collision detection
- Light levels
- "Ambient occlusion" in corners
- Biomes
- Pathfinding
- Multiplayer
