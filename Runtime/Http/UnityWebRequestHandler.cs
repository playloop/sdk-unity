#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Playloop.Http
{
    /// <summary>
    /// UnityWebRequest-backed transport. Use this inside Unity. It's the only
    /// HTTP client that works on every Unity target (including WebGL, where
    /// System.Net.Http is unavailable).
    ///
    /// <para>
    /// A UnityWebRequest can only be created on the main thread, and it
    /// completes from the main loop. The quit hook blocks the main thread
    /// while the final flush runs on a worker, so for that flush the handler
    /// switches to <see cref="DefaultHttpHandler"/>, which works from any
    /// thread (see <see cref="UseThreadSafeTransportForShutdown"/>).
    /// </para>
    /// </summary>
    public sealed class UnityWebRequestHandler : IHttpHandler
    {
        private readonly int _timeoutSeconds;
        private volatile IHttpHandler? _shutdownTransport;

        public UnityWebRequestHandler(int timeoutSeconds = 30)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// Send every later request through System.Net.Http instead. Called
        /// by the quit hook just before it runs the session end on a worker
        /// thread and waits for it. Never on WebGL, which has no
        /// System.Net.Http and whose quit hook does not block.
        /// </summary>
        internal void UseThreadSafeTransportForShutdown()
        {
            if (_shutdownTransport == null) _shutdownTransport = new DefaultHttpHandler(_timeoutSeconds);
        }

        public void Dispose()
        {
            var shutdown = _shutdownTransport;
            _shutdownTransport = null;
            shutdown?.Dispose();
        }

        public Task<HttpResponseData> SendAsync(HttpRequestSpec request, CancellationToken cancellationToken)
        {
            var shutdown = _shutdownTransport;
            if (shutdown != null) return shutdown.SendAsync(request, cancellationToken);

            var tcs = new TaskCompletionSource<HttpResponseData>();
            UnityWebRequest unityRequest = Build(request);
            unityRequest.timeout = _timeoutSeconds;

            CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try { unityRequest.Abort(); } catch { /* ignored */ }
                tcs.TrySetCanceled(cancellationToken);
            });

            UnityWebRequestAsyncOperation op = unityRequest.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    if (IsTransportFailure(unityRequest))
                    {
                        tcs.TrySetException(new PlayloopException(
                            "Network request failed: " + unityRequest.error,
                            status: 0,
                            body: null));
                        return;
                    }

                    var headers = new Dictionary<string, string>();
                    var responseHeaders = unityRequest.GetResponseHeaders();
                    if (responseHeaders != null)
                    {
                        foreach (var kv in responseHeaders)
                        {
                            headers[kv.Key.ToLowerInvariant()] = kv.Value;
                        }
                    }

                    var body = unityRequest.downloadHandler != null
                        ? unityRequest.downloadHandler.text ?? ""
                        : "";

                    tcs.TrySetResult(new HttpResponseData(
                        (int)unityRequest.responseCode,
                        body,
                        headers));
                }
                finally
                {
                    registration.Dispose();
                    unityRequest.Dispose();
                }
            };

            return tcs.Task;
        }

        private static bool IsTransportFailure(UnityWebRequest request)
        {
            // ConnectionError + DataProcessingError indicate the request never
            // got a real HTTP response. ProtocolError is a 4xx/5xx. We still
            // want to surface that as a regular response so HttpClient can map
            // it to a PlayloopException with the right status.
#if UNITY_2020_1_OR_NEWER
            return request.result == UnityWebRequest.Result.ConnectionError
                || request.result == UnityWebRequest.Result.DataProcessingError;
#else
            return request.isNetworkError;
#endif
        }

        private static UnityWebRequest Build(HttpRequestSpec request)
        {
            UnityWebRequest unityRequest;

            if (request.JsonBody != null)
            {
                unityRequest = new UnityWebRequest(request.Url, request.Method);
                unityRequest.uploadHandler = new UploadHandlerRaw(request.JsonBody);
                unityRequest.downloadHandler = new DownloadHandlerBuffer();
                unityRequest.SetRequestHeader("Content-Type", "application/json");
            }
            else if (request.FormFields != null || request.Files != null)
            {
                var sections = new List<IMultipartFormSection>();
                if (request.FormFields != null)
                {
                    foreach (var kv in request.FormFields)
                    {
                        sections.Add(new MultipartFormDataSection(kv.Key, kv.Value));
                    }
                }
                if (request.Files != null)
                {
                    foreach (var f in request.Files)
                    {
                        sections.Add(new MultipartFormFileSection(
                            f.FieldName, f.Content, f.FileName, f.ContentType));
                    }
                }

                // UnityWebRequest.Post handles the multipart encoding for us.
                unityRequest = UnityWebRequest.Post(request.Url, sections);
                if (request.Method != "POST")
                {
                    unityRequest.method = request.Method;
                }
            }
            else
            {
                unityRequest = new UnityWebRequest(request.Url, request.Method);
                unityRequest.downloadHandler = new DownloadHandlerBuffer();
            }

            foreach (var kv in request.Headers)
            {
                unityRequest.SetRequestHeader(kv.Key, kv.Value);
            }

            return unityRequest;
        }
    }
}
#endif
