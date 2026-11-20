using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>
    /// Reuses FormattedText objects across redraws. Building and measuring text is the most expensive part of a
    /// frame; block labels ("300", "1.7k") and grid cells repeat, so a scroll mostly hits the cache.
    /// </summary>
    internal sealed class TextCache
    {
        private const int MaxEntries = 6000;
        private readonly Dictionary<(string, Typeface, double, Brush, double), FormattedText> _map =
            new Dictionary<(string, Typeface, double, Brush, double), FormattedText>();
        private double _pixelsPerDip = 1;

        public void Reset(double pixelsPerDip)
        {
            _pixelsPerDip = pixelsPerDip;
            _map.Clear();
        }

        /// <summary>Text on one line; when <paramref name="maxWidth"/> &gt; 0 it is trimmed with an ellipsis.</summary>
        public FormattedText Get(string text, Typeface typeface, double size, Brush brush, double maxWidth = 0)
        {
            text = text ?? "";
            var key = (text, typeface, size, brush, maxWidth);
            if (_map.TryGetValue(key, out var ft)) return ft;
            if (_map.Count >= MaxEntries) _map.Clear();
            ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, _pixelsPerDip)
            {
                MaxLineCount = 1,
            };
            if (maxWidth > 0)
            {
                ft.MaxTextWidth = maxWidth;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            _map[key] = ft;
            return ft;
        }

        public int Count => _map.Count;
    }
}
