# Bug Reports sample

Minimum integration for the player Bug Reports surface.

## What you get

`PlayloopBugReportSample.cs` shows two patterns:

1. **Default form** (`OpenDefaultForm`). One call. The SDK builds a Canvas + VerticalLayoutGroup at runtime, awaits the player, and submits when they click Submit. No prefab, no scene authoring.
2. **Custom UI** (`SubmitFromCustomUI`). Your overlay (pause menu, modal, etc.) collects the title / description / severity; the SDK just carries them.

## Setup

1. Drop `PlayloopBugReportSample.cs` on any GameObject in a scene.
2. Set `ApiKey` in the Inspector.
3. Wire `OpenDefaultForm` to a UI Button's `On Click ()` event.

Play the scene, click the button, fill the form. The report is stored in Playloop, shows up on the game's dashboard, and routes to your connected issue tracker automatically.

## The form

The default form collects a **title** (short summary, required), a **description** (details, required), and a **severity** (Low / Medium / High / Critical, defaults to Medium). Build/version, environment, and platform are attached automatically from the same values telemetry stamps, so a triager sees where the report came from without the player typing anything.

## When to show it

- **Pause menu**: surface a "Report a bug" button. This is the recommended primary entry, so a player can file the moment something breaks.
- **Main menu**: a secondary entry for between-session reports.

Do not call `SubmitBugReportAsync` from an `Application.quitting` handler. The HTTP request may not flush before the process dies.

## Customizing the default form

Pass a `BugReportFormTheme` to `OpenAsync` to override colors, font, and all player-visible labels (heading, field labels, placeholders, severity labels, buttons) for localization:

```csharp
var theme = new BugReportFormTheme
{
    Title = "Report a bug",
    SeverityLabels = new[] { "Minor", "Annoying", "Bad", "Blocker" },
};
await client.BugReportForm.OpenAsync(sessionId, theme);
```
