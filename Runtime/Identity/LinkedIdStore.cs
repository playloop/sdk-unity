#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using UnityEngine;

namespace Playloop.Identity
{
    /// <summary>
    /// PlayerPrefs-backed storage for the player's optional self-supplied
    /// linked id (typically an email or account id). Persists across
    /// sessions; cleared explicitly via <see cref="Clear"/> or by passing
    /// null to <see cref="Set"/>.
    ///
    /// The server stitches identity across platforms using linked ids
    /// (Steam install + same player's iOS install + their account portal
    /// linking the two). Treated identically to vendor IDs for cross-game
    /// merge purposes.
    ///
    /// The SDK does NOT prompt the player for a linked id. That's a game-
    /// dev decision. <see cref="Playloop.SetLinkedId(string)"/> is the
    /// public API; this store is the persistence layer underneath.
    /// </summary>
    public static class LinkedIdStore
    {
        /// <summary>PlayerPrefs key the linked id is mirrored to.</summary>
        public const string PlayerPrefsKey = "Playloop.LinkedId";

        /// <summary>
        /// Read the persisted linked id. Returns null when nothing's been
        /// set (or when the SDK is running in a sandboxed env where
        /// PlayerPrefs throws).
        /// </summary>
        public static string? Get()
        {
            try
            {
                var value = PlayerPrefs.GetString(PlayerPrefsKey, string.Empty);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Persist a linked id, or clear it when <paramref name="linkedId"/>
        /// is null or empty. Writes to PlayerPrefs and calls Save so the
        /// value survives a hard process exit.
        /// </summary>
        public static void Set(string? linkedId)
        {
            try
            {
                if (string.IsNullOrEmpty(linkedId))
                {
                    PlayerPrefs.DeleteKey(PlayerPrefsKey);
                }
                else
                {
                    PlayerPrefs.SetString(PlayerPrefsKey, linkedId);
                }
                PlayerPrefs.Save();
            }
            catch
            {
                // Sandboxed env (read-only filesystem, test rig with no
                // PlayerPrefs file backing). The next Get() will return
                // null which is the right default. Telemetry continues
                // without the linked id.
            }
        }

        /// <summary>Convenience wrapper around <c>Set(null)</c>.</summary>
        public static void Clear() => Set(null);
    }
}
#else
namespace Playloop.Identity
{
    /// <summary>
    /// Standalone-.NET stub used outside Unity. Backs the linked id in a
    /// per-process static field so unit tests can exercise the
    /// round-trip without touching PlayerPrefs.
    /// </summary>
    public static class LinkedIdStore
    {
        public const string PlayerPrefsKey = "Playloop.LinkedId";

        private static string? _value;

        public static string? Get() => _value;

        public static void Set(string? linkedId)
        {
            _value = string.IsNullOrEmpty(linkedId) ? null : linkedId;
        }

        public static void Clear() => Set(null);
    }
}
#endif
