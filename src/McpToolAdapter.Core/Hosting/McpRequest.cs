// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;

namespace McpToolAdapter.Hosting
{
    /// <summary>
    /// A web-framework-independent view of an inbound request.
    /// </summary>
    /// <remarks>
    /// Hosts translate their own request type into this. Keeping the processor off
    /// <c>HttpContext</c> is what allows routing and authorization to be unit tested on any
    /// platform, rather than only inside IIS.
    /// </remarks>
    public sealed class McpRequest
    {
        private readonly IDictionary<string, string> _headers;

        public McpRequest(
            string method,
            string appRelativePath,
            IDictionary<string, string> headers = null,
            string body = null,
            string remoteIpAddress = null,
            bool isSecureConnection = true)
        {
            Method = (method ?? "GET").ToUpperInvariant();
            AppRelativePath = NormalizePath(appRelativePath);
            Body = body;
            RemoteIpAddress = remoteIpAddress;
            IsSecureConnection = isSecureConnection;

            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
            {
                foreach (var header in headers) _headers[header.Key] = header.Value;
            }
        }

        public string Method { get; }

        /// <summary>Request path relative to the application root, always starting with '/'.</summary>
        public string AppRelativePath { get; }

        public string Body { get; }
        public string RemoteIpAddress { get; }
        public bool IsSecureConnection { get; }

        public string Header(string name)
        {
            string value;
            return _headers.TryGetValue(name ?? string.Empty, out value) ? value : null;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/";
            var normalized = path.Trim();
            if (normalized.StartsWith("~", StringComparison.Ordinal)) normalized = normalized.Substring(1);
            if (!normalized.StartsWith("/", StringComparison.Ordinal)) normalized = "/" + normalized;

            var query = normalized.IndexOf('?');
            if (query >= 0) normalized = normalized.Substring(0, query);

            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }
    }

    /// <summary>A response for the host to write verbatim.</summary>
    public sealed class McpResponse
    {
        public McpResponse(int statusCode, string body, string contentType = "application/json; charset=utf-8")
        {
            StatusCode = statusCode;
            Body = body;
            ContentType = contentType;
        }

        public int StatusCode { get; }
        public string Body { get; }
        public string ContentType { get; }

        internal static McpResponse Json(int statusCode, JsonObject payload)
        {
            return new McpResponse(statusCode, McpToolAdapter.Json.Write(payload));
        }

        internal static McpResponse Error(int statusCode, string code, string message)
        {
            return Json(statusCode, new JsonObject
            {
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            });
        }
    }

    /// <summary>Parses a JSON object body. Supplied by the host, which already has a parser.</summary>
    public interface IJsonObjectParser
    {
        /// <summary>
        /// Parses <paramref name="json"/> into a loosely-typed map. Returns an empty map for null or
        /// whitespace. Throws <see cref="FormatException"/> if the text is not a JSON object.
        /// </summary>
        IDictionary<string, object> ParseObject(string json);
    }
}
