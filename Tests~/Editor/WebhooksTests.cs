#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Newtonsoft.Json;
using Playloop;
using Playloop.Webhooks;

namespace Playloop.Tests
{
    [TestFixture]
    public class WebhooksTests
    {
        private const string Secret = "whsec_super_secret";

        private static string Sign(string body, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Test]
        public void AcceptsCorrectlySignedBody_Analyzed()
        {
            var body = JsonConvert.SerializeObject(new
            {
                @event = "session.analyzed",
                session_id = "ses_8f3a2c",
                duration = "00:42:13",
                insights = new[]
                {
                    new { type = "stuck-point", severity = "high", confidence = 0.87 },
                },
            });
            var signature = Sign(body, Secret);
            var ev = PlayloopWebhooks.Verify(body, signature, Secret);

            Assert.AreEqual("session.analyzed", ev.Event);
            Assert.AreEqual("ses_8f3a2c", ev.SessionId);
            Assert.IsNotNull(ev.Insights);
            Assert.AreEqual("stuck-point", ev.Insights![0].Type);
        }

        [Test]
        public void AcceptsCorrectlySignedBody_Failed()
        {
            var body = JsonConvert.SerializeObject(new
            {
                @event = "session.failed",
                session_id = "ses_1",
                error = "transcription timed out",
            });
            var signature = Sign(body, Secret);
            var ev = PlayloopWebhooks.Verify(body, signature, Secret);
            Assert.AreEqual("session.failed", ev.Event);
            Assert.AreEqual("transcription timed out", ev.Error);
        }

        [Test]
        public void RejectsMissingSignature()
        {
            var ex = Assert.Throws<PlayloopException>(() =>
                PlayloopWebhooks.Verify("{}", signature: null, secret: Secret))!;
            Assert.AreEqual(401, ex.Status);
        }

        [Test]
        public void RejectsEmptySignature()
        {
            var ex = Assert.Throws<PlayloopException>(() =>
                PlayloopWebhooks.Verify("{}", signature: "", secret: Secret))!;
            Assert.AreEqual(401, ex.Status);
        }

        [Test]
        public void RejectsWrongSignature()
        {
            var body = JsonConvert.SerializeObject(new { @event = "session.analyzed", session_id = "a", duration = "b" });
            var wrong = Sign(body, "a-different-secret");
            var ex = Assert.Throws<PlayloopException>(() =>
                PlayloopWebhooks.Verify(body, wrong, Secret))!;
            Assert.AreEqual(401, ex.Status);
        }

        [Test]
        public void RejectsLengthMismatch()
        {
            var ex = Assert.Throws<PlayloopException>(() =>
                PlayloopWebhooks.Verify("{}", "ab", Secret))!;
            Assert.AreEqual(401, ex.Status);
        }

        [Test]
        public void RejectsNonJsonBody_AfterSignaturePasses()
        {
            const string body = "not-json-at-all";
            var signature = Sign(body, Secret);
            var ex = Assert.Throws<PlayloopException>(() =>
                PlayloopWebhooks.Verify(body, signature, Secret))!;
            Assert.AreEqual(400, ex.Status);
        }

        [Test]
        public void RejectsEmptySecret()
        {
            Assert.Throws<ArgumentException>(() =>
                PlayloopWebhooks.Verify("{}", "abc", ""));
        }

        [Test]
        public void RejectsNullBody()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PlayloopWebhooks.Verify(null!, "abc", Secret));
        }
    }
}
