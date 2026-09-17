#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playloop.BugReports;
using Playloop.Discord;
using Playloop.Experiments;
using Playloop.Feedback;
using Playloop.Http;
using Playloop.Playtest;
using Playloop.Resolve;
using Playloop.Sessions;
using Playloop.Telemetry;
using Playloop.Trace;

namespace Playloop
{
    /// <summary>
    /// Main entry point for the Playloop Unity SDK.
    ///
    /// <code>
    /// var client = new PlayloopClient(new PlayloopOptions { ApiKey = "pl_ik_..." });
    /// var session = await client.Sessions.IngestAsync(File.ReadAllBytes("session.mp4"), "necromancers-army");
    /// client.Telemetry.Track("death", new Dictionary&lt;string, object&gt; { { "room", "cave-2" } });
    /// client.Telemetry.AutoBatch();
    /// </code>
    ///
    /// Shutdown is automatic: the client hooks
    /// <c>UnityEngine.Application.quitting</c> at construction, then on quit
    /// runs a bounded synchronous flush of the final telemetry batch. Without
    /// this, Unity tears down the AppDomain before any user-side async
    /// <c>OnApplicationQuit</c> handler can complete its HTTP POST, and the
    /// final session is lost. Opt out via
    /// <see cref="PlayloopOptions.AutoShutdownOnQuit"/> only if you have a
    /// reason to manage shutdown yourself.
    /// </summary>
    public sealed class PlayloopClient : IDisposable
    {
        private readonly IHttpHandler _httpHandler;
        private readonly bool _autoShutdownEnabled;
        private readonly int _shutdownFlushTimeoutMs;
        private readonly GameResolver _resolver;
        private readonly string _environment;
        private readonly bool _enabled;
        private bool _disposed;
#if UNITY_WEBGL && !UNITY_EDITOR
        private UnityEngine.GameObject? _webGLDriverGo;
#endif
        // Crash handler handle, kept so Dispose can tear the trap down.
        private CrashHandler.Handle? _crashHandlerHandle;
        private readonly bool _enableCrashReporting;

        // Keep the SDK version in lockstep with `package.json`. Stamped
        // on every `session_start` event so the dashboard can surface
        // "shipped on playloop-unity@X" per session detail page.
        private const string SdkVersion = "playloop-unity@0.5.0";

        /// <summary>
        /// Last-instantiated <see cref="PlayloopClient"/>, exposed so editor
        /// tooling (Send Test Event, Status window verification) can find the
        /// active client without the host game having to wire its own
        /// singleton/bootstrap. Set on construction (last-write-wins),
        /// cleared on <see cref="Dispose"/> when the disposed instance is
        /// the current one.
        ///
        /// <para>
        /// Editor-only contract: do NOT rely on this from gameplay code.
        /// It goes null between Play sessions and after disposal. Inside
        /// the game, keep your own reference to the client you built.
        /// </para>
        /// </summary>
        public static PlayloopClient? Current { get; private set; }

        /// <summary>
        /// Game URL slug this client resolved from its ingest key, or
        /// <c>null</c> until the lazy resolve has settled (or if it failed).
        /// Surfaced as a public read-only property so editor tooling can
        /// compose dashboard URLs from the cached resolve without awaiting.
        /// </summary>
        public string? GameSlug => _resolver.Snapshot()?.Slug;

        /// <summary>
        /// Resolves the game identity (gameId + slug + name) from the ingest
        /// key, lazily and cached. Telemetry never awaits it; event-config and
        /// tester-keys do. Exposed so editor tooling can read the resolved
        /// game name for the connection pill.
        /// </summary>
        public GameResolver Resolver => _resolver;

        /// <summary>
        /// Environment slug this client was constructed with (dev /
        /// demo / production / custom). Same rationale as
        /// <see cref="GameSlug"/>: editor tooling needs it to compose the
        /// dashboard URL after Send Test Event.
        /// </summary>
        public string Environment => _environment;

        /// <summary>
        /// Whether this client is live. <c>true</c> for a normally-configured
        /// client; <c>false</c> when it was constructed without a usable ingest
        /// key (or base URL) and is running in DISABLED mode.
        ///
        /// <para>
        /// A disabled client is always SAFE to construct and call - the
        /// constructor never throws on a blank key, and every API is a no-op:
        /// <c>Telemetry.Track</c> drops silently, <c>Experiments.Variant</c> /
        /// <c>VariantAsync</c> return the control default (<c>null</c>), the
        /// feedback form resolves as <c>Failed</c> without showing UI, session
        /// / ingest calls fail soft, and no heartbeat, auto-instrumentation, or
        /// crash trap is ever started. Read this flag to detect misconfiguration
        /// (e.g. surface "Playloop not configured" in your own dev tooling) -
        /// never-raise does not mean invisible.
        /// </para>
        /// </summary>
        public bool IsEnabled => _enabled;

