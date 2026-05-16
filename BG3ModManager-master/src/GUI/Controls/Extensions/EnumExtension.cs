using DivinityModManager.Models.View;

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Windows.Markup;

namespace DivinityModManager.Controls.Extensions;

public class EnumExtension : MarkupExtension
{
	private Type _enumType;

	public EnumExtension(Type enumType)
	{
		if (enumType == null)
			throw new ArgumentNullException("enumType");

		EnumType = enumType;
	}

	public Type EnumType
	{
		get { return _enumType; }
		private set
		{
			if (_enumType == value)
				return;

			var enumType = Nullable.GetUnderlyingType(value) ?? value;

			if (enumType.IsEnum == false)
				throw new ArgumentException("Type must be an Enum.");

			_enumType = value;
		}
	}

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		var enumValues = Enum.GetValues(EnumType);

		var result = new List<EnumEntry>();

		foreach(var enumValue in enumValues)
		{
			var entry = new EnumEntry()
			{
				Value = enumValue
			};
			var field = EnumType.GetField(enumValue.ToString());
			var descriptionAttribute = field.GetCustomAttribute<DescriptionAttribute>(false);

			if (descriptionAttribute == null)
			{
				var displayAttribute = field.GetCustomAttribute<DisplayAttribute>(false);
				if (displayAttribute != null)
				{
					entry.Name = displayAttribute.Name;
					entry.Description = displayAttribute.Description;
				}
			}
			else
			{
				entry.Description = descriptionAttribute.Description;
			}

			result.Add(entry);
		}

		return result;
	}
}
