using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Json
{
    /// <summary>
    /// Byte-exact port of Python's
    /// <c>json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")</c>.
    /// <list type="bullet">
    /// <item>Object keys sorted by Unicode code point (Python str order, not UTF-16 ordinal order).</item>
    /// <item>No whitespace; non-ASCII emitted raw as UTF-8 (U+2028/U+2029 and DEL too).</item>
    /// <item>Escapes exactly as Python: <c>\" \\ \n \r \t \b \f</c>, other C0 controls as lowercase <c>\u00xx</c>.</item>
    /// <item>Integers of any size; floats as Python <c>repr(float)</c> (shortest round-trip, <c>1e+16</c>, <c>1e-05</c>, <c>-0.0</c>).</item>
    /// <item>NaN/Infinity, lone surrogates (Python's UTF-8 encode fails), decimals, dates and other CLR values are rejected.</item>
    /// </list>
    /// </summary>
    public static class CanonicalJson
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static byte[] ToBytes(JToken value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var builder = new StringBuilder();
            Write(builder, value);
            try
            {
                return StrictUtf8.GetBytes(builder.ToString());
            }
            catch (EncoderFallbackException error)
            {
                throw new WireFormatException("string contains a lone surrogate", error);
            }
        }

        /// <summary>Parse JSON text (see <see cref="WireJson.Parse(string)"/>) and canonicalize it.</summary>
        public static byte[] ToBytes(string json) => ToBytes(WireJson.Parse(json));

        public static string ToText(JToken value) => StrictUtf8.GetString(ToBytes(value));

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                var text = new StringBuilder(64);
                foreach (var b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        public static string Sha256Hex(JToken value) => Sha256Hex(ToBytes(value));

        private static void Write(StringBuilder output, JToken token)
        {
            switch (token)
            {
                case JObject obj:
                    output.Append('{');
                    var first = true;
                    foreach (var property in obj.Properties().OrderBy(p => p.Name, CodePointComparer.Instance))
                    {
                        if (!first) output.Append(',');
                        first = false;
                        WriteString(output, property.Name);
                        output.Append(':');
                        Write(output, property.Value);
                    }
                    output.Append('}');
                    return;
                case JArray array:
                    output.Append('[');
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i > 0) output.Append(',');
                        Write(output, array[i]);
                    }
                    output.Append(']');
                    return;
                case JValue value:
                    WriteValue(output, value);
                    return;
                default:
                    throw new WireFormatException("unsupported JSON token " + token.Type);
            }
        }

        private static void WriteValue(StringBuilder output, JValue value)
        {
            switch (value.Type)
            {
                case JTokenType.Null:
                    output.Append("null");
                    return;
                case JTokenType.Boolean:
                    output.Append((bool)value.Value ? "true" : "false");
                    return;
                case JTokenType.String:
                    WriteString(output, (string)value.Value);
                    return;
                case JTokenType.Integer:
                    output.Append(IntegerText(value.Value));
                    return;
                case JTokenType.Float:
                    if (!(value.Value is double || value.Value is float))
                        throw new WireFormatException("only binary floating point numbers have a Python JSON form");
                    output.Append(PythonFloat.Repr(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture)));
                    return;
                default:
                    throw new WireFormatException("value of type " + value.Type + " has no Python JSON form");
            }
        }

        private static string IntegerText(object value)
        {
            switch (value)
            {
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case BigInteger b: return b.ToString(CultureInfo.InvariantCulture);
                case ulong u: return u.ToString(CultureInfo.InvariantCulture);
                case short s: return s.ToString(CultureInfo.InvariantCulture);
                case byte b8: return b8.ToString(CultureInfo.InvariantCulture);
                case sbyte sb: return sb.ToString(CultureInfo.InvariantCulture);
                case ushort us: return us.ToString(CultureInfo.InvariantCulture);
                case uint ui: return ui.ToString(CultureInfo.InvariantCulture);
                default: throw new WireFormatException("unsupported integer value");
            }
        }

        /// <summary>Python json's <c>py_encode_basestring</c> (ensure_ascii=False).</summary>
        internal static void WriteString(StringBuilder output, string value)
        {
            output.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            output.Append(c);
                        break;
                }
            }
            output.Append('"');
        }
    }

    /// <summary>Orders strings by Unicode code point, like Python's str comparison.</summary>
    public sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new CodePointComparer();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                var a = x[i];
                var b = y[i];
                if (a == b) continue;
                // UTF-16 code units order everything correctly except surrogates (U+10000 and up,
                // encoded as D800-DFFF), which must sort above U+E000-U+FFFF.
                return Fix(a).CompareTo(Fix(b));
            }
            return x.Length.CompareTo(y.Length);
        }

        private static int Fix(char c) => c >= 0xD800 && c <= 0xDFFF ? c + 0x2000 : c >= 0xE000 ? c - 0x800 : c;
    }

    /// <summary>Python <c>float.__repr__</c> (David Gay shortest round-trip, 'r' format), computed exactly with BigInteger.</summary>
    public static class PythonFloat
    {
        private static readonly BigInteger Ten = 10;

        public static string Repr(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new WireFormatException("non-finite float");
            var bits = BitConverter.DoubleToInt64Bits(value);
            var negative = bits < 0;
            if (value == 0) return negative ? "-0.0" : "0.0";

            var exponentBits = (int)((bits >> 52) & 0x7FF);
            var fraction = bits & 0xFFFFFFFFFFFFFL;
            long mantissa;
            int exponent;
            if (exponentBits == 0)
            {
                mantissa = fraction;
                exponent = -1074;
            }
            else
            {
                mantissa = fraction | (1L << 52);
                exponent = exponentBits - 1075;
            }

            // value = mantissa * 2^exponent. Work in units of 2^(exponent-2) so the half-ulp
            // rounding boundaries are integers: v = 4m, high = 4m + 2, low = 4m - 2 (or 4m - 1 when
            // the next lower double is closer, i.e. m is the smallest normal mantissa).
            var v = new BigInteger(mantissa) * 4;
            var high = v + 2;
            var low = fraction == 0 && exponentBits > 1 ? v - 1 : v - 2;
            var inclusive = (mantissa & 1) == 0; // round-half-even parsing maps the midpoint to an even mantissa
            var shift = exponent - 2;
            BigInteger scaleNum = shift >= 0 ? BigInteger.One << shift : BigInteger.One;
            BigInteger scaleDen = shift >= 0 ? BigInteger.One : BigInteger.One << -shift;

            // Decimal exponent k with 10^(k-1) <= |value| < 10^k.
            var k = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
            while (Compare(v * scaleNum, scaleDen, k - 1) < 0) k--;
            while (Compare(v * scaleNum, scaleDen, k) >= 0) k++;

            string digits = null;
            var pointPosition = 0;
            for (var precision = 1; precision <= 17 && digits == null; precision++)
            {
                // Scale by 10^(precision - k): candidates are integers c with c * 10^(k - precision) near value.
                var power = precision - k;
                BigInteger num = scaleNum, den = scaleDen;
                if (power >= 0) num *= BigInteger.Pow(Ten, power);
                else den *= BigInteger.Pow(Ten, -power);
                var scaledV = v * num;
                var floor = BigInteger.DivRem(scaledV, den, out var remainder);
                var ceil = remainder.IsZero ? floor : floor + 1;
                BigInteger? best = null;
                foreach (var candidate in new[] { floor, ceil }.Distinct())
                {
                    var c = candidate * den;
                    var lowOk = inclusive ? c >= low * num : c > low * num;
                    var highOk = inclusive ? c <= high * num : c < high * num;
                    if (!lowOk || !highOk) continue;
                    if (best == null)
                    {
                        best = candidate;
                        continue;
                    }
                    var dBest = BigInteger.Abs(best.Value * den - scaledV);
                    var dCand = BigInteger.Abs(c - scaledV);
                    if (dCand < dBest || (dCand == dBest && candidate.IsEven)) best = candidate;
                }
                if (best != null)
                {
                    var text = best.Value.ToString(CultureInfo.InvariantCulture);
                    pointPosition = k + (text.Length - precision);
                    digits = text.TrimEnd('0');
                }
            }
            if (digits == null) throw new InvalidOperationException("no shortest representation found");
            return (negative ? "-" : "") + Format(digits, pointPosition);
        }

        // Compare v*scaleNum/scaleDen with 10^power.
        private static int Compare(BigInteger num, BigInteger den, int power)
        {
            return power >= 0
                ? num.CompareTo(den * BigInteger.Pow(Ten, power))
                : (num * BigInteger.Pow(Ten, -power)).CompareTo(den);
        }

        // value = 0.<digits> * 10^decpt; Python's format_float_short with 'r' uses exponent
        // notation when decpt <= -4 or decpt > 16.
        private static string Format(string digits, int decpt)
        {
            if (decpt <= -4 || decpt > 16)
            {
                var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits.Substring(1);
                var exp = decpt - 1;
                return mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
            }
            if (decpt <= 0) return "0." + new string('0', -decpt) + digits;
            if (decpt >= digits.Length) return digits + new string('0', decpt - digits.Length) + ".0";
            return digits.Substring(0, decpt) + "." + digits.Substring(decpt);
        }
    }
}
