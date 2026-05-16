using DivinityModManager.Models;

using System.Globalization;
using System.Windows.Data;

namespace DivinityModManager.Converters;

public class ModIsActiveConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is IDivinityModData data)
		{
			var registry = Services.Get<IModRegistryService>();
			if (registry != null && registry.ModIsActive(data.UUID))
			{
				return true;
			}
		}

		return false;
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return null;
	}
}
