#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using UnityEngine;

namespace Playloop
{
    /// <summary>
    /// Inspector-friendly Unity asset that mirrors <see cref="PlayloopOptions"/>.
    /// Lets consumers configure the SDK by right-clicking
    /// <c>Assets/Resources → Create → Playloop → Settings</c> and editing the
    /// fields in the Inspector: no code required for the common case. The
    /// asset's values are mapped to a fresh <see cref="PlayloopOptions"/>
    /// instance via <see cref="ToOptions"/>, which is what the client actually
    /// consumes.
    ///
    /// <para>
    /// Why a ScriptableObject (not just <see cref="PlayloopOptions"/> directly)?
    /// Unity's serializer only sees <c>[SerializeField]</c> fields, not the
    /// auto-properties on <see cref="PlayloopOptions"/>. Keeping these
    /// separate also means the C# API stays usable in non-Unity .NET hosts
    /// (Godot tests, CI, future server-side use) without dragging in
    /// <see cref="ScriptableObject"/> as a base class.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "PlayloopSettings",
        menuName = "Playloop/Settings",
        order = 100)]
    public sealed class PlayloopSettings : ScriptableObject
    {
        [Header("Connection")]
        [Tooltip("Your game's Playloop ingest key (pl_ik_...). Required. Each game has its own; copy it from the game's Connections → Unity page. Telemetry sent with it is attributed to that game automatically.")]
        [SerializeField] private string apiKey = "";

        [Tooltip("Base URL for the Playloop API. Leave as the default. Advanced/testing use only.")]
        [SerializeField] private string baseUrl = "https://playloop.gg";

        [Tooltip("Environment slug stamped on every session (dev / demo / production / etc.). Lowercased + trimmed server-side. Left at the default 'dev' (or empty), the SDK auto-derives it from the build: editor / development build → 'dev', shipping release build → 'production'. Any explicit non-default value overrides the auto-derive.")]
        [SerializeField] private string environment = "dev";

        [Tooltip("Send telemetry from the Unity editor + development builds. OFF by default: editor playtesting is suppressed so it doesn't pollute real session data (logs a one-time warning). Turn ON to test the ingest pipeline locally; pair with Environment = 'dev' so the traffic is easy to filter out. No effect in release builds.")]
        [SerializeField] private bool sendInEditor = false;

        [Header("Telemetry batching")]
        [Tooltip("How often the auto-batch loop flushes telemetry, in milliseconds.")]
        [SerializeField, Min(100)] private int flushIntervalMs = 5000;

        [Tooltip("Max events held in memory before the oldest is evicted. Bounds memory if Track() is faster than Flush().")]
        [SerializeField, Min(1)] private int maxBufferSize = 200;

        [Tooltip("Per-request timeout in seconds.")]
        [SerializeField, Min(1)] private int timeoutSeconds = 30;

        [Header("Retry / backoff")]
        [Tooltip("Total HTTP attempts including the first. Default 3 (i.e. 2 retries). Set to 1 to disable retries entirely. Canonical across Unity/Godot/Python/TypeScript SDKs.")]
        [SerializeField, Min(1)] private int retryAttempts = 3;

        [Tooltip("Base delay between retries in milliseconds. Exponential backoff multiplies this by 2^(attempt-1), capped at Retry Max Ms, with ±25% jitter.")]
        [SerializeField, Min(0)] private int retryBaseMs = 500;

        [Tooltip("Max delay cap between retries in milliseconds. Retry-After response headers are bounded at 2× this value.")]
        [SerializeField, Min(0)] private int retryMaxMs = 5000;

        [Header("Shutdown")]
        [Tooltip("Auto-flush + end session on Application.quitting (built players) and on Editor play-mode stop. Disable only if you manage shutdown yourself.")]
        [SerializeField] private bool autoShutdownOnQuit = true;

        [Tooltip("Max time the auto-shutdown hook blocks Unity's quit path waiting for the final HTTP POST.")]
        [SerializeField, Min(0)] private int shutdownFlushTimeoutMs = 3000;

        [Header("Auto-instrumentation")]
        [Tooltip("Built-in auto-events that fire from Telemetry.AutoBatch(). Drop the SDK in and useful telemetry shows up without writing any Track() calls.")]
        [SerializeField] private AutoInstrumentBlock autoInstrument = new();

        /// <summary>
        /// Builds a fresh <see cref="PlayloopOptions"/> populated from this
        /// asset's inspector values. Call once at startup; mutating the asset
        /// afterward does NOT propagate to a client that was already built.
        /// </summary>
        public PlayloopOptions ToOptions()
        {
            return new PlayloopOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                Environment = environment,
                SendInEditor = sendInEditor,
                TelemetryFlushIntervalMs = flushIntervalMs,
                TelemetryMaxBufferSize = maxBufferSize,
                TimeoutSeconds = timeoutSeconds,
                RetryAttempts = retryAttempts,
                RetryBaseMs = retryBaseMs,
                RetryMaxMs = retryMaxMs,
                AutoShutdownOnQuit = autoShutdownOnQuit,
                ShutdownFlushTimeoutMs = shutdownFlushTimeoutMs,
                AutoInstrument = new AutoInstrumentOptions
                {
                    Scenes = autoInstrument.scenes,
                    Errors = autoInstrument.errors,
                    Idle = autoInstrument.idle,
                    Fps = autoInstrument.fps,
                    Memory = autoInstrument.memory,
                    IdleThresholdSec = autoInstrument.idleThresholdSec,
                    FpsDropThreshold = autoInstrument.fpsDropThreshold,
                },
            };
        }

        /// <summary>
        /// Never-raise loader - the recommended one-liner. Loads the settings
        /// asset from <c>Assets/Resources/{resourcePath}.asset</c> and converts
        /// it to <see cref="PlayloopOptions"/>. When the asset is missing, logs
        /// one actionable warning and returns a blank <see cref="PlayloopOptions"/>
        /// (empty key), so
        /// <c>new PlayloopClient(PlayloopSettings.Load())</c> constructs a
        /// DISABLED client instead of throwing. A game that forgot to add the
        /// asset keeps running; check <c>PlayloopClient.IsEnabled</c> to detect
        /// the misconfiguration.
        /// </summary>
        public static PlayloopOptions Load(string resourcePath = "PlayloopSettings")
        {
            var asset = Resources.Load<PlayloopSettings>(resourcePath);
            if (asset == null)
            {
                Debug.LogWarning(
                    $"[Playloop] No PlayloopSettings asset at Assets/Resources/{resourcePath}.asset - SDK disabled. " +
                    "Right-click Assets/Resources in the Project view → Create → Playloop → Settings, " +
                    "fill in your ingest key, then make sure the file lives under Resources/ so it ships with the build. " +
                    "The client is safe to use meanwhile; check PlayloopClient.IsEnabled.");
                return new PlayloopOptions();
            }
            return asset.ToOptions();
        }

        /// <summary>
        /// Strict loader - same as <see cref="Load"/> but THROWS an
        /// <see cref="InvalidOperationException"/> when the asset is missing
        /// instead of disabling the SDK. Opt into this only when you want a
        /// hard fail-fast (e.g. a CI smoke test that must never ship without
        /// the asset). For a shipping game prefer <see cref="Load"/>, which is
        /// never-raise.
        /// </summary>
        public static PlayloopOptions LoadOrThrow(string resourcePath = "PlayloopSettings")
        {
            var asset = Resources.Load<PlayloopSettings>(resourcePath);
            if (asset == null)
            {
                throw new InvalidOperationException(
                    $"Playloop: no PlayloopSettings asset at Assets/Resources/{resourcePath}.asset. " +
                    "Right-click Assets/Resources in the Project view → Create → Playloop → Settings, " +
                    "fill in your ingest key, then make sure the file lives under Resources/ so it ships with the build.");
            }
            return asset.ToOptions();
        }

        // ─────────────────────────────────────────────────────────────────
        // Serializable nested block. Unity's serializer can't see auto-
        // properties (which is what PlayloopOptions.AutoInstrument exposes),
        // so the inspector-facing copy uses plain public fields. Fields are
        // private to keep the asset's surface immutable from outside code;
        // ToOptions() is the only consumer.
        // ─────────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class AutoInstrumentBlock
        {
            [Tooltip("Emit scene_changed on SceneManager.activeSceneChanged with { from, to }.")]
            public bool scenes = true;

            [Tooltip("Emit application_error on uncaught exceptions. Hard-capped at 50/session so a stuck error loop can't drain the event budget.")]
            public bool errors = true;

            [Tooltip("Emit idle_start / idle_end based on input activity (Input.anyKey, mouse movement, touches).")]
            public bool idle = true;

            [Tooltip("Emit fps_drop when rolling 3s median FPS falls below threshold. Throttled to one event per 5 seconds.")]
            public bool fps = true;

            [Tooltip("Emit memory_pressure when GC.GetTotalMemory crosses 90% of SystemInfo.systemMemorySize. Off by default: noisy on low-memory devices.")]
            public bool memory = false;

            [Tooltip("Seconds of no input before idle_start fires. 0 = auto (30s desktop / 60s mobile).")]
            [Min(0f)]
            public float idleThresholdSec = 0f;

            [Tooltip("Median FPS below which fps_drop fires. Default 30.")]
            [Min(1f)]
            public float fpsDropThreshold = 30f;
        }
    }
}
#endif
