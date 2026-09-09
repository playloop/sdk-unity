#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playloop.Http
{
    /// <summary>
    /// Internal HTTP wrapper used by the per-feature API classes. Adds the
    /// Authorization + Accept headers, JSON-encodes bodies, and turns non-2xx
    /// responses into <see cref="PlayloopException"/>.
    /// </summary>
    internal sealed class HttpClient
    {
        private readonly IHttpHandler _handler;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _environment;

        // When false, every request short-circuits to a soft failure
        // (default(T), no network, no throw). Set by PlayloopClient when it
        // builds in DISABLED mode (blank ingest key / base URL) so every
        // network-backed API - sessions, discord, playtest, resolver,
        // event-config, telemetry flush - fails soft instead of throwing.
        private readonly bool _enabled;

        public HttpClient(IHttpHandler handler, string baseUrl, string apiKey, string environment = "production", bool enabled = true)
        {
            _handler = handler;
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _apiKey = apiKey;
            _environment = NormalizeEnvironment(environment);
            _enabled = enabled;
        }

        private static string NormalizeEnvironment(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "production";
            return raw.Trim().ToLowerInvariant();
        }

        public Task<T> JsonAsync<T>(
            string method,
            string path,
            object? body = null,
            CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? extraHeaders = null)
        {
            if (!_enabled) return Task.FromResult<T>(default!);
            var headers = AuthHeaders();
            if (extraHeaders != null)
            {
                foreach (var kvp in extraHeaders) headers[kvp.Key] = kvp.Value;
            }
            byte[]? jsonBytes = null;
            if (body != null)
            {
                var json = JsonConvert.SerializeObject(body);
                jsonBytes = Encoding.UTF8.GetBytes(json);
            }
            var spec = new HttpRequestSpec(method, JoinUrl(path), headers, jsonBody: jsonBytes);
            return SendAsync<T>(spec, ct);
        }

        public Task<T> MultipartAsync<T>(
            string method,
            string path,
            Dictionary<string, string> fields,
            List<MultipartFile> files,
            CancellationToken ct = default)
        {
            if (!_enabled) return Task.FromResult<T>(default!);
            var headers = AuthHeaders();
            var spec = new HttpRequestSpec(
                method, JoinUrl(path), headers, formFields: fields, files: files);
            return SendAsync<T>(spec, ct);
        }

        private async Task<T> SendAsync<T>(HttpRequestSpec request, CancellationToken ct)
        {
            HttpResponseData response;
            try
            {
                response = await _handler.SendAsync(request, ct).ConfigureAwait(Playloop.PlAwait.Continue);
            }
            catch (PlayloopException)
            {
                throw; // already shaped
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlayloopException("Network request failed: " + ex.Message, status: 0);
            }

            response.Headers.TryGetValue("x-playloop-request-id", out var requestId);

            if (response.StatusCode >= 400)
            {
                object? parsedBody = ParseBodySafe(response.Body);
                var message = ExtractErrorMessage(parsedBody)
                    ?? $"Playloop API returned {response.StatusCode}";
                throw new PlayloopException(message, response.StatusCode, parsedBody, requestId);
            }

            if (response.StatusCode == 204 || string.IsNullOrEmpty(response.Body))
            {
                // For value types this returns default(T); the typical T here is
                // a class reference type so it's null. Callers using void-shaped
                // endpoints pass `object?` and ignore the result.
                return default!;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(response.Body)!;
            }
            catch (JsonException ex)
            {
                throw new PlayloopException(
                    "Failed to parse JSON response: " + ex.Message,
                    response.StatusCode,
                    response.Body,
                    requestId);
            }
        }

        private Dictionary<string, string> AuthHeaders() => new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + _apiKey,
            ["Accept"] = "application/json",
            ["X-Playloop-Environment"] = _environment,
            // Tells the platform which SDK card to attribute the session to on
            // /integrations. Without it, /api/telemetry falls back to
            // source='unity-telemetry' for back-compat.
            ["X-Playloop-SDK"] = "unity",
        };

        private string JoinUrl(string path) =>
            path.StartsWith("/", StringComparison.Ordinal) ? _baseUrl + path : _baseUrl + "/" + path;

        private static object? ParseBodySafe(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                return JsonConvert.DeserializeObject(body);
            }
            catch (JsonException)
            {
                return body; // surface the raw text when it isn't JSON
            }
        }

        private static string? ExtractErrorMessage(object? body)
        {
            if (body is JObject obj)
            {
                var error = obj["error"]?.ToString();
                if (!string.IsNullOrEmpty(error)) return error;
                var message = obj["message"]?.ToString();
                if (!string.IsNullOrEmpty(message)) return message;
            }
            if (body is string str && !string.IsNullOrEmpty(str))
            {
                return str;
            }
            return null;
        }
    }
}
