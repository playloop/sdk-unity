# Playloop™ SDK for Unity

Drop-in telemetry + AI playtest-insights SDK for Unity games. Stream events, get back structured insights, summaries by tester, and summaries by build, without a custom backend.

- **Language**: C# (.NET Standard 2.1)
- **Min Unity**: **2021.3 LTS**, tested through Unity 6
- **Targets**: every Unity platform including WebGL (`UnityWebRequest` under the hood)
- **No NuGet**: pulls `com.unity.nuget.newtonsoft-json` via UPM automatically

## Install

In Unity, open **Window → Package Manager → + → Install package from git URL** and paste:

```
https://github.com/playloop/sdk-unity.git#v0.5.0
```

UPM resolves dependencies. Then in the Project view, right-click `Assets/Resources` → **Create → Playloop → Settings**. The new `PlayloopSettings.asset` gives you an Inspector with checkboxes for every option (auto-instrument toggles, FPS threshold, flush interval, etc.). Paste your game's **Ingest key** and you're done. Each game has its own ingest key: open that game on [playloop.gg](https://playloop.gg), go to **Connections → Unity**, and copy the key shown there. Telemetry sent with that key is attributed to that game automatically, so you don't need to set a separate game id for events.

> **Use the Ingest key, not the Management key.** The Management key is server-side only. Never ship it in a game binary. See [API keys](#api-keys).

## First event

The shortest path uses the settings asset you just created:

```csharp
using System.Collections.Generic;
using Playloop;

// Loads Assets/Resources/PlayloopSettings.asset and converts it to PlayloopOptions.
// Never-raise: if the asset is missing (or its key is blank), you get a safe
// disabled client instead of an exception. See "Safe to construct" below.
var client = new PlayloopClient(PlayloopSettings.Load());
```

Or, if you'd rather build options in code (CI / tests / multi-environment switching):

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey      = "YOUR_INGEST_KEY",
    Environment = "demo",        // optional · default "dev"
});

// Device id auto-resolves via SystemInfo.deviceUniqueIdentifier.
// Pass deviceId explicitly only when you need to override.
client.Telemetry.StartSession(new Dictionary<string, object> {
    { "gameVersion", Application.version },
    { "platform",    Application.platform.ToString() },
});
client.Telemetry.AutoBatch();   // flushes every 5s

// Anywhere in your game:
client.Telemetry.Track("unit_purchased", new Dictionary<string, object> {
    { "unit_id", "skeleton" },
    { "cost",    50 },
});
```

That's the entire happy-path integration. The SDK takes care of batching, focus tracking, heartbeat, and bounded shutdown. Keep reading if you want to know exactly what.

## Auto-instrumentation

Calling `client.Telemetry.AutoBatch()` does more than start the 5s flush loop. It also flips on a set of "useful by default" events so a dropped-in SDK starts producing meaningful data inside 30 seconds, with **no manual `Track()` calls**.

| Event | Payload | Fires when |
| --- | --- | --- |
| `scene_changed` | `{ from, to }` | Unity's `SceneManager.activeSceneChanged` fires (initial scene → first real scene, scene transitions, etc.). |
| `application_error` | `{ message, stack, type }` | `Application.logMessageReceived` reports `Exception` / `Error` / `Assert`. Capped at 50 events per session so a stuck loop can't flood the budget. |
| `idle_start` | `{ idleAfterSec }` | 30s (desktop) / 60s (mobile) with no `Input.anyKey`, mouse movement, or touches. |
| `idle_end` | `{ idleDurationSec }` | Input resumes after an `idle_start`. |
| `fps_drop` | `{ medianFps, lowFps, windowSec }` | Rolling 3s median FPS falls below the threshold (default 30). Throttled to one event per 5 seconds. |

A sixth event, `memory_pressure { totalMb, allocatedMb }`, is off by default. Flip `AutoInstrument.Memory = true` to opt in. It fires when `GC.GetTotalMemory(false)` crosses 90% of `SystemInfo.systemMemorySize` (cooldown 30s) and can be noisy on low-memory devices.

### Opting out

The `PlayloopSettings` asset has a checkbox per auto-event under the **Auto-instrumentation** header. Flip whichever you don't want and save the asset. No code change.

If you'd rather configure in code, auto-events live on the `AutoInstrument` sub-object of `PlayloopOptions`:

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = {
        Scenes           = true,
        Errors           = true,
        Idle             = false,   // disable just idle tracking
        Fps              = true,
        Memory           = false,   // opt-in, off by default
        IdleThresholdSec = 0f,      // 0 → auto (30s desktop / 60s mobile)
        FpsDropThreshold = 30f,
    },
});
```

Setting every `AutoInstrument.*` flag to `false` reduces the SDK to "Track-what-you-call" behavior: same as the heartbeat-and-focus-tracking baseline before auto-instrumentation existed.

See `examples/AutoInstrument.md` for the full toggle reference + when each flag is worth turning off.

## Heatmaps

Playloop aggregates per-room heatmaps from any event named exactly `player_pos` with a payload of `{ x, y, room }`. Stream them from `FixedUpdate` or a 10–20Hz coroutine. That's enough cadence for clean maps without inflating event counts.

```csharp
client.Telemetry.Track("player_pos", new Dictionary<string, object> {
    { "x",    transform.position.x },
    { "y",    transform.position.z },   // top-down: use z for "y"
    { "room", currentRoom.id },
});
```

`x` and `y` must be finite numbers (world-space, any unit). `room` must be a non-empty string and stable across sessions for the same logical area. Events missing any field are silently dropped server-side.

## Retry policy

