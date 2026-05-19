using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BG3MM_UpdateHelper.Views;

/// <summary>bool → Visibility (true=Visible, false=Collapsed)</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → bool (inverted)</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}

/// <summary>double (0~1) → SolidColorBrush #2D2D30 with alpha applied</summary>
public class OpacityToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var alpha = value is double d ? (byte)(d * 255) : (byte)255;
        return new SolidColorBrush(Color.FromArgb(alpha, 0x2D, 0x2D, 0x30));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Full path → middle-ellipsis: C:\first\...\last\segment</summary>
public class PathEllipsisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return value ?? "";

        var parts = path.Split(System.IO.Path.DirectorySeparatorChar,
                               StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 4) return path;

        // C:\first\...\second-to-last\last
        var head = parts[0] + System.IO.Path.DirectorySeparatorChar;
        var tail = string.Join(System.IO.Path.DirectorySeparatorChar,
                               parts[^2], parts[^1]);
        return $"{head}...\\{tail}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>string key → Style (resolved from App.xaml Resources)</summary>
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
