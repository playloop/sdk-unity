#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Playloop.Tests
{
    /// <summary>
    /// Contract tests for <see cref="DeviceIdResolver"/>.
    ///
    /// <para>
    /// Inside Unity the resolver prefers <c>SystemInfo.deviceUniqueIdentifier</c>;
    /// under <c>dotnet test</c> we hit the standalone fallback that synthesizes
    /// a per-process GUID and caches it. Either way, two contracts must hold:
    /// always non-empty, and idempotent within a process.
    /// </para>
    ///
    /// <para>
    /// We also pin the surface area the rest of the SDK depends on:
    /// <see cref="PlayloopClient.DeviceId"/> exposes the resolved id, an explicit
    /// <see cref="PlayloopOptions.DeviceId"/> wins over auto-resolution, and the
    /// auto-resolved id rides on the first telemetry POST so the server can stamp
    /// it on the session row.
    /// </para>
    /// </summary>
    [TestFixture]
    public class DeviceIdResolverTests
    {
        [Test]
        public void Resolve_NeverReturnsNullOrEmpty()
        {
            var id = DeviceIdResolver.Resolve();
            Assert.IsFalse(string.IsNullOrEmpty(id),
                "DeviceIdResolver.Resolve() must always return a non-empty id. " +
                "Consumers can't fall back to a null device identifier.");
        }

        [Test]
        public void Resolve_IsIdempotent_AcrossConsecutiveCalls()
        {
            var first = DeviceIdResolver.Resolve();
            var second = DeviceIdResolver.Resolve();
            var third = DeviceIdResolver.Resolve();
            Assert.AreEqual(first, second,
                "Two consecutive Resolve() calls must return the same id. " +
                "This is the whole point of moving resolution into the SDK.");
            Assert.AreEqual(second, third);
        }

        [Test]
        public void PlayloopClient_AutoResolvesDeviceId_WhenOptionsLeaveItNull()
        {
            var handler = new MockHttpHandler();
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                // DeviceId left null. Should auto-resolve via DeviceIdResolver.
            };
            using var client = new PlayloopClient(options);

            Assert.IsFalse(string.IsNullOrEmpty(client.DeviceId),
                "PlayloopClient.DeviceId must be populated even when options.DeviceId is null.");
            Assert.AreEqual(DeviceIdResolver.Resolve(), client.DeviceId,
                "Auto-resolved DeviceId must match what DeviceIdResolver.Resolve() returns.");
        }

        [Test]
        public void PlayloopClient_ExplicitDeviceId_WinsOverResolver()
        {
            const string explicitId = "device-explicit-override";
            var handler = new MockHttpHandler();
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                DeviceId = explicitId,
            };
            using var client = new PlayloopClient(options);

            Assert.AreEqual(explicitId, client.DeviceId,
                "An explicit options.DeviceId must take precedence over the resolver. " +
                "This is the testing / account-linkage override path.");
        }

        [Test]
        public void PlayloopClient_WhitespaceDeviceId_TreatedAsUnset()
        {
            var handler = new MockHttpHandler();
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                DeviceId = "   ", // whitespace. Should NOT be honored
            };
            using var client = new PlayloopClient(options);

            Assert.AreNotEqual("   ", client.DeviceId,
                "Whitespace-only DeviceId must fall through to the resolver instead of being passed verbatim.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(client.DeviceId));
        }

        [Test]
        public async Task AutoResolvedDeviceId_LandsOnFirstTelemetryFlushBody()
        {
            // Mirrors PlayloopOptions_DeviceId_FlowsToFirstFlushBody but with
            // auto-resolution instead of an explicit value. The resolved id
            // must reach the wire on the create call so the server can stamp
            // the session row.
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}");
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 5000,
                SendInEditor = true, // the assertion is on the wire body
                // DeviceId omitted on purpose.
            };
            using var client = new PlayloopClient(options);

            client.Telemetry.Track("boot");
            await client.Telemetry.FlushAsync();

            var firstBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            var deviceIdOnWire = firstBody["deviceId"]?.Value<string>();
            Assert.IsFalse(string.IsNullOrEmpty(deviceIdOnWire),
                "Auto-resolved DeviceId must be forwarded to TelemetryApi and land on the first POST body.");
            Assert.AreEqual(client.DeviceId, deviceIdOnWire);
        }

        [Test]
        public async Task StartSession_WithoutDeviceId_FallsBackToClientResolvedId()
        {
            // The whole ergonomics win: consumers can call StartSession(metadata)
            // without re-plumbing the device id, and the client-resolved id
            // still rides on the first flush.
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"sessionId\":\"sess_test\",\"appended\":false,\"eventsIngested\":1,\"analyzeKicked\":false}");
            using var client = PlayloopClientTests.NewClient(handler);

            client.Telemetry.StartSession(
                metadata: new Dictionary<string, object> { { "level", "tutorial" } });

            client.Telemetry.Track("a");
            await client.Telemetry.FlushAsync();

            var firstBody = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.AreEqual(client.DeviceId, firstBody["deviceId"]!.Value<string>(),
                "StartSession(metadata) with no explicit deviceId must reuse the client-resolved id.");
        }
    }
}
