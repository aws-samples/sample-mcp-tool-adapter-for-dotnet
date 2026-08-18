// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Gateway;
using McpToolAdapter.OpenApi;

namespace McpToolAdapter.Hosting
{
    /// <summary>
    /// Routes, authorizes and executes endpoint requests.
    /// </summary>
    /// <remarks>
    /// Contains everything a host would otherwise reimplement, so the .NET Framework host and any
    /// modern host behave identically and only one of them has to be trusted.
    /// </remarks>
    public sealed class McpRequestProcessor
    {
        private readonly ToolDispatcher _dispatcher;
        private readonly McpEndpointOptions _options;
        private readonly IJsonObjectParser _parser;
        private readonly IMcpAuthorizer _authorizer;
        private readonly Lazy<JsonObject> _openApiDocument;
        private readonly Lazy<string> _openApiJson;
        private readonly Lazy<IReadOnlyList<GatewayIssue>> _gatewayIssues;
        private readonly IReadOnlyList<string> _configurationProblems;

        public McpRequestProcessor(
            ToolDispatcher dispatcher,
            McpEndpointOptions options,
            IJsonObjectParser parser,
            IMcpAuthorizer authorizer = null,
            OpenApiOptions openApiOptions = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _authorizer = authorizer ?? new SharedSecretAuthorizer();
            _configurationProblems = options.Validate();

            var documentOptions = openApiOptions ?? new OpenApiOptions
            {
                Title = string.IsNullOrWhiteSpace(options.Title) ? "Exposed application operations" : options.Title,
                Version = options.DocumentVersion,
                ServerUrl = options.ServerUrl,
                ToolPathPrefix = options.NormalizedBasePath + "/tools"
            };

            _openApiDocument = new Lazy<JsonObject>(
                () => new OpenApiDocumentBuilder().Build(dispatcher.Catalog, documentOptions));
            _openApiJson = new Lazy<string>(() => Json.Write(_openApiDocument.Value));

            _gatewayIssues = new Lazy<IReadOnlyList<GatewayIssue>>(() => AgentCoreCompatibility.Check(
                _openApiDocument.Value,
                dispatcher.Catalog,
                new AgentCoreTargetOptions
                {
                    TargetName = options.AgentCoreTargetName,
                    CatalogNamePrefix = options.NamePrefix
                }));
        }

        /// <summary>
        /// Bedrock AgentCore Gateway compatibility problems with the document this endpoint serves.
        /// </summary>
        /// <remarks>
        /// Advisory, not enforced: a compatibility problem is a reason the gateway will reject or
        /// mis-invoke the target, but it is not a reason to stop serving. Hosts should log these at
        /// startup, and they are reported on the health endpoint.
        /// </remarks>
        public IReadOnlyList<GatewayIssue> GatewayIssues
        {
            get { return _gatewayIssues.Value; }
        }

        /// <summary>
        /// Handles the request, or returns null when the path is not ours so the host lets the
        /// application's own pipeline continue untouched.
        /// </summary>
        public McpResponse TryHandle(McpRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var basePath = _options.NormalizedBasePath;
            var path = request.AppRelativePath;

            var isOurs = string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
            if (!isOurs) return null;

            // When disabled, respond exactly as though nothing were installed.
            if (!_options.Enabled) return NotFound();

            if (_configurationProblems.Count > 0)
            {
                // Details go to the audit log, not to an unauthenticated caller.
                return McpResponse.Error(503, "misconfigured",
                    "The endpoint is enabled but not correctly configured, so it is refusing requests.");
            }

            var authorization = _authorizer.Authorize(request, _options);
            if (!authorization.IsAllowed)
                return McpResponse.Error(authorization.StatusCode, authorization.ErrorCode, authorization.Message);

            var relative = path.Length > basePath.Length ? path.Substring(basePath.Length) : string.Empty;

            if (string.Equals(relative, "/health", StringComparison.OrdinalIgnoreCase))
                return Get(request, Health);

            if (string.Equals(relative, "/openapi.json", StringComparison.OrdinalIgnoreCase))
                return Get(request, () => new McpResponse(200, _openApiJson.Value));

            if (string.Equals(relative, "/tools", StringComparison.OrdinalIgnoreCase))
                return Get(request, ToolListing);

            if (relative.StartsWith("/tools/", StringComparison.OrdinalIgnoreCase))
            {
                var toolName = relative.Substring("/tools/".Length);
                return Invoke(request, toolName, authorization.Caller, authorization.Claims);
            }

            return NotFound();
        }

