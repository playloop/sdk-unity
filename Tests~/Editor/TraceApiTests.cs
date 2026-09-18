#nullable enable
#if PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playloop.Trace;

namespace Playloop.Tests
{
    /// <summary>
    /// The Trace's contract outside the fixture: gating, validation, the
    /// sampler's clock rules and the budget.
    /// </summary>
    [TestFixture]
    public class TraceApiTests
    {
        private const long Wall = 1758140000000L;

        private static PlayloopClient NewClient(MockHttpHandler handler, string environment = "playtest", Action<TraceOptions>? trace = null)
        {
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = handler,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                RetryAttempts = 1,
                EnableCrashReporting = false,
                Environment = environment,
            };
            trace?.Invoke(options.Trace);
            var client = new PlayloopClient(options);
            client.Telemetry.ClearBuffer();
            return client;
        }

        // "dev" is the options default, which the SDK auto-derives from the
        // build; with no engine present that derive lands on "production".
        // Tests use an explicit "playtest" slug, the way a playtest build does.
        [Test]
        public void Auto_IsOnOutsideProduction_OffInProduction()
        {
            using var dev = NewClient(new MockHttpHandler(), "playtest");
            Assert.AreEqual(TraceStatus.Active, dev.Trace.Status);
            Assert.IsTrue(dev.Trace.IsActive);

            using var prod = NewClient(new MockHttpHandler(), "production");
            Assert.AreEqual(TraceStatus.OffByEnvironment, prod.Trace.Status);
            Assert.IsFalse(prod.Trace.IsActive);

            using var forced = NewClient(new MockHttpHandler(), "production", t => t.Mode = TraceMode.On);
            Assert.AreEqual(TraceStatus.Active, forced.Trace.Status);

            using var off = NewClient(new MockHttpHandler(), "dev", t => t.Mode = TraceMode.Off);
            Assert.AreEqual(TraceStatus.OffByOption, off.Trace.Status);
        }

        [Test]
        public void OffInProduction_ReportsOneStateEvent()
        {
            using var client = NewClient(new MockHttpHandler(), "production");
            client.Trace.SetPosition(1f, 2f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.Tick(0.1, Wall + 100);

            var pending = client.Telemetry.SnapshotPending();
            Assert.AreEqual(1, pending.Count, "exactly one trace_state per session");
            Assert.AreEqual("trace_state", pending[0].Name);
            Assert.AreEqual(false, pending[0].Data!["enabled"]);
            Assert.AreEqual("production", pending[0].Data!["reason"]);
        }

        [Test]
        public void Disabled_IsInert_ButStillValidatesArguments()
        {
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });
            Assert.AreEqual(TraceStatus.Disabled, client.Trace.Status);

            Assert.DoesNotThrow(() => client.Trace.DefineActions("move", "jump"));
            Assert.DoesNotThrow(() => client.Trace.SetRoom("hall"));
            Assert.DoesNotThrow(() => client.Trace.SetPosition(1f, 1f, 45f));
            Assert.DoesNotThrow(() => client.Trace.SetInput(1, 1f, 0f));
            Assert.DoesNotThrow(() => client.Trace.SetEntity("key", 1f, 1f));
            Assert.DoesNotThrow(() => client.Trace.Tick(0.0));
            Assert.DoesNotThrow(() => client.Trace.End(TraceEndReason.Quit));

