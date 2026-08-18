// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using McpToolAdapter.Dispatch;
using McpToolAdapter.Shaping;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class DispatchTests
    {
        private static ToolDispatcher Dispatcher(
            Action<IToolBuilder> configure,
            ToolDispatcherOptions dispatcherOptions = null,
            ToolCatalogOptions catalogOptions = null)
        {
            var catalog = ToolCatalog.Build(
                catalogOptions ?? new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(configure));
            return new ToolDispatcher(catalog, dispatcherOptions);
        }

        [Fact]
        public void InvokesToolAndReturnsResult()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))));

            var result = dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 42 });

            // Payloads arrive normalized to JSON primitives, not as CLR types.
            var payload = Assert.IsType<JsonObject>(result.Payload);
            Assert.True(result.IsSuccess);
            Assert.Equal(42, payload["Id"]);
            Assert.Equal("Pending", payload["Status"]);
            Assert.Equal("2026-01-02T03:04:05.0000000Z", payload["PlacedUtc"]);
        }

        [Fact]
        public void InvokesStaticToolWithoutConstructingAnything()
        {
            var dispatcher = Dispatcher(b => b.ExposeStatic<int>(() => Maths.Add(default(int), default(int))));

            var result = dispatcher.Invoke("add", new Dictionary<string, object> { ["a"] = 2, ["b"] = 3 });

            Assert.True(result.IsSuccess);
            Assert.Equal(5, result.Payload);
        }

        [Fact]
        public void ReturnsUnknownToolRatherThanThrowing()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))));

            var result = dispatcher.Invoke("nope", new Dictionary<string, object>());

            Assert.False(result.IsSuccess);
            Assert.Equal(ToolErrorCodes.UnknownTool, result.ErrorCode);
        }

        [Fact]
        public void BlocksMutatingToolsByDefault()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))).Mutating());

            var result = dispatcher.Invoke("cancel_order",
                new Dictionary<string, object> { ["id"] = 1, ["reason"] = "x" });

            Assert.False(result.IsSuccess);
            Assert.Equal(ToolErrorCodes.MutationDisabled, result.ErrorCode);
        }

        [Fact]
        public void RunsMutatingToolsOnceExplicitlyEnabled()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))).Mutating(),
                new ToolDispatcherOptions { AllowMutatingTools = true });

            var result = dispatcher.Invoke("cancel_order",
                new Dictionary<string, object> { ["id"] = 1, ["reason"] = "duplicate" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ReturnsInvalidArgumentsWithDetail()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))));

            var result = dispatcher.Invoke("get_order_by_id", new Dictionary<string, object>());

            Assert.Equal(ToolErrorCodes.InvalidArguments, result.ErrorCode);
            Assert.Contains("missing required argument", result.ErrorMessage);
        }

        [Fact]
        public void HidesExceptionDetailByDefaultSoLegacyMessagesDoNotLeak()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, string>(s => s.Boom()));

            var result = dispatcher.Invoke("boom", new Dictionary<string, object>());

            Assert.Equal(ToolErrorCodes.InvocationFailed, result.ErrorCode);
            Assert.Equal("The operation failed.", result.ErrorMessage);
            Assert.DoesNotContain("hunter2", result.ErrorMessage);
        }

        [Fact]
        public void RevealsExceptionDetailWhenExplicitlyEnabled()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, string>(s => s.Boom()),
                new ToolDispatcherOptions { IncludeExceptionDetail = true });

            var result = dispatcher.Invoke("boom", new Dictionary<string, object>());

            Assert.Contains("InvalidOperationException", result.ErrorMessage);
        }

        [Fact]
        public void AuditsEveryCallIncludingFailures()
        {
            var entries = new List<ToolAuditEntry>();
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))),
                new ToolDispatcherOptions { Audit = entries.Add });

            dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 1 },
                new ToolCallContext("gateway-role", "corr-1"));
            dispatcher.Invoke("missing", new Dictionary<string, object>());

            Assert.Equal(2, entries.Count);
            Assert.True(entries[0].Succeeded);
            Assert.Equal("gateway-role", entries[0].Caller);
            Assert.Equal("corr-1", entries[0].CorrelationId);
            Assert.Equal(new[] { "id" }, entries[0].ArgumentNames);
            Assert.False(entries[1].Succeeded);
        }

        [Fact]
        public void AuditRecordsArgumentNamesButNeverValues()
        {
            ToolAuditEntry captured = null;
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, string>(s => s.Describe(default(int), default(string))),
                new ToolDispatcherOptions { Audit = e => captured = e });

            dispatcher.Invoke("describe", new Dictionary<string, object> { ["id"] = 1, ["note"] = "sensitive-value" });

            Assert.Contains("note", captured.ArgumentNames);
            Assert.DoesNotContain("sensitive-value", string.Join(",", captured.ArgumentNames));
        }

        [Fact]
        public void FailingAuditSinkDoesNotFailTheCall()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))),
                new ToolDispatcherOptions { Audit = _ => throw new InvalidOperationException("logger down") });

            var result = dispatcher.Invoke("get_order_by_id", new Dictionary<string, object> { ["id"] = 1 });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void TruncatesOversizedCollectionsAndSaysSo()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, IEnumerable<int>>(s => s.Sequence(default(int))).MaxResultItems(3),
                catalogOptions: new ToolCatalogOptions { RequireDescriptions = false });

            var result = dispatcher.Invoke("sequence", new Dictionary<string, object> { ["count"] = 100 });

            Assert.True(result.Truncated);
            Assert.Equal(100, result.TotalItems);
            Assert.Equal(3, result.ReturnedItems);
            Assert.Equal(3, ((object[])result.Payload).Length);

            var envelope = result.ToEnvelope();
            Assert.Equal(true, envelope["truncated"]);
            Assert.Contains("truncated to 3 of 100", (string)envelope["truncationNotice"]);
        }

        [Fact]
        public void AppliesCatalogWideDefaultItemCap()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService, IEnumerable<int>>(s => s.Sequence(default(int))),
                catalogOptions: new ToolCatalogOptions { RequireDescriptions = false, DefaultMaxResultItems = 2 });

            var result = dispatcher.Invoke("sequence", new Dictionary<string, object> { ["count"] = 10 });

            Assert.Equal(2, result.ReturnedItems);
        }

        [Fact]
        public void SuccessEnvelopeCarriesResultAndOmitsTruncationWhenNotTruncated()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, string>(s => s.Describe(default(int), default(string))));

            var envelope = dispatcher
                .Invoke("describe", new Dictionary<string, object> { ["id"] = 9 })
                .ToEnvelope();

            Assert.Equal(true, envelope["ok"]);
            Assert.Equal("describe", envelope["tool"]);
            Assert.Equal("9/none", envelope["result"]);
            Assert.False(envelope.ContainsKey("truncated"));
        }

        [Fact]
        public void ErrorEnvelopeCarriesCodeAndMessage()
        {
            var dispatcher = Dispatcher(b => b.Expose<OrderService, Order>(s => s.GetOrderById(default(int))));

            var envelope = dispatcher.Invoke("nope", new Dictionary<string, object>()).ToEnvelope();
            var error = (JsonObject)envelope["error"];

            Assert.Equal(false, envelope["ok"]);
            Assert.Equal(ToolErrorCodes.UnknownTool, error["code"]);
            Assert.False(envelope.ContainsKey("result"));
        }

        [Fact]
        public void VoidMethodSucceedsWithNullResult()
        {
            var dispatcher = Dispatcher(
                b => b.Expose<OrderService>(s => s.CancelOrder(default(int), default(string))).Mutating(),
                new ToolDispatcherOptions { AllowMutatingTools = true });

            var result = dispatcher.Invoke("cancel_order",
                new Dictionary<string, object> { ["id"] = 4, ["reason"] = "test" });

            Assert.True(result.IsSuccess);
            Assert.Null(result.Payload);
        }

        [Fact]
        public void FlattensDataTableIntoRowsWithColumnNames()
        {
            var table = new DataTable("Orders");
            table.Columns.Add("Id", typeof(int));
            table.Columns.Add("Email", typeof(string));
            table.Rows.Add(1, "a@example.com");
            table.Rows.Add(2, DBNull.Value);

            var pipeline = new ResultShapingPipeline(new IResultShaper[] { new DataTableShaper() });
            var shaped = pipeline.ShapeWithoutNormalizing(table, new ShapingContext(null, null));

            var rows = (object[])shaped.Payload;
            Assert.Equal(2, rows.Length);
            Assert.Equal(1, ((JsonObject)rows[0])["Id"]);
            Assert.Equal("a@example.com", ((JsonObject)rows[0])["Email"]);
            Assert.Null(((JsonObject)rows[1])["Email"]);
        }

        [Fact]
        public void TruncatesDataTableRowsToTheCap()
        {
            var table = new DataTable("Big");
            table.Columns.Add("N", typeof(int));
            for (var i = 0; i < 50; i++) table.Rows.Add(i);

            var pipeline = new ResultShapingPipeline(new IResultShaper[] { new DataTableShaper() });
            var shaped = pipeline.ShapeWithoutNormalizing(table, new ShapingContext(null, 5));

            Assert.True(shaped.Truncated);
            Assert.Equal(50, shaped.TotalItems);
            Assert.Equal(5, ((object[])shaped.Payload).Length);
        }

        [Fact]
        public void FlattensDataSetIntoTablesKeyedByName()
        {
            var set = new DataSet();
            var first = new DataTable("Alpha");
            first.Columns.Add("N", typeof(int));
            first.Rows.Add(1);
            set.Tables.Add(first);

            var pipeline = new ResultShapingPipeline(new IResultShaper[] { new DataTableShaper() });
            var shaped = pipeline.ShapeWithoutNormalizing(set, new ShapingContext(null, null));

            var payload = Assert.IsType<JsonObject>(shaped.Payload);
            Assert.True(payload.ContainsKey("Alpha"));
        }
    }
}
