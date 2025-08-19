using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes
{
    /// <summary>
    /// Fly and look around using player input.
    /// </summary>
    public class FlyingCameraController : MonoBehaviour
    {
        [SerializeField]
        private Vector2 _lookSensitivity = new(0.2f, -0.2f);
        [SerializeField]
        private float _moveSpeed = 5;
        [SerializeField]
        private float _sprintSpeed = 100;
        [SerializeField]
        private bool _moveRelative = true;

        private bool _inputActive;
        private Vector2 _lookAngles;

        private InputAction _lookAction;
        private InputAction _moveAction;
        private InputAction _sprintAction;
        private InputAction _jumpAction;
        private InputAction _crouchAction;

        private void Start()
        {
            _lookAction = InputSystem.actions.FindAction("Look");
            _moveAction = InputSystem.actions.FindAction("Move");
            _sprintAction = InputSystem.actions.FindAction("Sprint");
            _jumpAction = InputSystem.actions.FindAction("Jump");
            _crouchAction = InputSystem.actions.FindAction("Crouch");

            var rot = transform.eulerAngles;
            _lookAngles.x = rot.y;
            _lookAngles.y = rot.x;

            _inputActive = Touchscreen.current != null;
        }

        private void Update()
        {
            Rect displayRect = new(0, 0, Screen.width, Screen.height);

            // Activate input and lock cursor when using mouse + keyboard
            if (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false)
            {
                _inputActive = !_inputActive;
            }
            if ((Mouse.current?.press.wasReleasedThisFrame ?? false) && displayRect.Contains(Mouse.current.position.ReadValue()))
            {
                _inputActive = true;
            }

            if (Touchscreen.current == null)
            {
                Cursor.lockState = _inputActive ? CursorLockMode.Locked : CursorLockMode.None;
            }

            if (!_inputActive)
                return;

            // Look
            var lookInput = _lookAction.ReadValue<Vector2>();
            _lookAngles += lookInput * _lookSensitivity;
            transform.rotation = Quaternion.Euler(_lookAngles.y, _lookAngles.x, 0);

            // Move
            var moveInput = _moveAction.ReadValue<Vector2>();
            var isSprinting = _sprintAction.IsPressed();
            var isJumping = _jumpAction.IsPressed();
            var isCrouching = _crouchAction.IsPressed();
            var deltaY = (isJumping ? 1 : 0) - (isCrouching ? 1 : 0);
            var deltaPos = new Vector3(moveInput.x, deltaY, moveInput.y);
            deltaPos *= isSprinting ? _sprintSpeed : _moveSpeed;
            var moveRot = _moveRelative ? transform.rotation : Quaternion.Euler(0, _lookAngles.x, 0);
            transform.position += moveRot * deltaPos * Time.deltaTime;
        }
    }
}
