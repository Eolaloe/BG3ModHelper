using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DivinityModManager;

public static class EnumExtensions
{
	/// <summary>
	/// Get an enum's Description attribute value.
	/// </summary>
	public static string GetDescription(this Enum enumValue)
	{
		var member = enumValue.GetType().GetMember(enumValue.ToString()).FirstOrDefault();
		if (member != null)
		{
			var descriptionAttribute = member.GetCustomAttribute<DescriptionAttribute>(false);

			if (descriptionAttribute == null)
			{
				var displayAttribute = member.GetCustomAttribute<DisplayAttribute>(false);
				if (displayAttribute != null)
				{
					return displayAttribute.Name;
				}
			}
			else
			{
				return descriptionAttribute.Description;
			}
			return enumValue.ToString();
		}
		return "";
	}
}
