using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BG3MM_UpdateHelper.Views;

/// <summary>bool → Visibility (true=Visible, false=Collapsed)</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → bool (반전)</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}

/// <summary>string key → Style (App.xaml Resources에서 찾음)</summary>
public class StyleKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current.Resources[key] is Style style)
            return style;
        return Application.Current.Resources["PrimaryActionButton"] as Style
               ?? new Style(typeof(System.Windows.Controls.Button));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
