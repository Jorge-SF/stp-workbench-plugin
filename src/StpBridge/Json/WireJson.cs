using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Json
{
    /// <summary>Raised when wire JSON does not match its contract (the Python validators' ValueError).</summary>
    public sealed class WireFormatException : FormatException
    {
        public WireFormatException(string message) : base(message) { }

        public WireFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Parsing and strict field access for the STP wire JSON. Mirrors the Python validators:
    /// exact field sets, exact JSON types (no string/number/bool coercion), and no date parsing.
    /// </summary>
    public static class WireJson
    {
        /// <summary>
        /// Parse JSON text with Python <c>json.loads</c> semantics (see <see cref="PythonJsonParser"/>): date-like
        /// strings stay strings, integers are exact, floats are correctly rounded doubles. Trailing content,
        /// NaN/Infinity and duplicate object keys are rejected (Python would keep the last duplicate; the bridge
        /// never emits duplicates, so rejecting them is the stricter, safer choice).
        /// </summary>
        public static JToken Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return PythonJsonParser.Parse(json);
        }

        /// <summary>Parse UTF-8 bytes (a leading BOM is rejected, as Python's json.loads(bytes.decode("utf-8")) would).</summary>
        public static JToken Parse(byte[] utf8)
        {
            if (utf8 == null) throw new ArgumentNullException(nameof(utf8));
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(utf8);
            }
            catch (DecoderFallbackException error)
            {
                throw new WireFormatException("invalid UTF-8", error);
            }
            if (text.Length > 0 && text[0] == (char)0xFEFF) throw new WireFormatException("unexpected UTF-8 BOM");
            return Parse(text);
        }

        public static JObject ExactObject(JToken token, string label, params string[] fields)
            => ObjectWithFields(token, label, fields, Array.Empty<string>());

        /// <summary>Require an object whose keys are all of <paramref name="required"/> plus any of <paramref name="optional"/>.</summary>
        public static JObject ObjectWithFields(JToken token, string label, IReadOnlyCollection<string> required, IReadOnlyCollection<string> optional)
        {
            if (!(token is JObject obj)) throw new WireFormatException(label + " must be an object");
            var keys = new HashSet<string>(obj.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var missing = required.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var unknown = keys.Where(k => !required.Contains(k) && !optional.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || unknown.Count > 0)
            {
                throw new WireFormatException(
                    $"{label} has invalid fields (missing=[{string.Join(", ", missing)}], unknown=[{string.Join(", ", unknown)}])");
            }
            return obj;
        }

        /// <summary>Exact JSON string, possibly empty.</summary>
        public static string String(JObject obj, string key, string label)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.String) throw new WireFormatException($"{label}.{key} must be a string");
            return (string)((JValue)token).Value;
        }

        /// <summary>Exact non-empty JSON string (Python <c>_text</c> / <c>_required_text</c>).</summary>
        public static string Text(JObject obj, string key, string label)
        {
            var value = String(obj, key, label);
            if (value.Length == 0) throw new WireFormatException($"{label}.{key} must be a non-empty string");
            return value;
        }

        /// <summary>Exact JSON boolean (Python <c>type(x) is bool</c>).</summary>
        public static bool Bool(JObject obj, string key, string label)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.Boolean) throw new WireFormatException($"{label}.{key} must be a boolean");
            return (bool)((JValue)token).Value;
        }

        /// <summary>Exact JSON integer that fits Int64 (bools are not integers here, unlike Python's isinstance).</summary>
        public static long Integer(JObject obj, string key, string label)
        {
            var token = obj[key];
            if (token == null || token.Type != JTokenType.Integer || !(((JValue)token).Value is long value))
                throw new WireFormatException($"{label}.{key} must be an integer");
            return value;
        }

        public static JArray List(JObject obj, string key, string label)
        {
            if (!(obj[key] is JArray array)) throw new WireFormatException($"{label}.{key} must be an array");
            return array;
        }

        public static void RequireConstant(string actual, string expected, string label)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new WireFormatException($"unsupported {label} '{actual}'");
        }

        public static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

    }
}
