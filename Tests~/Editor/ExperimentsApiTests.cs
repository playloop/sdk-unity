#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Playloop;
using Playloop.Http;

namespace Playloop.Tests
{
    /// <summary>
    /// Experiments API lifecycle: covers the four experiment behaviors plus the
    /// prefetch option and the synchronous <c>Variant()</c> fast-path. Mirrors
    /// the TypeScript SDK's experiments tests.
    /// </summary>
    [TestFixture]
    public class ExperimentsApiTests
    {
        /// <summary>
        /// Counts experiment-endpoint fetches and serves a scriptable map. The
        /// responder returns whatever <see cref="Map"/> currently holds (or a
        /// failure status when <see cref="FailNext"/> is set), so a test can
        /// flip server state between fetches.
        /// </summary>
        private sealed class ExperimentHandler : IHttpHandler
        {
            private int _fetches;
            private readonly object _lock = new object();

            public Dictionary<string, string> Map { get; set; } = new Dictionary<string, string>();
            // Optional parallel config map: { experimentId: { key: primitive } }.
            // When non-null it is serialized as the response's `config` field.
            public JObject? Config { get; set; }
            public bool FailNext { get; set; }

            // Recorded bodies of every non-experiment telemetry POST, parsed as
            // JSON, so a test can assert on the session-create payload shape.
            private readonly List<JObject> _telemetryPosts = new List<JObject>();
            public IReadOnlyList<JObject> TelemetryPosts { get { lock (_lock) return _telemetryPosts.ToArray(); } }

            public int Fetches { get { lock (_lock) return _fetches; } }

            // URL of the most recent experiments fetch, so a test can assert
            // the query string (no gameId param anymore).
            private string _lastExperimentsUrl = "";
            public string LastExperimentsUrl { get { lock (_lock) return _lastExperimentsUrl; } }

            public Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken ct)
            {
                if (request.Url.Contains("/api/telemetry/experiments"))
                {
                    lock (_lock) { _fetches++; _lastExperimentsUrl = request.Url; }
                    if (FailNext)
                    {
                        return Task.FromResult(new HttpResponseData(
                            500, "{\"error\":\"boom\"}",
                            new Dictionary<string, string>()));
                    }
                    var experiments = new JObject();
                    foreach (var kvp in Map) experiments[kvp.Key] = kvp.Value;
                    var bodyObj = new JObject { ["ok"] = true, ["experiments"] = experiments };
                    if (Config != null) bodyObj["config"] = Config;
                    var body = bodyObj.ToString();
                    return Task.FromResult(new HttpResponseData(
                        200, body,
                        new Dictionary<string, string> { ["content-type"] = "application/json" }));
                }

                // Any other endpoint (telemetry POST etc.): record + succeed.
                if (request.JsonBody != null)
                {
                    var json = System.Text.Encoding.UTF8.GetString(request.JsonBody);
                    try { lock (_lock) _telemetryPosts.Add(JObject.Parse(json)); }
                    catch { /* non-object body: ignore */ }
                }
                return Task.FromResult(new HttpResponseData(
                    200, new JObject { ["ok"] = true, ["sessionId"] = "sess_test" }.ToString(),
                    new Dictionary<string, string> { ["content-type"] = "application/json" }));
            }

            public void Dispose() { }
        }

