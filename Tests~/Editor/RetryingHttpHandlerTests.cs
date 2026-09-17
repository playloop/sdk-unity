#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playloop.Http;

namespace Playloop.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="RetryingHttpHandler"/>. A stub
    /// <see cref="IHttpHandler"/> returns canned responses (or throws), a
    /// capturing <see cref="IDelayProvider"/> notes every sleep, and the
    /// random + clock seams are deterministic, so each scenario asserts both
    /// "correct number of calls" AND "correct delay between them" without
    /// real wall-clock waits.
    /// </summary>
    [TestFixture]
    public class RetryingHttpHandlerTests
    {
        // ──────────────────────────── test doubles ────────────────────────────

        private sealed class StubHandler : IHttpHandler
        {
            private readonly Queue<Func<HttpResponseData>> _script;
            public int Calls { get; private set; }
            public List<HttpRequestSpec> Requests { get; } = new List<HttpRequestSpec>();

            public StubHandler(params Func<HttpResponseData>[] script)
            {
                _script = new Queue<Func<HttpResponseData>>(script);
            }

            public Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken ct)
            {
                Calls++;
                Requests.Add(request);
                if (_script.Count == 0)
                {
                    throw new InvalidOperationException("StubHandler ran out of scripted responses.");
                }
                var next = _script.Dequeue();
                try
                {
                    return Task.FromResult(next());
                }
                catch (Exception ex)
                {
                    return Task.FromException<HttpResponseData>(ex);
                }
            }

            public void Dispose() { /* nothing */ }

            // Convenience factories so the scenarios read like a script.
            public static Func<HttpResponseData> Status(int code, string body = "", Dictionary<string, string>? headers = null) =>
                () => new HttpResponseData(code, body, headers ?? new Dictionary<string, string>());

            public static Func<HttpResponseData> NetworkError(string message = "connection refused") =>
                () => throw new PlayloopException("Network request failed: " + message, status: 0);
        }

        private sealed class RecordingDelay : IDelayProvider
        {
            public List<int> Delays { get; } = new List<int>();
            public Task DelayAsync(int milliseconds, CancellationToken ct)
            {
                Delays.Add(milliseconds);
                return Task.CompletedTask;
            }
        }

        private static HttpRequestSpec NewRequest() =>
            new HttpRequestSpec("GET", "https://api.test/x", new Dictionary<string, string>());

        private static RetryingHttpHandler Build(
            IHttpHandler inner,
            RetryPolicy policy,
            RecordingDelay delay,
            double randomUnit = 0.5,
            long nowMs = 0)
        {
            return new RetryingHttpHandler(
                inner,
                policy,
                delay,
                random: () => randomUnit,
                nowMs: () => nowMs,
                disposeInner: false);
        }

        // ──────────────────────────── scenarios ───────────────────────────────

        // 1. 2xx first try → no retry, 1 call.
        [Test]
        public async Task Success_FirstTry_NoRetry()
        {
            var stub = new StubHandler(StubHandler.Status(200, "{}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(200, resp.StatusCode);
            Assert.AreEqual(1, stub.Calls);
            Assert.AreEqual(0, delay.Delays.Count);
        }

        // 2. 429 then 200 → 2 calls.
        [Test]
        public async Task Status429_Then200_RetriesOnce()
        {
            var stub = new StubHandler(
                StubHandler.Status(429),
                StubHandler.Status(200, "{}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(200, resp.StatusCode);
            Assert.AreEqual(2, stub.Calls);
            // One backoff between attempts 1 and 2. randomUnit=0.5 → factor=1
            // → raw = 500 * 2^0 = 500ms.
            Assert.AreEqual(1, delay.Delays.Count);
            Assert.AreEqual(500, delay.Delays[0]);
        }

        // 3. 503 then 503 then 200 → 3 calls.
        [Test]
        public async Task Status503_503_Then200_RetriesTwice()
        {
            var stub = new StubHandler(
                StubHandler.Status(503),
                StubHandler.Status(503),
                StubHandler.Status(200, "{}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(200, resp.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            // Two backoffs: 500ms after attempt 1, 1000ms after attempt 2.
            Assert.AreEqual(2, delay.Delays.Count);
            Assert.AreEqual(500, delay.Delays[0]);
            Assert.AreEqual(1000, delay.Delays[1]);
        }

        // 4. 500 × 3 → 3 calls, returns 500 response (HttpClient wrapper maps to PlayloopException).
        [Test]
        public async Task Status500_Exhausted_ReturnsLastResponse()
        {
            var stub = new StubHandler(
                StubHandler.Status(500, "{\"error\":\"oof\"}"),
                StubHandler.Status(500, "{\"error\":\"oof\"}"),
                StubHandler.Status(500, "{\"error\":\"oof\"}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(500, resp.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            Assert.AreEqual(2, delay.Delays.Count);
        }

        // 4b. The PlayloopClient + HttpClient pipeline should surface that 500
        // as a PlayloopException with the body preserved.
        [Test]
        public void Status500_Exhausted_Throws_PlayloopExceptionFromHttpClient()
        {
            var handler = new MockHttpHandler
            {
                Responder = _ => new HttpResponseData(
                    500,
                    "{\"error\":\"server down\"}",
                    new Dictionary<string, string>
                    {
                        ["content-type"] = "application/json",
                    }),
            };
            var options = new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test.playloop.gg",
                Http = handler,
                // Override delay/random isn't reachable through PlayloopClient,
                // so use Disabled-with-1 to keep the integration test fast.
                // Path coverage for "exhausted" is in scenario 4 above.
                RetryAttempts = 1,
            };
            using var client = new PlayloopClient(options);

            var ex = Assert.ThrowsAsync<PlayloopException>(async () => await client.Sessions.ListAsync())!;
            Assert.AreEqual(500, ex.Status);
            StringAssert.Contains("server down", ex.Message);
        }

        // 5. 400 → 1 call, throws (no retry).
        [Test]
        public async Task Status400_DoesNotRetry()
        {
            var stub = new StubHandler(StubHandler.Status(400, "{\"error\":\"bad\"}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(400, resp.StatusCode);
            Assert.AreEqual(1, stub.Calls);
            Assert.AreEqual(0, delay.Delays.Count);
        }

        // 5b. Cover the other "do not retry" 5xx + non-retryable 4xx codes.
        [TestCase(401)]
        [TestCase(403)]
        [TestCase(404)]
        [TestCase(409)]
        [TestCase(422)]
        [TestCase(501)]
        [TestCase(505)]
        public async Task NonRetryableStatuses_NoRetry(int status)
        {
            var stub = new StubHandler(StubHandler.Status(status));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(status, resp.StatusCode);
            Assert.AreEqual(1, stub.Calls);
            Assert.AreEqual(0, delay.Delays.Count);
        }

        // 6. ConnectionError then 200 → 2 calls (network-error retry).
        [Test]
        public async Task NetworkError_Then200_Retries()
        {
            var stub = new StubHandler(
                StubHandler.NetworkError(),
                StubHandler.Status(200, "{}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(200, resp.StatusCode);
            Assert.AreEqual(2, stub.Calls);
            Assert.AreEqual(1, delay.Delays.Count);
            Assert.AreEqual(500, delay.Delays[0]);
        }

        // 6b. Exhausted network errors surface the last PlayloopException.
        [Test]
        public void NetworkError_Exhausted_ThrowsLast()
        {
            var stub = new StubHandler(
                StubHandler.NetworkError("dns fail"),
                StubHandler.NetworkError("dns fail"),
                StubHandler.NetworkError("dns fail"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var ex = Assert.ThrowsAsync<PlayloopException>(async () =>
                await h.SendAsync(NewRequest(), CancellationToken.None))!;

            Assert.AreEqual(0, ex.Status); // transport-failure convention
            StringAssert.Contains("dns fail", ex.Message);
            Assert.AreEqual(3, stub.Calls);
            Assert.AreEqual(2, delay.Delays.Count);
        }

        // 7. 429 with Retry-After: 1 → waits 1000ms before retry.
        [Test]
        public async Task Status429_WithRetryAfter1Second_WaitsExactly1000ms()
        {
            var stub = new StubHandler(
                StubHandler.Status(429, "", new Dictionary<string, string>
                {
                    ["retry-after"] = "1",
                }),
                StubHandler.Status(200, "{}"));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(200, resp.StatusCode);
            Assert.AreEqual(2, stub.Calls);
            Assert.AreEqual(1, delay.Delays.Count);
            Assert.AreEqual(1000, delay.Delays[0]);
        }

        // Extra: RetryAttempts=1 disables retries entirely.
        [Test]
        public async Task RetryAttempts1_DisablesRetries_On500()
        {
            var stub = new StubHandler(StubHandler.Status(500));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Disabled, delay);

            var resp = await h.SendAsync(NewRequest(), CancellationToken.None);

            Assert.AreEqual(500, resp.StatusCode);
            Assert.AreEqual(1, stub.Calls);
            Assert.AreEqual(0, delay.Delays.Count);
        }

        // Extra: cancellation is honored without sleeping.
        [Test]
        public void Cancellation_DuringSendAsync_Propagates()
        {
            var stub = new StubHandler(StubHandler.Status(500), StubHandler.Status(200));
            var delay = new RecordingDelay();
            using var h = Build(stub, RetryPolicy.Default, delay);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await h.SendAsync(NewRequest(), cts.Token));
        }
    }
}
