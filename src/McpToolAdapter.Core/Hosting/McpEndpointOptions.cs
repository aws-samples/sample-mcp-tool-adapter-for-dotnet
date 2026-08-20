// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;

namespace McpToolAdapter.Hosting
{
    /// <summary>
    /// Settings for the HTTP endpoint, independent of any web framework.
    /// </summary>
    /// <remarks>
    /// Every default is the safe one: disabled, read-only, HTTPS required, no exception detail.
    /// Enabling the endpoint is a deliberate, reviewable change rather than a side effect of
    /// installing a package.
    /// </remarks>
    public sealed class McpEndpointOptions
    {
        /// <summary>Master switch. False unless explicitly set, so installation alone changes nothing.</summary>
        public bool Enabled { get; set; }

        /// <summary>Path served, relative to the application root.</summary>
        public string BasePath { get; set; } = "/_mcp";

        /// <summary>Prefix applied to every tool name, identifying this application.</summary>
        public string NamePrefix { get; set; }

        /// <summary>Whether tools marked <c>Mutating()</c> may run.</summary>
        public bool AllowMutating { get; set; }

        /// <summary>
        /// Secret the caller presents in the <c>X-Mcp-Key</c> header. Required when enabled:
        /// without it the endpoint is unauthenticated remote invocation of business logic.
        /// </summary>
        public string SharedSecret { get; set; }

        /// <summary>Optional further restriction to these caller addresses.</summary>
        public IList<string> AllowedIpAddresses { get; } = new List<string>();

        /// <summary>Permit plain HTTP. False by default; the shared secret would travel in clear text.</summary>
        public bool AllowInsecureTransport { get; set; }

        /// <summary>Return exception type and message on failure. False by default — legacy exception text leaks internals.</summary>
        public bool IncludeExceptionDetail { get; set; }

        /// <summary>Default cap on returned collection items. Null means unlimited.</summary>
        public int? MaxResultItems { get; set; } = 200;

        /// <summary>Public base URL as the gateway reaches this application; emitted into the OpenAPI document.</summary>
        public string ServerUrl { get; set; }

        public string Title { get; set; }
        public string DocumentVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Name of the Bedrock AgentCore gateway target this endpoint will be registered as, if any.
        /// </summary>
        /// <remarks>
        /// Used only to check compatibility at startup. AgentCore prefixes every tool with
        /// <c>targetName___</c>, so knowing the target name is what makes it possible to detect a tool
        /// name that will breach the model's limit — a failure that otherwise appears at invocation
        /// rather than at target creation. Leave unset if you are not using AgentCore.
        /// </remarks>
        public string AgentCoreTargetName { get; set; }

        /// <summary>Header carrying the shared secret.</summary>
        public const string ApiKeyHeader = "X-Mcp-Key";

        /// <summary>Optional header carrying a correlation id, echoed into audit entries.</summary>
        public const string CorrelationHeader = "X-Correlation-Id";

        /// <summary>
        /// Reasons this configuration must not serve traffic. A non-empty result means the endpoint
        /// refuses every request rather than serving a weakened one.
        /// </summary>
        /// <remarks>
        /// Credential requirements are not checked here. They belong to the authorizer, which is the
        /// only thing that knows what it needs: a shared secret is mandatory for
        /// <see cref="SharedSecretAuthorizer"/> and irrelevant to a bearer-token authorizer. See
        /// <see cref="IMcpAuthorizer.ConfigurationProblems"/>.
        /// </remarks>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            if (!Enabled) return problems;

            if (string.IsNullOrWhiteSpace(BasePath) || !BasePath.StartsWith("/", StringComparison.Ordinal))
                problems.Add("The base path must start with '/'.");

            return problems;
        }

        /// <summary>
        /// <see cref="BasePath"/> with a guaranteed leading slash and no trailing slash. Hosts use
        /// this for the cheap prefix test that decides whether a request is ours.
        /// </summary>
        public string NormalizedBasePath
        {
            get
            {
                var path = string.IsNullOrWhiteSpace(BasePath) ? "/_mcp" : BasePath.Trim();
                if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
                return path.Length > 1 ? path.TrimEnd('/') : path;
            }
        }
    }
}
