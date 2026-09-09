#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using UnityEngine;

namespace Playloop
{
    /// <summary>
    /// Resolves the stable device identifier this install reports on every
    /// session. Used by <see cref="PlayloopClient"/> when
    /// <see cref="PlayloopOptions.DeviceId"/> is left unset, so consumers don't
    /// have to write their own PlayerPrefs-juggling bootstrap.
    ///
    /// <para>Strategy:</para>
    /// <list type="number">
    ///   <item>
    ///     Prefer <see cref="SystemInfo.deviceUniqueIdentifier"/>: Unity's
    ///     hardware-derived 32-char hex string. Stable across PlayerPrefs
    ///     resets, editor ↔ built-app boundaries, project rebuilds, and
    ///     bundle-identifier changes (which all silently fragment the
    ///     PlayerPrefs-only approach into multiple "users" from the same
    ///     machine, the bug this method exists to prevent).
    ///   </item>
    ///   <item>
    ///     If that returns <see cref="SystemInfo.unsupportedIdentifier"/>
    ///     (older WebGL builds, sandboxed envs), fall back to a
    ///     PlayerPrefs-backed GUID. Still better than nothing on those
    ///     platforms.
    ///   </item>
    ///   <item>
    ///     Mirror the resolved id to PlayerPrefs anyway under
    ///     <c>"Playloop.DeviceId"</c> so historical installs that previously
    ///     had a GUID don't lose continuity, and so debugging tools can read
    ///     it back.
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// The id is anonymous (a hash, not raw hardware IDs) but stable per
    /// install. That's the right primitive for "returning tester" metrics.
    /// </para>
    /// </summary>
    public static class DeviceIdResolver
    {
        /// <summary>
        /// PlayerPrefs key the resolved device id is mirrored to. Public so
        /// debugging tools / migrations can read it back without hard-coding
        /// the string in two places.
        /// </summary>
        public const string PlayerPrefsKey = "Playloop.DeviceId";

        /// <summary>
        /// Resolve a stable per-install device identifier. Always returns a
        /// non-empty string. See the type-level doc for the resolution order.
        /// </summary>
        public static string Resolve()
        {
            var hwId = SystemInfo.deviceUniqueIdentifier;
            if (!string.IsNullOrEmpty(hwId) && hwId != SystemInfo.unsupportedIdentifier)
            {
                // Mirror to PlayerPrefs alongside any pre-existing GUID so
                // debugging tools / pre-existing reads stay consistent. It's
                // a no-op write if the value is already there.
                try
                {
                    PlayerPrefs.SetString(PlayerPrefsKey, hwId);
                    PlayerPrefs.Save();
                }
                catch
                {
                    // PlayerPrefs can throw in edge environments (read-only
                    // sandboxes, headless test rigs). The hardware id is
                    // still authoritative. We just lose the diagnostic
                    // mirror, which is fine.
                }
                return hwId;
            }

            var existing = PlayerPrefs.GetString(PlayerPrefsKey, null);
            if (!string.IsNullOrEmpty(existing)) return existing;

            var fresh = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PlayerPrefsKey, fresh);
            PlayerPrefs.Save();
            return fresh;
        }
    }
}
#else
using System;

namespace Playloop
{
    /// <summary>
    /// Standalone-.NET stub used when this assembly is built outside Unity
    /// (e.g. <c>dotnet test</c>). Generates a per-process random GUID. The
    /// real Unity implementation lives above the <c>#else</c> guard.
    /// </summary>
    public static class DeviceIdResolver
    {
        /// <summary>Mirrors the Unity-side constant so test code referencing the key still compiles.</summary>
        public const string PlayerPrefsKey = "Playloop.DeviceId";

        // Cache so two consecutive calls return the same value within a
        // single process. Matches the Unity-side idempotence guarantee.
        private static string? _cached;

        /// <summary>
        /// Returns a stable per-process device id. Outside Unity there is no
        /// hardware identifier and no PlayerPrefs, so we synthesize a GUID
        /// on first call and cache it.
        /// </summary>
        public static string Resolve()
        {
            return _cached ??= Guid.NewGuid().ToString("N");
        }
    }
}
#endif
