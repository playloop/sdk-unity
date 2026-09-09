#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playloop.Http;

namespace Playloop.Sessions
{
    /// <summary>Upload and inspect playtest sessions.</summary>
    public sealed class SessionsApi
    {
        private readonly Http.HttpClient _http;

        internal SessionsApi(Http.HttpClient http)
        {
            _http = http;
        }

        /// <summary>Ingest a session from raw bytes (use this in Unity for files loaded via UnityWebRequest, AssetBundles, etc.).</summary>
        public Task<Session> IngestAsync(
            byte[] fileBytes,
            string game,
            IngestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (fileBytes == null) throw new ArgumentNullException(nameof(fileBytes));
            if (string.IsNullOrEmpty(game)) throw new ArgumentException("game is required.", nameof(game));

            var opts = options ?? new IngestOptions();
            opts.Game = game;
            return IngestInternalAsync(fileBytes, opts.FileName ?? "session.bin", opts, cancellationToken);
        }

        /// <summary>Ingest a session by filesystem path. Works on every Unity platform except WebGL.</summary>
        public Task<Session> IngestAsync(
            string filePath,
            string game,
            IngestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath is required.", nameof(filePath));
            if (string.IsNullOrEmpty(game)) throw new ArgumentException("game is required.", nameof(game));

            var bytes = File.ReadAllBytes(filePath);
            var opts = options ?? new IngestOptions();
            opts.Game = game;
            return IngestInternalAsync(bytes, opts.FileName ?? Path.GetFileName(filePath), opts, cancellationToken);
        }

        /// <summary>Re-run analysis on an existing session.</summary>
        public Task<Session> AnalyzeAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required.", nameof(sessionId));
            return _http.JsonAsync<Session>("POST", $"/api/analyze/{Uri.EscapeDataString(sessionId)}", body: null, ct: cancellationToken);
        }

        /// <summary>Fetch a single session with its insights.</summary>
        public Task<Session> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required.", nameof(sessionId));
            return _http.JsonAsync<Session>("GET", $"/api/sessions/{Uri.EscapeDataString(sessionId)}", body: null, ct: cancellationToken);
        }

        /// <summary>List recent sessions (optionally filtered by game).</summary>
        public Task<List<Session>> ListAsync(ListSessionsOptions? options = null, CancellationToken cancellationToken = default)
        {
            var query = new StringBuilder();
            if (options != null)
            {
                if (options.Limit.HasValue) Append(query, "limit", options.Limit.Value.ToString());
                if (options.Offset.HasValue) Append(query, "offset", options.Offset.Value.ToString());
                if (!string.IsNullOrEmpty(options.Game)) Append(query, "game", options.Game!);
            }

            var path = query.Length > 0 ? "/api/sessions?" + query : "/api/sessions";
            return _http.JsonAsync<List<Session>>("GET", path, body: null, ct: cancellationToken);
        }

        private Task<Session> IngestInternalAsync(
            byte[] fileBytes,
            string fileName,
            IngestOptions opts,
            CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, string> { ["game"] = opts.Game };
            if (!string.IsNullOrEmpty(opts.Title)) fields["title"] = opts.Title!;
            if (!string.IsNullOrEmpty(opts.TesterHandle)) fields["testerHandle"] = opts.TesterHandle!;
            if (!string.IsNullOrEmpty(opts.Source)) fields["source"] = opts.Source!;

            var files = new List<MultipartFile>
            {
                new MultipartFile("session", fileName, fileBytes, "application/octet-stream"),
            };

            return _http.MultipartAsync<Session>("POST", "/api/sessions", fields, files, cancellationToken);
        }

        private static void Append(StringBuilder sb, string key, string value)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
    }
}
