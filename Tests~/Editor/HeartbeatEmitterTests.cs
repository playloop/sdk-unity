#nullable enable
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Playloop.Telemetry;

namespace Playloop.Tests
{
    /// <summary>
    /// Tests for <see cref="HeartbeatEmitter"/>: the SDK-owned
    /// heartbeat emitter. Per the SDK event contract:
    ///
    /// <list type="bullet">
    ///   <item>One <c>session_heartbeat</c> event every <c>intervalSec</c> seconds</item>
    ///   <item>Each beat carries <c>tickNumber</c> (monotonic),
    ///     <c>playTimeSec</c>, and the full current state snapshot at top level</item>
    ///   <item><c>intervalSec: 0</c> disables emission</item>
    ///   <item><c>Start()</c> is idempotent; <c>Stop()</c> is idempotent</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class HeartbeatEmitterTests
    {
        private TelemetryApi _telemetry = null!;
        private StateApi _state = null!;

        [SetUp]
        public void SetUp()
        {
            var handler = new MockHttpHandler();
            var http = new Http.HttpClient(handler, "https://api.test", "pl_ik_test", "test");
            _telemetry = new TelemetryApi(http, 60_000, 200, "device-test", AutoInstrumentSettings.Disabled);
            _state = new StateApi(_telemetry);
        }

        [TearDown]
        public void TearDown()
        {
            _telemetry.Dispose();
        }

        // ----- EmitOnce -----------------------------------------------

        [Test]
        public void EmitOnce_FiresHeartbeatWithTickPlayTimeAndSnapshot()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 }, { "level", 1 } });
            var em = new HeartbeatEmitter(_telemetry, _state, 60.0);
            em.EmitOnce();

            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("session_heartbeat", ev[0].Name);
            var data = ev[0].Data!;
            Assert.AreEqual(1, data["tickNumber"]);
            Assert.IsTrue(data.ContainsKey("playTimeSec"));
            Assert.AreEqual(100, data["souls"]);
            Assert.AreEqual(1, data["level"]);
        }

        [Test]
        public void EmitOnce_TickNumberMonotonic()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 60.0);
            em.EmitOnce();
            em.EmitOnce();
            em.EmitOnce();

            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(1, ev[0].Data!["tickNumber"]);
            Assert.AreEqual(2, ev[1].Data!["tickNumber"]);
            Assert.AreEqual(3, ev[2].Data!["tickNumber"]);
        }

        [Test]
        public void EmitOnce_SnapshotTakenAtEmitTime()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 60.0);
            em.EmitOnce();
            _state.SetState(new Dictionary<string, object?> { { "souls", 500 } });
            em.EmitOnce();

            var ev = _telemetry.SnapshotPending();
            Assert.IsFalse(ev[0].Data!.ContainsKey("souls"));
            Assert.AreEqual(500, ev[1].Data!["souls"]);
        }

        [Test]
        public void EmitOnce_NoSummarySubkey()
        {
            // Drift guard vs v0.2 SummaryApi heartbeat-carry shape,
            // which attached state under `payload.summary`. The new
            // contract puts state directly at the event root.
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            var em = new HeartbeatEmitter(_telemetry, _state, 60.0);
            em.EmitOnce();

            var data = _telemetry.SnapshotPending()[0].Data!;
            Assert.IsFalse(data.ContainsKey("summary"));
            Assert.AreEqual(100, data["souls"]);
        }

        [Test]
        public void EmitOnce_EmptyStateStillFires()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 60.0);
            em.EmitOnce();

            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("session_heartbeat", ev[0].Name);
            Assert.AreEqual(1, ev[0].Data!["tickNumber"]);
        }

        // ----- Timer behavior -----------------------------------------

        [Test]
        public void Start_FiresBeatsOnInterval()
        {
            // Use a tight interval (50ms) so the test runs quickly.
            var em = new HeartbeatEmitter(_telemetry, _state, 0.05);
            em.Start();
            Assert.IsTrue(em.IsRunning);
            // Wait for ~3 beats to fire.
            Thread.Sleep(180);
            em.Stop();

            var count = _telemetry.SnapshotPending().Count;
            Assert.GreaterOrEqual(count, 2, "expected at least two beats from a 50ms interval timer");
        }

        [Test]
        public void Start_IntervalZeroDisables()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 0);
            em.Start();
            Assert.IsFalse(em.IsRunning);
            Thread.Sleep(120);
            Assert.AreEqual(0, _telemetry.SnapshotPending().Count);
        }

        [Test]
        public void Start_IsIdempotent()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 0.05);
            em.Start();
            em.Start();
            em.Start();
            Thread.Sleep(80);
            em.Stop();
            // If start were not idempotent we'd see overlapping timers.
            var count = _telemetry.SnapshotPending().Count;
            Assert.LessOrEqual(count, 3);
        }

        [Test]
        public void Stop_HaltsTimer()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 0.05);
            em.Start();
            Thread.Sleep(120);
            em.Stop();
            Assert.IsFalse(em.IsRunning);

            var baseline = _telemetry.SnapshotPending().Count;
            Thread.Sleep(120);
            Assert.AreEqual(baseline, _telemetry.SnapshotPending().Count);
        }

        [Test]
        public void Stop_IsIdempotent()
        {
            var em = new HeartbeatEmitter(_telemetry, _state, 0.05);
            em.Start();
            em.Stop();
            em.Stop();
            em.Stop();
            // No exception thrown.
            Assert.IsFalse(em.IsRunning);
        }
    }
}
