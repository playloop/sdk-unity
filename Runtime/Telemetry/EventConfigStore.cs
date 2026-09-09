#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playloop.Resolve;

namespace Playloop.Telemetry
{
    /// <summary>
    /// Per-event SDK-side config.
    /// Pulls per-event flags from <c>GET /api/v1/games/&lt;slug&gt;/event-config</c>
    /// and exposes a sync lookup that <see cref="TelemetryApi"/> consults
    /// on every <c>Track()</c> call.
    ///
    /// <para>
    /// The game slug is resolved from the ingest key
    /// (<see cref="GameResolver"/>). When the key can't be resolved (offline /
    /// invalid key), the store is inert and every lookup short-circuits: no
    /// filtering is applied, equivalent to a fresh config with no rows.
    /// </para>
    /// </summary>
    public sealed class EventConfigStore
    {
        private readonly Http.HttpClient _http;
        private readonly GameResolver _resolver;
        private readonly object _lock = new object();
        // Single-flight guard for RefreshAsync. Concurrent callers share
        // the in-flight task instead of issuing duplicate fetches.
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        private Dictionary<string, EventConfigEntry> _entries = new Dictionary<string, EventConfigEntry>();
        private GameConfigBlock _game = GameConfigBlock.Default;
        private bool _fetched;

        internal EventConfigStore(Http.HttpClient http, GameResolver resolver)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        /// Look up the per-event config row. Returns <c>null</c> when
        /// there's no row for this event (default-do-nothing) or when the
        /// store hasn't fetched yet. Callers treat <c>null</c> as "flow
        /// through normally."
        /// </summary>
        public EventConfigEntry? Lookup(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return null;
            lock (_lock)
            {
                return _entries.TryGetValue(eventName, out var entry) ? entry : null;
            }
        }

        /// <summary>True once the first successful fetch has populated the cache.</summary>
        public bool Loaded { get { lock (_lock) return _fetched; } }

        /// <summary>
        /// Per-game settings block. Returns the
        /// defaults when the fetch hasn't completed or when the ingest key
        /// couldn't be resolved to a game. Callers always get a
        /// usable shape.
        /// </summary>
        public GameConfigBlock GameSettings()
        {
            lock (_lock) return _game;
        }

        /// <summary>
        /// Fetch from server and replace the local cache. Idempotent:
        /// concurrent callers serialize through an internal semaphore.
        /// No-op when the ingest key can't be resolved to a game.
        /// </summary>
        public async Task RefreshAsync(CancellationToken ct = default)
        {
            // Resolve the game slug from the ingest key. Inert when the key
            // can't be resolved (offline / invalid key). No filtering applied.
            var slug = await _resolver.ResolveSlugAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(slug)) return;
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var path = $"/api/v1/games/{Uri.EscapeDataString(slug!)}/event-config";
                var response = await _http.JsonAsync<EventConfigResponse>("GET", path, body: null, ct: ct).ConfigureAwait(false);
                var next = new Dictionary<string, EventConfigEntry>();
                if (response?.Events != null)
                {
                    foreach (var row in response.Events)
                    {
                        if (row == null || string.IsNullOrEmpty(row.EventName)) continue;
                        next[row.EventName] = new EventConfigEntry(
                            eventName: row.EventName,
                            sdkIgnore: row.SdkIgnore,
                            linkToSummary: row.LinkToSummary,
                            hideFromAi: row.HideFromAi,
                            category: row.Category);
                    }
                }
                var nextGame = ParseGameBlock(response?.Game);
                lock (_lock)
                {
                    _entries = next;
                    _game = nextGame;
                    _fetched = true;
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static GameConfigBlock ParseGameBlock(GameConfigRow? raw)
        {
            if (raw == null) return GameConfigBlock.Default;
            var snapshot = string.IsNullOrEmpty(raw.SnapshotEventName) ? null : raw.SnapshotEventName;
            // Clamp defensively. A misbehaving deploy must not surprise
            // the SDK with out-of-range numerics.
            var count = raw.CrashRecentEventsCount;
            if (count < 0) count = 0;
            if (count > 50) count = 50;
            var rate = raw.CrashSampleRate;
            if (double.IsNaN(rate)) rate = 1.0;
            if (rate < 0) rate = 0;
            if (rate > 1) rate = 1;
            return new GameConfigBlock(
                snapshotEventName: snapshot,
                crashesEnabled: raw.CrashesEnabled,
                crashRecentEventsCount: count,
                crashSampleRate: rate,
                showBranding: raw.ShowBranding);
        }

        // Wire shape: only the fields the SDK acts on. The dashboard
        // carries more fields (aiVisibility, metricKind, displayName,
        // description) but they're inspector / analytics concerns and we
        // ignore them at runtime.
        internal sealed class EventConfigResponse
        {
            [JsonProperty("events")] public List<EventConfigRow>? Events { get; set; }
            [JsonProperty("game")] public GameConfigRow? Game { get; set; }
        }

        internal sealed class EventConfigRow
        {
            [JsonProperty("eventName")] public string EventName { get; set; } = "";
            [JsonProperty("sdkIgnore")] public bool SdkIgnore { get; set; }
            [JsonProperty("linkToSummary")] public bool LinkToSummary { get; set; }
            [JsonProperty("hideFromAi")] public bool HideFromAi { get; set; }
            [JsonProperty("category")] public string? Category { get; set; }
        }

        internal sealed class GameConfigRow
        {
            [JsonProperty("snapshotEventName")] public string? SnapshotEventName { get; set; }
            [JsonProperty("crashesEnabled")] public bool CrashesEnabled { get; set; } = true;
            [JsonProperty("crashRecentEventsCount")] public int CrashRecentEventsCount { get; set; } = 5;
            [JsonProperty("crashSampleRate")] public double CrashSampleRate { get; set; } = 1.0;
            [JsonProperty("showBranding")] public bool ShowBranding { get; set; } = true;
        }
    }

