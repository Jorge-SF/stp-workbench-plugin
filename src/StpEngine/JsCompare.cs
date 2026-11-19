using System;
using System.Collections.Generic;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// JavaScript string orderings the engine depends on.
    /// </summary>
    /// <remarks>
    /// JS uses two orderings: <c>Array.prototype.sort()</c> without a comparator (UTF-16 code units:
    /// <see cref="Ordinal"/>) and <c>String.prototype.localeCompare</c> (ICU root collation under Node/Chromium:
    /// <see cref="LocaleCompare"/>). The latter is emulated exactly for printable ASCII (punctuation, then digits,
    /// then letters case-insensitively at the primary level, lowercase first at the tertiary level); other characters
    /// sort after ASCII by code unit. Ids and civil texts in VGS snapshots are ASCII.
    /// </remarks>
    public static class JsCompare
    {
        // ICU root order of printable ASCII, as produced by Node's localeCompare (recorded with Node 26).
        private const string AsciiOrder = " _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789aAbBcCdDeEfFgGhHiIjJkKlLmMnNoOpPqQrRsStTuUvVwWxXyYzZ";

        private static readonly int[] Primary = new int[128];
        private static readonly int[] Tertiary = new int[128];

        static JsCompare()
        {
            for (int i = 0; i < 128; i++)
            {
                Primary[i] = -1;
            }

            int weight = 0;
            for (int i = 0; i < AsciiOrder.Length; i++)
            {
                char c = AsciiOrder[i];
                if (c >= 'A' && c <= 'Z')
                {
                    Primary[c] = Primary[char.ToLowerInvariant(c)];
                    Tertiary[c] = 1;
                    continue;
                }

                Primary[c] = weight++;
            }
        }

        /// <summary>Gets a comparer equivalent to <see cref="LocaleCompare"/>.</summary>
        public static IComparer<string> Locale { get; } = Comparer<string>.Create(LocaleCompare);

        /// <summary>JS default sort order (UTF-16 code units).</summary>
        /// <param name="left">The left string.</param>
        /// <param name="right">The right string.</param>
        /// <returns>Negative, zero or positive.</returns>
        public static int Ordinal(string left, string right) => string.CompareOrdinal(left, right);

        /// <summary>Emulates <c>left.localeCompare(right)</c> (ICU root collation, see remarks of the class).</summary>
        /// <param name="left">The left string.</param>
        /// <param name="right">The right string.</param>
        /// <returns>-1, 0 or 1.</returns>
        public static int LocaleCompare(string left, string right)
        {
            left = left ?? "undefined";
            right = right ?? "undefined";
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                int a = PrimaryWeight(left[i]), b = PrimaryWeight(right[i]);
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }

            if (left.Length != right.Length)
            {
                return left.Length < right.Length ? -1 : 1;
            }

            for (int i = 0; i < length; i++)
            {
                int a = TertiaryWeight(left[i]), b = TertiaryWeight(right[i]);
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }

            int ordinal = string.CompareOrdinal(left, right);
            return ordinal < 0 ? -1 : (ordinal > 0 ? 1 : 0);
        }

        private static int PrimaryWeight(char c) => c < 128 && Primary[c] >= 0 ? Primary[c] : 1000 + c;

        private static int TertiaryWeight(char c) => c < 128 ? Tertiary[c] : 0;
    }
}
