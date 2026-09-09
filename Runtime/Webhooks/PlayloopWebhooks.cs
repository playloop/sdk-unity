#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playloop.Webhooks
{
    /// <summary>Decoded webhook payload. Discriminate on <see cref="Event"/>.</summary>
    public sealed class WebhookEvent
    {
        [JsonProperty("event")] public string Event { get; set; } = "";
        [JsonProperty("session_id")] public string SessionId { get; set; } = "";

        // Only populated when Event == "session.analyzed"
        [JsonProperty("duration", NullValueHandling = NullValueHandling.Ignore)] public string? Duration { get; set; }
        [JsonProperty("insights", NullValueHandling = NullValueHandling.Ignore)] public List<WebhookInsight>? Insights { get; set; }

        // Only populated when Event == "session.failed"
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string? Error { get; set; }
    }

    public sealed class WebhookInsight
    {
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("severity", NullValueHandling = NullValueHandling.Ignore)] public string? Severity { get; set; }
        [JsonProperty("confidence")] public double Confidence { get; set; }
    }

    /// <summary>
    /// Verify the HMAC signature on an incoming Playloop webhook and return the
    /// parsed event. Synchronous (no I/O).
    ///
    /// <para>
    /// Throws <see cref="PlayloopException"/> (status 401) on missing or invalid
    /// signatures, and (status 400) when the verified body isn't valid JSON.
    /// </para>
    ///
    /// <para>
    /// Plan note: webhook delivery (configuring URL + secret on the dashboard
    /// and receiving outbound events) requires a Pro Playloop account. This
    /// verifier ships in every install and is plan-agnostic, but it has
    /// nothing to validate until webhooks are enabled on a Pro account.
    /// </para>
    /// </summary>
    public static class PlayloopWebhooks
    {
        public static WebhookEvent Verify(string body, string? signature, string secret)
        {
            if (string.IsNullOrEmpty(signature))
            {
                throw new PlayloopException("Missing X-Playloop-Signature header", status: 401);
            }
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("secret is required.", nameof(secret));
            }
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            var expected = ComputeHmacSha256Hex(secret, body);
            if (!ConstantTimeEqual(expected, signature))
            {
                throw new PlayloopException("Invalid webhook signature", status: 401);
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<WebhookEvent>(body);
                if (parsed == null)
                {
                    throw new PlayloopException("Webhook body is not valid JSON", status: 400);
                }
                return parsed;
            }
            catch (JsonException ex)
            {
                throw new PlayloopException("Webhook body is not valid JSON: " + ex.Message, status: 400);
            }
        }

        private static string ComputeHmacSha256Hex(string secret, string message)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return BytesToHex(hash);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Constant-time string compare. Returns false fast on length mismatch
        /// (the length itself is not secret, only the bytes are).
        /// </summary>
        private static bool ConstantTimeEqual(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int mismatch = 0;
            for (int i = 0; i < a.Length; i++)
            {
                mismatch |= a[i] ^ b[i];
            }
            return mismatch == 0;
        }
    }
}
