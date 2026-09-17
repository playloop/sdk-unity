#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playloop;
using Playloop.Http;

namespace Playloop.Tests
{
    /// <summary>
    /// EventConfigStore + telemetry integration for the SDK
    /// session-summary feature.
    /// Mirrors the TypeScript + Python event-config tests.
    /// </summary>
    [TestFixture]
    public class EventConfigStoreTests
    {
        // The store now resolves its slug from the ingest key via
        // GET /api/telemetry/resolve, so tests inject the slug through that
        // probe's response instead of a config field. A null slug means the
        // resolve "fails" (401), which keeps the store inert.
        private static PlayloopClient NewClientWithSlug(MockHttpHandler handler, string? gameSlug)
        {
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 50,
                RetryAttempts = 1,
                // Disable the periodic heartbeat in tests so the timer
                // doesn't fire spurious events while assertions are
                // running. Tests that need heartbeat coverage call
                // HeartbeatEmitter.EmitOnce() directly.
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                // These tests assert on the telemetry bodies, so the
                // suppress-in-editor gate must not drain them when the suite
                // runs inside the Unity editor.
                SendInEditor = true,
            };
            var client = new PlayloopClient(options);
            // Drop the constructor-fired session_start anchor so this
            // suite's existing assertions on PendingCount don't have to
            // account for it. Tests that need session_start visibility
            // construct PlayloopClient directly (see PlayloopClientTests).
            client.Telemetry.ClearBuffer();
            return client;
        }

