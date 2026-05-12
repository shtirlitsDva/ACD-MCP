using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Acd.Mcp.Ui
{
    // Colors log row headers: success → default foreground, failure → red.
    // Kept here rather than inline in XAML because the converter is shared by
    // every row.
    public sealed class SuccessBrushConverter : IValueConverter
    {
        private static readonly Brush Failure = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool ok && !ok) return Failure;
            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
