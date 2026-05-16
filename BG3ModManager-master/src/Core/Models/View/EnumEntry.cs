using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DivinityModManager.Models.View;

public class EnumEntry : ReactiveObject
{
	[Reactive] public string Name { get; set; }
	[Reactive] public string Description { get; set; }
	[Reactive] public object Value { get; set; }

	public EnumEntry(string description, object value, string name = "")
	{
		Name = name;
		Description = description;
		Value = value;
	}

	public EnumEntry(Enum enumValue)
	{
		Value = enumValue;
		Name = "";
		Description = "";
		var member = enumValue.GetType().GetMember(enumValue.ToString()).FirstOrDefault();
		if (member != null)
		{
			var descriptionAttribute = member.GetCustomAttribute<DescriptionAttribute>(false);

			if (descriptionAttribute == null)
			{
				var displayAttribute = member.GetCustomAttribute<DisplayAttribute>(false);
				if (displayAttribute != null)
				{
					Name = displayAttribute.Name;
					Description = displayAttribute.Description;
				}
			}
			else
			{
				Name = descriptionAttribute.Description;
			}
		}
	}

	public EnumEntry() { }
}