        /// <summary>
        /// A responder that answers the resolve probe with <paramref name="slug"/>
        /// (or 401 when null, which keeps the store inert), every event-config
        /// GET with <paramref name="rows"/>, and telemetry POSTs with an ok body.
        /// </summary>
        private static MockHttpHandler EventConfigResponder(JArray rows, string? slug = "g")
        {
            return new MockHttpHandler
            {
                Responder = req =>
                {
                    if (req.Url.Contains("/api/telemetry/resolve"))
                    {
                        if (slug == null)
                            return new HttpResponseData(401, "{\"error\":\"invalid key\"}",
                                new Dictionary<string, string>());
                        var rbody = new JObject
                        {
                            ["gameId"] = "game_abc",
                            ["slug"] = slug,
                            ["name"] = "Test Game",
                        }.ToString();
                        return new HttpResponseData(200, rbody,
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    if (req.Url.Contains("/event-config"))
                    {
                        var body = new JObject { ["events"] = rows }.ToString();
                        return new HttpResponseData(
                            200,
                            body,
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    // Telemetry POST.
                    return new HttpResponseData(
                        200,
                        new JObject { ["ok"] = true, ["sessionId"] = "sess_test" }.ToString(),
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
        }

        [Test]
        public async Task NoFetch_WhenKeyUnresolvable()
        {
            var handler = EventConfigResponder(new JArray(), slug: null);
            using var client = NewClientWithSlug(handler, gameSlug: null);
            // Give any fire-and-forget task a chance.
            await Task.Delay(50);
            Assert.IsFalse(handler.Calls.Any(c => c.Url.Contains("/event-config")));
        }

        [Test]
        public async Task PrefetchOnConstruction_WhenKeyResolves()
        {
            var handler = EventConfigResponder(new JArray(), slug: "necromancers-army");
            using var client = NewClientWithSlug(handler, gameSlug: "necromancers-army");

            // Wait for fire-and-forget refresh.
            for (int i = 0; i < 50; i++)
            {
                if (client.EventConfig.Loaded) break;
                await Task.Delay(20);
            }
            Assert.IsTrue(client.EventConfig.Loaded, "prefetch never settled");

            var configCalls = handler.Calls.Where(c => c.Url.Contains("/event-config")).ToList();
            Assert.AreEqual(1, configCalls.Count);
            StringAssert.EndsWith("/api/v1/games/necromancers-army/event-config", configCalls[0].Url);
            Assert.AreEqual("GET", configCalls[0].Method);
            Assert.IsTrue(configCalls[0].Headers.TryGetValue("Authorization", out var auth));
            Assert.AreEqual("Bearer pl_ik_test", auth);
        }

        [Test]
        public async Task UrlEncodesGameSlug()
        {
            var handler = EventConfigResponder(new JArray(), slug: "game with spaces");
            using var client = NewClientWithSlug(handler, gameSlug: "game with spaces");
            await client.RefreshEventConfigAsync();
            Assert.IsTrue(handler.Calls.Any(c => c.Url.Contains("game%20with%20spaces")));
        }

        [Test]
        public async Task SdkIgnore_DropsEventBeforeBuffer()
        {
            var handler = EventConfigResponder(new JArray
            {
                new JObject { ["eventName"] = "spammy_event", ["sdkIgnore"] = true },
            });
            using var client = NewClientWithSlug(handler, gameSlug: "g");
            await client.RefreshEventConfigAsync();

            client.Telemetry.Track("spammy_event", new Dictionary<string, object> { { "noisy", "data" } });
            client.Telemetry.Track("important_event", new Dictionary<string, object> { { "x", 1 } });

            Assert.AreEqual(1, client.Telemetry.PendingCount);
            await client.Telemetry.FlushAsync();

            var telemetryCalls = handler.Calls.Where(c => c.Url.EndsWith("/api/telemetry")).ToList();
            Assert.AreEqual(1, telemetryCalls.Count);
            var body = JObject.Parse(Encoding.UTF8.GetString(telemetryCalls[0].JsonBody!));
            var names = body["events"]!.Select(e => e["name"]!.ToString()).ToList();
            CollectionAssert.AreEqual(new[] { "important_event" }, names);
        }

        [Test]
        public async Task LinkToSummary_MergesAndEventStillShips()
        {
            var handler = EventConfigResponder(new JArray
            {
                new JObject { ["eventName"] = "wishlist_clicked", ["linkToSummary"] = true },
            });
            using var client = NewClientWithSlug(handler, gameSlug: "g");
            await client.RefreshEventConfigAsync();

            client.Telemetry.Track(
                "wishlist_clicked",
                new Dictionary<string, object> { { "source", "main_menu" }, { "count", 1 } });

            // Event still ships on the wire.
            Assert.AreEqual(1, client.Telemetry.PendingCount);

            // And state accumulator now carries the props. The
            // server-side `linkToSummary` flag (named for v0.2) maps
            // into the new state accumulator under the new contract.
            client.State.SetState(new Dictionary<string, object?> { { "final_souls", 268000 } });
            client.EndSession();
            await client.Telemetry.FlushAsync();

            var telemetryCalls = handler.Calls.Where(c => c.Url.EndsWith("/api/telemetry")).ToList();
            var body = JObject.Parse(Encoding.UTF8.GetString(telemetryCalls[0].JsonBody!));
            var sessionEnd = body["events"]!.First(e => e["name"]!.ToString() == "session_summary");
            Assert.AreEqual("main_menu", sessionEnd["data"]!["source"]!.ToString());
            Assert.AreEqual(1, (int)sessionEnd["data"]!["count"]!);
            Assert.AreEqual(268000, (int)sessionEnd["data"]!["final_souls"]!);
        }

        [Test]
        public async Task UnconfiguredEvents_FlowThroughUnchanged()
        {
            var handler = EventConfigResponder(new JArray
            {
                new JObject { ["eventName"] = "specifically_configured", ["sdkIgnore"] = true },
            });
            using var client = NewClientWithSlug(handler, gameSlug: "g");
            await client.RefreshEventConfigAsync();

            client.Telemetry.Track("unrelated_event", new Dictionary<string, object> { { "x", 1 } });

            Assert.AreEqual(1, client.Telemetry.PendingCount);
            await client.Telemetry.FlushAsync();

            var telemetryCalls = handler.Calls.Where(c => c.Url.EndsWith("/api/telemetry")).ToList();
            var body = JObject.Parse(Encoding.UTF8.GetString(telemetryCalls[0].JsonBody!));
            var events = body["events"]!.ToList();
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("unrelated_event", events[0]["name"]!.ToString());
        }

        [Test]
        public async Task Refresh_PicksUpServerSideEdits()
        {
            var phase = "initial";
            var handler = new MockHttpHandler
            {
                Responder = req =>
                {
                    if (req.Url.Contains("/api/telemetry/resolve"))
                    {
                        var rbody = new JObject
                        {
                            ["gameId"] = "game_abc",
                            ["slug"] = "g",
                            ["name"] = "Test Game",
                        }.ToString();
                        return new HttpResponseData(200, rbody,
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    if (req.Url.Contains("/event-config"))
                    {
                        JArray rows;
                        if (phase == "initial")
                        {
                            rows = new JArray();
                        }
                        else
                        {
                            rows = new JArray
                            {
                                new JObject { ["eventName"] = "now_ignored", ["sdkIgnore"] = true },
                            };
                        }
                        var body = new JObject { ["events"] = rows }.ToString();
                        return new HttpResponseData(
                            200,
                            body,
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    return new HttpResponseData(
                        200,
                        new JObject { ["ok"] = true, ["sessionId"] = "sess_test" }.ToString(),
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = NewClientWithSlug(handler, gameSlug: "g");
            await client.RefreshEventConfigAsync();

            client.Telemetry.Track("now_ignored", new Dictionary<string, object> { { "x", 1 } });
            Assert.AreEqual(1, client.Telemetry.PendingCount);

            phase = "after_save";
            await client.RefreshEventConfigAsync();

            client.Telemetry.Track("now_ignored", new Dictionary<string, object> { { "x", 2 } });
            // Still 1 from the first fire; second was dropped.
            Assert.AreEqual(1, client.Telemetry.PendingCount);
        }

        [Test]
        public async Task SparseRows_TreatedAsAllFalse()
        {
            var handler = EventConfigResponder(new JArray
            {
                new JObject { ["eventName"] = "partial_row" },
            });
            using var client = NewClientWithSlug(handler, gameSlug: "g");
            await client.RefreshEventConfigAsync();

            var entry = client.EventConfig.Lookup("partial_row");
            Assert.IsNotNull(entry);
            Assert.IsFalse(entry!.SdkIgnore);
            Assert.IsFalse(entry.LinkToSummary);
            Assert.IsFalse(entry.HideFromAi);
        }

        // The v0.2 adoption-telemetry events (`session.summary_flushed`
        // with source = "auto"/"manual") were dropped from the new
        // SDK contract. `session_summary` is unambiguous so no precedence
        // marker is needed. Coverage for the new StateAPI lives in
        // StateApiTests.
    }
}
