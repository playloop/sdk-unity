#nullable enable
using System;
using System.Collections.Generic;
using Playloop.Telemetry;

namespace Playloop.Trace
{
    /// <summary>
    /// Sampled session state for Playback: the player's position and facing,
    /// the current room, abstract action bits and movement axes, and up to a
    /// few named entities, 5 to 20 times a second. The game pushes its latest
    /// values; the SDK owns the clock and the packing and ships one chunk
    /// every five seconds as an ordinary <c>trace_chunk</c> telemetry event.
    ///
    /// <para>
    /// Nothing here takes an engine type. Every string is a slug from one of
    /// three validated label families (actions, rooms, entities), each capped
    /// in count, so a key name, a button, or free text cannot reach the wire.
    /// Sample rows are eight numbers.
    /// </para>
    ///
    /// <para>
    /// <see cref="Tick"/> is the sampling seam. Inside Unity a hidden driver
    /// calls it from <c>Update</c> once <c>Telemetry.AutoBatch()</c> runs; a
    /// headless host calls it with its own clock.
    /// </para>
    /// </summary>
    public sealed class TraceApi
    {
        private const string ChunkEventName = "trace_chunk";
        private const string StateEventName = "trace_state";

        private readonly TelemetryApi _telemetry;
        private readonly TraceSampler? _sampler;
        private readonly TraceStatus _baseStatus;
        private TraceStatus _status;
        private string? _offReason;
        private bool _stateReported;

        internal TraceApi(TelemetryApi telemetry, TraceOptions options, string environment, bool enabled)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            options ??= new TraceOptions();
            Plane = options.Plane;

            if (!enabled)
            {
                _baseStatus = TraceStatus.Disabled;
            }
            else if (options.Mode == TraceMode.Off)
            {
                _baseStatus = TraceStatus.OffByOption;
                _offReason = "option";
            }
            else if (options.Mode == TraceMode.Auto &&
                     string.Equals(environment, "production", StringComparison.Ordinal))
            {
                _baseStatus = TraceStatus.OffByEnvironment;
                _offReason = "production";
            }
            else
            {
                _baseStatus = TraceStatus.Active;
                _sampler = new TraceSampler(
                    options.Hz,
                    options.Plane,
                    options.MaxEntities,
                    options.MaxBytesPerSession,
                    (chunk, t0) => _telemetry.Track(ChunkEventName, chunk, t0))
                {
                    Warn = WarnOnce,
                };
            }
            _status = _baseStatus;
            Hz = _sampler?.Hz ?? Math.Max(TraceOptions.MinHz, Math.Min(TraceOptions.MaxHz, options.Hz));
        }

        /// <summary>Why nothing is flowing, or <see cref="TraceStatus.Active"/>.</summary>
        public TraceStatus Status => _status;

        /// <summary>True while samples are being taken.</summary>
        public bool IsActive => _status == TraceStatus.Active;

        /// <summary>The plane <see cref="SetPosition"/> coordinates are on.</summary>
        public TracePlane Plane { get; }

        /// <summary>Sample rate after clamping to 5..20.</summary>
        public int Hz { get; }

        /// <summary>Chunks sent so far in this session.</summary>
        internal int ChunkCount => _sampler?.ChunkCount ?? 0;

        // ───────────────────────── surface ─────────────────────────

