using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.OnScreen;

namespace Cubes.Input
{
    /// <summary>
    /// Send pointer delta to the <see cref="OnScreenControl.controlPath"/> when dragging inside the area of the <see cref="OnScreenPointer"/>.
    /// </summary>
    public class OnScreenPointer : OnScreenControl, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        [InputControl(layout = "Vector2")]
        [SerializeField]
        private string m_ControlPath;

        protected override string controlPathInternal
        {
            get => m_ControlPath;
            set => m_ControlPath = value;
        }

        public void OnDrag(PointerEventData eventData)
        {
            SendValueToControl(eventData.delta);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
        }

        public void OnPointerUp(PointerEventData eventData)
        {
        }
    }
}
