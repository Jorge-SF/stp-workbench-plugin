using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>
    /// Minimal canonical JSON writer for the tests: keys sorted by UTF-16 code unit, no whitespace, non-ASCII kept,
    /// JSON.stringify escaping. Equivalent to Python <c>json.dumps(sort_keys=True, separators=(",",":"),
    /// ensure_ascii=False)</c>. The production writer belongs to StpBridge.
    /// </summary>
    internal static class CanonicalJson
    {
        public static string Write(JToken token)
        {
            var builder = new StringBuilder();
            WriteToken(builder, token);
            return builder.ToString();
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static void WriteToken(StringBuilder builder, JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    builder.Append('{');
                    bool first = true;
                    foreach (var property in ((JObject)token).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        WriteString(builder, property.Name);
                        builder.Append(':');
                        WriteToken(builder, property.Value);
                    }

                    builder.Append('}');
                    break;
                case JTokenType.Array:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (var item in (JArray)token)
                    {
                        if (!firstItem)
                        {
                            builder.Append(',');
                        }

                        firstItem = false;
                        WriteToken(builder, item);
                    }

                    builder.Append(']');
                    break;
                case JTokenType.String:
                    WriteString(builder, (string)token);
                    break;
                case JTokenType.Boolean:
                    builder.Append((bool)token ? "true" : "false");
                    break;
                case JTokenType.Null:
                    builder.Append("null");
                    break;
                case JTokenType.Integer:
                    builder.Append(((long)token).ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new NotSupportedException($"canonical JSON: unsupported token {token.Type}");
            }
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
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
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}
