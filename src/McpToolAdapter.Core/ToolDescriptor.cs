// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections.Generic;
using System.Reflection;

namespace McpToolAdapter
{
    /// <summary>One bindable parameter of an exposed method.</summary>
    public sealed class ToolParameter
    {
        internal ToolParameter(string name, Type type, int position, bool isOptional, object defaultValue, string description, JsonObject schema)
        {
            Name = name;
            Type = type;
            Position = position;
            IsOptional = isOptional;
            DefaultValue = defaultValue;
            Description = description;
            Schema = schema;
        }

        public string Name { get; }
        public Type Type { get; }

        /// <summary>Index in the underlying method's parameter list.</summary>
        public int Position { get; }

        /// <summary>True when the caller may omit this argument.</summary>
        public bool IsOptional { get; }

        public object DefaultValue { get; }
        public string Description { get; }
        public JsonObject Schema { get; }
    }

    /// <summary>
    /// A validated, immutable description of one exposed method: how to describe it to a caller
    /// and how to invoke it. Produced by <see cref="ToolCatalog"/>; never constructed directly.
    /// </summary>
    public sealed class ToolDescriptor
    {
        internal ToolDescriptor(
            string name,
            string description,
            MethodInfo method,
            bool isMutating,
            IReadOnlyList<ToolParameter> parameters,
            JsonObject inputSchema,
            JsonObject resultSchema,
            int? maxResultItems,
            Func<object> instanceFactory,
            Func<object, object[], object> invoker)
        {
            Name = name;
            Description = description;
            Method = method;
            IsMutating = isMutating;
            Parameters = parameters;
            InputSchema = inputSchema;
            ResultSchema = resultSchema;
            MaxResultItems = maxResultItems;
            InstanceFactory = instanceFactory;
            Invoker = invoker;
        }

        /// <summary>
        /// Stable identifier. Becomes the OpenAPI <c>operationId</c> and therefore the MCP tool
        /// name a model sees, so treat it as a published contract: renaming it breaks callers.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Human-readable purpose. This is the single highest-leverage field in the whole
        /// descriptor: it is what a model reads to decide whether to call this tool at all.
        /// </summary>
        public string Description { get; }

        public MethodInfo Method { get; }

        /// <summary>True when the method changes state. Non-mutating tools can be served while
        /// mutations are globally disabled.</summary>
        public bool IsMutating { get; }

        public IReadOnlyList<ToolParameter> Parameters { get; }

        /// <summary>JSON Schema for the argument object.</summary>
        public JsonObject InputSchema { get; }

        /// <summary>JSON Schema for the result payload. Advisory; nothing binds against it.</summary>
        public JsonObject ResultSchema { get; }

        /// <summary>Per-tool cap on returned collection items, overriding the catalog default.</summary>
        public int? MaxResultItems { get; }

        /// <summary>Supplies the target instance; null for static methods.</summary>
        internal Func<object> InstanceFactory { get; }

        /// <summary>Compiled delegate. Reflection is used to discover methods, never to call them.</summary>
        internal Func<object, object[], object> Invoker { get; }

        public bool IsStatic
        {
            get { return Method.IsStatic; }
        }

        public override string ToString()
        {
            return Name + " -> " + Method.DeclaringType?.Name + "." + Method.Name;
        }
    }
}
