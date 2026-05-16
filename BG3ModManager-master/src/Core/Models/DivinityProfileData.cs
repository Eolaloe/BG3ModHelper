

using DivinityModManager.Util;

using DynamicData;

namespace DivinityModManager.Models;

public class DivinityProfileData : ReactiveObject
{
	[Reactive] public string Name { get; set; }

	/// <summary>
	/// The stored name in the profile.lsb or profile5.lsb file.
	/// </summary>
	[Reactive] public string ProfileName { get; set; }
	[Reactive] public string UUID { get; set; }
	[Reactive] public string ModSettingsFile { get; set; }

	[Reactive] public string Folder { get; private set; }

	/// <summary>
	/// The mod data under the Mods node, from modsettings.lsx.
	/// </summary>
	public List<DivinityProfileActiveModData> ActiveMods { get; set; }

	public DivinityLoadOrder GetLoadOrder(SourceCache<DivinityModData, string> mods)
	{
		var order = new DivinityLoadOrder() { Name = "Current", FilePath = Path.Combine(Folder, "modsettings.lsx"), IsModSettings = true };
		var i = 0;
		foreach (var activeMod in ActiveMods)
		{
			var mod = mods.Items.FirstOrDefault(m => m.UUID.Equals(activeMod.UUID, StringComparison.OrdinalIgnoreCase));
			if (mod != null)
			{
				order.Add(mod);
			}
			i++;
		}
		return order;
	}

	public DivinityProfileData(string uuid, string modSettingsFile)
	{
		UUID = uuid;
		ModSettingsFile = modSettingsFile;
		ActiveMods = [];

		this.WhenAnyValue(x => x.ModSettingsFile).Select(DivinityFileUtils.GetParentOrEmpty).BindTo(this, x => x.Folder);
	}
}
