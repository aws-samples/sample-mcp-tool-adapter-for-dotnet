// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace McpToolAdapter.Schema
{
    /// <summary>
    /// Maps CLR types onto JSON Schema.
    /// </summary>
    /// <remarks>
    /// Two modes, deliberately asymmetric:
    /// <list type="bullet">
    /// <item><description><see cref="ForInput"/> is strict. If a parameter type cannot be
    /// described and bound reliably it throws, and the failure surfaces at application start
    /// rather than at call time.</description></item>
    /// <item><description><see cref="ForOutput"/> is lenient. Legacy return types are frequently
    /// loose (<c>object</c>, <c>DataTable</c>, deep graphs); an imprecise result schema is
    /// acceptable because nothing binds against it.</description></item>
    /// </list>
    /// </remarks>
    public sealed class JsonSchemaGenerator
    {
        private const string DataTableTypeName = "System.Data.DataTable";
        private const string DataSetTypeName = "System.Data.DataSet";

        private readonly int _maxDepth;

        public JsonSchemaGenerator(int maxDepth = 5)
        {
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
            _maxDepth = maxDepth;
        }

        /// <summary>Schema for a value the caller supplies. Throws on types that cannot be bound.</summary>
        public JsonObject ForInput(Type type)
        {
            return Generate(type, strict: true, depth: 0, path: new List<Type>());
        }

        /// <summary>Schema for a value the method returns. Degrades to a permissive schema rather than throwing.</summary>
        public JsonObject ForOutput(Type type)
        {
            return Generate(type, strict: false, depth: 0, path: new List<Type>());
        }

        /// <summary>
        /// True when <paramref name="type"/> can appear as a bindable parameter. Used by catalog
        /// validation to report every offending parameter in one pass.
        /// </summary>
        public bool IsBindable(Type type, out string reason)
        {
            try
            {
                ForInput(type);
                reason = null;
                return true;
            }
            catch (SchemaGenerationException ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private JsonObject Generate(Type type, bool strict, int depth, List<Type> path)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return Generate(underlying, strict, depth, path);

            if (type.IsByRef)
                throw new SchemaGenerationException("by-ref type '" + Describe(type) + "' cannot be represented in JSON.");

            var primitive = TryPrimitive(type);
            if (primitive != null) return primitive;

            if (type.IsEnum) return Enum(type);

            if (type == typeof(byte[]))
                return new JsonObject { ["type"] = "string", ["format"] = "byte" };

            // Duck-typed on name so this assembly needs no System.Data reference. DataTable is
            // ubiquitous in WebForms-era code and its shape is only known at runtime.
            if (IsNamed(type, DataTableTypeName) || IsNamed(type, DataSetTypeName))
            {
                if (strict)
                    throw new SchemaGenerationException(
                        "'" + Describe(type) + "' cannot be an input parameter; its shape is not known " +
                        "until runtime. Accept explicit parameters instead.");

                return new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Tabular rows. Column names and types are determined at runtime.",
                    ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
                };
            }

            Type dictionaryValue;
            if (TryDictionary(type, out dictionaryValue))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = depth >= _maxDepth
                        ? (object)true
                        : Generate(dictionaryValue, strict, depth + 1, path)
                };
            }

            Type element;
            if (TryEnumerable(type, out element))
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = depth >= _maxDepth
                        ? (object)new JsonObject()
                        : Generate(element, strict, depth + 1, path)
                };
            }

            if (type == typeof(object))
            {
                if (strict)
                    throw new SchemaGenerationException(
                        "'object' is too loose to bind. Declare a concrete parameter type.");
                return new JsonObject();
            }

            if (typeof(Delegate).IsAssignableFrom(type) || typeof(Type).IsAssignableFrom(type))
                throw new SchemaGenerationException("'" + Describe(type) + "' cannot be represented in JSON.");

            if (strict && (type.IsInterface || type.IsAbstract))
                throw new SchemaGenerationException(
                    "'" + Describe(type) + "' is abstract or an interface, so no instance can be constructed " +
                    "for binding. Use a concrete type.");

            if (strict && type.GetConstructor(Type.EmptyTypes) == null)
                throw new SchemaGenerationException(
                    "'" + Describe(type) + "' has no public parameterless constructor, so it cannot be " +
                    "constructed during binding.");

            return Complex(type, strict, depth, path);
        }

        private JsonObject Complex(Type type, bool strict, int depth, List<Type> path)
        {
            if (path.Contains(type))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Recursive reference to " + Describe(type) + "; not expanded."
                };
            }

            if (depth >= _maxDepth)
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Nested beyond the configured schema depth of " + _maxDepth + "; not expanded."
                };
            }

            var properties = new JsonObject();

            path.Add(type);
            try
            {
                foreach (var member in ReadableMembers(type))
                {
                    JsonObject memberSchema;
                    try
                    {
                        memberSchema = Generate(member.Type, strict, depth + 1, path);
                    }
                    catch (SchemaGenerationException ex)
                    {
                        throw new SchemaGenerationException(
                            Describe(type) + "." + member.Name + ": " + ex.Message);
                    }

                    properties[member.Name] = memberSchema;
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }

            // No `required` list on a nested object, deliberately.
            //
            // A property is set after the object is constructed, so an omitted one keeps whatever the
            // type initialises it to — `public int Page { get; set; } = 1` stays 1. Marking non-nullable
            // value types as required would force a caller to supply a value the type already has a
            // sensible answer for, and AgentCore enforces `required` strictly: it rejected a real call
            // with "Missing required field(s): '/search/Page'" for exactly this reason.
            //
            // Method *parameters* are different and still marked required, because an omitted argument
            // there has no initialiser to fall back on unless the parameter is optional. See
            // ToolCatalog.BuildInputSchema.
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            schema["additionalProperties"] = false;
            return schema;
        }

        internal static IEnumerable<MemberDescriptor> ReadableMembers(Type type)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                yield return new MemberDescriptor(property.Name, property.PropertyType, property, null);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new MemberDescriptor(field.Name, field.FieldType, null, field);
            }
        }

        private static JsonObject Enum(Type type)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = System.Enum.GetNames(type).Cast<object>().ToArray()
            };
        }

        private static JsonObject TryPrimitive(Type type)
        {
            if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
            if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            if (type == typeof(char)) return new JsonObject { ["type"] = "string", ["maxLength"] = 1 };

            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong))
                return new JsonObject { ["type"] = "integer" };

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new JsonObject { ["type"] = "number" };

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return new JsonObject { ["type"] = "string", ["format"] = "date-time" };

            if (type == typeof(Guid))
                return new JsonObject { ["type"] = "string", ["format"] = "uuid" };

            if (type == typeof(TimeSpan))
                return new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Duration, formatted as [-][d.]hh:mm:ss[.fffffff]."
                };

            if (type == typeof(Uri))
                return new JsonObject { ["type"] = "string", ["format"] = "uri" };

            return null;
        }

        private static bool TryDictionary(Type type, out Type valueType)
        {
            valueType = null;
            foreach (var candidate in Interfaces(type))
            {
                if (!candidate.IsGenericType) continue;
                if (candidate.GetGenericTypeDefinition() != typeof(IDictionary<,>)) continue;

                var args = candidate.GetGenericArguments();
                if (args[0] != typeof(string)) continue;
                valueType = args[1];
                return true;
            }
            return false;
        }

        private static bool TryEnumerable(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;

            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            foreach (var candidate in Interfaces(type))
            {
                if (!candidate.IsGenericType) continue;
                if (candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }

            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                elementType = typeof(object);
                return true;
            }

            return false;
        }

        private static IEnumerable<Type> Interfaces(Type type)
        {
            if (type.IsInterface) yield return type;
            foreach (var i in type.GetInterfaces()) yield return i;
        }

        private static bool IsNamed(Type type, string fullName)
        {
            while (type != null && type != typeof(object))
            {
                if (type.FullName == fullName) return true;
                type = type.BaseType;
            }
            return false;
        }

        internal static string Describe(Type type)
        {
            if (type == null) return "<null>";
            if (!type.IsGenericType) return type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Describe)) + ">";
        }

        internal sealed class MemberDescriptor
        {
            public MemberDescriptor(string name, Type type, PropertyInfo property, FieldInfo field)
            {
                Name = name;
                Type = type;
                Property = property;
                Field = field;
            }

            public string Name { get; }
            public Type Type { get; }
            public PropertyInfo Property { get; }
            public FieldInfo Field { get; }

            public bool CanWrite
            {
                get { return Field != null || (Property != null && Property.CanWrite); }
            }

            public void SetValue(object target, object value)
            {
                if (Field != null) Field.SetValue(target, value);
                else if (Property != null && Property.CanWrite) Property.SetValue(target, value, null);
            }
        }
    }
}
