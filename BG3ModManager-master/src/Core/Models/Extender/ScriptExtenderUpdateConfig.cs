using DivinityModManager.Extensions;

using System.ComponentModel;
using System.Runtime.Serialization;

namespace DivinityModManager.Models.Extender;

[DataContract]
public class ScriptExtenderUpdateConfig : ReactiveObject
{
	[Reactive] public bool UpdaterIsAvailable { get; set; }
	[Reactive] public int UpdaterVersion { get; set; }

	[DefaultValue(ExtenderUpdateChannel.Release)]
	[SettingsEntry("Update Channel", "Use a specific update channel", HideFromUI = true)]
	[DataMember, Reactive]
	public ExtenderUpdateChannel UpdateChannel { get; set; }

	[DefaultValue("")]
	[SettingsEntry("Target Version", "Update to a specific version of the script extender (ex. '5.0.0.0')")]
	[DataMember, Reactive]
	public string TargetVersion { get; set; }

	[DefaultValue("")]
	[SettingsEntry("Target Resource Digest", "Use a specific Digest for the target update", true)]
	[DataMember, Reactive]
	public string TargetResourceDigest { get; set; }

	[DefaultValue(false)]
	[SettingsEntry("Disable Updates", "Disable automatic updating to the latest extender version")]
	[DataMember, Reactive]
	public bool DisableUpdates { get; set; }

	[DefaultValue(false)]
	[SettingsEntry("IPv4Only", "Use only IPv4 when fetching the latest update")]
	[DataMember, Reactive]
	public bool IPv4Only { get; set; }

	[DefaultValue(false)]
	[SettingsEntry("Debug", "Enable debug mode in the extender updater, which prints more messages to the console window")]
	[DataMember, Reactive]
	public bool Debug { get; set; }

	[DefaultValue("")]
	[SettingsEntry("Manifest URL", "", true)]
	[DataMember, Reactive]
	public string ManifestURL { get; set; }

	[DefaultValue("")]
	[SettingsEntry("Manifest Name", "", true)]
	[DataMember, Reactive]
	public string ManifestName { get; set; }

	[DefaultValue("")]
	[SettingsEntry("CachePath", "", true)]
	[DataMember, Reactive]
	public string CachePath { get; set; }

	[DefaultValue(false)]
	[SettingsEntry("Validate Signature", "", true)]
	[DataMember, Reactive]
	public bool ValidateSignature { get; set; }

	public ScriptExtenderUpdateConfig()
	{
		this.SetToDefault();
		UpdaterVersion = -1;
	}
}
