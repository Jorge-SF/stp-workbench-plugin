using System;
using System.Collections.Generic;
using System.Linq;

namespace Maptek.Vulcan.Stp.Engine.Internal
{
    /// <summary>
    /// A string-keyed map that enumerates like a plain JS object: array-index keys (<c>"0"</c> … <c>"4294967294"</c>)
    /// first in ascending numeric order, then the other keys in insertion order. Re-setting a key keeps its place;
    /// deleting and adding it again moves it to the end.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    internal sealed class JsObjectMap<T>
    {
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, T> values = new Dictionary<string, T>(StringComparer.Ordinal);

        public int Count => values.Count;

        public IEnumerable<string> Keys
        {
            get
            {
                var indexes = order.Where(IsArrayIndex).OrderBy(k => uint.Parse(k, System.Globalization.CultureInfo.InvariantCulture));
                return indexes.Concat(order.Where(k => !IsArrayIndex(k))).ToList();
            }
        }

        public IEnumerable<KeyValuePair<string, T>> Entries => Keys.Select(k => new KeyValuePair<string, T>(k, values[k]));

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public bool TryGetValue(string key, out T value) => values.TryGetValue(key, out value);

        public T this[string key]
        {
            get => values[key];
            set
            {
                if (!values.ContainsKey(key)) order.Add(key);
                values[key] = value;
            }
        }

        public bool Remove(string key)
        {
            if (!values.Remove(key)) return false;
            order.Remove(key);
            return true;
        }

        public JsObjectMap<T> Copy()
        {
            var copy = new JsObjectMap<T>();
            copy.order.AddRange(order);
            foreach (var pair in values) copy.values[pair.Key] = pair.Value;
            return copy;
        }

        /// <summary>ECMAScript array index: the canonical decimal form of an integer in [0, 2^32 - 2].</summary>
        internal static bool IsArrayIndex(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 10) return false;
            if (key == "0") return true;
            if (key[0] < '1' || key[0] > '9') return false;
            foreach (char c in key)
            {
                if (c < '0' || c > '9') return false;
            }

            return ulong.Parse(key, System.Globalization.CultureInfo.InvariantCulture) <= 4294967294UL;
        }
    }
}
