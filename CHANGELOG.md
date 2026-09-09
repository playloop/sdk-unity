# Changelog

## 0.5.0 Git distribution

- Install from the public Git repository using the pinned v0.5.0 tag.
- Updated setup and release instructions for Git installation.


## [0.5.0] - 2026-05-23

Pre-launch version pin. All four Playloop SDKs (Unity, Godot, Python,
TypeScript) are locked at `0.5.0` until first public launch. This keeps
the manifest, the runtime `SDK_VERSION` constant, and the CHANGELOG
aligned so the dashboard's "shipped on" attribution stays accurate
during the remaining pre-launch iterations.

### Changed: ingest-key-only config, no more game slug / id (added 2026-06-24)

Setup is now just the ingest key. The SDK resolves your game (id, slug, name)
from the key, so the `GameId` and `GameSlug` options are removed from
`PlayloopOptions`, and the Game slug field is gone from the settings window.
A/B experiments, the per-event config fetch, and Tester Keys all resolve what
they need from the key automatically. Verify Connection in the settings window
now reports the resolved game name ("Connected to <name>"). The Tester Keys
methods no longer take a per-call `gameId`.

### Changed: Settings window rebuilt on UI Toolkit (added 2026-06-24)

The `Playloop > Settings…` window (`Editor/PlayloopStatusWindow.cs`) is
rebuilt on UI Toolkit (was IMGUI). It now carries the Playloop brand palette,
rounded inputs, accent buttons, and the integrated wordmark lockup in the
header, matching the Godot SDK's settings window and the marketing site. All
behavior is unchanged: status pill, Verify Connection, Send Test Event, masked
API key, environment plus custom-env dialog, auto-instrument toggles, quick
links, and verify-cache. New `Editor/PlayloopBrandingWordmark.cs` embeds the
wordmark PNG (base64), the same no-Resources pattern as the runtime brand mark.

### Removed: in-engine Event Config editor (added 2026-06-19)

Removed the `Playloop > Event Config…` editor window
(`Editor/PlayloopEventConfigWindow.cs`), which pulled and SAVED per-event
config to the server with a management key. Per-event config is now managed
only from the web dashboard at `playloop.gg/games/<slug>/settings/events`. The
SDK still reads the resolved config at runtime (the `sdkIgnore` /
`linkToSummary` / `hideFromAi` filters and crash settings are unchanged) via
`RefreshEventConfigAsync()`; only the in-editor *editing* surface is gone.

### WebGL telemetry fix - now fully automatic (added 2026-06-15)

WebGL builds previously sent a session-create POST and then nothing:
heartbeats and flushes never went out and the server dropped the session
at its online window (~2 min). Two independent single-threaded-WebGL
issues, now fixed in the SDK so **WebGL needs zero game-side code**:

- **`ConfigureAwait(false)` stranding (the hang).** Unity WebGL is
  single-threaded with no ThreadPool, so a `ConfigureAwait(false)`
  continuation is scheduled to a scheduler that never runs - the
  `UnityWebRequest` completes (the server sees the POST) but the C#
  continuation that processes the response and releases the flush lock
  never resumes, so `FlushAsync` (and the feedback submit) hang forever
  after the first call. Introduced `PlAwait.Continue` (a gated constant:
  capture the main-thread context on WebGL, keep `ConfigureAwait(false)`
  everywhere else) and applied it across the flush + HTTP + retry +
  feedback path (`TelemetryApi`, `HttpClient`, `RetryingHttpHandler`,
  `FeedbackApi`). Desktop/editor/mobile behavior is byte-for-byte
  unchanged.
- **No background drivers on WebGL.** `AutoBatch`'s `Task.Run` flush loop
  and `HeartbeatEmitter`'s `System.Threading.Timer` never run on
  single-threaded WebGL. Added `PlayloopWebGLDriver`, a hidden
  MonoBehaviour the `PlayloopClient` constructor spawns automatically on
  WebGL that pumps flush + heartbeat from `Update()` on the main thread.
- **Session-end deadlock guard.** The quit hook's `Task.Run(...).Wait()`
  would block the only browser thread; on WebGL it now flushes
  best-effort without blocking (heartbeats are the recovery point, and
  `Application.quitting` rarely fires on a tab close anyway).

Net effect: drop the SDK into a WebGL game, call the normal API
(`new PlayloopClient`, `StartSession`, optionally `AutoBatch`), and
telemetry + heartbeats + feedback work with no WebGL-specific code. See
`docs/WEBGL.md`.

### Default feedback form UI polish (added 2026-06-09)

- The form now scales against a 1920x1080 reference resolution, so the
  card keeps a consistent on-screen proportion at any window size
  instead of filling the screen on large displays.
- Close (×) button in the panel's top-right corner. Resolves as
  `Dismissed`, same as Cancel.
- Escape dismisses the form, under the legacy Input Manager, the new
  Input System package, or "Both" (the Input System path is resolved
  by reflection; no package dependency added).
- Clickable elements show a pointing-hand cursor on hover; the close
  button and branding badge also get a hover highlight. Opt out with
  `FeedbackFormTheme.UseHandCursor = false`, or supply your own via
  `FeedbackFormTheme.HandCursor` + `HandCursorHotspot`. The built-in
  cursor scales to the display (2x/3x on dense screens) so it stays a
  normal cursor size everywhere.
