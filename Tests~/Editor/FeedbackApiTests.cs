#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Playloop;
using Playloop.Feedback;
using Playloop.Playtest;

namespace Playloop.Tests
{
    /// <summary>
    /// Player Feedback: multi-field SDK surface. Verifies the public
    /// client.Feedback.SubmitAsync / client.SubmitFeedbackAsync surface
    /// against MockHttpHandler. Server-side semantics are covered in the
    /// main repo's test suite.
    /// </summary>
    [TestFixture]
    public class FeedbackApiTests
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

        [Test]
        public void FeedbackOnlyClient_DoesNotDrainSavedCrashes()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playloop", "playloop-pending-crashes.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var previous = System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
            const string saved = "[{\"stackTrace\":\"Synthetic crash\",\"crashedAtMs\":1}]";
            try
            {
                System.IO.File.WriteAllText(path, saved);
                using var client = new PlayloopClient(new PlayloopOptions {
                    ApiKey = "pl_ik_test", Http = MockHttpHandler.ReturnsJson("{}"),
                    EnableCrashReporting = false, HeartbeatSec = 0, DeviceId = "synthetic",
                });
                client.InstallCrashHandler();
                Assert.AreEqual(saved, System.IO.File.ReadAllText(path));
            }
            finally
            {
                if (previous != null) System.IO.File.WriteAllBytes(path, previous);
                else System.IO.File.Delete(path);
            }
        }

        [Test]
        public async Task RetryAcrossCalls_PreservesExplicitRequestIdAndOriginalSession()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"ok\":true,\"submissionId\":\"fs_saved\",\"responseIds\":[\"fr_saved\"]}");
            using var client = NewClient(handler);
            var answers = new[] { new FeedbackResponseInput("q1", "Synthetic test note") };
            await client.SubmitFeedbackAsync("ff_x", "sess_original", answers, requestId: "saved-note");
            await client.SubmitFeedbackAsync("ff_x", "sess_original", answers, requestId: "saved-note");
            Assert.AreEqual(2, handler.Calls.Count);
            CollectionAssert.AreEqual(handler.Calls[0].JsonBody!, handler.Calls[1].JsonBody!);
        }

        [Test]
        public async Task NewCalls_GenerateDistinctRequestIds()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"ok\":true,\"submissionId\":\"fs_saved\",\"responseIds\":[\"fr_saved\"]}");
            using var client = NewClient(handler);
            var answers = new[] { new FeedbackResponseInput("q1", "Synthetic test note") };
            await client.Feedback.SubmitAsync("ff_x", "sess_1", answers);
            await client.Feedback.SubmitAsync("ff_x", "sess_1", answers);
            var first = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            var second = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[1].JsonBody!));
            Assert.IsTrue(Guid.TryParse((string?)first["requestId"], out _));
            Assert.AreNotEqual((string?)first["requestId"], (string?)second["requestId"]);
        }

        [Test]
        public async Task SubmitAsync_PostsMultiFieldBody()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"submissionId\":\"fs_abc\",\"formId\":\"ff_xyz\"," +
                "\"sessionId\":\"sess_1\",\"responseIds\":[\"fr_1\",\"fr_2\"]}");
            using var client = NewClient(handler);

            var responses = new List<FeedbackResponseInput>
            {
                new("q1", "5"),
                new("q2", "great"),
            };

            var result = await client.Feedback.SubmitAsync(
                formId: "ff_xyz",
                sessionId: "sess_1",
                responses: responses,
                askedAtSec: 12,
                answeredAtSec: 34,
                requestId: "persisted-note-1");

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("fs_abc", result.SubmissionId);
            Assert.AreEqual("ff_xyz", result.FormId);
            Assert.AreEqual("sess_1", result.SessionId);
            Assert.AreEqual(2, result.ResponseIds.Count);
            Assert.AreEqual("fr_1", result.ResponseIds[0]);

            Assert.AreEqual(1, handler.Calls.Count);
            var call = handler.Calls[0];
            StringAssert.Contains("/api/telemetry/feedback", call.Url);
            Assert.AreEqual("POST", call.Method);

            var body = JObject.Parse(Encoding.UTF8.GetString(call.JsonBody!));
            Assert.AreEqual("persisted-note-1", (string?)body["requestId"]);
            Assert.AreEqual("ff_xyz", (string?)body["formId"]);
            Assert.AreEqual("sess_1", (string?)body["sessionId"]);
            var arr = body["responses"] as JArray;
            Assert.IsNotNull(arr);
            Assert.AreEqual(2, arr!.Count);
            Assert.AreEqual("q1", (string?)arr[0]["fieldId"]);
            Assert.AreEqual("5", (string?)arr[0]["value"]);
            Assert.AreEqual(12L, (long)body["askedAtSec"]!);
            Assert.AreEqual(34L, (long)body["answeredAtSec"]!);
        }

        [Test]
        public async Task SubmitAsync_OmitsOptionalTimestampsWhenNotProvided()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"submissionId\":\"fs_x\",\"formId\":\"ff_x\"," +
                "\"sessionId\":\"s\",\"responseIds\":[\"fr_x\"]}");
            using var client = NewClient(handler);

            await client.Feedback.SubmitAsync(
                formId: "ff_x",
                sessionId: "s",
                responses: new[] { new FeedbackResponseInput("q1", "yes") });

            var body = JObject.Parse(Encoding.UTF8.GetString(handler.Calls[0].JsonBody!));
            Assert.IsNull(body["askedAtSec"]);
            Assert.IsNull(body["answeredAtSec"]);
        }

        [Test]
        public void SubmitAsync_ThrowsWithoutFormId()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Feedback.SubmitAsync("", "s",
                    new[] { new FeedbackResponseInput("q1", "5") }));
        }

        [Test]
        public void SubmitAsync_ThrowsWithoutSessionId()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Feedback.SubmitAsync("ff_x", "",
                    new[] { new FeedbackResponseInput("q1", "5") }));
        }

        [Test]
        public void SubmitAsync_ThrowsOnEmptyResponses()
        {
            using var client = NewClient(MockHttpHandler.ReturnsJson("{}"));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Feedback.SubmitAsync("ff_x", "s",
                    System.Array.Empty<FeedbackResponseInput>()));
        }

        [Test]
        public void SubmitAsync_ThrowsOnMalformedResponse()
        {
            // ok=false / missing submissionId surface as
            // PlayloopPlaytestException so callers can branch on Reason.
            var handler = MockHttpHandler.ReturnsJson("{\"ok\":false}");
            using var client = NewClient(handler);
            Assert.ThrowsAsync<PlayloopPlaytestException>(async () =>
                await client.Feedback.SubmitAsync("ff_x", "s",
                    new[] { new FeedbackResponseInput("q1", "5") }));
        }

        [Test]
        public async Task SubmitFeedbackAsync_TopLevelShortcut_HitsSameRoute()
        {
            var handler = MockHttpHandler.ReturnsJson(
                "{\"ok\":true,\"submissionId\":\"fs_top\",\"formId\":\"ff_x\"," +
                "\"sessionId\":\"s\",\"responseIds\":[\"fr_x\"]}");
            using var client = NewClient(handler);

            var result = await client.SubmitFeedbackAsync(
                "ff_x", "s",
                new[] { new FeedbackResponseInput("q1", "5") });

            Assert.AreEqual("fs_top", result.SubmissionId);
            StringAssert.Contains("/api/telemetry/feedback", handler.Calls[0].Url);
        }

        [Test]
        public void DefaultFeedbackFields_MatchesDashboardStarter()
        {
            // Snapshot covers the "first 3 fields" starter: keep in sync
            // with the TypeScript DEFAULT_FEEDBACK_FIELDS and Python
            // DEFAULT_FEEDBACK_FIELDS so a code-defined form (no
            // dashboard auth) renders identically across engines.
            var snapshot = DefaultFeedbackFields.Snapshot();
            Assert.AreEqual(3, snapshot.Count);
            Assert.AreEqual("q1", snapshot[0].Id);
            Assert.AreEqual(FeedbackFieldKinds.Rating1To5, snapshot[0].Kind);
            Assert.IsTrue(snapshot[0].Required == true);
            Assert.AreEqual(FeedbackFieldKinds.ShortText, snapshot[1].Kind);
            Assert.AreEqual(FeedbackFieldKinds.LongText, snapshot[2].Kind);
        }
    }
}
