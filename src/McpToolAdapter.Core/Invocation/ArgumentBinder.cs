// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using McpToolAdapter.Schema;

namespace McpToolAdapter.Invocation
{
    /// <summary>
    /// Binds a loosely-typed argument map (as produced by any JSON parser) onto a method's
    /// parameter array.
    /// </summary>
    /// <remarks>
    /// <para>Coercion is deliberately forgiving in one direction: a caller that sends
    /// <c>"42"</c> for an <see cref="int"/>, or <c>"pending"</c> for an enum, succeeds. Language
    /// models routinely stringify scalars, and rejecting that produces retry loops that look like
    /// tool failures. Coercion never invents data: a missing required argument or an
    /// unconvertible value is an error, never a silent default.</para>
    /// <para>All failures are collected before throwing, so a caller sees every problem at once.</para>
    /// </remarks>
    public sealed class ArgumentBinder
    {
        /// <summary>Binds <paramref name="arguments"/> for <paramref name="tool"/>.</summary>
        /// <exception cref="ArgumentBindingException">One or more arguments were missing or unconvertible.</exception>
        public object[] Bind(ToolDescriptor tool, IReadOnlyDictionary<string, object> arguments)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));

            var supplied = ToCaseInsensitive(arguments);
            var methodParameters = tool.Method.GetParameters();
            var bound = new object[methodParameters.Length];
            var errors = new List<string>();

            // Ambient parameters are not part of the caller-facing contract.
            for (var i = 0; i < methodParameters.Length; i++)
            {
                if (methodParameters[i].ParameterType == typeof(CancellationToken))
                    bound[i] = CancellationToken.None;
            }

            foreach (var parameter in tool.Parameters)
            {
                object raw;
                if (!supplied.TryGetValue(parameter.Name, out raw))
                {
                    if (parameter.IsOptional)
                    {
                        bound[parameter.Position] = parameter.DefaultValue ?? DefaultOf(parameter.Type);
                        continue;
                    }

                    errors.Add("missing required argument '" + parameter.Name + "'");
                    continue;
                }

                try
                {
                    bound[parameter.Position] = Convert(parameter.Type, raw, parameter.Name);
                }
                catch (ArgumentBindingException ex)
                {
                    errors.AddRange(ex.Errors);
                }
            }

            var unknown = supplied.Keys
                .Where(k => !tool.Parameters.Any(p => string.Equals(p.Name, k, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknown.Count > 0)
                errors.Add("unknown argument(s): " + string.Join(", ", unknown.ToArray()));

            if (errors.Count > 0) throw new ArgumentBindingException(errors);
            return bound;
        }

        private static Dictionary<string, object> ToCaseInsensitive(IReadOnlyDictionary<string, object> source)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (source == null) return result;
            foreach (var pair in source) result[pair.Key] = pair.Value;
            return result;
        }

        private object Convert(Type target, object value, string path)
        {
            var underlying = Nullable.GetUnderlyingType(target);
            if (underlying != null)
            {
                if (IsNullish(value)) return null;
                return Convert(underlying, value, path);
            }

            if (IsNullish(value))
            {
                if (target.IsValueType)
                    throw Fail(path, "null is not valid for non-nullable " + JsonSchemaGenerator.Describe(target));
                return null;
            }

            if (target == typeof(object)) return value;
            if (target.IsInstanceOfType(value) && target != typeof(string)) return value;

            if (target == typeof(string)) return AsString(value);
            if (target.IsEnum) return AsEnum(target, value, path);
            if (target == typeof(bool)) return AsBool(value, path);
            if (target == typeof(Guid)) return Parse(path, target, value, s => Guid.Parse(s));
            if (target == typeof(DateTime))
                return Parse(path, target, value, s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            if (target == typeof(DateTimeOffset))
                return Parse(path, target, value, s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            if (target == typeof(TimeSpan))
                return Parse(path, target, value, s => TimeSpan.Parse(s, CultureInfo.InvariantCulture));
            if (target == typeof(Uri))
                return Parse(path, target, value, s => new Uri(s, UriKind.RelativeOrAbsolute));
            if (target == typeof(byte[]))
                return Parse(path, target, value, System.Convert.FromBase64String);

            if (IsNumeric(target) || target == typeof(char))
            {
                try
                {
                    return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    throw Fail(path, "cannot convert " + Quote(value) + " to " + JsonSchemaGenerator.Describe(target));
                }
            }

            Type dictionaryValueType;
            if (TryDictionaryValueType(target, out dictionaryValueType))
                return AsDictionary(target, dictionaryValueType, value, path);

            Type elementType;
            if (TryElementType(target, out elementType))
                return AsCollection(target, elementType, value, path);

            return AsComplex(target, value, path);
        }

        private object AsComplex(Type target, object value, string path)
        {
            var map = value as IDictionary;
            if (map == null)
                throw Fail(path, "expected an object for " + JsonSchemaGenerator.Describe(target) +
                                 " but received " + Quote(value));

            object instance;
            try
            {
                instance = Activator.CreateInstance(target);
            }
            catch (Exception ex)
            {
                throw Fail(path, "cannot construct " + JsonSchemaGenerator.Describe(target) + ": " + ex.Message);
            }

            var members = JsonSchemaGenerator.ReadableMembers(target).Where(m => m.CanWrite).ToList();
            var errors = new List<string>();

            foreach (DictionaryEntry entry in map)
            {
                var key = System.Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                var member = members.FirstOrDefault(m => string.Equals(m.Name, key, StringComparison.OrdinalIgnoreCase));
                if (member == null)
                {
                    errors.Add(path + "." + key + ": unknown property on " + JsonSchemaGenerator.Describe(target));
                    continue;
                }

                try
                {
                    member.SetValue(instance, Convert(member.Type, entry.Value, path + "." + member.Name));
                }
                catch (ArgumentBindingException ex)
                {
                    errors.AddRange(ex.Errors);
                }
            }

            if (errors.Count > 0) throw new ArgumentBindingException(errors);
            return instance;
        }

        private object AsDictionary(Type target, Type valueType, object value, string path)
        {
            var map = value as IDictionary;
            if (map == null) throw Fail(path, "expected an object but received " + Quote(value));

            var concrete = target.IsInterface
                ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType)
                : target;

            var result = (IDictionary)Activator.CreateInstance(concrete);
            foreach (DictionaryEntry entry in map)
            {
                var key = System.Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
                result[key] = Convert(valueType, entry.Value, path + "." + key);
            }
            return result;
        }

        private object AsCollection(Type target, Type elementType, object value, string path)
        {
            var source = value as IEnumerable;
            if (source == null || value is string)
                throw Fail(path, "expected an array but received " + Quote(value));

            var items = new List<object>();
            var index = 0;
            foreach (var item in source)
                items.Add(Convert(elementType, item, path + "[" + index++ + "]"));

            if (target.IsArray)
            {
                var array = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }

            var concrete = target.IsInterface ? typeof(List<>).MakeGenericType(elementType) : target;
            var list = (IList)Activator.CreateInstance(concrete);
            foreach (var item in items) list.Add(item);
            return list;
        }

        private static object AsEnum(Type target, object value, string path)
        {
            var text = value as string;
            if (text != null)
            {
                try
                {
                    return Enum.Parse(target, text, ignoreCase: true);
                }
                catch (Exception)
                {
                    throw Fail(path, "'" + text + "' is not a valid " + target.Name +
                                     ". Valid values: " + string.Join(", ", Enum.GetNames(target)));
                }
            }

            try
            {
                return Enum.ToObject(target, System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                throw Fail(path, "cannot convert " + Quote(value) + " to " + target.Name);
            }
        }

        private static object AsBool(object value, string path)
        {
            if (value is bool) return value;
            var text = value as string;
            if (text != null)
            {
                bool parsed;
                if (bool.TryParse(text, out parsed)) return parsed;
                if (text == "1") return true;
                if (text == "0") return false;
            }
            if (IsNumeric(value.GetType()))
                return System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

            throw Fail(path, "cannot convert " + Quote(value) + " to boolean");
        }

        private static string AsString(object value)
        {
            var text = value as string;
            if (text != null) return text;
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return System.Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static object Parse(string path, Type target, object value, Func<string, object> parser)
        {
            var text = value as string;
            if (text == null)
                throw Fail(path, "expected a string for " + JsonSchemaGenerator.Describe(target) +
                                 " but received " + Quote(value));
            try
            {
                return parser(text);
            }
            catch (Exception)
            {
                throw Fail(path, "'" + text + "' is not a valid " + JsonSchemaGenerator.Describe(target));
            }
        }

        private static bool TryDictionaryValueType(Type type, out Type valueType)
        {
            valueType = null;
            var candidates = type.IsInterface
                ? new[] { type }.Concat(type.GetInterfaces())
                : type.GetInterfaces();

            foreach (var candidate in candidates)
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

        private static bool TryElementType(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;

            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            var candidates = type.IsInterface
                ? new[] { type }.Concat(type.GetInterfaces())
                : type.GetInterfaces();

            foreach (var candidate in candidates)
            {
                if (!candidate.IsGenericType) continue;
                if (candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }
            return false;
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                   type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
                   type == typeof(double) || type == typeof(decimal);
        }

        private static bool IsNullish(object value)
        {
            return value == null || value is DBNull;
        }

        private static object DefaultOf(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static string Quote(object value)
        {
            if (value == null) return "null";
            var text = AsString(value);
            if (text.Length > 60) text = text.Substring(0, 57) + "...";
            return "'" + text + "'";
        }

        private static ArgumentBindingException Fail(string path, string message)
        {
            return new ArgumentBindingException(new[] { path + ": " + message });
        }
    }
}
