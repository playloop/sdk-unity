#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playloop.Resolve;

namespace Playloop.Playtest
{
    /// <summary>
    /// Result of <see cref="PlaytestApi.LinkTesterAsync"/>. <c>AlreadyLinked</c>
    /// distinguishes a same-device idempotent retry from a fresh bind.
    /// </summary>
    public sealed class LinkTesterResult
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("alreadyLinked")] public bool AlreadyLinked { get; set; }
        [JsonProperty("handle")] public string Handle { get; set; } = "";
        [JsonProperty("claimedAt")] public long? ClaimedAt { get; set; }
    }

    /// <summary>
    /// Result of <see cref="PlaytestApi.GetCurrentTesterAsync"/>. When
    /// <see cref="Linked"/> is false, <see cref="Handle"/> and
    /// <see cref="ClaimedAt"/> are null.
    /// </summary>
    public sealed class CurrentTester
    {
        [JsonProperty("linked")] public bool Linked { get; set; }
        [JsonProperty("handle")] public string? Handle { get; set; }
        [JsonProperty("claimedAt")] public long? ClaimedAt { get; set; }
    }

    /// <summary>
    /// Result of <see cref="PlaytestApi.UnlinkTesterAsync"/>. Returns
    /// <c>false</c> when there was nothing to unlink (already unlinked
    /// or never linked).
    /// </summary>
    public sealed class UnlinkTesterResult
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("unlinked")] public bool Unlinked { get; set; }
    }

    /// <summary>
    /// Specific Tester Keys error carrying a <c>Reason</c> discriminator so
    /// callers can branch on the failure mode without matching on the
    /// message string.
    ///
    /// Note: this is a sibling of <see cref="PlayloopException"/>. That
    /// type is sealed, so we can't subclass it. Callers that care about
    /// the discriminator catch this; callers that want generic error
    /// handling catch the underlying <see cref="PlayloopException"/>
    /// stored on <see cref="InnerException"/>.
    /// </summary>
    public class PlayloopPlaytestException : Exception
    {
        public int Status { get; }
        public string Reason { get; }

        public PlayloopPlaytestException(int status, string reason, string message, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            Reason = reason;
        }
    }

    /// <summary>
    /// Raised on 409 when the claim token is bound to a DIFFERENT device.
    /// Surface a "Switch tester?" UX in your menu. The tester would need
    /// the original device to unlink, OR a fresh invite from the studio.
    /// </summary>
    public sealed class LinkAlreadyClaimedException : PlayloopPlaytestException
    {
        public LinkAlreadyClaimedException(string message, Exception? inner = null)
            : base(409, "already_linked_to_another_device", message, inner)
        {
        }
    }

    /// <summary>
    /// Tester Keys correlation (Unity SDK side).
    ///
    /// <code>
    /// // 5-line minimum
    /// var token = GetMainMenuInput();
    /// if (!string.IsNullOrEmpty(token))
    ///     await client.LinkTesterAsync(token);
    ///
    /// // Full UX
    /// var tester = await client.GetCurrentTesterAsync();
    /// if (tester.Linked) ShowLabel($"Playing as {tester.Handle}");
    /// else if (!string.IsNullOrEmpty(token))
    ///     await client.LinkTesterAsync(token);
    /// </code>
    ///
    /// <c>gameId</c> resolution: the ingest key identifies the game, so the
    /// SDK resolves the gameId from the key (<see cref="GameResolver"/>). There
    /// is no per-call override. The key is the single source of truth.
    /// </summary>
    public sealed class PlaytestApi
    {
        private readonly Http.HttpClient _http;
        private readonly GameResolver _resolver;
        private readonly string _defaultDeviceId;

        internal PlaytestApi(Http.HttpClient http, GameResolver resolver, string defaultDeviceId)
        {
            _http = http;
            _resolver = resolver;
            _defaultDeviceId = defaultDeviceId;
        }

        /// <summary>Bind this device to a Playloop tester handle.</summary>
        public async Task<LinkTesterResult> LinkTesterAsync(
            string claimToken,
            string? deviceId = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(claimToken))
                throw new ArgumentException("claimToken is required.", nameof(claimToken));

            var resolvedGame = await ResolveGameIdAsync(ct).ConfigureAwait(false);
            var resolvedDevice = string.IsNullOrEmpty(deviceId) ? _defaultDeviceId : deviceId!;

            try
            {
                var result = await _http.JsonAsync<LinkTesterResult>(
                    "POST",
                    "/api/telemetry/claim",
                    new Dictionary<string, string>
                    {
                        ["claimToken"] = claimToken,
                        ["gameId"] = resolvedGame,
                        ["deviceId"] = resolvedDevice,
                    },
                    ct).ConfigureAwait(false);

                if (result == null || !result.Ok || string.IsNullOrEmpty(result.Handle))
                {
                    throw new PlayloopPlaytestException(
                        0, "unknown", "LinkTester returned an unexpected response shape.");
                }
                return result;
            }
            catch (PlayloopException ex)
            {
                // Translate the generic transport error to a typed Playtest error
                // so callers can branch on `Reason` without parsing strings.
                var (reason, message) = ExtractReasonAndMessage(ex);
                if (ex.Status == 409 && reason == "already_linked_to_another_device")
                {
                    throw new LinkAlreadyClaimedException(message, ex);
                }
                throw new PlayloopPlaytestException(ex.Status, reason, message, ex);
            }
        }

        /// <summary>Query whether this device is linked to a Playloop tester.</summary>
        public async Task<CurrentTester> GetCurrentTesterAsync(
            string? deviceId = null,
            CancellationToken ct = default)
        {
            var resolvedGame = await ResolveGameIdAsync(ct).ConfigureAwait(false);
            var resolvedDevice = string.IsNullOrEmpty(deviceId) ? _defaultDeviceId : deviceId!;

            var path = $"/api/telemetry/tester?gameId={Uri.EscapeDataString(resolvedGame)}&deviceId={Uri.EscapeDataString(resolvedDevice)}";
            var result = await _http.JsonAsync<CurrentTester>("GET", path, null, ct)
                .ConfigureAwait(false);
            return result ?? new CurrentTester { Linked = false };
        }

        /// <summary>
        /// Clear the (gameId, deviceId) → handle binding for this device.
        ///
        /// Unlinking requires the original <paramref name="claimToken"/>
        /// (the SDK holds it from <see cref="LinkTesterAsync"/> time), so
        /// only the device that made the link can undo it. Pass the SAME
        /// token you used for LinkTesterAsync.
        /// </summary>
        public async Task<UnlinkTesterResult> UnlinkTesterAsync(
            string claimToken,
            string? deviceId = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(claimToken))
                throw new ArgumentException("claimToken is required.", nameof(claimToken));

            var resolvedGame = await ResolveGameIdAsync(ct).ConfigureAwait(false);
            var resolvedDevice = string.IsNullOrEmpty(deviceId) ? _defaultDeviceId : deviceId!;

            return await _http.JsonAsync<UnlinkTesterResult>(
                "DELETE",
                "/api/telemetry/claim",
                new Dictionary<string, string>
                {
                    ["claimToken"] = claimToken,
                    ["gameId"] = resolvedGame,
                    ["deviceId"] = resolvedDevice,
                },
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve the gameId from the ingest key. The tester-keys endpoints
        /// still take a gameId, but the key is the source of truth. We never
        /// ask the dev for it. Throws a clear <see cref="PlayloopPlaytestException"/>
        /// when the key can't be resolved (offline / invalid key) so the
        /// caller sees why the link couldn't happen.
        /// </summary>
        private async Task<string> ResolveGameIdAsync(CancellationToken ct)
        {
            var gameId = await _resolver.ResolveGameIdAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(gameId))
            {
                throw new PlayloopPlaytestException(
                    0,
                    "resolve_failed",
                    "Could not resolve the game from your ingest key (check the key and your connection).");
            }
            return gameId!;
        }

        private static (string Reason, string Message) ExtractReasonAndMessage(PlayloopException ex)
        {
            // PlayloopException.Body is the parsed JSON body when present
            // (an `object?`, typically a JObject or Dictionary). Pull out
            // the canonical `reason` + `message` fields; best-effort, fall
            // back to ex.Message if anything's off.
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
