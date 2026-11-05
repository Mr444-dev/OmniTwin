using System;
using System.Collections;
using System.IO;
using System.Text;
using OmniTwin.Simulation.Media;
using OmniTwin.Simulation.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

namespace OmniTwin.Simulation.Diagnostics
{
    /// <summary>Runs opt-in real UI Toolkit button checks in Play Mode or a standalone player.</summary>
    [DisallowMultipleComponent]
    public sealed class HudButtonSmokeTestRunner : MonoBehaviour
    {
        private const string RunArgument = "--omnitwin-hud-smoke-test";
        private readonly StringBuilder _report = new StringBuilder(2048);
        private UIDocument _document;
        private PolicyExecutor _executor;
        private VisualServicesController _visuals;
        private MediaConsoleController _media;
        private int _checks;
        private int _failures;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunWhenExplicitlyRequested()
        {
            foreach (var argument in Environment.GetCommandLineArgs())
                if (string.Equals(argument, RunArgument, StringComparison.Ordinal))
                {
                    CreateRunner();
                    return;
                }
        }

        /// <summary>Starts the same opt-in test from the Unity Editor's active Play Mode.</summary>
        public static HudButtonSmokeTestRunner StartFromPlayMode()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Start the TacticalDesk scene in Play Mode first.");
            var existing = FindAnyObjectByType<HudButtonSmokeTestRunner>();
            if (existing != null)
            {
                existing.StopAllCoroutines();
                Destroy(existing.gameObject);
            }
            return CreateFreshRunner();
        }

        private static HudButtonSmokeTestRunner CreateRunner()
        {
            var existing = FindAnyObjectByType<HudButtonSmokeTestRunner>();
            if (existing != null) return existing;
            return CreateFreshRunner();
        }

        private static HudButtonSmokeTestRunner CreateFreshRunner()
        {
            var host = new GameObject("OmniTwin HUD Button Smoke Test");
            DontDestroyOnLoad(host);
            return host.AddComponent<HudButtonSmokeTestRunner>();
        }

