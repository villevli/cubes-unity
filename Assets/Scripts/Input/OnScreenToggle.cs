using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.OnScreen;

namespace Cubes.Input
{
    public class OnScreenToggle : OnScreenControl, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        [InputControl(layout = "Button")]
        [SerializeField]
        private string m_ControlPath;

        protected override string controlPathInternal
        {
            get => m_ControlPath;
            set => m_ControlPath = value;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
        }

        public void OnPointerDown(PointerEventData eventData)
        {
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            SendValueToControl(control.IsPressed() ? 0.0f : 1.0f);
        }
    }
}
