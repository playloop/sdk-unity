#nullable enable
using System;

namespace Playloop
{
    /// <summary>
    /// Configuration for <see cref="PlayloopClient"/>. Construct one and pass it to the
    /// client constructor. All settings are read-only after the client is built.
    /// </summary>
    public sealed class PlayloopOptions
    {
        /// <summary>When false, do not install the automatic crash trap or drain saved crash reports.</summary>
        public bool EnableCrashReporting { get; set; } = true;

        /// <summary>Your Playloop API key (pl_ik_...). Required.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Base URL for the Playloop API. Defaults to https://playloop.gg.</summary>
        public string BaseUrl { get; set; } = "https://playloop.gg";

        /// <summary>The default <see cref="Environment"/>. When the consumer leaves
        /// <see cref="Environment"/> at this value (or empty), the SDK auto-derives the
        /// environment from the build (development build → <c>"dev"</c>, a shipping
        /// release build → <c>"production"</c>). An explicit non-default value always
        /// wins.</summary>
        public const string DefaultEnvironment = "dev";

        /// <summary>
        /// Environment slug stamped onto sessions created by this client. Sent on every
        /// request as the <c>X-Playloop-Environment</c> header. Slug shape is validated
        /// server-side; the SDK lowercases + trims once at construction time.
        ///
        /// <para>
        /// Defaults to <c>"dev"</c> (<see cref="DefaultEnvironment"/>). When left
        /// at the default (or empty), the SDK auto-derives the environment from the
        /// build: a development build (the editor, a <c>Debug.isDebugBuild</c> player,
        /// or a <c>DEVELOPMENT_BUILD</c>-compiled player) stays <c>"dev"</c>, and a
        /// shipping release build is promoted to <c>"production"</c>. Set this to any
        /// explicit non-default slug (<c>"production"</c>, <c>"demo"</c>, …) to override
        /// the auto-derive entirely.
        /// </para>
        /// </summary>
        public string Environment { get; set; } = DefaultEnvironment;

        /// <summary>
        /// When <c>false</c> (the default), telemetry sends are SUPPRESSED while running
        /// in the Unity editor or a development build, so editor playtesting and local
        /// debug runs don't pollute your real session data. The SDK logs a one-time
        /// <c>Debug.LogWarning</c> explaining the suppression and how to opt in.
        ///
        /// <para>
        /// Set <c>true</c> to send telemetry from the editor / development builds anyway
        /// (e.g. when you're deliberately testing the ingest pipeline end-to-end). Pair
        /// it with an explicit <c>Environment = "dev"</c> so the editor traffic is tagged
        /// and easy to filter out of production numbers. Has no effect in a release
        /// player build (nothing is suppressed there).
        /// </para>
        /// </summary>
        public bool SendInEditor { get; set; } = false;

        /// <summary>How often the auto-batch loop flushes telemetry, in milliseconds.</summary>
        public int TelemetryFlushIntervalMs { get; set; } = 5000;

        /// <summary>
        /// Max events held in memory before the oldest is evicted. Bounds memory if
        /// you call <c>Track()</c> faster than <c>Flush()</c> can drain it.
        /// </summary>
        public int TelemetryMaxBufferSize { get; set; } = 200;

        /// <summary>Per-request timeout in seconds. Default 30s.</summary>
        public int TimeoutSeconds { get; set; } = 30;

        // ─────────────────────────── Retry / backoff ───────────────────────────
        // Canonical contract: matches the Godot / Python / TypeScript SDKs.
        // See Runtime/Http/RetryPolicy.cs.

        /// <summary>
        /// Total HTTP attempts including the first. Default 3 (i.e. 2 retries).
        /// Set to 1 to disable retries.
        /// </summary>
        public int RetryAttempts { get; set; } = 3;

        /// <summary>Base delay between retries in milliseconds. Default 500.</summary>
        public int RetryBaseMs { get; set; } = 500;

        /// <summary>Max delay cap between retries in milliseconds. Default 5000.</summary>
        public int RetryMaxMs { get; set; } = 5000;

        /// <summary>
        /// Optional persistent device id stamped on the session row when the
        /// server creates a new session (ignored on appends). Forwarded to
        /// <see cref="Telemetry.TelemetryApi"/>.
        ///
        /// <para>
        /// Leave this null (the default) and the SDK calls
        /// <see cref="DeviceIdResolver.Resolve"/> at construction time:
        /// hardware id first (<c>SystemInfo.deviceUniqueIdentifier</c>), then
        /// a persisted GUID fallback. That's the right choice for almost every
        /// consumer; PlayerPrefs-only schemes fragment silently across
        /// editor↔build, bundle-id changes, and "Clear All PlayerPrefs"
        /// actions.
        /// </para>
        ///
        /// <para>
        /// Set explicitly when you need an override: testing, PlayFab
        /// CustomID linkage, deterministic IDs in CI, etc. Server rejects
        /// values longer than 128 chars.
        /// </para>
        /// </summary>
        public string? DeviceId { get; set; } = null;

