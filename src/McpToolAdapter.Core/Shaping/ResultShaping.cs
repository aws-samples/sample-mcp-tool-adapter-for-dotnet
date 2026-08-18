// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace McpToolAdapter.Shaping
{
    /// <summary>Per-call shaping inputs.</summary>
    public sealed class ShapingContext
    {
        public ShapingContext(ToolDescriptor tool, int? maxItems)
        {
            Tool = tool;
            MaxItems = maxItems;
        }

        public ToolDescriptor Tool { get; }

        /// <summary>Maximum collection items to return, or null for unlimited.</summary>
        public int? MaxItems { get; }
    }

    /// <summary>A result rewritten into something safe and cheap to serialize.</summary>
    public sealed class ShapedResult
    {
        public ShapedResult(object payload, bool truncated = false, int? totalItems = null, int? returnedItems = null)
        {
            Payload = payload;
            Truncated = truncated;
            TotalItems = totalItems;
            ReturnedItems = returnedItems;
        }

        public object Payload { get; }

        /// <summary>True when items were dropped to respect a cap.</summary>
        public bool Truncated { get; }

        public int? TotalItems { get; }
        public int? ReturnedItems { get; }
    }

    /// <summary>
    /// Converts an awkward return value into something a caller can consume.
    /// </summary>
    /// <remarks>
    /// Implement this for legacy types that do not serialize usefully. Shapers are tried in
    /// registration order and the first match wins.
    /// </remarks>
    public interface IResultShaper
    {
        bool CanShape(object value);
        ShapedResult Shape(object value, ShapingContext context);
    }

    /// <summary>
    /// Flattens <c>System.Data.DataTable</c> and <c>DataSet</c> into plain rows.
    /// </summary>
    /// <remarks>
    /// Reached reflectively by type name so this assembly needs no <c>System.Data</c> reference.
    /// Registered by default because ADO.NET-era code returns these constantly, and their default
    /// serialization is either unusable or enormous.
    /// </remarks>
    public sealed class DataTableShaper : IResultShaper
    {
        public bool CanShape(object value)
        {
            return value != null && (IsNamed(value.GetType(), "System.Data.DataTable") ||
                                     IsNamed(value.GetType(), "System.Data.DataSet"));
        }

        public ShapedResult Shape(object value, ShapingContext context)
        {
            if (IsNamed(value.GetType(), "System.Data.DataSet"))
            {
                var tables = (IEnumerable)Read(value, "Tables");
                var result = new JsonObject();
                var anyTruncated = false;
                var total = 0;
                var returned = 0;

                foreach (var table in tables)
                {
                    var shaped = ShapeTable(table, context);
                    result[Convert.ToString(Read(table, "TableName"), CultureInfo.InvariantCulture)] = shaped.Payload;
                    anyTruncated |= shaped.Truncated;
                    total += shaped.TotalItems ?? 0;
                    returned += shaped.ReturnedItems ?? 0;
                }

                return new ShapedResult(result, anyTruncated, total, returned);
            }

            return ShapeTable(value, context);
        }

        private static ShapedResult ShapeTable(object table, ShapingContext context)
        {
            var columnNames = ((IEnumerable)Read(table, "Columns"))
                .Cast<object>()
                .Select(c => Convert.ToString(Read(c, "ColumnName"), CultureInfo.InvariantCulture))
                .ToList();

            var allRows = ((IEnumerable)Read(table, "Rows")).Cast<object>().ToList();
            var take = context.MaxItems.HasValue ? Math.Min(context.MaxItems.Value, allRows.Count) : allRows.Count;

            var rows = new List<object>(take);
            for (var i = 0; i < take; i++)
            {
                var values = (object[])Read(allRows[i], "ItemArray");
                var row = new JsonObject();
                for (var c = 0; c < columnNames.Count && c < values.Length; c++)
                    row[columnNames[c]] = values[c] is DBNull ? null : values[c];
                rows.Add(row);
            }

            return new ShapedResult(rows.ToArray(), take < allRows.Count, allRows.Count, take);
        }

        private static object Read(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                throw new InvalidOperationException(
                    "Expected property '" + propertyName + "' on " + target.GetType().FullName + ".");
            return property.GetValue(target, null);
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
    }

    /// <summary>
    /// Runs registered shapers, then enforces the collection item cap.
    /// </summary>
    /// <remarks>
    /// The cap is not a nicety. A legacy <c>GetAllCustomers()</c> returning 50,000 rows will
    /// exhaust the calling model's context window and fail the whole conversation, not just the
    /// call. Truncating and saying so is strictly better than returning everything.
    /// </remarks>
    public sealed class ResultShapingPipeline
    {
        private readonly IReadOnlyList<IResultShaper> _shapers;
        private readonly int _maxDepth;

        public ResultShapingPipeline(IEnumerable<IResultShaper> shapers, int maxPayloadDepth = 12)
        {
            _shapers = (shapers ?? Enumerable.Empty<IResultShaper>()).ToList();
            _maxDepth = maxPayloadDepth;
        }

        /// <summary>
        /// Runs shapers, enforces the item cap, then normalizes to JSON primitives so the payload
        /// is host-independent and safe to serialize.
        /// </summary>
        public ShapedResult Shape(object value, ShapingContext context)
        {
            var shaped = ShapeWithoutNormalizing(value, context);
            var normalizer = new JsonPayloadNormalizer(_maxDepth, context.MaxItems);

            return new ShapedResult(
                normalizer.Normalize(shaped.Payload),
                shaped.Truncated,
                shaped.TotalItems,
                shaped.ReturnedItems);
        }

        /// <summary>Shaping and capping only, leaving CLR types intact. Exposed for tests.</summary>
        internal ShapedResult ShapeWithoutNormalizing(object value, ShapingContext context)
        {
            if (value == null) return new ShapedResult(null);

            foreach (var shaper in _shapers)
            {
                if (shaper.CanShape(value)) return shaper.Shape(value, context);
            }

            return Cap(value, context);
        }

        private static ShapedResult Cap(object value, ShapingContext context)
        {
            if (value is string) return new ShapedResult(value);

            var sequence = value as IEnumerable;
            if (sequence == null) return new ShapedResult(value);

            var items = new List<object>();
            var total = 0;
            var limit = context.MaxItems;

            foreach (var item in sequence)
            {
                total++;
                if (!limit.HasValue || items.Count < limit.Value) items.Add(item);
            }

            var truncated = limit.HasValue && total > items.Count;
            return new ShapedResult(items.ToArray(), truncated, total, items.Count);
        }
    }
}
