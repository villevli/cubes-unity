using UnityEngine;

namespace Cubes
{
    public class ChunkTests : MonoBehaviour
    {
        [SerializeField]
        private ChunkLoader _chunkLoader;

        private async void Start()
        {
            await Awaitable.NextFrameAsync();
            // Make hole
            await _chunkLoader.SetBlockAsync(new(0, -128, 0), new(63, 256, 63), BlockType.Air);
        }
    }
}