        public SessionsApi Sessions { get; }
        public TelemetryApi Telemetry { get; }
        public DiscordApi Discord { get; }
        /// <summary>Tester Keys correlation. Backs <see cref="LinkTesterAsync"/> / <see cref="GetCurrentTesterAsync"/> / <see cref="UnlinkTesterAsync"/>.</summary>
        public PlaytestApi Playtest { get; }

        /// <summary>
        /// Player Feedback: multi-field form submissions. Backs
        /// <see cref="SubmitFeedbackAsync"/>. Free for every studio.
        /// </summary>
        public FeedbackApi Feedback { get; }

        /// <summary>
        /// Player Bug Reports: title + description + severity submissions.
        /// Backs <see cref="SubmitBugReportAsync"/>. Free for every studio.
        /// </summary>
        public BugReportApi BugReports { get; }

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
        /// <summary>
        /// Default UGUI form for Player Feedback. Builds a Canvas +
        /// VerticalLayoutGroup at runtime; no prefab asset to maintain.
        /// Devs who want a custom UI can ignore this entirely and call
        /// <see cref="FeedbackApi.SubmitAsync"/> directly from their
        /// own overlay.
        ///
        /// Only available inside Unity: the standalone .NET build
        /// (CI tests via <c>PLAYLOOP_DOTNET_STANDALONE</c>) excludes
        /// the UGUI surface so the test project stays decoupled from
        /// UnityEngine.UI.
        /// </summary>
        public FeedbackForm FeedbackForm { get; }

        /// <summary>
        /// Default UGUI form for Bug Reports. Builds a Canvas +
        /// VerticalLayoutGroup at runtime; no prefab asset to maintain.
        /// Devs who want a custom UI can ignore this entirely and call
        /// <see cref="BugReportApi.SubmitAsync"/> directly from their
        /// own overlay.
        ///
        /// Only available inside Unity: the standalone .NET build
        /// (CI tests via <c>PLAYLOOP_DOTNET_STANDALONE</c>) excludes
        /// the UGUI surface so the test project stays decoupled from
        /// UnityEngine.UI.
        /// </summary>
        public BugReportForm BugReportForm { get; }
#endif
        /// <summary>
        /// Session-state accumulator. Devs call
        /// <c>State.SetState({...})</c> / <c>State.IncrementState({...})</c>
        /// to record current gameplay state; the SDK fires that state
        /// on every heartbeat AND once at session end (auto-flush on
        /// <c>Application.quitting</c> when
        /// <c>AutoShutdownOnQuit</c> is enabled, or manual via
        /// <see cref="EndSession"/>). See <see cref="StateApi"/> for
        /// the full surface.
        /// </summary>
        public StateApi State { get; }

        /// <summary>
        /// Sampled session state for Playback. Push the player's position,
        /// room, action bits and axes through <see cref="TraceApi"/>; the SDK
        /// samples them 5 to 20 times a second and ships one chunk every five
        /// seconds as a <c>trace_chunk</c> event. On by default outside
        /// <c>"production"</c>; see <see cref="TraceOptions"/>.
        /// </summary>
        public TraceApi Trace { get; }

        /// <summary>
        /// SDK-owned heartbeat emitter. Fires one <c>session_heartbeat</c> event
        /// every <see cref="PlayloopOptions.HeartbeatSec"/> seconds
        /// with the current state snapshot at the event's top level.
        /// See <see cref="HeartbeatEmitter"/>.
        /// </summary>
        public HeartbeatEmitter Heartbeat { get; }

        /// <summary>
        /// Per-event config consumer. Reads
        /// <c>GET /api/v1/games/&lt;slug&gt;/event-config</c> on construction
        /// (fire-and-forget) and applies <c>sdkIgnore</c> + <c>linkToSummary</c>
        /// to every subsequent <c>Telemetry.Track()</c> call. Inert when the
        /// ingest key can't be resolved to a game. Use
        /// <see cref="RefreshEventConfigAsync"/> for the inspector
        /// save-then-pull dev loop.
        /// </summary>
        public EventConfigStore EventConfig { get; }

