#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Playloop;
using Playloop.Feedback;

namespace Playloop.Tests
{
    /// <summary>
    /// Public-contract tests for the <see cref="FeedbackForm"/> overloads:
    /// argument validation that runs without instantiating the UGUI
    /// overlay, so it's safe in EditMode.
    ///
    /// The single-overlay guard and the lazy session-id fail-soft path
    /// both need a live UGUI form (EventSystem + InputField driven to the
    /// Send button), so they belong in PlayMode coverage, not these
    /// EditMode contract tests.
    /// </summary>
    [TestFixture]
    public class FeedbackFormTests
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
        public void OpenAsync_ConcreteId_ThrowsOnEmptySessionId()
        {
            using var client = NewClient();
            Assert.ThrowsAsync<ArgumentException>(
                () => client.FeedbackForm.OpenAsync("ff_test", ""));
        }

        [Test]
        public void OpenAsync_Provider_ThrowsOnEmptyFormId()
        {
            using var client = NewClient();
            Assert.ThrowsAsync<ArgumentException>(
                () => client.FeedbackForm.OpenAsync("", () => "ses_1"));
        }

        [Test]
        public void OpenAsync_Provider_ThrowsOnNullProvider()
        {
            using var client = NewClient();
            Assert.ThrowsAsync<ArgumentNullException>(
                () => client.FeedbackForm.OpenAsync("ff_test", (Func<string?>)null!));
        }

        [Test]
        public void OpenAsync_Provider_ThrowsOnEmptyFields()
        {
            using var client = NewClient();
            Assert.ThrowsAsync<ArgumentException>(
                () => client.FeedbackForm.OpenAsync(
                    "ff_test", () => "ses_1", fields: new List<FeedbackField>()));
        }
    }
}
#endif
