# Auto-instrumentation

`client.Telemetry.AutoBatch()` enables five "useful by default" auto-events out of the box. Drop the SDK in, call `AutoBatch()`, and the dashboard starts filling up inside 30 seconds without a single `Track()` call you wrote.

| Event | Payload | Default | Fires when |
| --- | --- | --- | --- |
| `scene_changed` | `{ from, to }` | on | Unity's `SceneManager.activeSceneChanged` fires. |
| `application_error` | `{ message, stack, type }` | on | `Application.logMessageReceived` reports `Exception` / `Error` / `Assert`. Hard cap: 50 events / session. |
| `idle_start` | `{ idleAfterSec }` | on | 30s (desktop) / 60s (mobile) with no `Input.anyKey`, mouse movement, or touches. |
| `idle_end` | `{ idleDurationSec }` | on | Input resumes after an `idle_start`. |
| `fps_drop` | `{ medianFps, lowFps, windowSec }` | on | 3s rolling median FPS falls below threshold (default 30 FPS). Throttled to one event per 5 seconds. |
| `memory_pressure` | `{ totalMb, allocatedMb }` | **off** | `GC.GetTotalMemory(false)` crosses 90% of `SystemInfo.systemMemorySize`. Cooldown 30s. |

## Quickstart: full defaults

```csharp
using Playloop;

var client = new PlayloopClient(new PlayloopOptions {
    ApiKey  = "YOUR_INGEST_KEY",
    BaseUrl = "https://playloop.gg",
});
client.Telemetry.StartSession();
client.Telemetry.AutoBatch();   // five auto-events flip on here

// ... rest of your game. You don't need to wire scene/error/idle/fps tracking.
```

## Opt out of a specific event

Auto-events live on the `AutoInstrument` sub-object of `PlayloopOptions` so the five per-event flags + two thresholds don't clutter the rest of the API surface. Setting one to `false` only disables that event; the others keep firing.

```csharp
// Game is full-immersion and "idle" is a normal state (deck-builder waiting
// on the player to plan a turn). Turn idle tracking off.
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = { Idle = false },
});
```

```csharp
// Game throws a lot of expected exceptions during boot (third-party SDK
// initialization, retried network calls). Don't pollute the error stream.
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = { Errors = false },
});
```

```csharp
// Console / handheld build where FPS dips are part of the platform reality
// and not actionable from a playtest standpoint.
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = { Fps = false },
});
```

## Tune the thresholds

Defaults are tuned for the median game; tighten or loosen as needed.

```csharp
new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",

    AutoInstrument = {
        // 60 FPS target: flag drops below 50 instead of below 30.
        FpsDropThreshold = 50f,

        // VN / clicker: idle threshold of 2 minutes instead of 30s.
        IdleThresholdSec = 120f,
    },
}
```

Leaving `AutoInstrument.IdleThresholdSec = 0` (the default) auto-resolves to 30s on desktop and 60s on mobile via `Application.isMobilePlatform`.

## Opt in to memory_pressure

`memory_pressure` is off by default because it's noisy on low-memory mobile devices (where a 90%-of-RAM allocation is routine, not an emergency). Flip it on when you want to track allocation spikes on a desktop build or you're shipping to a homogeneous device pool.

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = { Memory = true },
});
```

## Disable everything (legacy-equivalent mode)

```csharp
var client = new PlayloopClient(new PlayloopOptions {
    ApiKey = "YOUR_INGEST_KEY",
    AutoInstrument = new AutoInstrumentOptions {
        Scenes = false,
        Errors = false,
        Idle   = false,
        Fps    = false,
        Memory = false,
    },
});
```

With all five flags off, `AutoBatch()` reverts to the pre-auto-instrumentation baseline: the flush loop, the heartbeat, focus tracking, and bounded shutdown still run: only manual `Track()` calls produce game-specific events.

## Notes on cost + budgets

- `application_error` is hard-capped at 50 events per session so a stuck exception loop can't blow your event budget. Past 50, errors are dropped silently for the rest of the session.
- `fps_drop` is throttled to one event every 5 seconds. A 60-second slow patch produces ~12 events, not 1800.
- `memory_pressure` has a 30-second cooldown for the same reason.
- `idle_start` / `idle_end` are paired and rare by design. They should never produce more than one pair per (idle duration + active span). No throttle is needed.
- `scene_changed` is bounded by your scene count. Every transition fires one event.

## Implementation pointer

The MonoBehaviour glue lives at `Runtime/Telemetry/AutoInstrument.cs`. The pure-C# state machine (rate limits, FPS median, idle transitions) lives at `Runtime/Telemetry/AutoInstrumentEngine.cs` and is fully unit-tested via `dotnet test` without needing a Unity runtime.
