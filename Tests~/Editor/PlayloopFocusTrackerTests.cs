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
    /// Verifies the wire contract used by the focus tracker MonoBehaviour
    /// (<c>Runtime/Telemetry/PlayloopFocusTracker.cs</c>).
    ///
    /// <para>
    /// The MonoBehaviour itself can't be instantiated under <c>dotnet test</c>
    /// (it's <c>#if UNITY_2018_1_OR_NEWER</c>-guarded), so these tests pin the
    /// event-name + payload-shape contract by replaying the same Track call
    /// the tracker makes. If anyone changes the casing of <c>focus_lost</c> /
    /// <c>focus_gained</c> or the <c>timestampMs</c> key the server-side
    /// active-play-time math depends on, these tests fail.
    /// </para>
    /// </summary>
    [TestFixture]
    public class PlayloopFocusTrackerTests
    {
        [Test]
        public async Task FocusLost_EventNameAndPayloadMatchContract()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            // Mirrors what PlayloopFocusTracker.OnApplicationFocus(false) does.
            var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            client.Telemetry.Track("focus_lost", new Dictionary<string, object>
            {
                { "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            });
            var afterMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await client.Telemetry.FlushAsync();

            var call = handler.Calls.Single();
            var json = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            var events = (JArray)json["events"]!;
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual("focus_lost", events[0]["name"]!.Value<string>());

            // Payload is { timestampMs: <long> }. Server treats this as the
            // canonical idle-start moment for active-play-time math.
            var timestampMs = events[0]["data"]!["timestampMs"]!.Value<long>();
            Assert.GreaterOrEqual(timestampMs, beforeMs);
            Assert.LessOrEqual(timestampMs, afterMs);
        }

        [Test]
        public async Task FocusGained_EventNameAndPayloadMatchContract()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            // Mirrors what PlayloopFocusTracker.OnApplicationFocus(true) does.
            client.Telemetry.Track("focus_gained", new Dictionary<string, object>
            {
                { "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            });

            await client.Telemetry.FlushAsync();

            var call = handler.Calls.Single();
            var json = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            var events = (JArray)json["events"]!;
            Assert.AreEqual("focus_gained", events[0]["name"]!.Value<string>());
            Assert.IsNotNull(events[0]["data"]!["timestampMs"]);
        }

        [Test]
        public async Task FocusEvents_BufferAndFlushTogether()
        {
            var handler = MockHttpHandler.ReturnsStatus(204);
            using var client = PlayloopClientTests.NewClient(handler);

            // Simulate a quick alt-tab cycle. Both events should land in the
            // same batch. Track is sync, and the next AutoBatch tick drains
            // the buffer in one POST.
            client.Telemetry.Track("focus_lost", new Dictionary<string, object>
            {
                { "timestampMs", 1_700_000_000_000L },
            });
            client.Telemetry.Track("focus_gained", new Dictionary<string, object>
            {
                { "timestampMs", 1_700_000_004_000L },
            });

            Assert.AreEqual(2, client.Telemetry.PendingCount);
            await client.Telemetry.FlushAsync();

            var call = handler.Calls.Single();
            var json = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            var events = (JArray)json["events"]!;
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual("focus_lost", events[0]["name"]!.Value<string>());
            Assert.AreEqual("focus_gained", events[1]["name"]!.Value<string>());
            Assert.AreEqual(1_700_000_000_000L, events[0]["data"]!["timestampMs"]!.Value<long>());
            Assert.AreEqual(1_700_000_004_000L, events[1]["data"]!["timestampMs"]!.Value<long>());
        }
    }
}
