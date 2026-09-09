#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Playloop;
using Playloop.Http;
using Playloop.Playtest;

namespace Playloop.Tests
{
    /// <summary>
    /// Tester Keys correlation SDK methods (Unity).
    ///
    /// Verifies the public client.LinkTesterAsync / GetCurrentTesterAsync /
    /// UnlinkTesterAsync surface against MockHttpHandler. Server-side
    /// semantics are covered in the main repo's test suite.
    ///
    /// gameId is resolved from the ingest key via GET /api/telemetry/resolve
    /// (no per-call / config override anymore), so the mock answers that probe
    /// with the game identity and the feature endpoints use the resolved id.
    /// </summary>
    [TestFixture]
    public class PlaytestApiTests
    {
        /// <summary>
        /// A responder that answers GET /api/telemetry/resolve with the given
        /// gameId (or a 401 when <paramref name="resolveGameId"/> is null) and
        /// every other request with <paramref name="featureBody"/> +
        /// <paramref name="featureStatus"/>.
        /// </summary>
        private static MockHttpHandler RoutedHandler(
            string featureBody,
            int featureStatus = 200,
            string? resolveGameId = "game_abc")
        {
            return new MockHttpHandler
            {
                Responder = req =>
                {
                    if (req.Url.Contains("/api/telemetry/resolve"))
                    {
                        if (resolveGameId == null)
                            return new HttpResponseData(401, "{\"error\":\"invalid key\"}",
                                new Dictionary<string, string>());
                        var body = "{\"gameId\":\"" + resolveGameId +
                                   "\",\"slug\":\"necromancers-army\",\"name\":\"Necromancer's Army\"}";
                        return new HttpResponseData(200, body,
                            new Dictionary<string, string> { ["content-type"] = "application/json" });
                    }
                    return new HttpResponseData(featureStatus, featureBody,
                        new Dictionary<string, string> { ["content-type"] = "application/json" });
                },
            };
        }

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

        private static JObject? FindClaimBody(MockHttpHandler handler, string method)
        {
            foreach (var call in handler.Calls)
            {
                if (call.Method == method && call.Url.Contains("/api/telemetry/claim") && call.JsonBody != null)
                    return JObject.Parse(Encoding.UTF8.GetString(call.JsonBody));
            }
            return null;
        }

        [Test]
        public async Task LinkTesterAsync_PostsClaimPayload()
        {
            var handler = RoutedHandler(
                "{\"ok\":true,\"alreadyLinked\":false,\"handle\":\"RedFox42\",\"claimedAt\":123}");
            using var client = NewClient(handler);

            var result = await client.LinkTesterAsync("pl_tt_xxx");

            var body = FindClaimBody(handler, "POST");
            Assert.IsNotNull(body);
            Assert.AreEqual("pl_tt_xxx", body!["claimToken"]!.Value<string>());
            Assert.AreEqual("game_abc", body["gameId"]!.Value<string>());
            Assert.AreEqual("dev_abc", body["deviceId"]!.Value<string>());

            Assert.IsTrue(result.Ok);
            Assert.IsFalse(result.AlreadyLinked);
            Assert.AreEqual("RedFox42", result.Handle);
            Assert.AreEqual(123, result.ClaimedAt);
        }

        [Test]
        public void LinkTesterAsync_ThrowsWhenKeyUnresolvable()
        {
            // Resolve fails (invalid key) → the playtest call surfaces a clear
            // resolve_failed error instead of silently posting without a game.
            var handler = RoutedHandler("{}", resolveGameId: null);
            using var client = NewClient(handler);
            var ex = Assert.ThrowsAsync<PlayloopPlaytestException>(async () =>
                await client.LinkTesterAsync("pl_tt_x"));
            Assert.AreEqual("resolve_failed", ex!.Reason);
        }

        [Test]
        public void LinkTesterAsync_ThrowsWithoutClaimToken()
        {
            var handler = RoutedHandler("{}");
            using var client = NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.LinkTesterAsync(""));
        }

        [Test]
        public async Task LinkTesterAsync_ReturnsAlreadyLinkedOnSameDeviceRetry()
        {
            var handler = RoutedHandler(
                "{\"ok\":true,\"alreadyLinked\":true,\"handle\":\"h\",\"claimedAt\":1}");
            using var client = NewClient(handler);
            var result = await client.LinkTesterAsync("pl_tt_x");
            Assert.IsTrue(result.AlreadyLinked);
        }

