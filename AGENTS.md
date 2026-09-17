# AGENTS.md: Playloop SDK for Unity

Guidance for an AI coding agent (Cursor, Claude Code, Copilot, etc.) wiring the
Playloop Unity SDK into a game. This is the quick orientation; `README.md` has
the full reference. Code generated from this file should compile against the API
shapes below as-is.

## What this SDK is

Drop-in telemetry + AI playtest-insights for Unity games. Stream gameplay events
and session state, collect player feedback, run A/B experiments, and capture
crashes. Playloop turns the raw stream into per-tester and per-build insights.
C# (.NET Standard 2.1), Unity 2021.3 LTS through Unity 6, every platform
including WebGL. No NuGet: Newtonsoft.Json is pulled via UPM automatically.

## Install

Add the package to `Packages/manifest.json` (no Unity editor step required).
Add this line under `"dependencies"`:

```json
"gg.playloop.sdk": "https://github.com/playloop/sdk-unity.git#v0.5.0"
```

Unity resolves it on the next domain reload. (Equivalent GUI path: **Window →
Package Manager → + → Install package from git URL**.)

You need a **game ingest key** (`pl_ik_...`). Every game has its own; telemetry
sent with it is attributed to that game automatically, so there is **no game id
to configure**. Get one of these ways:

