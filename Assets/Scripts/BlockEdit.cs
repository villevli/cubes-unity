using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes
{
    /// <summary>
    /// Break and place blocks.
    /// </summary>
    public class BlockEdit : MonoBehaviour
    {
        [SerializeField]
        private ChunkLoader _chunkLoader;
        [SerializeField]
        private int _size = 1;

        private InputAction _breakBlockAction;
        private InputAction _placeBlockAction;

        private void Start()
        {
            _breakBlockAction = InputSystem.actions.FindAction("Attack");
            _placeBlockAction = InputSystem.actions.FindAction("Block");
        }

        private void Update()
        {
            if (_breakBlockAction.IsPressed())
            {
                SetBlock(GetEditBlockPos(), BlockType.Air);
            }
            if (_placeBlockAction.IsPressed())
            {
                SetBlock(GetEditBlockPos(), BlockType.Stone);
            }
        }

        private int3 GetEditBlockPos()
        {
            return (int3)math.floor(transform.position);
        }

        private async void SetBlock(int3 position, int blockType)
        {
            await _chunkLoader.SetBlockAsync(position, _size, blockType);
        }
    }
}