        [Test]
        public void LinkTesterAsync_ThrowsLinkAlreadyClaimedOn409()
        {
            var handler = RoutedHandler(
                "{\"ok\":false,\"reason\":\"already_linked_to_another_device\",\"message\":\"different device\"}",
                featureStatus: 409);
            using var client = NewClient(handler);
            var ex = Assert.ThrowsAsync<LinkAlreadyClaimedException>(async () =>
                await client.LinkTesterAsync("pl_tt_x"));
            Assert.AreEqual("already_linked_to_another_device", ex!.Reason);
            Assert.AreEqual(409, ex.Status);
        }

        [Test]
        public void LinkTesterAsync_ThrowsPlaytestExceptionOnOtherFailures()
        {
            var handler = RoutedHandler(
                "{\"ok\":false,\"reason\":\"wrong_game\"}", featureStatus: 403);
            using var client = NewClient(handler);
            var ex = Assert.ThrowsAsync<PlayloopPlaytestException>(async () =>
                await client.LinkTesterAsync("pl_tt_x"));
            Assert.AreEqual("wrong_game", ex!.Reason);
            Assert.AreEqual(403, ex.Status);
        }

        [Test]
        public async Task GetCurrentTesterAsync_ReturnsLinkedHandle()
        {
            var handler = RoutedHandler(
                "{\"linked\":true,\"handle\":\"RedFox42\",\"claimedAt\":999}");
            using var client = NewClient(handler);
            var result = await client.GetCurrentTesterAsync();

            Assert.IsTrue(result.Linked);
            Assert.AreEqual("RedFox42", result.Handle);
            Assert.AreEqual(999, result.ClaimedAt);

            HttpRequestSpec? testerCall = null;
            foreach (var call in handler.Calls)
                if (call.Url.Contains("/api/telemetry/tester?")) testerCall = call;
            Assert.IsNotNull(testerCall);
            Assert.AreEqual("GET", testerCall!.Method);
            StringAssert.Contains("gameId=game_abc", testerCall.Url);
            StringAssert.Contains("deviceId=dev_abc", testerCall.Url);
        }

        [Test]
        public async Task GetCurrentTesterAsync_ReturnsUnlinked()
        {
            var handler = RoutedHandler("{\"linked\":false}");
            using var client = NewClient(handler);
            var result = await client.GetCurrentTesterAsync();
            Assert.IsFalse(result.Linked);
            Assert.IsNull(result.Handle);
        }

        [Test]
        public async Task UnlinkTesterAsync_SendsWithClaimToken()
        {
            // Unlink requires the original claimToken.
            // Body shape: { claimToken, gameId, deviceId }.
            var handler = RoutedHandler("{\"ok\":true,\"unlinked\":true}");
            using var client = NewClient(handler);
            var result = await client.UnlinkTesterAsync("pl_tt_xxx");
            Assert.IsTrue(result.Unlinked);

            var body = FindClaimBody(handler, "DELETE");
            Assert.IsNotNull(body);
            Assert.AreEqual("pl_tt_xxx", body!["claimToken"]!.Value<string>());
            Assert.AreEqual("game_abc", body["gameId"]!.Value<string>());
            Assert.AreEqual("dev_abc", body["deviceId"]!.Value<string>());
        }

        [Test]
        public async Task UnlinkTesterAsync_ReturnsFalseWhenNothingLinked()
        {
            var handler = RoutedHandler("{\"ok\":true,\"unlinked\":false}");
            using var client = NewClient(handler);
            var result = await client.UnlinkTesterAsync("pl_tt_xxx");
            Assert.IsFalse(result.Unlinked);
        }

        [Test]
        public void UnlinkTesterAsync_ThrowsWithoutClaimToken()
        {
            // claimToken is required; empty/null is a programmer error.
            var handler = RoutedHandler("{\"ok\":true,\"unlinked\":false}");
            using var client = NewClient(handler);
            Assert.ThrowsAsync<System.ArgumentException>(async () =>
            {
                await client.UnlinkTesterAsync("");
            });
        }

        [Test]
        public async Task Playtest_NamespaceMatchesTopLevel()
        {
            var handler = RoutedHandler(
                "{\"ok\":true,\"alreadyLinked\":false,\"handle\":\"h\",\"claimedAt\":1}");
            using var client = NewClient(handler);
            var a = await client.LinkTesterAsync("pl_tt_x");
            var b = await client.Playtest.LinkTesterAsync("pl_tt_y");
            Assert.AreEqual(a.Handle, b.Handle);
        }
    }
}
