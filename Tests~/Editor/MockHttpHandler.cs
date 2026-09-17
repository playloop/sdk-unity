#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playloop.Http;

namespace Playloop.Tests
{
    /// <summary>
    /// Records every request and returns a response chosen by a user-supplied
    /// <see cref="Responder"/>. The default responder returns 200 with body "[]".
    ///
    /// Thread-safe. The constructor-prefetch in PlayloopClient races with
    /// explicit RefreshAsync calls from tests, so reads + writes to the
    /// call list must serialize. Reads return a snapshot so a caller's
    /// LINQ enumeration cannot race with an in-flight Add.
    /// </summary>
    public sealed class MockHttpHandler : IHttpHandler
    {
        private readonly List<HttpRequestSpec> _calls = new List<HttpRequestSpec>();
        private readonly object _callsLock = new object();

        /// <summary>
        /// Snapshot of every recorded FEATURE request. Each read returns a new
        /// list so callers can safely enumerate it even while another thread is
        /// still capturing requests through <see cref="SendAsync"/>.
        ///
        /// The SDK-internal game-resolve probe (<c>GET /api/telemetry/resolve</c>)
        /// is excluded here: it's plumbing the client fires at construction to
        /// resolve the game from the ingest key, and it races with whatever the
        /// test is actually asserting on. Tests that care about the resolve
        /// inspect behavior (the resolved values), not the raw call list. Use
        /// <see cref="AllCalls"/> to see it.
        /// </summary>
        public IReadOnlyList<HttpRequestSpec> Calls
        {
            get
            {
                lock (_callsLock)
                    return _calls.FindAll(c => !c.Url.Contains("/api/telemetry/resolve")).ToArray();
            }
        }

        /// <summary>Every recorded request, including the internal resolve probe.</summary>
        public IReadOnlyList<HttpRequestSpec> AllCalls
        {
            get { lock (_callsLock) return _calls.ToArray(); }
        }

        public Func<HttpRequestSpec, HttpResponseData> Responder { get; set; }
            = _ => new HttpResponseData(200, "[]", new Dictionary<string, string> { ["content-type"] = "application/json" });

        public Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
        {
            lock (_callsLock) _calls.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Responder(request));
        }

        public void Dispose() { /* nothing */ }

        // ─────────────────────────── convenience constructors ───────────────────────────

        public static MockHttpHandler ReturnsJson(string body, int status = 200, Dictionary<string, string>? headers = null) =>
            new MockHttpHandler
            {
                Responder = _ => new HttpResponseData(
                    status,
                    body,
                    headers ?? new Dictionary<string, string> { ["content-type"] = "application/json" }),
            };

        public static MockHttpHandler ReturnsStatus(int status, string body = "") =>
            new MockHttpHandler
            {
                Responder = _ => new HttpResponseData(status, body, new Dictionary<string, string>()),
            };
    }
}
