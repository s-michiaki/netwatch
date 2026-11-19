using System.Globalization;
using System.Windows.Data;

namespace NetWatch.ViewModels;

/// <summary>割合（0〜1）× 親の幅 → バーの幅。</summary>
public sealed class ShareWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double share = values.Length > 0 && values[0] is double s ? s : 0;
        double width = values.Length > 1 && values[1] is double w ? w : 0;
        return Math.Max(0, share * width);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
