#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Playloop.Feedback
{
    /// <summary>
    /// Theme settings for the default UGUI feedback form. Pass an
    /// instance to <see cref="FeedbackForm.OpenAsync"/> to override
    /// colors / font; defaults match Playloop's dashboard styling and
    /// work in either a dark or light overlay.
    ///
    /// Field colors are read once at instantiation. Re-open the form
    /// to apply a theme change.
    /// </summary>
    public sealed class FeedbackFormTheme
    {
        /// <summary>Backdrop dimmer behind the form panel.</summary>
        public Color BackdropColor { get; set; } = new Color(0f, 0f, 0f, 0.65f);
        public Color PanelColor { get; set; } = new Color(0.09f, 0.10f, 0.12f, 1f);
        public Color BorderColor { get; set; } = new Color(0.20f, 0.21f, 0.24f, 1f);
        public Color HeadingColor { get; set; } = Color.white;
        public Color LabelColor { get; set; } = new Color(0.85f, 0.85f, 0.88f, 1f);
        public Color InputBgColor { get; set; } = new Color(0.13f, 0.14f, 0.16f, 1f);
        public Color InputTextColor { get; set; } = Color.white;
        public Color AccentColor { get; set; } = new Color(0.40f, 0.62f, 1f, 1f);
        public Color ButtonPrimaryBg { get; set; } = new Color(0.40f, 0.62f, 1f, 1f);
        public Color ButtonPrimaryText { get; set; } = Color.white;
        public Color ButtonSecondaryBg { get; set; } = new Color(0.16f, 0.17f, 0.20f, 1f);
        public Color ButtonSecondaryText { get; set; } = new Color(0.85f, 0.85f, 0.88f, 1f);

        /// <summary>Heading shown at the top of the form. Optional; null hides it.</summary>
        public string? Title { get; set; } = "Feedback";

        /// <summary>Label on the dismiss button. Override to localize.</summary>
        public string CancelLabel { get; set; } = "Cancel";
        public string YesLabel { get; set; } = "Yes";
        public string NoLabel { get; set; } = "No";

        /// <summary>Label on the submit button. Override to localize.</summary>
        public string SendLabel { get; set; } = "Send";

        /// <summary>Override Unity's <c>Arial</c> default. Leave null to use the SDK default.</summary>
        public Font? Font { get; set; }

        /// <summary>
        /// Show a pointing-hand cursor while hovering clickable elements
        /// (buttons, rating cells, the close button, the branding badge).
        /// Disable for gamepad-only or custom-cursor games.
        /// </summary>
        public bool UseHandCursor { get; set; } = true;

        /// <summary>
        /// Replace the SDK's built-in hand cursor with your own texture
        /// (read-enabled, RGBA32 recommended). Used only when
        /// <see cref="UseHandCursor"/> is true. Pair with
        /// <see cref="HandCursorHotspot"/>.
        /// </summary>
        public Texture2D? HandCursor { get; set; }

        /// <summary>
        /// Click point of <see cref="HandCursor"/> in pixels from the
        /// texture's top-left. Ignored for the built-in cursor (which has
        /// its own hotspot).
        /// </summary>
        public Vector2 HandCursorHotspot { get; set; } = Vector2.zero;

        /// <summary>
        /// While the form is open, disable every other raycaster in the
        /// scene (game UI canvases, physics raycasters that feed clicks to
        /// world objects, UI Toolkit panels) and restore them when it
        /// closes, so nothing behind the form is clickable. Set false if
        /// your game needs its raycasters live while the form is up (e.g.
        /// a custom cursor driven through the event system). Input your
        /// game reads by polling never goes through a raycaster; gate that
        /// with <see cref="FeedbackForm.IsOpen"/> or the
        /// <see cref="FeedbackForm.Opened"/> / <see cref="FeedbackForm.Closed"/>
        /// events.
        /// </summary>
        public bool BlockGameRaycasts { get; set; } = true;
    }

    /// <summary>
    /// Outcome of a <see cref="FeedbackForm.OpenAsync"/> call:
    /// <c>Submitted</c> when the player clicked Send (and the request
    /// landed at Playloop), <c>Dismissed</c> when they clicked Cancel
    /// or hit Escape, <c>Failed</c> when validation or the network
    /// rejected the submission.
    /// </summary>
    public enum FeedbackFormOutcome
    {
        Submitted,
        Dismissed,
        Failed,
    }

    /// <summary>
    /// Return type for <see cref="FeedbackForm.OpenAsync"/>. When
    /// <see cref="Outcome"/> is <see cref="FeedbackFormOutcome.Submitted"/>,
    /// <see cref="Result"/> carries the server response;
    /// <see cref="Error"/> is populated only on
    /// <see cref="FeedbackFormOutcome.Failed"/>.
    /// </summary>
    public sealed class FeedbackFormResult
    {
        public FeedbackFormOutcome Outcome { get; init; }
        public SubmitFeedbackResult? Result { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Programmatic UGUI default form for Player Feedback. Builds a
    /// Canvas + VerticalLayoutGroup form at runtime, awaits the
    /// player's answers, and submits through <see cref="FeedbackApi"/>.
    ///
    /// No prefab asset to maintain: the form is constructed at
    /// instantiation time so the SDK ships as a pure-code package.
    /// Devs who want a custom UI can ignore this entirely and call
    /// <see cref="FeedbackApi.SubmitAsync"/> from their own overlay.
    ///
    /// Lifetime: the form parents itself to a sentinel Canvas, lives
    /// across scene loads via <c>DontDestroyOnLoad</c>, and destroys
    /// the Canvas when the player resolves the form. Only one form may
    /// be open at a time across the whole SDK. A concurrent
    /// <see cref="OpenAsync"/> resolves immediately as
    /// <see cref="FeedbackFormOutcome.Failed"/> rather than spawning a
    /// second overlay that would fight over the EventSystem and Canvas.
    /// </summary>
    public sealed class FeedbackForm
    {
        private readonly FeedbackApi _api;

        // Optional event-config store. When present, the form reads the
        // server-resolved "Powered by Playloop" branding flag from it; when
        // null (e.g. a hand-constructed form), branding defaults to shown.
        private readonly Telemetry.EventConfigStore? _eventConfig;

        // Single-overlay guard, shared across every FeedbackForm instance:
        // a live form may spawn and own the scene's EventSystem, so two
        // open forms (e.g. feedback + a future consent prompt) would fight
        // over input focus and Canvas sorting. 0 = none open, 1 = one open.
        // Held for the form's whole lifetime, released in OpenAsync's finally.
        private static int _openGuard;

        /// <summary>
        /// True while a default form is on screen. Game code that reads
        /// input by polling (<c>Input.GetMouseButtonDown</c>, Physics
        /// raycasts, Input System actions) bypasses the UI event system
        /// entirely, so the form's backdrop cannot intercept it. Check
        /// this flag (or subscribe to <see cref="Opened"/> /
        /// <see cref="Closed"/>) to gate gameplay input while the player
        /// is answering.
        /// </summary>
        public static bool IsOpen => _openGuard != 0;

        /// <summary>Fired on the main thread when a default form opens.</summary>
        public static event System.Action? Opened;

        /// <summary>
        /// Fired on the main thread when the form resolves (submitted,
        /// dismissed, or cancelled), after <see cref="IsOpen"/> has
        /// returned to false. Pair with <see cref="Opened"/> to pause the
        /// game while the form is up, e.g. via <c>Time.timeScale</c>.
        /// </summary>
        public static event System.Action? Closed;

        private static void FireSafely(System.Action? evt)
        {
            try { evt?.Invoke(); }
            catch (System.Exception ex) { Debug.LogException(ex); }
        }

        public FeedbackForm(FeedbackApi api, Telemetry.EventConfigStore? eventConfig = null)
        {
            _api = api ?? throw new System.ArgumentNullException(nameof(api));
            _eventConfig = eventConfig;
        }

        /// <summary>
        /// Open the default UGUI feedback form against a concrete session
        /// id. Resolves when the player submits, cancels, or the
        /// cancellation token fires. Safe to await from a coroutine
        /// wrapper or a MonoBehaviour async method.
        ///
        /// Pass <paramref name="fields"/> to render a custom field set;
        /// omit to use <see cref="DefaultFeedbackFields.Snapshot"/>.
        /// </summary>
        public Task<FeedbackFormResult> OpenAsync(
            string formId,
            string sessionId,
            IReadOnlyList<FeedbackField>? fields = null,
            FeedbackFormTheme? theme = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new System.ArgumentException("sessionId is required.", nameof(sessionId));
            return OpenAsync(formId, () => sessionId, fields, theme, ct);
        }

        /// <summary>
        /// Open the default UGUI feedback form, resolving the session id
        /// lazily when the player clicks Send. Use this overload to prompt
        /// before the telemetry session has flushed: the server-assigned
        /// id (<see cref="TelemetryApi.CurrentSessionId"/>) is null until
        /// the first flush, so passing a provider lets you show the form
        /// at session start (an early-feedback or consent prompt) without
        /// blocking on the network. The provider is read once, when the
        /// player submits; if it still yields no id the call resolves as
        /// <see cref="FeedbackFormOutcome.Failed"/> rather than throwing.
        ///
        /// Only one feedback form may be open at a time across the whole
        /// SDK (the overlay owns the EventSystem). A second concurrent open
        /// resolves immediately as <see cref="FeedbackFormOutcome.Failed"/>
        /// without spawning a competing overlay.
        /// </summary>
        public async Task<FeedbackFormResult> OpenAsync(
            string formId,
            System.Func<string?> sessionIdProvider,
            IReadOnlyList<FeedbackField>? fields = null,
            FeedbackFormTheme? theme = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(formId))
                throw new System.ArgumentException("formId is required.", nameof(formId));
            if (sessionIdProvider == null)
                throw new System.ArgumentNullException(nameof(sessionIdProvider));

            // Disabled SDK (blank ingest key): resolve as Failed WITHOUT
            // spawning the overlay. Showing a form that can never submit is
            // worse than not showing one; the caller branches on Outcome the
            // same way it would for any failure.
            if (!_api.Enabled)
            {
                return new FeedbackFormResult
                {
                    Outcome = FeedbackFormOutcome.Failed,
                    Error = "Playloop is not configured (no ingest key); feedback form is disabled.",
                };
            }

            var resolvedFields = fields ?? DefaultFeedbackFields.Snapshot();
            if (resolvedFields.Count == 0)
            {
                throw new System.ArgumentException(
                    "fields must contain at least one entry.", nameof(fields));
            }
            var resolvedTheme = theme ?? new FeedbackFormTheme();

            // Claim the single-overlay guard. A concurrent open fails fast
            // instead of stacking a second form that fights for input.
            if (Interlocked.CompareExchange(ref _openGuard, 1, 0) != 0)
            {
                return new FeedbackFormResult
                {
                    Outcome = FeedbackFormOutcome.Failed,
                    Error = "A feedback form is already open.",
                };
            }

            try
            {
                FireSafely(Opened);
                var tcs = new TaskCompletionSource<FeedbackFormOutcomeRaw>();
                // Resolve the branding flag from event-config (defaults to
                // shown when the config hasn't loaded or no store is wired).
                var showBranding = _eventConfig?.GameSettings().ShowBranding ?? true;
                var rootGo = BuildForm(resolvedFields, resolvedTheme, tcs, showBranding);

                // Hold full pointer capture: switch off every raycaster that
                // isn't ours so nothing behind the form (game UI, world
                // objects behind a PhysicsRaycaster, UI Toolkit panels)
                // receives clicks while the form is up. Restored below.
                List<BaseRaycaster>? suppressed = null;
                if (resolvedTheme.BlockGameRaycasts)
                {
                    suppressed = SuppressOtherRaycasters(rootGo);
                }

                // Wire cancellation: destroy the form + resolve as Dismissed
                // so the caller's await unblocks immediately.
                CancellationTokenRegistration ctReg = default;
                if (ct.CanBeCanceled)
                {
                    ctReg = ct.Register(() =>
                    {
                        if (rootGo != null) Object.Destroy(rootGo);
                        tcs.TrySetResult(new FeedbackFormOutcomeRaw { Outcome = FeedbackFormOutcome.Dismissed });
                    });
                }

                FeedbackFormOutcomeRaw raw;
                try
                {
                    raw = await tcs.Task.ConfigureAwait(true);
                }
                finally
                {
                    ctReg.Dispose();
                    if (rootGo != null) Object.Destroy(rootGo);
                    RestoreRaycasters(suppressed);
                }

                if (raw.Outcome == FeedbackFormOutcome.Dismissed)
                {
                    return new FeedbackFormResult { Outcome = FeedbackFormOutcome.Dismissed };
                }

                // Resolve the session id now, after the player answered, by
                // which point the first telemetry flush has almost always
                // established it. Fail soft (don't throw) if the session
                // still hasn't started, so a too-early prompt loses the
                // answers gracefully rather than crashing gameplay code.
                var sessionId = sessionIdProvider();
                if (string.IsNullOrEmpty(sessionId))
                {
                    return new FeedbackFormResult
                    {
                        Outcome = FeedbackFormOutcome.Failed,
                        Error = "No active session yet. Feedback can't be attributed until the "
                            + "telemetry session has started (its id is assigned on the first flush).",
                    };
                }

                // Submit phase. The form already validated requireds before
                // letting the player click Send, so any failure here is a
                // server-side reject (rate limit, auth, etc.).
                try
                {
                    var result = await _api.SubmitAsync(
                        formId,
                        sessionId!,
                        raw.Responses!,
                        askedAtSec: raw.AskedAtSec,
                        answeredAtSec: System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ct: ct).ConfigureAwait(true);
                    return new FeedbackFormResult
                    {
                        Outcome = FeedbackFormOutcome.Submitted,
                        Result = result,
                    };
                }
                catch (System.Exception ex)
                {
                    return new FeedbackFormResult
                    {
                        Outcome = FeedbackFormOutcome.Failed,
                        Error = ex.Message,
                    };
                }
            }
            finally
            {
                Interlocked.Exchange(ref _openGuard, 0);
                FireSafely(Closed);
            }
        }

        private struct FeedbackFormOutcomeRaw
        {
            public FeedbackFormOutcome Outcome;
            public List<FeedbackResponseInput>? Responses;
            public long AskedAtSec;
        }

        private static GameObject BuildForm(
            IReadOnlyList<FeedbackField> fields,
            FeedbackFormTheme theme,
            TaskCompletionSource<FeedbackFormOutcomeRaw> tcs,
            bool showBranding)
        {
            // Canvas root: overlay, top-most sorting order so the form
            // sits above the game's own UI without the dev having to
            // reorder their Canvas sorting.
            var root = new GameObject("Playloop_FeedbackForm");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32_000;
            // Snap every element to whole pixels. The scaler below produces
            // a fractional scale factor at most window sizes, which lands
            // text on sub-pixel positions and bilinear filtering smears it.
            // The form reads as uniformly blurry. The form is static (no
            // animated layout), so pixel-perfect costs nothing here.
            canvas.pixelPerfect = true;
            // Anchor the scaler to a 1080p reference so the card renders at
            // a consistent on-screen proportion in any game window. Without
            // an explicit reference resolution, ScaleWithScreenSize falls
            // back to Unity's 800x600 default and the form balloons to most
            // of the screen on modern displays.
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();

            // EventSystem: only spawn one if the scene doesn't already
            // have one (most scenes do; player UIs need it). "Any instance" is
            // all we need, so use the faster FindAnyObjectByType where it exists
            // (2022.2+); fall back to FindObjectOfType on 2021.3 (our min
            // version), where the old API is not yet deprecated.
#if UNITY_2022_2_OR_NEWER
            if (Object.FindAnyObjectByType<EventSystem>() == null)
#else
            if (Object.FindObjectOfType<EventSystem>() == null)
#endif
            {
                var es = new GameObject("Playloop_FeedbackEventSystem");
                es.transform.SetParent(root.transform, false);
                es.AddComponent<EventSystem>();
                AddCompatibleInputModule(es);
            }

            // Fullscreen backdrop. Its Image is a raycast target, so every
            // pointer event that reaches the UI event system and isn't on a
            // form control dies here instead of hitting the game's UI
            // underneath. Game code that polls input directly (mouse-button
            // polling, Physics raycasts, Input System actions) never enters
            // the event system and can't be intercepted by any Canvas.
            // That's what the static IsOpen / Opened / Closed surface is for.
            var backdrop = AddImage(root.transform, "Backdrop", theme.BackdropColor);
            Stretch(backdrop);
            backdrop.GetComponent<Image>().raycastTarget = true;

            // Panel: VerticalLayoutGroup so fields stack naturally with
            // a fixed width and the heights flex to their content.
            var panel = AddImage(root.transform, "Panel", theme.PanelColor);
            var panelRt = (RectTransform)panel.transform;
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(460f, 0f);
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(24, 24, 20, 20);
            vlg.spacing = 12f;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            panel.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            var border = panel.AddComponent<Outline>();
            border.effectColor = theme.BorderColor;
            border.effectDistance = new Vector2(1f, -1f);

            System.Action dismiss = () => tcs.TrySetResult(new FeedbackFormOutcomeRaw
            {
                Outcome = FeedbackFormOutcome.Dismissed,
            });

            // Escape dismisses, same as Cancel / the close button. Polled by
            // a tiny behaviour on the root so it works regardless of which
            // input backend the project runs.
            root.AddComponent<EscapeDismissBehaviour>().OnEscape = dismiss;

            if (!string.IsNullOrEmpty(theme.Title))
            {
                AddLabel(panel.transform, theme.Title!, theme, isHeading: true);
            }

            // Close (x) in the panel's top-right corner: opts out of the
            // VerticalLayoutGroup so it floats over the heading row.
            AddCloseButton(panel.transform, theme, dismiss);

            var askedAtSec = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var widgets = new List<IFieldWidget>(fields.Count);
            foreach (var field in fields)
            {
                AddLabel(panel.transform, field.Label, theme, isHeading: false);
                if (!string.IsNullOrEmpty(field.HelpText))
                {
                    AddLabel(panel.transform, field.HelpText!, theme, isHelp: true);
                }
                var widget = BuildFieldWidget(panel.transform, field, theme);
                widgets.Add(widget);
            }

            // Buttons row: Cancel + Send. Cancel resolves as Dismissed
            // without attempting validation; Send validates requireds
            // and resolves Submitted when every required field has a
            // value.
            var buttonRow = new GameObject("Buttons", typeof(RectTransform));
            buttonRow.transform.SetParent(panel.transform, false);
            var hlg = buttonRow.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 10f;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;
            hlg.childAlignment = TextAnchor.MiddleRight;
            hlg.padding = new RectOffset(0, 0, 8, 0);
            var rowLayout = buttonRow.AddComponent<LayoutElement>();
            rowLayout.minHeight = 40f;

            BuildButton(buttonRow.transform, theme.CancelLabel,
                theme.ButtonSecondaryBg, theme.ButtonSecondaryText, theme,
                onClick: dismiss);

            BuildButton(buttonRow.transform, theme.SendLabel,
                theme.ButtonPrimaryBg, theme.ButtonPrimaryText, theme,
                onClick: () =>
                {
                    var responses = new List<FeedbackResponseInput>(widgets.Count);
                    for (int i = 0; i < widgets.Count; i++)
                    {
                        var field = fields[i];
                        var value = widgets[i].ReadValue();
                        if (field.Required == true && string.IsNullOrEmpty(value))
                        {
                            // Surface the missing-field state inline. Don't
                            // resolve the task. Let the player fill it in
                            // and click Send again.
                            widgets[i].SetError(true);
                            return;
                        }
                        widgets[i].SetError(false);
                        // Skip empty optional fields so the wire payload
                        // stays tight; the server validates required vs
                        // optional via the form definition.
                        if (string.IsNullOrEmpty(value)) continue;
                        responses.Add(new FeedbackResponseInput(field.Id, value));
                    }
                    if (responses.Count == 0)
                    {
                        // Every field empty: treat like a dismiss so we
                        // don't 400 the server with no_responses.
                        tcs.TrySetResult(new FeedbackFormOutcomeRaw
                        {
                            Outcome = FeedbackFormOutcome.Dismissed,
                        });
                        return;
                    }
                    tcs.TrySetResult(new FeedbackFormOutcomeRaw
                    {
                        Outcome = FeedbackFormOutcome.Submitted,
                        Responses = responses,
                        AskedAtSec = askedAtSec,
                    });
                });

            // "Powered by Playloop" footer mark. Added last so it sits at the
            // bottom of the VerticalLayoutGroup. Shown unless the studio (on a
            // paid plan) turned it off. The value is resolved server-side and
            // read from the event-config the SDK fetches on startup. Tapping it
            // opens playloop.gg in the player's browser.
            if (showBranding)
            {
                AddBrandingBadge(panel.transform, theme);
            }

            return root;
        }

        // ---- Field widgets ----------------------------------------------

        private interface IFieldWidget
        {
            string ReadValue();
            void SetError(bool show);
        }

        private static IFieldWidget BuildFieldWidget(
            Transform parent, FeedbackField field, FeedbackFormTheme theme)
        {
            switch (field.Kind)
            {
                case FeedbackFieldKinds.Rating1To5:
                    return BuildRating(parent, theme);
                case FeedbackFieldKinds.YesNo:
                    return BuildYesNo(parent, theme);
                case FeedbackFieldKinds.LongText:
                    return BuildText(parent, field, theme, multiline: true);
                case FeedbackFieldKinds.ShortText:
                default:
                    return BuildText(parent, field, theme, multiline: false);
            }
        }

        private sealed class RatingWidget : IFieldWidget
        {
            public int Value;
            public Image[] Cells = System.Array.Empty<Image>();
            public Color SelectedBg;
            public Color DefaultBg;
            public Image? Border;
            public string ReadValue() => Value > 0 ? Value.ToString() : "";
            public void SetError(bool show)
            {
                if (Border != null)
                {
                    // Reuse the rating-row image's border slot for the
                    // missing-required affordance.
                    Border.color = show ? new Color(0.95f, 0.42f, 0.40f, 1f) : DefaultBg;
                }
            }
        }

        private static IFieldWidget BuildRating(Transform parent, FeedbackFormTheme theme)
        {
            var widget = new RatingWidget
            {
                SelectedBg = theme.AccentColor,
                DefaultBg = theme.InputBgColor,
            };

            var row = new GameObject("Rating", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;
            var layout = row.AddComponent<LayoutElement>();
            layout.minHeight = 36f;

            var cells = new List<Image>(5);
            for (int i = 1; i <= 5; i++)
            {
                int captured = i;
                var cell = AddImage(row.transform, $"r{i}", theme.InputBgColor);
                var le = cell.AddComponent<LayoutElement>();
                le.minHeight = 36f;
                var btn = cell.AddComponent<Button>();
                btn.targetGraphic = cell.GetComponent<Image>();
                MakeHoverable(cell, theme);
                AddCenteredLabel(cell.transform, i.ToString(), theme);
                btn.onClick.AddListener(() =>
                {
                    widget.Value = captured;
                    for (int j = 0; j < widget.Cells.Length; j++)
                    {
                        widget.Cells[j].color = (j < captured)
                            ? widget.SelectedBg
                            : widget.DefaultBg;
                    }
                    widget.SetError(false);
                });
                cells.Add(cell.GetComponent<Image>());
            }
            widget.Cells = cells.ToArray();
            return widget;
        }

        private sealed class YesNoWidget : IFieldWidget
        {
            public string Value = "";
            public Image? YesBg;
            public Image? NoBg;
            public Color SelectedBg;
            public Color DefaultBg;
            public string ReadValue() => Value;
            public void SetError(bool show)
            {
                if (YesBg != null) YesBg.color = string.IsNullOrEmpty(Value)
                    ? (show ? new Color(0.95f, 0.42f, 0.40f, 1f) : DefaultBg)
                    : (Value == "yes" ? SelectedBg : DefaultBg);
                if (NoBg != null) NoBg.color = string.IsNullOrEmpty(Value)
                    ? (show ? new Color(0.95f, 0.42f, 0.40f, 1f) : DefaultBg)
                    : (Value == "no" ? SelectedBg : DefaultBg);
            }
        }

        private static IFieldWidget BuildYesNo(Transform parent, FeedbackFormTheme theme)
        {
            var widget = new YesNoWidget
            {
                SelectedBg = theme.AccentColor,
                DefaultBg = theme.InputBgColor,
            };

            var row = new GameObject("YesNo", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childForceExpandHeight = false;
            hlg.childForceExpandWidth = true;
            row.AddComponent<LayoutElement>().minHeight = 36f;

            var yesBg = AddImage(row.transform, "Yes", theme.InputBgColor);
            yesBg.AddComponent<LayoutElement>().minHeight = 36f;
            AddCenteredLabel(yesBg.transform, theme.YesLabel, theme);
            var yesBtn = yesBg.AddComponent<Button>();
            yesBtn.targetGraphic = yesBg.GetComponent<Image>();
            MakeHoverable(yesBg, theme);
            yesBtn.onClick.AddListener(() =>
            {
                widget.Value = "yes";
                widget.YesBg!.color = widget.SelectedBg;
                widget.NoBg!.color = widget.DefaultBg;
            });
            widget.YesBg = yesBg.GetComponent<Image>();

            var noBg = AddImage(row.transform, "No", theme.InputBgColor);
            noBg.AddComponent<LayoutElement>().minHeight = 36f;
            AddCenteredLabel(noBg.transform, theme.NoLabel, theme);
            var noBtn = noBg.AddComponent<Button>();
            noBtn.targetGraphic = noBg.GetComponent<Image>();
            MakeHoverable(noBg, theme);
            noBtn.onClick.AddListener(() =>
            {
                widget.Value = "no";
                widget.NoBg!.color = widget.SelectedBg;
                widget.YesBg!.color = widget.DefaultBg;
            });
            widget.NoBg = noBg.GetComponent<Image>();

            return widget;
        }

        private sealed class TextWidget : IFieldWidget
        {
            public InputField? Input;
            public Image? Background;
            public Color DefaultBg;
            public string ReadValue() => Input != null ? Input.text ?? "" : "";
            public void SetError(bool show)
            {
                if (Background != null)
                    Background.color = show
                        ? new Color(0.95f, 0.42f, 0.40f, 0.25f)
                        : DefaultBg;
            }
        }

        private static IFieldWidget BuildText(
            Transform parent, FeedbackField field, FeedbackFormTheme theme, bool multiline)
        {
            var widget = new TextWidget { DefaultBg = theme.InputBgColor };
            var bg = AddImage(parent, multiline ? "LongText" : "ShortText", theme.InputBgColor);
            widget.Background = bg.GetComponent<Image>();
            var layout = bg.AddComponent<LayoutElement>();
            layout.minHeight = multiline ? 88f : 36f;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(bg.transform, false);
            var textRt = (RectTransform)textGo.transform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(10f, 6f);
            textRt.offsetMax = new Vector2(-10f, -6f);
            var text = textGo.AddComponent<Text>();
            text.font = theme.Font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = theme.InputTextColor;
            text.fontSize = 14;
            text.alignment = multiline ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft;
            text.supportRichText = false;

            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(bg.transform, false);
            var phRt = (RectTransform)placeholderGo.transform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(10f, 6f);
            phRt.offsetMax = new Vector2(-10f, -6f);
            var placeholder = placeholderGo.AddComponent<Text>();
            placeholder.font = text.font;
            placeholder.color = new Color(theme.InputTextColor.r, theme.InputTextColor.g, theme.InputTextColor.b, 0.45f);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.fontSize = 14;
            placeholder.alignment = text.alignment;
            placeholder.text = field.Placeholder ?? "";

            var input = bg.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = multiline ? InputField.LineType.MultiLineNewline : InputField.LineType.SingleLine;
            input.targetGraphic = bg.GetComponent<Image>();
            input.onValueChanged.AddListener(_ => widget.SetError(false));
            widget.Input = input;

            return widget;
        }

        // ---- Pointer capture --------------------------------------------

        /// <summary>
        /// Disable every enabled raycaster that isn't part of the form so
        /// no click can land behind it. BaseRaycaster covers UGUI
        /// (GraphicRaycaster), event-system world clicks (PhysicsRaycaster /
        /// Physics2DRaycaster), and UI Toolkit runtime panels
        /// (PanelRaycaster). Returns the list to hand back to
        /// <see cref="RestoreRaycasters"/>.
        /// </summary>
        private static List<BaseRaycaster> SuppressOtherRaycasters(GameObject formRoot)
        {
            var suppressed = new List<BaseRaycaster>();
#if UNITY_2022_2_OR_NEWER
            var all = Object.FindObjectsByType<BaseRaycaster>(FindObjectsSortMode.None);
#else
            var all = Object.FindObjectsOfType<BaseRaycaster>();
#endif
            foreach (var rc in all)
            {
                if (rc == null || !rc.enabled) continue;
                if (rc.transform.IsChildOf(formRoot.transform)) continue;
                rc.enabled = false;
                suppressed.Add(rc);
            }
            return suppressed;
        }

        private static void RestoreRaycasters(List<BaseRaycaster>? suppressed)
        {
            if (suppressed == null) return;
            foreach (var rc in suppressed)
            {
                // A raycaster destroyed while the form was up (scene change,
                // despawned UI) is just skipped.
                if (rc != null) rc.enabled = true;
            }
        }

        // ---- Hover affordances ------------------------------------------

        // Decoded once from the embedded PNG, then reused for every form.
        // Re-decoded if the display scale bucket changes (window moved to a
        // denser monitor).
        private static Texture2D? _handCursorTex;
        private static int _handCursorScale;
        private static bool _handCursorLoadFailed;

        /// <summary>
        /// The embedded hand-cursor art, upscaled to match the display.
        /// Software cursors render in raw screen pixels, so the 18x20 art
        /// is near-invisible on retina / 4K. Scale it nearest-neighbor
        /// (keeps the pixel-art edges crisp) by the screen-height bucket.
        /// </summary>
        private static Texture2D? GetHandCursorTexture(out Vector2 hotspot)
        {
            int scale = Mathf.Clamp(Mathf.RoundToInt(Screen.height / 1080f), 1, 4);
            hotspot = new Vector2(
                PlayloopHandCursor.HotspotX * scale,
                PlayloopHandCursor.HotspotY * scale);
            if (_handCursorTex != null && _handCursorScale == scale) return _handCursorTex;
            if (_handCursorLoadFailed) return null;
            try
            {
                var bytes = System.Convert.FromBase64String(PlayloopHandCursor.PngBase64);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                };
                if (!tex.LoadImage(bytes))
                {
                    _handCursorLoadFailed = true;
                    return null;
                }
                if (scale > 1)
                {
                    var src = tex;
                    var srcPx = src.GetPixels32();
                    int w = src.width, h = src.height;
                    var dst = new Texture2D(w * scale, h * scale, TextureFormat.RGBA32, false)
                    {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Point,
                    };
                    var dstPx = new Color32[w * scale * h * scale];
                    for (int y = 0; y < h * scale; y++)
                    {
                        int srcRow = (y / scale) * w;
                        int dstRow = y * w * scale;
                        for (int x = 0; x < w * scale; x++)
                        {
                            dstPx[dstRow + x] = srcPx[srcRow + x / scale];
                        }
                    }
                    dst.SetPixels32(dstPx);
                    dst.Apply(false, false);
                    Object.Destroy(src);
                    tex = dst;
                }
                if (_handCursorTex != null) Object.Destroy(_handCursorTex);
                _handCursorTex = tex;
                _handCursorScale = scale;
                return _handCursorTex;
            }
            catch
            {
                _handCursorLoadFailed = true;
                return null;
            }
        }

        /// <summary>
        /// Attach the pointer-hover affordance to a clickable: swaps in the
        /// hand cursor while hovered, and optionally tints a target graphic
        /// (used where the Selectable's own highlight tint is invisible,
        /// e.g. the transparent close-button background).
        /// </summary>
        private static void MakeHoverable(
            GameObject go, FeedbackFormTheme theme,
            Graphic? tint = null, Color? hoverColor = null)
        {
            var h = go.AddComponent<HoverAffordance>();
            h.UseHandCursor = theme.UseHandCursor;
            // A theme-supplied cursor wins over the embedded one and brings
            // its own hotspot.
            if (theme.HandCursor != null)
            {
                h.CustomCursor = theme.HandCursor;
                h.CustomHotspot = theme.HandCursorHotspot;
            }
            h.Tint = tint;
            if (tint != null && hoverColor.HasValue)
            {
                h.NormalColor = tint.color;
                h.HoverColor = hoverColor.Value;
            }
        }

        /// <summary>
        /// Pointer-hover feedback for the form's clickables. The cursor swap
        /// uses <see cref="CursorMode.ForceSoftware"/> because the texture is
        /// decoded at runtime (hardware cursors require importer settings the
        /// SDK doesn't ship). The cursor is restored on exit and, defensively,
        /// on disable so a form destroyed mid-hover never strands the hand.
        /// </summary>
        private sealed class HoverAffordance : MonoBehaviour,
            IPointerEnterHandler, IPointerExitHandler
        {
            public bool UseHandCursor;
            public Texture2D? CustomCursor;
            public Vector2 CustomHotspot;
            public Graphic? Tint;
            public Color NormalColor;
            public Color HoverColor;

            private bool _cursorSet;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (UseHandCursor)
                {
                    Texture2D? tex;
                    Vector2 hotspot;
                    if (CustomCursor != null)
                    {
                        tex = CustomCursor;
                        hotspot = CustomHotspot;
                    }
                    else
                    {
                        tex = GetHandCursorTexture(out hotspot);
                    }
                    if (tex != null)
                    {
                        Cursor.SetCursor(tex, hotspot, CursorMode.ForceSoftware);
                        _cursorSet = true;
                    }
                }
                if (Tint != null) Tint.color = HoverColor;
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                ResetState();
            }

            private void OnDisable()
            {
                ResetState();
            }

            private void ResetState()
            {
                if (_cursorSet)
                {
                    Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
                    _cursorSet = false;
                }
                if (Tint != null) Tint.color = NormalColor;
            }
        }

        // ---- Dismiss affordances ----------------------------------------

        /// <summary>
        /// Small "x" button floating in the panel's top-right corner. Same
        /// outcome as Cancel, for players whose pointer is already at the
        /// top of the form. Uses U+00D7 (multiplication sign), which the
        /// built-in legacy font reliably includes.
        /// </summary>
        private static void AddCloseButton(
            Transform parent, FeedbackFormTheme theme, System.Action onClose)
        {
            var go = AddImage(parent, "Close", new Color(0f, 0f, 0f, 0f));
            var le = go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-8f, -8f);
            rt.sizeDelta = new Vector2(36f, 36f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            btn.onClick.AddListener(() => onClose());
            AddCenteredLabel(go.transform, "×", theme);
            var text = go.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.fontSize = 22;
                text.color = new Color(
                    theme.LabelColor.r, theme.LabelColor.g, theme.LabelColor.b, 0.8f);
            }
            // The button bg is transparent, so the Selectable's own highlight
            // tint is invisible. Tint it explicitly on hover instead.
            MakeHoverable(go, theme,
                tint: go.GetComponent<Image>(),
                hoverColor: new Color(1f, 1f, 1f, 0.10f));
        }

        /// <summary>
        /// Per-frame Escape poll for the open form, working under either
        /// input backend: the legacy Input Manager via
        /// <see cref="UnityEngine.Input"/>, and the new Input System via
        /// reflection (so the SDK takes no hard dependency on
        /// <c>com.unity.inputsystem</c>). When the project enables "Both",
        /// either path may report the press. Dismiss is idempotent.
        /// </summary>
        private sealed class EscapeDismissBehaviour : MonoBehaviour
        {
            public System.Action? OnEscape;

            private void Update()
            {
                if (EscapePressedThisFrame()) OnEscape?.Invoke();
            }

            private static bool EscapePressedThisFrame()
            {
#if ENABLE_INPUT_SYSTEM
                if (EscapePressedNewInput()) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
                if (Input.GetKeyDown(KeyCode.Escape)) return true;
#endif
                return false;
            }

#if ENABLE_INPUT_SYSTEM
            // Reflection handles cached after the first lookup; the per-frame
            // cost is two property reads.
            private static System.Reflection.PropertyInfo? _kbCurrent;
            private static System.Reflection.PropertyInfo? _kbEscapeKey;
            private static System.Reflection.PropertyInfo? _keyWasPressed;
            private static bool _reflectionFailed;

            private static bool EscapePressedNewInput()
            {
                if (_reflectionFailed) return false;
                try
                {
                    if (_kbCurrent == null)
                    {
                        var kbType = System.Type.GetType(
                            "UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                        _kbCurrent = kbType?.GetProperty("current");
                        _kbEscapeKey = kbType?.GetProperty("escapeKey");
                        if (_kbCurrent == null || _kbEscapeKey == null)
                        {
                            _reflectionFailed = true;
                            return false;
                        }
                    }
                    var keyboard = _kbCurrent.GetValue(null);
                    if (keyboard == null) return false;
                    var key = _kbEscapeKey.GetValue(keyboard);
                    if (key == null) return false;
                    if (_keyWasPressed == null)
                    {
                        _keyWasPressed = key.GetType().GetProperty("wasPressedThisFrame");
                        if (_keyWasPressed == null)
                        {
                            _reflectionFailed = true;
                            return false;
                        }
                    }
                    return (bool)_keyWasPressed.GetValue(key);
                }
                catch
                {
                    _reflectionFailed = true;
                    return false;
                }
            }
#endif
        }

        // ---- Layout helpers ---------------------------------------------

        /// <summary>
        /// Add a UI input module compatible with the consuming project's
        /// active input backend. The legacy <see cref="StandaloneInputModule"/>
        /// receives NO input when the project runs the new Input System
        /// exclusively (Active Input Handling = "Input System Package"), which
        /// leaves the form rendered but unclickable / untypeable. When the new
        /// Input System is active we add its <c>InputSystemUIInputModule</c>
        /// instead, resolved by reflection so the SDK never takes a hard
        /// dependency on the <c>com.unity.inputsystem</c> package. Legacy-only
        /// projects (and "Both") fall back to / are covered correctly.
        /// </summary>
        private static void AddCompatibleInputModule(GameObject es)
        {
#if ENABLE_INPUT_SYSTEM
            // New Input System backend is enabled (alone or alongside legacy).
            // Its module supersedes StandaloneInputModule and works under both.
            var moduleType = System.Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (moduleType != null)
            {
                es.AddComponent(moduleType);
                return;
            }
            // ENABLE_INPUT_SYSTEM set but type unresolved (unexpected), fall
            // through to the legacy module so the form is at least functional
            // where the legacy backend is also enabled.
#endif
            es.AddComponent<StandaloneInputModule>();
        }

        private static GameObject AddImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return go;
        }

        private static void Stretch(GameObject go)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void AddLabel(
            Transform parent, string content, FeedbackFormTheme theme,
            bool isHeading = false, bool isHelp = false)
        {
            var go = new GameObject(isHeading ? "Heading" : (isHelp ? "Help" : "Label"),
                typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = theme.Font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = content;
            t.color = isHeading ? theme.HeadingColor : (isHelp ? new Color(theme.LabelColor.r, theme.LabelColor.g, theme.LabelColor.b, 0.7f) : theme.LabelColor);
            t.fontSize = isHeading ? 20 : (isHelp ? 12 : 14);
            t.fontStyle = isHeading ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            var le = go.AddComponent<LayoutElement>();
            // Text supplies its wrapped preferred height; this only sets the minimum.
            le.minHeight = isHeading ? 28f : (isHelp ? 18f : 20f);
        }

        /// <summary>Marketing site the branding badge links to.</summary>
        private const string BrandingUrl = "https://playloop.gg";

        // Decoded once from the embedded PNG, then reused for every form.
        private static Sprite? _logoSprite;
        private static bool _logoLoadFailed;

        /// <summary>
        /// Lazily decode the embedded brand mark into a Sprite. Returns null
        /// (and the badge falls back to text-only) if decoding ever fails.
        /// </summary>
        private static Sprite? GetLogoSprite()
        {
            if (_logoSprite != null) return _logoSprite;
            if (_logoLoadFailed) return null;
            try
            {
                var bytes = System.Convert.FromBase64String(PlayloopBrandingLogo.PngBase64);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!tex.LoadImage(bytes))
                {
                    _logoLoadFailed = true;
                    return null;
                }
                _logoSprite = Sprite.Create(
                    tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                return _logoSprite;
            }
            catch
            {
                _logoLoadFailed = true;
                return null;
            }
        }

        /// <summary>
        /// Clickable "[mark] Powered by Playloop" footer badge: the logo and
        /// the text share one hit area that opens <see cref="BrandingUrl"/> in
        /// the player's browser. Falls back to text-only if the mark can't be
        /// decoded.
        /// </summary>
        private static void AddBrandingBadge(Transform parent, FeedbackFormTheme theme)
        {
            var row = new GameObject("PlayloopBranding", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            // Fully-transparent image as the row's raycast target so a click
            // anywhere across the logo + text triggers the link (the children
            // opt out of raycasts below).
            var hit = row.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f);
            hit.raycastTarget = true;
            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.padding = new RectOffset(0, 0, 4, 0);
            row.AddComponent<LayoutElement>().preferredHeight = 20f;
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = hit;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => Application.OpenURL(BrandingUrl));

            var sprite = GetLogoSprite();
            if (sprite != null)
            {
                var logoGo = new GameObject("Mark", typeof(RectTransform));
                logoGo.transform.SetParent(row.transform, false);
                var img = logoGo.AddComponent<Image>();
                img.sprite = sprite;
                img.preserveAspect = true;
                img.raycastTarget = false;
                var logoLe = logoGo.AddComponent<LayoutElement>();
                logoLe.preferredWidth = 18f;
                logoLe.preferredHeight = 18f;
            }

            var textGo = new GameObject("Label", typeof(RectTransform));
            textGo.transform.SetParent(row.transform, false);
            var t = textGo.AddComponent<Text>();
            t.font = theme.Font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = "Powered by Playloop";
            t.color = new Color(theme.LabelColor.r, theme.LabelColor.g, theme.LabelColor.b, 0.7f);
            t.fontSize = 12;
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            textGo.AddComponent<LayoutElement>().preferredHeight = 18f;

            // Brighten the label on hover (the row's hit-area image is fully
            // transparent, so the Selectable tint has nothing to show).
            MakeHoverable(row, theme,
                tint: t,
                hoverColor: new Color(
                    theme.LabelColor.r, theme.LabelColor.g, theme.LabelColor.b, 1f));
        }

        private static void AddCenteredLabel(Transform parent, string content, FeedbackFormTheme theme)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var t = go.AddComponent<Text>();
            t.font = theme.Font ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = content;
            t.color = theme.InputTextColor;
            t.fontSize = 14;
            t.alignment = TextAnchor.MiddleCenter;
            // Force-pass the click through to the parent Button so the
            // text doesn't eat the input.
            t.raycastTarget = false;
        }

        private static void BuildButton(
            Transform parent, string label, Color bg, Color labelColor,
            FeedbackFormTheme theme, System.Action onClick)
        {
            var go = AddImage(parent, label, bg);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 100f;
            le.preferredHeight = 36f;
            AddCenteredLabel(go.transform, label, theme);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            btn.onClick.AddListener(() => onClick?.Invoke());
            MakeHoverable(go, theme);
            // Tint the text via the centered label child colour by
            // walking the just-added Text component.
            var text = go.GetComponentInChildren<Text>();
            if (text != null) text.color = labelColor;
        }
    }
}
#endif