        /// <summary>
        /// A/B experiments. <c>await client.Experiments.VariantAsync("exp_id")</c>
        /// resolves the player's variant (server-side evaluation, cached for
        /// the session); the SDK auto-tags the session's first telemetry flush
        /// with the resolved assignments. See <see cref="ExperimentsApi"/> for
        /// the lazy-fetch / refetch-once / last-good contract.
        /// </summary>
        public ExperimentsApi Experiments { get; }

        /// <summary>
        /// The device id this client will stamp on new sessions. Resolved
        /// once at construction time: explicit
        /// <see cref="PlayloopOptions.DeviceId"/> wins; otherwise comes from
        /// <see cref="DeviceIdResolver.Resolve"/>.
        /// </summary>
        public string DeviceId { get; }

        public PlayloopClient(PlayloopOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            // Never-raise on construction: a game that ships with a blank ingest
            // key (or base URL) does NOT throw - it builds a DISABLED client. All
            // the nested APIs still exist (no null windows), but every one is an
            // inert no-op and no background work (heartbeat / auto-instrument /
            // crash trap / prefetch) ever starts. Read IsEnabled to detect this.
            // Matches the Godot SDK's inert-namespaces contract.
            _enabled = options.IsConfigured;
            _enableCrashReporting = options.EnableCrashReporting;

            // Wrap whatever transport the caller picked (UnityWebRequest /
            // System.Net.Http / mock) with the canonical retry/backoff policy.
            // Retries live in the wrapper so every handler, including
            // MockHttpHandler in tests, gets the same contract for free.
            var rawHandler = options.Http ?? PickDefaultHandler(options.TimeoutSeconds);
            _httpHandler = new RetryingHttpHandler(
                rawHandler,
                options.BuildRetryPolicy(),
                // Caller-owned transports stay alive past disposal so a test
                // can inspect call history. SDK-owned transports follow our
                // lifetime.
                disposeInner: options.Http == null);
            _autoShutdownEnabled = options.AutoShutdownOnQuit;
            _shutdownFlushTimeoutMs = Math.Max(0, options.ShutdownFlushTimeoutMs);

            // Auto-resolve the device id when the consumer didn't supply
            // one. Whitespace counts as unset so an accidental " " in env
            // config doesn't override the resolver. See DeviceIdResolver
            // for the resolution order + why the hardware id is preferred.
            DeviceId = string.IsNullOrWhiteSpace(options.DeviceId)
                ? DeviceIdResolver.Resolve()
                : options.DeviceId!;

            // Resolve the environment once. When the consumer left it at the
            // default "dev" (or empty), auto-derive it from the build: a
            // development build (editor / Debug.isDebugBuild / DEVELOPMENT_BUILD)
            // stays "dev", a shipping release build is promoted to "production".
            // Any explicit non-default slug wins and is used verbatim. The header
            // + the session-metadata stamp + diagnostics all read this single
            // resolved value.
            string resolvedEnvironment = ResolveEnvironment(options.Environment);

            var http = new Http.HttpClient(_httpHandler, options.BaseUrl, options.ApiKey, resolvedEnvironment, enabled: _enabled);
            // Resolves the game identity (gameId + slug + name) from the ingest
            // key, lazily and cached. Telemetry never awaits it; event-config
            // and tester-keys do. See GameResolver.
            _resolver = new GameResolver(http);
            Sessions = new SessionsApi(http);
            Telemetry = new TelemetryApi(
                http,
                options.TelemetryFlushIntervalMs,
                options.TelemetryMaxBufferSize,
                DeviceId,
                Playloop.Telemetry.AutoInstrumentSettings.FromOptions(options),
                options.SendInEditor,
                enabled: _enabled);
            Discord = new DiscordApi(http, options.RelaySecret);
            Playtest = new PlaytestApi(http, _resolver, DeviceId);
            Feedback = new FeedbackApi(http, enabled: _enabled);
            BugReports = new BugReportApi(http, environment: resolvedEnvironment, enabled: _enabled);
            EventConfig = new EventConfigStore(http, _resolver);
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            // Pass EventConfig so the forms can read the server-resolved
            // "Powered by Playloop" branding flag.
            FeedbackForm = new FeedbackForm(Feedback, EventConfig);
            BugReportForm = new BugReportForm(BugReports, EventConfig);
#endif
            State = new StateApi(Telemetry);
            Trace = new TraceApi(Telemetry, options.Trace, resolvedEnvironment, enabled: _enabled);
            Telemetry.AttachTrace(Trace);
            Heartbeat = new HeartbeatEmitter(Telemetry, State, ResolveHeartbeatSec(options.HeartbeatSec));
            Experiments = new ExperimentsApi(http, DeviceId, enabled: _enabled);
            _environment = resolvedEnvironment;

            // DISABLED mode: every nested API is built (no null windows) but
            // inert. Stop here - no prefetch, no background fetch tasks, no quit
            // hooks, no crash trap, no session_start anchor, no heartbeat, no
            // WebGL driver. Log ONE actionable warning (not per-call spam) so the
            // misconfiguration is visible, publish ourselves so editor tooling
            // can read IsEnabled, and return.
            if (!_enabled)
            {
                WarnDisabled();
                Current = this;
                return;
            }

            // Warm up the game resolve so the round-trip overlaps host-app
            // startup. Fire-and-forget; never throws. event-config + tester-keys
            // await the cached result on first need.
            _resolver.Prefetch();

            // Wire the event-config consumer into Telemetry.Track().
            // Until the prefetch settles (or always, when the ingest key can't
            // be resolved), Lookup returns null and every event flows through
            // unchanged.
            Telemetry.SetEventConfigHooks(new ClientEventConfigHooks(EventConfig, State));

            // Tag the first telemetry flush with the SDK's cached A/B
            // experiment assignments. Provider reads a
            // snapshot at flush time; games that never use experiments send no
            // experimentTags. The server re-validates and persists the matches.
            Telemetry.SetExperimentTagsProvider(() => Experiments.Snapshot());

            // Fire-and-forget background fetch of the per-event config.
            // Errors are swallowed because default-do-nothing is a safe
            // fallback. RefreshAsync resolves the slug from the ingest key and
            // no-ops if that fails.
            _ = Task.Run(async () =>
            {
                try { await EventConfig.RefreshAsync().ConfigureAwait(false); }
                catch { /* best-effort prefetch */ }
            });

            // Eager experiment prefetch: opt-in via PrefetchExperiments.
            // Fire-and-forget; ExperimentsApi swallows errors and falls back to
            // a lazy fetch on the first VariantAsync() call.
            if (options.PrefetchExperiments)
            {
                _ = Task.Run(async () =>
                {
                    try { await Experiments.RefreshAsync().ConfigureAwait(false); }
                    catch { /* best-effort prefetch */ }
                });
            }

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            if (_autoShutdownEnabled)
            {
                UnityEngine.Application.quitting += OnApplicationQuitting;
#if UNITY_EDITOR
                // `Application.quitting` does NOT reliably fire when the developer
                // STOPS play mode in the Editor. It's raised on a real player quit,
                // not on play-mode exit. Without this, in-Editor playtests never ship
                // their session-end, so the server never receives `sessionEnded` and
                // never analyzes the session. Mirror the quit flush on the
                // ExitingPlayMode transition. Editor-only: stripped from builds.
                UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
            }
#endif

            // Install the crash trap + drain any persisted
            // crashes from a prior launch. The trap is
            // Application.logMessageReceived (Unity) or
            // AppDomain.UnhandledException (.NET standalone). The drain
            // replays each persisted crash as a `crash_reported` event
            // so the server-side ingest picks it up via the normal
            // telemetry route.
            InstallCrashHandlerInternal();

            // Fire the canonical session_start anchor (carries identity
            // + build metadata) and start the heartbeat timer. Both fire
            // synchronously here so the row order is always start →
            // beats → end regardless of whether the dev calls
            // EndSession() manually or relies on the quit-hook.
            EmitSessionStart();
            Heartbeat.Start();
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL is single-threaded: the heartbeat Timer + AutoBatch's flush
            // loop never run, so spawn a main-loop driver that pumps both. No
            // game-side WebGL code required.
            SpawnWebGLDriver(options);
#endif

            // Publish ourselves as the active client AFTER the constructor
            // has done all of its can-throw work. If we crashed earlier,
            // `Current` would point at a partially-constructed client and
            // editor tooling would see broken state.
            Current = this;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>
        /// Spawn the WebGL main-loop driver (see <see cref="Telemetry.PlayloopWebGLDriver"/>).
        /// Single-threaded WebGL has no ThreadPool, so AutoBatch's flush loop and
        /// the heartbeat Timer never run; this hidden MonoBehaviour pumps flush +
        /// heartbeat from Update() so WebGL telemetry works with zero game code.
        /// Mirrors the focus-tracker spawn; destroyed in <see cref="Dispose"/>.
        /// </summary>
        private void SpawnWebGLDriver(PlayloopOptions options)
        {
            try
            {
                var go = new UnityEngine.GameObject("[Playloop] WebGLDriver")
                {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                var driver = go.AddComponent<Telemetry.PlayloopWebGLDriver>();
                float flushSec = options.TelemetryFlushIntervalMs / 1000f;
                float heartbeatSec = (float)ResolveHeartbeatSec(options.HeartbeatSec);
                driver.Attach(Telemetry, Heartbeat, Trace, flushSec, heartbeatSec);
                _webGLDriverGo = go;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Playloop] Failed to spawn WebGL driver: {e.Message}. " +
                    "WebGL telemetry flush/heartbeat will not run.");
                _webGLDriverGo = null;
            }
        }
#endif

