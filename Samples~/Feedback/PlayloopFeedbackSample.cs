#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Playloop;
using Playloop.Feedback;
using UnityEngine;

namespace Playloop.Samples.Feedback
{
    /// <summary>
    /// Drop-in sample for Player Feedback. Attach this script to any
    /// GameObject in a scene with a configured PlayloopClient (set
    /// <see cref="ApiKey"/> in the Inspector or load from your own
    /// settings asset).
    ///
    /// Two patterns demonstrated:
    ///   1) <c>OpenDefaultForm</c>: the SDK's built-in UGUI form,
    ///      one call from scene to result.
    ///   2) <c>SubmitFromCustomUI</c>: your own overlay collects
    ///      answers; the SDK just transports them.
    /// </summary>
    public sealed class PlayloopFeedbackSample : MonoBehaviour
    {
        [Header("Playloop")]
        [Tooltip("Your game's ingest key. Copy it from the game's Connections → Unity page. Telemetry sent with it is attributed to that game automatically.")]
        public string ApiKey = "pl_ik_...";

        [Tooltip("Feedback form id (ff_…). Create the form at /games/<slug>/feedback.")]
        public string FormId = "ff_...";

        private PlayloopClient? _client;

        private void Start()
        {
            _client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = ApiKey,
            });

            // Start a telemetry session. StartSession() stages it; the
            // server assigns the id on the first flush, surfaced as
            // _client.Telemetry.CurrentSessionId. We Track one event so the
            // first flush has something to establish the session with. But
            // we don't block on it here: the feedback form resolves the id
            // lazily when the player submits.
            _client.Telemetry.StartSession(new Dictionary<string, object>
            {
                ["title"] = "Feedback sample",
            });
            _client.Telemetry.Track("feedback_sample_started");
        }

        /// <summary>
        /// Wire this up to a "Send feedback" button in your pause menu.
        /// Opens the SDK's default form, awaits the player, prints the
        /// outcome.
        /// </summary>
        public async void OpenDefaultForm()
        {
            if (_client == null)
            {
                Debug.LogWarning("[Playloop sample] Client not ready yet.");
                return;
            }

            // Pass a provider, not a fixed id: the form resolves the
            // session id when the player submits, so this works even if
            // the first telemetry flush hasn't landed yet.
            var client = _client;
            var result = await client.FeedbackForm.OpenAsync(
                formId: FormId,
                sessionIdProvider: () => client.Telemetry.CurrentSessionId);

            switch (result.Outcome)
            {
                case FeedbackFormOutcome.Submitted:
                    Debug.Log($"[Playloop sample] submission {result.Result!.SubmissionId} " +
                              $"with {result.Result.ResponseIds.Count} answers");
                    break;
                case FeedbackFormOutcome.Dismissed:
                    Debug.Log("[Playloop sample] player dismissed the form");
                    break;
                case FeedbackFormOutcome.Failed:
                    Debug.LogWarning($"[Playloop sample] failed: {result.Error}");
                    break;
            }
        }

        /// <summary>
        /// Use this shape when you already render your own UI and just
        /// want the SDK to carry the answers.
        /// </summary>
        public async Task SubmitFromCustomUI(string ratingOneToFive, string comment)
        {
            if (_client == null) return;
            var sessionId = _client.Telemetry.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                Debug.LogWarning("[Playloop sample] No active session yet. Try again after the first telemetry flush.");
                return;
            }

            var responses = new List<FeedbackResponseInput>
            {
                new("q1", ratingOneToFive),  // rating-1-5
                new("q2", comment),          // short-text
            };

            try
            {
                var result = await _client.SubmitFeedbackAsync(
                    formId: FormId,
                    sessionId: sessionId,
                    responses: responses);
                Debug.Log($"[Playloop sample] saved as {result.SubmissionId}");
            }
            catch (Playloop.Playtest.PlayloopPlaytestException ex)
            {
                Debug.LogWarning($"[Playloop sample] {ex.Reason}: {ex.Message}");
            }
        }
    }
}
