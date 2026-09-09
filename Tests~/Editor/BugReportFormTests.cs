#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Playloop;
using Playloop.BugReports;

namespace Playloop.Tests
{
    /// <summary>
    /// Public-contract tests for the <see cref="BugReportForm"/> overloads:
    /// argument validation + the disabled-client fail-soft path that run
    /// without instantiating the UGUI overlay, so they're safe in EditMode.
    ///
    /// The single-overlay guard, the severity default, and the full
    /// fill-and-submit path all need a live UGUI form (EventSystem +
    /// InputField driven to the Submit button), so they belong in PlayMode
    /// coverage, not these EditMode contract tests.
    /// </summary>
    [TestFixture]
    public class BugReportFormTests
    {
        private static PlayloopClient NewClient()
        {
            return new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = MockHttpHandler.ReturnsJson("{}"),
                DeviceId = "dev_abc",
                RetryAttempts = 1,
            });
        }

        [Test]
        public void OpenAsync_ThrowsOnNullProvider()
        {
            using var client = NewClient();
            Assert.ThrowsAsync<ArgumentNullException>(
                () => client.BugReportForm.OpenAsync((Func<string?>)null!));
        }

        [Test]
        public async Task OpenAsync_DisabledClient_ResolvesFailedWithoutOverlay()
        {
            // Blank ingest key => disabled SDK. OpenAsync resolves Failed
            // without ever spawning the overlay (a form that can never submit
            // is worse than no form).
            using var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "",
                BaseUrl = "https://api.test.playloop.gg",
                Http = MockHttpHandler.ReturnsJson("{}"),
                DeviceId = "dev_abc",
                RetryAttempts = 1,
            });

            var result = await client.BugReportForm.OpenAsync(sessionId: "sess_1");

            Assert.AreEqual(BugReportFormOutcome.Failed, result.Outcome);
            Assert.IsNotNull(result.Error);
        }
    }
}
#endif
