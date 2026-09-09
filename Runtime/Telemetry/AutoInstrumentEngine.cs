#nullable enable
using System;
using System.Collections.Generic;

namespace Playloop.Telemetry
{
    /// <summary>
    /// Pure-C# kernel for <see cref="AutoInstrument"/>. Holds the rolling FPS
    /// window, error counter, idle-state machine, and rate-limiting state for
    /// the auto-emitted events that <see cref="TelemetryApi.AutoBatch"/> turns
    /// on by default.
    ///
    /// <para>
    /// All Unity-specific glue (Application callbacks, Time.unscaledDeltaTime,
    /// Input polling) is intentionally kept OUT of this class so the logic is
    /// covered by plain NUnit tests that run via <c>dotnet test</c> without a
    /// Unity runtime: Unity's headless test runner is slow + flaky for
    /// per-frame state machines.
    /// </para>
    /// </summary>
    public sealed class AutoInstrumentEngine
    {
        /// <summary>Hard cap on application_error events per session.</summary>
        public const int MaxErrorsPerSession = 50;

        /// <summary>Min seconds between consecutive fps_drop events.</summary>
        public const float FpsDropCooldownSec = 5f;

        /// <summary>Rolling-window length (seconds) for the median FPS calculation.</summary>
        public const float FpsWindowSec = 3f;

        /// <summary>Min seconds between consecutive memory_pressure events.</summary>
        public const float MemoryCooldownSec = 30f;

        // ── error rate limit
        private int _errorsThisSession;

        // ── fps rolling window
        // Stores per-frame deltas in seconds. We compute the median FPS from
        // the inverse of those deltas across the trailing FpsWindowSec.
        private readonly List<float> _fpsDeltas = new List<float>(256);
        private readonly List<float> _fpsTimes = new List<float>(256);
        private float _lastFpsDropAtSec = float.NegativeInfinity;

        // ── memory cooldown
        private float _lastMemoryEventAtSec = float.NegativeInfinity;

        // ── idle state
        private bool _isIdle;
        private float _idleEnteredAtSec;
        private float _lastInputAtSec;

        // Idle threshold (seconds), captured at construction so changes
        // mid-session don't yank the state machine sideways. The Unity layer
        // resolves the default (30s desktop / 60s mobile) and feeds the value
        // in.
        public float IdleThresholdSec { get; }

        public float FpsDropThreshold { get; }

        public AutoInstrumentEngine(float idleThresholdSec, float fpsDropThreshold)
        {
            IdleThresholdSec = idleThresholdSec > 0 ? idleThresholdSec : 30f;
            FpsDropThreshold = fpsDropThreshold > 0 ? fpsDropThreshold : 30f;
        }

        /// <summary>Read-only diagnostic: current error count.</summary>
        public int ErrorCount => _errorsThisSession;

        /// <summary>Read-only diagnostic: is the engine currently in the idle state?</summary>
        public bool IsIdle => _isIdle;

        /// <summary>
        /// Decide whether to emit an <c>application_error</c>. Returns false
        /// once the per-session cap has been hit so the caller can drop the
        /// event without enqueuing.
        /// </summary>
        public bool TryRecordError()
        {
            if (_errorsThisSession >= MaxErrorsPerSession) return false;
            _errorsThisSession++;
            return true;
        }

        /// <summary>
        /// Reset the per-session error counter. Called when a new session
        /// starts so a 51st error in session N+1 isn't dropped because of a
        /// noisy session N.
        /// </summary>
        public void ResetErrorCounter()
        {
            _errorsThisSession = 0;
        }

        /// <summary>
        /// Decide whether to emit a <c>memory_pressure</c> event. Returns false
        /// when called inside the cooldown window so we don't spam.
        /// </summary>
        public bool TryRecordMemoryEvent(float nowSec)
        {
            if (nowSec - _lastMemoryEventAtSec < MemoryCooldownSec) return false;
            _lastMemoryEventAtSec = nowSec;
            return true;
        }

