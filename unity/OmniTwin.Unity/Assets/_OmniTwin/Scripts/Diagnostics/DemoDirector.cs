using System;
using System.Collections;
using System.IO;
using OmniTwin.Contracts;
using OmniTwin.Simulation.UI;
using UnityEngine;

namespace OmniTwin.Simulation.Diagnostics
{
    /// <summary>Optional, deterministic presentation timeline; enabled only by the explicit standalone demo flag.</summary>
    [DisallowMultipleComponent]
    public sealed class DemoDirector : MonoBehaviour
    {
        private const float TotalDurationSeconds = 155f;
        private TacticalHudController _hud;
        private string _markerPath;

        /// <summary>Reports whether this Player process explicitly requested the recorded presentation.</summary>
        public static bool IsRequested
        {
            get
            {
                foreach (var argument in Environment.GetCommandLineArgs())
                    if (string.Equals(argument, "--omnitwin-demo", StringComparison.Ordinal)) return true;
                return false;
            }
        }

        private void Start()
        {
            if (!IsRequested) { enabled = false; return; }
            _hud = UnityEngine.Object.FindAnyObjectByType<TacticalHudController>();
            if (_hud == null) throw new InvalidOperationException("The demo director requires the TacticalDesk HUD.");
            _markerPath = ReadMarkerPath();
            PublishMarker("DEMO_STARTED");
            StartCoroutine(RunPresentation());
        }

        private IEnumerator RunPresentation()
        {
            var start = Time.realtimeSinceStartup;
            _hud.EnableDemoPerceptionOverlay();
            _hud.SetDemoCamera(1);
            _hud.RunDemoMission(SwarmMissionKind.PatrolPerimeter, "Demo baseline: patrol the calibrated workspace perimeter.");
            Present(1, "LIVE CAMERA · SAM 3.1 · CALIBRATED WORLD", "The overhead camera is live. SAM-verified masks and their calibrated world tracks are shown with their actual source and freshness.", DemoHudTarget.ViewIso);
            yield return Until(start, 25f);

            Present(2, "LIVE OBJECT MOTION · PAIRED CAMERA AND WORLD", "Move the tabletop marker during this scene: its live mask, projected track, and agent routes follow the camera observation.", DemoHudTarget.ViewMap);
            _hud.SetDemoCamera(2);
            yield return Until(start, 35f);
            _hud.SetDemoCamera(3, 1);
            _hud.SetDemoHighlight(DemoHudTarget.ViewChase);
            yield return Until(start, 41f);
            _hud.SetDemoCamera(3, 4);
            yield return Until(start, 47f);
            _hud.SetDemoCamera(1);
            yield return Until(start, 50f);

            Present(3, "OPERATOR TAG · HAZARD CORDON · GRID SEARCH", "The operator marks the current duck track as a hazard for this mission; this explicit override is not presented as model inference.", DemoHudTarget.Cordon);
            _hud.ApplyDemoOperatorHazardTag();
            _hud.RunDemoMission(SwarmMissionKind.HazardQuarantine, "Create a 2.8 metre safety cordon around the operator-tagged live hazard and keep aerial units on the outer patrol.", 2.8f);
            yield return Until(start, 62f);
            _hud.RunDemoMission(SwarmMissionKind.GridSearchAndRescue, "Systematically search the free workspace cells while avoiding current obstacle tracks.");
            yield return Until(start, 75f);

            Present(4, "MUSE VOICE · MUSE SPARK OPEN · ROSLYN · APPCONTAINER", "When RECORD VOICE highlights, say a short command. A fresh transcript is used when available; otherwise the visible typed fallback is labeled. Brain and sandbox status come from the backend.", DemoHudTarget.RecordVoice);
            _hud.BeginDemoVoiceCommand("Establish a 2.8 metre safety cordon around the operator-tagged live hazard and patrol the outer perimeter with aerial drones.");
            yield return Until(start, 110f);

            Present(5, "OBJECT SEMANTICS · MUSE IMAGE", "The semantics panel separates model tags from the operator's hazard override. Muse Image generation is submitted only when the backend reports the service as configured; its real result or fallback is shown.", DemoHudTarget.SemanticsTab);
            _hud.SelectDemoDock(1);
            yield return Until(start, 116f);
            _hud.SelectDemoDock(2);
            var submitted = _hud.RequestDemoSurface("Top-down industrial warehouse surface, dark steel floor with a restrained high-contrast cyan tactical grid, seamless and readable for navigation");
            if (!submitted)
                Present(5, "OBJECT SEMANTICS · MUSE IMAGE", "Muse Image request skipped because the backend did not confirm Meta readiness. The authored local surface remains active.", DemoHudTarget.SceneryTab);
            else
                Present(5, "OBJECT SEMANTICS · MUSE IMAGE", "A real Muse Image request was submitted. The surface panel reports the actual response; a validated local URP style remains available as fallback.", DemoHudTarget.GenerateSurface);
            yield return Until(start, 135f);

            Present(6, "OMNITWIN · LIVE DIGITAL-TWIN PIPELINE", "Five agents, live camera perception, obstacle-aware control, and isolated policy execution share one scene. Provider and sandbox cards report observed status, not assumed success.", DemoHudTarget.ViewIso);
            _hud.SetDemoCamera(1);
            _hud.SetDemoHighlight(DemoHudTarget.ViewIso);
            if (_hud.ActivePolicyNameForDemo == "EmergencyHoldPolicy")
                _hud.RunDemoMission(SwarmMissionKind.PatrolPerimeter, "Final presentation baseline patrol using current live camera observations.");
            yield return Until(start, TotalDurationSeconds);
            Present(6, "PRESENTATION COMPLETE", "The displayed source and service states are the captured run's actual values.", DemoHudTarget.None);
            PublishMarker("DEMO_FINISHED");
            Application.Quit(0);
        }

