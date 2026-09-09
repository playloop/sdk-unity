#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Reflection;

namespace Playloop.Identity
{
    /// <summary>
    /// Resolves the platform-stable vendor identifier the SDK sends with
    /// session-create telemetry. Order of detection:
    ///
    /// <list type="number">
    ///   <item>
    ///     Steamworks (via runtime reflection, no asmdef dependency).
    ///     Returns the player's <c>SteamID</c> as a string. If Steamworks
    ///     isn't installed in the project, this branch silently no-ops.
    ///   </item>
    ///   <item>
    ///     iOS IDFV (<c>UnityEngine.iOS.Device.vendorIdentifier</c>). Only
    ///     attempted under <c>#if UNITY_IOS</c> and never in the editor.
    ///   </item>
    ///   <item>
    ///     Null. Other platforms (Android non-Steam, WebGL, console) don't
    ///     get a vendor id in V1.
    ///   </item>
    /// </list>
    ///
    /// The server uses vendor IDs to stitch identity across multiple games
    /// in a studio's library. Device
    /// IDs are inherently per-game; vendor IDs aren't.
    ///
    /// Reflection probe lives behind a static cache so we only walk the
    /// assembly load list once per process. A failed probe stays failed for
    /// the lifetime of the app. Steamworks won't appear mid-run.
    /// </summary>
    public static class VendorIdResolver
    {
        // Caches the resolved vendor id across calls; null sentinel
        // distinguishes "unresolved" from "explicitly empty."
        private static string? _cached;
        private static bool _resolved;

        /// <summary>
        /// Resolve a stable vendor identifier, or null when no platform
        /// produces one. Cached after first call.
        /// </summary>
        public static string? Resolve()
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = TryResolveSteam() ?? TryResolveIOS();
            return _cached;
        }

        /// <summary>Test-only: clears the cache so a follow-up Resolve re-probes.</summary>
        public static void __ResetForTests()
        {
            _resolved = false;
            _cached = null;
        }

        // Steamworks detection via reflection. Two common namespace +
        // assembly combos exist (`Steamworks.NET` by Riley Labrecque, and
        // Facepunch.Steamworks). We probe the most popular one first.
        private static string? TryResolveSteam()
        {
            // Steamworks.NET (rlabrecque/Steamworks.NET).
            var steamUserType =
                Type.GetType("Steamworks.SteamUser, com.rlabrecque.steamworks.net", throwOnError: false)
                ?? Type.GetType("Steamworks.SteamUser, Steamworks.NET", throwOnError: false)
                ?? Type.GetType("Steamworks.SteamUser, Assembly-CSharp", throwOnError: false);
            if (steamUserType == null) return null;

            var getSteamIdMethod = steamUserType.GetMethod(
                "GetSteamID",
                BindingFlags.Public | BindingFlags.Static);
            if (getSteamIdMethod == null) return null;

            try
            {
                var result = getSteamIdMethod.Invoke(null, parameters: null);
                if (result == null) return null;
                var s = result.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                // Steamworks present but not initialized (no SteamAPI.Init
                // called yet). Silently no-op. The caller falls through
                // to IDFV or null.
                return null;
            }
        }

        private static string? TryResolveIOS()
        {
#if UNITY_IOS && !UNITY_EDITOR
            try
            {
                var idfv = UnityEngine.iOS.Device.vendorIdentifier;
                return string.IsNullOrEmpty(idfv) ? null : idfv;
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }
    }
}
#else
namespace Playloop.Identity
{
    /// <summary>
    /// Standalone-.NET stub used when this assembly is built outside Unity
    /// (e.g. <c>dotnet test</c>). Always returns null: no platform vendor
    /// identifier exists outside a Unity runtime.
    /// </summary>
    public static class VendorIdResolver
    {
        public static string? Resolve() => null;
        public static void __ResetForTests() { /* no-op outside Unity */ }
    }
}
#endif
