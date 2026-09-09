#nullable enable
using System;
using System.Collections.Generic;

namespace Playloop.Telemetry
{
    /// <summary>
    /// Session-state accumulator.
    ///
    /// <para>
    /// Canonical four-event SDK contract:
    /// <c>session_start</c> / <c>event</c> / <c>session_heartbeat</c> /
    /// <c>session_summary</c>. The snapshot lives at the event's TOP level:
    /// no nested <c>summary:</c> sub-key inside heartbeats, no
    /// <c>_pl_source: "auto"</c> precedence marker, no separate
    /// summary event family.
    /// </para>
    ///
    /// <para>Mental model:</para>
    /// <list type="bullet">
    ///   <item><c>client.Telemetry.Track(name, props)</c> emits
    ///   IMMUTABLE historical events.</item>
    ///   <item><c>client.State.SetState({...})</c> /
    ///   <c>client.State.IncrementState({...})</c> mutate a state
    ///   ACCUMULATOR.</item>
    ///   <item>The SDK fires that snapshot on every heartbeat AND once
    ///   at session end (auto-flush on
    ///   <c>Application.quitting</c> / <c>OnApplicationPause(true)</c>
    ///   on mobile, or manual via
    ///   <c>client.EndSession()</c>).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Reliability (best-effort, not guaranteed):</b>
    /// Clean shutdown → auto-flush fires <c>session_summary</c> with the
    /// final snapshot. Force-quit / OS-eviction → the SDK's last
    /// <c>session_heartbeat</c> event (which already carried the snapshot at
    /// top level) serves as the recovery source server-side. If a
    /// field MUST survive a crash, also fire it as a normal
    /// <c>Telemetry.Track()</c> event.
    /// </para>
    /// </summary>
    public sealed class StateApi
    {
        private readonly TelemetryApi _telemetry;
        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _buffer = new Dictionary<string, object>();
        // Tracks the FIRST flush of this accumulator's lifecycle so the
        // shutdown hook + explicit EndSession() can fire in any order
        // without ever producing two session_summary events. Cleared by
        // ClearState().
        private bool _flushedOnce;

        internal StateApi(TelemetryApi telemetry)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <summary>
        /// Merge <paramref name="partial"/> into the accumulator.
        /// Last-writer-wins per key.
        ///
        /// <para>
        /// Values of <c>null</c> explicitly DELETE the key from the
        /// buffer. Null / empty input is a no-op.
        /// </para>
        /// </summary>
        public void SetState(IReadOnlyDictionary<string, object?>? partial)
        {
            if (partial == null) return;
            lock (_lock)
            {
                foreach (var kvp in partial)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    if (kvp.Value == null)
                    {
                        _buffer.Remove(kvp.Key);
                        continue;
                    }
                    _buffer[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>
        /// Numeric-add into the accumulator. Falls back to
        /// <see cref="SetState"/> semantics (last-writer-wins) for
        /// non-numeric values or non-numeric existing keys.
        ///
        /// <para>Useful for counters:</para>
        /// <code>
        /// client.State.IncrementState(new Dictionary&lt;string, object?&gt; {
        ///   { "harvests", 1 }, { "jar_clicks", 1 }
        /// });
        /// </code>
        /// </summary>
        public void IncrementState(IReadOnlyDictionary<string, object?>? partial)
        {
            if (partial == null) return;
            lock (_lock)
            {
                foreach (var kvp in partial)
                {
                    if (string.IsNullOrEmpty(kvp.Key)) continue;
                    if (kvp.Value == null)
                    {
                        _buffer.Remove(kvp.Key);
                        continue;
                    }

                    var value = kvp.Value;
                    _buffer.TryGetValue(kvp.Key, out var existing);

                    if (IsNumeric(value) && IsNumeric(existing))
                    {
                        _buffer[kvp.Key] = AddNumbers(existing!, value);
                    }
                    else if (IsNumeric(value) && existing == null && !_buffer.ContainsKey(kvp.Key))
                    {
                        _buffer[kvp.Key] = value;
                    }
                    else
                    {
                        // Non-numeric value, or existing key holds
                        // non-numeric data. Fall through to SetState
                        // semantics.
                        _buffer[kvp.Key] = value;
                    }
                }
            }
        }

        /// <summary>
        /// Reset the accumulator mid-session. Drops all buffered fields
        /// AND clears the flush guard so a subsequent
        /// <see cref="FlushSessionEnd"/> can fire again. Useful when a
        /// session-restart-without-EndSession is needed.
        /// </summary>
        public void ClearState()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _flushedOnce = false;
            }
        }

        /// <summary>
        /// Defensive copy of the current accumulator state. Used by
        /// the heartbeat emitter to attach the snapshot to every beat
        /// at TOP level: no nested <c>summary:</c> sub-key.
        ///
        /// Returns an empty dictionary when no state has been set.
        /// Heartbeats fire regardless of state.
        /// </summary>
        public IReadOnlyDictionary<string, object> Snapshot()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>(_buffer);
            }
        }

        /// <summary>
        /// Flush the accumulator as a <c>session_summary</c> event.
        /// Idempotent: once flushed, subsequent calls within the same
        /// accumulator lifecycle are no-ops until
        /// <see cref="ClearState"/> resets the state.
        ///
        /// <para>
        /// Unlike the v0.2 <c>SummaryApi.Flush()</c>, this ALWAYS fires
        /// (even with an empty buffer). The cost of an empty
        /// <c>session_summary</c> is one telemetry row; firing it always
        /// means a clean session end is recorded directly rather than
        /// having to be recovered from the last heartbeat.
        /// </para>
        /// </summary>
        public void FlushSessionEnd()
        {
            Dictionary<string, object> snapshot;
            lock (_lock)
            {
                if (_flushedOnce) return;
                _flushedOnce = true;
                snapshot = new Dictionary<string, object>(_buffer);
            }
            _telemetry.Track("session_summary", snapshot);
        }

        /// <summary>Number of buffered fields. Exposed for diagnostics.</summary>
        public int FieldCount
        {
            get { lock (_lock) return _buffer.Count; }
        }

        /// <summary>Whether <see cref="FlushSessionEnd"/> has already fired in this accumulator lifecycle.</summary>
        public bool HasFlushed
        {
            get { lock (_lock) return _flushedOnce; }
        }

        // ----- internals -------------------------------------------------

        private static bool IsNumeric(object? v)
        {
            // Exclude bool. Bool is convertible to int (true=1, false=0)
            // via Convert.ToDouble but devs don't want incrementing a
            // bool to numerically add 1. Match Python's bool guard.
            if (v == null || v is bool) return false;
            return v is sbyte or byte or short or ushort or int or uint
                or long or ulong or float or double or decimal;
        }

        private static object AddNumbers(object a, object b)
        {
            // Use double precision to keep things simple. Int + int
            // accumulators that overflow are a problem for the dev,
            // not the SDK. Decimal + int collapses cleanly to double.
            var ad = Convert.ToDouble(a);
            var bd = Convert.ToDouble(b);
            var sum = ad + bd;
            // Preserve int-likeness when both inputs were integral
            // and the sum fits in long. Devs assert on int values
            // in tests; demoting harvests=5 to harvests=5.0 would
            // be surprising.
            if (IsIntegral(a) && IsIntegral(b) && sum >= long.MinValue && sum <= long.MaxValue
                && Math.Floor(sum) == sum)
            {
                return (long)sum;
            }
            return sum;
        }

        private static bool IsIntegral(object v) =>
            v is sbyte or byte or short or ushort or int or uint or long or ulong;
    }
}