        /// <summary>
        /// Eagerly fetch A/B experiment assignments at construction instead of
        /// lazily on the first <c>client.Experiments.VariantAsync()</c> call.
        /// Default <c>false</c>. Games that never run experiments pay no
        /// network cost, and games that do pay it once, early. Set
        /// <c>true</c> when the very first thing you render depends on a
        /// variant (e.g. a title-screen experiment) and you want the
        /// synchronous <c>Experiments.Variant()</c> read to be warm by then.
        /// </summary>
        public bool PrefetchExperiments { get; set; } = false;

        /// <summary>
        /// Discord relay secret, sent as the <c>x-playloop-secret</c> header on
        /// Discord ingest calls. Copy it from Connections &gt; Discord on
        /// playloop.gg (it's shown once; rotate it there any time).
        ///
        /// <para>
        /// Only needed by a trusted relay process that calls
        /// <see cref="Discord.DiscordApi.IngestAsync"/>. Like the management
        /// key, keep it server-side; never ship it in a player build. A
        /// per-call value passed to <c>IngestAsync</c> overrides this one.
        /// </para>
        /// </summary>
        public string? RelaySecret { get; set; } = null;

        /// <summary>
        /// Optional custom HTTP transport. When null, the client picks a default:
        /// <c>UnityWebRequestHandler</c> inside Unity, <c>DefaultHttpHandler</c> otherwise.
        /// Tests inject a mock here.
        /// </summary>
        public Http.IHttpHandler? Http { get; set; }

        /// <summary>
        /// When true (default), the client hooks <c>Application.quitting</c> at
        /// construction so the last batch of events is flushed and the session
        /// is ended automatically when the game quits. Without this, async work
        /// in user-side <c>OnApplicationQuit</c> handlers is abandoned by Unity
        /// before the HTTP POST completes. The final session would be lost.
        /// Set false only if you want to manage shutdown yourself.
        ///
        /// Has no effect outside Unity (standalone .NET / Godot test runs).
        /// </summary>
        public bool AutoShutdownOnQuit { get; set; } = true;

        /// <summary>
        /// Max time the auto-shutdown hook blocks Unity's quit path waiting
        /// for the final HTTP POST to complete. Past this, the SDK gives up
        /// and lets the game exit. No point holding the editor or player
        /// hostage on a stuck network. Default 3000ms.
        /// </summary>
        public int ShutdownFlushTimeoutMs { get; set; } = 3000;

        /// <summary>
        /// Heartbeat cadence in seconds. The SDK emits one <c>session_heartbeat</c>
        /// event every <see cref="HeartbeatSec"/> seconds while the
        /// session is active. Each beat carries the current
        /// <see cref="Playloop.Telemetry.StateApi.Snapshot"/> at the
        /// event's top level (no <c>summary:</c> sub-key) plus
        /// <c>tickNumber</c> and <c>playTimeSec</c>.
        ///
        /// <para>
        /// The heartbeat carries your latest state snapshot, so if the
        /// app is force-quit (force-kill, OS-evicted, lost network) the
        /// final <c>session_summary</c> is still recovered from the last
        /// heartbeat.
        /// </para>
        ///
        /// <para>
        /// Default <c>60</c>. Set to <c>0</c> to disable heartbeats
        /// entirely. Values in <c>(0, 30)</c> are clamped to the 30s
        /// floor at construction time with a <c>Debug.LogWarning</c>:
        /// a sub-30s cadence emits a ton of events for marginal
        /// crash-recovery benefit (1s = 3600 events/hr per session).
        /// Most games should keep the default 60s.
        /// </para>
        /// </summary>
        public double HeartbeatSec { get; set; } = 60.0;

        /// <summary>
        /// Floor on <see cref="HeartbeatSec"/>. Values in
        /// <c>(0, MinHeartbeatSec)</c> are clamped at construction
        /// time. Exposed as a public constant so tests + diagnostics
        /// can reference the same boundary.
        /// </summary>
        public const double MinHeartbeatSec = 30.0;

        /// <summary>
        /// Built-in auto-events that fire from <c>Telemetry.AutoBatch()</c> so
        /// a dev who drops the SDK in sees useful data without writing a
        /// single <c>Track()</c> call. Each event has an independent
        /// kill-switch. See <see cref="AutoInstrumentOptions"/> for the per-event
        /// flags and <c>Runtime/Telemetry/AutoInstrument.cs</c> for the
        /// implementation.
        /// </summary>
        public AutoInstrumentOptions AutoInstrument { get; set; } = new();

