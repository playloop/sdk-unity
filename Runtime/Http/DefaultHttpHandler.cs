#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
// Alias to avoid collision with our internal Playloop.Http.HttpClient.
using SystemHttpClient = System.Net.Http.HttpClient;
using System.Threading;
using System.Threading.Tasks;

namespace Playloop.Http
{
    /// <summary>
    /// System.Net.Http-based handler. Used outside Unity (editor tooling, server
    /// jobs, the standalone `dotnet test` build). Works on every Unity target
    /// EXCEPT WebGL. For WebGL use <see cref="UnityWebRequestHandler"/>.
    /// </summary>
    public sealed class DefaultHttpHandler : IHttpHandler
    {
        private static readonly SystemHttpClient _shared = new SystemHttpClient();

        private readonly SystemHttpClient _http;
        private readonly bool _ownsClient;

        public DefaultHttpHandler(int timeoutSeconds = 30)
        {
            // We deliberately don't dispose _shared. HttpClient is documented
            // as long-lived and disposing it kills connection pooling. If the
            // caller wants a custom timeout, hand them their own instance.
            if (timeoutSeconds == 30)
            {
                _http = _shared;
                _ownsClient = false;
            }
            else
            {
                _http = new SystemHttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                _ownsClient = true;
            }
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _http.Dispose();
            }
        }

        public async Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
        {
            using var msg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

            foreach (var kv in request.Headers)
            {
                msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }

            if (request.JsonBody != null)
            {
                msg.Content = new ByteArrayContent(request.JsonBody);
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            else if (request.FormFields != null || request.Files != null)
            {
                var multipart = new MultipartFormDataContent();
                if (request.FormFields != null)
                {
                    foreach (var kv in request.FormFields)
                    {
                        multipart.Add(new StringContent(kv.Value), kv.Key);
                    }
                }
                if (request.Files != null)
                {
                    foreach (var file in request.Files)
                    {
                        var content = new ByteArrayContent(file.Content);
                        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                        multipart.Add(content, file.FieldName, file.FileName);
                    }
                }
                msg.Content = multipart;
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlayloopException(
                    "Network request failed: " + ex.Message,
                    status: 0,
                    body: null);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var headers = new Dictionary<string, string>();
            foreach (var h in response.Headers)
            {
                headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            }
            foreach (var h in response.Content.Headers)
            {
                headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            }

            response.Dispose();
            return new HttpResponseData(
                (int)response.StatusCode,
                body,
                headers);
        }
    }
}
