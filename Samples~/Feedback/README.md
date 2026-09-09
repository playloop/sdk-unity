# Player Feedback sample

Minimum integration for the multi-field Player Feedback surface.

## What you get

`PlayloopFeedbackSample.cs` shows two patterns:

1. **Default form** (`OpenDefaultForm`). One call. The SDK builds a Canvas + VerticalLayoutGroup at runtime, awaits the player, and submits when they click Send. No prefab, no scene authoring.
2. **Custom UI** (`SubmitFromCustomUI`). Your overlay (pause menu, modal, etc.) collects the answers; the SDK just carries them.

## Setup

1. Open `/games/<slug>/feedback` in your Playloop dashboard and create a form.
2. Copy the form's `ff_...` id.
3. Drop `PlayloopFeedbackSample.cs` on any GameObject in a scene.
4. Set `ApiKey` and `FormId` in the Inspector.
5. Wire `OpenDefaultForm` to a UI Button's `On Click ()` event.

Play the scene, click the button, fill the form. The submission shows up on the matching tester's detail page in the dashboard, grouped by `submissionId`.

## When to show the form

- **Pause menu**: surface a "Send feedback" button.
- **Level complete / boss down**: render at a natural breath moment.
- **Back-to-menu handler**: capture answers, then call `EndSession()` so the submission flushes with the session boundary.

Do not call `SubmitFeedbackAsync` from an `Application.quitting` handler. The HTTP request may not flush before the process dies.

## Customizing the default form

Pass a `FeedbackFormTheme` to `OpenAsync` to override colors / font. Pass your own `FeedbackField[]` instead of `DefaultFeedbackFields.Snapshot()` if you want a code-defined form instead of a dashboard-defined one:

```csharp
var fields = new[]
{
    new FeedbackField { Id = "q1", Label = "How was the boss fight?", Kind = FeedbackFieldKinds.Rating1To5, Required = true },
    new FeedbackField { Id = "q2", Label = "Anything to fix?", Kind = FeedbackFieldKinds.LongText },
};
await client.FeedbackForm.OpenAsync(formId, sessionId, fields);
```
