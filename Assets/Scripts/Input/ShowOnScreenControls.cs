using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes.Input
{
    public class ShowOnScreenControls : MonoBehaviour
    {
        [SerializeField]
        private GameObject _controls;

        private void Awake()
        {
            InputSystem.onDeviceChange += OnDeviceChange;
            UpdateControls();
        }

        private void OnDestroy()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is Touchscreen)
            {
                UpdateControls();
            }
        }

        private void UpdateControls()
        {
            _controls.SetActive(Touchscreen.current != null);
        }
    }
}
