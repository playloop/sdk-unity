#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using UnityEngine;

namespace Playloop.Identity
{
    /// <summary>
    /// PlayerPrefs-backed storage for the player's consent preference.
    /// Allowed values:
    ///
    /// <list type="bullet">
    ///   <item><c>"anonymous"</c>: default; SDK sends deviceId only.</item>
    ///   <item><c>"studio-wide"</c>: SDK sends vendorId for cross-game.</item>
    ///   <item><c>"cross-platform"</c>: adds linkedId.</item>
    ///   <item><c>"opt-out"</c>: stores the player's preference; SDK-side enforcement is on the roadmap.</item>
    /// </list>
    ///
    /// The server-side stub records the consent level on the player row
    /// via <c>updatePlayerConsentLevel</c>.
    /// </summary>
    public static class ConsentStore
    {
        /// <summary>PlayerPrefs key the consent level is mirrored to.</summary>
        public const string PlayerPrefsKey = "Playloop.ConsentLevel";

        /// <summary>Default returned when no value has been persisted.</summary>
        public const string DefaultLevel = "anonymous";

        /// <summary>
        /// Read the persisted consent level. Returns <see cref="DefaultLevel"/>
        /// when nothing's been set: the most conservative bucket, matching
        /// the server-side default in `players.consent_level`.
        /// </summary>
        public static string Get()
        {
            try
            {
                var value = PlayerPrefs.GetString(PlayerPrefsKey, string.Empty);
                return string.IsNullOrEmpty(value) ? DefaultLevel : value;
            }
            catch
            {
                return DefaultLevel;
            }
        }

        /// <summary>
        /// Persist a consent level. Caller is responsible for passing one of
        /// the four allowed strings; this helper doesn't validate (the
        /// server will silently fall back to defaults if it sees something
        /// unexpected). Empty/null falls back to <see cref="DefaultLevel"/>.
        /// </summary>
        public static void Set(string level)
        {
            try
            {
                var normalized = string.IsNullOrEmpty(level) ? DefaultLevel : level;
                PlayerPrefs.SetString(PlayerPrefsKey, normalized);
                PlayerPrefs.Save();
            }
            catch
            {
                // Sandboxed env: fall through; the next Get() returns the
                // default. Telemetry continues with whatever the previous
                // session persisted (or DefaultLevel on first run).
            }
        }

        /// <summary>
        /// Test-only: drop the persisted level so the next <see cref="Get"/>
        /// returns <see cref="DefaultLevel"/>. Same seam as the standalone
        /// stub, so a test suite resets the store the same way in both hosts.
        /// </summary>
        public static void __ResetForTests()
        {
            try
            {
                PlayerPrefs.DeleteKey(PlayerPrefsKey);
                PlayerPrefs.Save();
            }
            catch
            {
                // Sandboxed env: nothing persisted, nothing to drop.
            }
        }
    }
}
#else
namespace Playloop.Identity
{
    /// <summary>
    /// Standalone-.NET stub used outside Unity. Per-process static field;
    /// resets between tests via <c>__ResetForTests</c>.
    /// </summary>
    public static class ConsentStore
    {
        public const string PlayerPrefsKey = "Playloop.ConsentLevel";
        public const string DefaultLevel = "anonymous";

        private static string _value = DefaultLevel;

        public static string Get() => _value;

        public static void Set(string level)
        {
            _value = string.IsNullOrEmpty(level) ? DefaultLevel : level;
        }

        public static void __ResetForTests() => _value = DefaultLevel;
    }
}
#endif
