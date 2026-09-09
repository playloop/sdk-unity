#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Playloop.Discord
{
    public sealed class DiscordIngestResult
    {
        [JsonProperty("sessionsCreated")] public int SessionsCreated { get; set; }
        [JsonProperty("messagesIngested")] public int MessagesIngested { get; set; }
    }

    public sealed class DiscordApi
    {
        private readonly Http.HttpClient _http;
        private readonly string? _relaySecret;

        internal DiscordApi(Http.HttpClient http, string? relaySecret = null)
        {
            _http = http;
            _relaySecret = string.IsNullOrEmpty(relaySecret) ? null : relaySecret;
        }

        /// <summary>
        /// Ingest a Discord channel's messages as a Playloop session.
        ///
        /// <para>
        /// Requires the Discord relay secret alongside the API key. set it on
        /// the client (<see cref="PlayloopOptions.RelaySecret"/>) or per call
        /// via <paramref name="relaySecret"/>. Copy it from
        /// Connections &gt; Discord on playloop.gg. It is sent as the
        /// <c>x-playloop-secret</c> header.
        /// </para>
        /// </summary>
        /// <param name="relaySecret">
        /// Discord relay secret for this call. Overrides the client-level
        /// <see cref="PlayloopOptions.RelaySecret"/>. Copy it from
        /// Connections &gt; Discord on playloop.gg (it's shown once; rotate it
        /// there any time).
        /// </param>
        public Task<DiscordIngestResult> IngestAsync(
            string channelId,
            string game,
            string? threadId = null,
            string? relaySecret = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(channelId))
                throw new ArgumentException("channelId is required.", nameof(channelId));
            if (string.IsNullOrEmpty(game))
                throw new ArgumentException("game is required.", nameof(game));

            var body = new Dictionary<string, string>
            {
                ["channel_id"] = channelId,
                ["game"] = game,
            };
            if (!string.IsNullOrEmpty(threadId))
            {
                body["thread_id"] = threadId!;
            }

            var secret = string.IsNullOrEmpty(relaySecret) ? _relaySecret : relaySecret;
            Dictionary<string, string>? extraHeaders = secret == null
                ? null
                : new Dictionary<string, string> { ["x-playloop-secret"] = secret };

            return _http.JsonAsync<DiscordIngestResult>(
                "POST", "/api/webhooks/discord", body, cancellationToken, extraHeaders);
        }
    }
}
