// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Data;
using McpToolAdapter.Shaping;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class NormalizerTests
    {
        private readonly JsonPayloadNormalizer _normalizer = new JsonPayloadNormalizer();

        [Fact]
        public void FormatsDatesAsIso8601RatherThanTheLegacyJavaScriptSerializerFormat()
        {
            var value = new DateTime(2026, 8, 11, 14, 30, 0, DateTimeKind.Utc);

            Assert.Equal("2026-08-11T14:30:00.0000000Z", _normalizer.Normalize(value));
        }

        [Fact]
        public void RendersEnumsAsNames()
        {
            Assert.Equal("Shipped", _normalizer.Normalize(OrderStatus.Shipped));
        }

        [Fact]
        public void RendersGuidsAndUrisAsStrings()
        {
            var id = Guid.NewGuid();

            Assert.Equal(id.ToString(), _normalizer.Normalize(id));
            Assert.Equal("https://example.com/x", _normalizer.Normalize(new Uri("https://example.com/x")));
        }

        [Fact]
        public void EncodesByteArraysAsBase64()
        {
            Assert.Equal("AQID", _normalizer.Normalize(new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void ConvertsDbNullToNull()
        {
            Assert.Null(_normalizer.Normalize(DBNull.Value));
        }

        [Fact]
        public void ExpandsObjectsIntoJsonObjects()
        {
            var order = new Order { Id = 5, CustomerEmail = "a@b.com", Total = 9.99m, Status = OrderStatus.Pending };

            var result = Assert.IsType<JsonObject>(_normalizer.Normalize(order));

            Assert.Equal(5, result["Id"]);
            Assert.Equal("a@b.com", result["CustomerEmail"]);
            Assert.Equal(9.99m, result["Total"]);
        }

        [Fact]
        public void KeepsDictionariesAsObjectsRatherThanArraysOfPairs()
        {
            var source = new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" };

            var result = Assert.IsType<JsonObject>(_normalizer.Normalize(source));

            Assert.Equal(1, result["a"]);
            Assert.Equal("two", result["b"]);
        }

        [Fact]
        public void KeepsJsonObjectAsAnObject()
        {
            // JsonObject enumerates as key-value pairs, so a sequence check must not see it first.
            var source = new JsonObject { ["k"] = "v" };

            var result = Assert.IsType<JsonObject>(_normalizer.Normalize(source));

            Assert.Equal("v", result["k"]);
        }

        [Fact]
        public void TerminatesOnCircularReferences()
        {
            var parent = new RecursiveNode { Name = "parent" };
            parent.Child = parent;

            var result = Assert.IsType<JsonObject>(_normalizer.Normalize(parent));

            Assert.Equal("parent", result["Name"]);
            Assert.Contains("circular reference", (string)result["Child"]);
        }

        [Fact]
        public void StopsAtTheDepthLimit()
        {
            var shallow = new JsonPayloadNormalizer(maxDepth: 2);
            var value = new LevelOne { Two = new LevelTwo { Three = new LevelThree { Value = "deep" } } };

            var result = (JsonObject)shallow.Normalize(value);
            var two = (JsonObject)result["Two"];

            Assert.Contains("not expanded", (string)two["Three"]);
        }

        [Fact]
        public void CapsNestedCollections()
        {
            var capped = new JsonPayloadNormalizer(maxItems: 2);

            var result = (object[])capped.Normalize(new[] { 1, 2, 3, 4, 5 });

            Assert.Equal(2, result.Length);
        }

        [Fact]
        public void SubstitutesAPlaceholderWhenAGetterThrows()
        {
            var result = Assert.IsType<JsonObject>(_normalizer.Normalize(new ThrowingGetter()));

            Assert.Equal("fine", result["Good"]);
            Assert.Contains("unreadable", (string)result["Bad"]);
        }

        [Fact]
        public void FlattensADataTableEvenWithoutAShaperRegistered()
        {
            var table = new DataTable("T");
            table.Columns.Add("N", typeof(int));
            table.Rows.Add(1);

            var result = (object[])_normalizer.Normalize(table);

            Assert.Single(result);
            Assert.Equal(1, ((JsonObject)result[0])["N"]);
        }

        private sealed class ThrowingGetter
        {
            public string Good
            {
                get { return "fine"; }
            }

            public string Bad
            {
                get { throw new InvalidOperationException("connection closed"); }
            }
        }
    }

    public class JsonWriterTests
    {
        [Fact]
        public void WritesPrimitives()
        {
            Assert.Equal("null", Json.Write(null));
            Assert.Equal("true", Json.Write(true));
            Assert.Equal("false", Json.Write(false));
            Assert.Equal("42", Json.Write(42));
            Assert.Equal("42", Json.Write(42L));
            Assert.Equal("1.5", Json.Write(1.5));
            Assert.Equal("9.99", Json.Write(9.99m));
        }

        [Fact]
        public void UsesInvariantCultureForNumbers()
        {
            // A machine with a comma decimal separator must still emit valid JSON.
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                Assert.Equal("1.5", Json.Write(1.5));
                Assert.Equal("9.99", Json.Write(9.99m));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void SubstitutesNullForNaNAndInfinityWhichJsonCannotRepresent()
        {
            Assert.Equal("null", Json.Write(double.NaN));
            Assert.Equal("null", Json.Write(double.PositiveInfinity));
        }

        [Fact]
        public void EscapesControlCharactersAndQuotes()
        {
            Assert.Equal("\"a\\\"b\"", Json.Write("a\"b"));
            Assert.Equal("\"a\\\\b\"", Json.Write("a\\b"));
            Assert.Equal("\"a\\nb\"", Json.Write("a\nb"));
            Assert.Equal("\"a\\u0001b\"", Json.Write("a\u0001b"));
            Assert.Equal("\"a\\u2028b\"", Json.Write("a\u2028b"));
        }

        [Fact]
        public void PreservesInsertionOrderOfObjectKeys()
        {
            var value = new JsonObject { ["z"] = 1, ["a"] = 2, ["m"] = 3 };

            Assert.Equal("{\"z\":1,\"a\":2,\"m\":3}", Json.Write(value));
        }

        [Fact]
        public void WritesNestedObjectsAndArrays()
        {
            var value = new JsonObject
            {
                ["ok"] = true,
                ["items"] = new object[] { 1, "two", null },
                ["nested"] = new JsonObject { ["k"] = "v" }
            };

            Assert.Equal("{\"ok\":true,\"items\":[1,\"two\",null],\"nested\":{\"k\":\"v\"}}", Json.Write(value));
        }

        [Fact]
        public void WritesTheDispatchEnvelope()
        {
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, string>(s => s.Describe(default(int), default(string)))));

            var envelope = new Dispatch.ToolDispatcher(catalog)
                .Invoke("describe", new Dictionary<string, object> { ["id"] = 1, ["note"] = "hi" })
                .ToEnvelope();

            var json = Json.Write(envelope);

            Assert.StartsWith("{\"ok\":true,\"tool\":\"describe\",\"result\":\"1/hi\"", json);
            Assert.Contains("\"durationMs\":", json);
        }
    }
}
