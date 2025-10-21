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
        [SerializeField]
        private float _maxDistance = 64;
        [SerializeField]
        private float _startRepeatTime = 0.5f;
        [SerializeField]
        private float _repeatInterval = 0.05f;

        private InputAction _breakBlockAction;
        private float _breakPressedTimer;
        private float _breakRepeatTimer;

        private InputAction _placeBlockAction;
        private float _placePressedTimer;
        private float _placeRepeatTimer;

        private void Start()
        {
            _breakBlockAction = InputSystem.actions.FindAction("Attack");
            _placeBlockAction = InputSystem.actions.FindAction("Block");
        }

        private void Update()
        {
            var ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                // Debug.DrawLine(ray.origin, ray.GetPoint(hit.Distance), Color.blue);
                Debug.DrawRay(hit.Pos, (float3)hit.Normal, Color.red);
            }

            if (_breakBlockAction.WasPressedThisFrame())
            {
                _breakPressedTimer = 0;
                TryBreakBlock();
            }
            else if (_breakBlockAction.IsPressed()
                 && (_breakPressedTimer += Time.deltaTime) > _startRepeatTime
                 && (_breakRepeatTimer += Time.deltaTime) > _repeatInterval)
            {
                _breakRepeatTimer = 0;
                TryBreakBlock();
            }

            if (_placeBlockAction.WasPressedThisFrame())
            {
                _placePressedTimer = 0;
                TryPlaceBlock();
            }
            else if (_placeBlockAction.IsPressed()
                 && (_placePressedTimer += Time.deltaTime) > _startRepeatTime
                 && (_placeRepeatTimer += Time.deltaTime) > _repeatInterval)
            {
                _placeRepeatTimer = 0;
                TryPlaceBlock();
            }
        }

        private void TryPlaceBlock()
        {
            var ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                SetBlock((int3)math.floor(hit.Pos) + hit.Normal, BlockType.Stone);
            }
        }

        private void TryBreakBlock()
        {
            var ray = Camera.main.ScreenPointToRay(Pointer.current.position.ReadValue());
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                SetBlock((int3)math.floor(hit.Pos), BlockType.Air);
            }
        }

        private async void SetBlock(int3 position, int blockType)
        {
            await _chunkLoader.SetBlockAsync(position, _size, blockType);
        }
    }
}
