using NexusModsNET.DataModels;

namespace DivinityModManager.Models.NexusMods;

public struct NexusModsModDownloadLink
{
	public DivinityModData Mod { get; set; }
	public NexusModFileDownloadLink DownloadLink { get; set; }

	public NexusModsModDownloadLink(DivinityModData mod, NexusModFileDownloadLink link)
	{
		Mod = mod;
		DownloadLink = link;
	}
}
