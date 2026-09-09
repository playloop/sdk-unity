#nullable enable
namespace Playloop.Telemetry
{
    /// <summary>
    /// Immutable snapshot of the auto-instrumentation knobs captured at
    /// <see cref="TelemetryApi"/> construction. Threaded through so the
    /// Unity-only <c>AutoInstrument</c> MonoBehaviour can read flags +
    /// thresholds without holding a reference to the live
    /// <see cref="PlayloopOptions"/> (which the user may still be mutating
    /// on their side).
    /// </summary>
    public readonly struct AutoInstrumentSettings
    {
        public readonly bool AutoInstrumentScenes;
        public readonly bool AutoInstrumentErrors;
        public readonly bool AutoInstrumentIdle;
        public readonly bool AutoInstrumentFps;
        public readonly bool AutoInstrumentMemory;
        public readonly float IdleThresholdSec;
        public readonly float FpsDropThreshold;

        public AutoInstrumentSettings(
            bool autoInstrumentScenes,
            bool autoInstrumentErrors,
            bool autoInstrumentIdle,
            bool autoInstrumentFps,
            bool autoInstrumentMemory,
            float idleThresholdSec,
            float fpsDropThreshold)
        {
            AutoInstrumentScenes = autoInstrumentScenes;
            AutoInstrumentErrors = autoInstrumentErrors;
            AutoInstrumentIdle = autoInstrumentIdle;
            AutoInstrumentFps = autoInstrumentFps;
            AutoInstrumentMemory = autoInstrumentMemory;
            IdleThresholdSec = idleThresholdSec;
            FpsDropThreshold = fpsDropThreshold;
        }

        /// <summary>
        /// Convenience builder used by <see cref="PlayloopClient"/> to capture
        /// the auto-instrument config off the user-provided
        /// <see cref="PlayloopOptions"/>.
        /// </summary>
        public static AutoInstrumentSettings FromOptions(PlayloopOptions options)
        {
            var ai = options.AutoInstrument;
            return new AutoInstrumentSettings(
                ai.Scenes,
                ai.Errors,
                ai.Idle,
                ai.Fps,
                ai.Memory,
                ai.IdleThresholdSec,
                ai.FpsDropThreshold);
        }

        /// <summary>Default-all-off settings: used by tests and by the test client when no options are present.</summary>
        public static readonly AutoInstrumentSettings Disabled =
            new AutoInstrumentSettings(false, false, false, false, false, 0f, 0f);

        /// <summary>True iff any individual auto-event would fire.</summary>
        public bool AnyEnabled =>
            AutoInstrumentScenes || AutoInstrumentErrors || AutoInstrumentIdle ||
            AutoInstrumentFps || AutoInstrumentMemory;
    }
}
