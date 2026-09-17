**SendInEditor must be true to verify this fixture in the editor** (or run it as a development player): editor and development-build sends are suppressed by default, and a suppressed run never reaches the dashboard.

# Trace fixture

A tiny scripted game that walks a known path so you can check the Trace end to end: the SDK samples it and the session lands on the dashboard carrying exactly this route.

## What it does

`PlayloopTraceFixture.cs` builds its own scene at runtime (three colored quads for the rooms, a capsule for the player, a small cube for a key), starts a session, and walks the route in `TraceFixtureRoute.cs` on the unscaled clock:

- Rooms `hall` [0, 0, 20, 10], `crypt` [20, 0, 40, 10], `vault` [40, 0, 60, 10], declared with bounds.
- Actions `move`, `jump`, `attack`.
- Start at (1, 5) in `hall`, walk +x at 4 units a second.
- Attack from 6.0 s to 6.5 s, jump from 8.0 s to 8.3 s (facing 90 during the jump).
- Enter `crypt` at 4.75 s and `vault` at 9.75 s. The key at (30, 8) is picked up on the way out of `crypt`.
- Stop at (50, 5) in `vault` at 12.25 s, hold, and end the Trace with `Death` at 13.0 s, then end the session.

Expected wire: three `trace_chunk` events (seq 0, 1, 2) at 10 Hz on plane `xy`, 130 samples in all, with the end block on seq 2 at (50, 5) in `vault`.

`TraceFixtureRoute.cs` has no engine reference on purpose. The same table drives this scene and the SDK's own tests, so the audit and the fixture can never disagree about where the player was.

## Setup

1. Create an empty scene with nothing in it (the fixture adds a camera if the scene has none).
2. Add an empty GameObject and drop `PlayloopTraceFixture.cs` on it.
3. Set **Api Key** to your game's ingest key. Leave **Send In Editor** on and **Environment** at `dev`.
4. Press Play. After about 13 seconds the console logs that the session ended.
5. Open the session on the dashboard. It carries three `trace_chunk` events (seq 0, 1, 2) whose last one ends with reason `death` at (50, 5) in `vault`. Once Playback's route view is available it draws that route through the three rooms with the end marker on that spot, and the per-level view puts the end there too.

## Reading the result

- The `trace_state` event never appears for this run: the fixture forces `TraceMode.On`.
- The final flush carries `traceChunks: 3`, top-level and in the session metadata, so a missing chunk shows as a gap rather than as a shorter route.
- Stopping Play before the route finishes leaves the session end to the SDK's own play-mode hook; once the route has finished, the fixture waits up to three seconds for its end to reach the wire before it disposes the client.
- If nothing arrives, check that `SendInEditor` is on and that the key belongs to the game you are looking at.
