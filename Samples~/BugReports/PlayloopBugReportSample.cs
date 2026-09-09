#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Playloop;
using Playloop.BugReports;
using UnityEngine;

namespace Playloop.Samples.BugReports
{
    /// <summary>
    /// Drop-in sample for player Bug Reports. Attach this script to any
    /// GameObject in a scene with a configured PlayloopClient (set
    /// <see cref="ApiKey"/> in the Inspector or load from your own
    /// settings asset).
    ///
    /// Two patterns demonstrated:
    ///   1) <c>OpenDefaultForm</c>: the SDK's built-in UGUI form,
    ///      one call from scene to result.
    ///   2) <c>SubmitFromCustomUI</c>: your own overlay collects the
    ///      title / description / severity; the SDK just transports them.
    /// </summary>
    public sealed class PlayloopBugReportSample : MonoBehaviour
    {
        [Header("Playloop")]
        [Tooltip("Your game's ingest key. Copy it from the game's Connections → Unity page. Reports sent with it are attributed to that game automatically.")]
        public string ApiKey = "pl_ik_...";

        private PlayloopClient? _client;

        private void Start()
        {
            _client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = ApiKey,
            });

            // Start a telemetry session so a filed report can attach to it.
            // The server assigns the id on the first flush, surfaced as
            // _client.Telemetry.CurrentSessionId. A bug report is still worth
            // filing without a session, so the form resolves the id lazily and
            // submits unattributed if there isn't one yet.
            _client.Telemetry.StartSession(new Dictionary<string, object>
            {
                ["title"] = "Bug report sample",
            });
            _client.Telemetry.Track("bug_report_sample_started");
        }

        /// <summary>
        /// Wire this up to a "Report a bug" button in your pause menu.
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
            var result = await client.BugReportForm.OpenAsync(
                sessionIdProvider: () => client.Telemetry.CurrentSessionId);

            switch (result.Outcome)
            {
                case BugReportFormOutcome.Submitted:
                    Debug.Log($"[Playloop sample] bug report {result.Result!.BugReportId} " +
                              $"(routing: {result.Result.RoutingStatus})");
                    break;
                case BugReportFormOutcome.Dismissed:
                    Debug.Log("[Playloop sample] player dismissed the form");
                    break;
                case BugReportFormOutcome.Failed:
                    Debug.LogWarning($"[Playloop sample] failed: {result.Error}");
                    break;
            }
        }

        /// <summary>
        /// Use this shape when you already render your own UI and just
        /// want the SDK to carry the report.
        /// </summary>
        public async Task SubmitFromCustomUI(string title, string description, string severity)
        {
            if (_client == null) return;

            try
            {
                var result = await _client.SubmitBugReportAsync(
                    title: title,
                    description: description,
                    severity: severity, // BugReportSeverities.Low / Medium / High / Critical
                    sessionId: _client.Telemetry.CurrentSessionId,
                    context: new BugReportContext
                    {
                        // buildVersion / environment / platform auto-fill from
                        // the same values telemetry stamps; add device fields
                        // a triager might want.
                        Device = new Dictionary<string, object>
                        {
                            ["gpu"] = SystemInfo.graphicsDeviceName,
                        },
                    });
                Debug.Log($"[Playloop sample] filed as {result.BugReportId}");
            }
            catch (Playloop.Playtest.PlayloopPlaytestException ex)
            {
                Debug.LogWarning($"[Playloop sample] {ex.Reason}: {ex.Message}");
            }
        }
    }
}
