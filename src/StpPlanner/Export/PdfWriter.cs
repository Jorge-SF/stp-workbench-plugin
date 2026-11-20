// A minimal PDF 1.4 writer for the plan report: pages of filled/stroked rectangles, lines and text in the standard
// Helvetica fonts (WinAnsiEncoding, so Spanish accents print without embedding a font). No third-party library:
// the plugin targets .NET Framework 4.8 inside the Workbench and ships no PDF dependency.
// Coordinates are points from the top-left corner of the page (converted to PDF's bottom-left origin here).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Maptek.Vulcan.Stp.Planner.Export
{
    /// <summary>An RGB colour for <see cref="PdfWriter"/>.</summary>
    public readonly struct PdfColor
    {
        public PdfColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        public static PdfColor Black => new PdfColor(0, 0, 0);

        public static PdfColor White => new PdfColor(255, 255, 255);

        /// <summary>Parses <c>#RRGGBB</c>; anything else is <paramref name="fallback"/>.</summary>
        public static PdfColor Parse(string hex, PdfColor fallback)
        {
            if (hex == null || hex.Length != 7 || hex[0] != '#') return fallback;
            return int.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)
                ? new PdfColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : fallback;
        }

        /// <summary>Black or white, whichever reads better on this colour (template <c>printForeground</c>).</summary>
        public PdfColor Foreground()
        {
            double Linear(byte c)
            {
                double x = c / 255.0;
                return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
            }

            double l = .2126 * Linear(R) + .7152 * Linear(G) + .0722 * Linear(B);
            return (l + .05) / .05 >= 1.05 / (l + .05) ? Black : White;
        }

        internal string Operands => F(R / 255.0) + " " + F(G / 255.0) + " " + F(B / 255.0);

        internal static string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Builds a PDF document page by page.</summary>
    public sealed class PdfWriter
    {
        // Helvetica advance widths (1/1000 em) for ' ' .. '~' (Adobe Helvetica AFM).
        private static readonly short[] HelveticaAscii =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
        };

        private static readonly Encoding WinAnsi = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));

        private readonly List<Page> pages = new List<Page>();
        private Page current;

        /// <summary>Gets the number of pages so far.</summary>
        public int PageCount => pages.Count;

        /// <summary>Gets or sets the document title (Info dictionary).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets the current page width.</summary>
        public double Width => current?.Width ?? 0;

        /// <summary>Gets the current page height.</summary>
        public double Height => current?.Height ?? 0;

        /// <summary>Starts a new page; drawing goes to it.</summary>
        public void AddPage(double width, double height)
        {
            current = new Page(width, height);
            pages.Add(current);
        }

        /// <summary>Selects an existing page (0-based) for more drawing (page footers).</summary>
        public void SelectPage(int index) => current = pages[index];

        /// <summary>Text width in points.</summary>
        public static double MeasureText(string text, double size, bool bold = false)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double units = 0;
            foreach (char c in text) units += CharWidth(c);
            return units * size / 1000.0 * (bold ? 1.08 : 1.0);
        }

        /// <summary>Cuts <paramref name="text"/> with an ellipsis so it fits <paramref name="width"/>.</summary>
        public static string Fit(string text, double width, double size, bool bold = false)
        {
            text = text ?? string.Empty;
            if (MeasureText(text, size, bold) <= width) return text;
            const string ellipsis = "…";
            for (int n = text.Length - 1; n > 0; n--)
            {
                string candidate = text.Substring(0, n).TrimEnd() + ellipsis;
                if (MeasureText(candidate, size, bold) <= width) return candidate;
            }

            return string.Empty;
        }

        public void FillRect(double x, double y, double w, double h, PdfColor fill)
        {
            Ops().Append(fill.Operands).Append(" rg ").Append(Box(x, y, w, h)).Append(" re f\n");
        }

        public void StrokeRect(double x, double y, double w, double h, PdfColor stroke, double lineWidth = 0.5)
        {
            Ops().Append(stroke.Operands).Append(" RG ").Append(PdfColor.F(lineWidth)).Append(" w ").Append(Box(x, y, w, h)).Append(" re S\n");
        }

        public void Line(double x1, double y1, double x2, double y2, PdfColor stroke, double lineWidth = 0.5)
        {
            Ops().Append(stroke.Operands).Append(" RG ").Append(PdfColor.F(lineWidth)).Append(" w ")
                .Append(PdfColor.F(x1)).Append(' ').Append(PdfColor.F(Height - y1)).Append(" m ")
                .Append(PdfColor.F(x2)).Append(' ').Append(PdfColor.F(Height - y2)).Append(" l S\n");
        }

        /// <summary>Draws text with its baseline at <paramref name="baseline"/>.</summary>
        public void Text(double x, double baseline, string text, double size, PdfColor color, bool bold = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            Ops().Append("BT ").Append(color.Operands).Append(" rg /").Append(bold ? "F2 " : "F1 ").Append(PdfColor.F(size)).Append(" Tf ")
                .Append(PdfColor.F(x)).Append(' ').Append(PdfColor.F(Height - baseline)).Append(" Td ")
                .Append(Literal(text)).Append(" Tj ET\n");
        }

        /// <summary>Right-aligned text ending at <paramref name="right"/>.</summary>
        public void TextRight(double right, double baseline, string text, double size, PdfColor color, bool bold = false)
            => Text(right - MeasureText(text, size, bold), baseline, text, size, color, bold);

        /// <summary>Centred text.</summary>
        public void TextCentered(double centre, double baseline, string text, double size, PdfColor color, bool bold = false)
            => Text(centre - MeasureText(text, size, bold) / 2, baseline, text, size, color, bold);

        /// <summary>Writes the document.</summary>
        public byte[] ToBytes()
        {
            if (pages.Count == 0) throw new InvalidOperationException("the document has no pages");
            using (var stream = new MemoryStream())
            {
                var offsets = new List<long>();
                void Raw(string s)
                {
                    var b = Encoding.ASCII.GetBytes(s);
                    stream.Write(b, 0, b.Length);
                }

                void BeginObject(int number)
                {
                    while (offsets.Count < number) offsets.Add(0);
                    offsets[number - 1] = stream.Position;
                    Raw(number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
                }

                Raw("%PDF-1.4\n");
                stream.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

                // 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, 5 info, then (page, content) pairs.
                int firstPage = 6;
                var kids = new StringBuilder();
                for (int i = 0; i < pages.Count; i++) kids.Append(firstPage + 2 * i).Append(" 0 R ");

                BeginObject(1);
                Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                BeginObject(2);
                Raw("<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count.ToString(CultureInfo.InvariantCulture) + " >>\nendobj\n");
                BeginObject(3);
                Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
                BeginObject(4);
                Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");
                BeginObject(5);
                Raw("<< /Producer (STP UG) /Title ");
                WriteLiteral(stream, Title);
                Raw(" /CreationDate (D:" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ") >>\nendobj\n");

                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    int pageObject = firstPage + 2 * i;
                    BeginObject(pageObject);
                    Raw("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + PdfColor.F(page.Width) + " " + PdfColor.F(page.Height) + "] "
                        + "/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents " + (pageObject + 1).ToString(CultureInfo.InvariantCulture) + " 0 R >>\nendobj\n");
                    var content = WinAnsi.GetBytes(page.Content.ToString());
                    BeginObject(pageObject + 1);
                    Raw("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
                    stream.Write(content, 0, content.Length);
                    Raw("\nendstream\nendobj\n");
                }

                long xref = stream.Position;
                Raw("xref\n0 " + (offsets.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
                foreach (long offset in offsets) Raw(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                Raw("trailer\n<< /Size " + (offsets.Count + 1).ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R /Info 5 0 R >>\nstartxref\n"
                    + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
                return stream.ToArray();
            }
        }

        private static void WriteLiteral(Stream stream, string text)
        {
            var bytes = WinAnsi.GetBytes(Literal(text));
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>A PDF literal string; characters outside Windows-1252 become '?', control characters are dropped.</summary>
        internal static string Literal(string text)
        {
            var sb = new StringBuilder(text.Length + 2).Append('(');
            foreach (char raw in text ?? string.Empty)
            {
                char c = raw;
                if (c == '\\' || c == '(' || c == ')') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(WinAnsi.GetString(WinAnsi.GetBytes(new[] { c })));
            }

            return sb.Append(')').ToString();
        }

        private static int CharWidth(char c)
        {
            if (c >= ' ' && c <= '~') return HelveticaAscii[c - ' '];
            switch (c)
            {
                case 'á': case 'à': case 'â': case 'ä': case 'ã': case 'å': case 'é': case 'è': case 'ê': case 'ë':
                case 'ó': case 'ò': case 'ô': case 'ö': case 'õ': case 'ú': case 'ù': case 'û': case 'ü': case 'ñ':
                    return 556;
                case 'í': case 'ì': case 'î': case 'ï': case 'Í': case 'Ì': case 'Î': case 'Ï': case '·':
                    return 278;
                case 'Á': case 'À': case 'Â': case 'Ä': case 'Ã': case 'É': case 'È': case 'Ê': case 'Ë':
                    return 667;
                case 'Ó': case 'Ò': case 'Ô': case 'Ö': case 'Õ':
                    return 778;
                case 'Ú': case 'Ù': case 'Û': case 'Ü': case 'Ñ':
                    return 722;
                case '—': case '…':
                    return 1000;
                case '–':
                    return 556;
                case '°':
                    return 400;
                case '¿': case '¡':
                    return 611;
                default:
                    return 556;
            }
        }

        private StringBuilder Ops()
        {
            if (current == null) throw new InvalidOperationException("call AddPage first");
            return current.Content;
        }

        private string Box(double x, double y, double w, double h)
            => PdfColor.F(x) + " " + PdfColor.F(Height - y - h) + " " + PdfColor.F(w) + " " + PdfColor.F(h);

        private sealed class Page
        {
            public Page(double width, double height)
            {
                Width = width;
                Height = height;
            }

            public double Width { get; }

            public double Height { get; }

            public StringBuilder Content { get; } = new StringBuilder();
        }
    }
}