- `FeedbackFormTheme.CancelLabel` / `SendLabel` make the button text
  overridable, so the whole form (fields via your `FeedbackField`
  labels, chrome via the theme) can render in the player's language.
- Pixel-perfect rendering: form elements snap to whole pixels so text
  stays crisp at any window size (fractional canvas scale factors were
  landing glyphs on sub-pixel positions and blurring them).
- The form holds full pointer capture while open: every other raycaster
  in the scene (game UI, physics-driven world clicks, UI Toolkit
  panels) is disabled and restored on close, so nothing behind the
  form is clickable. Opt out with
  `FeedbackFormTheme.BlockGameRaycasts = false`.
- New static input-gating surface: `FeedbackForm.IsOpen` plus
  `FeedbackForm.Opened` / `FeedbackForm.Closed` events. The form's
  backdrop blocks UI clicks behind it, but input read by polling
  (mouse-button polling, Physics raycasts, Input System actions)
  bypasses the UI event system entirely; use the flag or the events to
  gate gameplay input (or pause via `Time.timeScale`) while the form
  is up.

### Cross-game player identity (added 2026-05-26)

The SDK now sends an extra triple of identity fields on the
session-create flush (`vendorId`, `linkedId`, `consentLevel`) so the
server-side resolver can stitch a player's identity across multiple
games in a studio's library.

- `PlayloopSdk.SetLinkedId(string)`. Set the player's optional caller-
  supplied linked id (typically an email or account id). Persisted in
  PlayerPrefs across sessions. Pass `null` to clear.
- `PlayloopSdk.SetConsent(level)`. Set the player's tracking preference.
  Allowed values: `"anonymous"`, `"studio-wide"`, `"cross-platform"`,
  `"opt-out"`. Persisted in PlayerPrefs. Default `"anonymous"`.
- `PlayloopSdk.GetConsent()`. Read the persisted consent level.
- `Playloop.Identity.VendorIdResolver`. Auto-detects platform vendor
  IDs. Steamworks via runtime reflection (no asmdef dependency on
  Steamworks); iOS IDFV via `UnityEngine.iOS.Device.vendorIdentifier`.
  Returns `null` on platforms where no vendor id exists.
- `Playloop.Identity.LinkedIdStore` + `ConsentStore`. PlayerPrefs-
  backed storage primitives the static facade delegates to.

The new fields are session-create-only (only sent on the first flush
of a launch) and optional on the server side. Existing deployments
without these calls keep working unchanged.

### A/B experiments (added 2026-05-29)

Read the player's server-resolved experiment variant in-game. Variant
assignment is decided server-side and is deterministic per device, so
every Playloop SDK agrees on which bucket a player lands in. Mirrors the
TypeScript SDK surface.

- `Playloop.Experiments.ExperimentsApi`, exposed as `client.Experiments`.
- `client.Experiments.VariantAsync(string id)` returns `Task<string>` (the
  variant key, or `null` when the experiment is unknown / not running /
  the first fetch failed). Never throws.
- `client.Experiments.Variant(string id)` is a synchronous fast-path that
  returns the cached value (or `null` on a cache miss) without fetching,
  for performance-critical paths where the caller knows the cache is warm.
- `client.Experiments.RefreshAsync()` re-pulls assignments on demand for
  long-running sessions.
- `PlayloopOptions.PrefetchExperiments` (default `false`) eagerly fetches
  assignments at construction instead of lazily on the first variant call.
- Resolved assignments tag the session-create flush as `experimentTags`;
  the server re-validates and persists only the matches.

Lazy fetch, session-scoped cache, refetch-once on an unknown id, and
last-good on network error follow the same contract as the other SDKs.
Requires `PlayloopOptions.GameId`; without it every variant resolves to
`null`.

### Player Feedback (added 2026-05-25)

Multi-field feedback forms. Free for every studio.

- `Playloop.Feedback.FeedbackApi` with `SubmitAsync(formId, sessionId, responses, ...)` for multi-field submissions to `POST /api/telemetry/feedback`.
- `Playloop.Feedback.FeedbackForm`, a programmatic UGUI default form. `client.FeedbackForm.OpenAsync(formId, sessionId)` builds a Canvas + VerticalLayoutGroup at runtime (no prefab asset to maintain) and resolves when the player submits, cancels, or the cancellation token fires.
- `FeedbackFieldKinds` constants: `Rating1To5`, `ShortText`, `LongText`, `YesNo`. `long-text` is new to the multi-field path.
- `FeedbackFormTheme` for color / font overrides on the default UI.
- `DefaultFeedbackFields.Snapshot()` matches the dashboard's "New form" starter (1-5 rating + two text fields). Use it for a no-dashboard-setup integration.
- `client.SubmitFeedbackAsync(...)` top-level convenience wrapper.

## [0.1.0] - 2026-05-13

Initial release.

### Added
- `PlayloopClient` async surface: Sessions, Telemetry, Discord, Webhooks
- `IHttpHandler` abstraction with `UnityWebRequestHandler` (Unity) and `DefaultHttpHandler` (System.Net.Http for editor/server)
- Telemetry auto-batch via cancellable `Task.Delay` loop
- Static `PlayloopWebhooks.Verify` with HMAC-SHA256 + constant-time compare
- UPM manifest, `.asmdef` files, and Samples~/BasicUsage
