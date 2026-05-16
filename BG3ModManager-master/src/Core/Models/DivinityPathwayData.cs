
using DivinityModManager.Extensions;

namespace DivinityModManager.Models;

public class DivinityPathwayData : ReactiveObject
{
	/// <summary>
	/// The path to the root game folder, i.e. SteamLibrary\steamapps\common\Baldur's Gate 3
	/// </summary>
	[Reactive] public string InstallPath { get; set; }

	/// <summary>
	/// The path to %LOCALAPPDATA%\Larian Studios\Baldur's Gate 3
	/// </summary>
	[Reactive] public string AppDataGameFolder { get; set; }

	/// <summary>
	/// The path to %LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\Mods
	/// </summary>
	[Reactive] public string AppDataModsPath { get; set; }

	/// <summary>
	/// The path to %LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\PlayerProfiles
	/// </summary>
	[Reactive] public string AppDataProfilesPath { get; set; }

	[Reactive] public string LastSaveFilePath { get; set; }

	[Reactive] public string ScriptExtenderLatestReleaseUrl { get; set; }
	[Reactive] public string ScriptExtenderLatestReleaseVersion { get; set; }

	public DivinityPathwayData()
	{
		InstallPath = "";
		AppDataGameFolder = "";
		AppDataModsPath = "";
		LastSaveFilePath = "";
		ScriptExtenderLatestReleaseUrl = "";
		ScriptExtenderLatestReleaseVersion = "";
	}

	public string ScriptExtenderSettingsFile(DivinityModManagerSettings settings)
	{
		if (settings.GameExecutablePath.IsExistingFile())
		{
			return Path.Combine(Path.GetDirectoryName(settings.GameExecutablePath), DivinityApp.EXTENDER_CONFIG_FILE);
		}
		return "";
	}

	public string ScriptExtenderUpdaterConfigFile(DivinityModManagerSettings settings)
	{
		if (settings.GameExecutablePath.IsExistingFile())
		{
			return Path.Combine(Path.GetDirectoryName(settings.GameExecutablePath), DivinityApp.EXTENDER_UPDATER_CONFIG_FILE);
		}
		return "";
	}
}