        private void Present(int act, string title, string subtitle, DemoHudTarget target)
        {
            _hud.SetDemoPresenter("CHAPTER " + act + "/6 · " + title, subtitle);
            _hud.SetDemoHighlight(target);
            PublishMarker("CHAPTER_" + act);
        }

        private static IEnumerator Until(float start, float targetSeconds)
        {
            while (Time.realtimeSinceStartup - start < targetSeconds) yield return null;
        }

        private static string ReadMarkerPath()
        {
            const string prefix = "--omnitwin-demo-marker=";
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var path = argument.Substring(prefix.Length).Trim('"');
                if (string.IsNullOrWhiteSpace(path) || path.Length > 2048) throw new ArgumentException("Invalid demo status marker path.");
                return Path.GetFullPath(path);
            }
            return null;
        }

        private void PublishMarker(string state)
        {
            if (string.IsNullOrEmpty(_markerPath)) return;
            try
            {
                var directory = Path.GetDirectoryName(_markerPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(_markerPath, state + "\n" + DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (IOException error) { Debug.LogWarning("OmniTwin demo marker unavailable: " + error.GetType().Name); }
            catch (UnauthorizedAccessException) { Debug.LogWarning("OmniTwin demo marker unavailable: access denied."); }
        }
    }

    /// <summary>Identifies the real HUD control emphasized during a demo step.</summary>
    public enum DemoHudTarget
    {
        /// <summary>No control is emphasized.</summary>
        None,
        /// <summary>Isometric camera preset.</summary>
        ViewIso,
        /// <summary>Map camera preset.</summary>
        ViewMap,
        /// <summary>Selected-agent chase camera preset.</summary>
        ViewChase,
        /// <summary>Perimeter patrol mission.</summary>
        Patrol,
        /// <summary>Hazard quarantine mission.</summary>
        Cordon,
        /// <summary>Grid search mission.</summary>
        Search,
        /// <summary>Emergency hold mission.</summary>
        Hold,
        /// <summary>Brain and voice dock tab.</summary>
        BrainTab,
        /// <summary>Object semantics dock tab.</summary>
        SemanticsTab,
        /// <summary>Scenery dock tab.</summary>
        SceneryTab,
        /// <summary>Brain synthesis button.</summary>
        Synthesize,
        /// <summary>Muse Image generation button.</summary>
        GenerateSurface,
        /// <summary>Microphone command capture button.</summary>
        RecordVoice,
        /// <summary>Fresh transcription handoff button.</summary>
        UseTranscript,
        /// <summary>Explicit operator hazard tag button.</summary>
        ApplyOperatorTag
    }
}
