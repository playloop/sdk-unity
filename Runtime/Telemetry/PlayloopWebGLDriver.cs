#nullable enable
#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Playloop.Telemetry
{
    /// <summary>
    /// WebGL-only main-loop driver. A Unity WebGL build is single-threaded with
    /// no ThreadPool, so the SDK's normal background drivers never run there:
    /// <see cref="TelemetryApi.AutoBatch"/>'s <c>Task.Run</c> flush loop never
    /// ticks and <see cref="HeartbeatEmitter"/>'s <c>System.Threading.Timer</c>
    /// never fires. This hidden MonoBehaviour pumps BOTH from <c>Update()</c> on
    /// the main thread instead, so WebGL games get periodic flushes + heartbeats
    /// with ZERO extra code - it is spawned automatically by the
    /// <see cref="PlayloopClient"/> constructor on WebGL.
    ///
    /// <para>
    /// Pairs with the <c>PlAwait</c> / ConfigureAwait fix: on WebGL the flush
    /// path captures the UnitySynchronizationContext, so the fire-and-forget
    /// <see cref="TelemetryApi.FlushAsync"/> below actually completes each cycle
    /// (resumes on the next frame) instead of stranding on the dead ThreadPool.
    /// </para>
    ///
    /// <para>Spawned with <see cref="HideFlags.HideAndDontSave"/> +
    /// <c>DontDestroyOnLoad</c> (mirrors <see cref="PlayloopFocusTracker"/>) and
    /// destroyed by <c>PlayloopClient.Dispose()</c>.</para>
    /// </summary>
    internal sealed class PlayloopWebGLDriver : MonoBehaviour
    {
        // Hard refs: PlayloopClient owns this GameObject's lifetime and destroys
        // it in Dispose() before the client/telemetry go away.
        private TelemetryApi? _telemetry;
        private HeartbeatEmitter? _heartbeat;
        private Playloop.Trace.TraceApi? _trace;

        private float _flushSec = 5f;
        private float _heartbeatSec = 60f;   // <= 0 disables heartbeats
        private float _nextFlush;
        private float _nextHeartbeat;
        private bool _flushInFlight;          // one flush in flight at a time

        internal void Attach(TelemetryApi telemetry, HeartbeatEmitter heartbeat, Playloop.Trace.TraceApi? trace, float flushSec, float heartbeatSec)
        {
            _telemetry = telemetry;
            _heartbeat = heartbeat;
            _trace = trace;
            if (flushSec > 0f) _flushSec = flushSec;
            _heartbeatSec = heartbeatSec;
            // First heartbeat fires promptly so the session reads as live well
            // inside the server's online window; first flush a beat later.
            _nextHeartbeat = 0f;
            _nextFlush = _flushSec;
        }

        private void Update()
        {
            float t = Time.unscaledTime;

            // The Trace samples on its own accumulator; one tick per frame.
            var trace = _trace;
            if (trace != null)
            {
                try { trace.Tick(Time.unscaledTimeAsDouble); }
                catch (Exception e) { Debug.LogWarning($"[Playloop] WebGL Trace tick failed: {e.Message}"); }
            }

            if (_heartbeat != null && _heartbeatSec > 0f && t >= _nextHeartbeat)
            {
                _nextHeartbeat = t + _heartbeatSec;
                try { _heartbeat.EmitOnce(); }
                catch (Exception e) { Debug.LogWarning($"[Playloop] WebGL heartbeat pump failed: {e.Message}"); }
            }

            if (t >= _nextFlush)
            {
                _nextFlush = t + _flushSec;
                Pump();
            }
        }

        // Fire-and-forget flush, one in flight at a time. FlushAsync resumes on
        // the main thread (PlAwait.Continue) so this completes each cycle on
        // WebGL; FlushAsync no-ops on an empty buffer, so the cadence is cheap.
        private async void Pump()
        {
            var telemetry = _telemetry;
            if (telemetry == null || _flushInFlight) return;
            _flushInFlight = true;
            try { await telemetry.FlushAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Playloop] WebGL flush pump failed: {e.Message}"); }
            finally { _flushInFlight = false; }
        }

        private void OnDestroy()
        {
            _telemetry = null;
            _heartbeat = null;
            _trace = null;
        }
    }
}
#endif