        private IEnumerator Start()
        {
            _document = FindAnyObjectByType<UIDocument>();
            _executor = FindAnyObjectByType<PolicyExecutor>();
            _visuals = FindAnyObjectByType<VisualServicesController>();
            _media = FindAnyObjectByType<MediaConsoleController>();
            var deadline = Time.realtimeSinceStartup + 20f;
            while ((_document == null || _executor == null || _visuals == null || _media == null) &&
                Time.realtimeSinceStartup < deadline)
            {
                _document = FindAnyObjectByType<UIDocument>();
                _executor = FindAnyObjectByType<PolicyExecutor>();
                _visuals = FindAnyObjectByType<VisualServicesController>();
                _media = FindAnyObjectByType<MediaConsoleController>();
                yield return null;
            }

            if (_document == null || _executor == null || _visuals == null || _media == null)
            {
                Check(false, "Tactical scene services", "HUD, policy executor, visual services, or voice service missing");
                Finish();
                yield break;
            }

            var root = _document.rootVisualElement;
            var operation = root.Q<Label>("operationText");
            var readyDeadline = Time.realtimeSinceStartup + 15f;
            while ((root.Q<DropdownField>("semanticTrack") == null || root.Q<DropdownField>("semanticTrack").choices.Count == 0) &&
                Time.realtimeSinceStartup < readyDeadline)
                yield return null;

            yield return TestMission(root, operation, "patrolButton", "PerimeterPatrolPolicy", "PATROL");
            yield return TestMission(root, operation, "cordonButton", "HazardQuarantinePolicy", "CORDON");
            yield return TestMission(root, operation, "searchButton", "GridSearchAndRescuePolicy", "GRID SEARCH");

            Click(root.Q<Button>("holdButton"));
            Check(_executor.ActivePolicyName == "EmergencyHoldPolicy" && !_executor.IsLocalMissionMoving &&
                operation.text.IndexOf("HOLD", StringComparison.OrdinalIgnoreCase) >= 0,
                "HOLD", _executor.ActivePolicyName + " / " + _executor.LocalExecutionStatus + " / " + operation.text);
            yield return new WaitForSecondsRealtime(0.2f);

            Click(root.Q<Button>("brainTab"));
            yield return new WaitForSecondsRealtime(0.15f);
            root.Q<TextField>("missionInstruction").value =
                "Establish a safety cordon around the largest hazard and patrol the outer perimeter.";
            Click(root.Q<Button>("generateButton"));
            yield return new WaitForSecondsRealtime(0.3f);
            var brainActive = _executor.IsVerifiedBrainPolicyActive ||
                (_executor.IsLocalMissionSelected && _executor.ActivePolicyName == "HybridCordonPatrolBaselinePolicy");
            Check(brainActive && (operation.text.IndexOf("POLICY VERIFIED AND ACTIVE", StringComparison.OrdinalIgnoreCase) < 0 || _executor.IsVerifiedBrainPolicyActive),
                "BRAIN / truthful local fallback", _executor.ActivePolicyName + " / " + operation.text);

            Click(root.Q<Button>("semanticsTab"));
            yield return new WaitForSecondsRealtime(0.15f);
            var track = root.Q<DropdownField>("semanticTrack");
            Check(track != null && track.choices.Count > 0, "current track choices", "count=" + (track == null ? 0 : track.choices.Count));
            if (track != null && track.choices.Count > 0)
            {
                Click(root.Q<Button>("selectTrack1Button"));
                yield return new WaitForSecondsRealtime(0.12f);
                Check(!string.IsNullOrEmpty(track.value) && operation.text.IndexOf("TRACK SELECTED", StringComparison.Ordinal) >= 0,
                    "select current track", track.value + " / " + operation.text);
                root.Q<TextField>("semanticLabel").value = "Smoke Test Hazard";
                root.Q<DropdownField>("semanticHazard").value = "high";
                Click(root.Q<Button>("applySemanticButton"));
                Check(operation.text.IndexOf("LOCAL TRACK #", StringComparison.Ordinal) >= 0 &&
                    operation.text.IndexOf("HAZARD VISUAL", StringComparison.Ordinal) >= 0,
                    "apply visible local hazard", operation.text);
                yield return new WaitForSecondsRealtime(0.1f);
                Click(root.Q<Button>("clearSemanticButton"));
                Check(operation.text.IndexOf("hazard visual cleared", StringComparison.OrdinalIgnoreCase) >= 0,
                    "clear operator hazard override", operation.text);
            }

            Click(root.Q<Button>("sceneryTab"));
            yield return new WaitForSecondsRealtime(0.15f);
            Click(root.Q<Button>("generateSceneryButton"));
            Check(!_visuals.IsDefaultSurface && _visuals.SceneryStatus.IndexOf("LOCAL STYLE ACTIVE", StringComparison.Ordinal) >= 0,
                "generate surface visible local effect", _visuals.SceneryStatus + " / " + operation.text);
            yield return new WaitForSecondsRealtime(0.1f);
            Click(root.Q<Button>("steelSurfaceButton"));
            Check(_visuals.SceneryStatus.IndexOf("INDUSTRIAL STEEL", StringComparison.Ordinal) >= 0, "industrial steel preset", _visuals.SceneryStatus);
            Click(root.Q<Button>("marsSurfaceButton"));
            Check(_visuals.SceneryStatus.IndexOf("MARS CRATER", StringComparison.Ordinal) >= 0, "Mars surface preset", _visuals.SceneryStatus);
            Click(root.Q<Button>("blueprintSurfaceButton"));
            Check(_visuals.SceneryStatus.IndexOf("NEON BLUEPRINT", StringComparison.Ordinal) >= 0, "blueprint surface preset", _visuals.SceneryStatus);
            Click(root.Q<Button>("resetSceneryButton"));
            Check(_visuals.IsDefaultSurface && operation.text.IndexOf("DEFAULT TACTICAL SURFACE RESTORED", StringComparison.Ordinal) >= 0,
                "restore authored surface", operation.text);

            Click(root.Q<Button>("brainTab"));
            yield return new WaitForSecondsRealtime(0.15f);
            Click(root.Q<Button>("useTranscriptButton"));
            Check(operation.text.IndexOf("TRANSCRIPT", StringComparison.OrdinalIgnoreCase) >= 0,
                "use transcript responds", operation.text);
            var voiceBefore = _media.VoiceStatus;
            Click(root.Q<Button>("recordVoiceButton"));
            yield return new WaitForSecondsRealtime(0.15f);
            var voiceResponded = _media.IsRecording || _media.IsTranscribing || _media.VoiceStatus != voiceBefore;
            Check(voiceResponded, "record voice responds", _media.VoiceStatus);
            if (_media.IsRecording || _media.IsTranscribing)
            {
                Click(root.Q<Button>("cancelVoiceButton"));
                Check(!_media.IsRecording, "cancel voice capture", _media.VoiceStatus);
            }

            var previewBefore = root.Q<Button>("previewToggleButton").text;
            Click(root.Q<Button>("previewToggleButton"));
            yield return new WaitForSecondsRealtime(0.15f);
            Check(root.Q<Button>("previewToggleButton").text != previewBefore, "camera preview toggle", root.Q<Button>("previewToggleButton").text);
            var masksBefore = root.Q<Button>("maskToggleButton").text;
            Click(root.Q<Button>("maskToggleButton"));
            yield return new WaitForSecondsRealtime(0.15f);
            Check(root.Q<Button>("maskToggleButton").text != masksBefore, "SAM mask toggle", root.Q<Button>("maskToggleButton").text);
            var lidarBefore = root.Q<Button>("lidarToggleButton").text;
            Click(root.Q<Button>("lidarToggleButton"));
            yield return new WaitForSecondsRealtime(0.15f);
            Check(root.Q<Button>("lidarToggleButton").text != lidarBefore, "LiDAR visibility toggle", root.Q<Button>("lidarToggleButton").text);

            Click(root.Q<Button>("view1Button"));
            Check(operation.text.IndexOf("ISOMETRIC VIEW ACTIVE", StringComparison.Ordinal) >= 0, "ISO camera", operation.text);
            Click(root.Q<Button>("view2Button"));
            Check(operation.text.IndexOf("MAP VIEW ACTIVE", StringComparison.Ordinal) >= 0, "MAP camera", operation.text);
            Click(root.Q<Button>("view3Button"));
            Check(operation.text.IndexOf("CHASE VIEW ACTIVE", StringComparison.Ordinal) >= 0, "CHASE camera", operation.text);

            Click(root.Q<Button>("patrolButton"));
            yield return new WaitForSecondsRealtime(0.25f);
            Check(_executor.ActivePolicyName == "PerimeterPatrolPolicy", "final scene left in patrol", _executor.ActivePolicyName);
            Click(root.Q<Button>("view1Button"));
            yield return new WaitForSecondsRealtime(0.15f);
            var preview = root.Q<Button>("previewToggleButton");
            if (preview.text.StartsWith("SHOW ", StringComparison.Ordinal))
            {
                Click(preview);
                yield return new WaitForSecondsRealtime(0.15f);
            }
            var masks = root.Q<Button>("maskToggleButton");
            if (masks.text.StartsWith("SHOW ", StringComparison.Ordinal))
            {
                Click(masks);
                yield return new WaitForSecondsRealtime(0.15f);
            }
            var lidar = root.Q<Button>("lidarToggleButton");
            if (lidar.text.StartsWith("SHOW ", StringComparison.Ordinal))
            {
                Click(lidar);
                yield return new WaitForSecondsRealtime(0.15f);
            }
            Check(lidar.text.StartsWith("HIDE ", StringComparison.Ordinal),
                "final LiDAR beams visible", lidar.text + " / " + operation.text);
            yield return new WaitForEndOfFrame();
            var capturePath = Path.Combine(Application.persistentDataPath, "hud-button-smoke-capture.png");
            ScreenCapture.CaptureScreenshot(capturePath, 1);
            yield return new WaitForEndOfFrame();
            Finish();
        }