        /// <summary>
        /// Sampled session state for Playback (position, room, action bits,
        /// axes, a few named entities), sent as <c>trace_chunk</c> events.
        /// <c>Mode</c> defaults to <c>Auto</c>: on everywhere except a
        /// <c>"production"</c> environment. See <see cref="Trace.TraceOptions"/>.
        /// </summary>
        public Playloop.Trace.TraceOptions Trace { get; set; } = new();

        /// <summary>
        /// True when this options bag carries the minimum config the client
        /// needs to actually talk to Playloop: a non-empty ingest key and a
        /// non-empty base URL. When false, <see cref="PlayloopClient"/>
        /// constructs in DISABLED mode (never-raise) instead of throwing -
        /// a game that ships with a blank key keeps running, telemetry just
        /// goes nowhere. See the client's <c>IsEnabled</c> property.
        ///
        /// <para>
        /// Retry values are NOT part of this check: a bad retry setting is
        /// clamped to a sane value (see <see cref="BuildRetryPolicy"/>), not
        /// a reason to disable the SDK.
        /// </para>
        /// </summary>
        internal bool IsConfigured =>
            !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(BaseUrl);

        /// <summary>
        /// Build the retry policy, clamping out-of-range values to their
        /// nearest legal value rather than throwing. Matches the Godot SDK's
        /// clamp-and-carry-on posture (attempts &gt;= 1, baseMs &gt;= 0,
        /// maxMs &gt;= baseMs). A misconfigured retry knob should never abort
        /// construction: the SDK is never-raise.
        /// </summary>
        internal Http.RetryPolicy BuildRetryPolicy()
        {
            int attempts = Math.Max(1, RetryAttempts);
            int baseMs = Math.Max(0, RetryBaseMs);
            int maxMs = Math.Max(baseMs, RetryMaxMs);
            return new Http.RetryPolicy
            {
                Attempts = attempts,
                BaseMs   = baseMs,
                MaxMs    = maxMs,
            };
        }
    }

    /// <summary>
    /// Per-event toggles + thresholds for the SDK's built-in auto-events. The
    /// defaults are tuned so "drop the SDK in and walk away" produces useful
    /// telemetry on day one; flip individual flags when a specific game
    /// generates too much noise from that source.
    /// </summary>
    public sealed class AutoInstrumentOptions
    {
        /// <summary>
        /// Emit a <c>scene_changed</c> event on
        /// <c>SceneManager.activeSceneChanged</c> with <c>{ from, to }</c>.
        /// Default true.
        /// </summary>
        public bool Scenes { get; set; } = true;

        /// <summary>
        /// Emit an <c>application_error</c> event for every
        /// <c>Application.logMessageReceived</c> at <c>Exception</c> /
        /// <c>Error</c> / <c>Assert</c> severity, with
        /// <c>{ message, stack, type }</c>. Hard-capped at 50 events per
        /// session so a stuck exception loop can't drain the per-session
        /// event budget. Default true.
        /// </summary>
        public bool Errors { get; set; } = true;

        /// <summary>
        /// Emit <c>idle_start</c> after <see cref="IdleThresholdSec"/> seconds
        /// of no input (no <c>Input.anyKey</c>, mouse movement, or touch
        /// input), and <c>idle_end</c> when input resumes. Default true.
        /// </summary>
        public bool Idle { get; set; } = true;

        /// <summary>
        /// Emit a <c>fps_drop</c> event when the rolling 3-second median FPS
        /// falls below <see cref="FpsDropThreshold"/>. Throttled to one event
        /// per 5 seconds so a sustained slow patch doesn't spam. Default true.
        /// </summary>
        public bool Fps { get; set; } = true;

        /// <summary>
        /// Emit a <c>memory_pressure</c> event when
        /// <c>GC.GetTotalMemory(false)</c> crosses 90% of
        /// <c>SystemInfo.systemMemorySize</c>. Opt-in because it's noisy on
        /// low-memory devices. Default false.
        /// </summary>
        public bool Memory { get; set; } = false;

        /// <summary>
        /// Seconds of no input before <c>idle_start</c> fires. Default 30s on
        /// desktop / 60s on mobile (auto-detected from
        /// <c>Application.isMobilePlatform</c> when this stays at its sentinel
        /// default of 0). Set explicitly to override.
        /// </summary>
        public float IdleThresholdSec { get; set; } = 0f;

        /// <summary>
        /// Rolling-3-second-median FPS below which a <c>fps_drop</c> event
        /// fires. Default 30 FPS: the common "noticeably choppy" threshold.
        /// Throttled to one event per 5 seconds.
        /// </summary>
        public float FpsDropThreshold { get; set; } = 30f;
    }
}
