#nullable enable
using System.Collections.Generic;

namespace Playloop.Http
{
    /// <summary>
    /// Transport-agnostic response. <c>StatusCode</c> is 0 for transport failures
    /// that didn't make it to the server.
    /// </summary>
    public sealed class HttpResponseData
    {
        public int StatusCode { get; }
        public string Body { get; }
        public Dictionary<string, string> Headers { get; }

        public HttpResponseData(int statusCode, string body, Dictionary<string, string>? headers = null)
        {
            StatusCode = statusCode;
            Body = body ?? "";
            Headers = headers ?? new Dictionary<string, string>();
        }
    }
}
