#nullable enable
using Playloop.Identity;

namespace Playloop
{
    /// <summary>
    /// Static convenience facade for the SDK's identity / consent APIs.
    ///
    /// The primary SDK entry point is the instance-based
    /// <see cref="PlayloopClient"/>; this facade is a static convenience
    /// wrapper for the cross-game player identity surface.
    /// Named <c>PlayloopSdk</c> (not <c>Playloop</c>) to avoid a name
    /// collision with the <c>Playloop</c> root namespace.
    ///
    /// Calls are persistent. The underlying stores write to PlayerPrefs
    /// (or a per-process static when running outside Unity). The next
    /// session-create telemetry POST will include the persisted values.
    ///
    /// <example>
    /// <code>
    /// // After the player signs into your game's account portal:
    /// PlayloopSdk.SetLinkedId("player@example.com");
    ///
    /// // After the player accepts the studio-wide consent option:
    /// PlayloopSdk.SetConsent("studio-wide");
    /// </code>
    /// </example>
    /// </summary>
    public static class PlayloopSdk
    {
        /// <summary>
        /// Set the player's optional self-supplied linked id (email, account
        /// id, etc.). Pass null to clear. Persists across sessions.
        /// </summary>
        public static void SetLinkedId(string? linkedId) => LinkedIdStore.Set(linkedId);

        /// <summary>
        /// Set the player's consent level. Allowed values:
        /// <c>"anonymous"</c>, <c>"studio-wide"</c>, <c>"cross-platform"</c>,
        /// <c>"opt-out"</c>. Other values are persisted as-is and the
        /// server interprets unknown values as <c>"anonymous"</c>.
        /// </summary>
        public static void SetConsent(string level) => ConsentStore.Set(level);

        /// <summary>Read the current consent level (defaults to <c>"anonymous"</c>).</summary>
        public static string GetConsent() => ConsentStore.Get();
    }
}
