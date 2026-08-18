// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;

namespace McpToolAdapter.OpenApi
{
    public sealed class OpenApiOptions
    {
        public string Title { get; set; } = "Exposed application operations";
        public string Version { get; set; } = "1.0.0";
        public string Description { get; set; }

        /// <summary>
        /// Public base URL of the host, as reachable by the gateway. Required by most OpenAPI
        /// consumers to construct request URLs.
        /// </summary>
        public string ServerUrl { get; set; }

        /// <summary>Path prefix the host serves tool invocations under.</summary>
        public string ToolPathPrefix { get; set; } = "/_mcp/tools";
    }

    /// <summary>
    /// Emits an OpenAPI 3.0 document describing the catalog.
    /// </summary>
    /// <remarks>
    /// <para>OpenAPI is the interchange format every MCP gateway already speaks — Amazon Bedrock
    /// AgentCore Gateway, Azure API Management, and the open-source OpenAPI-to-MCP bridges all
    /// consume it, each turning one <c>operationId</c> into one MCP tool. Emitting it rather than a
    /// bespoke manifest is what keeps this SDK from needing a gateway of its own.</para>
    /// <para>Every operation is a POST with a JSON body, including read-only ones. Encoding nested
    /// objects into query strings is lossy and length-limited, and a uniform shape keeps the two
    /// hosts identical. Read-only operations are marked with <c>x-mutating: false</c> and say so in
    /// their description.</para>
    /// <para>Schemas are inlined rather than referenced through <c>components</c>. The document is
    /// larger, but it survives consumers with incomplete <c>$ref</c> resolution.</para>
    /// </remarks>
    public sealed class OpenApiDocumentBuilder
    {
        public JsonObject Build(ToolCatalog catalog, OpenApiOptions options = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            options = options ?? new OpenApiOptions();

            var info = new JsonObject
            {
                ["title"] = options.Title,
                ["version"] = options.Version
            };
            if (!string.IsNullOrWhiteSpace(options.Description)) info["description"] = options.Description;

            var document = new JsonObject
            {
                ["openapi"] = "3.0.3",
                ["info"] = info
            };

            if (!string.IsNullOrWhiteSpace(options.ServerUrl))
            {
                document["servers"] = new object[]
                {
                    new JsonObject { ["url"] = options.ServerUrl.TrimEnd('/') }
                };
            }

            var paths = new JsonObject();
            var prefix = "/" + (options.ToolPathPrefix ?? string.Empty).Trim('/');

            foreach (var tool in catalog.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                paths[prefix + "/" + tool.Name] = new JsonObject { ["post"] = Operation(tool) };

            document["paths"] = paths;
            return document;
        }

        private static JsonObject Operation(ToolDescriptor tool)
        {
            var operation = new JsonObject
            {
                ["operationId"] = tool.Name,
                ["summary"] = Summarize(tool.Description),
                ["description"] = tool.IsMutating
                    ? tool.Description + " This operation changes state."
                    : tool.Description + " This operation is read-only.",
                ["x-mutating"] = tool.IsMutating,
                ["requestBody"] = new JsonObject
                {
                    ["required"] = tool.Parameters.Any(p => !p.IsOptional),
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = tool.InputSchema }
                    }
                },
                ["responses"] = Responses(tool)
            };

            return operation;
        }

        private static JsonObject Responses(ToolDescriptor tool)
        {
            var successSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ok"] = new JsonObject { ["type"] = "boolean" },
                    ["tool"] = new JsonObject { ["type"] = "string" },
                    ["result"] = tool.ResultSchema,
                    ["truncated"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Present and true when items were omitted to respect the result cap."
                    },
                    ["totalItems"] = new JsonObject { ["type"] = "integer" },
                    ["returnedItems"] = new JsonObject { ["type"] = "integer" },
                    ["truncationNotice"] = new JsonObject { ["type"] = "string" },
                    ["durationMs"] = new JsonObject { ["type"] = "integer" }
                },
                ["required"] = new object[] { "ok", "tool" }
            };

            return new JsonObject
            {
                ["200"] = Response("The operation completed.", successSchema),
                ["400"] = Response("The arguments were missing, unknown or unconvertible.", ErrorSchema()),
                ["403"] = Response("The operation changes state and mutating operations are disabled.", ErrorSchema()),
                ["404"] = Response("No such operation.", ErrorSchema()),
                ["500"] = Response("The operation failed.", ErrorSchema())
            };
        }

        private static JsonObject Response(string description, JsonObject schema)
        {
            return new JsonObject
            {
                ["description"] = description,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = schema }
                }
            };
        }

        private static JsonObject ErrorSchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["ok"] = new JsonObject { ["type"] = "boolean" },
                    ["tool"] = new JsonObject { ["type"] = "string" },
                    ["error"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string" },
                            ["message"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new object[] { "code", "message" }
                    }
                },
                ["required"] = new object[] { "ok", "error" }
            };
        }

        private static string Summarize(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;

            var stop = description.IndexOf(". ", StringComparison.Ordinal);
            var firstSentence = stop > 0 ? description.Substring(0, stop + 1) : description;
            return firstSentence.Length > 120 ? firstSentence.Substring(0, 117) + "..." : firstSentence;
        }
    }
}