        /// <summary>
        /// Manual end-of-session fire. Flushes the state accumulator as
        /// a <c>session_summary</c> event with the current snapshot at top
        /// level. Idempotent: repeat calls (manual OR auto-flush from
        /// the quit hook) are no-ops until <see cref="StateApi.ClearState"/>
        /// resets the accumulator.
        ///
        /// <para>
        /// Most games don't need to call this explicitly. The
        /// <c>Application.quitting</c> hook auto-flushes on shutdown
        /// when <see cref="PlayloopOptions.AutoShutdownOnQuit"/> is
        /// true. Call manually for game-defined moments like a "Quit
        /// to menu" button or the end of a level that semantically
        /// ends the playtest session.
        /// </para>
        /// </summary>
        public void EndSession()
        {
            Heartbeat.Stop();
            State.FlushSessionEnd();
        }

        /// <summary>
        /// Install the crash trap + drain pending crashes
        /// from a prior launch. Called once from the constructor;
        /// idempotent on the underlying storage (re-drain is harmless
        /// because the queue is cleared after the first read).
        ///
        /// Per-game <c>crashesEnabled</c> is honored: the trap is only
        /// installed when the (cached / default) value is true, and
        /// the drain skips persisted crashes when the flag is
        /// currently false. Both checks read the live config so a
        /// setting flipped between launches takes effect on the next
        /// session start.
        /// </summary>
        private void InstallCrashHandlerInternal()
        {
            if (!_enableCrashReporting) return;
            // Drain first: even if the current settings disable the
            // handler, any crashes already on disk should be flushed
            // (the dev probably wants to see them before going dark).
            try
            {
                var pending = CrashHandler.Drain();
                foreach (var crash in pending)
                {
                    try
                    {
                        var data = new Dictionary<string, object>
                        {
                            { "crashedSessionId", crash.CrashedSessionId! },
                            { "stackTrace", crash.StackTrace },
                            { "message", crash.Message! },
                            { "buildVersion", crash.BuildVersion! },
                            { "sdkVersion", crash.SdkVersion },
                            { "platform", crash.Platform },
                            { "recentEvents", crash.RecentEvents },
                            { "crashedAtMs", crash.CrashedAtMs },
                        };
                        Telemetry.Track("crash_reported", data);
                    }
                    catch
                    {
                        // Drop on the floor: losing one historical
                        // crash is better than blocking the host on a
                        // misconfigured telemetry layer.
                    }
                }
            }
            catch
            {
                // Drain failure (corrupt JSON, permission error, …):
                // acceptable, the next launch will try again on a
                // fresh file.
            }

            var ctx = new CrashHandler.InstallContext
            {
                SdkVersion = SdkVersion,
                GetCurrentSessionId = () => Telemetry.CurrentSessionId,
                GetRecentEvents = () =>
                {
                    try
                    {
                        var snap = Telemetry.SnapshotPending();
                        var list = new List<Dictionary<string, object?>>(snap.Count);
                        foreach (var ev in snap)
                        {
                            var item = new Dictionary<string, object?> { { "name", ev.Name }, { "ts", ev.Timestamp } };
                            if (ev.Data != null) item["data"] = ev.Data;
                            list.Add(item);
                        }
                        return list;
                    }
                    catch
                    {
                        return Array.Empty<Dictionary<string, object?>>();
                    }
                },
                GetBuildVersion = () =>
                {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                    return UnityEngine.Application.version;
#else
                    return null;
#endif
                },
                GetSettings = () =>
                {
                    var g = EventConfig.GameSettings();
                    return new CrashHandler.Settings
                    {
                        Enabled = g.CrashesEnabled,
                        RecentEventsCount = g.CrashRecentEventsCount,
                        SampleRate = g.CrashSampleRate,
                    };
                },
            };
            _crashHandlerHandle = CrashHandler.Install(ctx);
        }