Every HTTP call the SDK makes (telemetry flushes, session ingests, webhook sends) is wrapped in a single retry layer that handles transient failures automatically. You almost never need to think about it; the defaults are tuned so a flaky network or a server hiccup costs the player nothing.

**Defaults:**

| Option | Default | Notes |
| --- | --- | --- |
| `RetryAttempts` | `3` | Total attempts including the first (so up to 2 retries). Set to `1` to disable retries entirely. |
| `RetryBaseMs` | `500` | Base delay between retries. Backoff grows as `RetryBaseMs * 2^(attempt-1)`. |
| `RetryMaxMs` | `5000` | Cap on the computed delay. `Retry-After` server hints are bounded at `2× RetryMaxMs`. |

**Retried automatically:** HTTP `429`, `500`, `502`, `503`, `504`, and network-level failures (DNS, connection refused, TLS handshake, timeout, Unity's `ConnectionError` / `DataProcessingError`).

**Never retried:** HTTP `4xx` *except* `429`. Those are surfaced immediately as a `PlayloopException`. Also `501` and `505`. Validation errors raised client-side never hit the wire.

**`Retry-After` header** (`429` and `503` responses): when present, the value supersedes the exponential formula. Integer-seconds and HTTP-date formats are both supported. The SDK caps the wait at `2× RetryMaxMs` so a hostile server can't pin the SDK on a 10-minute sleep.

The retry policy is **identical across all five official SDKs** (Unity, Unreal, Godot, Python, TypeScript), so behavior matches whatever you're used to from a sibling integration.

### When you might tune it

- **Hard real-time multiplayer titles**: set `RetryAttempts = 1` so a stuck request fails fast instead of stalling for ~1.5s during gameplay. Pair with your own queue if you still want eventual delivery.
- **High-rate ingest in a long offline session**: leave the defaults; the exponential backoff already prevents thundering-herd on reconnect.
- **Respecting a stricter `Retry-After` under heavy load**: tune `RetryBaseMs` / `RetryMaxMs` so backoff aligns with the `Retry-After` ceiling the API returns.

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey         = "YOUR_INGEST_KEY",
    RetryAttempts  = 3,     // default
    RetryBaseMs    = 500,   // default
    RetryMaxMs     = 5000,  // default
});
```

In the `PlayloopSettings` Inspector, the same three fields live under **Retry / backoff**. No code change required for the common cases.

## What you get for free

- **AutoBatch**: events queue in memory and flush every 5s. Configurable via `TelemetryFlushIntervalMs`.
- **Heartbeat**: a `session_heartbeat` event fires every 60s (configurable via `HeartbeatSec`; 30s floor, `0` disables) with `{ tickNumber, playTimeSec }` plus your latest state snapshot merged in. Powers the stale-session sweeper on the server side.
- **Focus tracking**: `focus_lost` / `focus_gained` events fire the instant the OS window changes focus. The server uses these to subtract idle time from the wall duration so you get an accurate "active play time" stat.
- **Bounded shutdown**: `Application.quitting` hooks a synchronous flush (3s timeout) so the final batch + `sessionEnded:true` signal lands before Unity tears down.
- **Stable device identity**: `SystemInfo.deviceUniqueIdentifier` is hardware-derived, so the same machine collapses to one tester even across rebuilds, `PlayerPrefs` resets, or bundle-identifier changes.

## Session state

The AI digest's first block is `## Session summary (end-of-session state)`. Anything you put on `client.State` lands there. Use it for resolution state (final score, did-they-finish, last room reached), not per-action events.

```csharp
client.State.SetState(new Dictionary<string, object?> {
    { "final_souls", playerSouls },
    { "void_pact", true },
    { "act2_reached", true },
    { "components", new[] { 50, 50, 50, 48, 24 } },
});
client.State.IncrementState(new Dictionary<string, object?> {
    { "kills_total", 1 },
});
```

The accumulator rides every heartbeat AND fires once as a `session_summary` event when the session ends. `Application.quitting` triggers the flush automatically (controlled by `PlayloopOptions.AutoShutdownOnQuit`, default true); `client.Dispose()` and explicit `await client.Telemetry.EndSessionAsync()` also flush.

**Your snapshots are read as a progression.** Because the accumulator rides every heartbeat, Playloop reads how your state *changed* over the session (e.g. `souls` 0 → 120k → 998k), not only where it ended. Keep calling `SetState` as the player progresses, not just at the end, and the analysis sees the whole arc rather than a single final number. If your game already fires its own recurring snapshot or end-of-session event under a different name, point Playloop at it in your game's Settings → Events (it auto-detects the common names).

**Reliability is best-effort, not guaranteed.** Clean shutdowns flush reliably; force-quits do not. Heartbeats carry the current snapshot so the server can fall back to the latest snapshot when no `session_summary` arrived, but anything set between the last heartbeat and the crash is lost. If a field MUST survive a crash, fire it as a normal `Telemetry.Track()` event in addition.

| Method | What it does |
|---|---|
| `State.SetState(partial)` | Merge `partial` into the accumulator. Last-writer-wins per key. `null` value deletes the key. |
| `State.IncrementState(partial)` | Numeric-add into the accumulator. Falls back to `SetState` semantics for non-numeric keys. |
| `State.Snapshot()` | Defensive copy of the current accumulator. Used by the heartbeat emitter. |
| `State.FlushSessionEnd()` | Force-flush the `session_summary` row now. Idempotent: already-flushed accumulators no-op until `ClearState()` resets. |
| `State.ClearState()` | Drop the buffer and reset the once-only flush guard. Useful for "new game" mid-session. |

## Trace

