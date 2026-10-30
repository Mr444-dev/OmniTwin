using UnityEngine;
using UnityEngine.InputSystem;

namespace OmniTwin.Simulation.CameraControl
{
    /// <summary>Orbits the authored workspace using the installed Input System, without network or scene searches.</summary>
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

        /// <summary>Restores the authored overview.</summary>
        public void ResetView() { _distance = initialDistance; _yaw = initialYaw; _pitch = initialPitch; Apply(); }
        private void OnEnable() => ResetView();
        private void LateUpdate()
        {
            if (Keyboard.current != null && Keyboard.current.homeKey.wasPressedThisFrame) ResetView();
            var mouse = Mouse.current;
            if (mouse == null) return;
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