- **Ask the dev** to open the game on [playloop.gg](https://playloop.gg) →
  **Connections → Unity** and copy the key shown there.
- **Automate it** (if you have the Playloop MCP server or a management key): call
  the MCP `create_game` tool (backed by `POST /api/v1/games`, admin/owner role).
  It creates the game and returns a show-once ingest key to embed.

## Initialize

**Primary agent path: configure in code.** This compiles without any
in-editor step, so it's the reliable path for an agent. Construct one client
with the ingest key and keep a single reference:

```csharp
using Playloop;

var client = new PlayloopClient(new PlayloopOptions {
    ApiKey      = "pl_ik_...",          // required: the game's ingest key
    Environment = "dev",                // optional · default "dev"; "demo" / "production" / custom keeps data separate
});
```

There is **no `GameId` or `GameSlug` field** on `PlayloopOptions`. The SDK
resolves the game from the ingest key on its own. Setting those on the options
bag is a compile error (CS0117). Experiments, tester linking, and per-event
config all work with just the ingest key.

The ingest key is **write-only and safe to ship** in a build (it can only send
telemetry, never read or delete). For a **public** game repo, don't commit it:
read it from an env var or a git-ignored config instead, e.g.
`ApiKey = System.Environment.GetEnvironmentVariable("PLAYLOOP_INGEST_KEY")`.

**Alternative: settings asset (human-in-editor).** A designer can instead
right-click `Assets/Resources` → **Create → Playloop → Settings**, paste the key
into the Inspector, and load it with `PlayloopSettings.Load()` (never-raise;
returns a disabled client if the asset is missing) or
`PlayloopSettings.LoadOrThrow()` (strict). Note the `.asset` file **must** be
created in the Unity editor (a hand-written YAML asset will not load), so this
path needs a human at the editor. Prefer the code path when working headless.

Common `PlayloopOptions` fields: `ApiKey` (required ingest key), `BaseUrl`
(default `https://playloop.gg`), `Environment` (default `"dev"`), `SendInEditor`
(default `false`; see the verify note below), `DeviceId` (auto-resolved from
hardware if omitted), `TelemetryFlushIntervalMs` (default 5000),
`PrefetchExperiments`, `RetryAttempts`/`RetryBaseMs`/`RetryMaxMs`,
`AutoShutdownOnQuit` (default true), and the `AutoInstrument` sub-object. Full
table + the Inspector equivalents: `README.md`.

> **Use the Ingest key, not the Management key.** The Ingest key (`pl_ik_...`)
> is write-only and safe to ship in a build. The Management key (`pl_mgmt_...`,
> server-side only, reads/deletes data + Discord ingest) must never appear in a
> game binary.

> **Verifying in the editor?** Editor + development-build sends are **suppressed
> by default** (`SendInEditor = false`), so an editor playtest ships nothing and
> `/sessions` stays empty. For a verify pass, set `SendInEditor = true` and keep
> `Environment = "dev"` so the traffic is tagged and easy to filter out of real
> numbers. Revert `SendInEditor` to `false` before shipping.

## Telemetry basics

```csharp
client.Telemetry.StartSession(new Dictionary<string, object> {
    { "gameVersion", Application.version },
    { "platform",    Application.platform.ToString() },
});
client.Telemetry.AutoBatch();   // background flush loop, flushes every 5s

client.Telemetry.Track("unit_purchased", new Dictionary<string, object> {
    { "unit_id", "skeleton" }, { "cost", 50 },
});
```

`Track(name, payload)` is **synchronous**: it appends to an in-memory buffer; do
not `await` it. `AutoBatch()` also flips on useful-by-default auto-events
(scene changes, errors, idle, FPS drops) so a dropped-in SDK produces data with
no manual wiring; toggle each in the Settings asset or the `AutoInstrument`
options. Heartbeats, focus tracking, and a bounded flush on quit are handled for
you.

**Heatmaps**: emit an event named exactly `player_pos` with `{ x, y, room }`
(finite numbers + a stable room string) at 10–20Hz.

**Session state**: put resolution state (final score, did-they-finish) on
`client.State`; it rides every heartbeat and fires once at session end:

```csharp
client.State.SetState(new Dictionary<string, object?> { { "final_score", score } });
client.State.IncrementState(new Dictionary<string, object?> { { "kills_total", 1 } });
```

## Trace (sampled session state for Playback)

`client.Trace` samples where the player is 5 to 20 times a second and ships it as
`trace_chunk` events so Playback can draw the route and the per-level view can
show where sessions ended. Push the latest values; the SDK samples on its own
driver once `AutoBatch()` runs. Nothing takes an engine type:

```csharp
using Playloop.Trace;

client.Trace.DefineActions("move", "jump", "attack");            // bit i = labels[i], up to 16
client.Trace.SetRoom("crypt", new TraceBounds(20, 0, 40, 10));   // slug id, optional bounds
client.Trace.SetPosition(x, y, facingDeg);                       // every frame; facing -1 = unknown
client.Trace.SetInput(actionBits, axisX, axisY);                 // abstract mask + move vector
client.Trace.SetEntity("key", kx, ky);   client.Trace.ClearEntity("key");
client.Trace.Pause();  client.Trace.Resume();                    // cutscene, menu, bot run
client.Trace.End(TraceEndReason.Death);                          // Quit is sent for you at session end
```

Labels are lowercase slugs (`^[a-z][a-z0-9_]{0,23}$` for actions, room ids may
also carry `:` and `-`, up to 64 chars) and a bad one throws `ArgumentException`
even on a disabled client. **There is no overload that takes a `KeyCode`, an
`InputAction`, a button name, or a string**; do not add one. Sample rows are
eight numbers. `Trace.Status` explains why nothing is flowing (`OffByEnvironment`
in a `"production"` build under the default `TraceMode.Auto`, `OffByOption`,
`OffByConfig`, `Disabled`, `BudgetExhausted`, `Ended`). Options live on
`PlayloopOptions.Trace` (`Mode`, `Hz`, `Plane`, `MaxEntities`, `MaxBytesPerSession`).

**`PlayloopTrace` is the package's first Inspector-facing MonoBehaviour, on
purpose** (`Add Component → Playloop → Trace`: fields `target`, `roomId`,
`plane`, `facingFromVelocity`; call `Attach(client)`). Every other MonoBehaviour
in the SDK is an internal hidden helper; do not make this one hidden or
internal to match them. It feeds position and facing only; wire `SetInput`
from the game's own input code.

To verify the whole path, import the **Trace Fixture** sample (a tiny scripted
game that walks a known route and dies at (50, 5) in `vault`), keep
`SendInEditor` on, press Play, and open the session on the dashboard.

## Tester linking (Tester Keys)

If the studio distributes beta keys via Tester Keys, correlate each redeemed key
back to in-game sessions. The tester pastes their `pl_tt_...` claim token once.
No game id to set: the SDK resolves the game from the ingest key:

```csharp
await client.LinkTesterAsync(claimToken);          // bind this device
var tester = await client.GetCurrentTesterAsync();  // tester.Linked / tester.Handle
await client.UnlinkTesterAsync(claimToken);         // claimToken REQUIRED to unlink
```

All three are idempotent. Persist the `claimToken` locally at link time. You
need it to unlink. Handle `LinkAlreadyClaimedException` (token bound to another
device) with a "Switch tester?" affordance.

## Player feedback forms

Define a form on the dashboard at `/games/[slug]/feedback` (fields: `rating-1-5`,
`short-text`, `long-text`, `yes-no`; each field has a stable id `q1`, `q2`, …).
Two integration paths:

**Default UGUI form (drop-in)**: builds a Canvas at runtime, awaits the player,
submits for you. No prefab to wire:

```csharp
using Playloop.Feedback;

var result = await client.FeedbackForm.OpenAsync(
    formId:    "ff_xyz_from_dashboard",
    sessionId: currentSessionId);

if (result.Outcome == FeedbackFormOutcome.Submitted)
    Debug.Log($"Saved as {result.Result!.SubmissionId}");
// Other outcomes: Dismissed (cancel / × / Escape), Failed.
```

Pass a `FeedbackFormTheme` to restyle, or your own `FeedbackField[]` (with
localized labels) to render a custom field set. The form takes pointer capture
while open (`FeedbackForm.IsOpen`, `FeedbackForm.Opened` / `Closed` events let
you pause the game). Full theming + input-blocking notes: `README.md`.

**Custom UI (logic only)**: if you already have an overlay, submit directly:

```csharp
using Playloop.Feedback;

var result = await client.SubmitFeedbackAsync(
    formId:    "ff_xyz_from_dashboard",
    sessionId: currentSessionId,
    responses: new[] {
        new FeedbackResponseInput("q1", rating),
        new FeedbackResponseInput("q2", whatWorked),
    });
```

Feedback is **dev-triggered**: the SDK never auto-opens a form. Surface it from a
pause menu, level-complete moment, or back-to-menu handler. Capture answers
*before* calling `EndSession()`; never submit from a quit path (the request may
not flush). By default, the server enforces one submission per `(session, form)` and at most 3 per
session. Both surface as `PlayloopPlaytestException` with a `Reason`.

## A/B experiments

Works with just the ingest key (the SDK resolves the game from it). Read the
player's variant; assignment is deterministic server-side, so every Playloop SDK
agrees on the bucket:

```csharp
var variant = await client.Experiments.VariantAsync("exp_tutorial_v2");
if (variant == "treatment") ShowNewTutorial(); else ShowClassicTutorial();
```

`VariantAsync` never throws (returns `null` when there's no assignment).
`Variant("id")` is a synchronous cached read for hot paths; `PrefetchExperiments
= true` warms assignments at startup. The variant the player saw is tagged onto
the session automatically.

## Crash reports

The SDK installs an uncaught-exception trap on construction (managed C# only,
not native crashes). Crashes persist locally and flush on the next launch as a
`crash_reported` event, grouped by stack signature on the dashboard. Tune
`Crashes enabled` / `Recent events count` / `Sample rate` in the game's Settings.

## Webhooks

Verify HMAC signatures on incoming `session.analyzed` / `session.failed` events
(synchronous, no client needed):

```csharp
using Playloop.Webhooks;

WebhookEvent ev = PlayloopWebhooks.Verify(rawBody, signatureHeader, secret);
if (ev.Event == "session.analyzed")
    Debug.Log($"{ev.Insights!.Count} insights ready for {ev.SessionId}");
```

Configure the URL + secret at **Settings → Webhooks** on the dashboard.

## Using this with your coding agent

When wiring this SDK into a project, an agent should:

1. **Construct one client** at startup with the ingest key, in code
   (`new PlayloopClient(new PlayloopOptions { ApiKey = "pl_ik_..." })`), and keep
   a single reference. Don't build a client per scene. (A settings asset via
   `PlayloopSettings.Load()` is the human-in-editor alternative.)
2. **Call `AutoBatch()` once** after `StartSession(...)`. Don't hand-roll a flush
   or heartbeat loop; the SDK runs one (including on WebGL, no WebGL-specific
   code is needed).
3. **Treat `Track(...)` as fire-and-forget** (synchronous buffer append) and put
   end-state on `client.State`, not per-action events.
4. **Add a "Send feedback" entry** in the pause menu and on the main menu (not
   buried in options), opening `FeedbackForm.OpenAsync(formId, sessionId)`.
   Capture feedback before `EndSession()`, never from a quit handler.
5. **Don't set a game id.** There is no `GameId`/`GameSlug` option: experiments,
   tester linking, and per-event config all resolve the game from the ingest key.
6. **Never embed the Management key** in the game; only the ingest key ships.
7. **Feed the Trace from the movement code**, not from input handlers:
   `SetPosition` + `SetRoom` every frame (or the `PlayloopTrace` component),
   `SetInput` with the game's abstract action mask, `End(...)` on death or
   level complete. Never pass a key or button name anywhere near it.

## Where to go next

- **Full reference**: `README.md` in this package
- **Docs**: <https://playloop.gg/docs> · **All SDKs**: <https://playloop.gg/docs/sdks>
- **Dashboard**: <https://playloop.gg/dashboard> · **Live demo**: <https://playloop.gg/demo>

Sibling packages: `playloop` (Python), `@playloop/sdk` (TypeScript), `addons/playloop`
(Godot), `PlayloopSDK` (Unreal).

### Repeat voluntary feedback and safe retries

Forms default to one submission per form/session and three standard-form submissions per session. Enable **Allow repeat voluntary feedback** when creating a form for a player-opened feedback button. That form accepts new notes in the same session without consuming the standard-form allowance. It never schedules prompts.

Pass `requestId: savedRequestId` to reuse a request ID across calls. The SDK generates a fresh ID when omitted and keeps it unchanged during transport retries. Persist the ID with the original session ID, form ID, answers and timestamps until delivery is acknowledged. Cancellation or a lost response does not prove the server rejected the note. Retry the unchanged request to receive its original receipt. Changed answers or timestamps under the same ID return `idempotency_conflict` (409). A new note needs a new ID. Retain the draft on error; only an acknowledged receipt means delivered. Existing rate limits still apply.
