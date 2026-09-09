#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Playloop.Telemetry
{
    /// <summary>
    /// SDK-owned heartbeat emitter.
    ///
    /// <para>
    /// Every <c>IntervalSec</c> seconds (default 60), fires one
    /// <c>session_heartbeat</c> event carrying:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>tickNumber</c>: monotonic 1, 2, 3 … for ordering</item>
    ///   <item><c>playTimeSec</c>: elapsed seconds since the timer
    ///   started</item>
    ///   <item>All current state from <see cref="StateApi.Snapshot"/>
    ///   at TOP LEVEL</item>
    /// </list>
    ///
    /// <para>
    /// Heartbeats are the canonical liveness signal AND the
    /// crash-recovery source: the heartbeat carries your latest state
    /// snapshot, so if the app is force-quit (force-kill, OS-evicted,
    /// lost network) the final <c>session_summary</c> is still
    /// recovered from the last heartbeat.
    /// </para>
    ///
    /// <para>
    /// Set <c>intervalSec: 0</c> to disable heartbeats entirely.
    /// </para>
    ///
    /// <para>
    /// Threading: uses <c>System.Threading.Timer</c>. The callback
    /// fires on a thread-pool thread, which is safe because
    /// <c>TelemetryApi.Track</c> is itself thread-safe. The timer is
    /// background-only. It doesn't block app shutdown.
    /// </para>
    /// </summary>
    public sealed class HeartbeatEmitter
    {
        private readonly TelemetryApi _telemetry;
        private readonly StateApi _state;
        private readonly double _intervalSec;
        private readonly object _lock = new object();
        private Timer? _timer;
        private DateTime _startedAtUtc = DateTime.UtcNow;
        private int _tick;
        private bool _stopped;

        internal HeartbeatEmitter(TelemetryApi telemetry, StateApi state, double intervalSec)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _intervalSec = intervalSec;
        }

        /// <summary>Start the periodic emission. Idempotent: repeat
        /// calls are no-ops after the first.</summary>
        public void Start()
        {
            if (_intervalSec <= 0) return;
            lock (_lock)
            {
                if (_timer != null || _stopped) return;
                _startedAtUtc = DateTime.UtcNow;
                _tick = 0;
                var periodMs = (long)(_intervalSec * 1000.0);
                _timer = new Timer(OnTick, null, periodMs, periodMs);
            }
        }

        /// <summary>Stop the timer. Idempotent.</summary>
        public void Stop()
        {
            Timer? timer;
            lock (_lock)
            {
                _stopped = true;
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
        }

        /// <summary>
        /// Emit one heartbeat row immediately. Exposed for tests
        /// (deterministic firing without waiting on the interval) and
        /// for callers that want to force a beat ahead of an
        /// auto-flush.
        /// </summary>
        public void EmitOnce()
        {
            int tick;
            int playTimeSec;
            lock (_lock)
            {
                _tick += 1;
                tick = _tick;
                playTimeSec = (int)Math.Max(0, (DateTime.UtcNow - _startedAtUtc).TotalSeconds);
            }
            var snapshot = _state.Snapshot();
            var payload = new Dictionary<string, object>(snapshot.Count + 2)
            {
                { "tickNumber", tick },
                { "playTimeSec", playTimeSec },
            };
            foreach (var kvp in snapshot)
            {
                // Don't let a stray "tickNumber" / "playTimeSec" key
                // in the dev's state shadow the SDK-owned fields.
                if (payload.ContainsKey(kvp.Key)) continue;
                payload[kvp.Key] = kvp.Value;
            }
            _telemetry.Track("session_heartbeat", payload);
        }

        /// <summary>Read-only: number of heartbeats fired so far.</summary>
        public int TickNumber
        {
            get { lock (_lock) return _tick; }
        }

        /// <summary>Read-only: whether the timer is currently running.</summary>
        public bool IsRunning
        {
            get { lock (_lock) return _timer != null; }
        }

        private void OnTick(object? state)
        {
            try
            {
                EmitOnce();
            }
            catch
            {
                // Best-effort. Never throw from the timer thread.
            }
        }
    }
}
