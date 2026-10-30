using System;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;
using UnityEngine;

namespace OmniTwin.Simulation.Network
{
    /// <summary>Owns a desktop stream worker while exposing immutable state to Unity's main thread.</summary>
    [DisallowMultipleComponent]
    public sealed class TwinWebSocketClient : MonoBehaviour
    {
        [SerializeField] private string endpoint = "ws://127.0.0.1:5108/ws/twin";
        [SerializeField, Range(1, 10)] private int maximumAttempts = 3;
        private WorldStreamClient _client = new WorldStreamClient();
        private CancellationTokenSource _stop;
        private Task _worker;
        private long _loggedRevision = -1;

        /// <summary>Gets the latest atomic view; check component activity as well as observation freshness.</summary>
        public WorldStreamClientView View => _client.GetView();

        /// <summary>Reads freshness once for a presentation frame, including individual-track age information.</summary>
        public bool TryGetCurrentView(out WorldStreamClientView view)
        {
            view = _client.GetView();
            return isActiveAndEnabled && view.IsObservationCurrent;
        }

        /// <summary>Returns current validated data only while this component is active and enabled.</summary>
        public bool TryGetCurrentWorld(out WorldStreamMessage message)
        {
            var current = TryGetCurrentView(out var view);
            message = current ? view.State.Message : null;
            return message != null;
        }

        /// <summary>Starts a new bounded run; each run owns its client and cancellation source.</summary>
        [ContextMenu("Reconnect world stream")]
        public void Reconnect()
        {
            if (!gameObject.activeInHierarchy) return;
            enabled = false;
            enabled = true;
        }

        private void OnEnable()
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ||
                (address.Scheme != "ws" && address.Scheme != "wss") || address.UserInfo.Length != 0 || address.Fragment.Length != 0 ||
                maximumAttempts < 1 || maximumAttempts > 10)
            {
                Debug.LogError("OmniTwin: invalid stream endpoint or attempt budget.", this);
                enabled = false;
                return;
            }
            _client = new WorldStreamClient(maximumAttempts: maximumAttempts);
            _loggedRevision = -1;
            var client = _client;
            var stop = new CancellationTokenSource();
            _stop = stop;
            _worker = Task.Run(() => client.RunAsync(address, stop.Token));
        }

        private void Update()
        {
            var state = _client.GetView().State;
            if (state.Revision == _loggedRevision) return;
            _loggedRevision = state.Revision;
            if (state.Phase == WorldStreamClientPhase.Faulted)
                Debug.LogWarning("OmniTwin stream stopped: " + state.FailureCategory + ". Use Reconnect world stream after fixing the cause.", this);
        }

        private void OnDisable()
        {
            var stop = _stop;
            var worker = _worker;
            _stop = null;
            _worker = null;
            if (stop == null) return;
            stop.Cancel();
            if (worker == null) { stop.Dispose(); return; }
            _ = worker.ContinueWith(completed =>
            {
                // Observe faults even if the component was disabled before its next Update.
                var failure = completed.Exception;
                stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
