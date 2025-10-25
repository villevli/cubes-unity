using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

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

        private InputAction _pickBlockAction;
        private int _blockType = BlockType.Stone;

        private InputAction _breakBlockAction;
        private float _breakPressedTimer;
        private float _breakRepeatTimer;

        private InputAction _placeBlockAction;
        private float _placePressedTimer;
        private float _placeRepeatTimer;

        private Mesh _blockHighlightMesh;
        private Material _blockHighlightMaterial;
        private Material _blockHighlightMaterial2;

        public RayHit LastRayHit;

        private void Awake()
        {
            _blockHighlightMesh = CreateLineCube();
            _blockHighlightMaterial = new(Shader.Find("Hidden/Internal-Colored"));
            _blockHighlightMaterial.SetColor("_Color", Color.white);
            _blockHighlightMaterial2 = new(_blockHighlightMaterial);
            _blockHighlightMaterial2.SetColor("_Color", new(1, 1, 1, 0.1f));
            _blockHighlightMaterial2.SetFloat("_ZWrite", 0);
            _blockHighlightMaterial2.SetFloat("_ZTest", (int)CompareFunction.Disabled);
        }

        private void OnDestroy()
        {
            DestroyImmediate(_blockHighlightMesh);
            DestroyImmediate(_blockHighlightMaterial);
            DestroyImmediate(_blockHighlightMaterial2);
        }

        private void Start()
        {
            _pickBlockAction = InputSystem.actions.FindAction("Pick");
            _breakBlockAction = InputSystem.actions.FindAction("Attack");
            _placeBlockAction = InputSystem.actions.FindAction("Block");
        }

        private void Update()
        {
            var cam = Camera.main;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            // var ray = cam.ScreenPointToRay(Pointer.current.position.ReadValue());
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                // Debug.DrawLine(ray.origin, ray.GetPoint(hit.Distance), Color.blue);
                Debug.DrawRay(hit.Pos, (float3)hit.Normal, Color.red);

                HighlightBlock(math.floor(hit.Pos), (float3)_size);
            }
            LastRayHit = hit;

            if (_pickBlockAction.WasPressedThisFrame())
            {
                TryPickBlock(ray);
            }

            if (_breakBlockAction.WasPressedThisFrame())
            {
                _breakPressedTimer = 0;
                TryBreakBlock(ray);
            }
            else if (_breakBlockAction.IsPressed()
                 && (_breakPressedTimer += Time.deltaTime) > _startRepeatTime
                 && (_breakRepeatTimer += Time.deltaTime) > _repeatInterval)
            {
                _breakRepeatTimer = 0;
                TryBreakBlock(ray);
            }

            if (_placeBlockAction.WasPressedThisFrame())
            {
                _placePressedTimer = 0;
                TryPlaceBlock(ray);
            }
            else if (_placeBlockAction.IsPressed()
                 && (_placePressedTimer += Time.deltaTime) > _startRepeatTime
                 && (_placeRepeatTimer += Time.deltaTime) > _repeatInterval)
            {
                _placeRepeatTimer = 0;
                TryPlaceBlock(ray);
            }
        }

        private void TryPickBlock(Ray ray)
        {
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                _blockType = hit.BlockType;
            }
        }

        private void TryPlaceBlock(Ray ray)
        {
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                SetBlock((int3)math.floor(hit.Pos) + hit.Normal, _blockType);
            }
        }

        private void TryBreakBlock(Ray ray)
        {
            if (_chunkLoader.Raycast(ray, out var hit, _maxDistance))
            {
                SetBlock((int3)math.floor(hit.Pos), BlockType.Air);
            }
        }

        private async void SetBlock(int3 position, int blockType)
        {
            await _chunkLoader.SetBlockAsync(position, _size, blockType);
        }

        private void HighlightBlock(float3 pos, float3 size)
        {
            var matrix = Matrix4x4.TRS(pos, Quaternion.identity, size);
            DrawMesh(_blockHighlightMesh, matrix, _blockHighlightMaterial);
            DrawMesh(_blockHighlightMesh, matrix, _blockHighlightMaterial2);
        }

        private static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material)
        {
            Graphics.RenderMesh(new(material), mesh, 0, matrix);
        }

        private static Mesh CreateLineCube()
        {
            var verts = new float3[]
            {
                // bottom
                new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0),
                // top
                new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0),
            };
            var indices = new ushort[]
            {
                // bottom
                0, 1,
                1, 2,
                2, 3,
                3, 0,
                // top
                0 + 4, 1 + 4,
                1 + 4, 2 + 4,
                2 + 4, 3 + 4,
                3 + 4, 0 + 4,
                // sides
                0, 0 + 4,
                1, 1 + 4,
                2, 2 + 4,
                3, 3 + 4
            };

            var mesh = new Mesh();
            mesh.name = "LineCube";
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            mesh.SetVertexBufferParams(verts.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position)
            );
            mesh.SetVertexBufferData(verts, 0, 0, verts.Length);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new(0, indices.Length, MeshTopology.Lines));
            mesh.bounds = new((float3)0.5f, (float3)1);
            return mesh;
        }
    }
}