A Trace is sampled session state: where the player is, which room they are in, which abstract actions are held and which way they are moving, plus up to a few named entities, 5 to 20 times a second. You push the latest values; the SDK owns the clock, the packing, and the cadence (one chunk every five seconds, riding the normal telemetry batch as a `trace_chunk` event). That is what ships today. On the Playloop side, Playback reads those chunks as the route the player walked through the rooms, and the per-level view as every session's path with where each run ended, once those views are available.

```csharp
// Once, at startup. Bit i of the action mask is labels[i]; up to 16 labels.
client.Trace.DefineActions("move", "jump", "attack");

// When the player changes rooms. Bounds are optional and ride the chunk so Playback can frame the room.
client.Trace.SetRoom("crypt", new TraceBounds(20, 0, 40, 10));

// Every frame, from your own movement code.
client.Trace.SetPosition(player.x, player.y, facingDeg);      // facing -1 = unknown
client.Trace.SetInput(actionBits, moveAxis.x, moveAxis.y);    // the abstract mask + move vector
client.Trace.SetEntity("key", key.x, key.y);                  // named entities, up to 8
client.Trace.ClearEntity("key");                              // when one despawns

// Game-side gates: a cutscene, a menu, a bot run.
client.Trace.Pause();  client.Trace.Resume();

// How the run ended. A session can hold many runs; each End closes one.
client.Trace.End(TraceEndReason.Death);

// The next run. Optional: the next SetPosition opens it too.
client.Trace.Begin();
```

Sampling runs on its own driver once you call `Telemetry.AutoBatch()`; there is nothing to tick. Nothing is sampled until your first `SetPosition`, `SetRoom`, `SetInput` or `SetEntity` call, so a game that never wires the Trace sends no `trace_chunk` and no `trace_state`. The `PlayloopTrace` component (**Add Component → Playloop → Trace**) feeds a Transform's position and heading for you: set `roomId`, pick the plane (`XY` for side-on and 2D, `XZ` for top-down and 3D), and hand it your client with `Attach(client)`. Action bits and axes stay your call, because only the game knows its verbs.

**Runs.** A session can hold many runs: an arcade game that respawns, a roguelite that starts over, a level select. `End(reason)` closes the current run: the partial chunk goes out with that run's `end` block (where it ended and why), and the Trace waits between runs, sampling nothing. The next run opens on `Begin()` or on your next `SetPosition`, starting a new chunk whose clock restarts at its first sample. `SetRoom`, `SetInput` and `SetEntity` between runs only update the latest values; they do not open a run. A second `End` between runs is ignored, so each run keeps the first reason it was given. Every chunk carries its run index (`seg`), so Playback can show each run's route and where it ended. When the session ends, a run still open ends with `Quit` for you; if the Trace is between runs, nothing more is sent. The `PlayloopTrace` component pushes a position every frame, so with it the next run opens on the frame after `End`; disable the component while the player is dead or in a menu if you want that time left out.

