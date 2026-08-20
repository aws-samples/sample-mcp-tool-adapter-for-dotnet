// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Hosting;
using Xunit;

namespace McpToolAdapter.Tests
{
    /// <summary>
    /// Covers the seam that carries an end user's identity into the invocation — the mechanism that
    /// lets an application's own authorization checks keep working when a gateway calls it.
    /// </summary>
    public class InvocationScopeTests
    {
        private sealed class RecordingScope : IDisposable
        {
            public static readonly List<string> Events = new List<string>();

            public RecordingScope(string caller)
            {
                Events.Add("enter:" + caller);
            }

            public void Dispose()
            {
                Events.Add("exit");
            }
        }

        private static ToolDispatcher Dispatcher(ToolDispatcherOptions options, Action<IToolBuilder> configure = null)
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(configure ?? (b =>
                    b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))))));

            return new ToolDispatcher(catalog, options);
        }

        [Fact]
        public void EntersTheScopeBeforeInvokingAndExitsAfter()
        {
            RecordingScope.Events.Clear();
            var dispatcher = Dispatcher(new ToolDispatcherOptions
            {
                InvocationScope = context => new RecordingScope(context.Caller)
            });

            dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 1 },
                new ToolCallContext("alice"));

            Assert.Equal(new[] { "enter:alice", "exit" }, RecordingScope.Events);
        }

        [Fact]
        public void ExitsTheScopeEvenWhenTheMethodThrows()
        {
            // A leaked principal would attach a caller's identity to the rest of the request.
            RecordingScope.Events.Clear();
            var dispatcher = Dispatcher(
                new ToolDispatcherOptions { InvocationScope = c => new RecordingScope(c.Caller) },
                b => b.Expose<OrderService, string>(s => s.Boom()));

            var result = dispatcher.Invoke("boom", new Dictionary<string, object>(), new ToolCallContext("bob"));

            Assert.False(result.IsSuccess);
            Assert.Equal(new[] { "enter:bob", "exit" }, RecordingScope.Events);
        }

        [Fact]
        public void ToleratesAScopeFactoryThatDeclinesToEstablishAnything()
        {
            var dispatcher = Dispatcher(new ToolDispatcherOptions { InvocationScope = _ => null });

            var result = dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 5 },
                new ToolCallContext("carol"));

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void WorksWithNoScopeConfiguredAtAll()
        {
            var result = Dispatcher(new ToolDispatcherOptions())
                .Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 5 });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ScopeSeesTheClaimsTheAuthorizerEstablished()
        {
            IReadOnlyDictionary<string, string> observed = null;
            var dispatcher = Dispatcher(new ToolDispatcherOptions
            {
                InvocationScope = context =>
                {
                    observed = context.Claims;
                    return null;
                }
            });

            dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 1 },
                new ToolCallContext("alice", null, new Dictionary<string, string>
                {
                    ["sub"] = "alice@example.com",
                    ["roles"] = "reader approver"
                }));

            Assert.Equal("alice@example.com", observed["sub"]);
            Assert.Equal("reader approver", observed["roles"]);
        }

        [Fact]
        public void ClaimsDefaultToEmptyRatherThanNull()
        {
            var context = new ToolCallContext("service-account");

            Assert.NotNull(context.Claims);
            Assert.Empty(context.Claims);
        }

        [Fact]
        public void AuthorizerClaimsReachTheInvocationThroughTheProcessor()
        {
            // End to end through the HTTP layer: authorizer -> context -> scope.
            IReadOnlyDictionary<string, string> observed = null;

            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int)))));

            var dispatcher = new ToolDispatcher(catalog, new ToolDispatcherOptions
            {
                InvocationScope = context =>
                {
                    observed = context.Claims;
                    return null;
                }
            });

            var processor = new McpRequestProcessor(
                dispatcher,
                new McpEndpointOptions { Enabled = true, SharedSecret = new string('k', 32) },
                new PassThroughParser(),
                new StubAuthorizer());

            var response = processor.TryHandle(new McpRequest(
                "POST", "/_mcp/tools/get_order_by_id", new Dictionary<string, string>(), "{\"id\":1}",
                "10.0.0.1", true));

            Assert.Equal(200, response.StatusCode);
            Assert.Equal("dave@example.com", observed["sub"]);
        }

        private sealed class StubAuthorizer : IMcpAuthorizer
        {
            // Nothing to configure, so nothing can be misconfigured.
            public IReadOnlyList<string> ConfigurationProblems { get; } = new string[0];

            public McpAuthorizationResult Authorize(McpRequest request, McpEndpointOptions options)
            {
                return McpAuthorizationResult.Allow("dave@example.com", new Dictionary<string, string>
                {
                    ["sub"] = "dave@example.com"
                });
            }
        }

        private sealed class PassThroughParser : IJsonObjectParser
        {
            public IDictionary<string, object> ParseObject(string json)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["id"] = 1 };
            }
        }
    }
}
