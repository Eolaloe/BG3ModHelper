using DivinityModManager.Models;

using System.Globalization;
using System.Windows.Data;

namespace DivinityModManager.Converters;

public class ModToDisplayNameConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
	{
		if (value is ModuleShortDesc moduleDesc)
		{
			var registry = Services.Get<IModRegistryService>();
			if (registry != null && registry.TryGetDisplayName(moduleDesc.UUID, out var name))
			{
				return name;
			}
			return moduleDesc.Name;
		}
		else if (value is string uuid)
		{
			var registry = Services.Get<IModRegistryService>();
			if (registry != null && registry.TryGetDisplayName(uuid, out var name))
			{
				return name;
			}
			return uuid;
		}
		return "";
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
	{
		return null;
	}
}
