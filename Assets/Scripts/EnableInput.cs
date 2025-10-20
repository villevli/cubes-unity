using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes
{
    /// <summary>
    /// Activates input actions maps and locks cursor.
    /// </summary>
    public class EnableInput : MonoBehaviour
    {
        public bool InputActive
        {
            get => InputSystem.actions.enabled;
            set
            {
                if (value)
                    InputSystem.actions.Enable();
                else
                    InputSystem.actions.Disable();
            }
        }

        private void Start()
        {
            InputActive = Touchscreen.current != null;
        }

        private void Update()
        {
            Rect displayRect = new(0, 0, Screen.width, Screen.height);

            // Pressing esc disables input. Consistent with editor always unlocking the cursor with esc
            if (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false)
            {
                InputActive = false;
            }
            // Click to reactive input and lock the cursor
            if ((Pointer.current?.press.wasReleasedThisFrame ?? false) && displayRect.Contains(Pointer.current.position.ReadValue()))
            {
                InputActive = true;
            }

            if (Touchscreen.current == null)
            {
                Cursor.lockState = InputActive ? CursorLockMode.Locked : CursorLockMode.None;
            }
        }
    }
}
