// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.IO;
using System.Web;
using McpToolAdapter.Hosting;

namespace McpToolAdapter.Web
{
    /// <summary>
    /// Intercepts requests for the endpoint's path and serves them, letting everything else through.
    /// </summary>
    /// <remarks>
    /// <para>The request is handled in <c>BeginRequest</c> and finished with
    /// <see cref="HttpApplication.CompleteRequest"/> rather than by remapping to an
    /// <see cref="IHttpHandler"/>. That avoids depending on handler mappings for extensionless
    /// paths, and avoids <c>RemapHandler</c>, which is unavailable in the IIS classic pipeline.</para>
    /// <para>Requires the IIS integrated pipeline, where managed modules see every request. Under
    /// the classic pipeline an extensionless path never reaches managed code; use
    /// <see cref="McpHandler"/> with an explicit <c>.ashx</c> there instead.</para>
    /// <para>The path check runs before anything else is touched, so the cost imposed on the host
    /// application's own requests is one string comparison.</para>
    /// </remarks>
    public sealed class McpEndpointModule : IHttpModule
    {
        /// <summary>Largest request body accepted, guarding against memory exhaustion from a single call.</summary>
        internal const int MaxRequestBytes = 4 * 1024 * 1024;

        public void Init(HttpApplication context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.BeginRequest += OnBeginRequest;
        }

        public void Dispose()
        {
        }

        private static void OnBeginRequest(object sender, EventArgs e)
        {
            var application = (HttpApplication)sender;
            var context = application.Context;
            if (context == null) return;

            var runtime = McpEndpoint.Runtime;
            var path = AppRelativePath(context.Request);
            var basePath = runtime.Options.NormalizedBasePath;

            var isOurs = string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
            if (!isOurs) return;

            McpResponse response;

            if (runtime.Processor == null)
            {
                // Disabled looks like nothing is installed; enabled-but-broken says so without
                // disclosing the reason to an unauthenticated caller.
                response = runtime.Options.Enabled
                    ? new McpResponse(503,
                        "{\"ok\":false,\"error\":{\"code\":\"misconfigured\",\"message\":" +
                        "\"The endpoint is enabled but failed to start. See the application trace log.\"}}")
                    : new McpResponse(404, "{\"ok\":false,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}");
            }
            else if (context.Request.ContentLength > MaxRequestBytes)
            {
                response = new McpResponse(413,
                    "{\"ok\":false,\"error\":{\"code\":\"body_too_large\",\"message\":\"The request body is too large.\"}}");
            }
            else
            {
                response = runtime.Processor.TryHandle(ToMcpRequest(context, path));
                if (response == null) return;
            }

            Write(context, response);
            application.CompleteRequest();
        }

        internal static McpRequest ToMcpRequest(HttpContext context, string appRelativePath)
        {
            var request = context.Request;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in request.Headers.AllKeys)
            {
                if (name != null) headers[name] = request.Headers[name];
            }

            return new McpRequest(
                request.HttpMethod,
                appRelativePath,
                headers,
                ReadBody(request),
                request.UserHostAddress,
                request.IsSecureConnection);
        }

        private static string ReadBody(HttpRequest request)
        {
            if (request.ContentLength <= 0) return null;

            // InputStream is the *buffered* path: per its documentation the bytes are retained in the
            // internal storage ASP.NET uses to populate Form, Files and InputStream, so downstream
            // .aspx pages still run. GetBufferlessInputStream would consume the body and break them,
            // and is deliberately not used here. Only requests already matched to our own base path
            // reach this method, so the host application's requests are never read at all.
            var stream = request.InputStream;
            var originalPosition = stream.CanSeek ? stream.Position : 0;

            try
            {
                if (stream.CanSeek) stream.Position = 0;
                using (var reader = new StreamReader(stream, request.ContentEncoding ?? System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    return reader.ReadToEnd();
                }
            }
            finally
            {
                if (stream.CanSeek) stream.Position = originalPosition;
            }
        }

        /// <summary>
        /// Converts the absolute request path to one relative to the application root, so an
        /// application deployed into a virtual directory works without configuration.
        /// </summary>
        internal static string AppRelativePath(HttpRequest request)
        {
            var path = request.Path ?? "/";
            var applicationPath = request.ApplicationPath ?? "/";

            if (applicationPath.Length > 1 &&
                path.StartsWith(applicationPath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(applicationPath.Length);
            }

            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
            return path;
        }

        internal static void Write(HttpContext context, McpResponse response)
        {
            var http = context.Response;

            http.Clear();
            http.StatusCode = response.StatusCode;
            http.ContentType = response.ContentType;

            // Without this IIS replaces a non-2xx JSON body with its own HTML error page, which a
            // gateway cannot parse.
            http.TrySkipIisCustomErrors = true;

            http.Cache.SetCacheability(HttpCacheability.NoCache);
            http.AppendHeader("Cache-Control", "no-store");
            http.Write(response.Body ?? string.Empty);
        }
    }

    /// <summary>
    /// Explicit <see cref="IHttpHandler"/> for applications that prefer a visible <c>.ashx</c>, or
    /// that run under the IIS classic pipeline where <see cref="McpEndpointModule"/> never sees
    /// extensionless requests.
    /// </summary>
    /// <remarks>
    /// Register by adding a file such as <c>_mcp.ashx</c> containing:
    /// <code>&lt;%@ WebHandler Class="McpToolAdapter.Web.McpHandler" Language="C#" %&gt;</code>
    /// and setting <c>mcp:basePath</c> to the path that file is served from.
    /// </remarks>
    public class McpHandler : IHttpHandler
    {
        public bool IsReusable
        {
            get { return true; }
        }

        public void ProcessRequest(HttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var runtime = McpEndpoint.Runtime;
            var path = McpEndpointModule.AppRelativePath(context.Request);

            if (runtime.Processor == null)
            {
                McpEndpointModule.Write(context, runtime.Options.Enabled
                    ? new McpResponse(503,
                        "{\"ok\":false,\"error\":{\"code\":\"misconfigured\",\"message\":" +
                        "\"The endpoint is enabled but failed to start. See the application trace log.\"}}")
                    : new McpResponse(404, "{\"ok\":false,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}"));
                return;
            }

            var response = runtime.Processor.TryHandle(McpEndpointModule.ToMcpRequest(context, path))
                           ?? new McpResponse(404, "{\"ok\":false,\"error\":{\"code\":\"not_found\",\"message\":\"Not found.\"}}");

            McpEndpointModule.Write(context, response);
        }
    }
}
