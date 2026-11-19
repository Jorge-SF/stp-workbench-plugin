using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Json
{
    /// <summary>
    /// Strict JSON text → <see cref="JToken"/> parser with Python <c>json.loads</c> semantics, used instead of
    /// Newtonsoft's reader because .NET Framework's <c>double.Parse</c> is not correctly rounded and drops <c>-0.0</c>,
    /// which would break canonical-hash parity.
    /// <list type="bullet">
    /// <item>Integers (no fraction/exponent) are exact: <c>long</c>, else <see cref="BigInteger"/>.</item>
    /// <item>Other numbers are correctly rounded (half-even) binary doubles, computed with BigInteger.</item>
    /// <item>Strings: Python's escapes, raw control characters rejected (<c>strict=True</c>), surrogate pairs joined,
    /// lone surrogate escapes kept (canonical encoding then rejects them, as Python's UTF-8 encode would).</item>
    /// <item>Whitespace is space, tab, CR, LF only. No comments, single quotes, trailing commas.</item>
    /// <item>Rejected although Python accepts them: <c>NaN</c>/<c>Infinity</c> (never valid on this wire),
    /// numbers overflowing to infinity, and duplicate object keys (Python keeps the last one).</item>
    /// </list>
    /// </summary>
    internal sealed class PythonJsonParser
    {
        private const int MaxDepth = 512;
        private readonly string text;
        private int position;

        private PythonJsonParser(string text) => this.text = text;

        public static JToken Parse(string text)
        {
            var parser = new PythonJsonParser(text);
            parser.SkipWhitespace();
            var value = parser.ParseValue(0);
            parser.SkipWhitespace();
            if (parser.position != text.Length) throw parser.Error("trailing content after JSON value");
            return value;
        }

        private WireFormatException Error(string message) => new WireFormatException($"invalid JSON at offset {position}: {message}");

        private void SkipWhitespace()
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') break;
                position++;
            }
        }

        private JToken ParseValue(int depth)
        {
            if (depth > MaxDepth) throw Error("nesting too deep");
            if (position >= text.Length) throw Error("unexpected end of input");
            var c = text[position];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return new JValue(ParseString());
                case 't': Expect("true"); return new JValue(true);
                case 'f': Expect("false"); return new JValue(false);
                case 'n': Expect("null"); return JValue.CreateNull();
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw Error($"unexpected character '{c}'");
            }
        }

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw Error("invalid literal");
            position += literal.Length;
        }

        private JObject ParseObject(int depth)
        {
            var obj = new JObject();
            position++;
            SkipWhitespace();
            if (position < text.Length && text[position] == '}')
            {
                position++;
                return obj;
            }
            while (true)
            {
                SkipWhitespace();
                if (position >= text.Length || text[position] != '"') throw Error("expected a property name");
                var key = ParseString();
                SkipWhitespace();
                if (position >= text.Length || text[position] != ':') throw Error("expected ':'");
                position++;
                SkipWhitespace();
                var value = ParseValue(depth + 1);
                if (obj.Property(key) != null) throw Error($"duplicate key '{key}'");
                obj.Add(key, value);
                SkipWhitespace();
                if (position >= text.Length) throw Error("unterminated object");
                if (text[position] == ',')
                {
                    position++;
                    continue;
                }
                if (text[position] == '}')
                {
                    position++;
                    return obj;
                }
                throw Error("expected ',' or '}'");
            }
        }

        private JArray ParseArray(int depth)
        {
            var array = new JArray();
            position++;
            SkipWhitespace();
            if (position < text.Length && text[position] == ']')
            {
                position++;
                return array;
            }
            while (true)
            {
                SkipWhitespace();
                array.Add(ParseValue(depth + 1));
                SkipWhitespace();
                if (position >= text.Length) throw Error("unterminated array");
                if (text[position] == ',')
                {
                    position++;
                    continue;
                }
                if (text[position] == ']')
                {
                    position++;
                    return array;
                }
                throw Error("expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            position++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (position >= text.Length) throw Error("unterminated string");
                var c = text[position++];
                if (c == '"') return builder.ToString();
                if (c < 0x20) throw Error("invalid control character in string");
                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }
                if (position >= text.Length) throw Error("unterminated escape");
                var e = text[position++];
                switch (e)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        var unit = ReadHex4();
                        // Python joins \uD8xx\uDCxx into one code point; a lone half stays a lone surrogate.
                        if (unit >= 0xD800 && unit <= 0xDBFF && position + 6 <= text.Length && text[position] == '\\' && text[position + 1] == 'u')
                        {
                            var save = position;
                            position += 2;
                            var low = ReadHex4();
                            if (low >= 0xDC00 && low <= 0xDFFF)
                            {
                                builder.Append((char)unit).Append((char)low);
                                break;
                            }
                            position = save;
                        }
                        builder.Append((char)unit);
                        break;
                    default:
                        throw Error("invalid escape");
                }
            }
        }

        private int ReadHex4()
        {
            if (position + 4 > text.Length) throw Error("invalid \\u escape");
            var s = text.Substring(position, 4);
            foreach (var h in s)
            {
                if (!Uri.IsHexDigit(h)) throw Error("invalid \\u escape");
            }
            position += 4;
            return int.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        private JValue ParseNumber()
        {
            var start = position;
            var negative = false;
            if (text[position] == '-')
            {
                negative = true;
                position++;
            }
            var intStart = position;
            if (position < text.Length && text[position] == '0')
            {
                position++;
            }
            else if (position < text.Length && text[position] >= '1' && text[position] <= '9')
            {
                while (position < text.Length && char.IsDigit(text[position]) && text[position] < 128) position++;
            }
            else
            {
                throw Error("invalid number");
            }
            var intDigits = text.Substring(intStart, position - intStart);
            var fracDigits = "";
            var isFloat = false;
            if (position + 1 < text.Length && text[position] == '.' && IsAsciiDigit(text[position + 1]))
            {
                position++;
                var fracStart = position;
                while (position < text.Length && IsAsciiDigit(text[position])) position++;
                fracDigits = text.Substring(fracStart, position - fracStart);
                isFloat = true;
            }
            long exponent = 0;
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                var save = position;
                position++;
                var expNegative = false;
                if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                {
                    expNegative = text[position] == '-';
                    position++;
                }
                if (position < text.Length && IsAsciiDigit(text[position]))
                {
                    while (position < text.Length && IsAsciiDigit(text[position]))
                    {
                        if (exponent < 100_000_000) exponent = exponent * 10 + (text[position] - '0');
                        position++;
                    }
                    if (expNegative) exponent = -exponent;
                    isFloat = true;
                }
                else
                {
                    position = save; // Python's scanner leaves "1e" as "1" followed by garbage
                }
            }
            if (!isFloat)
            {
                var literal = text.Substring(start, position - start);
                if (long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var small)) return new JValue(small);
                return new JValue(BigInteger.Parse(literal, CultureInfo.InvariantCulture));
            }
            var coefficient = BigInteger.Parse(intDigits + fracDigits, CultureInfo.InvariantCulture);
            var value = DecimalToDouble(coefficient, exponent - fracDigits.Length);
            if (double.IsInfinity(value)) throw Error("number overflows a double");
            return new JValue(negative ? -value : value);
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        /// <summary>Correctly rounded (round-half-even) nearest double to coefficient × 10^exponent; coefficient ≥ 0.</summary>
        internal static double DecimalToDouble(BigInteger coefficient, long exponent)
        {
            if (coefficient.IsZero) return 0.0;
            var digits = coefficient.ToString(CultureInfo.InvariantCulture).Length;
            if (digits + exponent > 310) return double.PositiveInfinity;
            if (digits + exponent < -330) return 0.0;
            BigInteger num = coefficient, den = BigInteger.One;
            if (exponent >= 0) num *= BigInteger.Pow(10, (int)exponent);
            else den = BigInteger.Pow(10, (int)-exponent);

            // Pick s so that q = floor(num * 2^s / den) has exactly 53 significant bits; value ≈ q * 2^-s.
            var s = 52 - (BitLength(num) - BitLength(den));
            BigInteger q, r, d;
            while (true)
            {
                Scaled(num, den, s, out q, out r, out d);
                if (BitLength(q) > 53) { s--; continue; }
                if (BitLength(q) < 53) { s++; continue; }
                break;
            }
            var binaryExponent = -s;
            if (binaryExponent < -1074)
            {
                // Subnormal: fixed exponent 2^-1074, fewer significant bits.
                s = 1074;
                Scaled(num, den, s, out q, out r, out d);
                binaryExponent = -1074;
            }
            var twice = r * 2;
            if (twice > d || (twice == d && !q.IsEven)) q += 1;
            if (BitLength(q) > 53)
            {
                q >>= 1;
                binaryExponent += 1;
            }
            if (binaryExponent == -1074 && q < (BigInteger.One << 52))
                return BitConverter.Int64BitsToDouble((long)q); // subnormal (or exactly the smallest normal when q == 2^52)
            if (binaryExponent + 52 > 1023) return double.PositiveInfinity;
            var bits = ((long)(binaryExponent + 1075) << 52) | ((long)q & 0xFFFFFFFFFFFFFL);
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static void Scaled(BigInteger num, BigInteger den, int s, out BigInteger q, out BigInteger r, out BigInteger d)
        {
            var n = s >= 0 ? num << s : num;
            d = s >= 0 ? den : den << -s;
            q = BigInteger.DivRem(n, d, out r);
        }

        private static int BitLength(BigInteger value)
        {
            if (value.IsZero) return 0;
            var bytes = value.ToByteArray();
            var top = bytes[bytes.Length - 1];
            var length = (bytes.Length - 1) * 8;
            if (top == 0) return length - 8 + BitLengthOfByte(bytes[bytes.Length - 2]);
            return length + BitLengthOfByte(top);
        }

        private static int BitLengthOfByte(byte b)
        {
            var n = 0;
            while (b != 0)
            {
                n++;
                b >>= 1;
            }
            return n;
        }
    }
}
