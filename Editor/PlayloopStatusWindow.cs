#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Playloop.Editor
{
    /// <summary>
    /// First-stop window for the Playloop SDK (<b>Playloop > Settings…</b>).
    /// Surfaces connection status at a glance, edits the canonical
    /// <see cref="PlayloopSettings"/> asset, and offers one-click Verify
    /// Connection + Send Test Event.
    ///
    /// <para>
    /// Built with <b>UI Toolkit</b> (not IMGUI) so it carries the Playloop
    /// brand palette + wordmark lockup, matching the Godot SDK's settings
    /// window and the marketing site. All sizes are in logical px; UI Toolkit
    /// handles editor DPI scaling, so no manual EDSCALE math is needed.
    /// </para>
    /// </summary>
    public sealed class PlayloopStatusWindow : EditorWindow
    {
        // ─────────────────── Persisted state ───────────────────
        private const string PrefVerifyResult = "Playloop.Status.LastVerifyResult";
        private const string PrefVerifyAtTicks = "Playloop.Status.LastVerifyAtTicks";
        private const string PrefVerifyCacheKey = "Playloop.Status.LastVerifyCacheKey";

        // Resolved game name from the last successful Verify, surfaced in the
        // pill ("Connected to <name>"). Empty until a verify resolves the key.
        private string _resolvedGameName = "";

        // ─────────────────── Brand palette ───────────────────
        // Mirrors the Godot SDK status window (status_window.gd) so the two
        // editor surfaces read identically.
        private static Color Rgb(int r, int g, int b, float a = 1f)
            => new Color(r / 255f, g / 255f, b / 255f, a);

        private static readonly Color WindowBg = Rgb(18, 20, 24);
        private static readonly Color CardBg = Rgb(24, 26, 31);
        private static readonly Color InputBg = Rgb(33, 35, 41);
        private static readonly Color BorderCol = Rgb(51, 54, 61);
        private static readonly Color Accent = Rgb(102, 158, 255);
        private static readonly Color AccentHover = Rgb(122, 176, 255);
        private static readonly Color SecondaryBg = Rgb(41, 43, 51);
        private static readonly Color TextCol = Rgb(217, 217, 224);
        private static readonly Color MutedCol = Rgb(140, 145, 158);
        private const float LabelW = 116f;

        // ─────────────────── Status ───────────────────
        private enum Status
        {
            Unconfigured, Verifying, Connected, InvalidKey, NetworkError,
        }

        private Status _status = Status.Unconfigured;
        private DateTime? _lastVerifiedAt;
        private string _statusDetail = "";
        private bool _verifying;

        // ─────────────────── Element refs (dynamic) ───────────────────
        private VisualElement? _pillBox;
        private Label? _pillLabel;
        private Label? _pillSub;
        private Label? _detailLabel;
        private Button? _verifyBtn;
        private Button? _sendTestBtn;

        // ─────────────────── Open ───────────────────
        public static void Open()
        {
            var win = GetWindow<PlayloopStatusWindow>(title: "Playloop");
            win.minSize = new Vector2(520, 600);
            win.Show();
        }

        private void OnEnable()
        {
            RestoreCachedVerifyState();
        }

        public void CreateGUI() => Rebuild();

        // ─────────────────── Build ───────────────────

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.backgroundColor = WindowBg;
            root.style.paddingLeft = 18;
            root.style.paddingRight = 18;
            root.style.paddingTop = 14;
            root.style.paddingBottom = 14;
            root.style.flexGrow = 1;

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            var settings = PlayloopAssetBootstrap.TryLoadSettings();

            BuildHeader(scroll.contentContainer, settings);

            if (settings == null)
            {
                BuildMissingAsset(scroll.contentContainer);
                RefreshPill(null);
                return;
            }

            var so = new SerializedObject(settings);
            BuildConnection(scroll.contentContainer, settings, so);
            BuildAutoInstrument(scroll.contentContainer, so);
            BuildTrace(scroll.contentContainer, so);
            BuildQuickLinks(scroll.contentContainer, settings);

            // Tick the "Last verified 14s ago" label + verifying spinner state
            // once a second without user input.
            root.schedule.Execute(() => RefreshPill(settings)).Every(1000);

            RefreshPill(settings);
        }

        // ─────────────────── Header (wordmark + pill) ───────────────────

        private void BuildHeader(VisualElement parent, PlayloopSettings? settings)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 12;
            parent.Add(row);

            // Brand wordmark lockup (matches the site + Godot SDK). Falls back
            // to a text label if the embedded PNG can't decode.
            var wm = PlayloopBrandingWordmark.Texture();
            if (wm != null)
            {
                var img = new Image { image = wm, scaleMode = ScaleMode.ScaleToFit };
                img.style.height = 24;
                img.style.width = 110;
                img.style.flexShrink = 0;
                row.Add(img);
            }
            else
            {
                var word = new Label("Playloop");
                word.style.fontSize = 18;
                word.style.color = Color.white;
                word.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(word);
            }

            var spacer = new VisualElement { style = { flexGrow = 1 } };
            row.Add(spacer);

            // Status pill.
            _pillBox = new VisualElement();
            _pillBox.style.flexDirection = FlexDirection.Row;
            _pillBox.style.alignItems = Align.Center;
            Round(_pillBox, 12);
            _pillBox.style.paddingLeft = 12;
            _pillBox.style.paddingRight = 12;
            _pillBox.style.paddingTop = 6;
            _pillBox.style.paddingBottom = 6;
            _pillLabel = new Label("Not configured") { style = { fontSize = 13 } };
            _pillBox.Add(_pillLabel);
            row.Add(_pillBox);

            // Subtitle + detail under the row.
            _pillSub = MutedLabel("");
            _pillSub.style.marginBottom = 2;
            parent.Add(_pillSub);
            _detailLabel = MutedLabel("");
            _detailLabel.style.whiteSpace = WhiteSpace.Normal;
            _detailLabel.style.marginBottom = 8;
            parent.Add(_detailLabel);

            // Action row.
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginBottom = 12;
            parent.Add(actions);

            _verifyBtn = PrimaryButton("Verify Connection", () =>
            {
                var s = PlayloopAssetBootstrap.TryLoadSettings();
                if (s != null) _ = VerifyAsync(s);
            });
            actions.Add(_verifyBtn);

            _sendTestBtn = SecondaryButton("Send Test Event", () => PlayloopMenu.SendTestEvent());
            _sendTestBtn.style.marginLeft = 8;
            actions.Add(_sendTestBtn);
        }

        // ─────────────────── Missing-asset state ───────────────────

        private void BuildMissingAsset(VisualElement parent)
        {
            var card = Card();
            parent.Add(card);

            var title = new Label("PlayloopSettings.asset is missing");
            title.style.color = TextCol;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13;
            card.Add(title);

            var body = MutedLabel(
                "The SDK looks for a settings asset at " +
                $"{PlayloopEditorPaths.SettingsAssetPath}. Create one to begin. " +
                "you'll be able to paste your ingest key and pick a base URL next.");
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.marginTop = 4;
            body.style.marginBottom = 8;
            card.Add(body);

            card.Add(PrimaryButton("Create Default Settings Asset", () =>
            {
                PlayloopAssetBootstrap.CreateDefaultSettingsAsset();
                Rebuild();
            }));
        }

        // ─────────────────── Connection section ───────────────────

        private void BuildConnection(VisualElement parent, PlayloopSettings settings, SerializedObject so)
        {
            SectionHeading(parent, "Connection");
            var card = Card();
            parent.Add(card);

            BuildApiKeyRow(card, so);
            BuildTextRow(card, so, "baseUrl", "Base URL");
            BuildEnvironmentRow(card, so);
            BuildHeartbeatRow(card);
        }

        private void BuildApiKeyRow(VisualElement parent, SerializedObject so)
        {
            var prop = so.FindProperty("apiKey");
            if (prop == null) return;

            var row = FieldRow(parent, "Ingest key");

            var hasKey = !string.IsNullOrEmpty(prop.stringValue);
            if (hasKey)
            {
                var masked = new Label(MaskKey(prop.stringValue));
                masked.style.color = TextCol;
                masked.style.flexGrow = 1;
                masked.style.flexShrink = 1;
                masked.style.overflow = Overflow.Hidden;
                masked.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.Add(masked);
            }
            else
            {
                var field = new TextField { isPasswordField = true, value = prop.stringValue ?? "" };
                StyleInput(field);
                field.style.flexGrow = 1;

                // In-field placeholder hint (UI Toolkit has no native
                // placeholder before Unity 2023.2; the SDK targets 2021.3, so
                // overlay a muted label that hides once the field has a value).
                var hint = new Label("pl_ik_...");
                hint.pickingMode = PickingMode.Ignore;
                hint.style.position = Position.Absolute;
                hint.style.left = 8;
                hint.style.top = 0;
                hint.style.bottom = 0;
                hint.style.unityTextAlign = TextAnchor.MiddleLeft;
                hint.style.color = MutedCol;
                hint.style.fontSize = 13;
                field.Add(hint);
                hint.style.display = string.IsNullOrEmpty(field.value) ? DisplayStyle.Flex : DisplayStyle.None;

                field.RegisterValueChangedCallback(evt =>
                {
                    prop.stringValue = evt.newValue;
                    so.ApplyModifiedProperties();
                    InvalidateVerifyCache();
                    hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                });
                row.Add(field);
            }

            var paste = SecondaryButton("Paste", () =>
            {
                var clip = EditorGUIUtility.systemCopyBuffer ?? "";
                if (!string.IsNullOrWhiteSpace(clip))
                {
                    prop.stringValue = clip.Trim();
                    so.ApplyModifiedProperties();
                    InvalidateVerifyCache();
                    Rebuild();
                }
            });
            paste.style.marginLeft = 6;
            paste.style.width = 60;
            paste.style.flexShrink = 0;
            row.Add(paste);

            if (hasKey)
            {
                var clear = SecondaryButton("Clear", () =>
                {
                    prop.stringValue = "";
                    so.ApplyModifiedProperties();
                    InvalidateVerifyCache();
                    Rebuild();
                });
                clear.style.marginLeft = 6;
                clear.style.width = 60;
                clear.style.flexShrink = 0;
                row.Add(clear);
            }
        }

        private static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(not set)";
            if (key.Length <= 8) return new string('•', key.Length);
            // Fixed-width mask (prefix + 6 bullets + tail) regardless of key
            // length. A per-char mask of a long key blows the row past the
            // window width and pushes the Paste/Clear buttons off-screen.
            var prefixEnd = Math.Min(7, key.Length);
            var prefix = key.Substring(0, prefixEnd);
            var tail = key.Substring(Math.Max(0, key.Length - 4));
            return $"{prefix}{new string('•', 6)}{tail}";
        }

        private void BuildTextRow(VisualElement parent, SerializedObject so, string fieldName, string label)
        {
            var prop = so.FindProperty(fieldName);
            if (prop == null) return;
            var row = FieldRow(parent, label);
            var field = new TextField { value = prop.stringValue ?? "" };
            StyleInput(field);
            field.style.flexGrow = 1;
            field.RegisterValueChangedCallback(evt =>
            {
                prop.stringValue = evt.newValue;
                so.ApplyModifiedProperties();
                InvalidateVerifyCache();
            });
            row.Add(field);
        }

        private void BuildEnvironmentRow(VisualElement parent, SerializedObject so)
        {
            var prop = so.FindProperty("environment");
            if (prop == null) return;
            var current = prop.stringValue ?? "production";

            var row = FieldRow(parent, "Environment");
            var presets = new List<string> { "production", "demo", "dev", "custom…" };
            var isPreset = current == "production" || current == "demo" || current == "dev";
            var initial = isPreset ? current : "custom…";

            var dropdown = new DropdownField(presets, Math.Max(0, presets.IndexOf(initial)));
            StyleDropdown(dropdown);
            dropdown.style.flexGrow = 1;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == "custom…")
                {
                    var prefill = isPreset ? "" : current;
                    PlayloopMenuCustomEnv.Prompt(prefill, slug =>
                    {
                        if (!string.IsNullOrWhiteSpace(slug))
                        {
                            prop.stringValue = slug.Trim();
                            so.ApplyModifiedProperties();
                            InvalidateVerifyCache();
                            Rebuild();
                        }
                    });
                }
                else
                {
                    prop.stringValue = evt.newValue;
                    so.ApplyModifiedProperties();
                    InvalidateVerifyCache();
                }
            });
            row.Add(dropdown);

            if (!isPreset && !string.IsNullOrWhiteSpace(current))
            {
                var customHint = MutedLabel($"custom: {current}");
                customHint.style.marginLeft = 8;
                row.Add(customHint);
            }
        }

        private void BuildHeartbeatRow(VisualElement parent)
        {
            var row = FieldRow(parent, "Heartbeat");
            var hint = MutedLabel("60s (set via PlayloopOptions.HeartbeatSec; min 30s)");
            hint.style.flexGrow = 1;
            hint.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(hint);
        }

        // ─────────────────── Auto-instrument ───────────────────

        private void BuildAutoInstrument(VisualElement parent, SerializedObject so)
        {
            var block = so.FindProperty("autoInstrument");
            if (block == null) return;

            SectionHeading(parent, "Auto-instrument");
            var card = Card();
            parent.Add(card);

            var togglesRow = new VisualElement();
            togglesRow.style.flexDirection = FlexDirection.Row;
            togglesRow.style.flexWrap = Wrap.Wrap;
            card.Add(togglesRow);

            AddToggle(togglesRow, block, so, "scenes", "Scene changes");
            AddToggle(togglesRow, block, so, "errors", "Errors");
            AddToggle(togglesRow, block, so, "idle", "Idle");
            AddToggle(togglesRow, block, so, "fps", "FPS");
            AddToggle(togglesRow, block, so, "memory", "Memory");

            var note = MutedLabel(
                "Each event has an independent kill-switch. Memory pressure is off by default. It's noisy on low-memory devices.");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginTop = 6;
            card.Add(note);
        }

        // ─────────────────── Trace ───────────────────

        private void BuildTrace(VisualElement parent, SerializedObject so)
        {
            var block = so.FindProperty("trace");
            if (block == null) return;

            SectionHeading(parent, "Trace");
            var card = Card();
            parent.Add(card);

            AddEnumDropdown(card, block, so, "mode", "Mode", new List<string> { "Auto", "On", "Off" });
            AddEnumDropdown(card, block, so, "plane", "Plane", new List<string> { "XY", "XZ" });

            var hz = block.FindPropertyRelative("hz");
            var ents = block.FindPropertyRelative("maxEntities");
            var budget = block.FindPropertyRelative("maxBytesPerSession");
            string budgetMb = budget != null ? (budget.intValue / (1024f * 1024f)).ToString("0.#") : "2";
            var values = MutedLabel(
                $"{(hz != null ? hz.intValue : 10)} Hz, up to {(ents != null ? ents.intValue : 8)} named entities, {budgetMb} MB per session. Edit these on the settings asset.");
            values.style.whiteSpace = WhiteSpace.Normal;
            values.style.marginTop = 2;
            card.Add(values);

            var note = MutedLabel(
                "Auto samples everywhere except a production environment. The Trace carries player position and facing, the room id, action bits and movement axes. Never keys, text, screen, audio or camera.");
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginTop = 6;
            card.Add(note);
        }

        private void AddEnumDropdown(VisualElement parent, SerializedProperty block, SerializedObject so, string child, string label, List<string> choices)
        {
            var prop = block.FindPropertyRelative(child);
            if (prop == null) return;

            var row = FieldRow(parent, label);
            int initial = Math.Max(0, Math.Min(choices.Count - 1, prop.enumValueIndex));
            var dropdown = new DropdownField(choices, initial);
            StyleDropdown(dropdown);
            dropdown.style.flexGrow = 1;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                int idx = choices.IndexOf(evt.newValue);
                if (idx < 0) return;
                prop.enumValueIndex = idx;
                so.ApplyModifiedProperties();
            });
            row.Add(dropdown);
        }

        private void AddToggle(VisualElement parent, SerializedProperty block, SerializedObject so, string child, string label)
        {
            var prop = block.FindPropertyRelative(child);
            if (prop == null) return;

            // A fixed-width [checkbox][label] cell. Using Toggle's own
            // BaseField label puts the text in a left label column and floats
            // the checkbox to the far right of the cell; hide that label and
            // place our own right next to the checkbox so they read as a pair.
            // Auto-width cell (sized to its checkbox + label) with a fixed gap
            // to the next one. A fixed cell width left big gaps after short
            // labels (Idle/FPS) and overflowed, wrapping Memory to a new line;
            // content-sized cells pack tightly so all five fit on one row.
            var cell = new VisualElement();
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.flexShrink = 0;
            cell.style.marginRight = 18;
            cell.style.marginBottom = 4;

            var toggle = new Toggle { value = prop.boolValue };
            if (toggle.labelElement != null) toggle.labelElement.style.display = DisplayStyle.None;
            toggle.style.marginRight = 4;
            toggle.style.flexShrink = 0;
            toggle.RegisterValueChangedCallback(evt =>
            {
                prop.boolValue = evt.newValue;
                so.ApplyModifiedProperties();
            });

            var lbl = new Label(label);
            lbl.style.color = TextCol;
            lbl.style.fontSize = 13;

            cell.Add(toggle);
            cell.Add(lbl);
            parent.Add(cell);
        }

        // ─────────────────── Quick links ───────────────────

        private void BuildQuickLinks(VisualElement parent, PlayloopSettings settings)
        {
            SectionHeading(parent, "Quick links");
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            parent.Add(row);

            row.Add(LinkButton("Open Dashboard ↗", () => PlayloopMenu.OpenDashboardForThisGame()));
            row.Add(LinkButton("Docs", () => Application.OpenURL(PlayloopEditorPaths.DocsUrl)));
            row.Add(LinkButton("GitHub", () => Application.OpenURL(PlayloopEditorPaths.RepoUrl)));
        }

        // ─────────────────── Pill refresh ───────────────────

        private void RefreshPill(PlayloopSettings? settings)
        {
            if (_pillBox == null || _pillLabel == null) return;
            var resolved = ResolveDisplayStatus(settings);
            var (glyph, label, color, bg) = PillStyle(resolved);

            // On a successful resolve, name the game in the pill so the dev
            // can confirm the key points at the game they expect.
            if (resolved == Status.Connected && !string.IsNullOrWhiteSpace(_resolvedGameName))
                label = $"Connected to {_resolvedGameName}";

            _pillLabel.text = $"{glyph} {label}";
            _pillLabel.style.color = color;
            _pillBox.style.backgroundColor = bg;

            if (_pillSub != null) _pillSub.text = BuildPillSubtitle(resolved);
            if (_detailLabel != null) _detailLabel.text = _statusDetail ?? "";

            var apiKey = settings != null ? (PlayloopMenu.ReadString(settings, "apiKey") ?? "") : "";
            var canVerify = settings != null && !string.IsNullOrWhiteSpace(apiKey) && !_verifying;
            if (_verifyBtn != null) _verifyBtn.SetEnabled(canVerify);
            if (_sendTestBtn != null)
                _sendTestBtn.SetEnabled(Application.isPlaying && PlayloopClient.Current != null);
        }

        private Status ResolveDisplayStatus(PlayloopSettings? settings)
        {
            if (_verifying) return Status.Verifying;
            if (settings == null) return Status.Unconfigured;
            var apiKey = PlayloopMenu.ReadString(settings, "apiKey");
            if (string.IsNullOrWhiteSpace(apiKey)) return Status.Unconfigured;
            return _status;
        }

        private string BuildPillSubtitle(Status status)
        {
            if (status == Status.Connected && _lastVerifiedAt != null)
                return $"Last verified {RelativeTime(_lastVerifiedAt.Value)}";
            if (status == Status.Unconfigured) return "Configure to begin.";
            if (status == Status.Verifying) return "Reaching out to playloop.gg…";
            return string.Empty;
        }

        private static (string glyph, string label, Color color, Color bg) PillStyle(Status status) => status switch
        {
            Status.Unconfigured => ("⏳", "Not configured", Rgb(204, 204, 204), Rgb(64, 64, 69)),
            Status.Verifying => ("🌀", "Verifying…", Rgb(204, 204, 204), Rgb(64, 77, 102)),
            Status.Connected => ("✅", "Connected", Rgb(102, 255, 128), Rgb(26, 64, 38)),
            Status.InvalidKey => ("❌", "Invalid key", Rgb(255, 140, 140), Rgb(77, 26, 26)),
            Status.NetworkError => ("⚠", "Network error", Rgb(255, 217, 102), Rgb(77, 56, 13)),
            _ => ("●", "Unknown", Color.gray, Rgb(64, 64, 69)),
        };

        // ─────────────────── Verify ───────────────────

        private async Task VerifyAsync(PlayloopSettings settings)
        {
            var apiKey = PlayloopMenu.ReadString(settings, "apiKey") ?? "";
            var baseUrl = PlayloopMenu.ReadString(settings, "baseUrl") ?? "https://playloop.gg";

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _status = Status.Unconfigured;
                _statusDetail = "Add an API key first.";
                RefreshPill(settings);
                return;
            }

            _verifying = true;
            _statusDetail = "";
            RefreshPill(settings);

            try
            {
                // The ingest key alone identifies the game, so Verify resolves
                // it via GET /api/telemetry/resolve (no slug needed) and reports
                // the resolved game name.
                var url = $"{baseUrl.TrimEnd('/')}/api/telemetry/resolve";
                using var req = UnityWebRequest.Get(url);
                req.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                req.SetRequestHeader("Accept", "application/json");
                await SendAsync(req);

                if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
                {
                    _status = Status.Connected;
                    _lastVerifiedAt = DateTime.UtcNow;
                    _resolvedGameName = ParseResolvedName(req.downloadHandler?.text);
                    _statusDetail = string.IsNullOrWhiteSpace(_resolvedGameName)
                        ? "Key resolves to a game."
                        : $"Key resolves to '{_resolvedGameName}'.";
                    PersistVerifyCache(apiKey, _status);
                }
                else
                {
                    _resolvedGameName = "";
                    switch (req.responseCode)
                    {
                        case 401:
                            _status = Status.InvalidKey;
                            _statusDetail =
                                "Key is either expired or doesn't have ingest scope. " +
                                "Get the game's ingest key from its Connections → Unity page on playloop.gg.";
                            break;
                        case 429:
                            _status = Status.NetworkError;
                            _statusDetail = "Rate limited. Wait a moment and try again.";
                            break;
                        default:
                            _status = Status.NetworkError;
                            _statusDetail = $"HTTP {req.responseCode}: {req.error ?? "unknown"}";
                            break;
                    }
                    PersistVerifyCache(apiKey, _status);
                }
            }
            catch (Exception e)
            {
                _status = Status.NetworkError;
                _statusDetail = $"Verify threw: {e.GetType().Name}: {e.Message}";
            }
            finally
            {
                _verifying = false;
                RefreshPill(settings);
            }
        }

        /// <summary>
        /// Pull the <c>name</c> field out of the resolve response body. Best-
        /// effort: returns "" on any parse failure or a missing field.
        /// </summary>
        private static string ParseResolvedName(string? body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            try
            {
                var obj = Newtonsoft.Json.Linq.JObject.Parse(body);
                return obj["name"]?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static Task SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        // ─────────────────── Verify-cache persistence ───────────────────

        private static string BuildVerifyCacheKey(string apiKey)
        {
            return apiKey.Length <= 8 ? apiKey : apiKey.Substring(0, 8);
        }

        private void PersistVerifyCache(string apiKey, Status status)
        {
            EditorPrefs.SetString(PrefVerifyResult, status.ToString());
            EditorPrefs.SetString(PrefVerifyCacheKey, BuildVerifyCacheKey(apiKey));
            EditorPrefs.SetString(PrefVerifyAtTicks, DateTime.UtcNow.Ticks.ToString());
        }

        private void RestoreCachedVerifyState()
        {
            var settings = PlayloopAssetBootstrap.TryLoadSettings();
            if (settings == null) return;
            var apiKey = PlayloopMenu.ReadString(settings, "apiKey") ?? "";
            if (string.IsNullOrWhiteSpace(apiKey)) return;

            var savedKey = EditorPrefs.GetString(PrefVerifyCacheKey, "");
            if (savedKey != BuildVerifyCacheKey(apiKey)) return;

            var resultStr = EditorPrefs.GetString(PrefVerifyResult, "");
            if (Enum.TryParse<Status>(resultStr, out var s)) _status = s;
            var ticksStr = EditorPrefs.GetString(PrefVerifyAtTicks, "");
            if (long.TryParse(ticksStr, out var ticks))
                _lastVerifiedAt = new DateTime(ticks, DateTimeKind.Utc);
        }

        private void InvalidateVerifyCache()
        {
            _status = Status.Unconfigured;
            _lastVerifiedAt = null;
            _statusDetail = "";
            _resolvedGameName = "";
            RefreshPill(PlayloopAssetBootstrap.TryLoadSettings());
        }

        private static string RelativeTime(DateTime utc)
        {
            var delta = DateTime.UtcNow - utc;
            if (delta < TimeSpan.FromSeconds(5)) return "just now";
            if (delta < TimeSpan.FromMinutes(1)) return $"{(int)delta.TotalSeconds}s ago";
            if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes}m ago";
            if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours}h ago";
            return $"{(int)delta.TotalDays}d ago";
        }

        // ─────────────────── Styled-element helpers ───────────────────

        private static void Round(VisualElement e, int r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        private static void Border(VisualElement e, Color c, float w = 1)
        {
            e.style.borderTopWidth = w;
            e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w;
            e.style.borderRightWidth = w;
            e.style.borderTopColor = c;
            e.style.borderBottomColor = c;
            e.style.borderLeftColor = c;
            e.style.borderRightColor = c;
        }

        private static VisualElement Card()
        {
            var c = new VisualElement();
            c.style.backgroundColor = CardBg;
            Round(c, 6);
            c.style.paddingLeft = 12;
            c.style.paddingRight = 12;
            c.style.paddingTop = 10;
            c.style.paddingBottom = 10;
            c.style.marginBottom = 8;
            return c;
        }

        private static void SectionHeading(VisualElement parent, string text)
        {
            var h = new Label(text.ToUpperInvariant());
            h.style.color = MutedCol;
            h.style.fontSize = 11;
            h.style.marginTop = 8;
            h.style.marginBottom = 4;
            parent.Add(h);
        }

        private static VisualElement FieldRow(VisualElement parent, string labelText)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;
            var label = new Label(labelText);
            label.style.color = TextCol;
            label.style.fontSize = 13;
            label.style.width = LabelW;
            label.style.flexShrink = 0;
            row.Add(label);
            parent.Add(row);
            return row;
        }

        private static Label MutedLabel(string text)
        {
            var l = new Label(text);
            l.style.color = MutedCol;
            l.style.fontSize = 12;
            return l;
        }

        private static void StyleInput(TextField field)
        {
            var input = field.Q(className: "unity-base-text-field__input") ?? field;
            input.style.backgroundColor = InputBg;
            Round(input, 6);
            Border(input, BorderCol);
            input.style.color = TextCol;
            field.style.height = 26;
        }

        private static void StyleDropdown(DropdownField field)
        {
            field.style.height = 26;
            field.style.color = TextCol;
            var input = field.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.backgroundColor = InputBg;
                Round(input, 6);
                Border(input, BorderCol);
            }
        }

        private static Button PrimaryButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.backgroundColor = Accent;
            b.style.color = Color.white;
            Round(b, 6);
            b.style.height = 26;
            b.style.paddingLeft = 12;
            b.style.paddingRight = 12;
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            b.RegisterCallback<MouseEnterEvent>(_ => b.style.backgroundColor = AccentHover);
            b.RegisterCallback<MouseLeaveEvent>(_ => b.style.backgroundColor = Accent);
            return b;
        }

        private static Button SecondaryButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.backgroundColor = SecondaryBg;
            b.style.color = TextCol;
            Round(b, 6);
            Border(b, BorderCol);
            b.style.height = 26;
            b.style.paddingLeft = 10;
            b.style.paddingRight = 10;
            return b;
        }

        private static Button LinkButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.backgroundColor = Color.clear;
            b.style.color = MutedCol;
            b.style.borderTopWidth = 0;
            b.style.borderBottomWidth = 0;
            b.style.borderLeftWidth = 0;
            b.style.borderRightWidth = 0;
            b.style.fontSize = 12;
            b.style.marginRight = 12;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.RegisterCallback<MouseEnterEvent>(_ => b.style.color = Accent);
            b.RegisterCallback<MouseLeaveEvent>(_ => b.style.color = MutedCol);
            return b;
        }
    }

    /// <summary>
    /// Inline custom-environment dialog, reused by the settings window's
    /// "custom…" environment choice. (IMGUI utility window, small + self-
    /// contained; no need to port to UI Toolkit.)
    /// </summary>
    internal static class PlayloopMenuCustomEnv
    {
        public static void Prompt(string prefill, Action<string> onSubmit)
        {
            var win = ScriptableObject.CreateInstance<CustomEnvironmentDialog>();
            win.titleContent = new GUIContent("Set Custom Environment");
            win.SetPrefill(prefill, onSubmit);
            win.minSize = new Vector2(360, 110);
            win.maxSize = new Vector2(420, 110);
            win.ShowUtility();
            win.Focus();
        }

        private sealed class CustomEnvironmentDialog : EditorWindow
        {
            private string _value = "";
            private Action<string>? _onSubmit;

            public void SetPrefill(string prefill, Action<string> onSubmit)
            {
                _value = prefill ?? "";
                _onSubmit = onSubmit;
            }

            private void OnGUI()
            {
                EditorGUILayout.LabelField(
                    "Environment slug (lowercase, no spaces).",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);
                GUI.SetNextControlName("PlayloopStatusCustomEnvField");
                _value = EditorGUILayout.TextField("Env", _value);
                EditorGUI.FocusTextInControl("PlayloopStatusCustomEnvField");
                EditorGUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
                    GUI.enabled = !string.IsNullOrWhiteSpace(_value);
                    if (GUILayout.Button("Save", GUILayout.Width(80))) Submit();
                    GUI.enabled = true;
                }
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
