using System;
using System.Windows;
using System.Windows.Media;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>A FrameworkElement made of stacked DrawingVisual layers that can be redrawn independently.</summary>
    internal abstract class LayeredPanel : FrameworkElement
    {
        private readonly VisualCollection _children;
        protected readonly DrawingVisual[] Layers;

        protected LayeredPanel(int layerCount)
        {
            _children = new VisualCollection(this);
            Layers = new DrawingVisual[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                Layers[i] = new DrawingVisual();
                _children.Add(Layers[i]);
            }
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index)
        {
            if (index < 0 || index >= _children.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _children[index];
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(0, 0);

        /// <summary>Pixel-aligned coordinate for a 1 px line.</summary>
        protected static double Snap(double v) => Math.Round(v) + 0.5;
    }
}
