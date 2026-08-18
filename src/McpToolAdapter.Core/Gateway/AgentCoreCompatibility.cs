// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace McpToolAdapter.Gateway
{
    public enum GatewayIssueSeverity
    {
        /// <summary>Target creation or invocation will fail.</summary>
        Error,

        /// <summary>Works, but wastes the tool-name budget or is otherwise a poor configuration.</summary>
        Warning
    }

    public sealed class GatewayIssue
    {
        internal GatewayIssue(GatewayIssueSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code;
            Message = message;
        }

        public GatewayIssueSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }

        public override string ToString()
        {
            return Severity.ToString().ToUpperInvariant() + " " + Code + ": " + Message;
        }
    }

    public sealed class AgentCoreTargetOptions
    {
        /// <summary>
        /// The name the gateway target will be given. AgentCore prefixes every tool with it as
        /// <c>targetName___toolName</c>, so it consumes part of the tool-name budget.
        /// </summary>
        public string TargetName { get; set; }

        /// <summary>
        /// Maximum length of the tool name a model sees, including the target prefix. Defaults to 64,
        /// the limit Anthropic and Bedrock tool specifications impose.
        /// </summary>
        /// <remarks>
        /// 64 is the documented Bedrock <c>ToolSpecification.name</c> constraint: "Minimum length of
        /// 1. Maximum length of 64. Pattern: <c>[a-zA-Z0-9_-]+</c>". AgentCore itself publishes no
        /// number, documenting only that "each LLM will have ToolSpec constraints" and that breaching
        /// them fails in the data plane — at invocation, not at target creation. Raise this only for a
        /// model you know permits longer names.
        /// </remarks>
        public int MaxToolNameLength { get; set; } = 64;

        /// <summary>Name prefix configured on the catalog, used to detect double-prefixing.</summary>
        public string CatalogNamePrefix { get; set; }
    }

    /// <summary>
    /// Checks an emitted OpenAPI document and catalog against Amazon Bedrock AgentCore Gateway's
    /// documented OpenAPI target constraints.
    /// </summary>
    /// <remarks>
    /// <para>Exists because AgentCore's failure modes are split awkwardly. An unsupported schema
    /// construct fails at <c>CreateGatewayTarget</c>, which is at least immediate. But a tool name
    /// that breaches the model's tool-specification limit fails <em>in the data plane</em> — the
    /// target creates cleanly and the call fails later, which is a far worse place to find out.
    /// This moves both to application start.</para>
    /// <para>Constraints encoded here come from the AgentCore developer guide's OpenAPI feature
    /// support table and tool-naming documentation. Notably: <c>oneOf</c>, <c>anyOf</c> and
    /// <c>allOf</c> are unsupported; security schemes at the specification level are unsupported
    /// because outbound authentication is configured on the gateway target instead; and the
    /// <c>servers</c> URL must be the real endpoint.</para>
    /// </remarks>
    public static class AgentCoreCompatibility
    {
        private static readonly Regex ToolNamePattern = new Regex("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        /// <summary>Schema keywords AgentCore does not support for OpenAPI targets.</summary>
        private static readonly string[] UnsupportedSchemaKeywords = { "oneOf", "anyOf", "allOf", "not", "discriminator", "$ref" };

        /// <summary>
        /// Specification-level authentication keys. Unsupported: outbound authentication belongs on
        /// the gateway target's credential provider, not in the document.
        /// </summary>
        private static readonly string[] ForbiddenDocumentKeys = { "securitySchemes", "security" };

        public static IReadOnlyList<GatewayIssue> Check(
            JsonObject openApiDocument,
            ToolCatalog catalog,
            AgentCoreTargetOptions options = null)
        {
            if (openApiDocument == null) throw new ArgumentNullException(nameof(openApiDocument));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            options = options ?? new AgentCoreTargetOptions();

            var issues = new List<GatewayIssue>();

            CheckVersion(openApiDocument, issues);
            CheckServers(openApiDocument, issues);
            CheckOperations(openApiDocument, issues);
            CheckForbiddenKeys(openApiDocument, issues);
            CheckUnsupportedKeywords(openApiDocument, issues);
            CheckToolNames(catalog, options, issues);
            CheckDoublePrefixing(options, issues);

            return issues;
        }

        private static void CheckVersion(JsonObject document, List<GatewayIssue> issues)
        {
            object version;
            var declared = document.TryGetValue("openapi", out version) ? version as string : null;

            if (string.IsNullOrEmpty(declared))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "missing_openapi_version",
                    "The document has no 'openapi' version. AgentCore accepts 3.0 and 3.1 only."));
                return;
            }

            if (!declared.StartsWith("3.0", StringComparison.Ordinal) &&
                !declared.StartsWith("3.1", StringComparison.Ordinal))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "unsupported_openapi_version",
                    "OpenAPI '" + declared + "' is not supported. AgentCore accepts 3.0 and 3.1; Swagger 2.0 is rejected."));
            }
        }

        private static void CheckServers(JsonObject document, List<GatewayIssue> issues)
        {
            object servers;
            var list = document.TryGetValue("servers", out servers) ? servers as object[] : null;

            if (list == null || list.Length == 0)
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "missing_server_url",
                    "No 'servers' entry. AgentCore requires the server attribute to carry the real " +
                    "endpoint URL. Set the endpoint's serverUrl configuration value."));
                return;
            }

            var first = list[0] as JsonObject;
            object rawUrl;
            var url = first != null && first.TryGetValue("url", out rawUrl) ? rawUrl as string : null;

            if (string.IsNullOrWhiteSpace(url))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "missing_server_url",
                    "The first 'servers' entry has no URL."));
                return;
            }

            // Checked before parsing: braces are illegal in a URI host, so a templated URL fails
            // Uri.TryCreate and would otherwise be misreported as merely malformed.
            var isTemplated = url.IndexOf('{') >= 0;
            if (isTemplated)
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Warning, "templated_server_url",
                    "The server URL contains a '{placeholder}'. AgentCore's guidance is to use fully " +
                    "qualified static URLs; unconstrained server variables are an SSRF risk. If a " +
                    "variable is unavoidable, restrict it with an enum."));
            }

            Uri parsed;
            if (!isTemplated && !Uri.TryCreate(url, UriKind.Absolute, out parsed))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "invalid_server_url",
                    "The server URL '" + url + "' is not an absolute URI."));
                return;
            }

            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Warning, "insecure_server_url",
                    "The server URL is not HTTPS. The gateway sends the outbound credential on every " +
                    "call, so it should not travel in clear text."));
            }
        }

        private static void CheckOperations(JsonObject document, List<GatewayIssue> issues)
        {
            object rawPaths;
            var paths = document.TryGetValue("paths", out rawPaths) ? rawPaths as JsonObject : null;

            if (paths == null || paths.Count == 0)
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "no_operations",
                    "The document declares no paths, so the target would expose no tools."));
                return;
            }

            foreach (var path in paths)
            {
                var methods = path.Value as JsonObject;
                if (methods == null) continue;

                foreach (var method in methods)
                {
                    var operation = method.Value as JsonObject;
                    if (operation == null) continue;

                    object operationId;
                    if (!operation.TryGetValue("operationId", out operationId) ||
                        string.IsNullOrWhiteSpace(operationId as string))
                    {
                        issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "missing_operation_id",
                            "The " + method.Key.ToUpperInvariant() + " operation on '" + path.Key +
                            "' has no operationId. AgentCore uses operationId as the MCP tool name and " +
                            "requires it on every exposed operation."));
                    }

                    CheckContentTypes(operation, path.Key, issues);
                }
            }
        }

        private static void CheckContentTypes(JsonObject operation, string path, List<GatewayIssue> issues)
        {
            object rawBody;
            var body = operation.TryGetValue("requestBody", out rawBody) ? rawBody as JsonObject : null;
            if (body == null) return;

            object rawContent;
            var content = body.TryGetValue("content", out rawContent) ? rawContent as JsonObject : null;
            if (content == null) return;

            foreach (var mediaType in content.Keys)
            {
                if (mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) continue;

                issues.Add(new GatewayIssue(GatewayIssueSeverity.Warning, "unsupported_media_type",
                    "'" + path + "' accepts '" + mediaType + "'. Only application/json is fully " +
                    "supported for AgentCore OpenAPI targets."));
            }
        }

        private static void CheckForbiddenKeys(JsonObject document, List<GatewayIssue> issues)
        {
            foreach (var key in ForbiddenDocumentKeys)
            {
                if (!ContainsKeyAnywhere(document, key)) continue;

                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "specification_level_security",
                    "The document declares '" + key + "'. AgentCore does not support security schemes " +
                    "at the specification level; outbound authentication must be configured on the " +
                    "gateway target's credential provider instead."));
            }
        }

        private static void CheckUnsupportedKeywords(JsonObject document, List<GatewayIssue> issues)
        {
            foreach (var keyword in UnsupportedSchemaKeywords)
            {
                if (!ContainsKeyAnywhere(document, keyword)) continue;

                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "unsupported_schema_keyword",
                    "The document uses '" + keyword + "', which AgentCore does not support for " +
                    "OpenAPI targets. Flatten the schema to plain types, objects and arrays."));
            }
        }

        private static void CheckToolNames(ToolCatalog catalog, AgentCoreTargetOptions options, List<GatewayIssue> issues)
        {
            var targetName = options.TargetName;
            var prefixLength = string.IsNullOrEmpty(targetName) ? 0 : targetName.Length + 3;

            if (!string.IsNullOrEmpty(targetName) && !ToolNamePattern.IsMatch(targetName))
            {
                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "invalid_target_name",
                    "Target name '" + targetName + "' contains characters outside [A-Za-z0-9_-], which " +
                    "will produce tool names a model's tool specification rejects."));
            }

            foreach (var tool in catalog.Tools)
            {
                if (!ToolNamePattern.IsMatch(tool.Name))
                {
                    issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "invalid_tool_name",
                        "Tool '" + tool.Name + "' contains characters outside [A-Za-z0-9_-]."));
                }

                var effectiveLength = prefixLength + tool.Name.Length;
                if (effectiveLength <= options.MaxToolNameLength) continue;

                var effective = string.IsNullOrEmpty(targetName)
                    ? tool.Name
                    : targetName + "___" + tool.Name;

                issues.Add(new GatewayIssue(GatewayIssueSeverity.Error, "tool_name_too_long",
                    "'" + effective + "' is " + effectiveLength + " characters, over the " +
                    options.MaxToolNameLength + "-character limit. AgentCore prefixes every tool with " +
                    "'" + (targetName ?? "<targetName>") + "___', leaving " +
                    Math.Max(0, options.MaxToolNameLength - prefixLength) + " characters for the " +
                    "operationId. Shorten it with .Named(\"...\"), or use a shorter target name. " +
                    "This fails at invocation, not at target creation."));
            }
        }

        private static void CheckDoublePrefixing(AgentCoreTargetOptions options, List<GatewayIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(options.TargetName) || string.IsNullOrWhiteSpace(options.CatalogNamePrefix))
                return;

            var target = options.TargetName.Replace("-", "_").Replace(".", "_");
            if (!target.Equals(options.CatalogNamePrefix, StringComparison.OrdinalIgnoreCase)) return;

            issues.Add(new GatewayIssue(GatewayIssueSeverity.Warning, "double_prefixed_tool_names",
                "The catalog name prefix '" + options.CatalogNamePrefix + "' matches the gateway target " +
                "name, so tools appear as '" + options.TargetName + "___" + options.CatalogNamePrefix +
                "_...'. AgentCore already namespaces by target name — clear the name prefix and let it " +
                "do that, which also frees up the tool-name budget."));
        }

        /// <summary>Depth-first search for a key anywhere in the document, including inside arrays.</summary>
        private static bool ContainsKeyAnywhere(object node, string key)
        {
            var pairs = node as IEnumerable<KeyValuePair<string, object>>;
            if (pairs != null)
            {
                foreach (var pair in pairs)
                {
                    if (string.Equals(pair.Key, key, StringComparison.Ordinal)) return true;
                    if (ContainsKeyAnywhere(pair.Value, key)) return true;
                }
                return false;
            }

            if (node is string) return false;

            var sequence = node as IEnumerable;
            if (sequence == null) return false;

            foreach (var item in sequence)
            {
                if (ContainsKeyAnywhere(item, key)) return true;
            }

            return false;
        }
    }
}
