#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Playloop.Editor
{
    /// <summary>
    /// Top-level <b>Playloop/*</b> menu. Centralizes every editor-facing
    /// command (Settings window, Send Test Event, env switcher, asset
    /// bootstrap, dashboard quick-links) so the
    /// first-run flow surfaces in one obvious place instead of being
    /// scattered between the Window menu, an inspector ScriptableObject,
    /// and a Tools submenu.
    ///
    /// <para>
    /// Menu structure (priorities chosen so adjacent gaps ≥ 11 produce
    /// dividers):
    /// <code>
    /// Playloop/
    ///   Settings…                       (100)
    ///   Send Test Event %&amp;t            (102)
    ///   ─────────
    ///   Set Environment/Production      (201)
    ///   Set Environment/Demo            (202)
    ///   Set Environment/Dev             (203)
    ///   Set Environment/Custom…         (204)
    ///   ─────────
    ///   Create Default Settings Asset   (301)
    ///   Reveal Settings Asset           (302)
    ///   ─────────
    ///   Docs/Open Docs                  (401)
    ///   Docs/Open Roadmap               (402)
    ///   Open Dashboard for This Game    (403)
    ///   Open GitHub Repo                (404)
    /// </code>
    /// </para>
    ///
    /// <para>
    /// Uses validator/checked-state mirroring for the env switcher and a
    /// Send Test Event Force-flush + known-causes error block.
    /// </para>
    /// </summary>
    internal static class PlayloopMenu
    {
        // Canonical menu paths (used by both the [MenuItem] attributes
        // and the Menu.SetChecked calls in the validators). Keep these
        // in lockstep with the attributes below or the checked-state
        // mirror silently breaks.
        private const string MenuRoot = "Playloop/";
        private const string MenuSettings = MenuRoot + "Settings…";
        private const string MenuSendTest = MenuRoot + "Send Test Event %&t";
        private const string MenuSetEnvProd = MenuRoot + "Set Environment/Production";
        private const string MenuSetEnvDemo = MenuRoot + "Set Environment/Demo";
        private const string MenuSetEnvDev = MenuRoot + "Set Environment/Dev";
        private const string MenuSetEnvCustom = MenuRoot + "Set Environment/Custom…";
        private const string MenuCreateAsset = MenuRoot + "Create Default Settings Asset";
        private const string MenuRevealAsset = MenuRoot + "Reveal Settings Asset";
        private const string MenuOpenDashboard = MenuRoot + "Open Dashboard for This Game";
        private const string MenuOpenDocs = MenuRoot + "Docs/Open Docs";
        private const string MenuOpenRoadmap = MenuRoot + "Docs/Open Roadmap";
        private const string MenuOpenRepo = MenuRoot + "Open GitHub Repo";

        // ─────────────────── Top group: windows + test event ───────────────────

        [MenuItem(MenuSettings, priority = 100)]
        public static void OpenSettingsWindow() => PlayloopStatusWindow.Open();

        [MenuItem(MenuSendTest, priority = 102)]
        public static async void SendTestEvent()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog(
                    "Playloop",
                    "Send Test Event only works in Play mode (the SDK initializes at " +
                    "construction time, which only runs in a Play session). Press Play, " +
                    "then trigger this menu item again. The shortcut is Cmd/Ctrl+Alt+T.",
                    "OK");
                return;
            }

            var client = PlayloopClient.Current;
            if (client == null)
            {
                EditorUtility.DisplayDialog(
                    "Playloop",
                    "No active Playloop client. Common reasons:\n" +
                    " • The game hasn't constructed a PlayloopClient yet. Check that your " +
                    "bootstrap code runs at BeforeSceneLoad or earlier.\n" +
                    " • The PlayloopSettings.asset is missing or the API key is empty " +
                    "(LoadOrThrow throws on construction in that case).\n" +
                    " • The client was disposed (Dispose() clears PlayloopClient.Current).\n\n" +
                    "Open the Console and look for the first '[Playloop]' init log to see " +
                    "the exact reason the client never came up.",
                    "OK");
                return;
            }

            // Queue + force-flush. The auto-batch loop's normal cadence would
            // hide HTTP errors from the menu's success/failure dialog. We
            // want this command to report the actual server outcome.
            try
            {
                client.Telemetry.Track("test_event", new Dictionary<string, object>
                {
                    { "source", "editor-menu" },
                    { "wallClockMs", DateTime.UtcNow.ToFileTimeUtc() },
                });
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(
                    "Playloop",
                    $"Test event failed at Track(): {e.GetType().Name}\n\n{e.Message}",
                    "OK");
                return;
            }

            // Resolve the slug from the ingest key (cached after first call;
            // never throws). Falls back to a placeholder if the resolve hasn't
            // succeeded (offline / invalid key).
            var resolvedSlug = await client.Resolver.ResolveSlugAsync();
            var slug = string.IsNullOrEmpty(resolvedSlug) ? "(unresolved)" : resolvedSlug!;
            var env = client.Environment ?? "production";
            Debug.Log($"[Playloop] Queued test_event (env={env}, game={slug}). Flushing now…");

            try
            {
                await client.Telemetry.FlushAsync();
                var dashboardUrl = PlayloopEditorPaths.DashboardUrl(
                    ResolveBaseUrlFromSettings(),
                    slug,
                    env);
                Debug.Log($"[Playloop] ✅ Flushed OK. Check the dashboard at {dashboardUrl}");
                // DisplayDialog returns true when the left (OK/action)
                // button is clicked. Wire the "Open Dashboard" branch to
                // launch the browser; the right button is a no-op dismiss.
                var openDashboard = EditorUtility.DisplayDialog(
                    "Playloop",
                    "Test event sent.\n\n" +
                    $"Env: {env}\n" +
                    $"Game slug: {slug}\n\n" +
                    "Open the Playloop dashboard and confirm a 'test_event' row appeared.",
                    "Open Dashboard",
                    "Dismiss");
                if (openDashboard)
                {
                    Application.OpenURL(dashboardUrl);
                }
            }
            catch (Exception e)
            {
                // Surface a "common causes" block. The user is very rarely
                // staring at the Console, so the dialog needs to carry the
                // diagnostic info itself.
                var msg =
                    $"[Playloop] ❌ Flush FAILED: {e.GetType().Name}: {e.Message}\n" +
                    "Common causes:\n" +
                    "  401 → API key in PlayloopSettings.asset is invalid or expired.\n" +
                    "  403 → Key authenticates but lacks scope for this game.\n" +
                    "  404 → Game slug in PlayloopSettings doesn't exist on your account yet.\n" +
                    "  ECONNREFUSED → Base URL is unreachable (offline / wrong port).\n" +
                    "  400 → Malformed env slug or payload shape mismatch (rare).";
                Debug.LogError(msg);
                EditorUtility.DisplayDialog("Playloop", msg, "OK");
            }
        }

        [MenuItem(MenuSendTest, validate = true)]
        private static bool ValidateSendTestEvent()
        {
            return Application.isPlaying && PlayloopClient.Current != null;
        }

        // ─────────────────── Env switcher ───────────────────

        [MenuItem(MenuSetEnvProd, priority = 201)]
        public static void SetEnvProduction() => SetEnv("production");

        [MenuItem(MenuSetEnvDemo, priority = 202)]
        public static void SetEnvDemo() => SetEnv("demo");

        [MenuItem(MenuSetEnvDev, priority = 203)]
        public static void SetEnvDev() => SetEnv("dev");

        [MenuItem(MenuSetEnvCustom, priority = 204)]
        public static void SetEnvCustom()
        {
            var settings = EnsureSettingsExistsOrPrompt();
            if (settings == null) return;
            // EditorUtility has no built-in text-input modal, so we use
            // the asset's current value as the prefill and bring up a
            // lightweight inline window. Inline implementation: see
            // CustomEnvironmentDialog.Show below.
            var current = ReadEnvironment(settings) ?? "";
            CustomEnvironmentDialog.Show(current, slug =>
            {
                if (string.IsNullOrWhiteSpace(slug)) return;
                SetEnv(slug.Trim());
            });
        }

        [MenuItem(MenuSetEnvProd, validate = true)]
        private static bool ValidateProd() => UpdateEnvCheckedState("production", MenuSetEnvProd);

        [MenuItem(MenuSetEnvDemo, validate = true)]
        private static bool ValidateDemo() => UpdateEnvCheckedState("demo", MenuSetEnvDemo);

        [MenuItem(MenuSetEnvDev, validate = true)]
        private static bool ValidateDev() => UpdateEnvCheckedState("dev", MenuSetEnvDev);

        [MenuItem(MenuSetEnvCustom, validate = true)]
        private static bool ValidateCustom()
        {
            // Custom is checked iff the current env isn't one of the three
            // presets AND an asset exists with a non-empty value.
            var settings = PlayloopAssetBootstrap.TryLoadSettings();
            var env = settings != null ? ReadEnvironment(settings) : null;
            var isPreset = env == "production" || env == "demo" || env == "dev";
            Menu.SetChecked(MenuSetEnvCustom, settings != null && !string.IsNullOrWhiteSpace(env) && !isPreset);
            return true;
        }

        private static bool UpdateEnvCheckedState(string env, string menuPath)
        {
            var settings = PlayloopAssetBootstrap.TryLoadSettings();
            Menu.SetChecked(menuPath, settings != null && ReadEnvironment(settings) == env);
            return true;
        }

        private static void SetEnv(string env)
        {
            var settings = EnsureSettingsExistsOrPrompt();
            if (settings == null) return;
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("environment");
            if (prop == null)
            {
                Debug.LogError(
                    "[Playloop] PlayloopSettings has no serialized 'environment' field. " +
                    "Did the field name change? Update PlayloopMenu.SetEnv accordingly.");
                return;
            }
            Undo.RecordObject(settings, $"Set Playloop env to {env}");
            prop.stringValue = env;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Playloop] Active env → {env}");
        }

        // ─────────────────── Asset bootstrap ───────────────────

        [MenuItem(MenuCreateAsset, priority = 301)]
        public static void CreateDefaultSettingsAsset() =>
            PlayloopAssetBootstrap.CreateDefaultSettingsAsset();

        [MenuItem(MenuCreateAsset, validate = true)]
        private static bool ValidateCreateDefaultSettingsAsset() =>
            PlayloopAssetBootstrap.TryLoadSettings() == null;

        [MenuItem(MenuRevealAsset, priority = 302)]
        public static void RevealSettingsAsset() =>
            PlayloopAssetBootstrap.RevealSettingsAsset();

        [MenuItem(MenuRevealAsset, validate = true)]
        private static bool ValidateRevealSettingsAsset() =>
            PlayloopAssetBootstrap.TryLoadSettings() != null;

        // ─────────────────── Quick links ───────────────────

        [MenuItem(MenuOpenDocs, priority = 401)]
        public static void OpenDocs() => Application.OpenURL(PlayloopEditorPaths.DocsUrl);

        [MenuItem(MenuOpenRoadmap, priority = 402)]
        public static void OpenRoadmap() => Application.OpenURL(PlayloopEditorPaths.RoadmapUrl);

        [MenuItem(MenuOpenDashboard, priority = 403)]
        public static void OpenDashboardForThisGame()
        {
            var settings = PlayloopAssetBootstrap.TryLoadSettings();
            if (settings == null)
            {
                if (EditorUtility.DisplayDialog(
                        "Playloop",
                        "No PlayloopSettings asset found. Create one first?",
                        "Create", "Cancel"))
                {
                    settings = PlayloopAssetBootstrap.CreateDefaultSettingsAsset();
                }
                if (settings == null) return;
            }
            var baseUrl = ReadString(settings, "baseUrl") ?? "https://playloop.gg";
            var slug = ReadString(settings, "gameSlug") ?? "";
            if (string.IsNullOrWhiteSpace(slug))
            {
                // The runtime asset doesn't have a gameSlug field today
                // (it lives on PlayloopOptions for now). Fall back to the
                // dashboard origin so the dev at least lands somewhere
                // useful instead of a broken /games//dashboard URL.
                slug = ReadString(settings, "GameSlug") ?? "";
            }
            var env = ReadEnvironment(settings) ?? "production";
            var url = string.IsNullOrWhiteSpace(slug)
                ? PlayloopEditorPaths.ResolveDashboardOrigin(baseUrl)
                : PlayloopEditorPaths.DashboardUrl(baseUrl, slug, env);
            Application.OpenURL(url);
        }

        [MenuItem(MenuOpenRepo, priority = 404)]
        public static void OpenRepo() => Application.OpenURL(PlayloopEditorPaths.RepoUrl);

        // ─────────────────── Helpers ───────────────────

        // The runtime SDK's PlayloopSettings exposes its inspector fields
        // as private [SerializeField] members, so the only safe way to
        // read/write them from editor code is through SerializedObject.
        // These helpers wrap that boilerplate.

        internal static string? ReadEnvironment(PlayloopSettings settings)
            => ReadString(settings, "environment");

        internal static string? ReadString(PlayloopSettings settings, string fieldName)
        {
            var so = new SerializedObject(settings);
            var prop = so.FindProperty(fieldName);
            return prop != null && prop.propertyType == SerializedPropertyType.String
                ? prop.stringValue
                : null;
        }

        private static string ResolveBaseUrlFromSettings()
        {
            var settings = PlayloopAssetBootstrap.TryLoadSettings();
            if (settings == null) return "https://playloop.gg";
            return ReadString(settings, "baseUrl") ?? "https://playloop.gg";
        }

        private static PlayloopSettings? EnsureSettingsExistsOrPrompt()
        {
            var existing = PlayloopAssetBootstrap.TryLoadSettings();
            if (existing != null) return existing;
            if (EditorUtility.DisplayDialog(
                    "Playloop",
                    $"No PlayloopSettings asset found at {PlayloopEditorPaths.SettingsAssetPath}.\n\n" +
                    "Create the default asset now?",
                    "Create", "Cancel"))
            {
                return PlayloopAssetBootstrap.CreateDefaultSettingsAsset();
            }
            return null;
        }

        // ─────────────────── Inline custom-env prompt ───────────────────

        /// <summary>
        /// Lightweight modal-style EditorWindow used by the
        /// "Set Environment/Custom…" command. EditorUtility doesn't ship a
        /// stock text-input modal, so this is a 5-line popup with one
        /// text field + OK/Cancel. Closes on Enter or button click.
        /// </summary>
        private sealed class CustomEnvironmentDialog : EditorWindow
        {
            private string _value = "";
            private Action<string>? _onSubmit;

            public static void Show(string prefill, Action<string> onSubmit)
            {
                var win = CreateInstance<CustomEnvironmentDialog>();
                win.titleContent = new GUIContent("Set Custom Environment");
                win._value = prefill ?? "";
                win._onSubmit = onSubmit;
                win.minSize = new Vector2(360, 110);
                win.maxSize = new Vector2(420, 110);
                win.ShowUtility();
                win.Focus();
            }

            private void OnGUI()
            {
                EditorGUILayout.LabelField(
                    "Environment slug (lowercase, no spaces).",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);

                GUI.SetNextControlName("PlayloopCustomEnvField");
                _value = EditorGUILayout.TextField("Env", _value);
                EditorGUI.FocusTextInControl("PlayloopCustomEnvField");

                EditorGUILayout.Space(8);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                    {
                        Close();
                    }
                    GUI.enabled = !string.IsNullOrWhiteSpace(_value);
                    if (GUILayout.Button("Save", GUILayout.Width(80)))
                    {
                        Submit();
                    }
                    GUI.enabled = true;
                }

                // Enter key shortcut.
                var e = Event.current;
                if (e != null && e.type == EventType.KeyDown &&
                    (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) &&
                    !string.IsNullOrWhiteSpace(_value))
                {
                    Submit();
                    e.Use();
                }
            }

            private void Submit()
            {
                var cb = _onSubmit;
                var v = _value?.Trim() ?? "";
                Close();
                cb?.Invoke(v);
            }
        }
    }
}
#endif
