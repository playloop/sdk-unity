#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using Playloop.Telemetry;

namespace Playloop.Tests
{
    /// <summary>
    /// Tests for <see cref="StateApi"/>: the session-state accumulator
    /// that replaces v0.2 <c>SummaryApi</c>. Asserts against the
    /// SDK event contract.
    ///
    /// Tests build a real <see cref="TelemetryApi"/> with the no-op
    /// MockHttpHandler so we can inspect the buffered events via
    /// <see cref="TelemetryApi.SnapshotPending"/>. No HTTP fires; the
    /// asserts read against the in-memory buffer.
    /// </summary>
    [TestFixture]
    public class StateApiTests
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

        // ----- SetState ------------------------------------------------

        [Test]
        public void SetState_Merges_LastWriterWins()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 }, { "level", 1 } });
            _state.SetState(new Dictionary<string, object?> { { "souls", 250 }, { "harvests", 3 } });
            var snap = _state.Snapshot();
            Assert.AreEqual(250, snap["souls"]);
            Assert.AreEqual(1, snap["level"]);
            Assert.AreEqual(3, snap["harvests"]);
        }

        [Test]
        public void SetState_NullValue_DeletesKey()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 }, { "void_pact", true } });
            _state.SetState(new Dictionary<string, object?> { { "void_pact", null } });
            var snap = _state.Snapshot();
            Assert.IsFalse(snap.ContainsKey("void_pact"));
            Assert.AreEqual(100, snap["souls"]);
        }

        [Test]
        public void SetState_NullInput_IsNoop()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            _state.SetState(null);
            Assert.AreEqual(100, _state.Snapshot()["souls"]);
        }

        [Test]
        public void SetState_EmptyKeysIgnored()
        {
            _state.SetState(new Dictionary<string, object?> { { "", "drop" }, { "valid", 1 } });
            Assert.IsFalse(_state.Snapshot().ContainsKey(""));
            Assert.AreEqual(1, _state.Snapshot()["valid"]);
        }

        // ----- IncrementState -----------------------------------------

        [Test]
        public void IncrementState_NumericallyAdds()
        {
            _state.SetState(new Dictionary<string, object?> { { "harvests", 5 } });
            _state.IncrementState(new Dictionary<string, object?> { { "harvests", 2 } });
            _state.IncrementState(new Dictionary<string, object?> { { "harvests", 3 } });
            Assert.AreEqual(10L, _state.Snapshot()["harvests"]);
        }

        [Test]
        public void IncrementState_FirstTimeSeedsValue()
        {
            _state.IncrementState(new Dictionary<string, object?> { { "jar_clicks", 1 } });
            _state.IncrementState(new Dictionary<string, object?> { { "jar_clicks", 1 } });
            Assert.AreEqual(2L, _state.Snapshot()["jar_clicks"]);
        }

        [Test]
        public void IncrementState_NonNumericUpserts()
        {
            _state.SetState(new Dictionary<string, object?> { { "stage", "tutorial" } });
            _state.IncrementState(new Dictionary<string, object?> { { "stage", "act1" } });
            Assert.AreEqual("act1", _state.Snapshot()["stage"]);
        }

        [Test]
        public void IncrementState_BoolDoesNotNumericallyAdd()
        {
            // bool is convertible to int in C#. Guard so incrementing
            // a bool upserts (doesn't compute true+false=1).
            _state.SetState(new Dictionary<string, object?> { { "flag", true } });
            _state.IncrementState(new Dictionary<string, object?> { { "flag", false } });
            Assert.AreEqual(false, _state.Snapshot()["flag"]);
        }

        // ----- ClearState ---------------------------------------------

        [Test]
        public void ClearState_EmptiesBufferAndResetsFlushGuard()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            _state.FlushSessionEnd();
            Assert.IsTrue(_state.HasFlushed);

            _state.ClearState();
            Assert.IsFalse(_state.HasFlushed);
            Assert.AreEqual(0, _state.FieldCount);

            _state.SetState(new Dictionary<string, object?> { { "souls", 200 } });
            _state.FlushSessionEnd();

            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(2, ev.Count);
            Assert.AreEqual(200, ev[1].Data!["souls"]);
        }

        // ----- Snapshot -----------------------------------------------

        [Test]
        public void Snapshot_IsDefensiveCopy()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            var snap = (Dictionary<string, object>)_state.Snapshot();
            snap["souls"] = 999;
            Assert.AreEqual(100, _state.Snapshot()["souls"]);
        }

        [Test]
        public void Snapshot_EmptyBufferReturnsEmpty()
        {
            Assert.AreEqual(0, _state.Snapshot().Count);
        }

        // ----- FlushSessionEnd ----------------------------------------

        [Test]
        public void FlushSessionEnd_FiresOneSessionEndWithSnapshotAtTopLevel()
        {
            _state.SetState(new Dictionary<string, object?> {
                { "souls", 764482 },
                { "void_pact", true },
                { "demo_completed", true },
            });
            _state.FlushSessionEnd();

            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("session_summary", ev[0].Name);
            var data = ev[0].Data!;
            Assert.AreEqual(764482, data["souls"]);
            Assert.AreEqual(true, data["void_pact"]);
            Assert.AreEqual(true, data["demo_completed"]);
        }

        [Test]
        public void FlushSessionEnd_Idempotent()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            _state.FlushSessionEnd();
            _state.FlushSessionEnd();
            _state.FlushSessionEnd();
            Assert.AreEqual(1, _telemetry.SnapshotPending().Count);
        }

        [Test]
        public void FlushSessionEnd_FiresEvenWithEmptyBuffer()
        {
            // Drift guard vs v0.2 SummaryApi which suppressed empty-
            // buffer flushes. The new contract always emits. Firing
            // session_summary always means a clean session end is
            // recorded directly rather than recovered from the last
            // heartbeat.
            _state.FlushSessionEnd();
            var ev = _telemetry.SnapshotPending();
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("session_summary", ev[0].Name);
            Assert.IsTrue(ev[0].Data == null || ev[0].Data!.Count == 0);
        }

        [Test]
        public void FlushSessionEnd_NoPlSourceMarker()
        {
            _state.SetState(new Dictionary<string, object?> { { "souls", 100 } });
            _state.FlushSessionEnd();
            var data = _telemetry.SnapshotPending()[0].Data!;
            Assert.IsFalse(data.ContainsKey("_pl_source"));
        }
    }
}
