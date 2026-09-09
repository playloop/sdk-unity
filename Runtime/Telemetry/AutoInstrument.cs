#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Playloop.Telemetry
{
    /// <summary>
    /// Hidden MonoBehaviour that auto-emits the "useful by default" telemetry
    /// events the SDK promises: scene changes, application errors, idle
    /// transitions, FPS drops, and (opt-in) memory pressure. Spawned by
    /// <see cref="TelemetryApi.AutoBatch"/> alongside the focus tracker. A
    /// consumer who calls <c>client.Telemetry.AutoBatch()</c> gets all of this
    /// for free.
    ///
    /// <para>
    /// The pure-C# rate-limit + threshold logic lives in
    /// <see cref="AutoInstrumentEngine"/> so it can be unit-tested without a
    /// Unity runtime. This file is just the glue that pumps Unity events
    /// (scene-changed callback, logMessageReceived, Update, OS input) into
    /// the engine and dispatches the resulting events to
    /// <see cref="TelemetryApi.Track(string, IReadOnlyDictionary{string, object})"/>.
    /// </para>
    /// </summary>
    internal sealed class AutoInstrument : MonoBehaviour
    {
        private TelemetryApi? _telemetry;
        private AutoInstrumentSettings _settings;
        private AutoInstrumentEngine? _engine;

        // Stable Vector3 reference for "mouse moved" detection.
        private Vector3 _lastMousePosition;
        private bool _hasMousePositionBaseline;

        // Subscriptions we own and need to unwire on destroy.
        private bool _subscribedScenes;
        private bool _subscribedLogs;

        /// <summary>
        /// Wire up the api + settings. Called by <see cref="TelemetryApi"/> right
        /// after <c>AddComponent</c> so we can install our Unity subscriptions
        /// from the main thread before the first Update tick.
        /// </summary>
        internal void Attach(TelemetryApi telemetry, AutoInstrumentSettings settings)
        {
            _telemetry = telemetry;
            _settings = settings;

            // Resolve the idle threshold default (30s desktop / 60s mobile)
            // here, on the Unity main thread, since Application.isMobilePlatform
            // is Unity-only.
            float idleThreshold = settings.IdleThresholdSec > 0f
                ? settings.IdleThresholdSec
                : (Application.isMobilePlatform ? 60f : 30f);
            _engine = new AutoInstrumentEngine(idleThreshold, settings.FpsDropThreshold);
            _engine.ResetIdle(Time.unscaledTime);

            if (settings.AutoInstrumentScenes)
            {
                SceneManager.activeSceneChanged += OnSceneChanged;
                _subscribedScenes = true;
            }

            if (settings.AutoInstrumentErrors)
            {
                Application.logMessageReceived += OnLogMessage;
                _subscribedLogs = true;
            }
        }

        private void OnDestroy()
        {
            // Unwire Unity-owned subscriptions so the engine doesn't keep
            // running after AutoBatch has been torn down. Each unsubscribe
            // is guarded so OnDestroy is safe to call from any teardown
            // order.
            if (_subscribedScenes)
            {
                try { SceneManager.activeSceneChanged -= OnSceneChanged; }
                catch { /* best effort */ }
                _subscribedScenes = false;
            }
            if (_subscribedLogs)
            {
                try { Application.logMessageReceived -= OnLogMessage; }
                catch { /* best effort */ }
                _subscribedLogs = false;
            }
            _telemetry = null;
            _engine = null;
        }

        // ───────────────────── scene_changed ─────────────────────

        private void OnSceneChanged(Scene from, Scene to)
        {
            var telemetry = _telemetry;
            if (telemetry == null) return;
            try
            {
                telemetry.Track("scene_changed", new Dictionary<string, object>
                {
                    // `from` is the empty scene on the very first load; emit
                    // a stable string so downstream consumers don't have to
                    // null-guard.
                    { "from", string.IsNullOrEmpty(from.name) ? "<initial>" : from.name },
                    { "to",   string.IsNullOrEmpty(to.name) ? "<initial>" : to.name },
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Playloop] scene_changed enqueue failed: {e.Message}");
            }
        }

        // ───────────────────── application_error ─────────────────────

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;

            var telemetry = _telemetry;
            var engine = _engine;
            if (telemetry == null || engine == null) return;

            if (!engine.TryRecordError()) return; // cap reached

            try
            {
                telemetry.Track("application_error", new Dictionary<string, object>
                {
                    { "message", condition ?? string.Empty },
                    { "stack",   stackTrace ?? string.Empty },
                    { "type",    type.ToString() },
                });
            }
            catch (Exception e)
            {
                // Catching here matters: an exception from inside the
                // logMessageReceived handler would itself be reported back
                // through logMessageReceived, recursing the SDK into an
                // infinite Track loop. Swallow + Debug.LogWarning is the
                // safe path.
                Debug.LogWarning($"[Playloop] application_error enqueue failed: {e.Message}");
            }
        }

        // ───────────────────── fps_drop + idle ─────────────────────

        private void Update()
        {
            var telemetry = _telemetry;
            var engine = _engine;
            if (telemetry == null || engine == null) return;

            float now = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;

            if (_settings.AutoInstrumentFps)
            {
                var fps = engine.RecordFrame(dt, now);
                if (fps.Fire)
                {
                    try
                    {
                        telemetry.Track("fps_drop", new Dictionary<string, object>
                        {
                            { "medianFps", (double)Math.Round(fps.MedianFps, 2) },
                            { "lowFps",    (double)Math.Round(fps.LowFps, 2) },
                            { "windowSec", (double)fps.WindowSec },
                        });
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Playloop] fps_drop enqueue failed: {e.Message}");
                    }
                }
            }

            if (_settings.AutoInstrumentIdle)
            {
                bool inputThisFrame = DetectInputThisFrame();
                var idle = engine.Tick(inputThisFrame, now);
                switch (idle.Outcome)
                {
                    case IdleDecision.Kind.Start:
                        try
                        {
                            telemetry.Track("idle_start", new Dictionary<string, object>
                            {
                                { "idleAfterSec", (double)idle.IdleThresholdSec },
                            });
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[Playloop] idle_start enqueue failed: {e.Message}");
                        }
                        break;
                    case IdleDecision.Kind.End:
                        try
                        {
                            telemetry.Track("idle_end", new Dictionary<string, object>
                            {
                                { "idleDurationSec", (double)Math.Round(idle.IdleDurationSec, 2) },
                            });
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[Playloop] idle_end enqueue failed: {e.Message}");
                        }
                        break;
                }
            }

            if (_settings.AutoInstrumentMemory)
            {
                long totalBytes = SystemInfo.systemMemorySize * 1024L * 1024L; // SystemInfo is MB
                long allocBytes = GC.GetTotalMemory(forceFullCollection: false);
                if (totalBytes > 0 && allocBytes >= (long)(totalBytes * 0.9))
                {
                    if (engine.TryRecordMemoryEvent(now))
                    {
                        try
                        {
                            telemetry.Track("memory_pressure", new Dictionary<string, object>
                            {
                                { "totalMb",     (double)Math.Round(totalBytes / (1024.0 * 1024.0), 1) },
                                { "allocatedMb", (double)Math.Round(allocBytes / (1024.0 * 1024.0), 1) },
                            });
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[Playloop] memory_pressure enqueue failed: {e.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Per-frame poll for "is the user touching anything?". Checks the
        /// old Input Manager APIs (Input.anyKey / Input.mousePosition delta /
        /// Input.touchCount). These are still available even when a project
        /// also uses the new Input System package. New-Input-System-only
        /// projects can opt out via <see cref="AutoInstrumentOptions.Idle"/>
        /// = false.
        /// </summary>
        private bool DetectInputThisFrame()
        {
            try
            {
                if (Input.anyKey) return true;
                if (Input.touchCount > 0) return true;
                var mouse = Input.mousePosition;
                if (!_hasMousePositionBaseline)
                {
                    _lastMousePosition = mouse;
                    _hasMousePositionBaseline = true;
                    return false;
                }
                if (mouse != _lastMousePosition)
                {
                    _lastMousePosition = mouse;
                    return true;
                }
                return false;
            }
            catch
            {
                // Some platforms (server builds, headless) throw on Input
                // access. Treat as "no input" so the idle path doesn't crash.
                return false;
            }
        }
    }
}
#endif
