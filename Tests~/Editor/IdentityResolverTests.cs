#nullable enable
using NUnit.Framework;
using Playloop.Identity;

namespace Playloop.Tests
{
    /// <summary>
    /// Contract tests for <see cref="VendorIdResolver"/>,
    /// <see cref="LinkedIdStore"/>, <see cref="ConsentStore"/>, and the
    /// <see cref="Playloop"/> static facade.
    ///
    /// Outside Unity (under <c>dotnet test</c>) the resolver returns null,
    /// the stores back to per-process static fields, and the consent store
    /// defaults to <c>"anonymous"</c>. Inside Unity the resolver may find
    /// Steamworks via reflection or iOS IDFV. Same contract either way
    /// (returns null when nothing's detected, never throws).
    /// </summary>
    [TestFixture]
    public class IdentityResolverTests
    {
        [SetUp]
        public void Reset()
        {
            VendorIdResolver.__ResetForTests();
            LinkedIdStore.Clear();
        }

        [Test]
        public void VendorIdResolver_ReturnsNull_WhenNoPlatformVendorAvailable()
        {
            // Standalone test rig has neither Steamworks nor iOS IDFV.
            var id = VendorIdResolver.Resolve();
            Assert.IsNull(id, "VendorIdResolver.Resolve() must return null when no platform vendor id can be detected.");
        }

        [Test]
        public void VendorIdResolver_CachesAcrossCalls()
        {
            var first = VendorIdResolver.Resolve();
            var second = VendorIdResolver.Resolve();
            Assert.AreEqual(first, second,
                "VendorIdResolver caches its result so two calls in the same process return the same value.");
        }

        [Test]
        public void LinkedIdStore_RoundTripsAValue()
        {
            LinkedIdStore.Set("player@example.com");
            Assert.AreEqual("player@example.com", LinkedIdStore.Get());
        }

        [Test]
        public void LinkedIdStore_ClearsOnNullOrEmpty()
        {
            LinkedIdStore.Set("alice@example.com");
            LinkedIdStore.Set(null);
            Assert.IsNull(LinkedIdStore.Get());

            LinkedIdStore.Set("bob@example.com");
            LinkedIdStore.Set("");
            Assert.IsNull(LinkedIdStore.Get());
        }

        [Test]
        public void ConsentStore_DefaultsToAnonymous()
        {
            Assert.AreEqual("anonymous", ConsentStore.Get());
        }

        [Test]
        public void ConsentStore_PersistsArbitraryStrings()
        {
            ConsentStore.Set("studio-wide");
            Assert.AreEqual("studio-wide", ConsentStore.Get());

            ConsentStore.Set("cross-platform");
            Assert.AreEqual("cross-platform", ConsentStore.Get());

            ConsentStore.Set("opt-out");
            Assert.AreEqual("opt-out", ConsentStore.Get());
        }

        [Test]
        public void PlayloopFacade_DelegatesToStores()
        {
            PlayloopSdk.SetLinkedId("facade-test@example.com");
            Assert.AreEqual("facade-test@example.com", LinkedIdStore.Get());

            PlayloopSdk.SetConsent("studio-wide");
            Assert.AreEqual("studio-wide", PlayloopSdk.GetConsent());
            Assert.AreEqual("studio-wide", ConsentStore.Get());

            PlayloopSdk.SetLinkedId(null);
            Assert.IsNull(LinkedIdStore.Get());
        }
    }
}
