#nullable enable
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Playloop.Editor
{
    /// <summary>
    /// One-click asset bootstrap helpers for the Playloop top-level menu.
    /// The SDK auto-loads <c>Assets/Resources/PlayloopSettings.asset</c>
    /// via <see cref="PlayloopSettings.LoadOrThrow(string)"/>, so the
    /// expected first-run flow is "Playloop > Create Default Settings
    /// Asset": drop the asset in the right place, ping it in the
    /// Project view, and let the dev fill in the ingest key in the
    /// Inspector.
    ///
    /// <para>
    /// A more elaborate variant of this pattern also creates a separate
    /// <c>PlayloopSecrets</c> asset so the API key can be gitignored
    /// independently from the rest of the settings; the SDK ships only the
    /// unified <see cref="PlayloopSettings"/> asset, so this helper is
    /// simpler.
    /// </para>
    /// </summary>
    internal static class PlayloopAssetBootstrap
    {
        /// <summary>
        /// Idempotent: creates the asset only when it doesn't exist.
        /// Always pings the asset in the Project view so the dev sees
        /// where it landed and can immediately edit the API key in the
        /// Inspector.
        /// </summary>
        public static PlayloopSettings CreateDefaultSettingsAsset()
        {
            EnsureResourcesFolder();

            var existing = TryLoadSettings();
            if (existing != null)
            {
                Debug.Log($"[Playloop] {PlayloopEditorPaths.SettingsAssetPath} already exists, leaving as-is.");
                Selection.activeObject = existing;
                EditorGUIUtility.PingObject(existing);
                return existing;
            }

            var settings = ScriptableObject.CreateInstance<PlayloopSettings>();
            AssetDatabase.CreateAsset(settings, PlayloopEditorPaths.SettingsAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(
                $"[Playloop] Created {PlayloopEditorPaths.SettingsAssetPath}. " +
                "Open it in the Inspector and paste your ingest key (pl_ik_...) " +
                "into the API key field. Without it the SDK will refuse to build " +
                "a client.");
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
            return settings;
        }

        /// <summary>
        /// Ping the existing asset (or create it if missing, same
        /// behaviour as a "Reveal Settings Asset" menu). The
        /// validator on the menu item disables this command when the
        /// asset is already missing AND the user hasn't opted in, but
        /// we keep the auto-create fallback here so anything calling
        /// this directly doesn't crash.
        /// </summary>
        public static void RevealSettingsAsset()
        {
            var settings = TryLoadSettings() ?? CreateDefaultSettingsAsset();
            if (settings != null)
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }
        }

        /// <summary>
        /// Best-effort load of the canonical settings asset at
        /// <see cref="PlayloopEditorPaths.SettingsAssetPath"/>. Returns
        /// null if not found.
        /// </summary>
        public static PlayloopSettings TryLoadSettings()
        {
            return AssetDatabase.LoadAssetAtPath<PlayloopSettings>(PlayloopEditorPaths.SettingsAssetPath);
        }

        private static void EnsureResourcesFolder()
        {
            if (!AssetDatabase.IsValidFolder(PlayloopEditorPaths.ResourcesDir))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }
        }
    }
}
#endif
