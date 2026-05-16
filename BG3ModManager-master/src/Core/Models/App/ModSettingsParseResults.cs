namespace DivinityModManager.Models.App;

public class ModSettingsParseResults
{
	public List<DivinityProfileActiveModData> ActiveMods { get; set; }

	public int CountActive(bool includeIgnoredMods = false)
	{
		var i = 0;
		foreach (var mod in ActiveMods)
		{
			if (includeIgnoredMods || !DivinityApp.IgnoredMods.Lookup( mod.UUID).HasValue)
			{
				i++;
			}
		}
		return i;
	}
}
