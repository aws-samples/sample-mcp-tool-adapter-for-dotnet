// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using McpToolAdapter.Gateway;
using McpToolAdapter.OpenApi;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class AgentCoreCompatibilityTests
    {
        private static readonly OpenApiOptions Emit = new OpenApiOptions
        {
            Title = "Orders",
            Version = "1.0.0",
            ServerUrl = "https://orders.internal.example.com"
        };

        private static ToolCatalog Catalog(Action<IToolBuilder> configure, ToolCatalogOptions options = null)
        {
            return ToolCatalog.Build(
                options ?? new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(configure));
        }

        private static ToolCatalog DefaultCatalog(ToolCatalogOptions options = null)
        {
            return Catalog(b =>
            {
                b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)));
                b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))).Mutating();
            }, options);
        }

        private static IReadOnlyList<GatewayIssue> Check(
            ToolCatalog catalog, AgentCoreTargetOptions target = null, OpenApiOptions emit = null)
        {
            var document = new OpenApiDocumentBuilder().Build(catalog, emit ?? Emit);
            return AgentCoreCompatibility.Check(document, catalog, target);
        }

        private static IEnumerable<GatewayIssue> Errors(IReadOnlyList<GatewayIssue> issues)
        {
            return issues.Where(i => i.Severity == GatewayIssueSeverity.Error);
        }

        [Fact]
        public void TheDocumentWeEmitIsAcceptableToAgentCore()
        {
            // The point of the whole exercise: our own output passes the documented constraints.
            var issues = Check(DefaultCatalog(), new AgentCoreTargetOptions { TargetName = "orders" });

            Assert.Empty(issues);
        }

        [Fact]
        public void FlagsAMissingServerUrlBecauseAgentCoreRequiresTheRealEndpoint()
        {
            var issues = Check(DefaultCatalog(), null, new OpenApiOptions { ServerUrl = null });

            Assert.Contains(Errors(issues), i => i.Code == "missing_server_url");
        }

        [Fact]
        public void FlagsANonHttpsServerUrlAsAWarning()
        {
            var issues = Check(DefaultCatalog(), null, new OpenApiOptions { ServerUrl = "http://orders.internal" });

            Assert.Contains(issues, i => i.Code == "insecure_server_url" && i.Severity == GatewayIssueSeverity.Warning);
        }

        [Fact]
        public void FlagsATemplatedServerUrlAsAnSsrfRisk()
        {
            var issues = Check(DefaultCatalog(), null, new OpenApiOptions { ServerUrl = "https://{tenant}.example.com" });

            Assert.Contains(issues, i => i.Code == "templated_server_url");
        }

        [Fact]
        public void FlagsAToolNameThatBreachesTheBudgetOnceTheTargetPrefixIsCounted()
        {
            // 55 characters, fine alone; 'a-long-target-name___' pushes it over 64.
            var catalog = Catalog(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Named("get_order_by_its_numeric_identifier_for_the_order_system"));

            var withoutPrefix = Check(catalog);
            var withPrefix = Check(catalog, new AgentCoreTargetOptions { TargetName = "a-long-target-name" });

            Assert.Empty(withoutPrefix);
            var issue = Assert.Single(Errors(withPrefix), i => i.Code == "tool_name_too_long");
            Assert.Contains("a-long-target-name___", issue.Message);
            Assert.Contains("fails at invocation", issue.Message);
        }

        [Fact]
        public void ReportsTheRemainingBudgetSoTheFixIsObvious()
        {
            var catalog = Catalog(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Named("get_order_by_its_numeric_identifier_for_the_order_system"));

            var issue = Errors(Check(catalog, new AgentCoreTargetOptions { TargetName = "orders_target_name" }))
                .Single(i => i.Code == "tool_name_too_long");

            // 64 - (18 + 3) = 43 characters left for the operationId.
            Assert.Contains("leaving 43 characters", issue.Message);
        }

        [Fact]
        public void WarnsWhenTheCatalogPrefixDuplicatesTheTargetName()
        {
            var catalog = DefaultCatalog(new ToolCatalogOptions { RequireDescriptions = false, NamePrefix = "orderapp" });

            var issues = Check(catalog, new AgentCoreTargetOptions
            {
                TargetName = "orderapp",
                CatalogNamePrefix = "orderapp"
            });

            var issue = Assert.Single(issues, i => i.Code == "double_prefixed_tool_names");
            Assert.Equal(GatewayIssueSeverity.Warning, issue.Severity);
            Assert.Contains("orderapp___orderapp_", issue.Message);
        }

        [Fact]
        public void TreatsHyphensAndDotsInTargetNamesAsEquivalentWhenDetectingDoublePrefixing()
        {
            var catalog = DefaultCatalog(new ToolCatalogOptions { RequireDescriptions = false, NamePrefix = "order_app" });

            var issues = Check(catalog, new AgentCoreTargetOptions
            {
                TargetName = "order-app",
                CatalogNamePrefix = "order_app"
            });

            Assert.Contains(issues, i => i.Code == "double_prefixed_tool_names");
        }

        [Fact]
        public void RejectsATargetNameWithCharactersAModelToolSpecWouldReject()
        {
            var issues = Check(DefaultCatalog(), new AgentCoreTargetOptions { TargetName = "orders target!" });

            Assert.Contains(Errors(issues), i => i.Code == "invalid_target_name");
        }

        [Fact]
        public void RejectsSpecificationLevelSecuritySchemes()
        {
            // Outbound auth belongs on the gateway target's credential provider, never in the document.
            var document = new OpenApiDocumentBuilder().Build(DefaultCatalog(), Emit);
            document["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["apiKey"] = new JsonObject { ["type"] = "apiKey", ["name"] = "X-Mcp-Key", ["in"] = "header" }
                }
            };

            var issues = AgentCoreCompatibility.Check(document, DefaultCatalog());

            Assert.Contains(Errors(issues), i => i.Code == "specification_level_security");
        }

        [Theory]
        [InlineData("oneOf")]
        [InlineData("anyOf")]
        [InlineData("allOf")]
        [InlineData("$ref")]
        [InlineData("discriminator")]
        public void RejectsUnsupportedSchemaKeywordsWhereverTheyAppear(string keyword)
        {
            var catalog = DefaultCatalog();
            var document = new OpenApiDocumentBuilder().Build(catalog, Emit);

            // Bury it deep, to prove the search is not shallow.
            var paths = (JsonObject)document["paths"];
            var operation = (JsonObject)((JsonObject)paths[paths.Keys.First()])["post"];
            var body = (JsonObject)operation["requestBody"];
            var schema = (JsonObject)((JsonObject)((JsonObject)body["content"])["application/json"])["schema"];
            ((JsonObject)schema["properties"])["injected"] = new JsonObject
            {
                [keyword] = new object[] { new JsonObject { ["type"] = "string" } }
            };

            var issues = AgentCoreCompatibility.Check(document, catalog);

            Assert.Contains(Errors(issues), i => i.Code == "unsupported_schema_keyword" && i.Message.Contains(keyword));
        }

        [Fact]
        public void RejectsAnOperationWithoutAnOperationId()
        {
            var catalog = DefaultCatalog();
            var document = new OpenApiDocumentBuilder().Build(catalog, Emit);
            var paths = (JsonObject)document["paths"];
            var operation = (JsonObject)((JsonObject)paths[paths.Keys.First()])["post"];
            operation.Remove("operationId");

            var issues = AgentCoreCompatibility.Check(document, catalog);

            Assert.Contains(Errors(issues), i => i.Code == "missing_operation_id");
        }

        [Fact]
        public void RejectsAnUnsupportedOpenApiVersion()
        {
            var catalog = DefaultCatalog();
            var document = new OpenApiDocumentBuilder().Build(catalog, Emit);
            document["openapi"] = "2.0";

            Assert.Contains(
                Errors(AgentCoreCompatibility.Check(document, catalog)),
                i => i.Code == "unsupported_openapi_version");
        }

        [Fact]
        public void WarnsOnANonJsonRequestMediaType()
        {
            var catalog = DefaultCatalog();
            var document = new OpenApiDocumentBuilder().Build(catalog, Emit);
            var paths = (JsonObject)document["paths"];
            var operation = (JsonObject)((JsonObject)paths[paths.Keys.First()])["post"];
            var content = (JsonObject)((JsonObject)operation["requestBody"])["content"];
            content["application/xml"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object" } };

            Assert.Contains(
                AgentCoreCompatibility.Check(document, catalog),
                i => i.Code == "unsupported_media_type");
        }
    }

    public class ToolNameLimitTests
    {
        [Fact]
        public void RejectsAToolNameOverTheModelToolSpecLimitAtStartup()
        {
            var ex = Assert.Throws<ToolRegistrationException>(() => ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                    .Named(new string('a', 65)))));

            Assert.Contains(ex.Errors, e => e.Contains("65 characters, over the 64-character limit"));
        }

        [Fact]
        public void AcceptsANameExactlyAtTheLimit()
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                    .Named(new string('a', 64))));

            Assert.Single(catalog.Tools);
        }

        [Fact]
        public void CountsTheNamePrefixTowardTheLimit()
        {
            var ex = Assert.Throws<ToolRegistrationException>(() => ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false, NamePrefix = "a_very_long_application_prefix" },
                new LambdaRegistry(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                    .Named(new string('b', 40)))));

            Assert.Contains(ex.Errors, e => e.Contains("over the 64-character limit"));
        }

        [Fact]
        public void LimitIsConfigurableForModelsWithDifferentConstraints()
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false, MaxToolNameLength = 128 },
                new LambdaRegistry(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                    .Named(new string('a', 100))));

            Assert.Single(catalog.Tools);
        }
    }
}
