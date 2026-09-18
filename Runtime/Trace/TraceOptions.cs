#nullable enable
namespace Playloop.Trace
{
    /// <summary>
    /// When the Trace samples. <see cref="Auto"/> follows the resolved
    /// environment: on everywhere except <c>"production"</c>. <see cref="On"/>
    /// forces it on in production for a studio that has disclosed it.
    /// <see cref="Off"/> is off everywhere.
    /// </summary>
    public enum TraceMode
    {
        Auto,
        On,
        Off,
    }

    /// <summary>
    /// Which two world axes the Trace's <c>x</c> and <c>y</c> carry.
    /// <see cref="XY"/> for side-on and 2D games, <see cref="XZ"/> for
    /// top-down and 3D games where height is not the interesting axis.
    /// </summary>
    public enum TracePlane
    {
        XY,
        XZ,
    }

    /// <summary>
    /// Why a run ended. Written into the run's last chunk so Playback can
    /// mark where each run ended and why.
    /// </summary>
    public enum TraceEndReason
    {
        Death,
        LevelComplete,
        Quit,
        Timeout,
    }

    /// <summary>
    /// Why nothing is flowing (or that it is), answered at the source.
    /// </summary>
    public enum TraceStatus
    {
        /// <summary>Sampling.</summary>
        Active,
        /// <summary>The game called <see cref="TraceApi.Pause"/>.</summary>
        PausedByGame,
        /// <summary><see cref="TraceMode.Auto"/> resolved off because the environment is <c>"production"</c>.</summary>
        OffByEnvironment,
        /// <summary><see cref="TraceOptions.Mode"/> is <see cref="TraceMode.Off"/>.</summary>
        OffByOption,
        /// <summary>The dashboard's per-event config ignores <c>trace_chunk</c>.</summary>
        OffByConfig,
        /// <summary>The client was built without an ingest key.</summary>
        Disabled,
        /// <summary>The per-session byte budget was reached.</summary>
        BudgetExhausted,
        /// <summary>
        /// <see cref="TraceApi.End"/> closed a run and the next one has not
        /// opened yet (<see cref="TraceApi.Begin"/> or the next
        /// <see cref="TraceApi.SetPosition"/> opens it).
        /// </summary>
        BetweenRuns,
        /// <summary>The session ended. The Trace re-arms with the next session.</summary>
        Ended,
    }

    /// <summary>
    /// Declared bounds of a room, in world units on the Trace's plane. Optional:
    /// Playback fits a frame to the data when a room has none.
    /// </summary>
    public readonly struct TraceBounds
    {
        public readonly float XMin;
        public readonly float YMin;
        public readonly float XMax;
        public readonly float YMax;

        public TraceBounds(float xMin, float yMin, float xMax, float yMax)
        {
            XMin = xMin;
            YMin = yMin;
            XMax = xMax;
            YMax = yMax;
        }
    }

    /// <summary>
    /// Trace configuration. Lives on <see cref="PlayloopOptions.Trace"/>.
    /// </summary>
    public sealed class TraceOptions
    {
        /// <summary>Default per-session budget for serialized chunk JSON: 2 MB.</summary>
        public const int DefaultMaxBytesPerSession = 2 * 1024 * 1024;

        /// <summary>Lowest sample rate the SDK accepts.</summary>
        public const int MinHz = 5;

        /// <summary>Highest sample rate the SDK accepts.</summary>
        public const int MaxHz = 20;

        /// <summary>Hard cap on named tracked entities per session.</summary>
        public const int MaxEntitiesCap = 8;

        /// <summary>See <see cref="TraceMode"/>. Default <see cref="TraceMode.Auto"/>.</summary>
        public TraceMode Mode { get; set; } = TraceMode.Auto;

        /// <summary>Sample rate, clamped to 5..20 at construction. Default 10.</summary>
        public int Hz { get; set; } = 10;

        /// <summary>Which two world axes <c>SetPosition</c> carries. Default <see cref="TracePlane.XY"/>.</summary>
        public TracePlane Plane { get; set; } = TracePlane.XY;

        /// <summary>
        /// Named entities tracked per session, clamped to 0..8. A name past the
        /// cap is ignored with one warning per session. Default 8.
        /// </summary>
        public int MaxEntities { get; set; } = MaxEntitiesCap;

        /// <summary>
        /// Per-session budget for serialized chunk JSON. At the budget the
        /// sampler sends a final chunk marked <c>budget</c> and stops. Default 2 MB.
        /// </summary>
        public int MaxBytesPerSession { get; set; } = DefaultMaxBytesPerSession;
    }
}
