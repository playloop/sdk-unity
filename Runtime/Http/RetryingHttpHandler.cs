#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// Tests need access to the deterministic constructor (delay + random + clock
// seams) so retry timing can be asserted bit-for-bit. The test assembly is the
// only consumer of that overload.
[assembly: InternalsVisibleTo("Playloop.Tests")]

namespace Playloop.Http
{
    /// <summary>
    /// Delay seam so tests can record the requested sleep without actually
    /// waiting. Production binds to <see cref="Task.Delay(int, CancellationToken)"/>.
    /// </summary>
    public interface IDelayProvider
    {
        Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
    }

    internal sealed class TaskDelayProvider : IDelayProvider
    {
        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
            milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, cancellationToken);
    }

    /// <summary>
    /// <see cref="IHttpHandler"/> wrapper that adds retry/backoff per the
    /// canonical <see cref="RetryPolicy"/> contract. Wrap any inner handler
    /// (UnityWebRequest, System.Net.Http, mock) and every call site picks up
    /// retries for free: telemetry flush, session ingest, webhook send, etc.
    ///
    /// <para>
    /// Retries on HTTP 429 / 500 / 502 / 503 / 504 and on transport failures
    /// (network errors thrown by the inner handler as
    /// <see cref="PlayloopException"/> with <c>Status == 0</c>, or Unity's
    /// <c>ConnectionError</c> / <c>DataProcessingError</c> which the
    /// <c>UnityWebRequestHandler</c> already maps to that exception shape).
    /// </para>
    /// </summary>
    public sealed class RetryingHttpHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly bool _ownsInner;
        private readonly RetryPolicy _policy;
        private readonly IDelayProvider _delay;
        private readonly Func<double> _random;
        private readonly Func<long> _nowMs;

        /// <summary>
        /// Production constructor. Defaults to <see cref="Task.Delay(int, CancellationToken)"/>
        /// for sleeps, <see cref="System.Random"/> for jitter, and
        /// <see cref="DateTimeOffset.UtcNow"/> for the clock.
        /// </summary>
        public RetryingHttpHandler(IHttpHandler inner, RetryPolicy policy, bool disposeInner = true)
            : this(inner, policy, new TaskDelayProvider(), null, null, disposeInner) { }

        /// <summary>
        /// Test-friendly constructor. Inject a deterministic clock + random +
        /// delay provider so retry timing can be asserted byte-for-byte.
        /// </summary>
        internal RetryingHttpHandler(
            IHttpHandler inner,
            RetryPolicy policy,
            IDelayProvider delay,
            Func<double>? random,
            Func<long>? nowMs,
            bool disposeInner = true)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _delay = delay ?? new TaskDelayProvider();
            _ownsInner = disposeInner;

            // Default random source picked once per handler instance so two
            // concurrent retrying clients don't lockstep their jitter.
            var rng = new Random();
            _random = random ?? (() =>
            {
                lock (rng) { return rng.NextDouble(); }
            });
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public void Dispose()
        {
            if (_ownsInner)
            {
                _inner.Dispose();
            }
        }

        public async Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
        {
            // attemptsTotal is the cap on calls to the inner handler. 1 means
            // "make the request, never retry." Anything below 1 we clamp up
            // so the request is at least attempted once.
            var attemptsTotal = Math.Max(1, _policy.Attempts);

            PlayloopException? lastTransportError = null;
            HttpResponseData? lastResponse = null;

            for (var attempt = 1; attempt <= attemptsTotal; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isRetryable;
                string? retryAfter = null;

                try
                {
                    lastResponse = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(Playloop.PlAwait.Continue);
                    lastTransportError = null;

                    if (lastResponse.StatusCode >= 200 && lastResponse.StatusCode < 300)
                    {
                        return lastResponse; // success: short-circuit.
                    }

                    if (!RetryPolicy.IsRetryableStatus(lastResponse.StatusCode))
                    {
                        // Terminal error (4xx that isn't 429, plus 501/505,
                        // any other 5xx not in the retry list). Return it so
                        // the wrapper layer maps it to a PlayloopException.
                        return lastResponse;
                    }

                    isRetryable = true;
                    lastResponse.Headers.TryGetValue("retry-after", out retryAfter);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PlayloopException pex) when (pex.Status == 0)
                {
                    // Status == 0 is the convention both DefaultHttpHandler
                    // and UnityWebRequestHandler use for transport failures
                    // (network unreachable, DNS, TLS, connection refused,
                    // UnityWebRequest.Result.ConnectionError, etc.).
                    lastTransportError = pex;
                    isRetryable = true;
                }

                if (!isRetryable || attempt >= attemptsTotal)
                {
                    break;
                }

                var delayMs = _policy.ComputeDelayMs(attempt, retryAfter, _nowMs(), _random());
                if (delayMs > 0)
                {
                    await _delay.DelayAsync(delayMs, cancellationToken).ConfigureAwait(Playloop.PlAwait.Continue);
                }
            }

            // Exhausted attempts. Surface the last failure preserving status/body.
            if (lastTransportError != null)
            {
                throw lastTransportError;
            }
            // lastResponse must be non-null here. The loop always either
            // succeeded (returned), hit a non-retryable status (returned), or
            // caught a transport error and bound lastTransportError. The only
            // remaining path that breaks the loop is "ran out of retries with
            // a retryable status," so lastResponse is set.
            return lastResponse!;
        }
    }
}
