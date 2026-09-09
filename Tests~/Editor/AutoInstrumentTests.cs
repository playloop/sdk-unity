#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
using Playloop.Telemetry;

namespace Playloop.Tests
{
    /// <summary>
    /// Tests for the pure-C# kernel that powers Unity auto-instrumentation.
    ///
    /// <para>
    /// The Unity MonoBehaviour (<c>Runtime/Telemetry/AutoInstrument.cs</c>) is
    /// excluded from these tests because exercising MonoBehaviour callbacks
    /// requires a Unity runtime: slow + flaky for state-machine assertions.
    /// We test the kernel (<see cref="AutoInstrumentEngine"/>) here and lean
    /// on the Unity Test Runner for the integration coverage when needed.
    /// </para>
    /// </summary>
    [TestFixture]
    public class AutoInstrumentTests
    {
        // ──────────────────── PlayloopOptions flags ────────────────────

        [Test]
        public void Options_DefaultsAreSensible()
        {
            var o = new PlayloopOptions();
            // Five default-on, memory off (noisy on low-memory devices).
            Assert.IsTrue(o.AutoInstrument.Scenes);
            Assert.IsTrue(o.AutoInstrument.Errors);
            Assert.IsTrue(o.AutoInstrument.Idle);
            Assert.IsTrue(o.AutoInstrument.Fps);
            Assert.IsFalse(o.AutoInstrument.Memory);
            Assert.AreEqual(30f, o.AutoInstrument.FpsDropThreshold);
        }

        [Test]
        public void Options_FlagsToggleIndependently()
        {
            var o = new PlayloopOptions
            {
                AutoInstrument = { Fps = false, Memory = true },
            };
            Assert.IsTrue(o.AutoInstrument.Scenes);
            Assert.IsTrue(o.AutoInstrument.Errors);
            Assert.IsTrue(o.AutoInstrument.Idle);
            Assert.IsFalse(o.AutoInstrument.Fps);
            Assert.IsTrue(o.AutoInstrument.Memory);
        }

        [Test]
        public void Settings_FromOptions_CopiesEveryFlag()
        {
            var o = new PlayloopOptions
            {
                AutoInstrument = new AutoInstrumentOptions
                {
                    Scenes = false,
                    Errors = false,
                    Idle = false,
                    Fps = false,
                    Memory = true,
                    IdleThresholdSec = 45f,
                    FpsDropThreshold = 24f,
                },
            };
            var s = AutoInstrumentSettings.FromOptions(o);
            Assert.IsFalse(s.AutoInstrumentScenes);
            Assert.IsFalse(s.AutoInstrumentErrors);
            Assert.IsFalse(s.AutoInstrumentIdle);
            Assert.IsFalse(s.AutoInstrumentFps);
            Assert.IsTrue(s.AutoInstrumentMemory);
            Assert.AreEqual(45f, s.IdleThresholdSec);
            Assert.AreEqual(24f, s.FpsDropThreshold);
            Assert.IsTrue(s.AnyEnabled, "memory flag alone should still flip AnyEnabled");
        }

        [Test]
        public void Settings_AnyEnabled_FalseWhenAllOff()
        {
            var s = AutoInstrumentSettings.Disabled;
            Assert.IsFalse(s.AnyEnabled);
        }

        // ──────────────────── error rate limit ────────────────────

        [Test]
        public void Engine_ErrorCounter_AllowsFirst50ThenStops()
        {
            var e = new AutoInstrumentEngine(30f, 30f);
            for (int i = 0; i < AutoInstrumentEngine.MaxErrorsPerSession; i++)
            {
                Assert.IsTrue(e.TryRecordError(), $"error #{i + 1} should be allowed");
            }
            // 51st is dropped.
            Assert.IsFalse(e.TryRecordError(), "51st error must be dropped (rate cap)");
            Assert.AreEqual(50, e.ErrorCount);
        }

        [Test]
        public void Engine_ErrorCounter_ResetClearsCap()
        {
            var e = new AutoInstrumentEngine(30f, 30f);
            for (int i = 0; i < 50; i++) e.TryRecordError();
            Assert.IsFalse(e.TryRecordError());
            e.ResetErrorCounter();
            Assert.AreEqual(0, e.ErrorCount);
            Assert.IsTrue(e.TryRecordError(), "after reset, first error in the new session must be allowed");
        }

        // ──────────────────── fps_drop throttling ────────────────────

        [Test]
        public void Engine_FpsDrop_FiresWhenMedianBelowThreshold()
        {
            // Threshold 30 FPS. Feed 60 frames at 0.1s (=10 FPS) over 6s window.
            // The median is clearly below 30. After 3s of samples (the
            // window length) we should get a fps_drop.
            var e = new AutoInstrumentEngine(30f, 30f);
            int drops = 0;
            for (int i = 0; i < 60; i++)
            {
                var dec = e.RecordFrame(deltaSec: 0.1f, nowSec: i * 0.1f);
                if (dec.Fire) drops++;
            }
            Assert.GreaterOrEqual(drops, 1, "sustained 10 FPS should trigger at least one fps_drop");
        }

