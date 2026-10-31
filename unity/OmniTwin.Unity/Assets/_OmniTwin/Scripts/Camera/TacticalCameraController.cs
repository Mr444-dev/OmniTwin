using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace OmniTwin.Simulation.CameraControl
{
    /// <summary>Orbits the workspace with Input System while preserving HUD scroll and text focus.</summary>
    [DisallowMultipleComponent]
    public sealed class TacticalCameraController : MonoBehaviour
    {
        [SerializeField] private Vector3 focus = new Vector3(-2, 0, 0);
        [SerializeField] private float initialDistance = 32;
        [SerializeField] private float initialYaw = -32;
        [SerializeField] private float initialPitch = 53;
        [SerializeField] private float orbitSensitivity = 0.18f;
        [SerializeField] private float zoomSensitivity = 0.12f;
        private float _distance, _yaw, _pitch;
        private UIDocument _hud;

        /// <summary>Restores the authored overview.</summary>
        public void ResetView() { _distance = initialDistance; _yaw = initialYaw; _pitch = initialPitch; Apply(); }
        private void OnEnable() { _hud = UnityEngine.Object.FindAnyObjectByType<UIDocument>(); ResetView(); }
        private void LateUpdate()
        {
            var root = _hud == null ? null : _hud.rootVisualElement;
            var panel = root?.panel;
            if (panel?.focusController.focusedElement is VisualElement focused &&
                (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null)) return;
            if (Keyboard.current != null && Keyboard.current.homeKey.wasPressedThisFrame) ResetView();
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (panel != null)
            {
                var cursor = mouse.position.ReadValue(); cursor.y = Screen.height - cursor.y;
                var picked = panel.Pick(RuntimePanelUtils.ScreenToPanel(panel, cursor));
                if (picked != null && picked != root && picked.name != "screen") return;
            }
            if (mouse.rightButton.isPressed)
            {
                var delta = mouse.delta.ReadValue();
                _yaw += delta.x * orbitSensitivity;
                _pitch = Mathf.Clamp(_pitch - delta.y * orbitSensitivity, 25, 85);
            }
            var wheel = Mathf.Clamp(mouse.scroll.ReadValue().y, -4, 4);
            _distance = Mathf.Clamp(_distance * Mathf.Exp(-wheel * zoomSensitivity), 13, 48);
            Apply();
        }
        private void Apply()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0);
            transform.SetPositionAndRotation(focus - rotation * Vector3.forward * _distance, rotation);
        }
    }
}
