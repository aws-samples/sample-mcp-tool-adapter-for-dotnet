// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Hosting;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class HostingTests
    {
        // Deliberately self-describing rather than random-looking. It has to stay a compile-time
        // constant because it is a default parameter value below, so it cannot be generated — and a
        // 32-character hex string in a source file is indistinguishable from a real credential to a
        // secret scanner, which then cries wolf on every scan. This says what it is instead.
        // Only the 32-character minimum in McpEndpointOptions matters here.
        private const string Secret = "test-fixture-not-a-real-credential-0000";

        private static McpEndpointOptions Options(Action<McpEndpointOptions> customize = null)
        {
            var options = new McpEndpointOptions
            {
                Enabled = true,
                SharedSecret = Secret,
                NamePrefix = null
            };
            if (customize != null) customize(options);
            return options;
        }

        private static McpRequestProcessor Processor(
            McpEndpointOptions options = null,
            Action<ToolAuditEntry> audit = null,
            IMcpAuthorizer authorizer = null)
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b =>
                {
                    b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)));
                    b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))).Mutating();
                }));

            var effective = options ?? Options();
            var dispatcher = new ToolDispatcher(catalog, new ToolDispatcherOptions
            {
                AllowMutatingTools = effective.AllowMutating,
                IncludeExceptionDetail = effective.IncludeExceptionDetail,
                Audit = audit
            });

            return new McpRequestProcessor(dispatcher, effective, new FakeJsonParser(), authorizer);
        }

        private static McpRequest Request(
            string method, string path, string body = null, string key = Secret,
            bool secure = true, string remoteIp = null)
        {
            var headers = new Dictionary<string, string>();
            if (key != null) headers[McpEndpointOptions.ApiKeyHeader] = key;

            return new McpRequest(method, path, headers, body, remoteIp, secure);
        }

        [Fact]
        public void IgnoresPathsOutsideItsBaseSoTheApplicationPipelineContinues()
        {
            Assert.Null(Processor().TryHandle(Request("GET", "/Default.aspx")));
            Assert.Null(Processor().TryHandle(Request("GET", "/")));
            Assert.Null(Processor().TryHandle(Request("GET", "/_mcpsomethingelse")));
        }

        [Fact]
        public void ClaimsItsOwnBasePathAndEverythingUnderIt()
        {
            Assert.NotNull(Processor().TryHandle(Request("GET", "/_mcp/health")));
            Assert.NotNull(Processor().TryHandle(Request("GET", "/_mcp")));
        }

        [Fact]
        public void LooksAbsentWhenDisabledRatherThanAdvertisingItself()
        {
            var response = Processor(Options(o => o.Enabled = false)).TryHandle(Request("GET", "/_mcp/health"));

            Assert.Equal(404, response.StatusCode);
        }

        [Fact]
        public void NeedsNoSharedSecretWhenTheAuthorizerDoesNotUseOne()
        {
            // The case that motivated moving the requirement onto the authorizer: an application using
            // OAuth bearer tokens has no shared secret, and previously had to invent one to be served
            // at all.
            var response = Processor(
                    Options(o => o.SharedSecret = null),
                    authorizer: new AlwaysAllowAuthorizer())
                .TryHandle(Request("GET", "/_mcp/health", key: null));

            Assert.Equal(200, response.StatusCode);
        }

        [Fact]
        public void RefusesEveryRequestWhenTheAuthorizerReportsAConfigurationProblem()
        {
            var response = Processor(
                    Options(),
                    authorizer: new BrokenAuthorizer())
                .TryHandle(Request("GET", "/_mcp/health"));

            Assert.Equal(503, response.StatusCode);
            Assert.Contains("misconfigured", response.Body);
        }

        [Fact]
        public void RefusesEveryRequestWhenEnabledWithoutASharedSecret()
        {
            var response = Processor(Options(o => o.SharedSecret = null)).TryHandle(Request("GET", "/_mcp/health", key: null));

            Assert.Equal(503, response.StatusCode);
            Assert.Contains("misconfigured", response.Body);
        }

        [Fact]
        public void RefusesASharedSecretThatIsTooShortToBeWorthHaving()
        {
            var response = Processor(Options(o => o.SharedSecret = "short")).TryHandle(Request("GET", "/_mcp/health", key: "short"));

            Assert.Equal(503, response.StatusCode);
        }

        [Fact]
        public void RejectsMissingCredentials()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/health", key: null));

            Assert.Equal(401, response.StatusCode);
            Assert.Contains("missing_credentials", response.Body);
        }

        [Fact]
        public void RejectsAWrongKey()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/health", key: new string('x', 32)));

            Assert.Equal(401, response.StatusCode);
            Assert.Contains("invalid_credentials", response.Body);
        }

        [Fact]
        public void RejectsPlainHttpByDefault()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/health", secure: false));

            Assert.Equal(403, response.StatusCode);
            Assert.Contains("insecure_transport", response.Body);
        }

        [Fact]
        public void AllowsPlainHttpOnlyWhenExplicitlyPermitted()
        {
            var response = Processor(Options(o => o.AllowInsecureTransport = true))
                .TryHandle(Request("GET", "/_mcp/health", secure: false));

            Assert.Equal(200, response.StatusCode);
        }

        [Fact]
        public void EnforcesTheAddressAllowlistWhenConfigured()
        {
            var options = Options(o => o.AllowedIpAddresses.Add("10.0.0.5"));

            Assert.Equal(403, Processor(options).TryHandle(Request("GET", "/_mcp/health", remoteIp: "10.0.0.9")).StatusCode);
            Assert.Equal(200, Processor(options).TryHandle(Request("GET", "/_mcp/health", remoteIp: "10.0.0.5")).StatusCode);
        }

        [Fact]
        public void ServesTheOpenApiDocument()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/openapi.json"));

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("\"openapi\":\"3.0.3\"", response.Body);
            Assert.Contains("\"operationId\":\"get_order_by_id\"", response.Body);
            Assert.Contains("/_mcp/tools/get_order_by_id", response.Body);
        }

        [Fact]
        public void OpenApiPathsFollowAConfiguredBasePath()
        {
            var response = Processor(Options(o => o.BasePath = "/internal/ops"))
                .TryHandle(Request("GET", "/internal/ops/openapi.json"));

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("/internal/ops/tools/get_order_by_id", response.Body);
        }

        [Fact]
        public void ServesADiagnosticToolListing()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/tools"));

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("get_order_by_id", response.Body);
            Assert.Contains("OrderService.GetOrderById", response.Body);
        }

        [Fact]
        public void InvokesAToolFromAJsonBody()
        {
            var response = Processor().TryHandle(
                Request("POST", "/_mcp/tools/get_order_by_id", "{\"id\":77}"));

            Assert.Equal(200, response.StatusCode);
            Assert.Contains("\"ok\":true", response.Body);
            Assert.Contains("\"Id\":77", response.Body);
        }

        [Fact]
        public void RejectsGetOnAToolPathWithAUsefulMessage()
        {
            var response = Processor().TryHandle(Request("GET", "/_mcp/tools/get_order_by_id"));

            Assert.Equal(405, response.StatusCode);
            Assert.Contains("Use POST", response.Body);
        }

        [Fact]
        public void ReturnsNotFoundForAnUnknownTool()
        {
            var response = Processor().TryHandle(Request("POST", "/_mcp/tools/nope", "{}"));

            Assert.Equal(404, response.StatusCode);
        }

        [Fact]
        public void ReturnsBadRequestForAMalformedBody()
        {
            var response = Processor().TryHandle(Request("POST", "/_mcp/tools/get_order_by_id", "not json"));

            Assert.Equal(400, response.StatusCode);
            Assert.Contains("malformed_body", response.Body);
        }

        [Fact]
        public void ReturnsBadRequestForInvalidArguments()
        {
            var response = Processor().TryHandle(Request("POST", "/_mcp/tools/get_order_by_id", "{}"));

            Assert.Equal(400, response.StatusCode);
            Assert.Contains("missing required argument", response.Body);
        }

        [Fact]
        public void ReturnsForbiddenForAMutatingToolWhileMutationIsDisabled()
        {
            var response = Processor().TryHandle(
                Request("POST", "/_mcp/tools/cancel_order", "{\"id\":1,\"reason\":\"x\"}"));

            Assert.Equal(403, response.StatusCode);
            Assert.Contains("mutation_disabled", response.Body);
        }

        [Fact]
        public void RunsAMutatingToolOnceMutationIsEnabled()
        {
            var response = Processor(Options(o => o.AllowMutating = true)).TryHandle(
                Request("POST", "/_mcp/tools/cancel_order", "{\"id\":1,\"reason\":\"x\"}"));

            Assert.Equal(200, response.StatusCode);
        }

        [Fact]
        public void AcceptsAnEmptyBodyAsNoArguments()
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.ExposeStatic<int>(() => Maths.Add(default(int), default(int)))));
            var processor = new McpRequestProcessor(
                new ToolDispatcher(catalog), Options(), new FakeJsonParser());

            var response = processor.TryHandle(Request("POST", "/_mcp/tools/add", null));

            // No arguments supplied, so binding reports both as missing rather than crashing.
            Assert.Equal(400, response.StatusCode);
            Assert.Contains("missing required argument", response.Body);
        }

        [Fact]
        public void RecordsCallerAndCorrelationIdInTheAuditTrail()
        {
            ToolAuditEntry captured = null;
            var headers = new Dictionary<string, string>
            {
                [McpEndpointOptions.ApiKeyHeader] = Secret,
                [McpEndpointOptions.CorrelationHeader] = "trace-99"
            };

            Processor(audit: e => captured = e).TryHandle(
                new McpRequest("POST", "/_mcp/tools/get_order_by_id", headers, "{\"id\":1}", "10.0.0.1", true));

            Assert.NotNull(captured);
            Assert.Equal("shared-secret", captured.Caller);
            Assert.Equal("trace-99", captured.CorrelationId);
        }

        [Fact]
        public void HidesLegacyExceptionTextFromTheResponseBody()
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, string>(s => s.Boom())));
            var processor = new McpRequestProcessor(
                new ToolDispatcher(catalog), Options(), new FakeJsonParser());

            var response = processor.TryHandle(Request("POST", "/_mcp/tools/boom", "{}"));

            Assert.Equal(500, response.StatusCode);
            Assert.DoesNotContain("hunter2", response.Body);
            Assert.DoesNotContain("Server=", response.Body);
        }

        [Theory]
        [InlineData("/_MCP/HEALTH")]
        [InlineData("/_mcp/health/")]
        public void MatchesPathsCaseInsensitivelyAndIgnoresATrailingSlash(string path)
        {
            Assert.Equal(200, Processor().TryHandle(Request("GET", path)).StatusCode);
        }

        [Fact]
        public void StripsAQueryStringBeforeRouting()
        {
            Assert.Equal(200, Processor().TryHandle(Request("GET", "/_mcp/health?debug=1")).StatusCode);
        }

        [Fact]
        public void NormalizesTheTildePrefixedPathsThatSystemWebProduces()
        {
            Assert.Equal(200, Processor().TryHandle(Request("GET", "~/_mcp/health")).StatusCode);
        }

        [Theory]
        [InlineData("abc", "abc", true)]
        [InlineData("abc", "abd", false)]
        [InlineData("abc", "ab", false)]
        [InlineData("", "", true)]
        [InlineData(null, "abc", false)]
        public void FixedTimeComparisonMatchesOrdinaryEquality(string left, string right, bool expected)
        {
            Assert.Equal(expected, SharedSecretAuthorizer.FixedTimeEquals(left, right));
        }

        /// <summary>
        /// Minimal stand-in for the host's parser. Understands flat objects with integer, string,
        /// boolean and null values, which is all these tests need.
        /// </summary>
        private sealed class FakeJsonParser : IJsonObjectParser
        {
            public IDictionary<string, object> ParseObject(string json)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(json)) return result;

                var text = json.Trim();
                if (!text.StartsWith("{") || !text.EndsWith("}"))
                    throw new FormatException("Not a JSON object.");

                var inner = text.Substring(1, text.Length - 2).Trim();
                if (inner.Length == 0) return result;

                foreach (var pair in inner.Split(','))
                {
                    var separator = pair.IndexOf(':');
                    if (separator < 0) throw new FormatException("Not a JSON object.");

                    var key = pair.Substring(0, separator).Trim().Trim('"');
                    var raw = pair.Substring(separator + 1).Trim();

                    if (raw == "null") result[key] = null;
                    else if (raw == "true") result[key] = true;
                    else if (raw == "false") result[key] = false;
                    else if (raw.StartsWith("\"")) result[key] = raw.Trim('"');
                    else
                    {
                        int number;
                        if (!int.TryParse(raw, out number)) throw new FormatException("Unsupported value.");
                        result[key] = number;
                    }
                }

                return result;
            }
        }

        /// <summary>Carries no credential requirement, like a bearer-token authorizer with a valid setup.</summary>
        private sealed class AlwaysAllowAuthorizer : IMcpAuthorizer
        {
            public IReadOnlyList<string> ConfigurationProblems { get; } = new string[0];

            public McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions options)
            {
                return McpAuthorizationResult.Allow("bearer-token");
            }
        }

        /// <summary>Reports a problem, so the endpoint must refuse everything rather than serve.</summary>
        private sealed class BrokenAuthorizer : IMcpAuthorizer
        {
            public IReadOnlyList<string> ConfigurationProblems { get; } =
                new[] { "No discovery URL was configured." };

            public McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions options)
            {
                throw new InvalidOperationException("Must never be reached while misconfigured.");
            }
        }

    }
}
