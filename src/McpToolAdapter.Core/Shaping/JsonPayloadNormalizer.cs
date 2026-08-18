// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using McpToolAdapter.Schema;

namespace McpToolAdapter.Shaping
{
    /// <summary>
    /// Rewrites an arbitrary CLR object graph into JSON primitives: <see cref="JsonObject"/>,
    /// <c>object[]</c>, string, bool, number, or null.
    /// </summary>
    /// <remarks>
    /// <para>Normalizing before serialization rather than trusting the host's serializer buys three
    /// things that matter here.</para>
    /// <para>Dates come out as ISO 8601. The <c>JavaScriptSerializer</c> available in
    /// <c>System.Web.Extensions</c> — the only serializer a .NET Framework host can use without
    /// taking a dependency — emits <c>\/Date(1234)\/</c>, which downstream consumers misread.</para>
    /// <para>Cycles terminate. Legacy DTOs with parent/child back-references are common, and a
    /// naive reflective serializer either recurses until the stack dies or emits an unbounded
    /// document. Reference identity along the current path is tracked, and a revisit becomes a
    /// placeholder.</para>
    /// <para>Both hosts emit byte-identical output, because the shape is decided here rather than by
    /// whichever serializer the host happens to have.</para>
    /// </remarks>
    public sealed class JsonPayloadNormalizer
    {
        private const string DataTableTypeName = "System.Data.DataTable";

        private readonly int _maxDepth;
        private readonly int? _maxItems;

        public JsonPayloadNormalizer(int maxDepth = 12, int? maxItems = null)
        {
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
            _maxDepth = maxDepth;
            _maxItems = maxItems;
        }

        public object Normalize(object value)
        {
            return Visit(value, 0, new HashSet<object>(ReferenceComparer.Instance));
        }

        private object Visit(object value, int depth, HashSet<object> path)
        {
            if (value == null || value is DBNull) return null;

            var type = value.GetType();

            if (value is string || value is bool) return value;
            if (type.IsEnum) return value.ToString();

            if (value is DateTime dateTime) return dateTime.ToString("o", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateTimeOffset) return dateTimeOffset.ToString("o", CultureInfo.InvariantCulture);
            if (value is TimeSpan timeSpan) return timeSpan.ToString(null, CultureInfo.InvariantCulture);
            if (value is Guid guid) return guid.ToString();
            if (value is Uri uri) return uri.ToString();
            if (value is byte[] bytes) return Convert.ToBase64String(bytes);
            if (value is char) return value.ToString();

            if (type.IsPrimitive || value is decimal) return value;

            if (depth >= _maxDepth)
                return "<not expanded: nesting exceeded " + _maxDepth + " levels>";

            // Reference types can form cycles; value types cannot, and tracking them would
            // wrongly collapse equal-by-reference boxed copies.
            var tracked = !type.IsValueType;
            if (tracked)
            {
                if (path.Contains(value)) return "<circular reference to " + JsonSchemaGenerator.Describe(type) + ">";
                path.Add(value);
            }

            try
            {
                // Checked before IEnumerable: JsonObject and Dictionary<string, object> both
                // enumerate as key-value pairs, and treating either as a sequence would emit an
                // array of pairs instead of an object.
                if (value is IEnumerable<KeyValuePair<string, object>> pairs)
                    return VisitPairs(pairs, depth, path);

                var dictionary = value as IDictionary;
                if (dictionary != null) return VisitDictionary(dictionary, depth, path);

                if (value is IEnumerable sequence) return VisitSequence(sequence, depth, path);

                return VisitObject(value, type, depth, path);
            }
            finally
            {
                if (tracked) path.Remove(value);
            }
        }

        private object VisitPairs(IEnumerable<KeyValuePair<string, object>> pairs, int depth, HashSet<object> path)
        {
            var result = new JsonObject();
            var count = 0;

            foreach (var pair in pairs)
            {
                if (_maxItems.HasValue && count >= _maxItems.Value) break;
                var key = pair.Key ?? string.Empty;
                if (!result.ContainsKey(key)) result[key] = Visit(pair.Value, depth + 1, path);
                count++;
            }

            return result;
        }

        private object VisitDictionary(IDictionary dictionary, int depth, HashSet<object> path)
        {
            var result = new JsonObject();
            var count = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                if (_maxItems.HasValue && count >= _maxItems.Value) break;
                var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!result.ContainsKey(key)) result[key] = Visit(entry.Value, depth + 1, path);
                count++;
            }

            return result;
        }

        private object VisitSequence(IEnumerable sequence, int depth, HashSet<object> path)
        {
            var items = new List<object>();

            foreach (var item in sequence)
            {
                if (_maxItems.HasValue && items.Count >= _maxItems.Value) break;
                items.Add(Visit(item, depth + 1, path));
            }

            return items.ToArray();
        }

        private object VisitObject(object value, Type type, int depth, HashSet<object> path)
        {
            // Reached only for a DataTable that no shaper handled; without this it would serialize
            // as its internal plumbing rather than as rows.
            if (IsNamed(type, DataTableTypeName))
            {
                var shaped = new DataTableShaper().Shape(value, new ShapingContext(null, _maxItems));
                return Visit(shaped.Payload, depth + 1, path);
            }

            var result = new JsonObject();

            foreach (var member in JsonSchemaGenerator.ReadableMembers(type))
            {
                object memberValue;
                try
                {
                    memberValue = member.Property != null
                        ? member.Property.GetValue(value, null)
                        : member.Field.GetValue(value);
                }
                catch (Exception ex)
                {
                    // Legacy getters do throw — lazy loading against a closed connection, for
                    // instance. One bad property must not fail the whole call.
                    result[member.Name] = "<unreadable: " + ex.GetType().Name + ">";
                    continue;
                }

                result[member.Name] = Visit(memberValue, depth + 1, path);
            }

            return result;
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

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