        /// <summary>
        /// Record one frame of FPS data and report whether a <c>fps_drop</c>
        /// event should fire. The window is trailing-3-seconds; the median
        /// is the middle frame-rate of the kept samples. Throttled to one
        /// drop event every <see cref="FpsDropCooldownSec"/> seconds so a
        /// sustained slow patch produces one event, not 180 per minute.
        /// </summary>
        public FpsDropDecision RecordFrame(float deltaSec, float nowSec)
        {
            if (deltaSec <= 0 || float.IsNaN(deltaSec) || float.IsInfinity(deltaSec))
            {
                return FpsDropDecision.None;
            }

            _fpsDeltas.Add(deltaSec);
            _fpsTimes.Add(nowSec);

            // Drop samples older than the window. The two lists move in
            // lockstep so a single pivot search keeps them aligned.
            var cutoff = nowSec - FpsWindowSec;
            int trim = 0;
            while (trim < _fpsTimes.Count && _fpsTimes[trim] < cutoff) trim++;
            if (trim > 0)
            {
                _fpsTimes.RemoveRange(0, trim);
                _fpsDeltas.RemoveRange(0, trim);
            }

            // Need at least a second of data before we trust the median.
            if (_fpsDeltas.Count < 30) return FpsDropDecision.None;

            // Convert deltas to instantaneous FPS, sort a copy, pick the median.
            var fpsCopy = new float[_fpsDeltas.Count];
            for (int i = 0; i < _fpsDeltas.Count; i++)
            {
                fpsCopy[i] = 1f / _fpsDeltas[i];
            }
            Array.Sort(fpsCopy);
            float median = fpsCopy[fpsCopy.Length / 2];
            float low = fpsCopy[0];

            if (median >= FpsDropThreshold) return FpsDropDecision.None;
            if (nowSec - _lastFpsDropAtSec < FpsDropCooldownSec) return FpsDropDecision.None;

            _lastFpsDropAtSec = nowSec;
            return new FpsDropDecision(true, median, low, FpsWindowSec);
        }

        /// <summary>
        /// Run the idle state machine for one tick.
        ///
        /// <para>
        /// Pass <c>inputThisFrame</c> = true on every frame where the user
        /// pressed a key, moved the mouse, or touched the screen. The engine
        /// flips to <c>idle</c> after <see cref="IdleThresholdSec"/> seconds
        /// with no input, and back to <c>active</c> the moment input returns.
        /// </para>
        /// </summary>
        public IdleDecision Tick(bool inputThisFrame, float nowSec)
        {
            if (inputThisFrame)
            {
                _lastInputAtSec = nowSec;

                if (_isIdle)
                {
                    var dur = nowSec - _idleEnteredAtSec;
                    _isIdle = false;
                    return IdleDecision.End(dur);
                }
                return IdleDecision.None;
            }

            if (!_isIdle && nowSec - _lastInputAtSec >= IdleThresholdSec)
            {
                _isIdle = true;
                _idleEnteredAtSec = nowSec;
                return IdleDecision.Start(IdleThresholdSec);
            }

            return IdleDecision.None;
        }

        /// <summary>
        /// Stamp "input just happened" without running the rest of the tick.
        /// Useful when the Unity layer detects input via callbacks rather
        /// than a per-frame poll.
        /// </summary>
        public void NoteInput(float nowSec)
        {
            _lastInputAtSec = nowSec;
        }

        /// <summary>
        /// Reset the idle clock so the next Tick treats "now" as the most
        /// recent input. Called when a fresh session starts.
        /// </summary>
        public void ResetIdle(float nowSec)
        {
            _isIdle = false;
            _lastInputAtSec = nowSec;
            _idleEnteredAtSec = 0f;
        }
    }

    public readonly struct FpsDropDecision
    {
        public readonly bool Fire;
        public readonly float MedianFps;
        public readonly float LowFps;
        public readonly float WindowSec;

        public FpsDropDecision(bool fire, float medianFps, float lowFps, float windowSec)
        {
            Fire = fire;
            MedianFps = medianFps;
            LowFps = lowFps;
            WindowSec = windowSec;
        }

        public static readonly FpsDropDecision None = new FpsDropDecision(false, 0f, 0f, 0f);
    }

    public readonly struct IdleDecision
    {
        public enum Kind { None, Start, End }

        public readonly Kind Outcome;
        public readonly float IdleThresholdSec;
        public readonly float IdleDurationSec;

        private IdleDecision(Kind outcome, float thresholdSec, float durationSec)
        {
            Outcome = outcome;
            IdleThresholdSec = thresholdSec;
            IdleDurationSec = durationSec;
        }

        public static readonly IdleDecision None = new IdleDecision(Kind.None, 0f, 0f);
        public static IdleDecision Start(float thresholdSec) => new IdleDecision(Kind.Start, thresholdSec, 0f);
        public static IdleDecision End(float durationSec) => new IdleDecision(Kind.End, 0f, durationSec);
    }
}
