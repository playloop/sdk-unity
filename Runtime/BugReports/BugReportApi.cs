#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playloop.Playtest;

namespace Playloop.BugReports
{
    /// <summary>
    /// Severity levels a player can attach to a bug report. Ordered
    /// low → critical. Mirrors the TypeScript / Python SDK union so a
    /// report filed from any engine reads identically on the dashboard.
    /// </summary>
    public static class BugReportSeverities
    {
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";
        public const string Critical = "critical";

        /// <summary>The default when the player (or caller) picks nothing.</summary>
        public const string Default = Medium;

        public static readonly IReadOnlyList<string> All = new[]
        {
            Low,
            Medium,
            High,
            Critical,
        };

        internal static bool IsValid(string value)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (string.Equals(All[i], value, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Environment / build context attached to a bug report. Every field
    /// is optional: the SDK auto-fills <see cref="BuildVersion"/>,
    /// <see cref="Environment"/>, and <see cref="Platform"/> from the same
    /// runtime values telemetry already stamps, and anything you set here
    /// overrides the auto-filled value. <see cref="Device"/> is caller-only
    /// (the SDK never auto-collects it) and carries free-form key/value
    /// pairs a triager might want (graphics device, RAM, locale, ...).
    /// </summary>
    public sealed class BugReportContext
    {
        /// <summary>Build/version string. Auto-filled from the game's version when null.</summary>
        public string? BuildVersion { get; set; }
        /// <summary>Environment slug (dev / demo / production / ...). Auto-filled from the SDK's resolved environment when null.</summary>
        public string? Environment { get; set; }
        /// <summary>Platform string. Auto-filled from the runtime platform when null.</summary>
        public string? Platform { get; set; }
        /// <summary>Free-form device fields (string / number / bool values). Caller-supplied only.</summary>
        public Dictionary<string, object>? Device { get; set; }
    }

    /// <summary>
    /// Outcome of <see cref="BugReportApi.SubmitAsync"/>.
    /// <c>BugReportId</c> is the persisted report id. <c>RoutingStatus</c>
    /// reports how far the report got through the studio's connected
    /// issue tracker (<c>pending</c>, <c>stored_only</c>, <c>routed</c>,
    /// <c>partial</c>, <c>failed</c>). <c>Idempotent</c> is true when this
    /// call replayed an earlier submission with the same idempotency key
    /// (the server returned the original report instead of filing a
    /// duplicate).
    /// </summary>
    public sealed class SubmitBugReportResult
    {
        [Newtonsoft.Json.JsonProperty("ok")] public bool Ok { get; set; }
        [Newtonsoft.Json.JsonProperty("bugReportId")] public string BugReportId { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("routingStatus")] public string RoutingStatus { get; set; } = "";
        [Newtonsoft.Json.JsonProperty("idempotent")] public bool Idempotent { get; set; }
    }

    /// <summary>
    /// Player-initiated Bug Reports (Unity SDK side).
    ///
    /// Submit a title + description (plus an optional severity and context)
    /// to <c>POST /api/telemetry/bug-report</c>. The report is stored in
    /// Playloop, shows up on the game's dashboard, and routes to the
    /// studio's connected issue tracker automatically. <see cref="BugReportForm"/>
    /// ships a programmatic UGUI default UI you can drop into any scene
    /// without authoring a prefab.
    ///
    /// <para>
    /// Bug Reports is free for every studio. No plan-based throttling at
    /// this layer.
    /// </para>
    /// </summary>
    public sealed class BugReportApi
    {
        private readonly Http.HttpClient _http;

        // The SDK's resolved environment slug (dev / demo / production).
        // Used to auto-fill context.environment when the caller doesn't
        // set one, matching the value telemetry stamps on the session.
        private readonly string? _environment;

        // When false, the SDK was constructed in DISABLED mode (blank ingest
        // key). SubmitAsync resolves to a soft failure (Ok == false) instead
        // of throwing, and the default UGUI form reads this to resolve
        // OpenAsync as Failed without ever showing UI. Default true.
        internal bool Enabled { get; }

        internal BugReportApi(Http.HttpClient http, string? environment = null, bool enabled = true)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _environment = environment;
            Enabled = enabled;
        }

        /// <summary>
        /// Submit a player bug report. Wraps
        /// <c>POST /api/telemetry/bug-report</c>.
        ///
        /// <paramref name="title"/> (1..200) and <paramref name="description"/>
        /// (1..8000) are required. <paramref name="severity"/> defaults to
        /// <see cref="BugReportSeverities.Medium"/>. When
        /// <paramref name="sessionId"/> is null the caller is expected to
        /// resolve it at submit time (the form does this via the same
        /// <c>CurrentSessionId</c> plumbing feedback uses); pass one to
        /// override. <paramref name="idempotencyKey"/> auto-generates a GUID
        /// per call so the retry layer can dedupe a redelivered submit; pass
        /// one to override.
        ///
        /// Throws <see cref="PlayloopPlaytestException"/> on validation
        /// failure (400), rate limit (429), or auth issues (401). Use the
        /// thrown exception's <c>Reason</c> field to branch on specific
        /// failure modes.
        /// </summary>
        public async Task<SubmitBugReportResult> SubmitAsync(
            string title,
            string description,
            string? severity = null,
            string? sessionId = null,
            string? playerId = null,
            BugReportContext? context = null,
            string? idempotencyKey = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(title))
                throw new ArgumentException("title is required.", nameof(title));
            if (title.Length > 200)
                throw new ArgumentException("title must be 200 characters or fewer.", nameof(title));
            if (string.IsNullOrEmpty(description))
                throw new ArgumentException("description is required.", nameof(description));
            if (description.Length > 8000)
                throw new ArgumentException("description must be 8000 characters or fewer.", nameof(description));

            var resolvedSeverity = string.IsNullOrEmpty(severity)
                ? BugReportSeverities.Default
                : severity!;
            if (!BugReportSeverities.IsValid(resolvedSeverity))
                throw new ArgumentException(
                    $"severity must be one of: {string.Join(", ", BugReportSeverities.All)}.",
                    nameof(severity));

            // Build the idempotency key ONCE, up front, so a retry of THIS
            // call (the transport layer redelivering the same request) reuses
            // it and the server dedupes instead of filing a second report.
            var resolvedIdempotencyKey = string.IsNullOrEmpty(idempotencyKey)
                ? Guid.NewGuid().ToString()
                : idempotencyKey!;

            // Disabled SDK (blank ingest key): resolve to a soft failure
            // rather than throwing. The caller branches on result.Ok exactly
            // as it would for a server-side reject. Argument validation above
            // still fires - a caller bug (empty title / bad severity) is a
            // real bug regardless of whether the SDK is configured.
            if (!Enabled)
            {
                return new SubmitBugReportResult { Ok = false };
            }

            // Hand-build the payload so Newtonsoft never serializes empty
            // optional fields onto the wire. Matches the byte-shape the
            // TypeScript and Python SDKs send.
            var body = new Dictionary<string, object>
            {
                ["title"] = title,
                ["description"] = description,
                ["severity"] = resolvedSeverity,
                ["idempotencyKey"] = resolvedIdempotencyKey,
            };
            if (!string.IsNullOrEmpty(sessionId)) body["sessionId"] = sessionId!;
            if (!string.IsNullOrEmpty(playerId)) body["playerId"] = playerId!;

            var contextPayload = BuildContextPayload(context);
            if (contextPayload != null) body["context"] = contextPayload;

            try
            {
                var result = await _http.JsonAsync<SubmitBugReportResult>(
                    "POST",
                    "/api/telemetry/bug-report",
                    body,
                    ct).ConfigureAwait(Playloop.PlAwait.Continue);

                if (result == null || !result.Ok || string.IsNullOrEmpty(result.BugReportId))
                {
                    throw new PlayloopPlaytestException(
                        0, "unknown", "bugReports.SubmitAsync returned an unexpected response shape.");
                }
                return result;
            }
            catch (PlayloopException ex)
            {
                var (reason, message) = ExtractReasonAndMessage(ex);
                throw new PlayloopPlaytestException(ex.Status, reason, message, ex);
            }
        }

        /// <summary>
        /// Merge the caller's context (if any) with the SDK's auto-filled
        /// build/environment/platform values. Caller-supplied values win.
        /// Returns null when there's nothing to send.
        /// </summary>
        private Dictionary<string, object>? BuildContextPayload(BugReportContext? context)
        {
            var buildVersion = context?.BuildVersion ?? AutoBuildVersion();
            var environment = context?.Environment ?? _environment;
            var platform = context?.Platform ?? AutoPlatform();
            var device = context?.Device;

            var payload = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(buildVersion)) payload["buildVersion"] = buildVersion!;
            if (!string.IsNullOrEmpty(environment)) payload["environment"] = environment!;
            if (!string.IsNullOrEmpty(platform)) payload["platform"] = platform!;
            if (device != null && device.Count > 0) payload["device"] = device;

            return payload.Count > 0 ? payload : null;
        }

        private static string? AutoBuildVersion()
        {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            try { return UnityEngine.Application.version; }
            catch { return null; }
#else
            return null;
#endif
        }

        private static string? AutoPlatform()
        {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            try { return UnityEngine.Application.platform.ToString(); }
            catch { return null; }
#else
            return null;
#endif
        }

        private static (string Reason, string Message) ExtractReasonAndMessage(PlayloopException ex)
        {
            // Mirror the helper in FeedbackApi / PlaytestApi: pull `reason` +
            // `message` out of the parsed body when present; fall back to the
            // bare exception message otherwise.
            try
            {
                if (ex.Body is Newtonsoft.Json.Linq.JObject jobj)
                {
                    var reason = jobj["reason"]?.ToString() ?? "";
                    var msg = jobj["message"]?.ToString();
                    return (reason, msg ?? ex.Message);
                }
                if (ex.Body is IDictionary<string, object> dict)
                {
                    var reason = dict.TryGetValue("reason", out var r) ? r?.ToString() ?? "" : "";
                    var msg = dict.TryGetValue("message", out var m) ? m?.ToString() : null;
                    return (reason, msg ?? ex.Message);
                }
            }
            catch
            {
                /* fall through */
            }
            return ("unknown", ex.Message);
        }
    }
}
