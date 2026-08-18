// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Linq;
using McpToolAdapter.Schema;
using Xunit;

namespace McpToolAdapter.Tests
{
    public class SchemaTests
    {
        private readonly JsonSchemaGenerator _generator = new JsonSchemaGenerator();

        [Theory]
        [InlineData(typeof(string), "string")]
        [InlineData(typeof(bool), "boolean")]
        [InlineData(typeof(int), "integer")]
        [InlineData(typeof(long), "integer")]
        [InlineData(typeof(decimal), "number")]
        [InlineData(typeof(double), "number")]
        public void MapsPrimitives(Type clrType, string expected)
        {
            Assert.Equal(expected, _generator.ForInput(clrType)["type"]);
        }

        [Fact]
        public void MapsDateTimeToFormattedString()
        {
            var schema = _generator.ForInput(typeof(DateTime));
            Assert.Equal("string", schema["type"]);
            Assert.Equal("date-time", schema["format"]);
        }

        [Fact]
        public void MapsEnumToStringWithNames()
        {
            var schema = _generator.ForInput(typeof(OrderStatus));

            Assert.Equal("string", schema["type"]);
            Assert.Equal(new object[] { "Pending", "Shipped", "Cancelled" }, (object[])schema["enum"]);
        }

        [Fact]
        public void UnwrapsNullable()
        {
            Assert.Equal("integer", _generator.ForInput(typeof(int?))["type"]);
        }

        [Fact]
        public void MapsCollectionsToArrays()
        {
            var schema = _generator.ForInput(typeof(List<string>));

            Assert.Equal("array", schema["type"]);
            Assert.Equal("string", ((JsonObject)schema["items"])["type"]);
        }

        [Fact]
        public void MapsStringKeyedDictionaryToOpenObject()
        {
            var schema = _generator.ForInput(typeof(Dictionary<string, int>));

            Assert.Equal("object", schema["type"]);
            Assert.Equal("integer", ((JsonObject)schema["additionalProperties"])["type"]);
        }

        [Fact]
        public void ExpandsComplexTypeProperties()
        {
            var schema = _generator.ForInput(typeof(OrderQuery));
            var properties = (JsonObject)schema["properties"];

            Assert.Equal("object", schema["type"]);
            Assert.True(properties.ContainsKey("CustomerEmail"));
            Assert.True(properties.ContainsKey("Status"));
            Assert.True(properties.ContainsKey("Take"));
            Assert.Equal(false, schema["additionalProperties"]);
        }

        [Fact]
        public void DoesNotMarkNestedObjectMembersAsRequired()
        {
            // An omitted property keeps whatever the type initialises it to, so requiring it would
            // force callers to supply a value the type already answers for. AgentCore enforces
            // `required` strictly and rejected a real call over precisely this.
            var schema = _generator.ForInput(typeof(OrderQuery));

            Assert.False(schema.ContainsKey("required"));
            Assert.Equal(false, schema["additionalProperties"]);
        }

        [Fact]
        public void StillMarksNonOptionalMethodParametersAsRequired()
        {
            // Parameters are the opposite case: no initialiser to fall back on.
            var catalog = ToolCatalog.Build(
                new ToolCatalogOptions { RequireDescriptions = false },
                new LambdaRegistry(b => b.Expose<OrderService, string>(
                    s => s.Describe(default(int), default(string)))));

            var schema = catalog.Tools.Single().InputSchema;

            Assert.Equal(new object[] { "id" }, (object[])schema["required"]);
        }

        [Fact]
        public void StopsAtRecursiveReferenceInsteadOfLooping()
        {
            var schema = _generator.ForInput(typeof(RecursiveNode));
            var child = (JsonObject)((JsonObject)schema["properties"])["Child"];

            Assert.Equal("object", child["type"]);
            Assert.Contains("Recursive reference", (string)child["description"]);
        }

        [Fact]
        public void StopsExpandingBeyondConfiguredDepth()
        {
            var shallow = new JsonSchemaGenerator(maxDepth: 2);

            var schema = shallow.ForInput(typeof(LevelOne));
            var two = (JsonObject)((JsonObject)schema["properties"])["Two"];
            var three = (JsonObject)((JsonObject)two["properties"])["Three"];

            Assert.Contains("schema depth", (string)three["description"]);
        }

        [Fact]
        public void RejectsObjectAsInputBecauseItCannotBeBound()
        {
            var ex = Assert.Throws<SchemaGenerationException>(() => _generator.ForInput(typeof(object)));
            Assert.Contains("too loose", ex.Message);
        }

        [Fact]
        public void AllowsObjectAsOutput()
        {
            Assert.Empty(_generator.ForOutput(typeof(object)));
        }

        [Fact]
        public void RejectsInterfaceAsInput()
        {
            var ex = Assert.Throws<SchemaGenerationException>(() => _generator.ForInput(typeof(IComparable)));
            Assert.Contains("abstract or an interface", ex.Message);
        }

        [Fact]
        public void RejectsInputTypeWithoutParameterlessConstructor()
        {
            var ex = Assert.Throws<SchemaGenerationException>(() => _generator.ForInput(typeof(NeedsConstructorArgs)));
            Assert.Contains("no public parameterless constructor", ex.Message);
        }

        [Fact]
        public void ReportsTheOffendingMemberPathWhenNestedTypeFails()
        {
            var ex = Assert.Throws<SchemaGenerationException>(() => _generator.ForInput(typeof(HasLooseMember)));

            Assert.Contains("HasLooseMember.Anything", ex.Message);
        }

        [Fact]
        public void IsBindableReportsReasonWithoutThrowing()
        {
            string reason;

            Assert.True(_generator.IsBindable(typeof(int), out reason));
            Assert.Null(reason);

            Assert.False(_generator.IsBindable(typeof(object), out reason));
            Assert.NotNull(reason);
        }

        private sealed class HasLooseMember
        {
            public object Anything { get; set; }
        }
    }
}
