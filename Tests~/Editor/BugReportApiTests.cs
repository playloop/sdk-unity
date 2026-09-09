#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Playloop;
using Playloop.BugReports;
using Playloop.Playtest;

namespace Playloop.Tests
{
    /// <summary>
    /// Player Bug Reports: SDK surface. Verifies the public
    /// client.BugReports.SubmitAsync / client.SubmitBugReportAsync surface
    /// against MockHttpHandler. Server-side semantics (storage, routing to
    /// the connected tracker) are covered in the main repo's test suite.
    /// </summary>
    [TestFixture]
    public class BugReportApiTests
    {
        private static PlayloopClient NewClient(MockHttpHandler handler)
        {
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 50,
                RetryAttempts = 1,
                DeviceId = "dev_abc",
            };
            return new PlayloopClient(options);
        }

        private const string OkBody =
            "{\"ok\":true,\"bugReportId\":\"bug_abc\",\"routingStatus\":\"routed\"}";

        [Test]
        public async Task SubmitAsync_PostsBugReportBody()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            var result = await client.BugReports.SubmitAsync(
                title: "Fell through the floor",
                description: "In room 3, walking into the north wall drops me out of the world.",
                severity: BugReportSeverities.High,
                sessionId: "sess_1",
                playerId: "player_9");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("bug_abc", result.BugReportId);
            Assert.AreEqual("routed", result.RoutingStatus);
            Assert.IsFalse(result.Idempotent);

            Assert.AreEqual(1, handler.Calls.Count);
            var call = handler.Calls[0];
            StringAssert.Contains("/api/telemetry/bug-report", call.Url);
            Assert.AreEqual("POST", call.Method);

