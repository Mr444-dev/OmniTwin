using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using OmniTwin.Simulation.Agents;

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
        private Vector3 _targetFocus;
        private float _targetDistance, _targetYaw, _targetPitch;
        private SwarmAgentController _followAgent;
        private UIDocument _hud;
        private UnityEngine.Camera _camera;
        private int _preset = 1;
        private float _targetOrthoSize = 6.55f;

        /// <summary>Restores the authored tactical overview.</summary>
        public void ResetView() => SetPreset(1, null);
        /// <summary>Switches between overview, top-down, and selected-agent chase camera presets.</summary>
        public void SetPreset(int preset, SwarmAgentController selectedAgent)
        {
            if (preset < 1 || preset > 3) throw new ArgumentOutOfRangeException(nameof(preset));
            _preset = preset;
            _followAgent = preset == 3 ? selectedAgent : null;
            _targetFocus = preset == 3 && selectedAgent != null ? selectedAgent.transform.position : new Vector3(-2, 0, 0);
            _targetDistance = preset == 2 ? 24 : preset == 3 ? 10 : initialDistance;
            _targetYaw = preset == 2 ? 0 : preset == 3 ? -42 : initialYaw;
            _targetPitch = preset == 2 ? 90 : preset == 3 ? 32 : initialPitch;
            if (preset == 2) _targetOrthoSize = 8.0f;
            if (_camera != null) _camera.orthographic = preset == 2;
        }
        private void OnEnable() { _hud = UnityEngine.Object.FindAnyObjectByType<UIDocument>(); _camera = GetComponent<UnityEngine.Camera>(); ResetView(); }
        private void LateUpdate()
        {
            var root = _hud == null ? null : _hud.rootVisualElement;
            var panel = root?.panel;
            if (panel?.focusController.focusedElement is VisualElement focused &&
                (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null)) return;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.homeKey.wasPressedThisFrame) ResetView();
                else if (keyboard.digit1Key.wasPressedThisFrame) SetPreset(1, null);
                else if (keyboard.digit2Key.wasPressedThisFrame) SetPreset(2, null);
                else if (keyboard.digit3Key.wasPressedThisFrame) SetPreset(3, UnityEngine.Object.FindAnyObjectByType<SwarmAgentController>());
            }
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
                if (_preset == 2) _targetFocus += new Vector3(-delta.x * 0.025f, 0, -delta.y * 0.025f);
                else
                {
                    _targetYaw += delta.x * orbitSensitivity;
                    _targetPitch = Mathf.Clamp(_targetPitch - delta.y * orbitSensitivity, 25, 88);
                }
            }
            var wheel = Mathf.Clamp(mouse.scroll.ReadValue().y, -4, 4);
            if (_preset == 2) _targetOrthoSize = Mathf.Clamp(_targetOrthoSize * Mathf.Exp(-wheel * zoomSensitivity), 3.2f, 13);
            else _targetDistance = Mathf.Clamp(_targetDistance * Mathf.Exp(-wheel * zoomSensitivity), 8, 48);
            if (_followAgent != null) _targetFocus = _followAgent.transform.position;
            var blend = 1 - Mathf.Exp(-8 * Time.unscaledDeltaTime);
            focus = Vector3.Lerp(focus, _targetFocus, blend);
            _distance = Mathf.Lerp(_distance, _targetDistance, blend);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, blend);
            _pitch = Mathf.Lerp(_pitch, _targetPitch, blend);
            if (_camera != null && _preset == 2) _camera.orthographicSize = Mathf.Lerp(_camera.orthographicSize, _targetOrthoSize, blend);
            Apply();
        }
        private void Apply()
        {
            var rotation = _preset == 2 ? Quaternion.Euler(90, 0, 0) : Quaternion.Euler(_pitch, _yaw, 0);
            transform.SetPositionAndRotation(focus - rotation * Vector3.forward * _distance, rotation);
        }
    }
}
