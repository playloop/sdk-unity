#nullable enable
using System;
using NUnit.Framework;

namespace Playloop.Tests
{
    [TestFixture]
    public class PlayloopClientTests
    {
        [Test]
        public void Constructor_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PlayloopClient(null!));
        }

        [Test]
        public void Constructor_EmptyApiKey_DoesNotThrow_BuildsDisabledClient()
        {
            // Never-raise contract: a game that ships with a blank ingest key
            // must NOT throw. It builds a DISABLED no-op client instead.
            var options = new PlayloopOptions { ApiKey = "" };
            PlayloopClient client = null!;
            Assert.DoesNotThrow(() => client = new PlayloopClient(options));
            using (client)
            {
                Assert.IsFalse(client.IsEnabled);
            }
        }

        [Test]
        public void Constructor_EmptyBaseUrl_DoesNotThrow_BuildsDisabledClient()
        {
            // A blanked base URL is misconfiguration, not a crash: disabled mode.
            var options = new PlayloopOptions { ApiKey = "pl_ik_test", BaseUrl = "" };
            PlayloopClient client = null!;
            Assert.DoesNotThrow(() => client = new PlayloopClient(options));
            using (client)
            {
                Assert.IsFalse(client.IsEnabled);
            }
        }

        [Test]
        public void Constructor_ValidKey_IsEnabled()
        {
            using var client = NewClient(new MockHttpHandler());
            Assert.IsTrue(client.IsEnabled);
        }

        [Test]
        public void Constructor_InvalidRetryValues_DoNotThrow_ClampedNotDisabled()
        {
            // Bad retry knobs are CLAMPED (never-raise), not a reason to throw
            // or disable the SDK. A configured key stays enabled.
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                RetryAttempts = 0,      // clamps to 1
                RetryBaseMs = -50,      // clamps to 0
                RetryMaxMs = -100,      // clamps to >= baseMs
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
            };
            PlayloopClient client = null!;
            Assert.DoesNotThrow(() => client = new PlayloopClient(options));
            using (client)
            {
                Assert.IsTrue(client.IsEnabled);
            }
        }

        [Test]
        public void DisabledClient_AllApisAreCallableAndInert()
        {
            // The whole point of never-raise: every nested API exists (no null
            // windows) and every call is a safe no-op / control return.
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });

            Assert.IsFalse(client.IsEnabled);

            // Nested APIs are non-null.
            Assert.IsNotNull(client.Sessions);
            Assert.IsNotNull(client.Telemetry);
            Assert.IsNotNull(client.Experiments);
            Assert.IsNotNull(client.Feedback);
            Assert.IsNotNull(client.Discord);
            Assert.IsNotNull(client.Heartbeat);

            // No session_start anchor was fired on a disabled client.
            Assert.AreEqual(0, client.Telemetry.PendingCount);

            // Track drops silently. never buffers, never throws.
            Assert.DoesNotThrow(() => client.Telemetry.Track("death"));
            Assert.AreEqual(0, client.Telemetry.PendingCount);

            // AutoBatch is a no-op (no flush loop / instrument spawned).
            Assert.DoesNotThrow(() => client.Telemetry.AutoBatch());

            // Heartbeat never runs.
            Assert.IsFalse(client.Heartbeat.IsRunning);
            Assert.DoesNotThrow(() => client.Heartbeat.EmitOnce());
            Assert.AreEqual(0, client.Telemetry.PendingCount);
        }

        [Test]
        public void DisabledClient_ExperimentVariant_ReturnsControlNull()
        {
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });

            // Sync read: control default.
            Assert.IsNull(client.Experiments.Variant("exp_color"));

            // Async read: control default, immediately, no throw.
            var variant = client.Experiments.VariantAsync("exp_color").GetAwaiter().GetResult();
            Assert.IsNull(variant);
        }

        [Test]
        public void DisabledClient_FeedbackSubmit_FailsSoft_NoThrow()
        {
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });

            var responses = new[] { new Playloop.Feedback.FeedbackResponseInput("q1", "5") };
            Playloop.Feedback.SubmitFeedbackResult result = null!;
            Assert.DoesNotThrow(() =>
                result = client.Feedback
                    .SubmitAsync("form_1", "sess_1", responses)
                    .GetAwaiter().GetResult());
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Ok);
        }

        [Test]
        public void DisabledClient_SessionIngest_FailsSoft_NoThrow()
        {
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });

            // Fail-soft: returns a null Session rather than throwing.
            Playloop.Sessions.Session session = null!;
            Assert.DoesNotThrow(() =>
                session = client.Sessions
                    .IngestAsync(new byte[] { 1, 2, 3 }, "my-game")
                    .GetAwaiter().GetResult());
            Assert.IsNull(session);
        }

        [Test]
        public void DisabledClient_DisposeIsSafe()
        {
            var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });
            Assert.DoesNotThrow(() => client.Dispose());
            Assert.DoesNotThrow(() => client.Dispose());
        }

        [Test]
        public void DisabledClient_CallerBugsStillThrow()
        {
            // Never-raise ≠ swallow caller bugs. Genuine programmer errors at a
            // real call site still throw, even on a disabled client.
            using var client = new PlayloopClient(new PlayloopOptions { ApiKey = "" });
            Assert.Throws<ArgumentException>(() => client.Telemetry.Track(""));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Feedback.SubmitAsync("", "sess", new[]
                {
                    new Playloop.Feedback.FeedbackResponseInput("q1", "5"),
                }));
        }

        [Test]
        public void Constructor_ExposesNamespacedApis()
        {
            using var client = NewClient(new MockHttpHandler());
            Assert.IsNotNull(client.Sessions);
            Assert.IsNotNull(client.Telemetry);
            Assert.IsNotNull(client.Discord);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var client = NewClient(new MockHttpHandler());
            client.Dispose();
            Assert.DoesNotThrow(() => client.Dispose());
        }

        [Test]
        public void Dispose_DoesNotDispose_CallerProvidedHandler()
        {
            var handler = new MockHttpHandler();
            var client = NewClient(handler);
            client.Dispose();
            // If the client had Dispose()d the handler, the test would crash
            // accessing it. MockHttpHandler.Dispose() is a no-op so the indirect
            // signal is that no exceptions blow up.
            Assert.IsNotNull(handler.Calls);
        }

        [Test]
        public void Constructor_FiresSessionStartWithIdentityMetadata()
        {
            // session_start is the SDK contract's session-begin anchor.
            // Carries identity + build metadata so the dashboard can render
            // the session detail header without back-deriving from
            // the first Track() event.
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                TelemetryFlushIntervalMs = 60_000,
                RetryAttempts = 1,
                Environment = "staging",
                HeartbeatSec = 0, // disable so periodic timer doesn't muddy
                AutoShutdownOnQuit = false,
            };
            using var client = new PlayloopClient(options);
            var pending = client.Telemetry.SnapshotPending();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("session_start", pending[0].Name);
            var data = pending[0].Data!;
            // gameId / gameSlug are no longer stamped. The ingest key
            // identifies the game server-side.
            Assert.IsFalse(data.ContainsKey("gameId"));
            Assert.IsFalse(data.ContainsKey("gameSlug"));
            Assert.AreEqual("staging", data["environment"]);
            Assert.IsTrue(data.ContainsKey("sdkVersion"));
            StringAssert.StartsWith("playloop-unity@", (string)data["sdkVersion"]);
        }

        [Test]
        public void Constructor_SessionStartFiresBeforeHeartbeats()
        {
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                TelemetryFlushIntervalMs = 60_000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
            };
            using var client = new PlayloopClient(options);
            client.Heartbeat.EmitOnce();
            client.EndSession();

            var pending = client.Telemetry.SnapshotPending();
            Assert.AreEqual("session_start", pending[0].Name);
            // start → beat → end ordering.
            int startIdx = -1, beatIdx = -1, endIdx = -1;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Name == "session_start") startIdx = i;
                else if (pending[i].Name == "session_heartbeat") beatIdx = i;
                else if (pending[i].Name == "session_summary") endIdx = i;
            }
            Assert.AreNotEqual(-1, startIdx);
            Assert.AreNotEqual(-1, beatIdx);
            Assert.AreNotEqual(-1, endIdx);
            Assert.Less(startIdx, beatIdx);
            Assert.Less(beatIdx, endIdx);
        }

        [Test]
        public void Constructor_StampsEngineFingerprintAndIsEditor()
        {
            // #231 + #422: every session carries the cross-engine fingerprint
            // (engine / engineVersion) and the isEditor flag. The engine name is
            // always "unity"; engineVersion is a string (empty under dotnet test,
            // where there is no UnityEngine); isEditor mirrors the host, so it is
            // false under dotnet test and true under the editor's EditMode runner.
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                TelemetryFlushIntervalMs = 60_000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
            };
            using var client = new PlayloopClient(options);
            var data = client.Telemetry.SnapshotPending()[0].Data!;
            Assert.AreEqual("unity", data["engine"]);
            Assert.IsTrue(data.ContainsKey("engineVersion"));
            Assert.IsInstanceOf<string>(data["engineVersion"]);
            Assert.AreEqual(TestHost.IsEditor, data["isEditor"]);
        }

        [Test]
        public void Constructor_DefaultEnvironment_AutoDerivesFromBuild()
        {
            // #432: when Environment is left at the default, the SDK auto-derives
            // it from the build: a development build (the editor, a debug player)
            // stays "dev", a release build is promoted to "production". The
            // dotnet-test host is not a development build; the editor host is.
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                TelemetryFlushIntervalMs = 60_000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                // Environment left at default "dev"
            };
            using var client = new PlayloopClient(options);
            Assert.AreEqual(TestHost.DerivedEnvironment, client.Environment);
            Assert.AreEqual(TestHost.DerivedEnvironment, client.Telemetry.SnapshotPending()[0].Data!["environment"]);
        }

        [Test]
        public void Constructor_ExplicitEnvironment_OverridesAutoDerive()
        {
            // An explicit non-default slug always wins over the auto-derive.
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = new MockHttpHandler(),
                TelemetryFlushIntervalMs = 60_000,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                Environment = "staging",
            };
            using var client = new PlayloopClient(options);
            Assert.AreEqual("staging", client.Environment);
        }

        // Most existing tests assume "1 call → throw" behavior on 5xx, so the
        // shared NewClient helper disables retries by default. Retry-specific
        // tests build their own PlayloopOptions or RetryingHttpHandler.
        //
        // Also drops the constructor-fired `session_start` row from the
        // buffer so existing tests that assert on PendingCount don't have
        // to account for it. Tests that want to see session_start
        // construct PlayloopClient directly (see
        // Constructor_FiresSessionStartWithIdentityMetadata).
        //
        // SendInEditor is on because these tests assert on what reaches the
        // wire. With the default (off) the suppress-in-editor gate drains the
        // buffer and sends nothing whenever the suite runs inside the Unity
        // editor, so every wire assertion would fail there and pass under
        // dotnet test. The gate itself is covered by
        // TelemetryApiTests.FlushAsync_DefaultSendInEditor_FollowsTheBuild.
        internal static PlayloopClient NewClient(MockHttpHandler handler, string apiKey = "pl_ik_test")
        {
            var options = new PlayloopOptions
            {
                ApiKey = apiKey,
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                TelemetryFlushIntervalMs = 50,
                RetryAttempts = 1,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                SendInEditor = true,
            };
            var client = new PlayloopClient(options);
            client.Telemetry.ClearBuffer();
            return client;
        }
    }
}