            Assert.Throws<ArgumentException>(() => client.Trace.DefineActions("Move"));
            Assert.Throws<ArgumentException>(() => client.Trace.SetRoom("Hall 1"));
            Assert.Throws<ArgumentException>(() => client.Trace.SetEntity("KeyCode.Space", 0f, 0f));
            Assert.AreEqual(0, client.Telemetry.PendingCount);
        }

        [Test]
        public void Labels_AreSlugsFromThreeFamilies()
        {
            Assert.IsTrue(TraceLabels.IsActionLabel("move"));
            Assert.IsTrue(TraceLabels.IsActionLabel("a_1"));
            Assert.IsFalse(TraceLabels.IsActionLabel("Move"));
            Assert.IsFalse(TraceLabels.IsActionLabel("1move"));
            Assert.IsFalse(TraceLabels.IsActionLabel("move:1"));
            Assert.IsFalse(TraceLabels.IsActionLabel(new string('a', 25)));
            Assert.IsTrue(TraceLabels.IsActionLabel(new string('a', 24)));

            Assert.IsTrue(TraceLabels.IsRoomId("z3:pylon_corridors"));
            Assert.IsTrue(TraceLabels.IsRoomId("cave-2"));
            Assert.IsFalse(TraceLabels.IsRoomId("Cave"));
            Assert.IsFalse(TraceLabels.IsRoomId(""));
            Assert.IsFalse(TraceLabels.IsRoomId(new string('r', 65)));

            Assert.IsTrue(TraceLabels.IsEntityName("key"));
            Assert.IsFalse(TraceLabels.IsEntityName("key-1"));
            Assert.IsFalse(TraceLabels.IsEntityName(new string('e', 33)));
        }

        [Test]
        public void DefineActions_RejectsMoreThanSixteenAndRepeats()
        {
            using var client = NewClient(new MockHttpHandler());
            var seventeen = Enumerable.Range(0, 17).Select(i => $"a{i}").ToArray();
            Assert.Throws<ArgumentException>(() => client.Trace.DefineActions(seventeen));
            Assert.Throws<ArgumentException>(() => client.Trace.DefineActions("move", "move"));
            Assert.DoesNotThrow(() => client.Trace.DefineActions(Enumerable.Range(0, 16).Select(i => $"a{i}").ToArray()));
        }

        [Test]
        public void Sampler_OneSamplePerPeriod_NoBackfillOnAHitch()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.SetRoom("hall", null);

            sampler.Tick(0.00, Wall);
            sampler.Tick(0.05, Wall + 50);   // inside the period: no sample
            sampler.Tick(0.10, Wall + 100);
            sampler.Tick(0.20, Wall + 200);
            sampler.Tick(1.20, Wall + 1200); // a one-second hitch: ONE sample, dt shows it
            sampler.Tick(1.25, Wall + 1250); // still inside the re-anchored period
            sampler.Tick(1.30, Wall + 1300);
            sampler.End(TraceEndReason.Timeout);

            Assert.AreEqual(1, chunks.Count);
            var rows = ((object[])chunks[0]["s"]).Cast<object[]>().ToList();
            CollectionAssert.AreEqual(new long[] { 0, 100, 200, 1200, 1300 }, rows.Select(r => (long)r[0]).ToArray());
            Assert.AreEqual("timeout", ((Dictionary<string, object>)chunks[0]["end"])["reason"]);
        }

        [Test]
        public void Sampler_Quantizes_TwoDecimalsIntegerDegreesSixteenBits()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XZ, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.SetPosition(12.3456, -0.005, 359);
            sampler.SetInput(0x1FFFF, 1.7, -0.333);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.LevelComplete);

            var row = ((object[])chunks[0]["s"]).Cast<object[]>().Single();
            Assert.AreEqual(12.35, row[1]);
            Assert.AreEqual(-0.01, row[2]);
            Assert.AreEqual(359, row[3]);
            Assert.AreEqual(-1, row[4], "no room set");
            Assert.AreEqual(0xFFFF, row[5], "bits above 16 are dropped");
            Assert.AreEqual(1L, row[6], "axes clamp to [-1, 1] and integral values serialize as integers");
            Assert.AreEqual(-0.33, row[7]);
            Assert.AreEqual("xz", chunks[0]["plane"]);
            Assert.AreEqual("level_complete", ((Dictionary<string, object>)chunks[0]["end"])["reason"]);
        }

        [Test]
        public void Sampler_ChunksEveryFiveSeconds_AndOnlyFirstChunkCarriesActs()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(20, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.DefineActions(new[] { "move" });
            sampler.SetPosition(0, 0, -1); // the first state call starts sampling
            for (int ms = 0; ms < 12000; ms += 50) sampler.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(2, chunks.Count, "two full chunks of 100 samples at 20 Hz; the third is still open");
            Assert.AreEqual(100, ((object[])chunks[0]["s"]).Length);
            Assert.AreEqual(100, ((object[])chunks[1]["s"]).Length);
            Assert.IsTrue(chunks[0].ContainsKey("acts"));
            Assert.IsFalse(chunks[1].ContainsKey("acts"));

            sampler.DefineActions(new[] { "move", "dash" });
            sampler.End(TraceEndReason.Quit);
            Assert.AreEqual(3, chunks.Count);
            CollectionAssert.AreEqual(new[] { "move", "dash" }, (string[])chunks[2]["acts"]);
            Assert.AreEqual(2, chunks[2]["seq"]);
        }

        [Test]
        public void Sampler_StopsAtTheBudget_WithBudgetReason()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, maxBytesPerSession: 2000, (c, t0) => chunks.Add(c));
            sampler.SetPosition(0, 0, -1); // the first state call starts sampling
            for (int ms = 0; ms < 30000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            Assert.IsTrue(sampler.BudgetExhausted);
            Assert.AreEqual(2, chunks.Count, "the second chunk crosses a 2000-byte budget and is the last");
            Assert.AreEqual("budget", ((Dictionary<string, object>)chunks[1]["end"])["reason"]);
            sampler.End(TraceEndReason.Death);
            Assert.AreEqual(2, chunks.Count, "nothing after the budget chunk");
        }

        [Test]
        public void Budget_ReportsStateEvent_ThroughTheClient()
        {
            var handler = new MockHttpHandler();
            using var client = NewClient(handler, "playtest", t => t.MaxBytesPerSession = 2000);
            client.Trace.SetPosition(0f, 0f); // the first state call starts sampling
            for (int ms = 0; ms < 30000; ms += 100) client.Trace.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(TraceStatus.BudgetExhausted, client.Trace.Status);
            var names = client.Telemetry.SnapshotPending().Select(e => e.Name).ToList();
            Assert.AreEqual(2, names.Count(n => n == "trace_chunk"));
            Assert.AreEqual(1, names.Count(n => n == "trace_state"));
            var state = client.Telemetry.SnapshotPending().Single(e => e.Name == "trace_state");
            Assert.AreEqual("budget", state.Data!["reason"]);
        }

        [Test]
        public void Pause_DropsSamples_ResumeContinues()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.SetPosition(0, 0, -1); // the first state call starts sampling
            sampler.Tick(0.0, Wall);
            sampler.Pause();
            for (int ms = 100; ms < 1000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            sampler.Resume();
            sampler.Tick(1.0, Wall + 1000);
            sampler.End(TraceEndReason.Quit);
            var rows = ((object[])chunks[0]["s"]).Cast<object[]>().ToList();
            CollectionAssert.AreEqual(new long[] { 0, 1000 }, rows.Select(r => (long)r[0]).ToArray());
        }

        [Test]
        public void Entities_CapPerSession_NinthNameIgnoredWithOneWarning()
        {
            var warnings = new List<string>();
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c))
            {
                Warn = warnings.Add,
            };
            for (int i = 0; i < 10; i++) sampler.SetEntity($"e{i}", i, i);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.Quit);
            Assert.AreEqual(8, ((string[])chunks[0]["ents"]).Length);
            Assert.AreEqual(1, warnings.Count);
        }

        [Test]
        public void EndFromSessionEnd_IsQuit_AndTheNextSessionStartsFresh()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"sessionId\":\"s1\"}");
            using var client = NewClient(handler);
            client.Telemetry.StartSession();
            client.Trace.SetRoom("hall");
            client.Trace.Tick(0.0, Wall);
            client.Telemetry.EndSessionAsync().GetAwaiter().GetResult();

            var telemetryCall = handler.Calls.Single(c => c.Url.EndsWith("/api/telemetry", StringComparison.Ordinal));
            var body = JObject.Parse(Encoding.UTF8.GetString(telemetryCall.JsonBody!));
            var chunk = body["events"]!.Single(e => e["name"]!.Value<string>() == "trace_chunk")["data"]!;
            Assert.AreEqual("quit", chunk["end"]!["reason"]!.Value<string>());
            Assert.AreEqual(1, body["traceChunks"]!.Value<int>(), "top-level, the field the server reads on an append");
            Assert.AreEqual(1, body["sessionMetadata"]!["traceChunks"]!.Value<int>());

            Assert.AreEqual(TraceStatus.Active, client.Trace.Status, "re-armed for the next session");
            client.Telemetry.StartSession();
            client.Trace.Tick(10.0, Wall + 10000);
            client.Trace.End(TraceEndReason.LevelComplete);
            var next = client.Telemetry.SnapshotPending().Single(e => e.Name == "trace_chunk");
            Assert.AreEqual(0, next.Data!["seq"], "seq restarts per session");
        }

        [Test]
        public void EndBlock_WithoutAnOpenChunk_UsesTheLastSample()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.SetRoom("vault", null);
            sampler.SetPosition(50, 5, 0);
            for (int ms = 0; ms < 5000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(1, chunks.Count, "the 50th sample closed the chunk");
            sampler.End(TraceEndReason.Death);
            Assert.AreEqual(2, chunks.Count);
            var last = chunks[1];
            Assert.AreEqual(0, ((object[])last["s"]).Length);
            Assert.AreEqual(Wall + 4900, last["t0"], "an end-only chunk is stamped at the last sample");
            var end = (Dictionary<string, object>)last["end"];
            Assert.AreEqual("death", end["reason"]);
            Assert.AreEqual(50L, end["x"]);
            Assert.AreEqual(0, end["r"]);
            Assert.AreEqual("vault", ((Dictionary<string, object>)((object[])last["rooms"])[0])["id"]);
        }

        [Test]
        public void Sampler_IdlesUntilTheFirstStateCall()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            sampler.DefineActions(new[] { "move" }); // a declaration, not state

            Assert.IsFalse(sampler.IsWired);
            for (int ms = 0; ms < 12000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(0, chunks.Count, "the driver ticks from the first frame; an unwired game sends nothing");
            sampler.End(TraceEndReason.Quit);
            Assert.AreEqual(0, chunks.Count, "no end-only chunk either");

            // A fresh sampler: idle ticks, then the first state call. The clock
            // anchors on the next tick, so that tick is sample zero at dt 0.
            chunks.Clear();
            sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));
            for (int ms = 0; ms < 3000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            sampler.SetPosition(1, 2, 0);
            Assert.IsTrue(sampler.IsWired);
            for (int ms = 3000; ms <= 3500; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            sampler.End(TraceEndReason.Quit);

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(Wall + 3000, chunks[0]["t0"], "the chunk starts at the first tick after the state call");
            var rows = ((object[])chunks[0]["s"]).Cast<object[]>().ToList();
            CollectionAssert.AreEqual(new long[] { 0, 100, 200, 300, 400, 500 }, rows.Select(r => (long)r[0]).ToArray());
            Assert.AreEqual(1L, rows[0][1]);
            Assert.AreEqual(2L, rows[0][2]);
        }

        [Test]
        public void Unwired_SendsNoChunkAndNoState_ThroughTheClient()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"sessionId\":\"s1\"}");
            using var client = NewClient(handler);
            client.Telemetry.StartSession();

            for (int ms = 0; ms < 12000; ms += 100) client.Trace.Tick(ms / 1000.0, Wall + ms);

            Assert.AreEqual(TraceStatus.Active, client.Trace.Status, "the Trace is on; it is waiting for state");
            var pending = client.Telemetry.SnapshotPending().Select(e => e.Name).ToList();
            Assert.IsFalse(pending.Any(n => n.StartsWith("trace_", StringComparison.Ordinal)), "nothing trace-shaped is buffered");

            client.Telemetry.EndSessionAsync().GetAwaiter().GetResult();

            foreach (var call in handler.Calls.Where(c => c.Url.EndsWith("/api/telemetry", StringComparison.Ordinal)))
            {
                var body = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
                var names = (body["events"] ?? new JArray()).Select(e => e["name"]!.Value<string>()).ToList();
                Assert.IsFalse(names.Any(n => n!.StartsWith("trace_", StringComparison.Ordinal)), "no trace_chunk and no trace_state on the wire");
                Assert.IsNull(body["sessionMetadata"]?["traceChunks"], "no trace count is stamped for an unwired game");
            }
        }

        [Test]
        public void Entities_SetAfterClear_BeforeTheNextSample_StaysAlive()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));

            // "key" is sampled once, cleared, then set again before the next
            // sample. "gem" is cleared and set again before it was ever
            // sampled. "bat" is cleared and stays cleared.
            sampler.SetEntity("key", 1, 1);
            sampler.SetEntity("bat", 7, 7);
            sampler.Tick(0.0, Wall);

            sampler.ClearEntity("key");
            sampler.SetEntity("key", 2, 2);
            sampler.SetEntity("gem", 1, 1);
            sampler.ClearEntity("gem");
            sampler.SetEntity("gem", 3, 3);
            sampler.ClearEntity("bat");
            sampler.Tick(0.1, Wall + 100);
            sampler.End(TraceEndReason.Quit);

            Assert.AreEqual(1, chunks.Count);
            CollectionAssert.AreEqual(new[] { "key", "bat", "gem" }, (string[])chunks[0]["ents"]);
            var rows = ((object[])chunks[0]["e"]).Cast<object?[]>().ToList();

            // Sample 0: key at (1, 1), bat at (7, 7).
            CollectionAssert.AreEqual(new object?[] { 0, 0, 1L, 1L }, rows[0]);
            CollectionAssert.AreEqual(new object?[] { 0, 1, 7L, 7L }, rows[1]);
            // Sample 1: key moved to (2, 2) and is still alive, bat despawns
            // with a null position, gem appears at its final (3, 3).
            CollectionAssert.AreEqual(new object?[] { 1, 0, 2L, 2L }, rows[2]);
            CollectionAssert.AreEqual(new object?[] { 1, 1, null, null }, rows[3]);
            CollectionAssert.AreEqual(new object?[] { 1, 2, 3L, 3L }, rows[4]);
            Assert.AreEqual(5, rows.Count, "no despawn row for a name that was set again");
        }

        [Test]
        public void EndFromSessionEnd_StaysPendingUntilTheServerTakesIt()
        {
            // Create succeeds, the end request fails once, the retry succeeds.
            int posts = 0;
            var handler = new MockHttpHandler
            {
                Responder = req =>
                {
                    if (!req.Url.EndsWith("/api/telemetry", StringComparison.Ordinal))
                    {
                        return new Http.HttpResponseData(401, "{}", new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    posts++;
                    return posts == 2
                        ? new Http.HttpResponseData(500, "{\"error\":\"down\"}", new Dictionary<string, string> { ["content-type"] = "application/json" })
                        : new Http.HttpResponseData(200, "{\"sessionId\":\"s1\"}", new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = NewClient(handler);
            client.Telemetry.StartSession();
            client.Telemetry.Track("boot");
            client.Telemetry.FlushAsync().GetAwaiter().GetResult();
            Assert.AreEqual("s1", client.Telemetry.CurrentSessionId);

            client.Trace.SetRoom("hall");
            client.Trace.Tick(0.0, Wall);
            Assert.ThrowsAsync<PlayloopException>(async () => await client.Telemetry.EndSessionAsync());

            // Nothing about the old session moved: the end chunk is back on
            // the buffer, the session id is kept, and the Trace is not re-armed.
            Assert.AreEqual("s1", client.Telemetry.CurrentSessionId);
            Assert.AreEqual(1, client.Telemetry.PendingCount);
            Assert.AreEqual(TraceStatus.Ended, client.Trace.Status);

            // The next flush, which the batch loop would run, carries the same
            // end again and completes it.
            client.Telemetry.FlushAsync().GetAwaiter().GetResult();
            var retry = handler.Calls.Where(c => c.Url.EndsWith("/api/telemetry", StringComparison.Ordinal)).Last();
            var body = JObject.Parse(Encoding.UTF8.GetString(retry.JsonBody!));
            Assert.AreEqual("s1", body["sessionId"]!.Value<string>());
            Assert.IsTrue(body["sessionEnded"]!.Value<bool>());
            var chunk = body["events"]!.Single(e => e["name"]!.Value<string>() == "trace_chunk")["data"]!;
            Assert.AreEqual(0, chunk["seq"]!.Value<int>());
            Assert.AreEqual("quit", chunk["end"]!["reason"]!.Value<string>());
            Assert.AreEqual(1, body["traceChunks"]!.Value<int>());

            Assert.IsNull(client.Telemetry.CurrentSessionId);
            Assert.AreEqual(0, client.Telemetry.PendingCount);
            Assert.AreEqual(TraceStatus.Active, client.Trace.Status, "re-armed only after the server took the end");

            // The next session starts clean: its first chunk is seq 0 with
            // nothing from the old session mixed in.
            client.Telemetry.StartSession();
            client.Trace.Tick(10.0, Wall + 10000);
            client.Trace.End(TraceEndReason.LevelComplete);
            var pending = client.Telemetry.SnapshotPending().Where(e => e.Name == "trace_chunk").ToList();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(0, pending[0].Data!["seq"]);
            Assert.AreEqual("level_complete", ((Dictionary<string, object>)pending[0].Data!["end"])["reason"]);
        }

        [Test]
        public void Entities_CapCountsDistinctNamesPerSession()
        {
            var warnings = new List<string>();
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c))
            {
                Warn = warnings.Add,
            };
            for (int i = 0; i < 8; i++) sampler.SetEntity($"e{i}", i, i);
            sampler.Tick(0.0, Wall);

            // e0 despawns and is sampled as gone, then comes back: the same
            // name takes no second slot.
            sampler.ClearEntity("e0");
            sampler.Tick(0.1, Wall + 100);
            sampler.SetEntity("e0", 5, 5);
            Assert.AreEqual(0, warnings.Count, "a returning name is not a ninth name");

            // A ninth distinct name is refused, once, with one warning.
            sampler.SetEntity("e8", 9, 9);
            sampler.SetEntity("e9", 9, 9);
            Assert.AreEqual(1, warnings.Count);
            sampler.Tick(0.2, Wall + 200);
            sampler.End(TraceEndReason.Quit);
            var ents = (string[])chunks[0]["ents"];
            CollectionAssert.DoesNotContain(ents, "e8");
            CollectionAssert.DoesNotContain(ents, "e9");
            CollectionAssert.Contains(ents, "e0");

            // A new session counts over from the names it carries: the seven
            // cleared before the reset belong to the old session and are
            // dropped, e0 is the one live entity, so seven new names fit and
            // the eighth new one is refused.
            for (int i = 1; i < 8; i++) sampler.ClearEntity($"e{i}");
            sampler.ResetForNewSession();
            sampler.Tick(20.0, Wall + 20000);
            warnings.Clear();
            for (int i = 0; i < 7; i++) sampler.SetEntity($"n{i}", i, i);
            Assert.AreEqual(0, warnings.Count);
            sampler.SetEntity("n7", 7, 7);
            Assert.AreEqual(1, warnings.Count, "e0 plus seven new names is the cap");
            sampler.Tick(20.1, Wall + 20100);
            sampler.End(TraceEndReason.Quit);
            var ents2 = (string[])chunks.Last()["ents"];
            CollectionAssert.Contains(ents2, "e0");
            CollectionAssert.Contains(ents2, "n6");
            CollectionAssert.DoesNotContain(ents2, "n7");
            CollectionAssert.DoesNotContain(ents2, "e1", "a despawn pending at the reset is not written into the new session");
        }

        [Test]
        public void Rooms_BoundsCacheIsCappedPerClient_RoomsStillSampled()
        {
            var warnings = new List<string>();
            var chunks = new List<Dictionary<string, object>>();
            var sampler = new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c))
            {
                Warn = warnings.Add,
            };
            for (int i = 0; i < TraceSampler.MaxRoomBounds; i++)
            {
                sampler.SetRoom($"r{i}", new TraceBounds(i, 0, i + 1, 1));
            }
            Assert.AreEqual(0, warnings.Count);

            // One past the cap: the room is sampled, its bounds are not kept.
            sampler.SetRoom("overflow", new TraceBounds(0, 0, 9, 9));
            Assert.AreEqual(1, warnings.Count);
            sampler.Tick(0.0, Wall);
            sampler.SetRoom("overflow_2", new TraceBounds(0, 0, 9, 9));
            Assert.AreEqual(1, warnings.Count, "one warning per client");
            sampler.Tick(0.1, Wall + 100);

            // A room declared before the cap still updates its bounds.
            sampler.SetRoom("r0", new TraceBounds(0, 0, 50, 50));
            sampler.Tick(0.2, Wall + 200);
            sampler.End(TraceEndReason.Quit);

            var rooms = ((object[])chunks[0]["rooms"]).Cast<Dictionary<string, object>>().ToList();
            CollectionAssert.AreEqual(new[] { "overflow", "overflow_2", "r0" }, rooms.Select(r => (string)r["id"]).ToArray());
            Assert.IsFalse(rooms[0].ContainsKey("b"), "no bounds past the cap");
            Assert.IsFalse(rooms[1].ContainsKey("b"));
            CollectionAssert.AreEqual(new object[] { 0L, 0L, 50L, 50L }, (object[])rooms[2]["b"]);
            var rows = ((object[])chunks[0]["s"]).Cast<object[]>().ToList();
            Assert.AreEqual(0, rows[0][4], "the overflow room is still the sampled room");
            Assert.AreEqual(1, rows[1][4]);
            Assert.AreEqual(2, rows[2][4]);

            // The cap outlives a session.
            sampler.ResetForNewSession();
            warnings.Clear();
            sampler.SetRoom("overflow_3", new TraceBounds(0, 0, 9, 9));
            Assert.AreEqual(0, warnings.Count, "the one warning per client was already given");
        }
    }
}
#endif
