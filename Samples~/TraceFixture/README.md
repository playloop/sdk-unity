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
- Stop at (50, 5) in `vault` at 12.25 s, hold, and end the first run with `Death` at 13.0 s.
- Push nothing for two seconds. The Trace samples nothing between runs.
- At 15.0 s call `Trace.Begin()` and respawn in `hall` at (1, 5). Walk +x at the same speed, stop at (10, 5) at 17.25 s, and end the second run with `Quit` at 18.0 s. The key does not come back. Then end the session.

Expected wire: four `trace_chunk` events at 10 Hz on plane `xy`. Seq 0, 1 and 2 are run 0 (`seg: 0`, 50 + 50 + 30 samples) with the end block `death` on seq 2 at (50, 5) in `vault`. Seq 3 is run 1 (`seg: 1`, 30 samples, its clock restarting at 15.0 s) with the end block `quit` at (10, 5) in `hall`. The session end adds no chunk of its own, because the last run already has its end.

`TraceFixtureRoute.cs` has no engine reference on purpose. The same table drives this scene and the SDK's own tests, so the audit and the fixture can never disagree about where the player was.

## Setup

1. Create an empty scene with nothing in it (the fixture adds a camera if the scene has none).
2. Add an empty GameObject and drop `PlayloopTraceFixture.cs` on it.
3. Set **Api Key** to your game's ingest key. Leave **Send In Editor** on and **Environment** at `dev`.
4. Press Play. After about 18 seconds the console logs that the session ended.
5. Open the session on the dashboard. It carries four `trace_chunk` events: run 0 ends with reason `death` at (50, 5) in `vault`, run 1 ends with reason `quit` at (10, 5) in `hall`. Once Playback's route view is available it draws both runs with an end marker on each spot, and the per-level view puts both ends there too.

## Reading the result

- The `trace_state` event never appears for this run: the fixture forces `TraceMode.On`.
- The final flush carries `traceChunks: 4`, top-level and in the session metadata, so a missing chunk shows as a gap rather than as a shorter route.
- Stopping Play before the route finishes leaves the session end to the SDK's own play-mode hook; once the route has finished, the fixture waits up to three seconds for its end to reach the wire before it disposes the client.
- If nothing arrives, check that `SendInEditor` is on and that the key belongs to the game you are looking at.
