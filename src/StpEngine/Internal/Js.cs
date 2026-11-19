using System;
using System.Collections.Generic;
using System.Linq;

namespace Maptek.Vulcan.Stp.Engine.Internal
{
    /// <summary>Small helpers for JS semantics.</summary>
    internal static class Js
    {
        /// <summary>JS <c>String(value)</c> / template interpolation of a possibly undefined string.</summary>
        public static string S(string value) => value ?? "undefined";

        /// <summary>Stable sort, like V8's <c>Array.prototype.sort</c>.</summary>
        public static List<T> SortStable<T>(IEnumerable<T> items, Comparison<T> comparison) =>
            items.OrderBy(item => item, Comparer<T>.Create(comparison)).ToList();

        /// <summary>Three-way compare of two longs.</summary>
        public static int Cmp(long left, long right) => left < right ? -1 : (left > right ? 1 : 0);

        /// <summary>JS <c>Number.isSafeInteger</c> for an integer value.</summary>
        public static bool IsSafeInteger(long value) => value <= 9007199254740991L && value >= -9007199254740991L;

        /// <summary>JS <c>String.prototype.trim</c> whitespace set.</summary>
        public static string Trim(string value) => value.Trim(JsWhitespace);

        private static readonly char[] JsWhitespace = new[]
        {
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0xA0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,
            0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
        }.Select(code => (char)code).ToArray();
    }
}
