using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Maptek.Vulcan.Stp.Bridge.Json
{
    /// <summary>
    /// An exact, finite Python <c>decimal.Decimal</c> as carried in wire strings
    /// (<c>quantity_percentage</c>, <c>percentComplete</c>, ...). Keeps sign, coefficient and exponent
    /// separately, so <c>"40"</c>, <c>"40.0"</c> and <c>"4E+1"</c> stay distinct exactly as in Python,
    /// while <see cref="Equals(PyDecimal)"/> compares numerically like Python's <c>==</c>.
    /// </summary>
    public readonly struct PyDecimal : IEquatable<PyDecimal>, IComparable<PyDecimal>
    {
        private PyDecimal(bool negative, BigInteger coefficient, int exponent)
        {
            Negative = negative;
            Coefficient = coefficient;
            Exponent = exponent;
        }

        public bool Negative { get; }

        public BigInteger Coefficient { get; }

        public int Exponent { get; }

        /// <summary>
        /// Parse like <c>Decimal(text)</c> but only finite values: surrounding whitespace is ignored and
        /// underscores are dropped wherever they appear (CPython's <c>Decimal("1__0") == 10</c>). NaN/Infinity are rejected
        /// (the validators reject non-finite values). Non-ASCII digits, which Python also accepts, are
        /// rejected here.
        /// </summary>
        public static bool TryParse(string text, out PyDecimal value)
        {
            value = default;
            if (text == null) return false;
            var s = text.Trim(' ', '\t', '\n', '\r', '\f', '\v').Replace("_", "");
            var i = 0;
            var negative = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                negative = s[i] == '-';
                i++;
            }
            var digits = new StringBuilder();
            var fractionDigits = 0;
            var sawDigit = false;
            var sawPoint = false;
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (c >= '0' && c <= '9')
                {
                    digits.Append(c);
                    if (sawPoint) fractionDigits++;
                    sawDigit = true;
                }
                else if (c == '.' && !sawPoint)
                {
                    sawPoint = true;
                }
                else
                {
                    break;
                }
            }
            if (!sawDigit) return false;
            long exponent = 0;
            if (i < s.Length)
            {
                if (s[i] != 'e' && s[i] != 'E') return false;
                i++;
                var expNegative = false;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    expNegative = s[i] == '-';
                    i++;
                }
                var expDigits = 0;
                for (; i < s.Length; i++)
                {
                    var c = s[i];
                    if (c >= '0' && c <= '9')
                    {
                        if (exponent < 1_000_000_000) exponent = exponent * 10 + (c - '0');
                        expDigits++;
                    }
                    else
                    {
                        return false;
                    }
                }
                if (expDigits == 0) return false;
                if (expNegative) exponent = -exponent;
            }
            var finalExponent = exponent - fractionDigits;
            if (finalExponent > 999_999_999 || finalExponent < -999_999_999) return false;
            value = new PyDecimal(negative, BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture), (int)finalExponent);
            return true;
        }

        public static PyDecimal Parse(string text)
        {
            if (!TryParse(text, out var value)) throw new WireFormatException("not a finite decimal: '" + text + "'");
            return value;
        }

        public bool IsZero => Coefficient.IsZero;

        /// <summary>Python <c>format(value, "f")</c>: fixed point, exponent applied, no rounding (used by <c>vgs-stp-apply/1</c>).</summary>
        public string ToFixedString()
        {
            var digits = Coefficient.ToString(CultureInfo.InvariantCulture);
            var sign = Negative ? "-" : "";
            // Python rescales a zero with a positive exponent to exponent 0 for 'f' ("0E+2" -> "0").
            if (Exponent >= 0) return sign + digits + (IsZero ? "" : new string('0', Exponent));
            var point = digits.Length + Exponent;
            if (point > 0) return sign + digits.Substring(0, point) + "." + digits.Substring(point);
            return sign + "0." + new string('0', -point) + digits;
        }

        /// <summary>Python <c>str(value)</c> (to-scientific-string; used by <c>vgs-stp-snapshot/2</c> and <c>vgs-stp-splits/2</c>).</summary>
        public override string ToString()
        {
            var digits = Coefficient.ToString(CultureInfo.InvariantCulture);
            var sign = Negative ? "-" : "";
            var leftdigits = Exponent + digits.Length;
            int dotplace;
            if (Exponent <= 0 && leftdigits > -6) dotplace = leftdigits;
            else dotplace = 1;
            string intpart, fracpart;
            if (dotplace <= 0)
            {
                intpart = "0";
                fracpart = "." + new string('0', -dotplace) + digits;
            }
            else if (dotplace >= digits.Length)
            {
                intpart = digits + new string('0', dotplace - digits.Length);
                fracpart = "";
            }
            else
            {
                intpart = digits.Substring(0, dotplace);
                fracpart = "." + digits.Substring(dotplace);
            }
            var exp = leftdigits - dotplace;
            var expText = leftdigits == dotplace ? "" : "E" + (exp >= 0 ? "+" : "-") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
            return sign + intpart + fracpart + expText;
        }

        public int CompareTo(PyDecimal other)
        {
            var a = Signed(this);
            var b = Signed(other);
            var exponent = Math.Min(Exponent, other.Exponent);
            a *= BigInteger.Pow(10, Exponent - exponent);
            b *= BigInteger.Pow(10, other.Exponent - exponent);
            return a.CompareTo(b);
        }

        private static BigInteger Signed(PyDecimal value) => value.Negative ? -value.Coefficient : value.Coefficient;

        public bool Equals(PyDecimal other) => CompareTo(other) == 0;

        public override bool Equals(object obj) => obj is PyDecimal other && Equals(other);

        public override int GetHashCode()
        {
            if (IsZero) return 0;
            var coefficient = Coefficient;
            var exponent = Exponent;
            while (!coefficient.IsZero && (coefficient % 10).IsZero)
            {
                coefficient /= 10;
                exponent++;
            }
            return (Negative ? -1 : 1) * (coefficient.GetHashCode() ^ exponent);
        }

        public static bool operator ==(PyDecimal left, PyDecimal right) => left.Equals(right);

        public static bool operator !=(PyDecimal left, PyDecimal right) => !left.Equals(right);
    }
}
