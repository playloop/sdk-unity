# Set up Playloop in this Unity project

You are an AI coding agent. The user has asked you to install and configure **Playloop**, a player-behavior telemetry SDK, into the Unity project you're currently working in.

Follow the steps below in order. Skip any step that's already done. Every step here produces compiling code with no required trip into the Unity editor; the settings-asset path (which does need the editor) is offered only as an alternative.

## Prerequisites

- Unity **2021.3 LTS** or newer (tested through Unity 6).
- The project must have a `Packages/manifest.json` (every Unity project does).
- A **game ingest key** starting with `pl_ik_`. Each game has its own ingest key, and telemetry sent with it is attributed to that game automatically, so there is **no separate game id to configure**. Get one of these ways:
  - **Ask the user** to open that game on https://playloop.gg and go to **Connections → Unity**, then copy the key shown there. (This is the game's own setup page. Do NOT send them to Settings → API keys; that is where the account-level Management key lives, which must never ship in a build.)
  - **Automate it** if you have the Playloop MCP server (or a management key): call the MCP `create_game` tool (backed by `POST /api/v1/games`, admin/owner role). It creates the game and returns a show-once ingest key you can embed.
  - If you can't get a key yet, wire everything up with a placeholder and tell the user exactly where to paste the real key.

## Step 1. Install the package

Open `Packages/manifest.json`. Under the `"dependencies"` object add this entry:

```json
"gg.playloop.sdk": "https://github.com/playloop/sdk-unity.git#v0.5.0"
```

If the entry already exists, leave it. Unity will resolve the package on next domain reload.

## Step 2. Bootstrap script (configure in code, the primary path)

Create `Assets/Playloop/PlayloopBootstrap.cs`. This configures the SDK entirely in code, so it compiles without any in-editor step:

```csharp
using UnityEngine;
using Playloop;

public static class PlayloopBootstrap
{
    static PlayloopClient _client;

    /// <summary>The live client. Reference it as PlayloopBootstrap.Client.Telemetry.Track(...).</summary>
    public static PlayloopClient Client => _client;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        _client = new PlayloopClient(new PlayloopOptions
        {
            // The game ingest key (pl_ik_...). It is write-only and safe to ship
            // in a build (it can only send telemetry, never read or delete data).
            // For a PUBLIC repo, don't commit the literal: read it from an env var
            // or a git-ignored config, e.g.
            //   ApiKey = System.Environment.GetEnvironmentVariable("PLAYLOOP_INGEST_KEY"),
            ApiKey = "pl_ik_REPLACE_ME",
        });

        _client.Telemetry.StartSession(new System.Collections.Generic.Dictionary<string, object>
        {
            { "gameVersion", Application.version },
            { "platform",    Application.platform.ToString() },
        });
        _client.Telemetry.AutoBatch();   // 5-second background flush loop
        Application.quitting += () => _client?.Dispose();
    }
}
```

This runs once at startup, opens the client, starts a session, turns on the auto-batch flush, and disposes cleanly on quit. There is **no `GameId` or `GameSlug` field** to set on `PlayloopOptions` (setting one is a compile error, CS0117); the SDK resolves the game from the ingest key.

If the SDK ever comes up with a blank/missing key, it does not throw: it builds a **disabled** client where every call is a safe no-op. Check `PlayloopBootstrap.Client.IsEnabled` if you want to detect that in code.

### Alternative: the settings asset (needs a human in the editor)

If the user would rather manage the key in the Inspector, they can create a settings asset instead. This step **cannot be done headless**: tell the user to open Unity, and in the Project view right-click `Assets/Resources/` (create the folder if it doesn't exist), then choose **Create → Playloop → Settings**. In the Inspector for the new `PlayloopSettings.asset`, paste the ingest key into the **Api Key** field. Do not try to author the `.asset` YAML by hand: Unity needs to generate the script GUID and binary metadata, and a hand-written asset will not load. Then load it in the bootstrap with `PlayloopSettings.Load()` (never-raise) in place of the `new PlayloopOptions { ... }` block:

```csharp
_client = new PlayloopClient(PlayloopSettings.Load());
```

## Step 3. Track your first event

Pick one existing line in the user's code where something interesting happens (a level completes, a boss dies, a checkpoint hits) and add:

```csharp
PlayloopBootstrap.Client.Telemetry.Track(
    "level_completed",
    new System.Collections.Generic.Dictionary<string, object> {
        { "level", 3 },
        { "deaths", 0 },
    });
```

`Track` is synchronous (it just buffers); do not `await` it.

## Step 4. Verify

If `dotnet` is on the user's PATH, run:

```bash
dotnet build
```

Otherwise the user opens Unity. The console should compile silently. If you see `CS0246 The type or namespace name 'Playloop' could not be found`, the package didn't install. Re-check Step 1. If you see `CS0117 'PlayloopOptions' does not contain a definition for 'GameId'` (or `GameSlug`), remove that line: those fields don't exist.

**Seeing data land:** editor and development-build sends are **suppressed by default** (`SendInEditor = false`), so playing in the editor ships nothing and https://playloop.gg/sessions stays empty. To confirm the pipeline from an editor playtest, temporarily set `SendInEditor = true` and keep `Environment = "dev"` so the traffic is tagged and easy to filter out of real numbers:

```csharp
_client = new PlayloopClient(new PlayloopOptions
{
    ApiKey       = "pl_ik_REPLACE_ME",
    SendInEditor = true,   // TEMP: send from the editor for this verify pass
    Environment  = "dev",  // tags the editor traffic
});
```

Revert `SendInEditor` to `false` (or delete the line) before shipping. In a real release player build nothing is ever suppressed, so no change is needed there.

## Things you must not do

- **Do not set `GameId` or `GameSlug` on `PlayloopOptions`.** Those fields do not exist and will not compile. Game identity comes from the ingest key.
- **Do not commit a real ingest key to a public repo.** It is write-only and safe to *ship in a built binary*, but a public source repo is different: read it from an env var (`System.Environment.GetEnvironmentVariable(...)`) or a git-ignored config file. In a private repo, an inline literal is fine.
- **Do not use a Management key** (one that starts with `pl_mgmt_`). Those are account-level server-side admin keys and must never ship in a game binary. The ingest key comes from the game's **Connections → Unity** page, not from Settings → API keys.
- **Do not `await` `Track()`.** `Track` is synchronous; it just buffers. Only `FlushAsync()`, `StopAutoBatchAsync()`, and `EndSessionAsync()` are awaitable.
- **Do not dispose the client twice.** The bootstrap's `Application.quitting` handler owns disposal. Don't call `Dispose` from `OnDestroy` on a separate MonoBehaviour.
- **Do not pin a base URL other than `https://playloop.gg`** unless the user explicitly provides a development or test endpoint.

## When you are done

Tell the user, verbatim:

> Playloop is installed. If you want to confirm it from the editor, set `SendInEditor = true` in the bootstrap, play for a few seconds, then check https://playloop.gg/sessions (your first session should appear within a minute). Set it back to false before shipping. In a real build it sends automatically.

## If you get stuck

Full SDK reference, gotchas, and the public API surface live in `AGENTS.md` at the repo root: https://github.com/playloop/sdk-unity/blob/main/AGENTS.md. Read that if a step above raises questions you can't resolve from this file alone.
