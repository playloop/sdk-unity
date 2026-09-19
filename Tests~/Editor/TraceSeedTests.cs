#nullable enable
#if PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Playloop.Trace;

namespace Playloop.Tests
{
    /// <summary>
    /// <c>Trace.SetSeed</c>: the run's seed rides every chunk of that run as
    /// <c>seed</c>, clears when the next run opens, and is validated like the
    /// other labels.
    /// </summary>
    [TestFixture]
    public class TraceSeedTests
    {
        private const long Wall = 1758140000000L;

        private static TraceSampler NewSampler(List<Dictionary<string, object>> chunks)
            => new TraceSampler(10, TracePlane.XY, 8, TraceOptions.DefaultMaxBytesPerSession, (c, t0) => chunks.Add(c));

        private static void Play(TraceSampler sampler, double fromSec, double toSec)
        {
            for (double t = fromSec; t < toSec - 1e-9; t += 0.1)
            {
                double r = Math.Round(t, 3);
                sampler.Tick(r, Wall + (long)Math.Round(r * 1000));
            }
        }

        [Test]
        public void Seed_RidesEveryChunkOfItsRun_InWireOrder()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetSeed("r7xK-2_a");
            sampler.SetPosition(0, 0, -1);
            Play(sampler, 0.0, 12.0); // two full chunks and a partial one
            sampler.End(TraceEndReason.Death);

            Assert.AreEqual(3, chunks.Count);
            foreach (var c in chunks)
            {
                Assert.AreEqual("r7xK-2_a", c["seed"], "every chunk, so a lost chunk cannot lose it");
                CollectionAssert.AreEqual(new[] { "v", "seq", "seg", "seed", "t0", "hz", "plane" }, c.Keys.Take(7).ToArray());
            }
        }

        [Test]
        public void NoSeed_NoKey()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetPosition(0, 0, -1);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.Quit);
            Assert.IsFalse(chunks[0].ContainsKey("seed"));
            CollectionAssert.AreEqual(new[] { "v", "seq", "seg", "t0", "hz", "plane" }, chunks[0].Keys.Take(6).ToArray());
        }

        [Test]
        public void Seed_ClearsWhenTheNextRunOpens()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetSeed("first");
            sampler.SetPosition(0, 0, -1);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.Death);
            sampler.SetPosition(1, 1, -1); // opens run 1 with no SetSeed
            sampler.Tick(1.0, Wall + 1000);
            sampler.End(TraceEndReason.Quit);

            Assert.AreEqual("first", chunks[0]["seed"], "the end chunk still carries its run's seed");
            Assert.IsFalse(chunks[1].ContainsKey("seed"), "a run without SetSeed has no seed");
        }

        [Test]
        public void SetSeed_BetweenRuns_AppliesToTheRunThatOpensNext()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetSeed("run0");
            sampler.SetPosition(0, 0, -1);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.Death);
            sampler.SetSeed("run1");
            sampler.Begin();
            sampler.Tick(1.0, Wall + 1000);
            sampler.End(TraceEndReason.Quit);

            Assert.AreEqual("run0", chunks[0]["seed"]);
            Assert.AreEqual("run1", chunks[1]["seed"]);
        }

        [Test]
        public void SetSeed_MidRun_ReplacesTheCurrentRunsSeed()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetPosition(0, 0, -1);
            sampler.Tick(0.0, Wall);
            sampler.SetSeed("late");
            sampler.End(TraceEndReason.Quit);
            Assert.AreEqual("late", chunks[0]["seed"]);
        }

        [Test]
        public void ResetForNewSession_ClearsTheSeed_AndOneSetForTheNextRun()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetSeed("old");
            sampler.SetPosition(0, 0, -1);
            sampler.Tick(0.0, Wall);
            sampler.End(TraceEndReason.Death);
            sampler.SetSeed("pending");
            sampler.EndSession();
            sampler.ResetForNewSession();
            sampler.Tick(5.0, Wall + 5000);
            sampler.End(TraceEndReason.Quit);

            Assert.AreEqual(2, chunks.Count);
            Assert.IsFalse(chunks[1].ContainsKey("seed"), "a new session starts with no seed");

            // A seed set while a run is open clears too.
            sampler.SetPosition(2, 2, -1);
            Assert.IsFalse(sampler.IsBetweenRuns);
            sampler.SetSeed("mid");
            sampler.ResetForNewSession();
            sampler.Tick(9.0, Wall + 9000);
            sampler.End(TraceEndReason.Quit);
            Assert.IsFalse(chunks[2].ContainsKey("seed"));
        }

        [Test]
        public void SetSeed_Long_WritesItsDecimalString()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"sessionId\":\"s1\"}");
            using var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = handler,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                RetryAttempts = 1,
                EnableCrashReporting = false,
                Environment = "playtest",
            });
            client.Telemetry.ClearBuffer();
            client.Trace.SetSeed(-9007199254740993L);
            client.Trace.SetPosition(0f, 0f);
            client.Trace.Tick(0.0, Wall);
            client.Trace.End(TraceEndReason.Quit);

            var chunk = client.Telemetry.SnapshotPending().Single(e => e.Name == "trace_chunk");
            Assert.AreEqual("-9007199254740993", chunk.Data!["seed"], "a string, so a 64-bit seed survives JSON readers");
        }

        [Test]
        public void SetSeed_ValidatesLikeTheOtherLabels_EvenWhenDisabled()
        {
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });
            Assert.DoesNotThrow(() => client.Trace.SetSeed("A-z_09"));
            Assert.DoesNotThrow(() => client.Trace.SetSeed(new string('s', 32)));
            Assert.DoesNotThrow(() => client.Trace.SetSeed(long.MinValue));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed(""));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed((string)null!));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed(new string('s', 33)));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed("two words"));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed("seed:1"));
            Assert.Throws<ArgumentException>(() => client.Trace.SetSeed("séed"));

            Assert.IsTrue(TraceLabels.IsSeed("123"));
            Assert.IsFalse(TraceLabels.IsSeed("1.5"));
        }

        [Test]
        public void SetSeed_DoesNotWireTheTrace()
        {
            var chunks = new List<Dictionary<string, object>>();
            var sampler = NewSampler(chunks);
            sampler.SetSeed("idle");
            Assert.IsFalse(sampler.IsWired);
            for (int ms = 0; ms < 6000; ms += 100) sampler.Tick(ms / 1000.0, Wall + ms);
            Assert.AreEqual(0, chunks.Count, "a seed alone sends no chunk of zeros");
        }
    }
}
#endif