        /// <summary>
        /// Public re-install entry point. Idempotent: uninstalls the
        /// prior handle first. Useful in tests or after a clear-then-
        /// configure flow in a long-running app.
        /// </summary>
        public void InstallCrashHandler()
        {
            if (_crashHandlerHandle != null)
            {
                try { _crashHandlerHandle.Uninstall(); } catch { /* best-effort */ }
                _crashHandlerHandle = null;
            }
            InstallCrashHandlerInternal();
        }

        private void EmitSessionStart()
        {
            var meta = new Dictionary<string, object>
            {
                { "sdkVersion", SdkVersion },
                { "environment", _environment },
                { "deviceId", DeviceId },
                // Cross-engine fingerprint (stamped on every session, all
                // platforms incl. the headless dotnet-test build). `engine` is
                // the constant "unity"; `engineVersion` is Application.unityVersion
                // (empty outside Unity); `isEditor` flags editor-originated
                // sessions so they're trivial to filter on the dashboard.
                { "engine", PlayloopRuntimeEnv.EngineName },
                { "engineVersion", PlayloopRuntimeEnv.EngineVersion },
                { "isEditor", PlayloopRuntimeEnv.IsEditor },
            };
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            try { meta["platform"] = UnityEngine.Application.platform.ToString(); }
            catch { /* SystemInfo unavailable in some test contexts */ }
            try { meta["unityVersion"] = UnityEngine.Application.unityVersion; }
            catch { /* same, best-effort */ }
            try { meta["systemLanguage"] = UnityEngine.Application.systemLanguage.ToString(); }
            catch { /* same, best-effort */ }
#endif
            try { Telemetry.Track("session_start", meta); }
            catch
            {
                // Best-effort: never throw from the constructor's
                // anchor emit. A misconfigured telemetry layer should
                // still let the client construct so the caller can
                // surface the error explicitly via their own Track()
                // calls.
            }
        }