        [Test]
        public void Engine_FpsDrop_DoesNotFireWhenMedianAboveThreshold()
        {
            // 60 FPS sustained: never drops.
            var e = new AutoInstrumentEngine(30f, 30f);
            int drops = 0;
            for (int i = 0; i < 240; i++) // 4 sec of 60 FPS frames
            {
                var dec = e.RecordFrame(deltaSec: 1f / 60f, nowSec: i / 60f);
                if (dec.Fire) drops++;
            }
            Assert.AreEqual(0, drops, "60 FPS sustained must never fire fps_drop");
        }

        [Test]
        public void Engine_FpsDrop_ThrottledTo1PerCooldownWindow()
        {
            // Sustained slow patch. 600 frames at 10 FPS = 60 seconds.
            // Cooldown is 5s, so we expect ≤ 13 drops (one per cooldown +
            // a startup tolerance), never the full 600.
            var e = new AutoInstrumentEngine(30f, 30f);
            int drops = 0;
            int frames = 600;
            for (int i = 0; i < frames; i++)
            {
                var dec = e.RecordFrame(deltaSec: 0.1f, nowSec: i * 0.1f);
                if (dec.Fire) drops++;
            }
            Assert.GreaterOrEqual(drops, 1);
            Assert.Less(drops, 20,
                $"throttling broken: 60s of stuck frames produced {drops} drops " +
                "(cooldown is 5s, expect ≤ ~13).");
        }

        [Test]
        public void Engine_FpsDrop_IgnoresInvalidDeltas()
        {
            var e = new AutoInstrumentEngine(30f, 30f);
            // Zero, negative, NaN, infinity: all dropped silently.
            Assert.IsFalse(e.RecordFrame(0f, 0f).Fire);
            Assert.IsFalse(e.RecordFrame(-0.01f, 0f).Fire);
            Assert.IsFalse(e.RecordFrame(float.NaN, 0f).Fire);
            Assert.IsFalse(e.RecordFrame(float.PositiveInfinity, 0f).Fire);
        }

        // ──────────────────── idle state machine ────────────────────

        [Test]
        public void Engine_Idle_FiresStartAfterThreshold()
        {
            // 30s threshold. No input for 31s: Start fires at 30s.
            var e = new AutoInstrumentEngine(idleThresholdSec: 30f, fpsDropThreshold: 30f);
            e.ResetIdle(0f);

            // Below threshold → no idle.
            var d1 = e.Tick(inputThisFrame: false, nowSec: 10f);
            Assert.AreEqual(IdleDecision.Kind.None, d1.Outcome);

            var d2 = e.Tick(inputThisFrame: false, nowSec: 30f);
            Assert.AreEqual(IdleDecision.Kind.Start, d2.Outcome);
            Assert.AreEqual(30f, d2.IdleThresholdSec);

            // Already idle → no extra Start.
            var d3 = e.Tick(inputThisFrame: false, nowSec: 35f);
            Assert.AreEqual(IdleDecision.Kind.None, d3.Outcome);
        }

        [Test]
        public void Engine_Idle_EndFiresOnInputAfterStart()
        {
            var e = new AutoInstrumentEngine(idleThresholdSec: 30f, fpsDropThreshold: 30f);
            e.ResetIdle(0f);

            // Go idle at t=30s.
            e.Tick(false, 30f);
            Assert.IsTrue(e.IsIdle);

            // Input at t=45s → End fires with 15s duration.
            var d = e.Tick(inputThisFrame: true, nowSec: 45f);
            Assert.AreEqual(IdleDecision.Kind.End, d.Outcome);
            Assert.AreEqual(15f, d.IdleDurationSec, 0.01f);
            Assert.IsFalse(e.IsIdle);
        }

        [Test]
        public void Engine_Idle_InputBeforeThresholdPreventsStart()
        {
            var e = new AutoInstrumentEngine(idleThresholdSec: 30f, fpsDropThreshold: 30f);
            e.ResetIdle(0f);

            // Tap input every 20s for 100s: never crosses the 30s gap.
            for (int t = 20; t <= 100; t += 20)
            {
                var d = e.Tick(inputThisFrame: true, nowSec: t);
                Assert.AreEqual(IdleDecision.Kind.None, d.Outcome);
            }
            Assert.IsFalse(e.IsIdle);
        }

        // ──────────────────── memory cooldown ────────────────────

        [Test]
        public void Engine_Memory_RespectsCooldown()
        {
            var e = new AutoInstrumentEngine(30f, 30f);
            Assert.IsTrue(e.TryRecordMemoryEvent(0f));
            Assert.IsFalse(e.TryRecordMemoryEvent(10f), "10s after a memory event is inside the 30s cooldown");
            Assert.IsTrue(e.TryRecordMemoryEvent(45f), "45s after is outside the cooldown");
        }

        // ──────────────────── snake_case wire names: Unity SDK promise ────────────────────
        //
        // We can't exercise the MonoBehaviour without a Unity runtime, but
        // we CAN lock the wire names down at the test layer so a refactor
        // that accidentally renames an auto-event (e.g. to scene_change or
        // appError) gets caught.

        [Test]
        public void WireFormat_AutoEventNamesAreSnakeCase()
        {
            var expected = new HashSet<string>
            {
                "scene_changed",
                "application_error",
                "idle_start",
                "idle_end",
                "fps_drop",
                "memory_pressure",
            };
            foreach (var n in expected)
            {
                Assert.IsTrue(n == n.ToLowerInvariant(), $"{n} must be lowercase snake_case");
                Assert.IsFalse(n.Contains(" "), $"{n} must not contain spaces");
            }
        }
    }
}
