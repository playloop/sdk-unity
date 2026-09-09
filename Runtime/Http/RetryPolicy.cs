#nullable enable
using System;

namespace Playloop.Http
{
    /// <summary>
    /// Retry policy for transient HTTP failures. The contract is canonical across
    /// the Unity / Unreal / Godot / Python / TypeScript SDKs.
    /// Don't drift the defaults in one SDK without updating
    /// the others.
    ///
    /// <para>
    /// Used by <see cref="RetryingHttpHandler"/>, which wraps an inner
    /// <see cref="IHttpHandler"/> and re-issues the request on retryable
    /// failures (HTTP 429 / 500 / 502 / 503 / 504 or network errors) with
    /// exponential backoff + ±25% jitter. The <c>Retry-After</c> response
    /// header wins when present.
    /// </para>
    /// </summary>
    public sealed class RetryPolicy
    {
        /// <summary>
        /// Total HTTP attempts including the first. Default 3 (i.e. 2 retries).
        /// Set to 1 to disable retries entirely.
        /// </summary>
        public int Attempts { get; init; } = 3;

        /// <summary>Base delay in milliseconds. Default 500ms.</summary>
        public int BaseMs { get; init; } = 500;

        /// <summary>Max delay cap in milliseconds. Default 5000ms.</summary>
        public int MaxMs { get; init; } = 5000;

        /// <summary>Canonical defaults: 3 attempts, 500ms base, 5000ms cap.</summary>
        public static readonly RetryPolicy Default = new RetryPolicy();

        /// <summary>One-attempt policy: disables retries.</summary>
        public static readonly RetryPolicy Disabled = new RetryPolicy { Attempts = 1 };

        /// <summary>True iff the status code should trigger a retry.</summary>
        internal static bool IsRetryableStatus(int status) =>
            status == 429 || status == 500 || status == 502 || status == 503 || status == 504;

        /// <summary>
        /// Pure delay calculation. Both <paramref name="nowMs"/> and
        /// <paramref name="randomUnit"/> are injected so tests can pin the
        /// exact delay deterministically.
        /// </summary>
        /// <param name="attemptJustFinished">1-based count of the attempt
        /// that just failed (so the first retry passes 1).</param>
        /// <param name="retryAfterHeader">Value of the server's
        /// <c>Retry-After</c> header, if any. Wins when parseable; otherwise
        /// the exponential formula applies.</param>
        /// <param name="nowMs">Current unix time in milliseconds (used to
        /// compute the delta against an HTTP-date <c>Retry-After</c>).</param>
        /// <param name="randomUnit">Random value in [0, 1) used for the ±25%
        /// jitter multiplier.</param>
        internal int ComputeDelayMs(int attemptJustFinished, string? retryAfterHeader, long nowMs, double randomUnit)
        {
            // Retry-After wins when present. Cap at MaxMs * 2 so a hostile or
            // misconfigured server can't pin the SDK on a 10-minute sleep.
            if (!string.IsNullOrEmpty(retryAfterHeader))
            {
                if (int.TryParse(retryAfterHeader, out var seconds) && seconds >= 0)
                {
                    return Math.Min(MaxMs * 2, seconds * 1000);
                }
                if (DateTimeOffset.TryParse(retryAfterHeader, out var date))
                {
                    var diffMs = (int)Math.Max(0, date.ToUnixTimeMilliseconds() - nowMs);
                    return Math.Min(MaxMs * 2, diffMs);
                }
                // Unparseable Retry-After value falls through to exponential.
            }

            // Exponential with ±25% multiplicative jitter.
            //   raw      = min(MaxMs, BaseMs * 2^(attempt-1))
            //   jittered = raw * (0.75 + randomUnit * 0.5)
            var safeAttempt = Math.Max(1, attemptJustFinished);
            var raw = Math.Min(MaxMs, BaseMs * (int)Math.Pow(2, safeAttempt - 1));
            var factor = 0.75 + randomUnit * 0.5;
            return (int)(raw * factor);
        }
    }
}

// netstandard2.1 doesn't define System.Runtime.CompilerServices.IsExternalInit,
// which the C# compiler requires to emit `init` setters. Shim it internally so
// the property syntax above compiles without taking a dependency on a polyfill
// package. Visible only inside this assembly.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