        /// <summary>
        /// Re-fetch the per-event config from server and apply it to
        /// every subsequent <see cref="TelemetryApi.Track"/> call.
        /// Designed for the inspector save-then-pull dev loop: edit
        /// flags in the Editor window, click Save, then call this in
        /// the host app to see the change live without restarting.
        /// Production picks up changes on next session start
        /// automatically (constructor prefetch).
        ///
        /// No-op when the ingest key can't be resolved to a game. Errors
        /// from the underlying fetch propagate so the caller can surface
        /// them in dev tooling.
        /// </summary>
        public Task RefreshEventConfigAsync(CancellationToken ct = default)
            => EventConfig.RefreshAsync(ct);

        /// <summary>
        /// Wiring adapter passed into <see cref="TelemetryApi"/>. Reads
        /// from the store, writes through to the summary. Tiny class
        /// instead of inline lambdas so tests can substitute a fake.
        /// </summary>
        private sealed class ClientEventConfigHooks : IEventConfigHooks
        {
            private readonly EventConfigStore _store;
            private readonly StateApi _state;
            public ClientEventConfigHooks(EventConfigStore store, StateApi state)
            {
                _store = store;
                _state = state;
            }
            public EventConfigEntry? Lookup(string eventName) => _store.Lookup(eventName);
            public void MergeIntoSummary(IReadOnlyDictionary<string, object> data)
            {
                if (data == null) return;
                // StateApi.SetState takes IReadOnlyDictionary<string, object?>.
                // Wrap once so callers can pass non-nullable dicts. The
                // hook is named "MergeIntoSummary" for the server-side
                // contract; under the new SDK shape it actually merges
                // into the state accumulator.
                var nullable = new Dictionary<string, object?>(data.Count);
                foreach (var kvp in data) nullable[kvp.Key] = kvp.Value;
                _state.SetState(nullable);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Clear the static handle if we're the active instance. Tests
            // and Play↔Edit transitions can build/tear down clients in
            // quick succession; last-write-wins on construction means we
            // only clear when *we* are the one currently published.
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            if (_autoShutdownEnabled)
            {
                try { UnityEngine.Application.quitting -= OnApplicationQuitting; }
                catch { /* late shutdown, best effort */ }
#if UNITY_EDITOR
                try { UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged; }
                catch { /* late shutdown, best effort */ }
#endif
            }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
            // Destroy the WebGL main-loop driver.
            var webGLDriverGo = _webGLDriverGo;
            _webGLDriverGo = null;
            if (webGLDriverGo != null)
            {
                try { UnityEngine.Object.Destroy(webGLDriverGo); } catch { /* best-effort */ }
            }
#endif
            // Stop the heartbeat timer so it doesn't keep firing
            // events after telemetry tears down.
            try { Heartbeat.Stop(); } catch { /* best-effort */ }
            if (_crashHandlerHandle != null)
            {
                try { _crashHandlerHandle.Uninstall(); } catch { /* best-effort */ }
                _crashHandlerHandle = null;
            }
            Telemetry.Dispose();
            // _httpHandler is the retrying wrapper we always own. Its
            // disposeInner flag (set in the ctor) decides whether the
            // user-supplied or SDK-picked transport gets disposed too.
            _httpHandler.Dispose();
        }

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
        /// <summary>
        /// Bounded synchronous shutdown: runs on Unity's quit path. Async-void
        /// here would be abandoned by Unity before the HTTP POST completes, so
        /// we explicitly Task.Run + Wait: the standard pattern for flushing
        /// a final analytics/telemetry POST on the quit path.
        /// </summary>
        private void OnApplicationQuitting()
        {
            if (_disposed) return;
            // Stop the heartbeat timer first so it doesn't race with
            // the final flush, then emit the canonical `session_summary`
            // event with the accumulator's snapshot at top level
            // before EndSessionAsync ships the batch. Synchronous:
            // both just append to the buffer.
            try { Heartbeat.Stop(); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Playloop] Heartbeat stop threw during shutdown: {e.Message}");
            }
            try { State.FlushSessionEnd(); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Playloop] State flush threw during shutdown: {e.Message}");
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL is single-threaded: Task.Run has no thread pool and .Wait()
            // would block the only (browser) thread, deadlocking against the
            // flush that itself needs the main loop to resume. So do NOT block on
            // WebGL. Fire the end-session flush best-effort (no .Wait()); the
            // session_summary was already buffered by FlushSessionEnd above, and
            // the heartbeat carries your latest state snapshot, so if the app is
            // force-quit the final session_summary is still recovered from the
            // last heartbeat. NOTE: Application.quitting rarely fires on a browser
            // tab close, so this hook is itself unreliable on WebGL - heartbeats
            // are the real session-liveness/recovery mechanism there.
            try { _ = Telemetry.EndSessionAsync(); }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Playloop] WebGL shutdown flush threw: {e.Message}");
            }
            finally
            {
                Dispose();
            }
#else
            try
            {
                var shutdown = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await Telemetry.EndSessionAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                    try { await Telemetry.StopAutoBatchAsync().ConfigureAwait(false); }
                    catch { /* best effort */ }
                });

                bool completed = shutdown.Wait(_shutdownFlushTimeoutMs);
                if (!completed)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Playloop] Final flush did NOT complete within {_shutdownFlushTimeoutMs}ms. Last events may be lost.");
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Playloop] Shutdown hook threw: {e.Message}");
            }
            finally
            {
                Dispose();
            }
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only counterpart to the <c>Application.quitting</c> hook.
        /// `Application.quitting` is unreliable on Editor play-mode stop, so we
        /// run the same bounded synchronous session-end flush when play mode is
        /// exiting. Otherwise an in-Editor playtest never ships its session-end
        /// and the session is never analyzed. Stripped from player builds.
        /// </summary>
        private void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                OnApplicationQuitting();
            }
        }
