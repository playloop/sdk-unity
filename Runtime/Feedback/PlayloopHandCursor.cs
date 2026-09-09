#nullable enable
namespace Playloop.Feedback
{
    /// <summary>
    /// Embedded pointing-hand cursor (18x20 PNG, base64) shown while the
    /// player hovers a clickable element of the default feedback form.
    /// Embedded as bytes so the runtime-built overlay can load it with no
    /// Resources folder or import settings.
    /// </summary>
    internal static class PlayloopHandCursor
    {
        public const string PngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAABIAAAAUCAYAAACAl21KAAAAYElEQVR42uXTQQoAIAgEQB/h/79aeCjE0mzt1oJHBzQjcsLMTYoqEWCkhH0EwYu3EIxGkEWfQMsOra4bTpl9uxcqQ3r2a8jDIOh2nBBCsSdQeEel/SBQ6ptksDSUKd3TAXSPdy1/MaS8AAAAAElFTkSuQmCC";

        /// <summary>Fingertip position within the image, from the top-left.</summary>
        public const float HotspotX = 7f;
        public const float HotspotY = 0f;
    }
}
