#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Playloop.Resolve
{
    /// <summary>
    /// The game identity the ingest key resolves to. Returned by
    /// <c>GET /api/telemetry/resolve</c>. The key alone identifies the game,
    /// so no slug/id input is needed.
    /// </summary>
    public sealed class ResolvedGame
    {
        public string GameId { get; }
        public string Slug { get; }
        public string Name { get; }

        public ResolvedGame(string gameId, string slug, string name)
        {
            GameId = gameId;
            Slug = slug;
            Name = name;
        }
    }

    /// <summary>
    /// Resolves the game identity from the ingest key.
    ///
    /// <para>
    /// The per-game ingest key already identifies the game server-side, so
    /// the SDK no longer asks the dev for a slug or id. Instead it lazily
    /// hits <c>GET /api/telemetry/resolve</c> (auth: <c>Bearer &lt;ingestKey&gt;</c>)
    /// once and caches the result for the lifetime of the client.
    /// </para>
    ///
    /// <para>Contract:</para>
    /// <list type="bullet">
    ///   <item><description><b>Lazy + cached.</b> The fetch fires on the first
    ///   <see cref="ResolveAsync"/> call (or a constructor warm-up via
    ///   <see cref="Prefetch"/>). The in-flight task is shared so concurrent
    ///   callers issue exactly one request; the result is cached for the
    ///   client's lifetime.</description></item>
    ///   <item><description><b>Non-blocking + never throws.</b> Telemetry never
    ///   awaits this. The key attributes the session server-side already.
    ///   Consumers that DO need the game identity (event-config, tester-keys)
    ///   await <see cref="ResolveAsync"/>, which resolves to <c>null</c> on any
    ///   failure (offline, invalid key, bad shape) rather than throwing. The
    ///   failure is cached so we don't retry on the hot path.</description></item>
    /// </list>
    /// </summary>
    public sealed class GameResolver
    {
        private readonly Http.HttpClient _http;
        private readonly object _lock = new object();

        /// <summary>The resolved game, or <c>null</c> after a failed/empty resolve.</summary>
        private ResolvedGame? _result;

        /// <summary>True once the first resolve attempt has settled (success OR failure).</summary>
        private bool _resolved;

        /// <summary>Shared in-flight task so concurrent callers issue one request.</summary>
        private Task<ResolvedGame?>? _inFlight;

        internal GameResolver(Http.HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>
        /// Resolve the game identity from the ingest key. Cached after the
        /// first attempt; never throws (returns <c>null</c> on any failure).
        /// </summary>
        public Task<ResolvedGame?> ResolveAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_resolved) return Task.FromResult(_result);
                if (_inFlight != null) return _inFlight;
                var fetch = FetchResolveAsync(ct);
                _inFlight = fetch;
                // Clear the handle once THIS fetch settles so a follow-up call
                // reads the cached result rather than the stale in-flight task.
                fetch.ContinueWith(
                    t => { lock (_lock) { if (ReferenceEquals(_inFlight, t)) _inFlight = null; } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return fetch;
            }
        }

        /// <summary>Await just the resolved gameId (<c>null</c> on failure).</summary>
        public async Task<string?> ResolveGameIdAsync(CancellationToken ct = default)
        {
            var game = await ResolveAsync(ct).ConfigureAwait(false);
            return game?.GameId;
        }

        /// <summary>Await just the resolved slug (<c>null</c> on failure).</summary>
        public async Task<string?> ResolveSlugAsync(CancellationToken ct = default)
        {
            var game = await ResolveAsync(ct).ConfigureAwait(false);
            return game?.Slug;
        }

        /// <summary>
        /// The cached resolved game without triggering a fetch. <c>null</c>
        /// when the resolve hasn't settled yet or failed. Editor tooling reads
        /// this to compose dashboard URLs without awaiting.
        /// </summary>
        public ResolvedGame? Snapshot()
        {
            lock (_lock) return _result;
        }

        /// <summary>
        /// Fire the resolve as a fire-and-forget warm-up (e.g. from the client
        /// constructor) so the round-trip overlaps host-app startup. Errors
        /// are swallowed. The next <see cref="ResolveAsync"/> reads the cached
        /// (possibly null) result.
        /// </summary>
        public void Prefetch()
        {
            try
            {
                _ = ResolveAsync();
            }
            catch
            {
                // Best-effort. FetchResolveAsync already swallows; this is
                // belt-and-suspenders against a synchronous throw.
            }
        }

        private async Task<ResolvedGame?> FetchResolveAsync(CancellationToken ct)
        {
            ResolvedGame? next = null;
            try
            {
                var res = await _http
                    .JsonAsync<ResolveResponse>("GET", "/api/telemetry/resolve", null, ct)
                    .ConfigureAwait(false);
                if (res != null
                    && !string.IsNullOrEmpty(res.GameId)
                    && !string.IsNullOrEmpty(res.Slug))
                {
                    next = new ResolvedGame(res.GameId!, res.Slug!, res.Name ?? "");
                }
                // Well-formed but empty / malformed response leaves next null.
            }
            catch
            {
                // Network / HTTP error (offline, invalid key). Cache the failure
                // so consumers no-op without retrying on the hot path. Never throw.
            }

            lock (_lock)
            {
                _result = next;
                _resolved = true;
            }
            return next;
        }

        /// <summary>Wire shape of <c>GET /api/telemetry/resolve</c>.</summary>
        private sealed class ResolveResponse
        {
            [JsonProperty("gameId")] public string? GameId { get; set; }
            [JsonProperty("slug")] public string? Slug { get; set; }
            [JsonProperty("name")] public string? Name { get; set; }
        }
    }
}
