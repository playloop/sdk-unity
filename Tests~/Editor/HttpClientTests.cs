#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Playloop.Http;

namespace Playloop.Tests
{
    [TestFixture]
    public class HttpClientTests
    {
        [Test]
        public async Task SetsBearerAuthorizationHeader()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.ListAsync();

            Assert.AreEqual(1, handler.Calls.Count);
            Assert.AreEqual("Bearer pl_ik_test", handler.Calls[0].Headers["Authorization"]);
            Assert.AreEqual("application/json", handler.Calls[0].Headers["Accept"]);
            // Default env stamped on every request.
            Assert.AreEqual("production", handler.Calls[0].Headers["X-Playloop-Environment"]);
            // SDK identifier so /api/telemetry can attribute sessions to the Unity card.
            Assert.AreEqual("unity", handler.Calls[0].Headers["X-Playloop-SDK"]);
        }

        [Test]
        public async Task SendsEnvironmentHeader_WhenConfigured()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Environment = "play_test",
                Http = handler,
            };
            using var client = new PlayloopClient(options);
            await client.Sessions.ListAsync();
            Assert.AreEqual("play_test", handler.Calls[0].Headers["X-Playloop-Environment"]);
        }

        [Test]
        public async Task LowercasesAndTrimsEnvironmentAtConstruction()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Environment = "  STAGING  ",
                Http = handler,
            };
            using var client = new PlayloopClient(options);
            await client.Sessions.ListAsync();
            Assert.AreEqual("staging", handler.Calls[0].Headers["X-Playloop-Environment"]);
        }

        [Test]
        public async Task EmptyEnvironment_FallsBackToProduction()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Environment = "   ",
                Http = handler,
            };
            using var client = new PlayloopClient(options);
            await client.Sessions.ListAsync();
            Assert.AreEqual("production", handler.Calls[0].Headers["X-Playloop-Environment"]);
        }

        [Test]
        public async Task ParsesJsonResponse()
        {
            var handler = MockHttpHandler.ReturnsJson("[{\"id\":\"ses_1\",\"title\":\"a\"}]");
            using var client = PlayloopClientTests.NewClient(handler);
            var sessions = await client.Sessions.ListAsync();
            Assert.AreEqual(1, sessions.Count);
            Assert.AreEqual("ses_1", sessions[0].Id);
        }

        [Test]
        public void NonSuccessStatus_ThrowsPlayloopException()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"error\":\"unauthorized\"}",
                status: 401,
                headers: new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-playloop-request-id"] = "req_abc",
                });
            using var client = PlayloopClientTests.NewClient(handler);

            var ex = Assert.ThrowsAsync<PlayloopException>(async () => await client.Sessions.ListAsync())!;
            Assert.AreEqual(401, ex.Status);
            Assert.AreEqual("req_abc", ex.RequestId);
            StringAssert.Contains("unauthorized", ex.Message);
        }

        [Test]
        public void NonSuccessStatus_FallsBackToMessageField()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"message\":\"server fell over\"}", status: 500);
            using var client = PlayloopClientTests.NewClient(handler);
            var ex = Assert.ThrowsAsync<PlayloopException>(async () => await client.Sessions.ListAsync())!;
            Assert.AreEqual(500, ex.Status);
            StringAssert.Contains("server fell over", ex.Message);
        }

        [Test]
        public void NonSuccessStatus_FallsBackToGenericMessage()
        {
            var handler = MockHttpHandler.ReturnsJson("{}", status: 502);
            using var client = PlayloopClientTests.NewClient(handler);
            var ex = Assert.ThrowsAsync<PlayloopException>(async () => await client.Sessions.ListAsync())!;
            Assert.AreEqual(502, ex.Status);
            StringAssert.Contains("502", ex.Message);
        }

        [Test]
        public void NonJsonErrorBody_IsCarriedAsText()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "Internal Server Error",
                status: 500,
                headers: new Dictionary<string, string> { ["content-type"] = "text/plain" });
            using var client = PlayloopClientTests.NewClient(handler);
            var ex = Assert.ThrowsAsync<PlayloopException>(async () => await client.Sessions.ListAsync())!;
            Assert.AreEqual(500, ex.Status);
            Assert.AreEqual("Internal Server Error", ex.Body);
        }

        [Test]
        public void Status204_Returns_DefaultValue_ForReturnType()
        {
            // Routes that return 204 use _http.JsonAsync<object> internally.
            // TelemetryApi.FlushAsync exercises that path; HttpClientTests just
            // verifies no parse error explodes when body is empty + status 204.
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);
            client.Telemetry.Track("x");
            Assert.DoesNotThrowAsync(async () => await client.Telemetry.FlushAsync());
        }

        [Test]
        public async Task ConcatenatesBaseUrlAndPathCorrectly()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg/", // trailing slash
                Http = handler,
            };
            using var client = new PlayloopClient(options);
            await client.Sessions.ListAsync();
            Assert.AreEqual("https://api.test.playloop.gg/api/sessions", handler.Calls[0].Url);
        }
    }
}
