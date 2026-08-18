// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Collections;
using System.Collections.Generic;

namespace McpToolAdapter
{
    /// <summary>
    /// An insertion-ordered string-keyed map used to build JSON Schema and OpenAPI documents.
    /// </summary>
    /// <remarks>
    /// Ordering is not cosmetic. Emitted OpenAPI documents get checked in, diffed and reviewed;
    /// a map with non-deterministic ordering produces noise in those diffs. It implements
    /// <see cref="IDictionary{TKey,TValue}"/> so any host serializer can write it without
    /// this assembly taking a dependency on a JSON library.
    /// </remarks>
    public sealed class JsonObject : IDictionary<string, object>
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public object this[string key]
        {
            get { return _values[key]; }
            set
            {
                if (!_values.ContainsKey(key)) _order.Add(key);
                _values[key] = value;
            }
        }

        public ICollection<string> Keys
        {
            get { return _order.ToArray(); }
        }

        public ICollection<object> Values
        {
            get
            {
                var values = new List<object>(_order.Count);
                foreach (var key in _order) values.Add(_values[key]);
                return values;
            }
        }

        public int Count
        {
            get { return _order.Count; }
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public void Add(string key, object value)
        {
            if (_values.ContainsKey(key))
                throw new ArgumentException("Duplicate key: " + key, nameof(key));
            _order.Add(key);
            _values.Add(key, value);
        }

        public void Add(KeyValuePair<string, object> item)
        {
            Add(item.Key, item.Value);
        }

        public void Clear()
        {
            _values.Clear();
            _order.Clear();
        }

        public bool Contains(KeyValuePair<string, object> item)
        {
            object existing;
            return _values.TryGetValue(item.Key, out existing) && Equals(existing, item.Value);
        }

        public bool ContainsKey(string key)
        {
            return _values.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            foreach (var pair in this) array[arrayIndex++] = pair;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var key in _order)
                yield return new KeyValuePair<string, object>(key, _values[key]);
        }

        public bool Remove(string key)
        {
            if (!_values.Remove(key)) return false;
            _order.Remove(key);
            return true;
        }

        public bool Remove(KeyValuePair<string, object> item)
        {
            return Contains(item) && Remove(item.Key);
        }

        public bool TryGetValue(string key, out object value)
        {
            return _values.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
