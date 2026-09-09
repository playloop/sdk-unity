#nullable enable
using System;
using System.Reflection;
using NUnit.Framework;
using Playloop.Http;

namespace Playloop.Tests
{
    /// <summary>
    /// Pure-C# unit tests for <see cref="RetryPolicy.ComputeDelayMs"/>.
    /// The clock + random unit are injected so every assertion is deterministic.
    /// The retry contract is canonical across the Unity / Godot / Python /
    /// TypeScript SDKs. If any number here changes, the other SDKs are wrong.
    /// </summary>
    [TestFixture]
    public class RetryPolicyTests
    {
        // ComputeDelayMs is internal; bind once via reflection so the tests
        // don't need [InternalsVisibleTo] plumbing.
        private static readonly MethodInfo s_compute = typeof(RetryPolicy).GetMethod(
            "ComputeDelayMs",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RetryPolicy.ComputeDelayMs not found");

        private static int Compute(RetryPolicy p, int attempt, string? retryAfter, long nowMs, double randomUnit)
        {
            return (int)s_compute.Invoke(p, new object?[] { attempt, retryAfter, nowMs, randomUnit })!;
        }

        [Test]
        public void Disabled_StillUsesDefaultBaseAndMax()
        {
            // Disabled policy (Attempts=1) doesn't affect delay math. The
            // loop in RetryingHttpHandler never asks. But the math should
            // still behave when called.
            var p = RetryPolicy.Disabled;
            var d = Compute(p, attempt: 1, retryAfter: null, nowMs: 0, randomUnit: 0.5);
            // 500 * 2^0 * (0.75 + 0.5 * 0.5) = 500 * 1 = 500
            Assert.AreEqual(500, d);
        }

        [Test]
        public void ExponentialGrowth_AtMidpointJitter()
        {
            // randomUnit = 0.5 → factor = 1.0 → no net jitter, easy assertions.
            var p = new RetryPolicy { Attempts = 5, BaseMs = 500, MaxMs = 5000 };
            Assert.AreEqual(500,  Compute(p, 1, null, 0, 0.5)); // 500 * 1
            Assert.AreEqual(1000, Compute(p, 2, null, 0, 0.5)); // 500 * 2
            Assert.AreEqual(2000, Compute(p, 3, null, 0, 0.5)); // 500 * 4
            Assert.AreEqual(4000, Compute(p, 4, null, 0, 0.5)); // 500 * 8
        }

        [Test]
        public void ExponentialGrowth_IsCappedAtMaxMs()
        {
            var p = new RetryPolicy { Attempts = 10, BaseMs = 500, MaxMs = 5000 };
            // Attempt 5 → raw = 500 * 16 = 8000, capped at 5000.
            Assert.AreEqual(5000, Compute(p, 5, null, 0, 0.5));
            Assert.AreEqual(5000, Compute(p, 6, null, 0, 0.5));
            Assert.AreEqual(5000, Compute(p, 7, null, 0, 0.5));
        }

        [Test]
        public void Jitter_FloorIs75Percent_OfRaw()
        {
            // randomUnit = 0.0 → factor = 0.75 → minimum jittered delay.
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            Assert.AreEqual(375,  Compute(p, 1, null, 0, 0.0)); // 500 * 0.75
            Assert.AreEqual(750,  Compute(p, 2, null, 0, 0.0)); // 1000 * 0.75
        }

        [Test]
        public void Jitter_CeilingIs125Percent_OfRaw()
        {
            // randomUnit = 0.99999... → factor → ~1.25. Use a value just under
            // 1.0 (the spec is randomUnit in [0, 1), so 1.0 isn't valid).
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            const double almostOne = 0.99999;
            Assert.AreEqual(624,  Compute(p, 1, null, 0, almostOne)); // 500 * 1.24999...
            Assert.AreEqual(1249, Compute(p, 2, null, 0, almostOne)); // 1000 * 1.24999...
        }

        [Test]
        public void RetryAfter_IntegerSeconds_Wins()
        {
            // Even with the exponential formula screaming "500", a Retry-After
            // of 3 takes precedence and returns 3000ms.
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            Assert.AreEqual(3000, Compute(p, 1, "3", 0, 0.5));
            Assert.AreEqual(0,    Compute(p, 1, "0", 0, 0.5));
        }

        [Test]
        public void RetryAfter_HttpDate_ComputesDelta()
        {
            // Server says "wait until t". Our clock is at `now`. Expected delay
            // = (t - now). RFC1123 only encodes second-precision so we pin the
            // delta on a whole second to keep the assertion stable.
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            const long nowMs = 1_700_000_000_000L; // 2023-11-14ish
            const long futureMs = nowMs + 3_000;   // +3s
            var httpDate = DateTimeOffset.FromUnixTimeMilliseconds(futureMs)
                .ToString("R"); // RFC1123: "Tue, 14 Nov 2023 22:13:23 GMT"

            Assert.AreEqual(3000, Compute(p, 1, httpDate, nowMs, 0.5));
        }

        [Test]
        public void RetryAfter_HttpDate_PastValue_ClampsToZero()
        {
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            const long nowMs = 1_700_000_000_000L;
            var pastMs = nowMs - 5_000;
            var httpDate = DateTimeOffset.FromUnixTimeMilliseconds(pastMs).ToString("R");

            Assert.AreEqual(0, Compute(p, 1, httpDate, nowMs, 0.5));
        }

        [Test]
        public void RetryAfter_Invalid_FallsBackToExponential()
        {
            // "soon" doesn't parse as int or HTTP-date → exponential applies.
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            Assert.AreEqual(500, Compute(p, 1, "soon", 0, 0.5));
            // Empty string also falls through.
            Assert.AreEqual(500, Compute(p, 1, "", 0, 0.5));
        }

        [Test]
        public void RetryAfter_IntegerSeconds_CappedAtMaxMsTimesTwo()
        {
            // Hostile server asking for 60s → clamp to MaxMs * 2 = 10s.
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            Assert.AreEqual(10_000, Compute(p, 1, "60", 0, 0.5));
            Assert.AreEqual(10_000, Compute(p, 1, "3600", 0, 0.5));
        }

        [Test]
        public void RetryAfter_HttpDate_CappedAtMaxMsTimesTwo()
        {
            var p = new RetryPolicy { Attempts = 3, BaseMs = 500, MaxMs = 5000 };
            const long nowMs = 1_700_000_000_000L;
            var farFutureMs = nowMs + 60_000; // +60s
            var httpDate = DateTimeOffset.FromUnixTimeMilliseconds(farFutureMs).ToString("R");

            Assert.AreEqual(10_000, Compute(p, 1, httpDate, nowMs, 0.5));
        }

        [Test]
        public void IsRetryableStatus_Match()
        {
            var ms = typeof(RetryPolicy).GetMethod("IsRetryableStatus",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            bool R(int s) => (bool)ms.Invoke(null, new object[] { s })!;

            Assert.IsTrue(R(429));
            Assert.IsTrue(R(500));
            Assert.IsTrue(R(502));
            Assert.IsTrue(R(503));
            Assert.IsTrue(R(504));

            // 4xx that should NOT retry.
            Assert.IsFalse(R(400));
            Assert.IsFalse(R(401));
            Assert.IsFalse(R(403));
            Assert.IsFalse(R(404));
            Assert.IsFalse(R(409));
            Assert.IsFalse(R(422));

            // Other 5xx that should NOT retry.
            Assert.IsFalse(R(501));
            Assert.IsFalse(R(505));

            // 2xx / 3xx are never retried (the wrapper returns success first).
            Assert.IsFalse(R(200));
            Assert.IsFalse(R(204));
            Assert.IsFalse(R(304));
        }
    }
}
