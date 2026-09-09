#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Playloop.Tests
{
    [TestFixture]
    public class DiscordApiTests
    {
        [Test]
        public async Task IngestAsync_PostsJsonWithSnakeCaseBody()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"sessionsCreated\":1,\"messagesIngested\":142}");
            using var client = PlayloopClientTests.NewClient(handler);

            var result = await client.Discord.IngestAsync("channel_123", "necromancers-army");

            Assert.AreEqual(1, result.SessionsCreated);
            Assert.AreEqual(142, result.MessagesIngested);

            var call = handler.Calls[0];
            Assert.AreEqual("POST", call.Method);
            StringAssert.EndsWith("/api/webhooks/discord", call.Url);
            Assert.AreEqual("application/json", call.Headers["Accept"]);

            var body = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            Assert.AreEqual("channel_123", body["channel_id"]!.Value<string>());
            Assert.AreEqual("necromancers-army", body["game"]!.Value<string>());
            Assert.IsNull(body["thread_id"]);
        }

        [Test]
        public async Task IngestAsync_IncludesThreadIdWhenProvided()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Discord.IngestAsync("c", "g", threadId: "t");
            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual("t", body["thread_id"]!.Value<string>());
        }

        [Test]
        public async Task IngestAsync_SendsClientLevelRelaySecretHeader()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = NewClientWithRelaySecret(handler, "sec_client");

            await client.Discord.IngestAsync("c", "g");

            Assert.AreEqual("sec_client", handler.Calls[0].Headers["x-playloop-secret"]);
        }

        [Test]
        public async Task IngestAsync_PerCallRelaySecretOverridesClientLevel()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = NewClientWithRelaySecret(handler, "sec_client");

            await client.Discord.IngestAsync("c", "g", relaySecret: "sec_call");

            Assert.AreEqual("sec_call", handler.Calls[0].Headers["x-playloop-secret"]);
        }

        [Test]
        public async Task IngestAsync_OmitsRelaySecretHeaderWhenNotConfigured()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = PlayloopClientTests.NewClient(handler);

            await client.Discord.IngestAsync("c", "g");

            Assert.IsFalse(handler.Calls[0].Headers.ContainsKey("x-playloop-secret"));
        }

        // Mirrors PlayloopClientTests.NewClient, plus a RelaySecret. The
        // shared helper doesn't expose an options hook, so build directly.
        private static PlayloopClient NewClientWithRelaySecret(
            MockHttpHandler handler, string relaySecret)
        {
            var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 50,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                RelaySecret = relaySecret,
            });
            client.Telemetry.ClearBuffer();
            return client;
        }

        [Test]
        public void IngestAsync_RequiresChannelId()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Discord.IngestAsync("", "g"));
        }

        [Test]
        public void IngestAsync_RequiresGame()
        {
            var handler = MockHttpHandler.ReturnsJson("{}");
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Discord.IngestAsync("c", ""));
        }
    }
}
