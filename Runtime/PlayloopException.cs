#nullable enable
using System;

namespace Playloop
{
    /// <summary>
    /// Thrown when the Playloop API returns a non-2xx response or a transport
    /// error occurs. Carries the HTTP status code (0 for transport failures),
    /// the parsed response body when available, and the request id if present.
    /// </summary>
    public sealed class PlayloopException : Exception
    {
        public int Status { get; }
        public object? Body { get; }
        public string? RequestId { get; }

        public PlayloopException(string message, int status, object? body = null, string? requestId = null)
            : base(message)
        {
            Status = status;
            Body = body;
            RequestId = requestId;
        }
    }
}
