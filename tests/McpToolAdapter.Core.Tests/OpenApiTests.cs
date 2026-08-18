// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using McpToolAdapter.OpenApi;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class OpenApiTests
    {
        private static JsonObject Document(Action<IToolBuilder> configure, OpenApiOptions options = null)
        {
            var catalog = ToolCatalog.Build(new ToolCatalogOptions(), new LambdaRegistry(configure));
            return new OpenApiDocumentBuilder().Build(catalog, options);
        }

        [Fact]
        public void EmitsOpenApi30WithInfo()
        {
            var document = Document(
                b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Describes("Fetch one order."),
                new OpenApiOptions { Title = "Order app", Version = "2.1.0", ServerUrl = "https://orders.internal/" });

            Assert.Equal("3.0.3", document["openapi"]);
            Assert.Equal("Order app", ((JsonObject)document["info"])["title"]);
            Assert.Equal("2.1.0", ((JsonObject)document["info"])["version"]);

            var servers = (object[])document["servers"];
            Assert.Equal("https://orders.internal", ((JsonObject)servers[0])["url"]);
        }

        [Fact]
        public void MapsEachToolToAPostOperationWhoseOperationIdIsTheToolName()
        {
            // operationId is what an OpenAPI-to-MCP gateway turns into the MCP tool name.
            var document = Document(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("Fetch one order."));

            var paths = (JsonObject)document["paths"];
            var path = paths.Keys.Single();
            Assert.Equal("/_mcp/tools/get_order_by_id", path);

            var operation = (JsonObject)((JsonObject)paths[path])["post"];
            Assert.Equal("get_order_by_id", operation["operationId"]);
        }

        [Fact]
        public void HonoursCustomToolPathPrefix()
        {
            var document = Document(
                b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Describes("x"),
                new OpenApiOptions { ToolPathPrefix = "/api/ops" });

            Assert.Equal("/api/ops/get_order_by_id", ((JsonObject)document["paths"]).Keys.Single());
        }

        [Fact]
        public void MarksMutationAndSaysSoInTheDescription()
        {
            var document = Document(b =>
            {
                b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Describes("Fetch one order.");
                b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string)))
                    .Describes("Cancel an order.").Mutating();
            });

            var paths = (JsonObject)document["paths"];
            var read = (JsonObject)((JsonObject)paths["/_mcp/tools/get_order_by_id"])["post"];
            var write = (JsonObject)((JsonObject)paths["/_mcp/tools/cancel_order"])["post"];

            Assert.Equal(false, read["x-mutating"]);
            Assert.Contains("read-only", (string)read["description"]);
            Assert.Equal(true, write["x-mutating"]);
            Assert.Contains("changes state", (string)write["description"]);
        }

        [Fact]
        public void CarriesTheInputSchemaAsTheRequestBody()
        {
            var document = Document(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("Fetch one order."));

            var operation = (JsonObject)((JsonObject)((JsonObject)document["paths"])["/_mcp/tools/get_order_by_id"])["post"];
            var body = (JsonObject)operation["requestBody"];
            var schema = (JsonObject)((JsonObject)((JsonObject)body["content"])["application/json"])["schema"];
            var properties = (JsonObject)schema["properties"];

            Assert.Equal(true, body["required"]);
            Assert.Equal("integer", ((JsonObject)properties["id"])["type"]);
        }

        [Fact]
        public void MarksRequestBodyOptionalWhenEveryArgumentIsOptional()
        {
            var document = Document(b => b.Expose<OrderService, string>(s => s.Boom()).Describes("Always fails."));

            var operation = (JsonObject)((JsonObject)((JsonObject)document["paths"])["/_mcp/tools/boom"])["post"];
            Assert.Equal(false, ((JsonObject)operation["requestBody"])["required"]);
        }

        [Fact]
        public void DescribesSuccessAndFailureResponses()
        {
            var document = Document(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("Fetch one order."));

            var operation = (JsonObject)((JsonObject)((JsonObject)document["paths"])["/_mcp/tools/get_order_by_id"])["post"];
            var responses = (JsonObject)operation["responses"];

            Assert.True(responses.ContainsKey("200"));
            Assert.True(responses.ContainsKey("400"));
            Assert.True(responses.ContainsKey("403"));
            Assert.True(responses.ContainsKey("404"));
            Assert.True(responses.ContainsKey("500"));

            var success = (JsonObject)((JsonObject)((JsonObject)((JsonObject)responses["200"])["content"])["application/json"])["schema"];
            Assert.True(((JsonObject)success["properties"]).ContainsKey("result"));
        }

        [Fact]
        public void OrdersPathsDeterministicallySoCheckedInDocumentsDiffCleanly()
        {
            var document = Document(b =>
            {
                b.Expose<OrderService, string>(s => s.Describe(default(int), default(string))).Describes("z");
                b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Describes("a");
                b.ExposeStatic<int>(() => Maths.Add(default(int), default(int))).Describes("m");
            });

            var paths = ((JsonObject)document["paths"]).Keys.ToArray();

            Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToArray(), paths);
        }

        [Fact]
        public void SummaryTakesTheFirstSentenceOfTheDescription()
        {
            var document = Document(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("Fetch one order. Additional detail that belongs in the description only."));

            var operation = (JsonObject)((JsonObject)((JsonObject)document["paths"])["/_mcp/tools/get_order_by_id"])["post"];

            Assert.Equal("Fetch one order.", operation["summary"]);
            Assert.Contains("Additional detail", (string)operation["description"]);
        }
    }
}