#endif
#endif

        // -----------------------------------------------------------------
        // Tester Keys: top-level convenience methods so the studio's main
        // menu code reads as `client.LinkTesterAsync(...)` rather than
        // `client.Playtest.LinkTesterAsync(...)`. Both forms work.
        // -----------------------------------------------------------------

        /// <summary>Bind this device to a Playloop tester handle. See <see cref="PlaytestApi.LinkTesterAsync"/>.</summary>
        public Task<LinkTesterResult> LinkTesterAsync(
            string claimToken,
            string? deviceId = null,
            CancellationToken ct = default)
            => Playtest.LinkTesterAsync(claimToken, deviceId, ct);

        /// <summary>Query the linked tester for this device, if any. See <see cref="PlaytestApi.GetCurrentTesterAsync"/>.</summary>
        public Task<CurrentTester> GetCurrentTesterAsync(
            string? deviceId = null,
            CancellationToken ct = default)
            => Playtest.GetCurrentTesterAsync(deviceId, ct);

        /// <summary>
        /// Unlink this device. See <see cref="PlaytestApi.UnlinkTesterAsync"/>.
        ///
        /// The original <paramref name="claimToken"/> is required (the SDK
        /// holds it from <see cref="LinkTesterAsync"/> time), so only the
        /// device that made the link can undo it.
        /// </summary>
        public Task<UnlinkTesterResult> UnlinkTesterAsync(
            string claimToken,
            string? deviceId = null,
            CancellationToken ct = default)
            => Playtest.UnlinkTesterAsync(claimToken, deviceId, ct);

        /// <summary>
        /// Submit a multi-field Player Feedback response. Thin shortcut
        /// for <see cref="FeedbackApi.SubmitAsync"/>.
        /// </summary>
        public Task<SubmitFeedbackResult> SubmitFeedbackAsync(
            string formId,
            string sessionId,
            IReadOnlyList<FeedbackResponseInput> responses,
            long? askedAtSec = null,
            long? answeredAtSec = null,
            CancellationToken ct = default,
            string? requestId = null)
            => Feedback.SubmitAsync(formId, sessionId, responses, askedAtSec, answeredAtSec, ct, requestId);

        /// <summary>
        /// Submit a player Bug Report. Thin shortcut for
        /// <see cref="BugReportApi.SubmitAsync"/>.
        /// </summary>
        public Task<SubmitBugReportResult> SubmitBugReportAsync(
            string title,
            string description,
            string? severity = null,
            string? sessionId = null,
            string? playerId = null,
            BugReportContext? context = null,
            string? idempotencyKey = null,
            CancellationToken ct = default)
            => BugReports.SubmitAsync(title, description, severity, sessionId, playerId, context, idempotencyKey, ct);

        /// <summary>
        /// Resolve the heartbeat cadence with the documented sentinel +
        /// floor:
        ///   * Non-finite / negative → 60 (default).
        ///   * Exactly 0 → 0 (disabled, no heartbeats).
        ///   * (0, MinHeartbeatSec) → clamped to MinHeartbeatSec +
        ///     <c>Debug.LogWarning</c>.
        ///   * &gt;= MinHeartbeatSec → passes through.
        /// </summary>
        private static double ResolveHeartbeatSec(double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0)
            {
                return 60.0;
            }
            if (raw == 0.0) return 0.0;
            if (raw < PlayloopOptions.MinHeartbeatSec)
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                UnityEngine.Debug.LogWarning(
                    $"[Playloop] HeartbeatSec={raw} is below the " +
                    $"{PlayloopOptions.MinHeartbeatSec}s floor; clamping to " +
                    $"{PlayloopOptions.MinHeartbeatSec}s. Set 0 to disable heartbeats entirely.");
