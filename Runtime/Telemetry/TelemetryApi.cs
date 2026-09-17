#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playloop.Telemetry
{
    /// <summary>One in-game event. <c>Id</c> is a short random hex string generated per <see cref="TelemetryApi.Track(string, IReadOnlyDictionary{string, object})"/> call and is used by the server to dedup events per session. <c>Timestamp</c> is filled by the SDK when you call <see cref="TelemetryApi.Track(string, IReadOnlyDictionary{string, object})"/>.</summary>
    public sealed class TelemetryEvent
    {
        [JsonProperty("id", Order = -2)] public string Id { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public IReadOnlyDictionary<string, object>? Data { get; set; }
        [JsonProperty("timestamp")] public long Timestamp { get; set; }
    }

    /// <summary>
    /// Telemetry batcher. <c>Track</c> is synchronous. It just appends to an
    /// in-memory buffer. <c>FlushAsync</c> POSTs the queued events. <c>AutoBatch</c>
    /// starts a background <see cref="Task"/> that flushes on an interval.
    ///
    /// <para>
    /// The server creates a session on the first flush and returns its id; the SDK
    /// caches that id and replays it on every subsequent flush so events append to
    /// the same session. Call <see cref="StartSession"/> before the first flush to
    /// stage session metadata + an optional deviceId; call
    /// <see cref="EndSessionAsync"/> to flush with <c>sessionEnded: true</c>, which
    /// tells the server to kick AI analysis once and lets the next Track/Flush
    /// start a fresh session.
    /// </para>
    /// </summary>
    public sealed class TelemetryApi : IDisposable
    {
        private readonly Http.HttpClient _http;
        private readonly int _flushIntervalMs;
        private readonly int _maxBufferSize;

        // When false (default), telemetry sends are suppressed in the Unity
        // editor + development builds so local playtesting doesn't pollute real
        // session data. Set true via PlayloopOptions.SendInEditor to opt in.
        private readonly bool _sendInEditor;

        // When false, the whole SDK was constructed in DISABLED mode (blank
        // ingest key). Track() drops every event on the floor and AutoBatch()
        // never spawns its flush loop / instrument GameObjects, so an
        // unconfigured game pays zero cost and raises nothing. Default true.
        private readonly bool _enabled;
        // Guards the one-time suppression warning (logged on the first flush
        // that's actually suppressed). 0 = not yet warned, 1 = warned.
        private int _editorSuppressionWarned;

        private readonly object _bufferLock = new object();
        private readonly List<TelemetryEvent> _buffer = new List<TelemetryEvent>();

        // SemaphoreSlim instead of lock: lets FlushAsync await it without blocking.
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        // Set in Dispose() before _flushLock is disposed. Guards a late
        // continuation from touching the disposed semaphore (see FlushAsync).
        private volatile bool _disposed;

        // Session state, guarded by _bufferLock (same access pattern as the buffer).
        private string? _currentSessionId;
        private string? _deviceId;
        private IReadOnlyDictionary<string, object>? _sessionMetadata;
        private bool _endRequested;
        // True once StartSession has been called and before EndSessionAsync clears state.
        // Distinct from _currentSessionId, which is null until the FIRST flush returns
        // the server-assigned id. We need this flag so StartSession can be idempotent
        // during the pre-first-flush window (parity with the TS SDK).
        private bool _sessionActive;

        private CancellationTokenSource? _batchCts;
        private Task? _batchTask;

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
        // Hidden GameObject hosting the focus tracker. Owned by AutoBatch's
        // lifecycle: spawned in AutoBatch(), torn down in StopAutoBatchAsync()
        // and Dispose(). Null outside Unity (the focus tracker file is fully
        // #if-guarded so the type doesn't even exist there).
        private UnityEngine.GameObject? _focusTrackerGo;

        // Hidden GameObject hosting the auto-instrumentation MonoBehaviour
        // (scene/error/idle/fps/memory). Same lifecycle as the focus tracker.
        private UnityEngine.GameObject? _autoInstrumentGo;

        // Hidden GameObject hosting the Trace driver, which pumps
        // Trace.Tick from Update. Same lifecycle as the focus tracker.
        private UnityEngine.GameObject? _traceDriverGo;
#endif

        // The client's Trace, attached once at construction so the session
        // end can close it and the final flush can count its chunks.
        private Playloop.Trace.TraceApi? _trace;

        // Captured at construction so AutoBatch() can spawn the
        // AutoInstrument MonoBehaviour with the same flag set the consumer
        // configured on PlayloopOptions. Defaults to Disabled when no
        // settings are supplied (the legacy two-arg constructor path keeps
        // existing tests working without opting into auto-events).
        private readonly AutoInstrumentSettings _autoInstrumentSettings;

        // Event-config hooks. Wired by PlayloopClient at construction
        // so Track() can consult sdkIgnore (drop on the wire) and
        // linkToSummary (auto-merge into State). Stays null when GameSlug
        // isn't configured. Every Track() then flows through unchanged.
        private IEventConfigHooks? _eventConfigHooks;

        // Experiment-tag provider. Wired by PlayloopClient at construction so
        // the session-create flush can tag itself with the SDK's cached A/B
        // assignments. Stays null until wired; games
        // that never use experiments send no `experimentTags` field, so their
        // payloads are byte-for-byte unchanged.
        private Func<IReadOnlyDictionary<string, string>>? _experimentTagsProvider;

        /// <summary>
        /// Install the event-config hooks. Wired once by
        /// <see cref="PlayloopClient"/> so <see cref="Track"/> can consult
        /// per-event flags without importing the state surface here.
        /// Replacing the hooks (e.g. in tests) is supported. Last writer
        /// wins.
        /// </summary>
        public void SetEventConfigHooks(IEventConfigHooks? hooks)
        {
            _eventConfigHooks = hooks;
        }

        /// <summary>
        /// Install the experiment-tag provider. Wired once by
        /// <see cref="PlayloopClient"/> so the session-create flush can attach
        /// the player's cached A/B variant assignments as <c>experimentTags</c>.
        /// The server re-validates and persists only the matches. Pass
        /// <c>null</c> to clear (tests). Last writer wins.
        /// </summary>
        public void SetExperimentTagsProvider(Func<IReadOnlyDictionary<string, string>>? provider)
        {
            _experimentTagsProvider = provider;
        }

        /// <summary>
        /// Attach the client's Trace. Wired once by <see cref="PlayloopClient"/>
        /// so <see cref="EndSessionAsync"/> closes it before the final flush and
        /// <see cref="AutoBatch"/> spawns its driver.
        /// </summary>
        internal void AttachTrace(Playloop.Trace.TraceApi trace)
        {
            _trace = trace;
        }

        /// <summary>
        /// True when the per-event config says to drop this event at the SDK.
        /// False until the config settles.
        /// </summary>
        internal bool IsEventIgnored(string name)
        {
            var flags = _eventConfigHooks?.Lookup(name);
            return flags != null && flags.SdkIgnore;
        }

        internal TelemetryApi(Http.HttpClient http, int flushIntervalMs, int maxBufferSize, string? deviceId = null)
            : this(http, flushIntervalMs, maxBufferSize, deviceId, AutoInstrumentSettings.Disabled, sendInEditor: true, enabled: true)
        {
            // The legacy/test constructor path keeps sends ON (sendInEditor:true)
            // so existing fixtures that flush through MockHttpHandler under the
            // editor/dotnet build keep observing the POST. Production always goes
            // through PlayloopClient, which threads the real PlayloopOptions value.
        }

        internal TelemetryApi(
            Http.HttpClient http,
            int flushIntervalMs,
            int maxBufferSize,
            string? deviceId,
            AutoInstrumentSettings autoInstrumentSettings,
            bool sendInEditor = false,
            bool enabled = true)
        {
            _http = http;
            _flushIntervalMs = flushIntervalMs;
            _maxBufferSize = maxBufferSize;
            _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            _autoInstrumentSettings = autoInstrumentSettings;
            _sendInEditor = sendInEditor;
            _enabled = enabled;
        }

        /// <summary>Number of events currently buffered. Exposed for tests + diagnostics.</summary>
        public int PendingCount
        {
            get { lock (_bufferLock) return _buffer.Count; }
        }

        /// <summary>
        /// Test-only: drop every event currently in the buffer without
        /// sending it. Production code should call <see cref="FlushAsync"/>
        /// to deliver events; this exists so tests can discard the
        /// constructor-fired <c>session_start</c> anchor row before
        /// asserting on subsequent <c>Track()</c> calls.
        /// </summary>
        public void ClearBuffer()
        {
            lock (_bufferLock) { _buffer.Clear(); }
        }

        /// <summary>
        /// Defensive copy of the current pending-event buffer. Exposed
        /// for tests + diagnostics. Production callers should use
        /// <see cref="FlushAsync"/> to actually deliver events. Returned
        /// list is a new list on every call so callers can mutate it
        /// without poisoning the buffer.
        /// </summary>
        public IReadOnlyList<TelemetryEvent> SnapshotPending()
        {
            lock (_bufferLock)
            {
                return new List<TelemetryEvent>(_buffer);
            }
        }

        /// <summary>
        /// Server-assigned session id from the most recent successful flush, or
        /// <c>null</c> before the first flush / after <see cref="EndSessionAsync"/>.
        /// Read-only. Exposed for diagnostics.
        /// </summary>
        public string? CurrentSessionId
        {
            get { lock (_bufferLock) return _currentSessionId; }
        }

        /// <summary>
        /// Stage session metadata and an optional deviceId for the NEXT flush. The
        /// server stamps these onto the session row when it creates the session;
        /// they are ignored on subsequent appends. Does not make a network call.
        ///
        /// <para>
        /// <c>deviceId</c> is optional. When null or empty, the api falls back
        /// to whatever the owning <see cref="PlayloopClient"/> resolved at
        /// construction time (explicit <see cref="PlayloopOptions.DeviceId"/>,
        /// or <see cref="DeviceIdResolver.Resolve"/> otherwise). Pass an
        /// explicit value only when you need to override the default for this
        /// session: testing, account linkage, etc.
        /// </para>
        ///
        /// <para>
        /// Idempotent: if a session is already active (either staged via a prior
        /// StartSession call or cached after the first flush), this is a no-op:
        /// staged metadata and deviceId are NOT replaced. To start a fresh
        /// session with new metadata, call <see cref="EndSessionAsync"/> first
        /// (which clears state) and then call <c>StartSession</c> again. This
        /// matches the canonical TS SDK behavior: most forgiving in a long-
        /// running game loop, doesn't lose in-flight data, doesn't crash on
        /// accidental double-call from gameplay code.
        /// </para>
        /// </summary>
        /// <param name="metadata">Arbitrary key/value bag merged into <c>session.metadata</c>.</param>
        /// <param name="deviceId">Optional persistent device id override (≤128 chars). Null/empty falls back to the client-resolved value.</param>
        public void StartSession(IReadOnlyDictionary<string, object>? metadata = null, string? deviceId = null)
        {
            lock (_bufferLock)
            {
                if (_sessionActive || _currentSessionId != null)
                {
                    // Already inside an active session: ignore. Idempotent no-op,
                    // matches TS SDK semantics. Call EndSessionAsync() first if you
                    // want a new session with different metadata.
                    return;
                }
                _sessionActive = true;
                _sessionMetadata = metadata;
                if (!string.IsNullOrEmpty(deviceId))
                {
                    _deviceId = deviceId;
                }
                // else: keep _deviceId as set by the constructor (the
                // PlayloopClient-resolved value), so consumers can simply call
                // StartSession(metadata) without re-plumbing the device id.
            }
        }

        /// <summary>
        /// Flush with <c>sessionEnded: true</c> so the server kicks AI analysis
        /// once, then clear the cached session id + deviceId + metadata so the
        /// next Track/Flush starts a fresh session. Idempotent. Calling when no
        /// session is active is a no-op.
        /// </summary>
        public async Task EndSessionAsync(CancellationToken ct = default)
        {
            bool hasActiveSession;
            lock (_bufferLock)
            {
                hasActiveSession = _sessionActive || _currentSessionId != null;
                if (!hasActiveSession) return;
                _endRequested = true;
            }

            // Close the Trace first so its final chunk rides the same flush
            // that carries sessionEnded. Idempotent: a game that already
            // called End(Death) or End(LevelComplete) keeps that reason.
            try { _trace?.End(Playloop.Trace.TraceEndReason.Quit); }
            catch { /* the Trace must never block the session end */ }

            try
            {
                await FlushAsync(ct).ConfigureAwait(Playloop.PlAwait.Continue);
            }
            finally
            {
                lock (_bufferLock)
                {
                    _currentSessionId = null;
                    // Intentionally keep _deviceId. It's the stable
                    // per-install identifier from PlayloopClient (or an
                    // explicit StartSession override). The next session
                    // should stamp the same id; clearing it here would
                    // either drop the id entirely or force the consumer
                    // to re-pass it on every StartSession call.
                    _sessionMetadata = null;
                    _endRequested = false;
                    _sessionActive = false;
                }
                try { _trace?.ResetForNewSession(); }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Queue an event for the next flush. Synchronous. Does not hit the network.
        ///
        /// <para>
        /// Event-config flags applied here:
        /// </para>
        /// <para>
        /// <c>sdkIgnore</c>: drop the event entirely. It is not buffered
        /// and never reaches the server, saving both wire and ingest cost.
        /// </para>
        /// <para>
        /// <c>linkToSummary</c>: merge the event's <c>data</c> into the
        /// session-summary accumulator. The event STILL flows to the
        /// server as a normal event; the summary just gets a snapshot
        /// of its fields on top.
        /// </para>
        /// <para>
        /// When the SDK has no <c>GameSlug</c> configured OR the config
        /// fetch hasn't settled yet, both branches no-op and the event
        /// flows through unchanged.
        /// </para>
        /// </summary>
        public void Track(string name, IReadOnlyDictionary<string, object>? data = null)
            => Track(name, data, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        /// <summary>
        /// <see cref="Track(string, IReadOnlyDictionary{string, object})"/> with
        /// an explicit event timestamp. The Trace uses it so a chunk's event
        /// time is the chunk's own <c>t0</c>, the wall clock of its first sample.
        /// </summary>
        internal void Track(string name, IReadOnlyDictionary<string, object>? data, long timestampMs)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("telemetry.Track() requires an event name.", nameof(name));
            }

            // Disabled SDK (blank ingest key): drop the event on the floor.
            // Never buffers, never sends, never raises. Matches the
            // never-raise contract for an unconfigured client.
            if (!_enabled) return;

            var hooks = _eventConfigHooks;
            if (hooks != null)
            {
                var flags = hooks.Lookup(name);
                if (flags != null && flags.SdkIgnore) return;
                if (flags != null && flags.LinkToSummary && data != null && data.Count > 0)
                {
                    hooks.MergeIntoSummary(data);
                }
            }

            var ev = new TelemetryEvent
            {
                Id = GenerateEventId(),
                Name = name,
                Data = data,
                Timestamp = timestampMs,
            };

            lock (_bufferLock)
            {
                _buffer.Add(ev);
                if (_buffer.Count > _maxBufferSize)
                {
                    _buffer.RemoveAt(0); // evict the oldest
                }
            }
        }

        /// <summary>
        /// Flush whatever is currently buffered. No-op if the buffer is empty AND
        /// there's no pending end-session signal to deliver. Concurrent callers
        /// serialize through an internal semaphore.
        /// </summary>
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            // After Dispose() the _flushLock is gone. On WebGL the quit hook
            // fires EndSessionAsync() fire-and-forget and then Dispose()s
            // synchronously; the awaited continuation would otherwise resume a
            // frame later and hit ObjectDisposedException on the semaphore.
            // Bail cleanly instead.
            if (_disposed) return;

            // Suppress-in-editor gate. When SendInEditor is off (default) and we're
            // in the Unity editor or a development build, no telemetry leaves the
            // process. Local playtesting must not pollute real session data. We
            // still drain the buffer so it can't grow unbounded across a long
            // editor session, and log a one-time warning so the suppression is
            // never a silent mystery. The gate is a no-op in a release player build
            // (IsDevelopmentBuild is false) and outside Unity / under dotnet test.
            if (!_sendInEditor && PlayloopRuntimeEnv.IsDevelopmentBuild)
            {
                lock (_bufferLock)
                {
                    _buffer.Clear();
                    _endRequested = false;
                }
                WarnEditorSuppressionOnce();
                return;
            }

            await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(Playloop.PlAwait.Continue);
            try
            {
                List<TelemetryEvent> events;
                string? sessionId;
                string? deviceId;
                IReadOnlyDictionary<string, object>? metadata;
                bool sessionEnded;

                lock (_bufferLock)
                {
                    // Skip the network roundtrip when there is nothing to send AND
                    // no end-session signal pending.
                    if (_buffer.Count == 0 && !_endRequested) return;

                    events = new List<TelemetryEvent>(_buffer);
                    _buffer.Clear();

                    sessionId = _currentSessionId;
                    sessionEnded = _endRequested;
                    // deviceId + sessionMetadata only flow on the create call (no cached sessionId yet).
                    deviceId = sessionId == null ? _deviceId : null;
                    metadata = sessionId == null ? _sessionMetadata : null;
                }

                var body = new Dictionary<string, object> { ["events"] = events };
                if (!string.IsNullOrEmpty(sessionId)) body["sessionId"] = sessionId!;
                if (!string.IsNullOrEmpty(deviceId)) body["deviceId"] = deviceId!;
                if (metadata != null) body["sessionMetadata"] = metadata;
                if (sessionEnded)
                {
                    body["sessionEnded"] = true;
                    // The final flush says how many Trace chunks the session
                    // wrote, so Playback can tell a hard stop from a lost chunk.
                    int traceChunks = _trace?.ChunkCount ?? 0;
                    if (traceChunks > 0)
                    {
                        var stamped = metadata == null
                            ? new Dictionary<string, object>()
                            : new Dictionary<string, object>(metadata);
                        stamped["traceChunks"] = traceChunks;
                        body["sessionMetadata"] = stamped;
                    }
                }
                // Cross-game identity. Only attached on the
                // session-create flush (sessionId == null at the build
                // time of this body). Vendor id is auto-detected
                // (Steamworks / IDFV via reflection); linked id +
                // consent come from the persistent stores set by the
                // game via the static `Playloop` facade.
                if (sessionId == null)
                {
                    var vendorId = Playloop.Identity.VendorIdResolver.Resolve();
                    var linkedId = Playloop.Identity.LinkedIdStore.Get();
                    var consentLevel = Playloop.Identity.ConsentStore.Get();
                    if (!string.IsNullOrEmpty(vendorId)) body["vendorId"] = vendorId!;
                    if (!string.IsNullOrEmpty(linkedId)) body["linkedId"] = linkedId!;
                    if (!string.IsNullOrEmpty(consentLevel)) body["consentLevel"] = consentLevel;

                    // Experiment assignments. Sent only
                    // on session-create (this create flush); the server
                    // re-validates and persists the matches. Omitted entirely
                    // when no experiments were fetched, so payloads for games
                    // that don't use experiments are byte-for-byte unchanged.
                    var experimentTags = _experimentTagsProvider?.Invoke();
                    if (experimentTags != null && experimentTags.Count > 0)
                    {
                        body["experimentTags"] = experimentTags;
                    }
                }

                try
                {
                    var response = await _http.JsonAsync<JObject>(
                        "POST",
                        "/api/telemetry",
                        body: body,
                        ct: cancellationToken).ConfigureAwait(Playloop.PlAwait.Continue);

                    // Cache the server-assigned session id on first flush.
                    if (response != null)
                    {
                        var returnedId = response["sessionId"]?.Value<string>();
                        if (!string.IsNullOrEmpty(returnedId))
                        {
                            lock (_bufferLock)
                            {
                                if (_currentSessionId == null)
                                {
                                    _currentSessionId = returnedId;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Re-queue events at the front so order is preserved.
                    lock (_bufferLock)
                    {
                        _buffer.InsertRange(0, events);
                    }
                    throw;
                }
            }
            finally
            {
                _flushLock.Release();
            }
        }

        /// <summary>
        /// Start a background task that flushes the buffer every interval.
        /// Safe to call multiple times. Second call is a no-op. Call
        /// <see cref="StopAutoBatchAsync"/> (or dispose the client) to shut down.
        /// </summary>
        public void AutoBatch()
        {
            // Disabled SDK: never spawn the flush loop, the focus tracker, or
            // the auto-instrument GameObject. An unconfigured game that still
            // calls AutoBatch() pays no cost and emits nothing.
            if (!_enabled) return;

            if (_batchTask != null && !_batchTask.IsCompleted) return;

            _batchCts = new CancellationTokenSource();
            var token = _batchCts.Token;

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            // Spawn the focus tracker alongside AutoBatch so consumers get
            // exact-moment `focus_lost`/`focus_gained` events without wiring
            // anything themselves. The heartbeat still carries `isFocused` at
            // 60s resolution as a diagnostic backup. GameObject construction
            // must happen on the Unity main thread. AutoBatch() is called by
            // consumers during their bootstrap (main thread) so we're safe
            // here.
            EnsureFocusTrackerSpawned();

            // Spawn the auto-instrumentation MonoBehaviour on the same path
            // when at least one of its flags is on. Same lifecycle as the
            // focus tracker, torn down by StopAutoBatchAsync()/Dispose().
            // This is the entry-point for the 5 default auto-events
            // (scene_changed, application_error, idle_start/end, fps_drop)
            // plus the opt-in memory_pressure event.
            if (_autoInstrumentSettings.AnyEnabled)
            {
                EnsureAutoInstrumentSpawned();
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            // Spawn the Trace driver on the same path. On a WebGL player the
            // main-loop driver the client spawned already pumps the tick.
            EnsureTraceDriverSpawned();
#endif
#endif

            // Capture Unity's main-thread SynchronizationContext at AutoBatch()
            // call time. Inside Unity, this is the UnitySynchronizationContext
            // and posts back to the main thread, required because
            // UnityWebRequest construction + SendWebRequest() must be initiated
            // on the main thread. Without this hop, FlushAsync silently throws
            // every interval and AutoBatch's swallow-catch hides the failure.
            // Outside Unity (dotnet test, headless scenarios) this will be null;
            // we await on the current thread instead, which is fine because
            // DefaultHttpHandler (the non-Unity path) is thread-agnostic.
            var mainThreadContext = SynchronizationContext.Current;

            _batchTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(_flushIntervalMs, token).ConfigureAwait(Playloop.PlAwait.Continue);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }

                        try
                        {
                            if (mainThreadContext != null)
                            {
                                // Marshal the flush onto Unity's main thread so
                                // UnityWebRequest works. Post is fire-and-forget;
                                // we wrap with a TaskCompletionSource so we can
                                // observe completion + exceptions properly.
                                var tcs = new TaskCompletionSource<bool>();
                                mainThreadContext.Post(async _ =>
                                {
                                    try
                                    {
                                        await FlushAsync(token).ConfigureAwait(Playloop.PlAwait.Continue);
                                        tcs.TrySetResult(true);
                                    }
                                    catch (Exception ex)
                                    {
                                        tcs.TrySetException(ex);
                                    }
                                }, null);
                                await tcs.Task.ConfigureAwait(Playloop.PlAwait.Continue);
                            }
                            else
                            {
                                await FlushAsync(token).ConfigureAwait(Playloop.PlAwait.Continue);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            // Surface the failure to the Unity console (or stderr
                            // outside Unity) so silent breakage stops being a
                            // mystery. The loop keeps firing. One bad flush
                            // doesn't kill the whole pipeline.
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                            UnityEngine.Debug.LogWarning(
                                $"[Playloop] AutoBatch flush failed: {ex.GetType().Name}: {ex.Message}");
#else
                            Console.Error.WriteLine(
                                $"[Playloop] AutoBatch flush failed: {ex.GetType().Name}: {ex.Message}");
#endif
                        }
                    }
                }
                catch (OperationCanceledException) { /* clean exit */ }
            }, token);
        }

        /// <summary>Cancel the auto-batch task and wait for it to wind down.</summary>
        public async Task StopAutoBatchAsync()
        {
            var cts = _batchCts;
            var task = _batchTask;
            _batchCts = null;
            _batchTask = null;

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            // Tear down the focus tracker on the same code path that owns
            // AutoBatch. Safe to call from any thread. The underlying
            // UnityEngine.Object.Destroy queues on Unity's main thread.
            TeardownFocusTracker();
            TeardownAutoInstrument();
            TeardownTraceDriver();
#endif

            if (cts == null || task == null) return;

            cts.Cancel();
            try { await task.ConfigureAwait(Playloop.PlAwait.Continue); }
            catch (OperationCanceledException) { /* expected */ }
            finally { cts.Dispose(); }
        }

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
        private void EnsureFocusTrackerSpawned()
        {
            if (_focusTrackerGo != null) return;

            try
            {
                var go = new UnityEngine.GameObject("[Playloop] FocusTracker")
                {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                var tracker = go.AddComponent<PlayloopFocusTracker>();
                tracker.Attach(this);
                _focusTrackerGo = go;
            }
            catch (Exception e)
            {
                // Spawning the GameObject can fail if AutoBatch is called from
                // a non-main-thread context (e.g. a worker). We log + carry on.
                // AutoBatch itself is the primary signal; focus tracking is
                // a precision-add on top, and the 60s heartbeat still carries
                // isFocused as a backup.
                UnityEngine.Debug.LogWarning(
                    $"[Playloop] Failed to spawn focus tracker: {e.Message}. " +
                    "Focus events will not be emitted; heartbeats still carry isFocused.");
                _focusTrackerGo = null;
            }
        }

        private void TeardownFocusTracker()
        {
            var go = _focusTrackerGo;
            _focusTrackerGo = null;
            if (go == null) return;
            try
            {
                UnityEngine.Object.Destroy(go);
            }
            catch
            {
                // Best effort. Destroy can throw if Unity is mid-teardown.
            }
        }

        private void EnsureAutoInstrumentSpawned()
        {
            if (_autoInstrumentGo != null) return;

            try
            {
                var go = new UnityEngine.GameObject("[Playloop] AutoInstrument")
                {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                var instrument = go.AddComponent<AutoInstrument>();
                instrument.Attach(this, _autoInstrumentSettings);
                _autoInstrumentGo = go;
            }
            catch (Exception e)
            {
                // Same fallback as the focus tracker. Auto-instrumentation
                // is a free-perk feature, not a load-bearing one. If the
                // spawn fails (worker-thread AutoBatch, ultra-restricted
                // runtime), log and carry on; user-side Track() calls still
                // flow through AutoBatch unchanged.
                UnityEngine.Debug.LogWarning(
                    $"[Playloop] Failed to spawn auto-instrument: {e.Message}. " +
                    "scene_changed / application_error / idle / fps_drop events will not fire this session.");
                _autoInstrumentGo = null;
            }
        }

        private void TeardownAutoInstrument()
        {
            var go = _autoInstrumentGo;
            _autoInstrumentGo = null;
            if (go == null) return;
            try
            {
                UnityEngine.Object.Destroy(go);
            }
            catch
            {
                // Best effort. Destroy can throw mid-teardown.
            }
        }

        private void EnsureTraceDriverSpawned()
        {
            if (_traceDriverGo != null) return;
            var trace = _trace;
            if (trace == null || trace.Status == Playloop.Trace.TraceStatus.Disabled) return;

            try
            {
                var go = new UnityEngine.GameObject("[Playloop] Trace")
                {
                    hideFlags = UnityEngine.HideFlags.HideAndDontSave,
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                var driver = go.AddComponent<Playloop.Trace.PlayloopTraceDriver>();
                driver.Attach(trace);
                _traceDriverGo = go;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Playloop] Failed to spawn the Trace driver: {e.Message}. " +
                    "No Trace samples will be taken this session.");
                _traceDriverGo = null;
            }
        }

        private void TeardownTraceDriver()
        {
            var go = _traceDriverGo;
            _traceDriverGo = null;
            if (go == null) return;
            try
            {
                UnityEngine.Object.Destroy(go);
            }
            catch
            {
                // Best effort. Destroy can throw mid-teardown.
            }
        }
#endif

        /// <summary>
        /// Generate a short random hex id (12 chars / 6 bytes) for each event so
        /// the server can dedup per session. Uses <see cref="System.Security.Cryptography.RandomNumberGenerator"/>
        /// for a cryptographically-strong source. <c>Convert.ToHexString</c> is not
        /// available on netstandard2.1, so we fall back to
        /// <c>BitConverter.ToString(...).Replace("-", "")</c>.
        /// </summary>
        /// <summary>
        /// Log the suppress-in-editor explanation exactly once per
        /// <see cref="TelemetryApi"/> instance, regardless of how many flush
        /// cycles get suppressed. Interlocked guard so a background flush loop
        /// racing a manual flush can't double-log.
        /// </summary>
        private void WarnEditorSuppressionOnce()
        {
            if (Interlocked.Exchange(ref _editorSuppressionWarned, 1) != 0) return;
            const string msg =
                "[Playloop] Telemetry is SUPPRESSED in the editor / development builds " +
                "so local playtesting doesn't pollute your real session data. To send " +
                "from the editor anyway, set SendInEditor = true on PlayloopOptions (or " +
                "tick \"Send In Editor\" on the PlayloopSettings asset). Pair it with " +
                "Environment = \"dev\" so the traffic is easy to filter out. This is a " +
                "no-op in release player builds (nothing is suppressed there).";
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            UnityEngine.Debug.LogWarning(msg);
#else
            Console.Error.WriteLine(msg);
#endif
        }

        private static string GenerateEventId()
        {
            var bytes = new byte[6]; // 12 hex chars
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            // Fire-and-forget cancellation. Dispose() is sync. Callers who want
            // a clean shutdown should await StopAutoBatchAsync explicitly.
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = null;
            _batchTask = null;
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            TeardownFocusTracker();
            TeardownAutoInstrument();
            TeardownTraceDriver();
#endif
            _disposed = true;   // before disposing the lock, so a late FlushAsync bails
            _flushLock.Dispose();
        }
    }
}
