#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playloop.Tests
{
    [TestFixture]
    public class TelemetryApiTests
    {
        [Test]
        public void Track_BuffersSynchronously()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            client.Telemetry.Track("death", new Dictionary<string, object> { { "room", "tutorial_02" } });
            client.Telemetry.Track("quit");
            Assert.AreEqual(2, client.Telemetry.PendingCount);
            Assert.AreEqual(0, handler.Calls.Count);
        }

        [Test]
        public void Track_RequiresEventName()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.Throws<System.ArgumentException>(() => client.Telemetry.Track(""));
        }

        [Test]
        public async Task FlushAsync_NoopOnEmpty()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Telemetry.FlushAsync();
            Assert.AreEqual(0, handler.Calls.Count);
        }

        [Test]
        public async Task FlushAsync_PostsBufferedEventsAndClears()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a", new Dictionary<string, object> { { "x", 1 } });
            client.Telemetry.Track("b");

            await client.Telemetry.FlushAsync();

            Assert.AreEqual(0, client.Telemetry.PendingCount);
            Assert.AreEqual(1, handler.Calls.Count);

            var call = handler.Calls[0];
            Assert.AreEqual("POST", call.Method);
            StringAssert.EndsWith("/api/telemetry", call.Url);

            var jsonText = Encoding.UTF8.GetString(call.JsonBody!);
            var json = JObject.Parse(jsonText);
            var events = (JArray)json["events"]!;
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual("a", events[0]["name"]!.Value<string>());
            Assert.AreEqual(1, events[0]["data"]!["x"]!.Value<int>());
            Assert.AreEqual("b", events[1]["name"]!.Value<string>());
            Assert.IsNull(events[1]["data"]); // omitted when null

            // Every event carries a short random hex id (1-24 chars, server contract).
            foreach (var ev in events)
            {
                var id = ev["id"]?.Value<string>();
                Assert.IsFalse(string.IsNullOrEmpty(id), "every event must carry an id");
                Assert.GreaterOrEqual(id!.Length, 1);
                Assert.LessOrEqual(id.Length, 24);
            }
        }

        [Test]
        public void Track_GeneratesDistinctIdsPerCall()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a");
            client.Telemetry.Track("a"); // same name on purpose. Ids must still differ

            // Grab the buffered events via a flush so we can inspect serialized output.
            // (The buffer itself isn't exposed; the flushed body is the contract.)
            var flushTask = client.Telemetry.FlushAsync();
            flushTask.GetAwaiter().GetResult();

            var jsonText = Encoding.UTF8.GetString(handler.Calls[0].JsonBody!);
            var events = (JArray)JObject.Parse(jsonText)["events"]!;
            Assert.AreEqual(2, events.Count);
            var id0 = events[0]["id"]!.Value<string>();
            var id1 = events[1]["id"]!.Value<string>();
            Assert.IsFalse(string.IsNullOrEmpty(id0));
            Assert.IsFalse(string.IsNullOrEmpty(id1));
            Assert.AreNotEqual(id0, id1, "consecutive Track calls must produce distinct ids");
        }

        [Test]
        public async Task FlushAsync_BodyContainsEventIdField()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("boot");
            await client.Telemetry.FlushAsync();

            var jsonText = Encoding.UTF8.GetString(handler.Calls[0].JsonBody!);
            var events = (JArray)JObject.Parse(jsonText)["events"]!;
            Assert.AreEqual(1, events.Count);
            var id = events[0]["id"]?.Value<string>();
            Assert.IsFalse(string.IsNullOrEmpty(id), "flushed event body must contain an id");
            Assert.GreaterOrEqual(id!.Length, 1);
            Assert.LessOrEqual(id.Length, 24);
        }

        [Test]
        public void FlushAsync_RequeuesOnFailure()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"error\":\"fail\"}", status: 500);
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a");
            client.Telemetry.Track("b");

            Assert.ThrowsAsync<PlayloopException>(async () => await client.Telemetry.FlushAsync());
            Assert.AreEqual(2, client.Telemetry.PendingCount);
        }

        [Test]
        public async Task AutoBatch_FlushesOnInterval()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            // The fixture's NewClient sets TelemetryFlushIntervalMs = 50.
            using var client = PlayloopClientTests.NewClient(handler);
            client.Telemetry.Track("a");
            client.Telemetry.AutoBatch();

            // Wait up to 2 seconds for the daemon task to fire.
            for (int i = 0; i < 200; i++)
            {
                if (client.Telemetry.PendingCount == 0) break;
                await Task.Delay(20);
            }

            Assert.AreEqual(0, client.Telemetry.PendingCount);
            Assert.GreaterOrEqual(handler.Calls.Count, 1);

            await client.Telemetry.StopAutoBatchAsync();
        }

        [Test]
        public void AutoBatch_IsIdempotent()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            client.Telemetry.AutoBatch();
            Assert.DoesNotThrow(() => client.Telemetry.AutoBatch());
        }

        [Test]
        public async Task StopAutoBatch_BeforeStart_IsNoop()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Telemetry.StopAutoBatchAsync(); // must not throw
        }

        [Test]
        public async Task AutoBatch_SwallowsErrors_KeepsFiring()
        {
            // First call fails, second succeeds. The loop should recover.
            int call = 0;
            var handler = new MockHttpHandler
            {
                Responder = _ =>
                {
                    call++;
                    return call == 1
                        ? new Http.HttpResponseData(500, "{\"error\":\"flaky\"}",
                            new Dictionary<string, string> { ["content-type"] = "application/json" })
                        : new Http.HttpResponseData(204, "", new Dictionary<string, string>());
                },
            };
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a");
            client.Telemetry.AutoBatch();

            for (int i = 0; i < 200; i++)
            {
                if (client.Telemetry.PendingCount == 0) break;
                await Task.Delay(20);
            }

            Assert.AreEqual(0, client.Telemetry.PendingCount);
            await client.Telemetry.StopAutoBatchAsync();
        }

        // ──────────────────── per-launch session caching ────────────────────

        /// <summary>
        /// First flush has no cached sessionId in the body; after the response
        /// arrives the SDK caches <c>response.sessionId</c>. Second flush replays
        /// that id so events append to the same session.
        /// </summary>
        [Test]
        public async Task FlushAsync_CachesServerSessionIdAndReusesIt()
        {
            int callCount = 0;
            var handler = new MockHttpHandler
            {
                Responder = _ =>
                {
                    callCount++;
                    var appended = callCount > 1;
                    var body = $"{{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":{appended.ToString().ToLowerInvariant()},\"eventsIngested\":1,\"analyzeKicked\":false}}";
                    return new Http.HttpResponseData(
                        200,
                        body,
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = PlayloopClientTests.NewClient(handler);

            Assert.IsNull(client.Telemetry.CurrentSessionId);

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            // First call: no sessionId in body, server returns one, SDK caches it.
            Assert.AreEqual(1, handler.Calls.Count);
            var firstBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.IsNull(firstBody["sessionId"], "first flush must not carry a sessionId");
            Assert.AreEqual("sess_test", client.Telemetry.CurrentSessionId);

            client.Telemetry.Track("b");
            await client.Telemetry.FlushAsync();

            // Second call: cached sessionId is replayed.
            Assert.AreEqual(2, handler.Calls.Count);
            var secondBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[1].JsonBody!));
            Assert.AreEqual("sess_test", secondBody["sessionId"]!.Value<string>());
            Assert.AreEqual("sess_test", client.Telemetry.CurrentSessionId);
        }

        /// <summary>
        /// <c>StartSession</c> stages metadata + deviceId so they ride along on the
        /// first POST (create) and never appear on subsequent appends.
        /// </summary>
        [Test]
        public async Task StartSession_StagesMetadataAndDeviceIdForFirstFlush()
        {
            int callCount = 0;
            var handler = new MockHttpHandler
            {
                Responder = _ =>
                {
                    callCount++;
                    var appended = callCount > 1;
                    var body = $"{{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":{appended.ToString().ToLowerInvariant()},\"eventsIngested\":1,\"analyzeKicked\":false}}";
                    return new Http.HttpResponseData(
                        200,
                        body,
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.StartSession(
                metadata: new Dictionary<string, object> { { "level", "tutorial" }, { "build", 42 } },
                deviceId: "device-abc");

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            var firstBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("device-abc", firstBody["deviceId"]!.Value<string>());
            Assert.AreEqual("tutorial", firstBody["sessionMetadata"]!["level"]!.Value<string>());
            Assert.AreEqual(42, firstBody["sessionMetadata"]!["build"]!.Value<int>());
            Assert.IsNull(firstBody["sessionEnded"], "sessionEnded should be omitted unless explicitly ending");

            // Second flush: metadata and deviceId are gone, sessionId is present.
            client.Telemetry.Track("b");
            await client.Telemetry.FlushAsync();
            var secondBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[1].JsonBody!));
            Assert.IsNull(secondBody["deviceId"]);
            Assert.IsNull(secondBody["sessionMetadata"]);
            Assert.AreEqual("sess_test", secondBody["sessionId"]!.Value<string>());
        }

        /// <summary>
        /// Calling <c>StartSession</c> a second time while a session is already
        /// active is an idempotent no-op. Staged metadata and deviceId are NOT
        /// replaced. Matches the canonical TS SDK semantics (see
        /// <c>TelemetryApi.StartSession</c> doc comment). If the consumer wants
        /// a fresh session, they must call <c>EndSessionAsync</c> first.
        /// </summary>
        [Test]
        public async Task StartSession_IsIdempotentNoOpWhenAlreadyActive()
        {
            var handler = new MockHttpHandler
            {
                Responder = _ => new Http.HttpResponseData(
                    200,
                    "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}",
                    new Dictionary<string, string> { ["content-type"] = "application/json" }),
            };
            using var client = PlayloopClientTests.NewClient(handler);

            // First call stages metadata + deviceId for the next flush.
            client.Telemetry.StartSession(
                metadata: new Dictionary<string, object> { { "level", "tutorial" } },
                deviceId: "device-original");

            // Second call (before any flush): should be ignored, original
            // staged values preserved. No exception.
            Assert.DoesNotThrow(() => client.Telemetry.StartSession(
                metadata: new Dictionary<string, object> { { "level", "boss-fight" } },
                deviceId: "device-replacement"));

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("device-original", body["deviceId"]!.Value<string>(),
                "deviceId should NOT have been replaced by the second StartSession call");
            Assert.AreEqual("tutorial", body["sessionMetadata"]!["level"]!.Value<string>(),
                "metadata should NOT have been replaced by the second StartSession call");

            // Third call (after the first flush, _currentSessionId is now set):
            // also a no-op.
            Assert.DoesNotThrow(() => client.Telemetry.StartSession(
                metadata: new Dictionary<string, object> { { "level", "credits" } }));
        }

        /// <summary>
        /// <c>EndSessionAsync</c> sends <c>sessionEnded: true</c> on the next
        /// flush and clears the cached sessionId so the following flush starts a
        /// fresh session.
        /// </summary>
        [Test]
        public async Task EndSessionAsync_FlushesWithSessionEndedAndClearsCache()
        {
            int callCount = 0;
            var handler = new MockHttpHandler
            {
                Responder = _ =>
                {
                    callCount++;
                    // Three different session ids over the test to confirm we
                    // start a new session after End.
                    var sessionId = callCount <= 2 ? "sess_first" : "sess_second";
                    var appended = callCount == 2; // 1=create, 2=append+end, 3=new create
                    var body = $"{{\"ok\":true,\"sessionId\":\"{sessionId}\",\"appended\":{appended.ToString().ToLowerInvariant()},\"eventsIngested\":1,\"analyzeKicked\":false}}";
                    return new Http.HttpResponseData(
                        200,
                        body,
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = PlayloopClientTests.NewClient(handler);

            // Establish a session.
            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();
            Assert.AreEqual("sess_first", client.Telemetry.CurrentSessionId);

            // End it (with one more event in the buffer for good measure).
            client.Telemetry.Track("b");
            await client.Telemetry.EndSessionAsync();

            Assert.AreEqual(2, handler.Calls.Count);
            var endBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[1].JsonBody!));
            Assert.AreEqual("sess_first", endBody["sessionId"]!.Value<string>());
            Assert.IsTrue(endBody["sessionEnded"]!.Value<bool>());
            Assert.IsNull(client.Telemetry.CurrentSessionId);

            // The next Track + Flush starts a new session (no sessionId in body).
            client.Telemetry.Track("c");
            await client.Telemetry.FlushAsync();
            var newBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[2].JsonBody!));
            Assert.IsNull(newBody["sessionId"], "post-End flush must not carry the old session id");
            Assert.AreEqual("sess_second", client.Telemetry.CurrentSessionId);
        }

        /// <summary>
        /// Calling <c>EndSessionAsync</c> twice in a row is a no-op for the second
        /// call. There's no active session, so no extra POST.
        /// </summary>
        [Test]
        public async Task EndSessionAsync_IsIdempotent()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}");
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();
            Assert.AreEqual("sess_test", client.Telemetry.CurrentSessionId);

            await client.Telemetry.EndSessionAsync();
            var callsAfterFirstEnd = handler.Calls.Count;
            Assert.IsNull(client.Telemetry.CurrentSessionId);

            // Second End: no active session → no-op, no extra HTTP call.
            await client.Telemetry.EndSessionAsync();
            Assert.AreEqual(callsAfterFirstEnd, handler.Calls.Count);
            Assert.IsNull(client.Telemetry.CurrentSessionId);
        }

        /// <summary>
        /// <c>PlayloopOptions.DeviceId</c> flows through the client constructor
        /// to the TelemetryApi and lands on the first POST body.
        /// </summary>
        [Test]
        public async Task PlayloopOptions_DeviceId_FlowsToFirstFlushBody()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                DeviceId = "device-from-options",
                TelemetryFlushIntervalMs = 5000,
                SendInEditor = true, // the assertion is on the wire body
            };
            using var client = new PlayloopClient(options);

            client.Telemetry.Track("boot");
            await client.Telemetry.FlushAsync();

            var firstBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("device-from-options", firstBody["deviceId"]!.Value<string>());
        }

        // ──────────────────── suppress-in-editor gate ────────────────────

        /// <summary>
        /// With <c>SendInEditor</c> left at its default (off), a development
        /// build (the editor, a debug player) drains the buffer on flush and
        /// sends nothing, so local playtesting never pollutes real session
        /// data. A release build, and the dotnet host, send as usual. The
        /// expectation follows the host the suite is running in.
        /// </summary>
        [Test]
        public async Task FlushAsync_DefaultSendInEditor_FollowsTheBuild()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 5000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                // SendInEditor left at its default (false).
            };
            using var client = new PlayloopClient(options);
            client.Telemetry.ClearBuffer();

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            Assert.AreEqual(0, client.Telemetry.PendingCount, "the buffer drains whether or not the flush is sent");
            Assert.AreEqual(TestHost.IsDevelopmentBuild ? 0 : 1, handler.Calls.Count,
                "a development build sends nothing by default; a release build or the dotnet host sends");
        }

        /// <summary>
        /// <c>SendInEditor = true</c> lifts the gate everywhere: the flush
        /// reaches the wire in the editor exactly as it does in a release
        /// build. This is the switch every wire-asserting test in the suite
        /// relies on.
        /// </summary>
        [Test]
        public async Task FlushAsync_SendInEditor_SendsInEveryHost()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 5000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                SendInEditor = true,
            };
            using var client = new PlayloopClient(options);
            client.Telemetry.ClearBuffer();

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            Assert.AreEqual(0, client.Telemetry.PendingCount);
            Assert.AreEqual(1, handler.Calls.Count);
        }

        /// <summary>
        /// With the default <c>SendInEditor</c>, a development host drops the
        /// end flush like every other flush, and that still completes the
        /// session end: the cached id clears and the next flush is a fresh
        /// session. A release host sends it. Either way nothing is left
        /// pending.
        /// </summary>
        [Test]
        public async Task EndSessionAsync_DefaultSendInEditor_CompletesTheEndInEveryHost()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 5000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                // SendInEditor left at its default (false).
            };
            using var client = new PlayloopClient(options);
            client.Telemetry.ClearBuffer();

            client.Telemetry.StartSession(new Dictionary<string, object> { { "level", "one" } });
            client.Telemetry.Track("a");
            await client.Telemetry.EndSessionAsync();

            Assert.AreEqual(TestHost.IsDevelopmentBuild ? 0 : 1, handler.Calls.Count);
            Assert.AreEqual(0, client.Telemetry.PendingCount);
            Assert.IsNull(client.Telemetry.CurrentSessionId);

            // A second StartSession is accepted: the first session is over.
            client.Telemetry.StartSession(new Dictionary<string, object> { { "level", "two" } });
            client.Telemetry.Track("b");
            await client.Telemetry.FlushAsync();
            if (!TestHost.IsDevelopmentBuild)
            {
                var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[1].JsonBody!));
                Assert.IsNull(body["sessionId"], "a fresh session after the end");
                Assert.AreEqual("two", body["sessionMetadata"]!["level"]!.Value<string>());
            }
        }

        /// <summary>
        /// A failed end flush leaves the end requested and the events on the
        /// buffer, so the next flush carries the same end and completes it.
        /// Only then does the session state reset.
        /// </summary>
        [Test]
        public async Task EndSessionAsync_KeepsTheEndPendingWhenTheFlushFails()
        {
            // Counts telemetry POSTs only: the client's resolve probe also
            // goes through this responder.
            int posts = 0;
            var handler = new MockHttpHandler
            {
                Responder = req =>
                {
                    if (req.Url.EndsWith("/api/telemetry")) posts++;
                    return posts == 2 && req.Url.EndsWith("/api/telemetry")
                        ? new Http.HttpResponseData(500, "{\"error\":\"down\"}", new Dictionary<string, string> { ["content-type"] = "application/json" })
                        : new Http.HttpResponseData(200,
                            "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":true,\"eventsIngested\":1,\"analyzeKicked\":false}",
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();
            Assert.AreEqual("sess_test", client.Telemetry.CurrentSessionId);

            client.Telemetry.Track("b");
            Assert.ThrowsAsync<PlayloopException>(async () => await client.Telemetry.EndSessionAsync());

            Assert.AreEqual("sess_test", client.Telemetry.CurrentSessionId, "the session is not over until the server says so");
            Assert.AreEqual(1, client.Telemetry.PendingCount, "the end flush's events are back on the buffer");

            await client.Telemetry.FlushAsync();
            Assert.AreEqual(3, handler.Calls.Count);
            var retry = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[2].JsonBody!));
            Assert.AreEqual("sess_test", retry["sessionId"]!.Value<string>());
            Assert.IsTrue(retry["sessionEnded"]!.Value<bool>(), "the retry still carries the end");
            Assert.AreEqual("b", retry["events"]![0]!["name"]!.Value<string>());

            Assert.IsNull(client.Telemetry.CurrentSessionId);
            Assert.AreEqual(0, client.Telemetry.PendingCount);

            client.Telemetry.Track("c");
            await client.Telemetry.FlushAsync();
            var next = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[3].JsonBody!));
            Assert.IsNull(next["sessionId"], "the flush after a completed end starts a new session");
            Assert.IsNull(next["sessionEnded"]);
        }

        [Test]
        public void BufferEvictsOldestAtCap()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryMaxBufferSize = 3,
                TelemetryFlushIntervalMs = 5000,
            };
            using var client = new PlayloopClient(options);
            client.Telemetry.Track("a");
            client.Telemetry.Track("b");
            client.Telemetry.Track("c");
            client.Telemetry.Track("d");
            Assert.AreEqual(3, client.Telemetry.PendingCount);
        }
    }
}
