#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Playloop.Telemetry
{
    /// <summary>
    /// Hidden MonoBehaviour that fires <c>focus_lost</c> and <c>focus_gained</c>
    /// telemetry events the instant Unity's focus state flips. Spawned by
    /// <see cref="TelemetryApi.AutoBatch"/> so consumers don't need to wire
    /// anything. Focus tracking turns on alongside the auto-batch loop and
    /// turns off when <see cref="TelemetryApi.StopAutoBatchAsync"/> runs.
    ///
    /// <para>
    /// The heartbeat already carries <c>isFocused</c> at 60s resolution as a
    /// diagnostic backup; this component exists so server-side "active play
    /// time" math (wall clock minus idle gaps where the player alt-tabbed
    /// away) has the exact transition timestamps instead of having to
    /// interpolate between heartbeats.
    /// </para>
    ///
    /// <para>
    /// The GameObject is created with <see cref="HideFlags.HideAndDontSave"/>
    /// and <c>DontDestroyOnLoad</c> so it survives scene transitions and
    /// doesn't clutter the hierarchy. Same pattern as <c>PlayloopHeartbeat</c>.
    /// </para>
    /// </summary>
    internal sealed class PlayloopFocusTracker : MonoBehaviour
    {
        // Held as a plain reference (not WeakReference): the TelemetryApi
        // owns this GameObject's lifetime (StopAutoBatchAsync()/Dispose()
        // destroys us before the api goes away), so a hard ref is safe.
        private TelemetryApi? _telemetry;

        /// <summary>Wire up the api reference. Called by the factory in AutoBatch.</summary>
        internal void Attach(TelemetryApi telemetry)
        {
            _telemetry = telemetry;
        }

        /// <summary>
        /// Unity invokes this on the main thread whenever the application gains
        /// or loses focus: alt-tab, window minimize, OS-level focus steal,
        /// editor play-mode pause toggles. Fires immediately so the server has
        /// the exact transition moment rather than waiting on the next 60s
        /// heartbeat.
        ///
        /// <para>
        /// Track is synchronous (just appends to a buffer) so it's safe to call
        /// from the focus callback. AutoBatch will pick up the event on the
        /// next flush tick (≤5s). On a clean app quit the bounded
        /// shutdown hook in <see cref="PlayloopClient"/> drains the buffer
        /// before Unity tears the AppDomain down.
        /// </para>
        /// </summary>
        private void OnApplicationFocus(bool hasFocus)
        {
            var telemetry = _telemetry;
            if (telemetry == null) return;

            try
            {
                telemetry.Track(
                    hasFocus ? "focus_gained" : "focus_lost",
                    new Dictionary<string, object>
                    {
                        { "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                    });
            }
            catch (Exception e)
            {
                // Telemetry must never crash gameplay. Surface to the console so
                // a misbehaving Track doesn't silently swallow focus events
                // forever.
                Debug.LogWarning($"[Playloop] Focus tracker failed to enqueue event: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            _telemetry = null;
        }
    }
}
#endif
