#nullable enable
using System;
using System.Reflection;

namespace Playloop
{
    /// <summary>
    /// Resolves the optional <c>distribution</c> value stamped on the
    /// <c>session_start</c> event: where this build is being played
    /// (<c>steam</c>, <c>itch</c>, <c>direct</c>, ...).
    ///
    /// <para>
    /// Order: the value the developer set (<see cref="PlayloopOptions.Distribution"/>),
    /// then <c>steam</c> when a Steamworks integration is present AND initialised,
    /// then nothing. The SDK never guesses: when neither is known the field is
    /// omitted. A web build needs neither, since Playloop reads the page it was
    /// served from.
    /// </para>
    ///
    /// <para>
    /// Steam is detected by reflection, so the SDK takes no dependency on any
    /// Steamworks package and compiles the same with or without one. It reads two
    /// well-known "is Steam up?" flags if their types are loaded:
    /// <c>Steamworks.SteamClient.IsValid</c> (Facepunch.Steamworks) and
    /// <c>SteamManager.Initialized</c> (the Steamworks.NET manager script). It never
    /// calls into the native Steam API. Construct the client after Steam has
    /// initialised, or set <see cref="PlayloopOptions.Distribution"/> yourself.
    /// </para>
    /// </summary>
    internal static class DistributionResolver
    {
        internal const string Steam = "steam";

        /// <summary>Longest value the server accepts.</summary>
        internal const int MaxLength = 40;

        /// <summary>The value to stamp, or <c>null</c> to omit the field.</summary>
        internal static string? Resolve(string? configured, Func<bool> isSteamInitialized)
        {
            var explicitValue = Normalize(configured);
            if (explicitValue != null) return explicitValue;
            bool steam;
            try { steam = isSteamInitialized(); }
            catch { steam = false; }
            return steam ? Steam : null;
        }

        /// <summary>
        /// Trim + lowercase, and drop anything that is not a short slug of
        /// letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, or that is one of the
        /// values Playloop reports itself.
        /// </summary>
        internal static string? Normalize(string? raw)
        {
            if (raw == null) return null;
            var value = raw.Trim().ToLowerInvariant();
            if (value.Length == 0 || value.Length > MaxLength) return null;
            if (!IsSlugStart(value[0])) return null;
            foreach (var c in value)
            {
                if (!IsSlugStart(c) && c != '.' && c != '_' && c != '-') return null;
            }
            return Array.IndexOf(Reserved, value) >= 0 ? null : value;
        }

        /// <summary>Values Playloop reports itself; a build may not claim them.</summary>
        private static readonly string[] Reserved = { "itch-web", "web-other", "tvg-site", "unknown", "other" };

        private static bool IsSlugStart(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        /// <summary>
        /// True when a loaded Steamworks integration reports itself initialised.
        /// Always false in a web build, where Steam cannot run.
        /// </summary>
        internal static bool IsSteamInitialized()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            return ReadStaticBool("Steamworks.SteamClient", "IsValid")
                || ReadStaticBool("SteamManager", "Initialized");
#endif
        }

        /// <summary>
        /// Reads a public static bool property off a type found in any loaded
        /// assembly. False when the type or property is absent, or on any error.
        /// </summary>
        internal static bool ReadStaticBool(string typeName, string propertyName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? type;
                    try { type = asm.GetType(typeName, throwOnError: false); }
                    catch { continue; }
                    if (type == null) continue;
                    var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                    if (prop == null || prop.PropertyType != typeof(bool)) continue;
                    if (prop.GetValue(null) is bool b && b) return true;
                }
            }
            catch
            {
                // Reflection is best-effort: a stripped or unloadable assembly
                // means "not detected", never an error the game sees.
            }
            return false;
        }
    }
}