        private McpResponse Invoke(
            McpRequest request, string toolName, string caller,
            IReadOnlyDictionary<string, string> claims)
        {
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                return McpResponse.Error(405, "method_not_allowed",
                    "Use POST with a JSON body to invoke an operation.");
            }

            if (string.IsNullOrWhiteSpace(toolName)) return NotFound();

            ToolDescriptor unused;
            if (!_dispatcher.Catalog.TryGet(toolName, out unused))
            {
                return McpResponse.Error(404, ToolErrorCodes.UnknownTool,
                    "No operation named '" + toolName + "'.");
            }

            IDictionary<string, object> arguments;
            try
            {
                arguments = _parser.ParseObject(request.Body) ?? new Dictionary<string, object>();
            }
            catch (Exception)
            {
                return McpResponse.Error(400, "malformed_body",
                    "The request body must be a JSON object, or empty when the operation takes no arguments.");
            }

            var context = new ToolCallContext(
                caller, request.Header(McpEndpointOptions.CorrelationHeader), claims);
            var result = _dispatcher.Invoke(toolName, new ReadOnlyArguments(arguments), context);

            return new McpResponse(StatusFor(result), Json.Write(result.ToEnvelope()));
        }

        private static int StatusFor(ToolInvocationResult result)
        {
            if (result.IsSuccess) return 200;

            switch (result.ErrorCode)
            {
                case ToolErrorCodes.UnknownTool: return 404;
                case ToolErrorCodes.InvalidArguments: return 400;
                case ToolErrorCodes.MutationDisabled: return 403;
                default: return 500;
            }
        }

        private McpResponse Health()
        {
            var payload = new JsonObject
            {
                ["ok"] = true,
                ["tools"] = _dispatcher.Catalog.Tools.Count,
                ["mutatingAllowed"] = _options.AllowMutating
            };

            var issues = GatewayIssues;
            if (issues.Count > 0)
            {
                payload["gatewayIssues"] = issues
                    .Select(i => (object)new JsonObject
                    {
                        ["severity"] = i.Severity.ToString().ToLowerInvariant(),
                        ["code"] = i.Code,
                        ["message"] = i.Message
                    })
                    .ToArray();
            }

            return McpResponse.Json(200, payload);
        }

        private McpResponse ToolListing()
        {
            var tools = _dispatcher.Catalog.Tools
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => (object)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["mutating"] = t.IsMutating,
                    ["target"] = (t.Method.DeclaringType == null ? "?" : t.Method.DeclaringType.FullName) + "." + t.Method.Name,
                    ["inputSchema"] = t.InputSchema
                })
                .ToArray();

            return McpResponse.Json(200, new JsonObject { ["ok"] = true, ["tools"] = tools });
        }

        private static McpResponse Get(McpRequest request, Func<McpResponse> handler)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                return McpResponse.Error(405, "method_not_allowed", "Use GET for this path.");
            return handler();
        }

        private static McpResponse NotFound()
        {
            return McpResponse.Error(404, "not_found", "Not found.");
        }

        /// <summary>Adapts a mutable map to the read-only contract the dispatcher expects.</summary>
        private sealed class ReadOnlyArguments : IReadOnlyDictionary<string, object>
        {
            private readonly IDictionary<string, object> _inner;

            public ReadOnlyArguments(IDictionary<string, object> inner)
            {
                _inner = inner;
            }

            public object this[string key]
            {
                get { return _inner[key]; }
            }

            public IEnumerable<string> Keys
            {
                get { return _inner.Keys; }
            }

            public IEnumerable<object> Values
            {
                get { return _inner.Values; }
            }

            public int Count
            {
                get { return _inner.Count; }
            }

            public bool ContainsKey(string key)
            {
                return _inner.ContainsKey(key);
            }

            public bool TryGetValue(string key, out object value)
            {
                return _inner.TryGetValue(key, out value);
            }

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                return _inner.GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
