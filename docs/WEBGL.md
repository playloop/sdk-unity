# Playloop SDK on Unity WebGL

## TL;DR (for humans and agents)

**You do not need to do anything WebGL-specific.** Use the SDK exactly like
on desktop:

```csharp
var client = new PlayloopClient(options);   // session starts here
client.Telemetry.StartSession(metadata);    // optional metadata
client.Telemetry.AutoBatch();               // optional: focus + auto-events
// client.Telemetry.Track(...), client.State.SetState(...), etc.
```

Telemetry, heartbeats, and the feedback form all work on WebGL automatically as
of the 2026-06-15 fix. **Do NOT hand-roll a flush/heartbeat pump** (older
integrations did this as a workaround - it is no longer needed and will
double-send if combined with the SDK driver).

> Agent note: when integrating this SDK into a Unity WebGL (browser) game,
> add no WebGL-specific telemetry code. If you find a manual
> `FlushAsync()`/`Heartbeat.EmitOnce()` pump in a consumer game, remove it -
> the SDK now drives this itself on WebGL.

## Why WebGL needed special handling

A Unity WebGL build (without `webGLThreadsSupport`) is a **single-threaded**
WebAssembly process: there is no `ThreadPool`, and the only scheduler Unity
pumps is the per-frame `UnitySynchronizationContext`. That breaks two things
the SDK relied on:

1. **`ConfigureAwait(false)` strands continuations.** It schedules the
   post-await continuation onto the (nonexistent) ThreadPool. The
   `UnityWebRequest` still sends and the server still responds, but the C#
   continuation that processes the response never runs - so `FlushAsync` and
   feedback submit would hang after the first call, the flush lock stayed held,
   and the session was dropped at the server's ~120s online window.
2. **Background drivers never tick.** `AutoBatch`'s `Task.Run` flush loop and
   `HeartbeatEmitter`'s `System.Threading.Timer` don't run on a single thread.

## What the SDK does automatically on WebGL

- **`PlAwait.Continue`** - a compile-time constant (`true` only under
  `UNITY_WEBGL && !UNITY_EDITOR`) used in place of `ConfigureAwait(false)` on the
  flush/HTTP/retry/feedback path, so continuations resume on the main thread.
  Off WebGL it is `false` - behavior is byte-for-byte unchanged.
- **`PlayloopWebGLDriver`** - a hidden MonoBehaviour the `PlayloopClient`
  constructor spawns on WebGL. It pumps `FlushAsync()` (flush interval) and
  `Heartbeat.EmitOnce()` (heartbeat interval) from `Update()` on the main thread.
- **Non-blocking shutdown** - the quit hook does not `Task.Run(...).Wait()` on
  WebGL (that would block the only browser thread).

## Caveats to be aware of

- **Session-end / `session_summary` on tab close is unreliable.**
  `Application.quitting` rarely fires when a browser tab is closed, and there is
  no safe blocking flush on a single thread. Heartbeats are the liveness +
  crash-recovery mechanism on WebGL (the heartbeat carries your latest state
  snapshot, so if the app is force-quit the final `session_summary` is still
  recovered from the last heartbeat). Don't depend on a clean `EndSession`
  firing on WebGL.
- **CORS.** The browser sends telemetry from the host page's origin (e.g.
  itch.io serves WebGL games from `html-classic.itch.zone`). The ingest endpoint
  must return `Access-Control-Allow-Origin` for that origin. `playloop.gg`
  already does (`*`).
- **Seeing `[Playloop]` logs.** Unity strips/limits `Debug.Log` output on
  release WebGL builds; tick **Build Settings -> Development Build** to see SDK
  logs in the browser console.

## Verifying on WebGL

1. Open the build, F12 -> **Network**, filter `playloop`. During play you should
   see a `POST /api/telemetry` roughly every flush interval (200, carrying a
   `sessionId` after the first), not just one create POST.
2. The dashboard session should stay online past the ~2 minute window.
