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
    /// The <c>tr</c> stamp on tracked events: the run and room each event
    /// belongs to, read at Track time so the game's call order places an
    /// event on the right side of a room change even when no sample falls
    /// between them. Asserted on the wire, where the contract lives.
    /// </summary>
    [TestFixture]
    public class TraceEventStampTests
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

        /// <summary>Flush, then every event that reached /api/telemetry, in order.</summary>
        private static List<JObject> Wire(PlayloopClient client, MockHttpHandler handler)
        {
            client.Telemetry.FlushAsync().GetAwaiter().GetResult();
            return handler.Calls
                .Where(c => c.Url.EndsWith("/api/telemetry", StringComparison.Ordinal))
                .SelectMany(c => (JArray)JObject.Parse(Encoding.UTF8.GetString(c.JsonBody!))["events"]!)
                .Cast<JObject>()
                .ToList();
        }

        private static JObject Named(List<JObject> events, string name)
            => events.Single(e => e["name"]!.Value<string>() == name);

        private static MockHttpHandler Handler() => MockHttpHandler.ReturnsJson("{\"sessionId\":\"s1\"}");

        [Test]
        public void OrdinaryEvent_WhileTracing_CarriesRunAndRoom_AtTheTopLevel()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetRoom("hall");
            client.Trace.SetPosition(1f, 1f);
            client.Trace.Tick(0.0, Wall);
            client.Telemetry.Track("chest_opened", new Dictionary<string, object> { ["gold"] = 3 });

            var ev = Named(Wire(client, handler), "chest_opened");
            Assert.AreEqual(JToken.Parse("{\"s\":0,\"r\":\"hall\"}").ToString(), ev["tr"]!.ToString());
            Assert.IsNull(ev["data"]!["tr"], "tr is a sibling of data, never inside it");
            CollectionAssert.AreEqual(new[] { "id", "name", "data", "timestamp", "tr" },
                ev.Properties().Select(p => p.Name).ToArray(), "tr is appended after the existing keys");
        }

        [Test]
        public void RoomNeverDeclared_WritesNullRoom()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetPosition(1f, 1f);
            client.Telemetry.Track("jumped");

            var tr = Named(Wire(client, handler), "jumped")["tr"]!;
            Assert.AreEqual(0, tr["s"]!.Value<int>());
            Assert.AreEqual(JTokenType.Null, tr["r"]!.Type, "r is written as null, not omitted");
        }

        [Test]
        public void NoStamp_WhenTheTraceIsOff()
        {
            var handler = Handler();
            using var client = NewClient(handler, "production");
            client.Trace.SetRoom("hall");
            client.Trace.SetPosition(1f, 1f);
            client.Telemetry.Track("chest_opened");

            var events = Wire(client, handler);
            Assert.IsNull(Named(events, "chest_opened")["tr"]);
            Assert.IsNull(Named(events, "trace_state")["tr"], "the state note never carries tr");
        }

        [Test]
        public void NoStamp_WhileTheTraceIsUnwired()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Telemetry.Track("menu_opened");
            client.Trace.Tick(0.0, Wall);

            Assert.IsNull(Named(Wire(client, handler), "menu_opened")["tr"], "no state call yet, so no run to place it on");
        }

        [Test]
        public void NoStamp_WhenStoppedByTheBudget()
        {
            var handler = Handler();
            using var client = NewClient(handler, "playtest", t => t.MaxBytesPerSession = 2000);
            client.Trace.SetPosition(0f, 0f);
            for (int ms = 0; ms < 30000; ms += 100) client.Trace.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(TraceStatus.BudgetExhausted, client.Trace.Status);
            client.Telemetry.Track("late_event");

            Assert.IsNull(Named(Wire(client, handler), "late_event")["tr"]);
        }

        [Test]
        public void TraceChunks_NeverCarryTr()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetRoom("hall");
            client.Trace.SetPosition(1f, 1f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.End(TraceEndReason.Death);

            var chunk = Named(Wire(client, handler), "trace_chunk");
            Assert.IsNull(chunk["tr"]);
        }

        [Test]
        public void EventAfterSetRoom_CarriesTheNewRoom_BeforeTheNextSample()
        {
            // A boss dies and the next zone is built in one frame: both events
            // sit between the same two samples, and only the call order
            // separates them.
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetRoom("z1:arena");
            client.Trace.SetPosition(4f, 4f);
            client.Trace.Tick(0.0, Wall);
            client.Telemetry.Track("boss_killed");
            client.Trace.SetRoom("z2:entry");
            client.Telemetry.Track("zone_entered");
            client.Trace.Tick(0.1, Wall + 100);

            var events = Wire(client, handler);
            Assert.AreEqual("z1:arena", Named(events, "boss_killed")["tr"]!["r"]!.Value<string>());
            Assert.AreEqual("z2:entry", Named(events, "zone_entered")["tr"]!["r"]!.Value<string>());
            Assert.AreEqual(0, Named(events, "zone_entered")["tr"]!["s"]!.Value<int>());
        }

        [Test]
        public void EventBetweenEndAndTheNextRun_CarriesTheEndedRun_ThenSetPositionOpensTheNext()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetRoom("hall");
            client.Trace.SetPosition(1f, 1f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.End(TraceEndReason.Death);
            client.Telemetry.Track("death_screen");
            client.Trace.SetRoom("crypt");
            client.Telemetry.Track("room_picked");
            client.Trace.SetPosition(2f, 2f);
            client.Telemetry.Track("respawned");
            client.Trace.Tick(1.0, Wall + 1000);
            client.Trace.End(TraceEndReason.Quit);

            var events = Wire(client, handler);
            Assert.AreEqual(0, Named(events, "death_screen")["tr"]!["s"]!.Value<int>(), "the ended run, not the next one");
            Assert.AreEqual(0, Named(events, "room_picked")["tr"]!["s"]!.Value<int>());
            Assert.AreEqual("crypt", Named(events, "room_picked")["tr"]!["r"]!.Value<string>(), "the room is the one last declared");
            Assert.AreEqual(1, Named(events, "respawned")["tr"]!["s"]!.Value<int>(), "SetPosition opened run 1");

            var segs = events.Where(e => e["name"]!.Value<string>() == "trace_chunk").Select(e => e["data"]!["seg"]!.Value<int>()).ToArray();
            CollectionAssert.AreEqual(new[] { 0, 1 }, segs, "the stamps match the seg the chunks carry");
        }

        [Test]
        public void EventAfterBegin_CarriesTheNewRun()
        {
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetPosition(1f, 1f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.End(TraceEndReason.LevelComplete);
            client.Trace.Begin();
            client.Telemetry.Track("level_started");

            Assert.AreEqual(1, Named(Wire(client, handler), "level_started")["tr"]!["s"]!.Value<int>());
        }

        [Test]
        public void ARunThatTookNoSample_SharesItsIndexWithTheNextRun()
        {
            // A run with no sample writes no chunk and takes no seg, so the
            // next run carries the same index. Its events are stamped with
            // that index: there is no path of its own to place them on.
            var handler = Handler();
            using var client = NewClient(handler);
            client.Trace.SetPosition(1f, 1f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.End(TraceEndReason.Death);
            client.Trace.Begin();
            client.Telemetry.Track("in_empty_run");
            client.Trace.End(TraceEndReason.Death);
            client.Telemetry.Track("after_empty_run");
            client.Trace.SetPosition(2f, 2f);
            client.Trace.Tick(1.0, Wall + 1000);
            client.Telemetry.Track("in_next_run");
            client.Trace.End(TraceEndReason.Quit);

            var events = Wire(client, handler);
            Assert.AreEqual(1, Named(events, "in_empty_run")["tr"]!["s"]!.Value<int>());
            Assert.AreEqual(1, Named(events, "after_empty_run")["tr"]!["s"]!.Value<int>());
            Assert.AreEqual(1, Named(events, "in_next_run")["tr"]!["s"]!.Value<int>());
            var segs = events.Where(e => e["name"]!.Value<string>() == "trace_chunk").Select(e => e["data"]!["seg"]!.Value<int>()).ToArray();
            CollectionAssert.AreEqual(new[] { 0, 1 }, segs);
        }
    }
}
#endif
