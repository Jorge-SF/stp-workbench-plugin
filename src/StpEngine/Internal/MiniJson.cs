using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Maptek.Vulcan.Stp.Engine.Internal
{
    /// <summary>
    /// Minimal, strict JSON reader with <c>JSON.parse</c> semantics, used only for the tiny horizon preference
    /// (so the engine stays free of JSON library references). Objects become
    /// <see cref="Dictionary{TKey,TValue}"/> (duplicate keys: last wins), arrays <see cref="List{T}"/>, numbers
    /// <see cref="double"/> (overflow gives ±Infinity, like JS), plus <see cref="string"/>, <see cref="bool"/> and
    /// <see cref="Null"/>.
    /// </summary>
    internal sealed class MiniJson
    {
        /// <summary>The JSON <c>null</c> value.</summary>
        public static readonly object Null = new object();

        private readonly string text;
        private int pos;

        private MiniJson(string text)
        {
            this.text = text;
        }

        /// <summary>Parses a complete JSON text.</summary>
        /// <exception cref="FormatException">Invalid JSON.</exception>
        public static object Parse(string text)
        {
            var reader = new MiniJson(text ?? throw new FormatException("null"));
            reader.SkipWhitespace();
            object value = reader.ReadValue();
            reader.SkipWhitespace();
            if (reader.pos != reader.text.Length)
            {
                throw new FormatException("trailing content");
            }

            return value;
        }

        private void SkipWhitespace()
        {
            while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\n' || text[pos] == '\r'))
            {
                pos++;
            }
        }

        private char Peek() => pos < text.Length ? text[pos] : throw new FormatException("unexpected end");

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("unexpected token");
            }

            pos += literal.Length;
        }

        private object ReadValue()
        {
            char c = Peek();
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ReadNumber();
                    }

                    throw new FormatException("unexpected token");
            }
        }

        private Dictionary<string, object> ReadObject()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            pos++;
            SkipWhitespace();
            if (Peek() == '}')
            {
                pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                {
                    throw new FormatException("expected key");
                }

                string key = ReadString();
                SkipWhitespace();
                Expect(":");
                SkipWhitespace();
                result[key] = ReadValue();
                SkipWhitespace();
                char c = Peek();
                pos++;
                if (c == '}')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new FormatException("expected , or }");
                }
            }
        }

        private List<object> ReadArray()
        {
            var result = new List<object>();
            pos++;
            SkipWhitespace();
            if (Peek() == ']')
            {
                pos++;
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                result.Add(ReadValue());
                SkipWhitespace();
                char c = Peek();
                pos++;
                if (c == ']')
                {
                    return result;
                }

                if (c != ',')
                {
                    throw new FormatException("expected , or ]");
                }
            }
        }

        private string ReadString()
        {
            pos++;
            var builder = new StringBuilder();
            while (true)
            {
                char c = Peek();
                pos++;
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c < 0x20)
                {
                    throw new FormatException("control character in string");
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                char e = Peek();
                pos++;
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
                        if (pos + 4 > text.Length || !ushort.TryParse(text.Substring(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code)
                            || !IsHex(text, pos, 4))
                        {
                            throw new FormatException("bad unicode escape");
                        }

                        builder.Append((char)code);
                        pos += 4;
                        break;
                    default:
                        throw new FormatException("bad escape");
                }
            }
        }

        private static bool IsHex(string s, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private double ReadNumber()
        {
            int start = pos;
            if (Peek() == '-')
            {
                pos++;
            }

            if (Peek() == '0')
            {
                pos++;
            }
            else
            {
                ReadDigits();
            }

            if (pos < text.Length && text[pos] == '.')
            {
                pos++;
                ReadDigits();
            }

            if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
            {
                pos++;
                if (pos < text.Length && (text[pos] == '+' || text[pos] == '-'))
                {
                    pos++;
                }

                ReadDigits();
            }

            string literal = text.Substring(start, pos - start);
            try
            {
                return double.Parse(literal, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                return literal.StartsWith("-", StringComparison.Ordinal) ? double.NegativeInfinity : double.PositiveInfinity;
            }
        }

        private void ReadDigits()
        {
            int start = pos;
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
            {
                pos++;
            }

            if (pos == start)
            {
                throw new FormatException("expected digit");
            }
        }
    }
}
