using System.Globalization;
using System.Windows.Data;

namespace UsageTrackerNative;

public sealed class RatioToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2
            && values[0] is double ratio
            && values[1] is double totalWidth
            && totalWidth > 0)
        {
            return Math.Max(0, Math.Min(ratio, 1.0) * totalWidth);
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
