#nullable enable
#if UNITY_EDITOR
using System;
using UnityEngine.Networking;

namespace Playloop.Editor
{
    /// <summary>
    /// Constants shared across the editor menus + windows. URLs all
    /// live here so the marketing site, docs, and repo URL can be
    /// updated in one place when (not if) they move. The
    /// <c>DashboardUrl</c> composer also strips an <c>api.</c> host
    /// prefix when one is configured so dashboard links land on the
    /// apex (<c>https://playloop.gg</c>) rather than 404ing on a
    /// dedicated API host.
    /// </summary>
    internal static class PlayloopEditorPaths
    {
        public const string DocsUrl = "https://playloop.gg/sdks#unity";
        public const string RoadmapUrl = "https://playloop.gg/roadmap";
        public const string RepoUrl = "https://github.com/playloop/sdk-unity";

        /// <summary>
        /// Default path inside <c>Assets/Resources</c> where the SDK
        /// auto-discovers <see cref="PlayloopSettings"/> at runtime.
        /// Editor bootstrap actions create the asset here so
        /// <see cref="PlayloopSettings.LoadOrThrow(string)"/> can find
        /// it without any extra wiring.
        /// </summary>
        public const string SettingsAssetPath = "Assets/Resources/PlayloopSettings.asset";

        public const string ResourcesDir = "Assets/Resources";

        /// <summary>
        /// Compose the dashboard URL for a specific game + env. Strips a
        /// leading <c>api.</c> from the host so the link lands on the
        /// apex marketing/dashboard domain rather than the API origin.
        /// </summary>
        public static string DashboardUrl(string baseUrl, string gameSlug, string env)
        {
            var origin = ResolveDashboardOrigin(baseUrl);
            if (string.IsNullOrWhiteSpace(gameSlug))
            {
                return origin;
            }
            var encodedSlug = UnityWebRequest.EscapeURL(gameSlug);
            var encodedEnv = string.IsNullOrWhiteSpace(env)
                ? "production"
                : UnityWebRequest.EscapeURL(env);
            return $"{origin}/games/{encodedSlug}/dashboard?env={encodedEnv}";
        }

        /// <summary>
        /// Same api → apex host swap, exposed so other editor surfaces
        /// (event-config window's "Open in Dashboard" link) can reuse
        /// the same resolution. Falls back to the configured base URL
        /// when the dashboard and API share an origin.
        /// </summary>
        public static string ResolveDashboardOrigin(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return "https://playloop.gg";
            try
            {
                var uri = new Uri(baseUrl.TrimEnd('/'));
                var host = uri.Host;
                if (host.StartsWith("api.", StringComparison.OrdinalIgnoreCase))
                {
                    host = host.Substring(4);
                }
                var builder = new UriBuilder
                {
                    Scheme = uri.Scheme,
                    Host = host,
                    Port = uri.IsDefaultPort ? -1 : uri.Port,
                };
                return builder.Uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return baseUrl.TrimEnd('/');
            }
        }
    }
}
#endif
