using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>Visible when the value is null or an empty string; Collapsed otherwise. Parameter "invert" swaps.</summary>
    public sealed class EmptyToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool empty = value == null || (value is string s && s.Length == 0);
            if (parameter as string == "invert") empty = !empty;
            return empty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>True when an int is greater than zero.</summary>
    public sealed class PositiveConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i && i > 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Null for null or an empty string (a tooltip bound through it does not show an empty box).</summary>
    public sealed class EmptyToNullConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null || (value is string s && s.Length == 0) ? null : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
