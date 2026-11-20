using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>Frozen brushes, pens and fonts. Colours are the CSS variables of docs/mockups/planner_layout.html.</summary>
    internal static class TimelineTheme
    {
        public static readonly Brush Panel = Solid(0xFF, 0xFF, 0xFF);
        public static readonly Brush HeaderBack = Solid(0xF8, 0xF9, 0xFB);
        public static readonly Brush GroupBack = Solid(0xF3, 0xF5, 0xF8);
        public static readonly Brush Weekend = Solid(0xF5, 0xF6, 0xF8);
        public static readonly Brush Selected = Solid(0xE3, 0xEE, 0xFA);   // --accent-soft
        public static readonly Brush Text = Solid(0x1F, 0x29, 0x33);
        public static readonly Brush Muted = Solid(0x66, 0x72, 0x7F);
        public static readonly Brush DoneText = Solid(0x4A, 0x55, 0x60);
        public static readonly Brush BlockText = Solid(0xFF, 0xFF, 0xFF);
        public static readonly Brush CompletedLineText = Solid(0x5B, 0x67, 0x73);
        public static readonly Brush LinkBrush = Solid(0xD4, 0x89, 0x2A);

        public static readonly Pen Line = MakePen(Solid(0xD5, 0xD9, 0xE0), 1);       // --line
        public static readonly Pen LineSoft = MakePen(Solid(0xE8, 0xEB, 0xEF), 1);   // --line-soft
        public static readonly Pen Divider = MakePen(Solid(0xD5, 0xD9, 0xE0), 2);
        public static readonly Pen DirectMove = MakePen(Solid(0x1F, 0x29, 0x33), 1.6);
        public static readonly Pen CascadeMove = MakePen(Solid(0xB5, 0x47, 0x08), 1.2, 3, 2);
        public static readonly Pen CompletedLine = MakePen(Solid(0x5B, 0x67, 0x73), 1, 4, 3);
        public static readonly Pen Link = MakeLinkPen();

        public static readonly Brush Hatch = MakeHatch();

        /// <summary>Capacity-exempt blocks (short_term_planner_template_v2.html <c>--exempt-color</c>).</summary>
        public static readonly Color ExemptColor = Color.FromRgb(0x68, 0xBD, 0x79);
        public static readonly Brush Exempt = Solid(0x68, 0xBD, 0x79);

        // Editing overlay (reference .drag-preview-segment: #b9daf6 fill, 2px dashed #0b66c3, opacity .62).
        public static readonly Brush GhostFill = Solid(0xB9, 0xDA, 0xF6, 0xA0);
        public static readonly Pen GhostPen = MakePen(Solid(0x0B, 0x66, 0xC3), 2, 3, 2);
        // Capacity preview (reference .drag-preview-segment.drag-preview-warning: #ffe2df fill, dashed #b42318).
        public static readonly Brush GhostWarnFill = Solid(0xFF, 0xE2, 0xDF, 0xE0);
        public static readonly Pen GhostWarnPen = MakePen(Solid(0xB4, 0x23, 0x18), 2, 3, 2);
        public static readonly Brush BadgeWarnBack = Solid(0xB4, 0x23, 0x18);
        public static readonly Brush DragOrigin = Solid(0xFF, 0xFF, 0xFF, 0x99);   // fades the blocks being moved
        public static readonly Brush BadgeBack = Solid(0x1F, 0x29, 0x33);
        public static readonly Brush BadgeText = Solid(0xFF, 0xFF, 0xFF);
        public static readonly Pen FocusPen = MakePen(Solid(0x0B, 0x66, 0xC3), 2);

        // "Avance real" boxes (reference .actual-progress-control / -drag-grip / #timeline-links .actual-progress-continuity)
        public static readonly Brush ProgressBack = Solid(0xFF, 0xFF, 0xFF);
        public static readonly Pen ProgressBorder = MakePen(Solid(0x6B, 0x9D, 0xD0), 1);
        public static readonly Brush ProgressText = Solid(0x17, 0x20, 0x33);
        public static readonly Brush ProgressGrip = Solid(0x23, 0x67, 0x9F);
        public static readonly Brush ProgressFocus = Solid(0x0B, 0x66, 0xC3);
        public static readonly Brush ProgressContinuityBrush = Solid(0x0B, 0x66, 0xC3);
        public static readonly Pen ProgressContinuity = MakePen(Solid(0x0B, 0x66, 0xC3), 1.5, 4, 3);
        public static readonly Brush ProgressDropBack = Solid(0xE5, 0xF2, 0xFF);
        public static readonly Color BlockedColor = Color.FromRgb(0xB4, 0x23, 0x18);  // reference .drag-preview-warning
        public static readonly Brush BlockedBack = Solid(0xFF, 0xE2, 0xDF);
        public static readonly Brush BlockedText = Solid(0x8A, 0x1C, 0x14);
        public static readonly Brush TipBack = Solid(0xFF, 0xFF, 0xFF);
        public static readonly Brush TipBorder = Solid(0xC9, 0xCF, 0xD8);

        public static readonly Typeface Regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        public static readonly Typeface Icons = new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public const double GridFontSize = 12;
        public const double ChartFontSize = 10;
        public const string LockGlyph = "";

        private static readonly Dictionary<Color, Brush> RowBrushes = new Dictionary<Color, Brush>();

        public static Brush ForColor(Color color)
        {
            if (!RowBrushes.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                RowBrushes[color] = brush;
            }
            return brush;
        }

        private static Brush Solid(byte r, byte g, byte b, byte a = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Pen MakePen(Brush brush, double thickness, params double[] dashes)
        {
            var pen = new Pen(brush, thickness);
            if (dashes.Length > 0) pen.DashStyle = new DashStyle(Scale(dashes, thickness), 0);
            pen.Freeze();
            return pen;
        }

        // WPF dash lengths are in multiples of the pen thickness; the mockup's are in pixels.
        private static double[] Scale(double[] dashes, double thickness)
        {
            var scaled = new double[dashes.Length];
            for (int i = 0; i < dashes.Length; i++) scaled[i] = dashes[i] / thickness;
            return scaled;
        }

        private static Pen MakeLinkPen()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xD9, 0xD4, 0x89, 0x2A)); // opacity .85
            brush.Freeze();
            var pen = new Pen(brush, 1.2) { LineJoin = PenLineJoin.Miter };
            pen.Freeze();
            return pen;
        }

        // Mockup: 6x6 tile rotated 45°, #c9ced5 with a 2 px #aeb5be stripe.
        private static Brush MakeHatch()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Solid(0xC9, 0xCE, 0xD5), null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
            group.Children.Add(new GeometryDrawing(Solid(0xAE, 0xB5, 0xBE), null, new RectangleGeometry(new Rect(0, 0, 2, 6))));
            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 6, 6),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 6, 6),
                ViewboxUnits = BrushMappingMode.Absolute,
                Transform = new RotateTransform(45),
            };
            brush.Freeze();
            return brush;
        }
    }
}
