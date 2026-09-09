#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Playloop.Editor
{
    /// <summary>
    /// Unity Editor window for symbolicating crash stacks captured by
    /// the SDK's crash trap. Source-map symbolication for browser /
    /// Node stacks runs server-side automatically; Unity stacks need a
    /// dev-side pass because the symbol files live on the dev's
    /// machine (Library/Il2cppOutputProject for IL2CPP) and aren't
    /// shippable to the server economically.
    ///
    /// The flow:
    ///   1. Set the management key + game slug + base URL (stored in
    ///      EditorPrefs, same slug/base-url prefs the Status window uses).
    ///   2. Click <b>Fetch unresolved crashes</b>: pulls every crash
    ///      with status in (pending, no_match, unsupported) for this
    ///      game.
    ///   3. Per crash: paste a resolved stack into the editable box.
    ///      The dev gets the resolved stack from Unity's own symbol
    ///      tools. See the "How to resolve a stack" foldout for
    ///      copy-paste commands per platform. Future versions can
    ///      auto-run ndk-stack / atos / Unity's symbol-map tool by
    ///      shelling out to the dev's installed toolchain.
    ///   4. Click <b>Submit</b>: POSTs the resolved stack back to
    ///      <c>/api/v1/games/&lt;slug&gt;/crashes/&lt;id&gt;/resolve</c>.
    ///      The server stamps status=symbolicated + recomputes the
    ///      group signature so previously-distinct stacks collapse.
    ///
    /// Open via: <b>Playloop > Symbolicate Crashes…</b>.
    /// </summary>
    public sealed class PlayloopSymbolicationWindow : EditorWindow
    {
        private const string PrefBaseUrl = "Playloop.Editor.BaseUrl";
        private const string PrefGameSlug = "Playloop.Editor.GameSlug";
        private const string PrefMgmtKey = "Playloop.Editor.ManagementKey";
        private const string PrefPlatformFilter = "Playloop.Editor.SymbolicateWindow.Platform";

        private string _baseUrl = "https://playloop.gg";
        private string _gameSlug = "";
        private string _managementKey = "";
        private string _platformFilter = ""; // empty = all
        private bool _showSettings = true;
        private bool _showHowTo = false;

        private List<UnresolvedCrash> _crashes = new List<UnresolvedCrash>();
        private readonly Dictionary<string, string> _editsById = new Dictionary<string, string>();
        private readonly HashSet<string> _expandedIds = new HashSet<string>();
        private readonly HashSet<string> _busyIds = new HashSet<string>();
        private Vector2 _scroll;
        private string _status = "";
        private bool _busy;

        [MenuItem("Playloop/Symbolicate Crashes…", priority = 103)]
        public static void Open()
        {
            var win = GetWindow<PlayloopSymbolicationWindow>(title: "Playloop Symbolicate Crashes");
            win.minSize = new Vector2(720, 480);
            win.Show();
        }

        private void OnEnable()
        {
            _baseUrl = EditorPrefs.GetString(PrefBaseUrl, _baseUrl);
            _gameSlug = EditorPrefs.GetString(PrefGameSlug, "");
            _managementKey = EditorPrefs.GetString(PrefMgmtKey, "");
            _platformFilter = EditorPrefs.GetString(PrefPlatformFilter, "");
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (_busy || _busyIds.Count > 0) Repaint();
        }

        private void OnGUI()
        {
            DrawSettings();
            EditorGUILayout.Space(4);
            DrawToolbar();
            EditorGUILayout.Space(4);
            DrawHowTo();
            EditorGUILayout.Space(2);
            DrawStatus();
            EditorGUILayout.Space(2);
            DrawList();
        }

        private void DrawSettings()
        {
            _showSettings = EditorGUILayout.BeginFoldoutHeaderGroup(_showSettings, "Settings");
            if (_showSettings)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    _baseUrl = EditorGUILayout.TextField("Base URL", _baseUrl);
                    _gameSlug = EditorGUILayout.TextField("Game slug", _gameSlug);
                    _managementKey = EditorGUILayout.PasswordField("Management key", _managementKey);
                    _platformFilter = EditorGUILayout.TextField(
                        new GUIContent(
                            "Platform filter",
                            "ILIKE prefix on the SDK's platform string, e.g. " +
                            "'Windows', 'Linux', 'iPhone', 'Android'. Leave empty for all."),
                        _platformFilter);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Save settings", GUILayout.Width(120)))
                        {
                            EditorPrefs.SetString(PrefBaseUrl, _baseUrl);
                            EditorPrefs.SetString(PrefGameSlug, _gameSlug);
                            EditorPrefs.SetString(PrefMgmtKey, _managementKey);
                            EditorPrefs.SetString(PrefPlatformFilter, _platformFilter);
                            _status = "Saved.";
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawToolbar()
        {
            int readyCount = CountReady();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_busy))
                {
                    if (GUILayout.Button("Fetch unresolved crashes", GUILayout.Height(24)))
                    {
                        _ = FetchAsync();
                    }
                }
                using (new EditorGUI.DisabledScope(_busy || readyCount == 0))
                {
                    if (GUILayout.Button(
                            new GUIContent(
                                $"Submit all ready ({readyCount})",
                                "Submits every crash whose editable field has a non-empty " +
                                "resolved stack. Rows auto-detected as already-symbolic are " +
                                "pre-filled and counted here automatically."),
                            GUILayout.Height(24),
                            GUILayout.Width(180)))
                    {
                        _ = SubmitAllReadyAsync();
                    }
                }
                using (new EditorGUI.DisabledScope(_busy || _crashes.Count == 0))
                {
                    if (GUILayout.Button(
                            new GUIContent(
                                "Auto-fill all",
                                "Re-run the already-symbolic heuristic and pre-fill every " +
                                "row that passes it. Use after editing rows manually if you " +
                                "want to reset to the auto-detected state."),
                            GUILayout.Height(24),
                            GUILayout.Width(110)))
                    {
                        AutoFillReady();
                    }
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    _crashes.Count == 0
                        ? "No crashes loaded."
                        : $"{_crashes.Count} crash(es) · {readyCount} ready",
                    EditorStyles.miniLabel);
            }
        }

        private int CountReady()
        {
            int n = 0;
            foreach (var c in _crashes)
            {
                if (_editsById.TryGetValue(c.Id, out var v) && !string.IsNullOrWhiteSpace(v))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Heuristic: does this stack already carry source-position info?
        /// True when at least one frame matches the "fn (File.ext:line)"
        /// shape with a known managed-source extension. Unity Editor /
        /// Mono / dev IL2CPP builds (with Script Debugging on) routinely
        /// produce such stacks; retail IL2CPP builds typically don't.
        ///
        /// Conservative: false positives mean we'd auto-fill a stack
        /// that still needs work, which the dev will catch when they
        /// glance at the editable field before clicking Submit all. The
        /// real cost we're avoiding is FORCING the dev to copy-paste
        /// when the stack was already useful.
        /// </summary>
        internal static bool IsAlreadySymbolic(string stack)
        {
            if (string.IsNullOrWhiteSpace(stack)) return false;
            // Require at least one frame referencing a source file with a
            // line number, AND not just an "at addr" line. Common Unity
            // managed stack shapes:
            //   "  at MyClass.MyMethod () [0x00012] in <path>:42"
            //   "  at MyClass.MyMethod () (at Assets/Scripts/MyClass.cs:42)"
            //   "MyClass.MyMethod () (at Assets/Scripts/MyClass.cs:42)"
            // We accept any of these. The marker is `<file.ext>:<digits>`
            // where ext is a known managed-source extension.
            var lines = stack.Split('\n');
            int matches = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (HasSourceMarker(line)) matches++;
            }
            // Don't treat a single match in a long stack as "symbolic";
            // require at least 2 (or, for short stacks, every-non-blank).
            return matches >= 2 || (matches >= 1 && lines.Length <= 4);
        }

        private static readonly string[] SourceExtensions = { ".cs", ".gd", ".js", ".ts", ".tsx", ".py" };

        private static bool HasSourceMarker(string line)
        {
            // Look for ".<ext>:<digits>". Handles "Foo.cs:42", "Foo.cs:42:7",
            // "Foo.cs:42)" and the bracketed Unity Editor format
            // "(at Assets/Scripts/Foo.cs:42)".
            int colon = -1;
            foreach (var ext in SourceExtensions)
            {
                int i = line.IndexOf(ext + ":", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    colon = i + ext.Length;
                    break;
                }
            }
            if (colon < 0) return false;
            // Need a digit immediately after the colon.
            return colon + 1 < line.Length && char.IsDigit(line[colon + 1]);
        }

        private void AutoFillReady()
        {
            int filled = 0;
            foreach (var c in _crashes)
            {
                if (IsAlreadySymbolic(c.StackTrace))
                {
                    _editsById[c.Id] = c.StackTrace;
                    filled++;
                }
            }
            _status = filled > 0
                ? $"Auto-filled {filled} already-symbolic stack(s). Click 'Submit all ready' to ship them."
                : "No stacks looked already-symbolic. Use the per-row paste flow for IL2CPP retail builds.";
            Repaint();
        }

        private void DrawHowTo()
        {
            _showHowTo = EditorGUILayout.BeginFoldoutHeaderGroup(
                _showHowTo, "How to resolve a Unity stack");
            if (_showHowTo)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        "Pick the platform that produced the crash.",
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        "Unity Editor / Mono build",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        "Stacks already include file + line when the build was made with " +
                        "Script Debugging enabled. Copy the raw stack and submit as-is. " +
                        "It's already 'symbolicated' for grouping purposes.",
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(
                        "IL2CPP (iOS / Android / Switch / desktop release)",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        "Use Unity's Symbol Map Tool (Window > Analysis > IL2CPP in Unity 6+) " +
                        "or the platform's native tooling: atos for iOS dSYMs, ndk-stack for " +
                        "Android NDK symbols. Point each tool at the build's symbol output, " +
                        "feed it the raw stack from this window, and paste the result into " +
                        "the editable field below each crash.",
                        EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(
                        "What 'symbolicated' means here",
                        EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(
                        "Any stack that resolves at least the function + file name for each " +
                        "frame. Line numbers are great but not strictly required for grouping. " +
                        "The server normalizes line/column jitter when computing the group " +
                        "signature, so two builds of the same bug collapse correctly.",
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawStatus()
        {
            if (string.IsNullOrEmpty(_status)) return;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(_status, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawList()
        {
            if (_crashes.Count == 0)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        "Click \"Fetch unresolved crashes\" to load the list of crashes " +
                        "the server couldn't resolve automatically (Unity / IL2CPP / native " +
                        "stacks).",
                        EditorStyles.wordWrappedMiniLabel);
                }
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var c in _crashes)
            {
                DrawCrash(c);
                EditorGUILayout.Space(2);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawCrash(UnresolvedCrash c)
        {
            var expanded = _expandedIds.Contains(c.Id);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                bool isReady =
                    _editsById.TryGetValue(c.Id, out var editVal) &&
                    !string.IsNullOrWhiteSpace(editVal);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var foldLabel = expanded ? "▼" : "▶";
                    if (GUILayout.Button(foldLabel, EditorStyles.label, GUILayout.Width(16)))
                    {
                        if (expanded) _expandedIds.Remove(c.Id);
                        else _expandedIds.Add(c.Id);
                    }
                    // "Ready" pill: green-ish ✓ when this row has a resolved
                    // stack queued, gray "-" when it still needs the dev's
                    // attention. The bulk Submit button picks up exactly
                    // the green ones.
                    var pillColor = isReady ? new Color(0.4f, 0.85f, 0.5f) : new Color(0.6f, 0.6f, 0.6f);
                    var prevContent = GUI.contentColor;
                    GUI.contentColor = pillColor;
                    GUILayout.Label(
                        isReady ? "✓ READY" : "- NEEDS PASTE",
                        EditorStyles.miniBoldLabel,
                        GUILayout.Width(110));
                    GUI.contentColor = prevContent;
                    var title = !string.IsNullOrEmpty(c.Message) ? c.Message : "(no message)";
                    EditorGUILayout.LabelField(
                        new GUIContent(title, c.Id),
                        EditorStyles.boldLabel,
                        GUILayout.MinWidth(220));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(
                        $"build {c.BuildVersion ?? "?"} · {c.Platform ?? "?"} · {c.SymbolicationStatus}",
                        EditorStyles.miniLabel);
                }

                if (!expanded) return;

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Raw stack", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    c.StackTrace,
                    EditorStyles.textArea,
                    GUILayout.MinHeight(120),
                    GUILayout.MaxHeight(220));

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    "Symbolicated stack (paste below)",
                    EditorStyles.miniBoldLabel);
                if (!_editsById.TryGetValue(c.Id, out var current)) current = "";
                var next = EditorGUILayout.TextArea(
                    current, GUILayout.MinHeight(100), GUILayout.MaxHeight(220));
                if (!string.Equals(next, current, StringComparison.Ordinal))
                {
                    _editsById[c.Id] = next;
                }

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Copy raw to symbolicated", GUILayout.Width(190)))
                    {
                        _editsById[c.Id] = c.StackTrace;
                        Repaint();
                    }
                    if (GUILayout.Button(
                            new GUIContent(
                                "Resolve via shell tool…",
                                "Spawn a local command (ndk-stack, atos, Unity's Symbol Map " +
                                "Tool, anything else you have on PATH). The raw stack is " +
                                "piped to stdin; stdout becomes the resolved stack."),
                            GUILayout.Width(180)))
                    {
                        ShellResolveDialog.Show(c, OnShellResolveResult);
                    }
                    GUILayout.FlexibleSpace();
                    var isBusy = _busyIds.Contains(c.Id);
                    using (new EditorGUI.DisabledScope(
                               isBusy ||
                               !_editsById.TryGetValue(c.Id, out var v) ||
                               string.IsNullOrWhiteSpace(v)))
                    {
                        if (GUILayout.Button(isBusy ? "Submitting…" : "Submit resolved stack",
                                GUILayout.Width(180)))
                        {
                            _ = SubmitAsync(c);
                        }
                    }
                }
            }
        }

        private void OnShellResolveResult(string crashId, string? resolved, string? error)
        {
            if (!string.IsNullOrEmpty(error))
            {
                _status = $"Shell tool failed for {Trunc(crashId, 12)}…: {error}";
            }
            else if (!string.IsNullOrWhiteSpace(resolved))
            {
                _editsById[crashId] = resolved!;
                _status =
                    $"Tool returned a resolved stack for {Trunc(crashId, 12)}…. " +
                    "Review and click Submit (or use bulk submit).";
            }
            Repaint();
        }

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        private async Task FetchAsync()
        {
            if (string.IsNullOrEmpty(_gameSlug))
            {
                _status = "Set a Game slug first.";
                return;
            }
            if (string.IsNullOrEmpty(_managementKey))
            {
                _status = "Set a Management key first.";
                return;
            }
            _busy = true;
            _status = "Fetching…";
            Repaint();
            try
            {
                var qs = new StringBuilder();
                qs.Append("?limit=200");
                if (!string.IsNullOrWhiteSpace(_platformFilter))
                {
                    qs.Append("&platform=")
                      .Append(UnityWebRequest.EscapeURL(_platformFilter.Trim()));
                }
                var url =
                    $"{_baseUrl.TrimEnd('/')}/api/v1/games/{UnityWebRequest.EscapeURL(_gameSlug)}" +
                    $"/crashes/unresolved{qs}";
                using var req = UnityWebRequest.Get(url);
                req.SetRequestHeader("Authorization", $"Bearer {_managementKey}");
                req.SetRequestHeader("Accept", "application/json");
                await SendAsync(req);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"Fetch failed: HTTP {req.responseCode}: {req.downloadHandler.text}";
                    return;
                }
                var response = JsonConvert.DeserializeObject<UnresolvedCrashesResponse>(
                    req.downloadHandler.text);
                _crashes = response?.Crashes ?? new List<UnresolvedCrash>();
                // Auto-fill the editable field for any stack that already
                // looks symbolic. Saves the dev a copy-paste round-trip on
                // Editor / dev-build crashes (which is most of them).
                int auto = 0;
                foreach (var c in _crashes)
                {
                    if (IsAlreadySymbolic(c.StackTrace))
                    {
                        _editsById[c.Id] = c.StackTrace;
                        auto++;
                    }
                }
                _status = _crashes.Count == 0
                    ? "No unresolved crashes for this game / filter."
                    : $"Loaded {_crashes.Count} crash(es); {auto} auto-detected as already-symbolic " +
                      "(click 'Submit all ready' to ship them).";
            }
            catch (Exception e)
            {
                _status = $"Fetch threw: {e.Message}";
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private async Task SubmitAllReadyAsync()
        {
            var queued = new List<UnresolvedCrash>();
            foreach (var c in _crashes)
            {
                if (_editsById.TryGetValue(c.Id, out var v) && !string.IsNullOrWhiteSpace(v))
                {
                    queued.Add(c);
                }
            }
            if (queued.Count == 0)
            {
                _status = "Nothing to submit. No rows have a resolved stack.";
                return;
            }
            _status = $"Submitting {queued.Count}…";
            Repaint();
            // Fire in parallel. Each task pushes status updates through
            // _busyIds (per-row spinner). We await all so the final
            // "n succeeded, m failed" status reflects the real outcome.
            var tasks = new List<Task<bool>>(queued.Count);
            foreach (var c in queued) tasks.Add(SubmitSingleAsync(c));
            var results = await Task.WhenAll(tasks);
            int ok = 0, fail = 0;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i]) ok++; else fail++;
            }
            _status = fail == 0
                ? $"✓ Submitted {ok}. Reopen the Crashes tab on the dashboard to see the updated groups."
                : $"Submitted {ok}; {fail} failed. See per-row status above for details.";
            Repaint();
        }

        /// <summary>
        /// Single-crash submit that returns a success bool rather than
        /// writing to <c>_status</c>. Used by <see cref="SubmitAllReadyAsync"/>
        /// so the outer call can roll the results up into one summary line.
        /// </summary>
        private async Task<bool> SubmitSingleAsync(UnresolvedCrash c)
        {
            if (!_editsById.TryGetValue(c.Id, out var resolved) ||
                string.IsNullOrWhiteSpace(resolved))
                return false;
            _busyIds.Add(c.Id);
            Repaint();
            try
            {
                var url =
                    $"{_baseUrl.TrimEnd('/')}/api/v1/games/{UnityWebRequest.EscapeURL(_gameSlug)}" +
                    $"/crashes/{UnityWebRequest.EscapeURL(c.Id)}/resolve";
                var payload = JsonConvert.SerializeObject(new ResolvePayload
                {
                    SymbolicatedStackTrace = resolved,
                });
                var bodyBytes = Encoding.UTF8.GetBytes(payload);
                using var req = new UnityWebRequest(url, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(bodyBytes) { contentType = "application/json" },
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                req.SetRequestHeader("Authorization", $"Bearer {_managementKey}");
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("Content-Type", "application/json");
                await SendAsync(req);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    return false;
                }
                _crashes.RemoveAll(x => x.Id == c.Id);
                _editsById.Remove(c.Id);
                _expandedIds.Remove(c.Id);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _busyIds.Remove(c.Id);
            }
        }

        private async Task SubmitAsync(UnresolvedCrash c)
        {
            if (!_editsById.TryGetValue(c.Id, out var resolved) ||
                string.IsNullOrWhiteSpace(resolved))
            {
                _status = "Paste a resolved stack before submitting.";
                return;
            }
            _busyIds.Add(c.Id);
            Repaint();
            try
            {
                var url =
                    $"{_baseUrl.TrimEnd('/')}/api/v1/games/{UnityWebRequest.EscapeURL(_gameSlug)}" +
                    $"/crashes/{UnityWebRequest.EscapeURL(c.Id)}/resolve";
                var payload = JsonConvert.SerializeObject(new ResolvePayload
                {
                    SymbolicatedStackTrace = resolved,
                });
                var bodyBytes = Encoding.UTF8.GetBytes(payload);
                using var req = new UnityWebRequest(url, "POST")
                {
                    uploadHandler = new UploadHandlerRaw(bodyBytes) { contentType = "application/json" },
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                req.SetRequestHeader("Authorization", $"Bearer {_managementKey}");
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("Content-Type", "application/json");
                await SendAsync(req);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _status = $"Submit failed for {c.Id.Substring(0, Math.Min(c.Id.Length, 12))}…: " +
                              $"HTTP {req.responseCode}: {req.downloadHandler.text}";
                    return;
                }
                // Remove from the list. It's resolved now and shouldn't
                // re-appear on the next fetch either.
                _crashes.RemoveAll(x => x.Id == c.Id);
                _editsById.Remove(c.Id);
                _expandedIds.Remove(c.Id);
                _status = "Resolved. Reopen the Crashes tab on the dashboard to see the new group.";
            }
            catch (Exception e)
            {
                _status = $"Submit threw: {e.Message}";
            }
            finally
            {
                _busyIds.Remove(c.Id);
                Repaint();
            }
        }

        private static Task SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        // Wire shape for GET /unresolved + POST /resolve.

        private sealed class UnresolvedCrashesResponse
        {
            [JsonProperty("crashes")] public List<UnresolvedCrash>? Crashes { get; set; }
        }

        // `internal` (not `private`) because ShellResolveDialog.Show takes
        // this type as a parameter and is itself `internal`. CS0051 fires
        // when a method parameter type is less accessible than the method
        // signature. The enclosing class is still the only consumer of
        // the wire type. Nothing outside this file constructs one.
        internal sealed class UnresolvedCrash
        {
            [JsonProperty("id")] public string Id { get; set; } = "";
            [JsonProperty("buildVersion")] public string? BuildVersion { get; set; }
            [JsonProperty("platform")] public string? Platform { get; set; }
            [JsonProperty("signatureHash")] public string SignatureHash { get; set; } = "";
            [JsonProperty("stackTrace")] public string StackTrace { get; set; } = "";
            [JsonProperty("message")] public string? Message { get; set; }
            [JsonProperty("symbolicationStatus")] public string SymbolicationStatus { get; set; } = "";
            [JsonProperty("recordedAt")] public long RecordedAt { get; set; }
            [JsonProperty("reportedAt")] public long ReportedAt { get; set; }
        }

        private sealed class ResolvePayload
        {
            [JsonProperty("symbolicatedStackTrace")]
            public string SymbolicatedStackTrace { get; set; } = "";
        }

        // ────────────── Shell-out resolver dialog ──────────────

        /// <summary>
        /// Modal-style EditorWindow that lets the dev type (or paste)
        /// a local CLI command (ndk-stack, atos, Unity's Symbol Map
        /// Tool, anything else they have installed) and feeds the
        /// raw stack to its stdin. Stdout is captured and returned to
        /// the host window via the <c>onResult</c> callback. Stderr
        /// surfaces in the error slot below the field on failure.
        ///
        /// The last command used per platform is persisted in
        /// EditorPrefs so the dev only types it once per
        /// project/platform combo. Suggested defaults appear when no
        /// prior command exists for the platform.
        /// </summary>
        internal sealed class ShellResolveDialog : EditorWindow
        {
            private const string PrefPrefix = "Playloop.Editor.SymbolicateWindow.ShellCmd.";

            private UnresolvedCrash? _crash;
            private Action<string, string?, string?>? _onResult; // (crashId, resolved, error)

            private string _command = "";
            private string _error = "";
            private string _stdout = "";
            private bool _busy;

            internal static void Show(
                UnresolvedCrash crash,
                Action<string, string?, string?> onResult)
            {
                var win = CreateInstance<ShellResolveDialog>();
                win._crash = crash;
                win._onResult = onResult;
                win._command = LoadDefaultCommand(crash);
                win.titleContent = new GUIContent(
                    $"Resolve via shell tool: {crash.Platform ?? "unknown"}");
                win.minSize = new Vector2(640, 440);
                win.maxSize = new Vector2(900, 600);
                win.ShowUtility();
                win.Focus();
            }

            private void OnGUI()
            {
                if (_crash == null) { Close(); return; }
                EditorGUILayout.LabelField(
                    $"Crash {Trunc(_crash.Id, 16)}… · build {_crash.BuildVersion ?? "?"} · " +
                    $"{_crash.Platform ?? "?"}",
                    EditorStyles.boldLabel);
                EditorGUILayout.Space(4);

                EditorGUILayout.LabelField(
                    "Shell command (raw stack is piped to stdin; stdout becomes the resolved stack):",
                    EditorStyles.wordWrappedMiniLabel);
                _command = EditorGUILayout.TextField(_command);

                EditorGUILayout.LabelField(
                    SuggestionFor(_crash.Platform),
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);

                EditorGUILayout.LabelField("Raw stack (stdin)", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    _crash.StackTrace,
                    EditorStyles.textArea,
                    GUILayout.MinHeight(100), GUILayout.MaxHeight(140));

                EditorGUILayout.LabelField("Stdout preview", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    _stdout,
                    EditorStyles.textArea,
                    GUILayout.MinHeight(100), GUILayout.MaxHeight(180));

                if (!string.IsNullOrEmpty(_error))
                {
                    var prev = GUI.contentColor;
                    GUI.contentColor = new Color(0.96f, 0.45f, 0.45f);
                    EditorGUILayout.LabelField(_error, EditorStyles.wordWrappedMiniLabel);
                    GUI.contentColor = prev;
                }

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                    {
                        Close();
                    }
                    using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_command)))
                    {
                        if (GUILayout.Button(_busy ? "Running…" : "Run", GUILayout.Width(100)))
                        {
                            _ = RunAsync();
                        }
                    }
                    using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_stdout)))
                    {
                        if (GUILayout.Button("Use stdout & close", GUILayout.Width(150)))
                        {
                            SaveDefaultCommand(_crash, _command);
                            var crashId = _crash!.Id;
                            var stdout = _stdout;
                            var cb = _onResult;
                            Close();
                            cb?.Invoke(crashId, stdout, null);
                        }
                    }
                }
            }

            private async Task RunAsync()
            {
                _error = "";
                _stdout = "";
                _busy = true;
                Repaint();
                try
                {
                    var (exe, args) = SplitCommand(_command);
                    if (string.IsNullOrEmpty(exe))
                    {
                        _error = "Empty command.";
                        return;
                    }
                    var (stdout, stderr, code) = await RunProcessAsync(
                        exe, args, _crash!.StackTrace, timeoutMs: 30_000);
                    _stdout = stdout;
                    if (code != 0)
                    {
                        _error =
                            $"Process exited {code}. " +
                            (string.IsNullOrEmpty(stderr) ? "(no stderr)" : $"Stderr: {stderr.Trim()}");
                    }
                    else if (string.IsNullOrWhiteSpace(stdout))
                    {
                        _error =
                            "Process exited 0 but stdout was empty. Did the tool log to " +
                            "stderr instead? Adjust the command or check the tool's usage.";
                    }
                }
                catch (Exception e)
                {
                    _error = $"{e.GetType().Name}: {e.Message}";
                }
                finally
                {
                    _busy = false;
                    Repaint();
                }
            }

            /// <summary>
            /// Naive whitespace split for command + args. Misses paths
            /// with spaces. The dev can work around by quoting the
            /// path with shell-style quotes which we handle here.
            /// </summary>
            internal static (string exe, string[] args) SplitCommand(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return ("", Array.Empty<string>());
                var tokens = new List<string>();
                var cur = new StringBuilder();
                bool inDouble = false, inSingle = false;
                foreach (var ch in raw.Trim())
                {
                    if (ch == '"' && !inSingle) { inDouble = !inDouble; continue; }
                    if (ch == '\'' && !inDouble) { inSingle = !inSingle; continue; }
                    if (char.IsWhiteSpace(ch) && !inDouble && !inSingle)
                    {
                        if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                        continue;
                    }
                    cur.Append(ch);
                }
                if (cur.Length > 0) tokens.Add(cur.ToString());
                if (tokens.Count == 0) return ("", Array.Empty<string>());
                var head = tokens[0];
                tokens.RemoveAt(0);
                return (head, tokens.ToArray());
            }

            /// <summary>
            /// Async wrapper around Process.Start. Pipes <c>stdin</c>
            /// in, captures stdout + stderr, enforces a timeout. Kills
            /// the process if the timeout elapses.
            /// </summary>
            internal static Task<(string stdout, string stderr, int code)> RunProcessAsync(
                string exe, string[] args, string stdin, int timeoutMs)
            {
                var tcs = new TaskCompletionSource<(string, string, int)>();
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    foreach (var a in args) psi.ArgumentList.Add(a);

                    var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    var stdoutBuf = new StringBuilder();
                    var stderrBuf = new StringBuilder();
                    proc.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null) stdoutBuf.AppendLine(e.Data);
                    };
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) stderrBuf.AppendLine(e.Data);
                    };
                    proc.Exited += (sender, evt) =>
                    {
                        try
                        {
                            tcs.TrySetResult(
                                (stdoutBuf.ToString(), stderrBuf.ToString(), proc.ExitCode));
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    try
                    {
                        proc.StandardInput.Write(stdin ?? "");
                    }
                    catch
                    {
                        // Process closed stdin early, fine.
                    }
                    finally
                    {
                        try { proc.StandardInput.Close(); } catch { /* swallow */ }
                    }

                    // Timeout watchdog. If the process hangs (e.g. tool
                    // waits on tty input we didn't supply), we kill it
                    // after `timeoutMs` and report a 124 exit code (the
                    // shell convention for "timed out").
                    Task.Delay(timeoutMs).ContinueWith(_ =>
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.Kill();
                                tcs.TrySetResult(
                                    (stdoutBuf.ToString(),
                                     stderrBuf.ToString() + "[timed out after " +
                                        timeoutMs + "ms]",
                                     124));
                            }
                        }
                        catch
                        {
                            /* process already exited or kill failed */
                        }
                    });
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
                return tcs.Task;
            }

            private static string SuggestionFor(string? platform)
            {
                var p = (platform ?? "").ToLowerInvariant();
                if (p.StartsWith("android"))
                    return "Suggested: ndk-stack -sym <path/to/symbols-dir>";
                if (p.StartsWith("iphone") || p.StartsWith("osx") || p.StartsWith("mac"))
                    return "Suggested: atos -o <path/to/dSYM>/Contents/Resources/DWARF/<binary>";
                if (p.StartsWith("windows"))
                    return "Suggested: cdb -z <minidump>, or your IL2CPP managed-symbol tool";
                return "Suggested: point at whatever local tool resolves stacks for " + (platform ?? "this platform") + ".";
            }

            private static string LoadDefaultCommand(UnresolvedCrash crash)
            {
                var key = PrefKey(crash.Platform);
                return EditorPrefs.GetString(key, "");
            }

            private static void SaveDefaultCommand(UnresolvedCrash crash, string cmd)
            {
                if (string.IsNullOrWhiteSpace(cmd)) return;
                var key = PrefKey(crash.Platform);
                EditorPrefs.SetString(key, cmd);
            }

            private static string PrefKey(string? platform)
                => PrefPrefix + (string.IsNullOrWhiteSpace(platform) ? "_default" : platform!);
        }
    }
}
#endif
