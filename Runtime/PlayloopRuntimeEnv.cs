#nullable enable
namespace Playloop
{
    /// <summary>
    /// Single source of truth for the build-environment facts the SDK reads at
    /// runtime: "are we in the editor?", "is this a development build?", and the
    /// engine fingerprint. Centralised here so the environment auto-derive
    /// (<see cref="PlayloopClient"/>), the suppress-in-editor gate
    /// (<see cref="Telemetry.TelemetryApi"/>), and the session-metadata stamp all
    /// agree on the same answer.
    ///
    /// <para>
    /// Every member is a compile-time constant or a cheap UnityEngine read. Under
    /// <c>PLAYLOOP_DOTNET_STANDALONE</c> (the headless dotnet-test build, no
    /// UnityEngine) the editor/dev-build facts are <c>false</c> and the engine
    /// version is empty. The engine name stays <c>"unity"</c> so the fingerprint
    /// is stable in CI.
    /// </para>
    /// </summary>
    internal static class PlayloopRuntimeEnv
    {
        /// <summary>Cross-engine engine fingerprint. Always <c>"unity"</c> for this SDK.</summary>
        public const string EngineName = "unity";

        /// <summary>
        /// The engine version string (<c>Application.unityVersion</c>), or empty
        /// outside Unity (dotnet test).
        /// </summary>
        public static string EngineVersion
        {
            get
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                try { return UnityEngine.Application.unityVersion; }
                catch { return ""; }
#else
                return "";
#endif
            }
        }

        /// <summary>
        /// True when running in the Unity editor. Always <c>false</c> outside Unity.
        /// </summary>
        public static bool IsEditor
        {
            get
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                try { return UnityEngine.Application.isEditor; }
                catch { return false; }
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// True when this is a development build: the editor, a
        /// <c>Debug.isDebugBuild</c> player, or one compiled with the
        /// <c>DEVELOPMENT_BUILD</c> define. Always <c>false</c> outside Unity.
        /// Drives both the environment auto-derive and the suppress-in-editor gate.
        /// </summary>
        public static bool IsDevelopmentBuild
        {
            get
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                try
                {
                    if (UnityEngine.Application.isEditor) return true;
                    if (UnityEngine.Debug.isDebugBuild) return true;
                }
                catch { return false; }
#if DEVELOPMENT_BUILD
                return true;
#else
                return false;
#endif
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Derive an environment slug from the build when the consumer left
        /// <see cref="PlayloopOptions.Environment"/> at its default (<c>"dev"</c>): a
        /// development build (editor / debug player / DEVELOPMENT_BUILD) stays
        /// <c>"dev"</c>, while a shipping release build is promoted to
        /// <c>"production"</c>. An explicit non-default environment always wins and
        /// never reaches this method.
        /// </summary>
        public static string DeriveEnvironment()
            => IsDevelopmentBuild ? "dev" : "production";
    }
}
