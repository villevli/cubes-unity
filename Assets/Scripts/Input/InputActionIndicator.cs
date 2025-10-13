using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes.Input
{
    public class InputActionIndicator : MonoBehaviour
    {
        [SerializeField]
        private InputActionReference _action;

        [SerializeField]
        private GameObject _indicatorPressed;
        [SerializeField]
        private GameObject _indicatorNotPressed;

        private void Update()
        {
            var isPressed = _action.action.IsPressed();
            if (_indicatorPressed != null)
                _indicatorPressed.SetActive(isPressed);
            if (_indicatorNotPressed != null)
                _indicatorNotPressed.SetActive(!isPressed);
        }
    }
}