        private IEnumerator TestMission(VisualElement root, Label operation, string buttonName, string expectedPolicy, string label)
        {
            Click(root.Q<Button>(buttonName));
            Check(operation.text.IndexOf("COMMAND ACCEPTED", StringComparison.Ordinal) >= 0,
                label + " immediate HUD acknowledgement", operation.text);
            yield return new WaitForSecondsRealtime(0.35f);
            Check(_executor.ActivePolicyName == expectedPolicy,
                label + " active policy", _executor.ActivePolicyName + " / " + _executor.LocalExecutionStatus);
        }

        private static void Click(Button button)
        {
            if (button == null || !button.enabledInHierarchy)
                throw new InvalidOperationException("A required HUD button is missing or disabled.");
            var point = button.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown,
                mousePosition = point,
                button = 0,
                clickCount = 1
            })) button.SendEvent(down);
            using (var up = PointerUpEvent.GetPooled(new Event
            {
                type = EventType.MouseUp,
                mousePosition = point,
                button = 0,
                clickCount = 1
            })) button.SendEvent(up);
        }

        private void Check(bool passed, string name, string evidence)
        {
            _checks++;
            if (!passed) _failures++;
            var line = (passed ? "PASS " : "FAIL ") + name + " · " + evidence;
            _report.AppendLine(line);
            if (passed) Debug.Log("OmniTwin HUD smoke: " + line);
            else Debug.LogError("OmniTwin HUD smoke: " + line);
        }

        private void Finish()
        {
            var summary = "HUD BUTTON SMOKE RESULT · " + (_checks - _failures) + "/" + _checks + " passed · " + _failures + " failed";
            _report.Insert(0, summary + Environment.NewLine);
            var path = Path.Combine(Application.persistentDataPath, "hud-button-smoke-test.txt");
            try { File.WriteAllText(path, _report.ToString()); }
            catch (IOException error) { Debug.LogError("OmniTwin HUD smoke report write failed: " + error.GetType().Name); }
            Debug.Log("OmniTwin HUD smoke: " + summary + " · report=" + path);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(_failures == 0 ? 0 : 1);
#endif
        }
    }
}
