#nullable enable
using NUnit.Framework;

namespace Playloop.Tests
{
    /// <summary>A stand-in for a Steamworks "is Steam up?" flag, read by reflection.</summary>
    public static class FakeSteamFlag
    {
        public static bool Up { get; set; }
        public static string NotABool { get; set; } = "yes";
    }

    [TestFixture]
    public class DistributionTests
    {
        private static PlayloopOptions Options(string? distribution) => new PlayloopOptions
        {
            ApiKey = "pl_ik_test",
            BaseUrl = "https://api.test",
            Http = new MockHttpHandler(),
            TelemetryFlushIntervalMs = 60_000,
            RetryAttempts = 1,
            HeartbeatSec = 0,
            AutoShutdownOnQuit = false,
            Distribution = distribution,
        };

        [Test]
        public void Resolve_ExplicitValueWins_OverDetectedSteam()
        {
            Assert.AreEqual("steam-playtest", DistributionResolver.Resolve("Steam-Playtest", () => true));
            Assert.AreEqual("itch", DistributionResolver.Resolve(" itch ", () => false));
        }

        [Test]
        public void Resolve_DetectedSteam_WhenNothingSet()
        {
            Assert.AreEqual("steam", DistributionResolver.Resolve(null, () => true));
            Assert.AreEqual("steam", DistributionResolver.Resolve("   ", () => true));
        }

        [Test]
        public void Resolve_NothingKnown_IsNull_NeverAGuess()
        {
            Assert.IsNull(DistributionResolver.Resolve(null, () => false));
            // A throwing detector is "not detected", never an error.
            Assert.IsNull(DistributionResolver.Resolve(null, () => throw new System.Exception("boom")));
        }

        [Test]
        public void Normalize_DropsAnythingThatIsNotAShortSlug()
        {
            Assert.AreEqual("direct", DistributionResolver.Normalize("DIRECT"));
            Assert.AreEqual("steam.beta_2", DistributionResolver.Normalize("steam.beta_2"));
            Assert.IsNull(DistributionResolver.Normalize(""));
            Assert.IsNull(DistributionResolver.Normalize("has space"));
            Assert.IsNull(DistributionResolver.Normalize("-leading"));
            Assert.IsNull(DistributionResolver.Normalize("https://x.y"));
            Assert.IsNull(DistributionResolver.Normalize(new string('a', 41)));
            Assert.IsNull(DistributionResolver.Normalize("Unknown"));
            Assert.IsNull(DistributionResolver.Normalize("other"));
        }

        [Test]
        public void ReadStaticBool_ReadsALoadedFlag_AndIgnoresMissingOrWrongTypes()
        {
            FakeSteamFlag.Up = false;
            Assert.IsFalse(DistributionResolver.ReadStaticBool("Playloop.Tests.FakeSteamFlag", "Up"));
            FakeSteamFlag.Up = true;
            try
            {
                Assert.IsTrue(DistributionResolver.ReadStaticBool("Playloop.Tests.FakeSteamFlag", "Up"));
            }
            finally
            {
                FakeSteamFlag.Up = false;
            }
            Assert.IsFalse(DistributionResolver.ReadStaticBool("Playloop.Tests.FakeSteamFlag", "NotABool"));
            Assert.IsFalse(DistributionResolver.ReadStaticBool("Playloop.Tests.FakeSteamFlag", "Missing"));
            Assert.IsFalse(DistributionResolver.ReadStaticBool("No.Such.Type", "IsValid"));
        }

        [Test]
        public void SessionStart_CarriesTheConfiguredDistribution()
        {
            using var client = new PlayloopClient(Options("Itch"));
            var pending = client.Telemetry.SnapshotPending();
            Assert.AreEqual("session_start", pending[0].Name);
            Assert.AreEqual("itch", pending[0].Data!["distribution"]);
        }

        [Test]
        public void SessionStart_OmitsDistribution_WhenUnknown()
        {
            // No value set and no Steamworks integration loaded in the test host.
            using var client = new PlayloopClient(Options(null));
            var pending = client.Telemetry.SnapshotPending();
            Assert.AreEqual("session_start", pending[0].Name);
            Assert.IsFalse(pending[0].Data!.ContainsKey("distribution"));
        }

        [Test]
        public void SessionStart_OmitsDistribution_WhenTheValueIsNotASlug()
        {
            using var client = new PlayloopClient(Options("not a slug"));
            var pending = client.Telemetry.SnapshotPending();
            Assert.IsFalse(pending[0].Data!.ContainsKey("distribution"));
        }
    }
}
