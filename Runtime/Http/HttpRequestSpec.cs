#nullable enable
using System.Collections.Generic;

namespace Playloop.Http
{
    /// <summary>
    /// Transport-agnostic request descriptor. Built by the HTTP wrapper and
    /// handed to an <see cref="IHttpHandler"/> which performs the actual I/O.
    /// </summary>
    public sealed class HttpRequestSpec
    {
        public string Method { get; }
        public string Url { get; }
        public Dictionary<string, string> Headers { get; }

        /// <summary>JSON body bytes when Content-Type is application/json. Null otherwise.</summary>
        public byte[]? JsonBody { get; }

        /// <summary>Multipart fields (string -> string) for form submissions.</summary>
        public Dictionary<string, string>? FormFields { get; }

        /// <summary>Multipart file uploads keyed by form field name.</summary>
        public List<MultipartFile>? Files { get; }

        public HttpRequestSpec(
            string method,
            string url,
            Dictionary<string, string> headers,
            byte[]? jsonBody = null,
            Dictionary<string, string>? formFields = null,
            List<MultipartFile>? files = null)
        {
            Method = method;
            Url = url;
            Headers = headers;
            JsonBody = jsonBody;
            FormFields = formFields;
            Files = files;
        }
    }

    public sealed class MultipartFile
    {
        public string FieldName { get; }
        public string FileName { get; }
        public byte[] Content { get; }
        public string ContentType { get; }

        public MultipartFile(string fieldName, string fileName, byte[] content, string contentType = "application/octet-stream")
        {
            FieldName = fieldName;
            FileName = fileName;
            Content = content;
            ContentType = contentType;
        }
    }
}
