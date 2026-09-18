#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Playloop.Trace
{
    /// <summary>
    /// The pure half of the Trace: sampling, quantizing and packing, with no
    /// engine types and no clock of its own. <see cref="Tick"/> is handed the
    /// unscaled game clock and the wall clock; when a chunk is full it hands
    /// ONE chunk document to the sink. The sampler holds at most one chunk in
    /// memory and never writes to disk.
    ///
    /// <para>
    /// Nothing is sampled until the game pushes state for the first time
    /// (<see cref="SetPosition"/>, <see cref="SetRoom"/>, <see cref="SetInput"/>
    /// or <see cref="SetEntity"/>). The driver ticks from the first frame, so
    /// without this gate a game that never wired the Trace would still send
    /// chunks of zeros every five seconds.
    /// </para>
    ///
    /// <para>
    /// A session holds one or more runs. <see cref="End"/> closes the current
    /// run and the sampler goes between runs, sampling nothing; the next run
    /// opens on <see cref="Begin"/> or on the next <see cref="SetPosition"/>.
    /// Each run starts a new chunk whose clock is re-anchored at its first
    /// tick. <see cref="EndSession"/> closes whatever run is open and stops
    /// the sampler until <see cref="ResetForNewSession"/>.
    /// </para>
    ///
    /// <para>
    /// Chunk document (format version 1), keys in this order: <c>v</c>,
    /// <c>seq</c> (per session, counting across runs), <c>seg</c> (the
    /// 0-based run index, always written), <c>t0</c> (unix ms of the chunk's
    /// first sample), <c>hz</c>, <c>plane</c>, <c>acts</c> (first chunk and
    /// after every redefine), per-chunk <c>rooms</c> and <c>ents</c> tables,
    /// <c>s</c> sample rows of exactly eight numbers
    /// <c>[dt, x, y, f, r, a, ax, ay]</c>, sparse entity rows <c>e</c> of
    /// <c>[sampleIndex, entityIndex, x, y]</c>, and an <c>end</c> block on
    /// the last chunk of each run.
    /// </para>
    /// </summary>
    public sealed class TraceSampler
    {
        /// <summary>Wire format version written into every chunk.</summary>
        public const int FormatVersion = 1;

        /// <summary>Seconds of samples per chunk.</summary>
        public const double ChunkSeconds = 5.0;

        /// <summary>Most action labels a session can define (one bit each).</summary>
        public const int MaxActions = 16;

        /// <summary>An entity is re-emitted only after it moves farther than this.</summary>
        public const double EntityMoveThreshold = 0.05;

        /// <summary>
        /// Most rooms whose declared bounds are remembered over the client's
        /// lifetime. Past it a new room id is still sampled, only its bounds
        /// are dropped; rooms already declared keep updating.
        /// </summary>
        public const int MaxRoomBounds = 1024;

        /// <summary>Receives each finished chunk document and its <c>t0</c>.</summary>
        public delegate void ChunkSink(Dictionary<string, object> chunk, long t0UnixMs);

        private const double Eps = 1e-4;

        private readonly int _hz;
        private readonly double _periodSec;
        private readonly int _samplesPerChunk;
        private readonly string _plane;
        private readonly int _maxEntities;
        private readonly long _maxBytes;
        private readonly ChunkSink _sink;

        // Latest values pushed by the game. Sampled, never buffered.
        private double _x;
        private double _y;
        private int _facing = -1;
        private string? _roomId;
        private int _bits;
        private double _ax;
        private double _ay;
        private readonly Dictionary<string, object[]> _roomBounds = new Dictionary<string, object[]>(StringComparer.Ordinal);
        private bool _warnedRoomBoundsCap;
        private string[] _actions = Array.Empty<string>();
        private bool _actionsDirty = true;

        private sealed class Entity
        {
            public string Name = "";
            public double X;
            public double Y;
            public double LastX;
            public double LastY;
            public bool EmittedThisChunk;
            public bool Despawn;
        }

        private readonly List<Entity> _entities = new List<Entity>();
        // The distinct names this session has tracked. The cap is on names,
        // so a name that despawns and comes back takes no second slot, and a
        // new session starts the count over from the names it carries.
        private readonly HashSet<string> _sessionEntityNames = new HashSet<string>(StringComparer.Ordinal);
        private bool _warnedEntityCap;

        // The one open chunk.
        private bool _chunkOpen;
        private long _t0;
        private double _chunkStartSec;
        private readonly List<object> _rows = new List<object>();
        private readonly List<object> _entityRows = new List<object>();
        private readonly List<string> _chunkRoomIds = new List<string>();
        private readonly List<object> _chunkRoomEntries = new List<object>();
        private readonly List<string> _chunkEnts = new List<string>();

        // Last sample of the current run, for the end block.
        private bool _hasSample;
        private int _lastR;
        private object _lastX = 0L;
        private object _lastY = 0L;
        private long _lastDt;
        private long _lastSampleWallMs;
        private string? _lastRoomId;

        private bool _wired;
        private bool _hasTicked;
        private double _nextSampleSec;
        private int _seq;
        private int _seg;
        private long _bytesSent;
        private bool _budgetExhausted;
        private bool _betweenRuns;
        private bool _sessionEnded;
        private bool _paused;

        public TraceSampler(int hz, TracePlane plane, int maxEntities, long maxBytesPerSession, ChunkSink sink)
        {
            _hz = Math.Max(TraceOptions.MinHz, Math.Min(TraceOptions.MaxHz, hz));
            _periodSec = 1.0 / _hz;
            _samplesPerChunk = (int)Math.Round(_hz * ChunkSeconds);
            _plane = plane == TracePlane.XZ ? "xz" : "xy";
            _maxEntities = Math.Max(0, Math.Min(TraceOptions.MaxEntitiesCap, maxEntities));
            _maxBytes = Math.Max(0, maxBytesPerSession);
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        /// <summary>Optional warning channel for the one-per-session notices.</summary>
        public Action<string>? Warn { get; set; }

        /// <summary>Sample rate after clamping.</summary>
        public int Hz => _hz;

        /// <summary>Chunks emitted so far in this session (the next chunk's <c>seq</c>).</summary>
        public int ChunkCount => _seq;

        /// <summary>The current run's index: the <c>seg</c> of its chunks.</summary>
        public int Segment => _seg;

        public bool IsPaused => _paused;

        /// <summary>
        /// True after <see cref="End"/> closed a run and before the next one
        /// opens. Nothing is sampled between runs.
        /// </summary>
        public bool IsBetweenRuns => _betweenRuns && !_sessionEnded;

        /// <summary>True after <see cref="EndSession"/>, until <see cref="ResetForNewSession"/>.</summary>
        public bool IsSessionEnded => _sessionEnded;

        public bool BudgetExhausted => _budgetExhausted;

        /// <summary>
        /// True once the game has pushed state at least once. Until then
        /// <see cref="Tick"/> samples nothing.
        /// </summary>
        public bool IsWired => _wired;

        // ───────────────────────── setters ─────────────────────────

        public void DefineActions(string[] labels)
        {
            _actions = (string[])labels.Clone();
            _actionsDirty = true;
        }

        public void SetRoom(string roomId, TraceBounds? bounds)
        {
            _wired = true;
            _roomId = roomId;
            if (!bounds.HasValue) return;

            if (!_roomBounds.ContainsKey(roomId) && _roomBounds.Count >= MaxRoomBounds)
            {
                if (!_warnedRoomBoundsCap)
                {
                    _warnedRoomBoundsCap = true;
                    Warn?.Invoke($"[Playloop] Trace: bounds for room \"{roomId}\" dropped, this client already remembers bounds for {MaxRoomBounds} rooms. The room is still sampled; Playback fits a frame to its data.");
                }
                return;
            }
            var b = bounds.Value;
            _roomBounds[roomId] = new[] { Q2(b.XMin), Q2(b.YMin), Q2(b.XMax), Q2(b.YMax) };
        }

        /// <summary>
        /// Latest position. Between runs this also opens the next run: a
        /// player who has a position again is playing again.
        /// </summary>
        public void SetPosition(double x, double y, int facing)
        {
            _wired = true;
            _x = x;
            _y = y;
            _facing = facing;
            if (_betweenRuns) Begin();
        }

        public void SetInput(int actionBits, double axisX, double axisY)
        {
            _wired = true;
            _bits = actionBits & 0xFFFF;
            _ax = Clamp1(axisX);
            _ay = Clamp1(axisY);
        }

        public void SetEntity(string name, double x, double y)
        {
            _wired = true;
            var en = Find(name);
            if (en == null)
            {
                if (!_sessionEntityNames.Contains(name))
                {
                    if (_sessionEntityNames.Count >= _maxEntities)
                    {
                        if (!_warnedEntityCap)
                        {
                            _warnedEntityCap = true;
                            Warn?.Invoke($"[Playloop] Trace: entity \"{name}\" ignored, the session already tracks {_maxEntities} names (TraceOptions.MaxEntities).");
                        }
                        return;
                    }
                    _sessionEntityNames.Add(name);
                }
                en = new Entity { Name = name };
                _entities.Add(en);
            }
            // A name cleared since the last sample is alive again: the clear
            // and the set cancel out, and the new position is what gets sampled.
            en.Despawn = false;
            en.X = x;
            en.Y = y;
        }

        public void ClearEntity(string name)
        {
            var en = Find(name);
            if (en != null) en.Despawn = true;
        }

        /// <summary>
        /// Open the next run after <see cref="End"/>. A no-op while a run is
        /// open (the first run opens by itself) and after the session ended.
        /// </summary>
        public void Begin()
        {
            if (!_betweenRuns || _sessionEnded) return;
            _betweenRuns = false;
            // The new run's first tick is sample zero of a new chunk.
            _hasTicked = false;
            _hasSample = false;
            // A despawn still pending belongs to the run that ended: the new
            // run never saw that entity, so it is dropped rather than written
            // as a despawn row. Live entities carry over and are emitted once
            // in the new run's first chunk.
            _entities.RemoveAll(en => en.Despawn);
        }

        public void Pause() => _paused = true;

        public void Resume() => _paused = false;

        // ───────────────────────── sampling ─────────────────────────

        /// <summary>
        /// Advance the sampler. Emits at most one sample per call: a frame
        /// longer than one period shows up as a large <c>dt</c>, never as
        /// back-filled rows. Idle until the first state call; the clock is
        /// anchored on the first tick after it, so that tick is sample zero.
        /// </summary>
        public void Tick(double unscaledSec, long nowUnixMs)
        {
            if (!_wired) return;
            if (_sessionEnded || _betweenRuns || _budgetExhausted || _paused) return;
            if (double.IsNaN(unscaledSec) || double.IsInfinity(unscaledSec)) return;

            if (!_hasTicked)
            {
                _hasTicked = true;
                _nextSampleSec = unscaledSec;
            }
            if (unscaledSec + Eps < _nextSampleSec) return;

            Sample(unscaledSec, nowUnixMs);

            _nextSampleSec += _periodSec;
            if (_nextSampleSec <= unscaledSec + Eps)
            {
                _nextSampleSec = unscaledSec + _periodSec;
            }
        }

        /// <summary>
        /// Close the current run with a reason. Flushes the open chunk with
        /// the <c>end</c> block and goes between runs. A run that took no
        /// sample writes nothing. Idempotent per run: a second call between
        /// runs is a no-op.
        /// </summary>
        public void End(TraceEndReason reason)
        {
            if (_betweenRuns || _sessionEnded) return;
            _betweenRuns = true;
            if (_budgetExhausted || !_hasSample) return;

            if (!_chunkOpen)
            {
                OpenChunk(0.0, _lastSampleWallMs);
                _lastDt = 0;
                _lastR = RoomIndex(_lastRoomId);
            }
            Flush(ReasonWord(reason));
            _seg++;
        }

        /// <summary>
        /// The session is ending: close the open run with
        /// <see cref="TraceEndReason.Quit"/> and stop until
        /// <see cref="ResetForNewSession"/>. Between runs this writes
        /// nothing, because the last run already has its end.
        /// </summary>
        public void EndSession()
        {
            if (_sessionEnded) return;
            End(TraceEndReason.Quit);
            _sessionEnded = true;
        }

        /// <summary>
        /// Re-arm for the next session on the same client: the wire state
        /// resets (seq, seg, budget, end) while the game's declared rooms,
        /// actions and entities carry over. The new session opens its first
        /// run straight away.
        /// </summary>
        public void ResetForNewSession()
        {
            _chunkOpen = false;
            _rows.Clear();
            _entityRows.Clear();
            _chunkRoomIds.Clear();
            _chunkRoomEntries.Clear();
            _chunkEnts.Clear();
            _hasSample = false;
            _hasTicked = false;
            _seq = 0;
            _seg = 0;
            _bytesSent = 0;
            _budgetExhausted = false;
            _betweenRuns = false;
            _sessionEnded = false;
            _actionsDirty = true;
            _warnedEntityCap = false;
            // A despawn still pending belongs to the session that just ended:
            // the new session never saw that entity, so it is dropped rather
            // than written as a despawn row. The names carried in are the
            // ones still alive, and they count against the new session's cap.
            _entities.RemoveAll(en => en.Despawn);
            _sessionEntityNames.Clear();
            foreach (var en in _entities)
            {
                en.EmittedThisChunk = false;
                _sessionEntityNames.Add(en.Name);
            }
        }

        private void Sample(double sec, long nowUnixMs)
        {
            if (_chunkOpen && sec - _chunkStartSec >= ChunkSeconds - Eps)
            {
                Flush(null);
            }
            if (!_chunkOpen) OpenChunk(sec, nowUnixMs);

            long dt = (long)Math.Round((sec - _chunkStartSec) * 1000.0);
            int r = RoomIndex(_roomId);
            object qx = Q2(_x);
            object qy = Q2(_y);
            _rows.Add(new[] { (object)dt, qx, qy, _facing, r, _bits, Q2(_ax), Q2(_ay) });
            int si = _rows.Count - 1;

            _hasSample = true;
            _lastR = r;
            _lastX = qx;
            _lastY = qy;
            _lastDt = dt;
            _lastRoomId = _roomId;
            _lastSampleWallMs = _t0 + dt;

            EmitEntities(si);

            if (_rows.Count >= _samplesPerChunk) Flush(null);
        }

        private void OpenChunk(double sec, long nowUnixMs)
        {
            _chunkOpen = true;
            _t0 = nowUnixMs;
            _chunkStartSec = sec;
            _rows.Clear();
            _entityRows.Clear();
            _chunkRoomIds.Clear();
            _chunkRoomEntries.Clear();
            _chunkEnts.Clear();
            foreach (var en in _entities) en.EmittedThisChunk = false;
        }

        private void EmitEntities(int sampleIndex)
        {
            for (int i = 0; i < _entities.Count;)
            {
                var en = _entities[i];
                if (en.Despawn)
                {
                    int ei = EntityIndex(en.Name);
                    _entityRows.Add(new object?[] { sampleIndex, ei, null, null });
                    _entities.RemoveAt(i);
                    continue;
                }
                double dx = en.X - en.LastX;
                double dy = en.Y - en.LastY;
                if (!en.EmittedThisChunk || dx * dx + dy * dy > EntityMoveThreshold * EntityMoveThreshold)
                {
                    int ei = EntityIndex(en.Name);
                    _entityRows.Add(new[] { (object)sampleIndex, ei, Q2(en.X), Q2(en.Y) });
                    en.LastX = en.X;
                    en.LastY = en.Y;
                    en.EmittedThisChunk = true;
                }
                i++;
            }
        }

        private void Flush(string? endReason)
        {
            if (!_chunkOpen) return;

            var chunk = new Dictionary<string, object>
            {
                ["v"] = FormatVersion,
                ["seq"] = _seq,
                ["seg"] = _seg,
                ["t0"] = _t0,
                ["hz"] = _hz,
                ["plane"] = _plane,
            };
            if (_seq == 0 || _actionsDirty)
            {
                chunk["acts"] = (string[])_actions.Clone();
                _actionsDirty = false;
            }
            chunk["rooms"] = _chunkRoomEntries.ToArray();
            chunk["ents"] = _chunkEnts.ToArray();
            chunk["s"] = _rows.ToArray();
            chunk["e"] = _entityRows.ToArray();
            if (endReason != null) chunk["end"] = EndBlock(endReason);

            long bytes = JsonConvert.SerializeObject(chunk).Length;
            if (endReason == null && _bytesSent + bytes > _maxBytes)
            {
                chunk["end"] = EndBlock("budget");
                _budgetExhausted = true;
            }
            _bytesSent += bytes;

            _chunkOpen = false;
            _seq++;
            _sink(chunk, _t0);
        }

        private Dictionary<string, object> EndBlock(string reason) => new Dictionary<string, object>
        {
            ["reason"] = reason,
            ["r"] = _lastR,
            ["x"] = _lastX,
            ["y"] = _lastY,
            ["dt"] = _lastDt,
        };

        private int RoomIndex(string? roomId)
        {
            if (roomId == null) return -1;
            int idx = _chunkRoomIds.IndexOf(roomId);
            if (idx >= 0) return idx;
            _chunkRoomIds.Add(roomId);
            var entry = new Dictionary<string, object> { ["id"] = roomId };
            if (_roomBounds.TryGetValue(roomId, out var b)) entry["b"] = b;
            _chunkRoomEntries.Add(entry);
            return _chunkRoomIds.Count - 1;
        }

        private int EntityIndex(string name)
        {
            int idx = _chunkEnts.IndexOf(name);
            if (idx >= 0) return idx;
            _chunkEnts.Add(name);
            return _chunkEnts.Count - 1;
        }

        private Entity? Find(string name)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                if (string.Equals(_entities[i].Name, name, StringComparison.Ordinal)) return _entities[i];
            }
            return null;
        }

        internal static string ReasonWord(TraceEndReason reason)
        {
            switch (reason)
            {
                case TraceEndReason.Death: return "death";
                case TraceEndReason.LevelComplete: return "level_complete";
                case TraceEndReason.Quit: return "quit";
                case TraceEndReason.Timeout: return "timeout";
                default: return "unknown";
            }
        }

        private static double Clamp1(double v)
        {
            if (double.IsNaN(v)) return 0.0;
            return v < -1.0 ? -1.0 : (v > 1.0 ? 1.0 : v);
        }

        private static double Q2d(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            return Math.Round(v * 100.0, MidpointRounding.AwayFromZero) / 100.0;
        }

        /// <summary>
        /// Two-decimal quantization. Integral results are boxed as
        /// <see cref="long"/> so they serialize without a fractional part,
        /// which keeps the chunk bytes identical across JSON writers.
        /// </summary>
        internal static object Q2(double v)
        {
            double q = Q2d(v);
            if (q == Math.Floor(q) && Math.Abs(q) < 1e15) return (long)q;
            return q;
        }
    }
}
