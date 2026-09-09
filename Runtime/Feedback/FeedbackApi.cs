#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playloop.Playtest;

namespace Playloop.Feedback
{
    /// <summary>
    /// Field kinds supported by Player Feedback.
    /// </summary>
    public static class FeedbackFieldKinds
    {
        public const string Rating1To5 = "rating-1-5";
        public const string ShortText = "short-text";
        public const string LongText = "long-text";
        public const string YesNo = "yes-no";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Rating1To5,
            ShortText,
            LongText,
            YesNo,
        };
    }

    /// <summary>
    /// Stable definition of a single field within a feedback form.
    /// Mirrors the TypeScript / Python SDK shape so a form authored in
    /// the dashboard renders identically across engines.
    /// </summary>
    [Serializable]
    public sealed class FeedbackField
    {
        /// <summary>Stable id within the form ("q1", "q2", ...). Submissions reference fields by this id.</summary>
        [JsonProperty("id")] public string Id { get; set; } = "";
        /// <summary>Player-facing label.</summary>
        [JsonProperty("label")] public string Label { get; set; } = "";
        /// <summary>One of <see cref="FeedbackFieldKinds"/>.</summary>
        [JsonProperty("kind")] public string Kind { get; set; } = FeedbackFieldKinds.ShortText;
        [JsonProperty("required", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Required { get; set; }
        [JsonProperty("placeholder", NullValueHandling = NullValueHandling.Ignore)]
        public string? Placeholder { get; set; }
        [JsonProperty("helpText", NullValueHandling = NullValueHandling.Ignore)]
        public string? HelpText { get; set; }
    }

    /// <summary>One field's answer within a submission.</summary>
    [Serializable]
    public sealed class FeedbackResponseInput
    {
        public FeedbackResponseInput() { }
        public FeedbackResponseInput(string fieldId, string value)
        {
            FieldId = fieldId;
            Value = value;
        }

        /// <summary>The field's id as defined on the form ("q1", "q2", ...).</summary>
        [JsonProperty("fieldId")] public string FieldId { get; set; } = "";
        /// <summary>Stringified value. For ratings: the numeric value as a string ("5"). For yes/no: "yes" or "no".</summary>
        [JsonProperty("value")] public string Value { get; set; } = "";
    }

    /// <summary>
    /// Outcome of <see cref="FeedbackApi.SubmitAsync"/>. <c>SubmissionId</c>
    /// groups every multi-field row produced by the same call;
    /// <c>ResponseIds</c> carries the persisted row id for each field in the
    /// order they were submitted.
    /// </summary>
    public sealed class SubmitFeedbackResult
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("submissionId")] public string SubmissionId { get; set; } = "";
        [JsonProperty("formId")] public string FormId { get; set; } = "";
        [JsonProperty("sessionId")] public string SessionId { get; set; } = "";
        [JsonProperty("responseIds")] public List<string> ResponseIds { get; set; } = new();
    }

    /// <summary>
    /// Default field set: matches the dashboard's "New form" starter.
    /// Pair with the TypeScript SDK's <c>DEFAULT_FEEDBACK_FIELDS</c> and
    /// the Python SDK's <c>DEFAULT_FEEDBACK_FIELDS</c> to keep the
    /// snapshot in sync across engines.
    /// </summary>
    public static class DefaultFeedbackFields
    {
        public static IReadOnlyList<FeedbackField> Snapshot() => new[]
        {
            new FeedbackField { Id = "q1", Label = "How was your session?", Kind = FeedbackFieldKinds.Rating1To5, Required = true },
            new FeedbackField { Id = "q2", Label = "What worked?", Kind = FeedbackFieldKinds.ShortText },
            new FeedbackField { Id = "q3", Label = "What didn't?", Kind = FeedbackFieldKinds.LongText },
        };
    }

    /// <summary>
    /// Player Feedback (Unity SDK side).
    ///
    /// Submit multi-field forms defined in the studio dashboard at
    /// <c>/games/[slug]/feedback</c>. The SDK is transport-only for the
    /// logic surface; <see cref="FeedbackForm"/> ships a programmatic
    /// UGUI default UI you can drop into any scene without authoring a
    /// prefab.
    ///
    /// <para>
    /// Player Feedback is free for every studio. No plan-based throttling
    /// at this layer.
    /// </para>
    /// </summary>
    public sealed class FeedbackApi
    {
        private readonly Http.HttpClient _http;

        // When false, the SDK was constructed in DISABLED mode (blank ingest
        // key). SubmitAsync resolves to a soft failure (Ok == false) instead
        // of throwing, and the default UGUI form reads this to resolve
        // OpenAsync as Failed without ever showing UI. Default true.
        internal bool Enabled { get; }

        internal FeedbackApi(Http.HttpClient http, bool enabled = true)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            Enabled = enabled;
        }

        /// <summary>
        /// Submit a multi-field feedback response. Wraps
        /// <c>POST /api/telemetry/feedback</c>.
        ///
        /// Throws <see cref="PlayloopPlaytestException"/> on validation
        /// failure (400), unknown form (404), rate limit (429), or auth
        /// issues (401). Use the thrown exception's <c>Reason</c> field
        /// to branch on specific failure modes (<c>unknown_field</c>,
        /// <c>no_responses</c>).
        /// </summary>
        public async Task<SubmitFeedbackResult> SubmitAsync(
            string formId,
            string sessionId,
            IReadOnlyList<FeedbackResponseInput> responses,
            long? askedAtSec = null,
            long? answeredAtSec = null,
            CancellationToken ct = default,
            string? requestId = null)
        {
            if (string.IsNullOrEmpty(formId))
                throw new ArgumentException("formId is required.", nameof(formId));
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("sessionId is required.", nameof(sessionId));
            if (responses == null || responses.Count == 0)
                throw new ArgumentException(
                    "responses must contain at least one entry.", nameof(responses));

            // Validate each entry up front so the caller gets a clean
            // ArgumentException instead of a server-side 400.
            for (int i = 0; i < responses.Count; i++)
            {
                var r = responses[i];
                if (r == null || string.IsNullOrEmpty(r.FieldId))
                    throw new ArgumentException(
                        $"responses[{i}].FieldId is required.", nameof(responses));
                if (string.IsNullOrEmpty(r.Value))
                    throw new ArgumentException(
                        $"responses[{i}].Value is required.", nameof(responses));
            }

            // Disabled SDK (blank ingest key): resolve to a soft failure
            // rather than throwing. The caller branches on result.Ok exactly
            // as it would for a server-side reject. Argument validation above
            // still fires - a caller bug (empty formId / value) is a real bug
            // regardless of whether the SDK is configured.
            if (!Enabled)
            {
                return new SubmitFeedbackResult
                {
                    Ok = false,
                    FormId = formId,
                    SessionId = sessionId,
                };
            }

            // Create once before transport retries. Persist and pass this ID with
            // the unchanged draft when retrying after cancellation or reopening.
            requestId ??= Guid.NewGuid().ToString("N");
            if (!System.Text.RegularExpressions.Regex.IsMatch(requestId, @"\A[A-Za-z0-9_-]{1,128}\z"))
                throw new ArgumentException("requestId must contain 1-128 letters, digits, underscores or hyphens.", nameof(requestId));

            // Hand-build the payload so Newtonsoft never serializes empty
            // optional fields onto the wire. Matches the byte-shape the
            // TypeScript and Python SDKs send.
            var wireResponses = new List<Dictionary<string, object>>(responses.Count);
            foreach (var r in responses)
            {
                wireResponses.Add(new Dictionary<string, object>
                {
                    ["fieldId"] = r.FieldId,
                    ["value"] = r.Value,
                });
            }
            var body = new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["formId"] = formId,
                ["sessionId"] = sessionId,
                ["responses"] = wireResponses,
            };
            if (askedAtSec.HasValue) body["askedAtSec"] = askedAtSec.Value;
            if (answeredAtSec.HasValue) body["answeredAtSec"] = answeredAtSec.Value;

            try
            {
                var result = await _http.JsonAsync<SubmitFeedbackResult>(
                    "POST",
                    "/api/telemetry/feedback",
                    body,
                    ct).ConfigureAwait(Playloop.PlAwait.Continue);

                if (result == null || !result.Ok || string.IsNullOrEmpty(result.SubmissionId))
                {
                    throw new PlayloopPlaytestException(
                        0, "unknown", "feedback.SubmitAsync returned an unexpected response shape.");
                }
                return result;
            }
            catch (PlayloopException ex)
            {
                var (reason, message) = ExtractReasonAndMessage(ex);
                throw new PlayloopPlaytestException(ex.Status, reason, message, ex);
            }
        }

        private static (string Reason, string Message) ExtractReasonAndMessage(PlayloopException ex)
        {
            // Mirror the helper in PlaytestApi: pull `reason` + `message`
            // out of the parsed body when present; fall back to the bare
            // exception message otherwise.
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