    /// <summary>
    /// Per-game settings block returned alongside <c>events</c> from
    /// the GET. The SDK reads this in one round trip so the crash
    /// handler + the import snapshot resolver don't need bespoke
    /// endpoints.
    /// </summary>
    public sealed class GameConfigBlock
    {
        public string? SnapshotEventName { get; }
        public bool CrashesEnabled { get; }
        public int CrashRecentEventsCount { get; }
        public double CrashSampleRate { get; }
        /// <summary>
        /// Whether the in-game feedback / prompt widgets render the
        /// "Powered by Playloop" mark. Resolved server-side; defaults true.
        /// </summary>
        public bool ShowBranding { get; }

        public GameConfigBlock(string? snapshotEventName, bool crashesEnabled, int crashRecentEventsCount, double crashSampleRate, bool showBranding = true)
        {
            SnapshotEventName = snapshotEventName;
            CrashesEnabled = crashesEnabled;
            CrashRecentEventsCount = crashRecentEventsCount;
            CrashSampleRate = crashSampleRate;
            ShowBranding = showBranding;
        }

        /// <summary>Defaults applied when the GET response omits the block (older deploy).</summary>
        public static GameConfigBlock Default { get; } = new GameConfigBlock(
            snapshotEventName: null,
            crashesEnabled: true,
            crashRecentEventsCount: 5,
            crashSampleRate: 1.0,
            showBranding: true);
    }

    /// <summary>
    /// The subset of the server's event row that the runtime SDK acts on.
    /// </summary>
    public sealed class EventConfigEntry
    {
        public string EventName { get; }
        /// <summary>Drop the event before it enters the telemetry buffer.</summary>
        public bool SdkIgnore { get; }
        /// <summary>Auto-merge the event's data into <c>client.Summary</c> on every fire.</summary>
        public bool LinkToSummary { get; }
        /// <summary>
        /// Server-side AI digest exclusion. Forwarded for completeness;
        /// the SDK does not act on it at runtime.
        /// </summary>
        public bool HideFromAi { get; }
        public string? Category { get; }

        public EventConfigEntry(string eventName, bool sdkIgnore, bool linkToSummary, bool hideFromAi, string? category)
        {
            EventName = eventName;
            SdkIgnore = sdkIgnore;
            LinkToSummary = linkToSummary;
            HideFromAi = hideFromAi;
            Category = category;
        }
    }

    /// <summary>
    /// Hook shape consumed by <see cref="TelemetryApi"/> so the telemetry
    /// layer stays decoupled from the event-config + summary surfaces.
    /// <see cref="PlayloopClient"/> wires both ends together at construction.
    /// </summary>
    public interface IEventConfigHooks
    {
        EventConfigEntry? Lookup(string eventName);
        void MergeIntoSummary(IReadOnlyDictionary<string, object> data);
    }
}