        private static PlayloopClient NewClient(ExperimentHandler handler, bool prefetch = false)
        {
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                DeviceId = "device_xyz",
                PrefetchExperiments = prefetch,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                RetryAttempts = 1,
            };
            return new PlayloopClient(options);
        }

        [Test]
        public async Task LazyFetch_FirstVariantTriggersExactlyOneFetch()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_tutorial"] = "v2" },
            };
            using var client = NewClient(handler);

            // No fetch should happen at construction (lazy).
            Assert.AreEqual(0, handler.Fetches, "construction must not fetch when prefetch is off");

            var variant = await client.Experiments.VariantAsync("exp_tutorial");
            Assert.AreEqual("v2", variant);
            Assert.AreEqual(1, handler.Fetches, "first VariantAsync triggers exactly one fetch");

            // A second known-id lookup is served from cache: no new fetch.
            var again = await client.Experiments.VariantAsync("exp_tutorial");
            Assert.AreEqual("v2", again);
            Assert.AreEqual(1, handler.Fetches, "cached lookups don't re-fetch");
        }

        [Test]
        public async Task ExperimentsFetch_SendsDeviceIdOnly_NoGameId()
        {
            // The ingest key identifies the game server-side, so the SDK no
            // longer sends a gameId on the experiments query.
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "v1" },
            };
            using var client = NewClient(handler);

            await client.Experiments.VariantAsync("exp_a");

            StringAssert.Contains("deviceId=device_xyz", handler.LastExperimentsUrl);
            StringAssert.DoesNotContain("gameId", handler.LastExperimentsUrl);
        }

        [Test]
        public async Task SessionScopedCache_StableAcrossServerChange()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "control" },
            };
            using var client = NewClient(handler);

            Assert.AreEqual("control", await client.Experiments.VariantAsync("exp_a"));

            // Server flips the allocation mid-session. The cached answer must
            // stand. Variant stability beats freshness.
            handler.Map = new Dictionary<string, string> { ["exp_a"] = "treatment" };
            Assert.AreEqual("control", await client.Experiments.VariantAsync("exp_a"));
            Assert.AreEqual(1, handler.Fetches, "cache holds for the session: no implicit re-fetch");

            // Explicit RefreshAsync picks up the new server state.
            await client.Experiments.RefreshAsync();
            Assert.AreEqual("treatment", await client.Experiments.VariantAsync("exp_a"));
        }

        [Test]
        public async Task UnknownId_RefetchesOnceThenCachesNegative()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_known"] = "v1" },
            };
            using var client = NewClient(handler);

            // Warm the cache via a known id (fetch #1).
            await client.Experiments.VariantAsync("exp_known");
            Assert.AreEqual(1, handler.Fetches);

            // Unknown id → exactly one refetch (fetch #2), still unknown → null.
            Assert.IsNull(await client.Experiments.VariantAsync("exp_missing"));
            Assert.AreEqual(2, handler.Fetches, "unknown id refetches once");

            // Asking again for the same unknown id uses the negative cache.
            // No further fetch.
            Assert.IsNull(await client.Experiments.VariantAsync("exp_missing"));
            Assert.AreEqual(2, handler.Fetches, "negative answer is cached: no retry loop");
        }

        [Test]
        public async Task UnknownId_BecomesKnownAfterRefetch()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_known"] = "v1" },
            };
            using var client = NewClient(handler);

            await client.Experiments.VariantAsync("exp_known");

            // Server starts a new experiment; the refetch-once path should
            // surface it on the first ask for the previously-unknown id.
            handler.Map = new Dictionary<string, string>
            {
                ["exp_known"] = "v1",
                ["exp_new"] = "beta",
            };
            Assert.AreEqual("beta", await client.Experiments.VariantAsync("exp_new"));
        }

        [Test]
        public async Task NetworkFailure_FirstFetchReturnsNull_ThenLastGoodWins()
        {
            var handler = new ExperimentHandler { FailNext = true };
            using var client = NewClient(handler);

            // First fetch fails with no prior cache → null, never throws.
            Assert.IsNull(await client.Experiments.VariantAsync("exp_a"));

            // Recover: server is healthy now. Refresh populates the cache.
            handler.FailNext = false;
            handler.Map = new Dictionary<string, string> { ["exp_a"] = "v3" };
            await client.Experiments.RefreshAsync();
            Assert.AreEqual("v3", await client.Experiments.VariantAsync("exp_a"));

            // Server goes down again: last-good cache stands.
            handler.FailNext = true;
            await client.Experiments.RefreshAsync();
            Assert.AreEqual("v3", await client.Experiments.VariantAsync("exp_a"),
                "a failed refresh keeps the last-good answer");
        }

        [Test]
        public async Task SyncVariant_ReturnsCachedOrNull()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "v9" },
            };
            using var client = NewClient(handler);

            // Cache is cold: sync read is a miss and does NOT fetch.
            Assert.IsNull(client.Experiments.Variant("exp_a"));
            Assert.AreEqual(0, handler.Fetches, "sync Variant() never fetches");

            // Warm the cache, then the sync read hits.
            await client.Experiments.VariantAsync("exp_a");
            Assert.AreEqual("v9", client.Experiments.Variant("exp_a"));
            Assert.IsNull(client.Experiments.Variant("exp_unknown"));
        }

        [Test]
        public async Task PrefetchExperiments_WarmsCacheWithoutVariantCall()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "v4" },
            };
            using var client = NewClient(handler, prefetch: true);

            // The prefetch is fire-and-forget: poll briefly until it settles.
            for (var i = 0; i < 50 && handler.Fetches == 0; i++)
            {
                await Task.Delay(10);
            }

            Assert.GreaterOrEqual(handler.Fetches, 1, "prefetch fired at construction");
            // Sync read is warm without ever awaiting VariantAsync.
            Assert.AreEqual("v4", client.Experiments.Variant("exp_a"));
        }

        [Test]
        public async Task ExperimentTags_RideOnSessionCreateFlush()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "treatment" },
            };
            using var client = NewClient(handler);

            // Resolve a variant so the cache is populated before the flush.
            await client.Experiments.VariantAsync("exp_a");
            client.Telemetry.Track("level_complete");
            await client.Telemetry.FlushAsync();

            // The first telemetry POST is the session-create flush. It must
            // carry the resolved assignment as experimentTags.
            var createFlush = handler.TelemetryPosts.FirstOrDefault();
            Assert.IsNotNull(createFlush, "a telemetry POST should have been sent");
            var tags = createFlush!["experimentTags"] as JObject;
            Assert.IsNotNull(tags, "session-create flush carries experimentTags");
            Assert.AreEqual("treatment", tags!["exp_a"]?.ToString());
        }

        [Test]
        public async Task GetConfig_ExposesAssignedVariantPrimitives()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "treatment" },
                Config = new JObject
                {
                    ["exp_a"] = new JObject
                    {
                        ["enemy_damage"] = 15,
                        ["show_hints"] = true,
                        ["cta_label"] = "Bravo",
                    },
                },
            };
            using var client = NewClient(handler);

            var cfg = await client.Experiments.GetConfigAsync("exp_a");
            Assert.IsNotNull(cfg);
            Assert.AreEqual(15, cfg!.GetInt("enemy_damage", 0));
            Assert.AreEqual(15f, cfg.GetFloat("enemy_damage", 0f));
            Assert.IsTrue(cfg.GetBool("show_hints", false));
            Assert.AreEqual("Bravo", cfg.GetString("cta_label", "fallback"));
            // Missing keys fall back.
            Assert.AreEqual(99, cfg.GetInt("nope", 99));
            Assert.AreEqual("dflt", cfg.GetString("nope", "dflt"));
        }

        [Test]
        public async Task GetConfig_ReturnsNullWhenVariantHasNoConfig()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "control" },
                Config = new JObject { ["exp_b"] = new JObject { ["x"] = 1 } },
            };
            using var client = NewClient(handler);
            Assert.IsNull(await client.Experiments.GetConfigAsync("exp_a"));
        }

        [Test]
        public async Task GetConfig_ReturnsNullWhenResponseOmitsConfig()
        {
            // Older server: no `config` field at all. Backward-compatible.
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "control" },
            };
            using var client = NewClient(handler);
            Assert.AreEqual("control", await client.Experiments.VariantAsync("exp_a"));
            Assert.IsNull(await client.Experiments.GetConfigAsync("exp_a"));
        }

        [Test]
        public async Task GetConfig_DropsNonPrimitiveValues()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "treatment" },
                Config = new JObject
                {
                    ["exp_a"] = new JObject
                    {
                        ["ok"] = "yes",
                        ["nested"] = new JObject { ["a"] = 1 },
                        ["list"] = new JArray { 1, 2 },
                        ["keep"] = 3,
                    },
                },
            };
            using var client = NewClient(handler);

            var cfg = await client.Experiments.GetConfigAsync("exp_a");
            Assert.IsNotNull(cfg);
            // Only the two primitive keys survive.
            Assert.AreEqual(2, cfg!.Count);
            Assert.AreEqual("yes", cfg.GetString("ok", null));
            Assert.AreEqual(3, cfg.GetInt("keep", 0));
            Assert.IsFalse(cfg.ContainsKey("nested"));
            Assert.IsFalse(cfg.ContainsKey("list"));
        }

        [Test]
        public async Task GetConfig_SharesLazyFetchAndCacheWithVariant()
        {
            var handler = new ExperimentHandler
            {
                Map = new Dictionary<string, string> { ["exp_a"] = "treatment" },
                Config = new JObject { ["exp_a"] = new JObject { ["speed"] = 2 } },
            };
            using var client = NewClient(handler);

            Assert.AreEqual("treatment", await client.Experiments.VariantAsync("exp_a"));
            var cfg = await client.Experiments.GetConfigAsync("exp_a");
            Assert.AreEqual(2, cfg!.GetInt("speed", 0));
            // One fetch total: GetConfig reused the cache VariantAsync warmed.
            Assert.AreEqual(1, handler.Fetches);
        }
    }
}