            var body = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            Assert.AreEqual("Fell through the floor", (string?)body["title"]);
            StringAssert.Contains("north wall", (string?)body["description"]);
            Assert.AreEqual("high", (string?)body["severity"]);
            Assert.AreEqual("sess_1", (string?)body["sessionId"]);
            Assert.AreEqual("player_9", (string?)body["playerId"]);
            // An idempotency key is auto-generated per call so the retry
            // layer can dedupe a redelivered submit.
            Assert.IsFalse(string.IsNullOrEmpty((string?)body["idempotencyKey"]));
        }

        [Test]
        public async Task SubmitAsync_DefaultsSeverityToMedium()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            await client.BugReports.SubmitAsync("t", "d");

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("medium", (string?)body["severity"]);
        }

        [Test]
        public async Task SubmitAsync_OmitsOptionalIdentifiersWhenNotProvided()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            await client.BugReports.SubmitAsync("t", "d");

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.IsNull(body["sessionId"]);
            Assert.IsNull(body["playerId"]);
        }

        [Test]
        public async Task SubmitAsync_AutoFillsContextEnvironment()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            await client.BugReports.SubmitAsync("t", "d");

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            var ctx = body["context"] as JObject;
            Assert.IsNotNull(ctx, "context should be auto-filled");
            // Environment is always resolvable (dev / demo / production), so
            // the auto-filled context always carries it.
            Assert.IsFalse(string.IsNullOrEmpty((string?)ctx!["environment"]));
        }

        [Test]
        public async Task SubmitAsync_CallerContextOverridesAutoFill()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            await client.BugReports.SubmitAsync(
                "t", "d",
                context: new BugReportContext
                {
                    BuildVersion = "1.4.2",
                    Environment = "qa",
                    Platform = "WindowsPlayer",
                    Device = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["gpu"] = "RTX 4070",
                        ["ramMb"] = 32768,
                    },
                });

            var ctx = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!))["context"] as JObject;
            Assert.IsNotNull(ctx);
            Assert.AreEqual("1.4.2", (string?)ctx!["buildVersion"]);
            Assert.AreEqual("qa", (string?)ctx["environment"]);
            Assert.AreEqual("WindowsPlayer", (string?)ctx["platform"]);
            Assert.AreEqual("RTX 4070", (string?)ctx["device"]!["gpu"]);
            Assert.AreEqual(32768L, (long)ctx["device"]!["ramMb"]!);
        }

        [Test]
        public async Task SubmitAsync_UsesProvidedIdempotencyKey()
        {
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = NewClient(handler);

            await client.BugReports.SubmitAsync(
                "t", "d", idempotencyKey: "idem_fixed_123");

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("idem_fixed_123", (string?)body["idempotencyKey"]);
        }

        [Test]
        public async Task SubmitAsync_SurfacesIdempotentReplay()
        {
            // A repeated idempotency key returns 200 with idempotent:true and
            // the original report id.
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"bugReportId\":\"bug_abc\",\"routingStatus\":\"routed\"," +
                "\"idempotent\":true}",
                status: 200);
            using var client = NewClient(handler);

            var result = await client.BugReports.SubmitAsync("t", "d");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("bug_abc", result.BugReportId);
            Assert.IsTrue(result.Idempotent);
        }

        [Test]
        public void SubmitAsync_ThrowsWithoutTitle()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync("", "d"));
        }

        [Test]
        public void SubmitAsync_ThrowsOnTooLongTitle()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync(new string('x', 201), "d"));
        }

        [Test]
        public void SubmitAsync_ThrowsWithoutDescription()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync("t", ""));
        }

        [Test]
        public void SubmitAsync_ThrowsOnTooLongDescription()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync("t", new string('x', 8001)));
        }

        [Test]
        public void SubmitAsync_ThrowsOnUnknownSeverity()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync("t", "d", severity: "catastrophic"));
        }

        [Test]
        public void SubmitAsync_ThrowsOnMalformedResponse()
        {
            // ok=false / missing bugReportId surface as
            // PlayloopPlaytestException so callers can branch on Reason.
            var handler = MockHttpHandler.ReturnsJson("{\"ok\":false}");
            using var client = NewClient(handler);
            Assert.ThrowsAsync<PlayloopPlaytestException>(async () =>
                await client.BugReports.SubmitAsync("t", "d"));
        }

        [Test]
        public async Task SubmitBugReportAsync_TopLevelShortcut_HitsSameRoute()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"bugReportId\":\"bug_top\",\"routingStatus\":\"pending\"}",
                status: 201);
            using var client = NewClient(handler);

            var result = await client.SubmitBugReportAsync("t", "d");

            Assert.AreEqual("bug_top", result.BugReportId);
            StringAssert.Contains("/api/telemetry/bug-report", handler.Calls[0].Url);
        }

        [Test]
        public async Task SubmitAsync_DisabledClient_SoftFailsWithoutNetwork()
        {
            // Blank ingest key => disabled SDK. SubmitAsync resolves to a
            // soft failure (Ok == false) with no HTTP call, and never throws.
            var handler = MockHttpHandler.ReturnsJson(OkBody, status: 201);
            using var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                DeviceId = "dev_abc",
                RetryAttempts = 1,
            });

            var result = await client.BugReports.SubmitAsync("t", "d");

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, handler.Calls.Count);
        }

        [Test]
        public void SubmitAsync_DisabledClient_StillValidatesArguments()
        {
            // A caller bug (empty title) is a real bug regardless of whether
            // the SDK is configured, so validation fires before the soft-fail.
            using var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "",
                BaseUrl = "https://api.test.playloop.gg",
                Http = MockHttpHandler.ReturnsJson("{}"),
                DeviceId = "dev_abc",
                RetryAttempts = 1,
            });
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.BugReports.SubmitAsync("", "d"));
        }

        [Test]
        public void BugReportSeverities_MatchesUnionAcrossEngines()
        {
            // Keep in sync with the TypeScript / Python severity unions so a
            // report filed from any engine reads identically on the dashboard.
            var all = BugReportSeverities.All;
            Assert.AreEqual(4, all.Count);
            Assert.AreEqual("low", all[0]);
            Assert.AreEqual("medium", all[1]);
            Assert.AreEqual("high", all[2]);
            Assert.AreEqual("critical", all[3]);
            Assert.AreEqual("medium", BugReportSeverities.Default);
        }
    }
}