#endif
                return PlayloopOptions.MinHeartbeatSec;
            }
            return raw;
        }

        /// <summary>
        /// Resolve the environment slug. An explicit non-default value (anything
        /// other than <see cref="PlayloopOptions.DefaultEnvironment"/> = <c>"dev"</c>,
        /// after a trim) is honored verbatim. The default (or an empty / whitespace
        /// value) triggers the build auto-derive: development build → <c>"dev"</c>,
        /// shipping release build → <c>"production"</c>. See
        /// <see cref="PlayloopRuntimeEnv"/>.
        /// </summary>
        private static string ResolveEnvironment(string? configured)
        {
            var trimmed = configured?.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                !string.Equals(trimmed, PlayloopOptions.DefaultEnvironment, StringComparison.Ordinal))
            {
                return trimmed!;
            }
            return PlayloopRuntimeEnv.DeriveEnvironment();
        }

        /// <summary>
        /// Log the single "SDK disabled" warning for a client built without a
        /// usable ingest key. One actionable line, not per-call spam - the
        /// never-raise contract keeps the client callable, this makes the
        /// misconfiguration visible so it isn't a silent mystery.
        /// </summary>
        private static void WarnDisabled()
        {
            const string msg =
                "[Playloop] No API key configured - SDK disabled. The client is safe to " +
                "construct and call, but telemetry is dropped, experiments resolve to control, " +
                "and the feedback form will not open. Set PlayloopOptions.ApiKey (or the ingest " +
                "key on the PlayloopSettings asset) to your game's key from Connections → Unity. " +
                "Check PlayloopClient.IsEnabled to detect this in code.";
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            UnityEngine.Debug.LogWarning(msg);
#else
            Console.Error.WriteLine(msg);
#endif
        }

        private static IHttpHandler PickDefaultHandler(int timeoutSeconds)
        {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            return new UnityWebRequestHandler(timeoutSeconds);
#else
            return new DefaultHttpHandler(timeoutSeconds);
#endif
        }
    }
}
