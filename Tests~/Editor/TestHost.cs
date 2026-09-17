#nullable enable
namespace Playloop.Tests
{
    /// <summary>
    /// The build facts of whatever host is running this suite, derived
    /// independently of the SDK so a test can say "the SDK agrees with the
    /// build it is in" instead of hard-coding one host's answer.
    ///
    /// <para>
    /// The suite runs in two hosts: <c>dotnet test</c> (no UnityEngine, never
    /// a development build, never the editor) and the Unity editor's EditMode
    /// runner (always the editor, so always a development build). Assertions
    /// about the environment auto-derive, the <c>isEditor</c> stamp and the
    /// suppress-in-editor gate read these instead of a literal.
    /// </para>
    /// </summary>
    internal static class TestHost
    {
        /// <summary>True when the suite is running inside the Unity editor.</summary>
        public static bool IsEditor
        {
            get
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                return UnityEngine.Application.isEditor;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// True when the host counts as a development build: the editor or a
        /// debug player. Mirrors the rule the SDK documents for
        /// <c>PlayloopOptions.SendInEditor</c> and the environment auto-derive.
        /// </summary>
        public static bool IsDevelopmentBuild
        {
            get
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                return UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The environment slug the SDK should auto-derive when the consumer
        /// leaves <c>PlayloopOptions.Environment</c> at its default: a
        /// development build stays <c>"dev"</c>, a release build is promoted to
        /// <c>"production"</c>.
        /// </summary>
        public static string DerivedEnvironment => IsDevelopmentBuild ? "dev" : "production";
    }
}
