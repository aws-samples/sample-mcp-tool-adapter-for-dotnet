// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McpToolAdapter
{
    /// <summary>
    /// Writes a normalized payload tree as JSON.
    /// </summary>
    /// <remarks>
    /// <para>Small enough to hand-write, and worth hand-writing. By the time a payload reaches here
    /// <see cref="Shaping.JsonPayloadNormalizer"/> has already reduced it to primitives,
    /// <see cref="JsonObject"/> and arrays — so this needs no reflection, no type mapping and no
    /// configuration, which is exactly the part of a serializer that carries surprises.</para>
    /// <para>The alternative on .NET Framework is <c>JavaScriptSerializer</c>, which formats dates in
    /// the legacy <c>\/Date(ticks)\/</c> form and caps JSON strings at a documented default
    /// <c>MaxJsonLength</c> of 2,097,152 characters. Writing output here instead keeps the core
    /// dependency-free and makes both hosts produce identical bytes.</para>
    /// <para>Parsing is deliberately not implemented: hosts already have a parser
    /// (<c>JavaScriptSerializer</c> on .NET Framework, <c>System.Text.Json</c> on modern .NET), and
    /// a hand-written parser is where correctness bugs would actually live.</para>
    /// </remarks>
    public static class Json
    {
        public static string Write(object value)
        {
            var builder = new StringBuilder(256);
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is string text)
            {
                WriteString(builder, text);
                return;
            }

            if (value is bool flag)
            {
                builder.Append(flag ? "true" : "false");
                return;
            }

            if (value is float || value is double)
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    // Neither is representable in JSON; null is the least surprising substitute.
                    builder.Append("null");
                    return;
                }
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal decimalValue)
            {
                builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (IsIntegral(value))
            {
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            if (value is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                WriteObject(builder, pairs);
                return;
            }

            if (value is IDictionary dictionary)
            {
                var converted = new List<KeyValuePair<string, object>>(dictionary.Count);
                foreach (DictionaryEntry entry in dictionary)
                {
                    converted.Add(new KeyValuePair<string, object>(
                        Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty,
                        entry.Value));
                }
                WriteObject(builder, converted);
                return;
            }

            if (value is IEnumerable sequence)
            {
                builder.Append('[');
                var first = true;
                foreach (var item in sequence)
                {
                    if (!first) builder.Append(',');
                    WriteValue(builder, item);
                    first = false;
                }
                builder.Append(']');
                return;
            }

            // Defensive: a normalized tree never reaches this, but emitting a string beats
            // throwing while writing a response.
            WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object>> pairs)
        {
            builder.Append('{');
            var first = true;
            foreach (var pair in pairs)
            {
                if (!first) builder.Append(',');
                WriteString(builder, pair.Key ?? string.Empty);
                builder.Append(':');
                WriteValue(builder, pair.Value);
                first = false;
            }
            builder.Append('}');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        // U+2028/U+2029 are valid JSON but break naive JavaScript consumers.
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        private static bool IsIntegral(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong;
        }
    }
}
