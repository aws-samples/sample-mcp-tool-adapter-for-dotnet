// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class CatalogTests
    {
        private static ToolCatalog Build(Action<IToolBuilder> configure, ToolCatalogOptions options = null)
        {
            return ToolCatalog.Build(options ?? new ToolCatalogOptions(), new LambdaRegistry(configure));
        }

        [Fact]
        public void BuildsToolFromExpressionRegistration()
        {
            var catalog = Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("Fetch one order."));

            var tool = catalog.Tools.Single();
            Assert.Equal("get_order_by_id", tool.Name);
            Assert.Equal("Fetch one order.", tool.Description);
            Assert.False(tool.IsMutating);
            Assert.Equal("id", tool.Parameters.Single().Name);
        }

        [Fact]
        public void AppliesNamePrefixSoToolsFromDifferentApplicationsDoNotCollide()
        {
            var catalog = Build(
                b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Describes("x"),
                new ToolCatalogOptions { NamePrefix = "OrderApp" });

            Assert.Equal("order_app_get_order_by_id", catalog.Tools.Single().Name);
        }

        [Fact]
        public void HonoursExplicitName()
        {
            var catalog = Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Named("fetch_order")
                .Describes("x"));

            Assert.Equal("fetch_order", catalog.Tools.Single().Name);
        }

        [Fact]
        public void ExposesStaticMethodsWithoutAnInstance()
        {
            var catalog = Build(b => b.ExposeStatic<int>(() => Maths.Add(default(int), default(int)))
                .Describes("Add two numbers."));

            Assert.True(catalog.Tools.Single().IsStatic);
        }

        [Fact]
        public void MarksMutatingTools()
        {
            var catalog = Build(b => b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string)))
                .Describes("Cancel an order.")
                .Mutating());

            Assert.True(catalog.Tools.Single().IsMutating);
        }

        [Fact]
        public void RequiresDescriptionByDefault()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))));

            Assert.Contains(ex.Errors, e => e.Contains("no description"));
        }

        [Fact]
        public void RejectsDuplicateToolNamesNamingBothSites()
        {
            var ex = Assert.Throws<ToolRegistrationException>(() => Build(b =>
            {
                b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))).Named("dup").Describes("a");
                b.Expose<OrderService, string>(s => s.Describe(default(int), default(string))).Named("dup").Describes("b");
            }));

            Assert.Contains(ex.Errors, e => e.Contains("duplicate tool name 'dup'"));
        }

        [Fact]
        public void RejectsOutParameters()
        {
            // Registered by name because C# forbids out arguments inside expression trees.
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose(typeof(OrderService), "TrySomething").Describes("x")));

            Assert.Contains(ex.Errors, e => e.Contains("out/ref parameter"));
        }

        [Fact]
        public void RejectsGenericMethods()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose(typeof(OrderService), "Echo").Describes("x")));

            Assert.Contains(ex.Errors, e => e.Contains("generic methods cannot be exposed"));
        }

        [Fact]
        public void RejectsUnbindableParameterTypeAndSaysWhich()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose<OrderService, object>(s => s.Loose(default(object))).Describes("x")));

            Assert.Contains(ex.Errors, e => e.Contains("parameter 'anything'"));
        }

        [Fact]
        public void RejectsTargetThatCannotBeConstructedAndSuggestsTheFix()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose<NeedsConstructorArgs, string>(s => s.Read()).Describes("x")));

            Assert.Contains(ex.Errors, e => e.Contains(".Using(() => ...)"));
        }

        [Fact]
        public void AcceptsTargetWithExplicitInstanceFactory()
        {
            var catalog = Build(b => b.Expose<NeedsConstructorArgs, string>(s => s.Read())
                .Describes("x")
                .Using(() => new NeedsConstructorArgs("configured")));

            Assert.Single(catalog.Tools);
        }

        [Fact]
        public void ReportsEveryProblemInOnePassRatherThanFailingOnTheFirst()
        {
            var ex = Assert.Throws<ToolRegistrationException>(() => Build(b =>
            {
                b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)));            // no description
                b.Expose<OrderService, object>(s => s.Loose(default(object))).Describes("x"); // unbindable
                b.Expose<NeedsConstructorArgs, string>(s => s.Read()).Describes("y");         // unconstructable
            }));

            Assert.Equal(3, ex.Errors.Count);
        }

        [Fact]
        public void RejectsMethodNameThatDoesNotResolve()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose(typeof(OrderService), "NoSuchMethod").Describes("x")));

            Assert.Contains("No public method 'NoSuchMethod'", ex.Message);
        }

        [Fact]
        public void RejectsAmbiguousNameBasedRegistrationAndPointsAtTheExpressionForm()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose(typeof(AmbiguousService), "Do").Describes("x")));

            Assert.Contains("overloads", ex.Message);
        }

        [Fact]
        public void RejectsPropertyAccessInsteadOfMethodCall()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose<OrderService, string>(s => s.LastCancelled).Describes("x")));

            Assert.Contains("Expected a method call expression", ex.Message);
        }

        [Fact]
        public void RejectsDescriptionForAParameterThatDoesNotExist()
        {
            var ex = Assert.Throws<ToolRegistrationException>(
                () => Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                    .Describes("x")
                    .Describes("nonexistent", "...")));

            Assert.Contains(ex.Errors, e => e.Contains("do not exist"));
        }

        [Fact]
        public void PutsParameterDescriptionIntoTheSchema()
        {
            var catalog = Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Describes("x")
                .Describes("id", "The numeric order identifier."));

            var properties = (JsonObject)catalog.Tools.Single().InputSchema["properties"];
            Assert.Equal("The numeric order identifier.", ((JsonObject)properties["id"])["description"]);
        }

        [Fact]
        public void BuildsInputSchemaWithRequiredArgumentsAndNoExtras()
        {
            var catalog = Build(b => b.Expose<OrderService, string>(s => s.Describe(default(int), default(string)))
                .Describes("x"));

            var schema = catalog.Tools.Single().InputSchema;
            Assert.Equal("object", schema["type"]);
            Assert.Equal(new object[] { "id" }, (object[])schema["required"]);
            Assert.Equal(false, schema["additionalProperties"]);
        }

        [Fact]
        public void FindsRegistriesByAssemblyScanning()
        {
            var catalog = ToolCatalog.BuildFromAssemblies(
                new ToolCatalogOptions(),
                typeof(DiscoverableTools).GetTypeInfo().Assembly);

            Assert.Contains(catalog.Tools, t => t.Name == "discovered_get_order");
        }

        [Fact]
        public void LooksUpToolsCaseInsensitively()
        {
            var catalog = Build(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))
                .Named("fetch_order").Describes("x"));

            ToolDescriptor tool;
            Assert.True(catalog.TryGet("FETCH_ORDER", out tool));
            Assert.False(catalog.TryGet("missing", out tool));
        }

        [Theory]
        [InlineData("GetOrderById", "get_order_by_id")]
        [InlineData("Search", "search")]
        [InlineData("GetHTTPStatus", "get_http_status")]
        [InlineData("OrderApp", "order_app")]
        [InlineData("already_snake", "already_snake")]
        [InlineData("With Spaces", "with_spaces")]
        [InlineData("ID", "id")]
        public void ConvertsNamesToSnakeCase(string input, string expected)
        {
            Assert.Equal(expected, ToolCatalog.ToSnakeCase(input));
        }

        private sealed class AmbiguousService
        {
            public int Do(int a)
            {
                return a;
            }

            public int Do(string a)
            {
                return a.Length;
            }
        }
    }
}
