// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using McpToolAdapter.Invocation;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class BindingTests
    {
        private readonly ArgumentBinder _binder = new ArgumentBinder();

        private static ToolDescriptor Tool(Action<IToolBuilder> configure)
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(configure));
            return catalog.Tools.Single();
        }

        private static ToolDescriptor GetOrderTool()
        {
            return Tool(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))));
        }

        [Fact]
        public void BindsExactTypes()
        {
            var bound = _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["id"] = 7 });

            Assert.Equal(new object[] { 7 }, bound);
        }

        [Fact]
        public void CoercesStringifiedNumbers()
        {
            // Models routinely send scalars as strings; rejecting that produces retry loops.
            var bound = _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["id"] = "7" });

            Assert.Equal(7, bound[0]);
        }

        [Fact]
        public void MatchesArgumentNamesCaseInsensitively()
        {
            var bound = _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["ID"] = 3 });

            Assert.Equal(3, bound[0]);
        }

        [Fact]
        public void ReportsMissingRequiredArgument()
        {
            var ex = Assert.Throws<ArgumentBindingException>(
                () => _binder.Bind(GetOrderTool(), new Dictionary<string, object>()));

            Assert.Contains("missing required argument 'id'", ex.Message);
        }

        [Fact]
        public void RejectsUnknownArguments()
        {
            var ex = Assert.Throws<ArgumentBindingException>(
                () => _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["id"] = 1, ["nope"] = 2 }));

            Assert.Contains("unknown argument(s): nope", ex.Message);
        }

        [Fact]
        public void ReportsEveryFailureAtOnce()
        {
            var tool = Tool(b => b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))));

            var ex = Assert.Throws<ArgumentBindingException>(
                () => _binder.Bind(tool, new Dictionary<string, object> { ["extra"] = 1 }));

            Assert.Equal(3, ex.Errors.Count);
            Assert.Contains(ex.Errors, e => e.Contains("missing required argument 'id'"));
            Assert.Contains(ex.Errors, e => e.Contains("missing required argument 'reason'"));
            Assert.Contains(ex.Errors, e => e.Contains("unknown argument"));
        }

        [Fact]
        public void UsesDeclaredDefaultForOmittedOptionalArgument()
        {
            var tool = Tool(b => b.Expose<OrderService, string>(s => s.Describe(default(int), default(string))));

            var bound = _binder.Bind(tool, new Dictionary<string, object> { ["id"] = 5 });

            Assert.Equal(5, bound[0]);
            Assert.Equal("none", bound[1]);
        }

        [Fact]
        public void BindsNestedObjectsAndCollections()
        {
            var tool = Tool(b => b.Expose<OrderService, System.Collections.Generic.IList<Order>>(
                s => s.Search(default(OrderQuery))));

            var bound = _binder.Bind(tool, new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object>
                {
                    ["CustomerEmail"] = "a@b.com",
                    ["Status"] = "Shipped",
                    ["Take"] = "2",
                    ["Tags"] = new object[] { "urgent", "vip" }
                }
            });

            var query = Assert.IsType<OrderQuery>(bound[0]);
            Assert.Equal("a@b.com", query.CustomerEmail);
            Assert.Equal(OrderStatus.Shipped, query.Status);
            Assert.Equal(2, query.Take);
            Assert.Equal(new[] { "urgent", "vip" }, query.Tags);
        }

        [Fact]
        public void ReportsUnknownNestedPropertyWithItsPath()
        {
            var tool = Tool(b => b.Expose<OrderService, System.Collections.Generic.IList<Order>>(
                s => s.Search(default(OrderQuery))));

            var ex = Assert.Throws<ArgumentBindingException>(() => _binder.Bind(tool, new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["Nope"] = 1 }
            }));

            Assert.Contains("query.Nope", ex.Message);
        }

        [Fact]
        public void ReportsInvalidEnumValueWithValidOptions()
        {
            var tool = Tool(b => b.Expose<OrderService, System.Collections.Generic.IList<Order>>(
                s => s.Search(default(OrderQuery))));

            var ex = Assert.Throws<ArgumentBindingException>(() => _binder.Bind(tool, new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["Status"] = "Exploded" }
            }));

            Assert.Contains("not a valid OrderStatus", ex.Message);
            Assert.Contains("Pending, Shipped, Cancelled", ex.Message);
        }

        [Fact]
        public void RejectsNullForNonNullableValueType()
        {
            var ex = Assert.Throws<ArgumentBindingException>(
                () => _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["id"] = null }));

            Assert.Contains("null is not valid", ex.Message);
        }

        [Fact]
        public void RejectsUnconvertibleValueRatherThanDefaulting()
        {
            var ex = Assert.Throws<ArgumentBindingException>(
                () => _binder.Bind(GetOrderTool(), new Dictionary<string, object> { ["id"] = "not-a-number" }));

            Assert.Contains("cannot convert", ex.Message);
        }

        [Fact]
        public void SuppliesCancellationTokenWithoutRequiringItFromTheCaller()
        {
            var tool = Tool(b => b.Expose<OrderService, int>(s => s.SumWithToken(default(int), default(int), default(System.Threading.CancellationToken))));

            Assert.Equal(2, tool.Parameters.Count);
            Assert.DoesNotContain(tool.Parameters, p => p.Name == "cancellationToken");

            var bound = _binder.Bind(tool, new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 });
            Assert.Equal(3, bound.Length);
            Assert.Equal(System.Threading.CancellationToken.None, bound[2]);
        }
    }
}