`Trace.Status` says why nothing is flowing: `Active`, `PausedByGame`, `OffByEnvironment`, `OffByOption`, `OffByConfig` (the dashboard's per-event config ignores `trace_chunk`, which is the server-side off switch), `Disabled` (no ingest key), `BudgetExhausted`, `BetweenRuns` (after `End`, before the next run opens), or `Ended` (the session ended; the Trace re-arms with the next session). When the Trace is off for the session, the SDK sends one `trace_state` event with the reason so the session page can say so instead of showing an empty frame.

**Where it is on.** `TraceOptions.Mode` defaults to `Auto`: on everywhere except a `"production"` environment (see [Per-game environments](#per-game-environments)), so development, playtest and demo builds carry it and a shipping release does not. Set `Mode = On` to keep it in production once you have disclosed it, or `Off` to turn it off everywhere. The environment is a slug your build sets, so this default is the SDK's, not the server's. With `SendInEditor` off, editor runs send nothing, like every other event.

**Budget.** Each session may send up to `MaxBytesPerSession` (default 2 MB) of Trace data, about 90 minutes at 10 Hz with no entities, or about 10 minutes at 20 Hz with 8 moving entities. At the budget the SDK sends a final chunk marked `budget` and stops; that chunk records where the Trace stopped and why, so the end of the data is never mistaken for the end of the session. Lower `Hz` or track fewer entities for long sessions.

### What the Trace sends

Every string on the wire is a label you declared (an action, a room id, an entity name), each a lowercase slug with a fixed cap; every sample is eight numbers. There is no overload that takes a key, a button, or free text, so keystrokes cannot reach the wire by construction. World coordinates only, rounded to two decimals; integer degrees; nothing is written to disk on the player's machine. It follows the same opt-out handling as the rest of telemetry.

The wording Playloop's own privacy notice uses, which you can quote or link from your store page's privacy field:

> Sampled gameplay state from games that enable Trace: player position and facing, the current room or level ID, abstract action flags and movement axes, and a small number of named in-game object positions, 5 to 20 times a second. Never keystrokes, text, screen, audio, or camera.

Link that item from your Steam privacy field (or the equivalent on your store) when you ship a build with the Trace on, the same way you disclose the rest of your telemetry.

## Discord ingest

Relay a Discord channel (optionally a thread) into a playtest session so the conversation folds into Playloop alongside your telemetry. The same surface exists in every Playloop SDK; in Unity it lives on `client.Discord`:

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = managementKey,        // pl_mgmt_... (server-side only)
    RelaySecret = relaySecret,     // from Connections > Discord on playloop.gg
});

var result = await client.Discord.IngestAsync(
    "123456789012345678",  // Discord channel id
    "my-game",             // game slug
    threadId: null);       // optional thread to scope ingestion to
```

The server opens one session per channel/thread window and appends to it while it stays open.

**This endpoint requires a management key, not the ingest key.** It's built for a trusted relay (a bot or server bridge), not a packaged player build. Set `ApiKey` to a `pl_mgmt_...` key in the process that calls `IngestAsync`, and keep that key off end-user machines (see [API keys](#api-keys)).

**It also requires your Discord relay secret.** Copy it from the Connections > Discord page on playloop.gg (it's shown once when generated; you can rotate it there any time). Pass it as the client-level `RelaySecret` option, or per call via `IngestAsync(..., relaySecret: "...")` if one process relays for more than one account (the per-call value wins). Like the management key, keep it server-side only. Calls without it are rejected.

## Event config (per-event SDK behavior)

Configure per-event SDK behavior from the Playloop dashboard at `playloop.gg/games/<slug>/settings/events`, and the SDK applies it at runtime. Three knobs per event:

- **`sdkIgnore`**: drop the event entirely. It never reaches the network, saving wire + ingest cost.
- **`linkToSummary`**: every fire merges the event's payload into `client.State`. The event still ships to the server normally; the state accumulator just gets a snapshot of its fields on top.
- **`hideFromAi`**: server-side AI digest exclusion. The event stays in the transcript but is omitted from AI findings.

It works with just your ingest key, no extra config:

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = ingestKey,
});
```

The SDK resolves your game from the ingest key and fetches the config on construction (fire-and-forget; events fired before the fetch settles flow through unchanged). Edit flags on the dashboard; production picks up changes on next game session.

To pull config changes mid-session in dev (e.g. after saving an edit on the dashboard):

```csharp
await client.RefreshEventConfigAsync();
```

If the key can't be resolved (offline, or a bad key), the SDK skips the fetch and applies no filtering.

## Crash reports

The SDK installs an uncaught-exception trap on construction. It hooks `Application.logMessageReceived` filtered to `LogType.Exception` / `LogType.Assert`. Crashes persist to `Application.persistentDataPath/playloop-pending-crashes.json` (the process is dying and can't ship live) and flush on the next session start as a `crash_reported` event. The Crashes tab on each game (`/games/<slug>/crashes`) groups crashes by stack signature so a single bug shows once with a count.

**Scope: managed C# only.** This trap captures uncaught C# exceptions and failed asserts. It does **not** capture native crashes: segfaults, crashes inside native plugins, IL2CPP native faults, and out-of-memory kills happen below the managed layer and won't produce a report. Delivery is on the player's next launch, not live, since the crashing process can't send. If you need native crash coverage (minidumps, native plugins), run a dedicated native crash reporter alongside Playloop and turn `Crashes enabled` off if you want to avoid double-reporting.

**Per-game settings** (Settings → Events on the dashboard):

- `Crashes enabled` (master switch, default on)
- `Recent events count` (last N events captured with each crash, default 5, range 0–50)
- `Sample rate` (fraction of crashes persisted, default 1.0)

**Resolving stacks** for the dashboard:

- **Editor sessions, Mono builds, Development Builds with Script Debugging on**: already include file + line. Nothing to do.
- **IL2CPP retail builds** (iOS, Android, Switch, desktop release): open **Playloop > Symbolicate Crashes…**, click **Fetch unresolved crashes**, then either let the heuristic auto-fill stacks that already look symbolic OR click **Resolve via shell tool…** per row to run your local symbolicator (`ndk-stack`, `atos`, Unity's Symbol Map Tool, anything on your PATH). The raw stack is piped to stdin; stdout becomes the resolved stack. Last-used command remembered per-platform in `EditorPrefs`. Click **Submit all ready** when done.

To re-install the trap manually (e.g. after toggling settings mid-session):

```csharp
PlayloopClient.Current.InstallCrashHandler();
```

**Slack / Discord pings on new crashes**: paste an incoming webhook URL under `Settings > Crash notifications` on the game. The first occurrence of any new stack signature pings the channel; deduped over 24 hours so a crash storm against the same bug pings once a day.

## Configuration

`PlayloopOptions` fields:

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `ApiKey` | `string` | - | Your Ingest key. A blank/missing key never throws; it builds a **disabled** client (every call no-ops). Read `IsEnabled`. See [Safe to construct](#safe-to-construct-never-raise). |
| `Environment` | `string` | `"dev"` | Environment slug (e.g. `dev`, `demo`, `production`). Auto-created on first use. Left at the default, it's [auto-derived from the build](#per-game-environments) (development build → `"dev"`, shipping release → `"production"`); any explicit non-default value wins. |
| `SendInEditor` | `bool` | `false` | When `false`, telemetry is suppressed in the editor + development builds so playtesting doesn't pollute real data (logs a one-time warning). Set `true` to send from the editor anyway. See [Editor & development builds](#editor--development-builds). |
| `DeviceId` | `string?` | auto-resolved | Pass to override the hardware ID. |
| `TelemetryFlushIntervalMs` | `int` | `5000` | Flush cadence for the background loop. |
| `PrefetchExperiments` | `bool` | `false` | Eagerly fetch experiment assignments at construction instead of lazily on the first `Experiments.VariantAsync()` call. |
| `RelaySecret` | `string?` | `null` | Discord relay secret, sent as the `x-playloop-secret` header on Discord ingest calls. Copy it from Connections > Discord on playloop.gg. Only needed by processes that relay Discord channels; keep it server-side. See [Discord ingest](#discord-ingest). |
| `RetryAttempts` | `int` | `3` | Total HTTP attempts including the first. Set to `1` to disable retries. See [Retry policy](#retry-policy). |
| `RetryBaseMs` | `int` | `500` | Base exponential-backoff delay in milliseconds. |
| `RetryMaxMs` | `int` | `5000` | Maximum exponential-backoff delay (cap) in milliseconds. |
| `AutoShutdownOnQuit` | `bool` | `true` | Set false to opt out of the `Application.quitting` hook. |
| `Trace` | `TraceOptions` | `Mode = Auto`, `Hz = 10`, `Plane = XY`, `MaxEntities = 8`, `MaxBytesPerSession = 2 MB` | Sampled session state for Playback. `Auto` is on everywhere except a `"production"` environment; `On` forces it on, `Off` turns it off. See [Trace](#trace). |

### Safe to construct (never-raise)

The client is **always safe to construct and call.** If you ship a build with a blank or missing ingest key, `new PlayloopClient(...)` does **not** throw. It returns a **disabled** client: every API is a harmless no-op. `Telemetry.Track` drops silently, `Experiments.Variant` / `VariantAsync` return the control default (`null`), the feedback form resolves as `Failed` without opening, session and ingest calls fail soft, and no heartbeat, auto-instrumentation, or crash trap is ever started. A single warning is logged so the misconfiguration is visible, not silent.

This means a forgotten key never crashes your game. Check `IsEnabled` when you want to detect it in code:

```csharp
var client = new PlayloopClient(PlayloopSettings.Load());
if (!client.IsEnabled)
{
    // No ingest key configured. The client is safe to keep calling
    // (everything no-ops); surface this in your own dev tooling if useful.
    Debug.Log("Playloop is running disabled - set your ingest key to enable it.");
}
```

`PlayloopSettings.Load()` follows the same rule: a missing `PlayloopSettings.asset` gives you a blank options bag (so the client comes up disabled) rather than an exception. Prefer it over `PlayloopSettings.LoadOrThrow()`, which is the opt-in strict variant that throws when the asset is missing (use it only when you want a hard fail-fast, e.g. a CI check).

Genuine caller bugs still throw as before: passing an empty event name to `Telemetry.Track("")`, or an empty `formId` to a feedback submit, is a mistake at the call site, not a configuration problem, so it surfaces immediately even on a disabled client.

## Per-game environments

The `Environment` option becomes the `X-Playloop-Environment` header on every request. Use it to keep dev / demo / production data separate without juggling multiple accounts:

```csharp
new PlayloopOptions { Environment = "demo" }       // playtest builds
new PlayloopOptions { Environment = "production" } // Steam release
```

Environments auto-create the first time you send to them. Filter by env on every dashboard view.

**Auto-derive.** Leave `Environment` at its default (`"dev"`) and the SDK derives it from the build. A development build (the editor, a debug player, or a `DEVELOPMENT_BUILD`) stays `"dev"`, and a shipping release build is promoted to `"production"`. Set any explicit non-default slug (`"production"`, `"demo"`, etc.) to override the auto-derive completely.

## Editor & development builds

By default, telemetry is **suppressed while running in the Unity editor or a development build**, so your playtesting and local debug runs don't mix into real session data. The SDK logs a single explanatory warning the first time a send is suppressed.

To send telemetry from the editor anyway (say you're testing the ingest pipeline end to end), set `SendInEditor = true`, or tick **Send In Editor** on the `PlayloopSettings` asset. Pair it with `Environment = "dev"` so the editor traffic is tagged and trivial to filter out of your production numbers. Suppression is a no-op in a release player build: nothing is ever held back there.

Every session is also stamped with an `engine` (`"unity"`), an `engineVersion` (your Unity version), and an `isEditor` flag, so you can slice editor-originated sessions out on the dashboard.

## API keys

Two scopes, two homes. Use the right one for the right surface.

- **Ingest key** (per game). Write-only, authenticates `POST /api/telemetry` only. **This is the one to put in `PlayloopSettings.asset`.** Each game has its own ingest key, so telemetry sent with it is attributed to that game automatically (no separate game id needed for events). Find and rotate it on the game's setup page: open the game on [playloop.gg](https://playloop.gg), go to **Connections → Unity**, and copy the key shown there. A leaked ingest key can spam telemetry but can't read sessions or delete data, and it's rate-limited per key and per IP.
- **Management key** (per account). Full scope: reads sessions, deletes data, manages webhooks. Lives under **Settings → API keys** on [playloop.gg](https://playloop.gg). **Never ship it in a game binary** (a decompiled Unity build exposes any string in any `Resources/*.asset` file). Use it from server-side scripts only.

## Webhooks

> **Free on every account/tier.** Webhook delivery (configuring the URL + secret on the dashboard and receiving outbound `session.analyzed` / `session.failed` events) is available on every account/tier. The `PlayloopWebhooks.Verify(...)` helper below is just an HMAC verifier. It ships in every install but has nothing to validate until you configure a webhook URL + secret on the dashboard.

Verify HMAC signatures on incoming `session.analyzed` / `session.failed` events without rolling your own crypto. **Synchronous**: pure HMAC + JSON parse, no I/O.

```csharp
using Playloop.Webhooks;

WebhookEvent ev = PlayloopWebhooks.Verify(
    body:      rawJsonBody,
    signature: incomingSignatureHeader,
    secret:    webhookSecret
);
if (ev.Event == "session.analyzed") {
    Debug.Log($"{ev.Insights!.Count} insights ready for {ev.SessionId}");
}
```

Set up the webhook URL + secret from [Settings → Webhooks](https://playloop.gg/settings/webhooks).

## Linking a tester (Tester Keys correlation)

If your studio uses [Playloop Tester Keys](https://playloop.gg/docs/tester-keys/sdk-correlation) to distribute beta keys, the SDK can correlate each redeemed key back to in-game sessions. The tester pastes their `pl_tt_...` claim token into your game once, and from then on their sessions auto-tag with the handle they picked at redemption.

Minimum integration (4 lines in your main menu):

```csharp
var token = mainMenuInput.text;
if (!string.IsNullOrEmpty(token))
{
    await client.LinkTesterAsync(token);
}
```

Full UX: show who they're playing as + offer a "Switch tester" affordance. Persist the `claimToken` locally at link-time so you can pass it back on unlink (the server requires it, see the security note below):

```csharp
var tester = await client.GetCurrentTesterAsync();
if (tester.Linked)
{
    label.text = $"Playing as {tester.Handle}";
    switchTesterButton.onClick.AddListener(async () =>
    {
        await client.UnlinkTesterAsync(storedClaimToken);
        ReloadMenu();
    });
}
else if (!string.IsNullOrEmpty(token))
{
    await client.LinkTesterAsync(token);
    // ↑ remember `token` locally; you'll need it for UnlinkTesterAsync later.
}
```

No game id to set. The SDK resolves your game from the ingest key, so Tester Keys calls just work with your key:

```csharp
var client = new PlayloopClient(new PlayloopOptions
{
    ApiKey = "pl_ik_...",
});
```

Errors:
- `LinkAlreadyClaimedException`: token is already bound to a DIFFERENT device. Surface a "Switch tester?" UX; the tester would need a fresh invite OR to unlink the original device first.
- `PlayloopPlaytestException(Reason="unknown")` (404). Uniform rejection: bad token / wrong game / wrong device. The audit log distinguishes them server-side; the wire response is collapsed to deny cross-studio inventory enumeration.

All three methods are idempotent: re-running `LinkTesterAsync` from the same device with the same token returns `AlreadyLinked = true`; `GetCurrentTesterAsync` is safe to call on every menu render; `UnlinkTesterAsync` returns `Unlinked = false` when there was nothing to unlink (after the claim-token authorization passes).

**Security note:** `UnlinkTesterAsync` requires the original `claimToken`. Without it, knowing the device id from an exported CSV or audit row would let a griefer break attribution.

## Player Feedback

Player Feedback is free for every studio. Both authoring forms on the dashboard AND the runtime calls below: no plan-based throttling at the SDK layer.

Define a form in the studio dashboard at `/games/[slug]/feedback`. One form can have multiple fields. Supported field kinds:

- `rating-1-5`: a 1 to 5 scale.
- `short-text`: single-line string.
- `long-text`: multi-line string.
- `yes-no`: binary choice.

Each field gets a stable id (`q1`, `q2`, ...). Your submission matches answers to fields by that id.

### Default UGUI form (drop-in)

The fastest path: open the SDK's built-in form. It builds a Canvas at runtime, awaits the player, then calls the submit API for you. No prefab to wire, no scene authoring.

```csharp
using Playloop.Feedback;

var result = await client.FeedbackForm.OpenAsync(
    formId: "ff_xyz_from_dashboard",
    sessionId: currentSessionId);

if (result.Outcome == FeedbackFormOutcome.Submitted)
{
    Debug.Log($"Saved as {result.Result!.SubmissionId}");
}
```

Pass a `FeedbackFormTheme` to override colors, or your own `FeedbackField[]` to render a custom field set. All player-visible text is overridable for localization: field labels and placeholders come from your `FeedbackField` entries, and the form chrome comes from the theme (`Title`, `CancelLabel`, `SendLabel`). Pass strings from your game's localization system and the form renders in the player's language. Responses are stored by field id, so client-side localization never affects how submissions map on the dashboard. The form sits on its own top-most Canvas (`sortingOrder = 32000`) so it never fights your game UI, and scales against a 1080p reference resolution so the card keeps the same on-screen proportion at any window size.

Players can dismiss the form three ways: the Cancel button, the close (×) button in the top-right corner, or the Escape key. All three resolve as `FeedbackFormOutcome.Dismissed`. Escape works under the legacy Input Manager, the new Input System package, or "Both". Note the SDK can't consume the key press, so if your game also acts on Escape (for example toggling a pause menu), check `result.Outcome` before reacting to the same press.

Clickable elements (buttons, rating cells, the close button, the branding badge) show a pointing-hand cursor on hover. Three theme knobs control it:

```csharp
var theme = new FeedbackFormTheme
{
    UseHandCursor = false,                 // turn it off entirely
    // or replace it with your own:
    HandCursor = myCursorTexture,          // read-enabled Texture2D
    HandCursorHotspot = new Vector2(4, 0), // click point, from top-left
};
```

#### Blocking game input while the form is open

While the form is open it holds full pointer capture: every other raycaster in the scene (game UI canvases, `PhysicsRaycaster`/`Physics2DRaycaster` world-object clicks, UI Toolkit panels) is disabled and restored when the form closes, so nothing behind the form is clickable. Set `FeedbackFormTheme.BlockGameRaycasts = false` if your game needs its raycasters live while the form is up.

The one thing no overlay can intercept is input your game reads by polling (`Input.GetMouseButtonDown`, manual `Physics.Raycast` from the camera in `Update`, Input System action callbacks). That input never touches a raycaster or the UI event system. If your game acts on raw clicks or keys, gate that input while the form is up:

```csharp
using Playloop.Feedback;

// Option A: check the flag in your input code
if (FeedbackForm.IsOpen) return;

// Option B: pause the game for the form's lifetime
FeedbackForm.Opened += () => Time.timeScale = 0f;
FeedbackForm.Closed += () => Time.timeScale = 1f;
```

`Opened` and `Closed` fire on the main thread. `Closed` fires for every outcome: submitted, dismissed, or failed.

### Custom UI (logic only)

If you already have a pause-menu overlay, skip the default form and call the submit API directly:

```csharp
using Playloop.Feedback;

long askedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
// Your UI renders the form (rating + two text fields below)
// and resolves with the player's answers.
var result = await client.SubmitFeedbackAsync(
    formId: "ff_xyz_from_dashboard",
    sessionId: currentSessionId,
    responses: new[]
    {
        new FeedbackResponseInput("q1", rating),         // rating-1-5
        new FeedbackResponseInput("q2", whatWorked),     // short-text
        new FeedbackResponseInput("q3", whatDidnt),      // long-text
    },
    askedAtSec: askedAt,
    answeredAtSec: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

Debug.Log($"Saved as {result.SubmissionId}, {result.ResponseIds.Count} answers persisted.");
```

Every call lands as ONE submission (shared `SubmissionId`) with N answer rows. The dashboard reassembles them into a single card per submission on the tester detail page.

### Spam caps (anti-flood)

Standard feedback forms have two server-side limits per playtest session. These are the default; repeat voluntary forms opt out when created:

1. **By default, one submission per `(session, form)`**. Returns HTTP 409 with `reason="already_submitted"`.
2. **At most 3 submissions per session across standard forms**. Returns HTTP 429 with `reason="session_submission_limit"`.

Both surface as `PlayloopPlaytestException` with a `Reason` discriminator, so you can branch in C#:

```csharp
try
{
    await client.SubmitFeedbackAsync(formId, sessionId, responses);
}
catch (PlayloopPlaytestException ex) when (ex.Reason == "already_submitted")
{
    // Player already answered this form this session. Show "Thanks!" UI.
}
```

### When to show the form

Player Feedback is dev-triggered. The SDK never auto-opens the form. Common patterns:

- **Pause menu**: surface a "Send feedback" button.
- **Level complete / boss down**: render at the natural breath moment.
- **Back-to-menu handler**: capture answers, then call `EndSession()` so the submission flushes with the session boundary.

⚠️ Do not call `SubmitFeedbackAsync` from `Application.quitting` or an `OnDisable` quit path. The HTTP request may not flush before the process dies. If you want feedback on session end, capture it BEFORE you call `EndSession()`.

## Bug Reports

Let a player file a bug the moment they hit one. A report carries a **title** (1 to 200 chars), a **description** (1 to 8000 chars), and a **severity** (`low` / `medium` / `high` / `critical`, defaults to `medium`). Reports are stored in Playloop, show up on the game's dashboard, and route to your studio's connected issue tracker automatically.

Bug Reports is free for every studio. No plan-based throttling at the SDK layer.

### Default UGUI form (drop-in)

The fastest path: open the SDK's built-in form. It builds a Canvas at runtime, awaits the player, then calls the submit API for you. No prefab to wire, no scene authoring.

```csharp
using Playloop.BugReports;

var result = await client.BugReportForm.OpenAsync(
    sessionId: currentSessionId);

if (result.Outcome == BugReportFormOutcome.Submitted)
{
    Debug.Log($"Filed as {result.Result!.BugReportId} (routing: {result.Result.RoutingStatus})");
}
```

The form collects the title, description, and severity, and attaches the build version, environment, and platform automatically (the same values telemetry stamps), so a triager sees where the report came from without the player typing anything.

Pass a `BugReportFormTheme` to override colors, font, and all player-visible text for localization: the heading (`Title`), the field labels (`TitleFieldLabel`, `DescriptionFieldLabel`, `SeverityFieldLabel`), the placeholders, the four `SeverityLabels`, and the buttons (`CancelLabel`, `SubmitLabel`). The severity values sent to Playloop are always the fixed slugs, so client-side localization never affects how reports read on the dashboard. Like the feedback form, it sits on its own top-most Canvas (`sortingOrder = 32000`), scales against a 1080p reference, holds full pointer capture while open (toggle with `BugReportFormTheme.BlockGameRaycasts`), and dismisses via Cancel, the close (×) button, or Escape. Gate polled game input with `BugReportForm.IsOpen` or the `BugReportForm.Opened` / `BugReportForm.Closed` events, exactly as with the feedback form.

### Custom UI (logic only)

If you already have a pause-menu overlay, skip the default form and call the submit API directly:

```csharp
using Playloop.BugReports;

var result = await client.SubmitBugReportAsync(
    title: "Fell through the floor in room 3",
    description: "Walking into the north wall drops me out of the world.",
    severity: BugReportSeverities.High,
    sessionId: currentSessionId,
    context: new BugReportContext
    {
        // buildVersion / environment / platform auto-fill when omitted;
        // add any device fields a triager might want.
        Device = new Dictionary<string, object> { ["gpu"] = SystemInfo.graphicsDeviceName },
    });

Debug.Log($"Filed as {result.BugReportId}");
```

Each call auto-generates an idempotency key so a transport-level retry of the same submit is deduped server-side rather than filing a duplicate report. Pass your own `idempotencyKey` to control that (for example, to make a user-visible "resend" reuse the original).

### When to show it

Bug Reports is dev-triggered. The SDK never auto-opens the form. The recommended entry is a **"Report a bug" button in your pause menu**, so a player can file the instant something breaks, with a secondary entry on the **main menu** for between-session reports.

⚠️ Do not call `SubmitBugReportAsync` from `Application.quitting` or an `OnDisable` quit path. The HTTP request may not flush before the process dies.

## A/B experiments

Run experiments from the Playloop dashboard and read the player's variant in your game. Variant assignment is decided server-side and is deterministic per device, so every Playloop SDK agrees on which bucket a player lands in.

```csharp
var client = new PlayloopClient(new PlayloopOptions
{
    ApiKey = "pl_ik_...",
});

var variant = await client.Experiments.VariantAsync("exp_tutorial_v2");
if (variant == "treatment")
    ShowNewTutorial();
else
    ShowClassicTutorial(); // "control", or null if the experiment isn't running
```

`VariantAsync` returns a `Task<string?>` (the variant key, or `null` when there's no assignment) and **never throws**, so it's safe to call straight from gameplay code:

- **Lazy fetch.** Assignments are fetched on the first `VariantAsync` call, not at construction. Games that never run an experiment pay zero network cost.
- **Session-scoped cache.** The assignment map is cached for the lifetime of the client, so a player sees a stable variant for the whole playthrough.
- **Unknown experiment.** Calling `VariantAsync` for an id the SDK hasn't seen triggers exactly one re-fetch (in case you just created the experiment), then caches the answer. A brand-new experiment created mid-session is picked up on that re-fetch.
- **Offline-safe.** If the network is down, a previously-cached variant still answers. If the very first fetch fails, `VariantAsync` returns `null`.

The variant the player saw is tagged onto the session automatically: the first telemetry flush carries the assignments, and the dashboard's experiment detail page counts each session under its variant. The server re-checks every tag against its own answer before storing it, so the dashboard numbers always reflect the real assignment.

### Synchronous read (hot path)

On a performance-critical path where you already resolved the variant earlier, read it synchronously without an `await`. `Variant(...)` returns the cached value (or `null` on a cache miss) and never fetches:

```csharp
// Warm once at startup…
await client.Experiments.VariantAsync("exp_enemy_ai");

// …then read with no await in your Update loop:
var arm = client.Experiments.Variant("exp_enemy_ai"); // cached value or null
```

### Eager prefetch

If you gate the very first thing a player sees on a variant, warm the assignments at startup so the synchronous `Variant(...)` read is ready by the time you need it:

```csharp
var client = new PlayloopClient(new PlayloopOptions
{
    ApiKey = "pl_ik_...",
    PrefetchExperiments = true,
});
```

### Manual refresh

For long-running sessions, re-pull assignments on demand:

```csharp
await client.Experiments.RefreshAsync();
```

Experiments are scoped to the game your ingest key belongs to, resolved automatically from the key.

### Variant config values

A variant can carry an optional config payload: a flat map of primitive values (`string`, number, or `bool`) you set from the dashboard. Use it to tweak a tuning number, a feature flag, or a label per variant without shipping a new build. Read values through the typed getters, each of which takes a fallback:

```csharp
var cfg = await client.Experiments.GetConfigAsync("exp_tutorial_v2");
float enemyDamage = cfg?.GetFloat("enemy_damage", 10f) ?? 10f;
bool showHints = cfg?.GetBool("show_hints", false) ?? false;
string label = cfg?.GetString("cta_label", "Start") ?? "Start";
```

`GetConfigAsync` returns a `Task<VariantConfig?>` and never throws. It shares the same lazy fetch and session cache as `VariantAsync`, and returns `null` when the experiment isn't running or the assigned variant has no config. On a performance-critical path where the cache is already warm, `GetConfig(...)` reads it synchronously. Values are primitives only, by design: no arrays and no nested objects.

## Tests

Unity Test Runner picks up `Tests/Editor/*.cs` as NUnit fixtures. The same tests run outside Unity via `dotnet test`. The SDK uses `PLAYLOOP_DOTNET_STANDALONE` to swap `UnityWebRequest` for `System.Net.Http.HttpClient` so CI doesn't need a Unity install:

```bash
dotnet test Tests~/Editor/Playloop.Tests.csproj
```

## Where to go next

- **Full docs**: <https://playloop.gg/docs>
- **All SDKs**: <https://playloop.gg/docs/sdks>
- **Dashboard**: <https://playloop.gg/dashboard>
- **Live example** (no signup): <https://playloop.gg/demo>
- **Roadmap**: <https://playloop.gg/roadmap>
- **SDK troubleshooting**: <https://playloop.gg/docs/sdk/troubleshooting>

The Editor menu also includes a **Playloop > Docs** submenu (**Open Docs** and **Open Roadmap**) for quick access.

Sibling packages: `playloop` (Python), `@playloop/sdk` (TypeScript), `addons/playloop` (Godot), `PlayloopSDK` (Unreal plugin).

## License

MIT

### Repeat voluntary feedback and safe retries

Forms default to one submission per form/session and three standard-form submissions per session. Enable **Allow repeat voluntary feedback** when creating a form for a player-opened feedback button. That form accepts new notes in the same session without consuming the standard-form allowance. It never schedules prompts.

Pass `requestId: savedRequestId` to reuse a request ID across calls. The SDK generates a fresh ID when omitted and keeps it unchanged during transport retries. Persist the ID with the original session ID, form ID, answers and timestamps until delivery is acknowledged. Cancellation or a lost response does not prove the server rejected the note. Retry the unchanged request to receive its original receipt. Changed answers or timestamps under the same ID return `idempotency_conflict` (409). A new note needs a new ID. Retain the draft on error; only an acknowledged receipt means delivered. Existing rate limits still apply.

For a feedback-only client, set `EnableCrashReporting = false` before construction to prevent automatic crash capture and draining of saved crashes. The default is `true`. This option does not disable explicit game telemetry calls.
