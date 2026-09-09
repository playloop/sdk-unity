#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Playloop.Experiments
{
    /// <summary>
    /// A/B experiments: Unity SDK side.
    ///
    /// Variant evaluation is server-side: the SDK fetches the player's
    /// pre-resolved <c>{ experimentId: variantKey }</c> map from
    /// <c>GET /api/telemetry/experiments</c> and caches it for the lifetime
    /// of this client (one game session). <see cref="VariantAsync"/> reads
    /// from that cache.
    ///
    /// <para>Four behaviors:</para>
    /// <list type="bullet">
    ///   <item><description><b>Lazy fetch.</b> The map is fetched on the
    ///   FIRST <see cref="VariantAsync"/> call, not at construction. Games
    ///   that never run experiments never pay the network cost. Opt into
    ///   eager startup fetch with
    ///   <see cref="PlayloopOptions.PrefetchExperiments"/>.</description></item>
    ///   <item><description><b>Session-scoped cache.</b> The cache holds for
    ///   the client's lifetime: variant stability across a playthrough beats
    ///   freshness. Call <see cref="RefreshAsync"/> for manual control in
    ///   long-running sessions.</description></item>
    ///   <item><description><b>Refetch-once on unknown id.</b>
    ///   <c>VariantAsync("exp_new")</c> for an id not in the cache triggers
    ///   exactly ONE refetch per unknown id per session. If it's still
    ///   unknown after the refetch, the answer is <c>null</c> and that
    ///   negative is cached: no retry loops.</description></item>
    ///   <item><description><b>Last-good on network error.</b> A failed fetch
    ///   never throws and never blocks: if a cached map exists, its answers
    ///   stand; if the very first fetch failed, <see cref="VariantAsync"/>
    ///   returns <c>null</c>.</description></item>
    /// </list>
    ///
    /// <para>
    /// <see cref="VariantAsync"/> is on the game's hot path. It is async,
    /// side-effect-free for the caller, and never throws. The synchronous
    /// <see cref="Variant"/> returns the cached answer only (<c>null</c> on a
    /// cache miss) for perf-critical paths where the caller knows the cache
    /// is already warm.
    /// </para>
    /// </summary>
    public sealed class ExperimentsApi
    {
        private readonly Http.HttpClient _http;
        private readonly string _deviceId;

        // When false, the SDK was constructed in DISABLED mode (blank ingest
        // key). Every variant resolves to null (the control default) with no
        // network attempt, so an unconfigured game never accidentally lands a
        // treatment and never blocks on a fetch that would fail anyway.
        private readonly bool _enabled;

        // Guards every mutable field below. The flush loop reads Snapshot()
        // from a background thread while game code calls VariantAsync() from
        // the main thread, so cache access must serialize.
        private readonly object _lock = new object();

        /// <summary>
        /// Server-resolved assignment map. <c>null</c> until the first
        /// SUCCESSFUL fetch; an empty dictionary after a successful fetch that
        /// returned no running experiments.
        /// </summary>
        private Dictionary<string, string>? _cache;

        /// <summary>
        /// Parallel map of each assigned variant's optional config payload,
        /// keyed by experiment id. Filled from the same fetch as
        /// <see cref="_cache"/>. An experiment whose variant has no config
        /// simply has no entry here, so <see cref="GetConfig"/> returns
        /// <c>null</c> for it. <c>null</c> until the first successful fetch.
        /// </summary>
        private Dictionary<string, VariantConfig>? _configCache;

        /// <summary>True once the lazy first fetch has been ATTEMPTED (success or fail).</summary>
        private bool _attemptedInitialFetch;

        /// <summary>Ids we've already spent the one allowed refetch on (negative cache).</summary>
        private readonly HashSet<string> _refetchedUnknown = new HashSet<string>();

        /// <summary>Dedupes concurrent fetches into a single in-flight request.</summary>
        private Task? _inFlight;

        internal ExperimentsApi(Http.HttpClient http, string deviceId, bool enabled = true)
        {
            _http = http;
            _deviceId = deviceId;
            _enabled = enabled;
        }

        /// <summary>
        /// Resolve the player's variant for <paramref name="experimentId"/>.
        /// Async, never throws, returns <c>null</c> when the experiment is
        /// unknown / not running / the first fetch failed. See the class
        /// docstring for the full contract.
        /// </summary>
        public async Task<string?> VariantAsync(string experimentId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(experimentId)) return null;
            // Disabled SDK: resolve to control (null) immediately, no fetch.
            if (!_enabled) return null;

            // Lazy first fetch: only the FIRST VariantAsync() call triggers it.
            bool needInitial;
            lock (_lock) { needInitial = !_attemptedInitialFetch; }
            if (needInitial) await RefreshAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_cache != null && _cache.TryGetValue(experimentId, out var cached))
                {
                    return cached;
                }
            }

            // Unknown id: refetch ONCE per id per session, then cache the
            // negative answer so we never loop.
            bool doRefetch;
            lock (_lock)
            {
                doRefetch = _refetchedUnknown.Add(experimentId);
            }
            if (doRefetch)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_cache != null && _cache.TryGetValue(experimentId, out var after))
                    {
                        return after;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Synchronous variant lookup: returns the cached answer only, or
        /// <c>null</c> on a cache miss. Never fetches. This is the "I know
        /// it's already loaded" optimization for performance-critical paths:
        /// call <see cref="VariantAsync"/> (or enable
        /// <see cref="PlayloopOptions.PrefetchExperiments"/>) once early, then
        /// read with <see cref="Variant"/> on the hot path.
        /// </summary>
        public string? Variant(string experimentId)
        {
            if (string.IsNullOrEmpty(experimentId)) return null;
            lock (_lock)
            {
                if (_cache != null && _cache.TryGetValue(experimentId, out var cached))
                {
                    return cached;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolve the assigned variant's config payload for
        /// <paramref name="experimentId"/>: a flat map of primitive values
        /// (string / number / bool) set from the dashboard, read through
        /// <see cref="VariantConfig"/>'s typed getters. Returns <c>null</c>
        /// when the experiment is unknown / not running, or when the assigned
        /// variant carries no config. Async, never throws, and shares the same
        /// lazy fetch + session cache as <see cref="VariantAsync"/>.
        /// </summary>
        /// <example><code>
        /// var cfg = await client.Experiments.GetConfigAsync("exp_tutorial_v2");
        /// float damage = cfg?.GetFloat("enemy_damage", 10f) ?? 10f;
        /// bool hints = cfg?.GetBool("show_hints", false) ?? false;
        /// </code></example>
        public async Task<VariantConfig?> GetConfigAsync(string experimentId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(experimentId)) return null;
            if (!_enabled) return null;

            // Lazy first fetch mirrors VariantAsync(): only the first call pays.
            bool needInitial;
            lock (_lock) { needInitial = !_attemptedInitialFetch; }
            if (needInitial) await RefreshAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_configCache != null && _configCache.TryGetValue(experimentId, out var cached))
                {
                    return cached;
                }
            }

            // Unknown id: spend the one allowed refetch (shared with the
            // VariantAsync negative cache), then read whatever config arrived.
            bool doRefetch;
            lock (_lock) { doRefetch = _refetchedUnknown.Add(experimentId); }
            if (doRefetch)
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_configCache != null && _configCache.TryGetValue(experimentId, out var after))
                    {
                        return after;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Synchronous config lookup: returns the cached config only, or
        /// <c>null</c> on a cache miss. Never fetches. The "I know it's already
        /// loaded" optimization paired with <see cref="Variant"/>: warm the
        /// cache once with <see cref="VariantAsync"/> /
        /// <see cref="GetConfigAsync"/> (or
        /// <see cref="PlayloopOptions.PrefetchExperiments"/>), then read on the
        /// hot path.
        /// </summary>
        public VariantConfig? GetConfig(string experimentId)
        {
            if (string.IsNullOrEmpty(experimentId)) return null;
            lock (_lock)
            {
                if (_configCache != null && _configCache.TryGetValue(experimentId, out var cached))
                {
                    return cached;
                }
            }
            return null;
        }

        /// <summary>
        /// Force a re-fetch of the assignment map from the server. Useful in
        /// long-running sessions when an experiment is created or its
        /// allocations change mid-play. Never throws. A failed refresh leaves
        /// the last-good cache in place. Concurrent calls share one request.
        /// </summary>
        public Task RefreshAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_inFlight != null) return _inFlight;
                var fetch = FetchMapAsync(ct);
                _inFlight = fetch;
                // Clear the handle once THIS fetch settles so the NEXT
                // RefreshAsync starts a fresh request. The identity guard makes
                // it race-free: a fetch that completes synchronously runs this
                // continuation (via lock reentrancy) AFTER the assignment above,
                // and a stale continuation from a prior fetch never nulls a
                // newer in-flight request. FetchMapAsync swallows every error,
                // so the continuation always runs.
                fetch.ContinueWith(
                    t => { lock (_lock) { if (ReferenceEquals(_inFlight, t)) _inFlight = null; } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return fetch;
            }
        }

        /// <summary>
        /// Snapshot of the current assignment map (a copy). Empty dictionary
        /// when nothing has been fetched yet. The telemetry layer reads this
        /// to tag the first flush's <c>experimentTags</c>: the server then
        /// re-validates and persists only the matches.
        /// </summary>
        public IReadOnlyDictionary<string, string> Snapshot()
        {
            lock (_lock)
            {
                return _cache != null
                    ? new Dictionary<string, string>(_cache)
                    : new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Perform one fetch of the assignment map. Marks the initial-fetch
        /// attempt as done regardless of outcome. Swallows every error so the
        /// last-good cache stands and <see cref="VariantAsync"/> never throws.
        /// </summary>
        private async Task FetchMapAsync(CancellationToken ct)
        {
            lock (_lock) { _attemptedInitialFetch = true; }

            // The ingest key identifies the game server-side, so experiments no
            // longer need a gameId: the server scopes the lookup to the key's
            // game. We send only the device id.
            var path =
                $"/api/telemetry/experiments?deviceId={Uri.EscapeDataString(_deviceId)}";
            try
            {
                var res = await _http.JsonAsync<ExperimentMapResponse>("GET", path, null, ct)
                    .ConfigureAwait(false);
                if (res?.Experiments != null)
                {
                    // Replace (not merge): the server map is authoritative for
                    // the session; a stopped experiment should drop out of the
                    // cache.
                    var next = new Dictionary<string, string>(res.Experiments.Count);
                    foreach (var kvp in res.Experiments)
                    {
                        if (kvp.Value != null) next[kvp.Key] = kvp.Value;
                    }
                    // The parallel config map is optional and only present for
                    // experiments whose assigned variant carries a non-empty
                    // payload. An older server that omits it leaves an empty
                    // config cache, so GetConfig() returns null
                    // (backward-compatible).
                    var nextConfig = SanitizeConfigMap(res.Config);
                    lock (_lock) { _cache = next; _configCache = nextConfig; }
                }
                else
                {
                    // Well-formed empty response: record an empty successful fetch.
                    lock (_lock)
                    {
                        _cache ??= new Dictionary<string, string>();
                        _configCache ??= new Dictionary<string, VariantConfig>();
                    }
                }
            }
            catch
            {
                // Network / HTTP error. Last-good wins: keep whatever cache we
                // have (possibly null on a first-fetch failure). Never throw.
            }
        }

        /// <summary>
        /// Coerce the server's raw <c>config</c> map into
        /// <c>{ experimentId: VariantConfig }</c>, keeping only primitive
        /// values (string / number / bool). Anything else (an array, a nested
        /// object, null) is dropped defensively: the contract is primitives
        /// only, so a malformed payload can never reach game code.
        /// </summary>
        private static Dictionary<string, VariantConfig> SanitizeConfigMap(
            Dictionary<string, Dictionary<string, object?>>? raw)
        {
            var outMap = new Dictionary<string, VariantConfig>();
            if (raw == null) return outMap;
            foreach (var expEntry in raw)
            {
                if (expEntry.Value == null) continue;
                var values = new Dictionary<string, object>();
                foreach (var kvp in expEntry.Value)
                {
                    if (kvp.Value == null) continue;
                    // Newtonsoft deserializes JSON primitives to long / double /
                    // bool / string when the target is object. Keep only those.
                    if (kvp.Value is string || kvp.Value is bool
                        || kvp.Value is long || kvp.Value is int
                        || kvp.Value is double || kvp.Value is float)
                    {
                        values[kvp.Key] = kvp.Value;
                    }
                }
                if (values.Count > 0) outMap[expEntry.Key] = new VariantConfig(values);
            }
            return outMap;
        }

        /// <summary>Wire shape of <c>GET /api/telemetry/experiments</c>.</summary>
        private sealed class ExperimentMapResponse
        {
            [JsonProperty("experiments")]
            public Dictionary<string, string>? Experiments { get; set; }

            /// <summary>
            /// Optional parallel map of the assigned variant's config payload
            /// (#1048). Absent for older experiments or an older server, in
            /// which case no config is exposed.
            /// </summary>
            [JsonProperty("config")]
            public Dictionary<string, Dictionary<string, object?>>? Config { get; set; }
        }
    }

    /// <summary>
    /// A variant's optional runtime config payload: a flat, read-only map of
    /// primitive values (string / number / bool) the dashboard attaches to the
    /// assigned variant. Lets you tweak a tuning number, a feature flag, or a
    /// label from the dashboard without shipping a new build. Read values
    /// through the typed getters, each of which returns a fallback when the key
    /// is missing or the stored value is not the requested type. Primitives
    /// only, by design: no arrays and no nested objects.
    /// </summary>
    public sealed class VariantConfig
    {
        private readonly IReadOnlyDictionary<string, object> _values;

        internal VariantConfig(IReadOnlyDictionary<string, object> values)
        {
            _values = values;
        }

        /// <summary>The config keys present on this variant.</summary>
        public IEnumerable<string> Keys => _values.Keys;

        /// <summary>Number of config values present.</summary>
        public int Count => _values.Count;

        /// <summary>True when <paramref name="key"/> is present.</summary>
        public bool ContainsKey(string key) => key != null && _values.ContainsKey(key);

        /// <summary>
        /// Read a string value. Returns <paramref name="fallback"/> when the
        /// key is missing or the stored value is not a string.
        /// </summary>
        public string? GetString(string key, string? fallback = null)
        {
            if (key != null && _values.TryGetValue(key, out var v) && v is string s) return s;
            return fallback;
        }

        /// <summary>
        /// Read a boolean value. Returns <paramref name="fallback"/> when the
        /// key is missing or the stored value is not a bool.
        /// </summary>
        public bool GetBool(string key, bool fallback = false)
        {
            if (key != null && _values.TryGetValue(key, out var v) && v is bool b) return b;
            return fallback;
        }

        /// <summary>
        /// Read a numeric value as a <see cref="double"/>. Accepts any stored
        /// number (integer or floating point). Returns
        /// <paramref name="fallback"/> when the key is missing or the value is
        /// not a number.
        /// </summary>
        public double GetNumber(string key, double fallback = 0d)
        {
            if (key != null && _values.TryGetValue(key, out var v))
            {
                switch (v)
                {
                    case long l: return l;
                    case int i: return i;
                    case double d: return d;
                    case float f: return f;
                }
            }
            return fallback;
        }

        /// <summary>Read a numeric value as an <see cref="int"/> (truncating).</summary>
        public int GetInt(string key, int fallback = 0)
        {
            if (key != null && _values.TryGetValue(key, out var v))
            {
                switch (v)
                {
                    case long l: return (int)l;
                    case int i: return i;
                    case double d: return (int)d;
                    case float f: return (int)f;
                }
            }
            return fallback;
        }

        /// <summary>Read a numeric value as a <see cref="float"/>.</summary>
        public float GetFloat(string key, float fallback = 0f)
        {
            if (key != null && _values.TryGetValue(key, out var v))
            {
                switch (v)
                {
                    case long l: return l;
                    case int i: return i;
                    case double d: return (float)d;
                    case float f: return f;
                }
            }
            return fallback;
        }
    }
}
