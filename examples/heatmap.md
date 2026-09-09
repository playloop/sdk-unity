# Heatmaps: `player_pos` event

Playloop builds per-room heatmaps from telemetry events named exactly
`player_pos` with a payload of `{ x, y, room }`. The aggregator bins
positions into a 32×18 grid per room, log-scales intensity, and caps at
12 rooms per game. The SDK has always supported arbitrary events; this
file shows the convention so your data shows up on the dashboard.

## Minimal example

Attach this to your player GameObject. `FixedUpdate` fires at Unity's
fixed timestep (default ~50Hz). That's too fast for clean heatmaps, so
gate it to ~10–20Hz with a stamp interval.

```csharp
using System.Collections.Generic;
using Playloop;
using UnityEngine;

public sealed class HeatmapEmitter : MonoBehaviour
{
    [SerializeField] PlayloopBootstrap _playloop;   // your SDK bootstrap singleton
    [SerializeField] string _currentRoomId = "tutorial_01";

    const float StampIntervalSec = 0.1f; // 10Hz
    float _lastStamp;

    void FixedUpdate()
    {
        if (Time.time - _lastStamp < StampIntervalSec) return;
        _lastStamp = Time.time;

        _playloop.Client.Telemetry.Track("player_pos", new Dictionary<string, object> {
            { "x",    transform.position.x },
            { "y",    transform.position.z },   // top-down: z is the "y" axis on the heatmap
            { "room", _currentRoomId },
        });
    }
}
```

## Payload rules

- `x` and `y` must be finite numbers (world-space, any unit: pixels,
  meters, tiles, etc.).
- `room` must be a non-empty string and stable across sessions for the
  same logical area. `"tutorial_01"` always, never `"tutorial_01_v2"`
  on a rebuild.
- Events missing any field are silently dropped server-side.

## Cadence

10–20Hz is enough for clean maps. Higher rates inflate event counts
without improving fidelity. The aggregator bins to a 32×18 grid.

## Top-down vs side-scrolling

For a top-down game, send Unity's `position.x` and `position.z` as
`x`/`y`. For a 2D side-scroller, `position.x` and `position.y` are the
right pair.

## Stable room IDs

The aggregator caps at 12 rooms per game (highest event count wins).
Use scene name, level GUID, or any other identifier that's stable
across runs, not a runtime-generated value.
