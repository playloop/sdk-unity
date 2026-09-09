#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Playloop.Http
{
    /// <summary>
    /// Pluggable HTTP transport. Production code picks
    /// <see cref="UnityWebRequestHandler"/> inside Unity and
    /// <see cref="DefaultHttpHandler"/> elsewhere. Tests inject a mock.
    /// </summary>
    public interface IHttpHandler : IDisposable
    {
        Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken);
    }
}