        /// <summary>
        /// Declare the action labels, bit <c>i</c> = <c>labels[i]</c>. Up to 16,
        /// each matching <c>^[a-z][a-z0-9_]{0,23}$</c>. Rides the next chunk.
        /// </summary>
        public void DefineActions(params string[] labels)
        {
            if (labels == null) throw new ArgumentNullException(nameof(labels));
            if (labels.Length > TraceSampler.MaxActions)
            {
                throw new ArgumentException($"Trace.DefineActions accepts at most {TraceSampler.MaxActions} labels.", nameof(labels));
            }
            for (int i = 0; i < labels.Length; i++)
            {
                if (!TraceLabels.IsActionLabel(labels[i]))
                {
                    throw new ArgumentException($"Trace action label \"{labels[i]}\" must match ^[a-z][a-z0-9_]{{0,23}}$.", nameof(labels));
                }
                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(labels[i], labels[j], StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Trace action label \"{labels[i]}\" is repeated.", nameof(labels));
                    }
                }
            }
            if (_sampler == null) { ReportStateOnce(); return; }
            _sampler.DefineActions(labels);
        }

        /// <summary>
        /// The room the player is in, matching <c>^[a-z][a-z0-9_:-]{0,63}$</c>.
        /// Optional declared bounds ride the chunk's room table so Playback
        /// frames the room the same way every time.
        /// </summary>
        public void SetRoom(string roomId, TraceBounds? bounds = null)
        {
            if (!TraceLabels.IsRoomId(roomId))
            {
                throw new ArgumentException("Trace room id must match ^[a-z][a-z0-9_:-]{0,63}$.", nameof(roomId));
            }
            if (_sampler == null) { ReportStateOnce(); return; }
            _sampler.SetRoom(roomId, bounds);
        }

        /// <summary>
        /// Latest position on the declared plane, with an optional facing in
        /// degrees (0 along +x, 90 along +y). Pass <c>-1</c> for unknown.
        /// </summary>
        public void SetPosition(float x, float y, float facingDeg = -1f)
        {
            if (_sampler == null) { ReportStateOnce(); return; }
            _sampler.SetPosition(x, y, NormalizeFacing(facingDeg));
        }

        /// <summary>
        /// The abstract action mask (bit <c>i</c> = the <c>i</c>th label from
        /// <see cref="DefineActions"/>) and the movement vector in [-1, 1].
        /// There is deliberately no overload that takes a key, a button name,
        /// or a string.
        /// </summary>
        public void SetInput(int actionBits, float axisX, float axisY)
        {
            if (_sampler == null) { ReportStateOnce(); return; }
            _sampler.SetInput(actionBits, axisX, axisY);
        }

        /// <summary>
        /// Latest position of a named entity, matching <c>^[a-z][a-z0-9_]{0,31}$</c>.
        /// A name past <see cref="TraceOptions.MaxEntities"/> is ignored with
        /// one warning per session.
        /// </summary>
        public void SetEntity(string name, float x, float y)
        {
            if (!TraceLabels.IsEntityName(name))
            {
                throw new ArgumentException("Trace entity name must match ^[a-z][a-z0-9_]{0,31}$.", nameof(name));
            }
            if (_sampler == null) { ReportStateOnce(); return; }
            _sampler.SetEntity(name, x, y);
        }

        /// <summary>Mark a named entity gone. It is written once more with a null position.</summary>
        public void ClearEntity(string name)
        {
            if (!TraceLabels.IsEntityName(name))
            {
                throw new ArgumentException("Trace entity name must match ^[a-z][a-z0-9_]{0,31}$.", nameof(name));
            }
            if (_sampler == null) return;
            _sampler.ClearEntity(name);
        }

        /// <summary>Stop sampling until <see cref="Resume"/>. Nothing is buffered while paused.</summary>
        public void Pause()
        {
            if (_sampler == null) return;
            _sampler.Pause();
            if (_status == TraceStatus.Active) _status = TraceStatus.PausedByGame;
        }

        /// <summary>Resume sampling after <see cref="Pause"/>.</summary>
        public void Resume()
        {
            if (_sampler == null) return;
            _sampler.Resume();
            if (_status == TraceStatus.PausedByGame) _status = TraceStatus.Active;
        }

        /// <summary>
        /// Close the Trace with a reason. Sends the partial chunk with the
        /// <c>end</c> block. Idempotent; the session end calls it with
        /// <see cref="TraceEndReason.Quit"/> for you.
        /// </summary>
        public void End(TraceEndReason reason)
        {
            if (_sampler == null) return;
            _sampler.End(reason);
            if (_status == TraceStatus.Active || _status == TraceStatus.PausedByGame)
            {
                _status = TraceStatus.Ended;
            }
        }

        /// <summary>
        /// The sampling seam. Call once per frame with the unscaled clock in
        /// seconds; the SDK's hidden driver does this for you inside Unity.
        /// </summary>
        public void Tick(double unscaledSec)
            => Tick(unscaledSec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        internal void Tick(double unscaledSec, long nowUnixMs)
        {
            if (_sampler == null) { ReportStateOnce(); return; }
            if (_status == TraceStatus.OffByConfig || _status == TraceStatus.BudgetExhausted) return;

            if (_telemetry.IsEventIgnored(ChunkEventName))
            {
                _status = TraceStatus.OffByConfig;
                _offReason = "config";
                _sampler.Pause();
                ReportStateOnce();
                return;
            }

            _sampler.Tick(unscaledSec, nowUnixMs);

            if (_sampler.BudgetExhausted)
            {
                _status = TraceStatus.BudgetExhausted;
                _offReason = "budget";
                ReportStateOnce();
            }
        }

        /// <summary>Re-arm for the next session on this client.</summary>
        internal void ResetForNewSession()
        {
            if (_sampler == null) return;
            _sampler.ResetForNewSession();
            _stateReported = false;
            if (_status == TraceStatus.Ended || _status == TraceStatus.BudgetExhausted)
            {
                _status = _sampler.IsPaused ? TraceStatus.PausedByGame : TraceStatus.Active;
                _offReason = null;
            }
        }

        // ───────────────────────── internals ─────────────────────────

        private void ReportStateOnce()
        {
            if (_stateReported || _offReason == null) return;
            _stateReported = true;
            try
            {
                _telemetry.Track(StateEventName, new Dictionary<string, object>
                {
                    ["enabled"] = false,
                    ["reason"] = _offReason,
                });
            }
            catch
            {
                // Best effort: the state note must never break the game loop.
            }
        }

        private static int NormalizeFacing(float facingDeg)
        {
            if (float.IsNaN(facingDeg) || float.IsInfinity(facingDeg) || facingDeg == -1f) return -1;
            int deg = (int)Math.Round(facingDeg, MidpointRounding.AwayFromZero);
            return ((deg % 360) + 360) % 360;
        }

        private static void WarnOnce(string message)
        {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            UnityEngine.Debug.LogWarning(message);
#else
            Console.Error.WriteLine(message);
#endif
        }
    }

    /// <summary>
    /// The three label families every string on the Trace wire comes from.
    /// Hand-rolled character checks so a per-frame call allocates nothing.
    /// </summary>
    public static class TraceLabels
    {
        /// <summary><c>^[a-z][a-z0-9_]{0,23}$</c></summary>
        public static bool IsActionLabel(string? s) => IsSlug(s, 24, allowRoomChars: false);

        /// <summary><c>^[a-z][a-z0-9_:-]{0,63}$</c></summary>
        public static bool IsRoomId(string? s) => IsSlug(s, 64, allowRoomChars: true);

        /// <summary><c>^[a-z][a-z0-9_]{0,31}$</c></summary>
        public static bool IsEntityName(string? s) => IsSlug(s, 32, allowRoomChars: false);

        private static bool IsSlug(string? s, int maxLength, bool allowRoomChars)
        {
            if (s == null || s.Length == 0 || s.Length > maxLength) return false;
            char first = s[0];
            if (first < 'a' || first > 'z') return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' ||
                          (allowRoomChars && (c == ':' || c == '-'));
                if (!ok) return false;
            }
            return true;
        }
    }
}
