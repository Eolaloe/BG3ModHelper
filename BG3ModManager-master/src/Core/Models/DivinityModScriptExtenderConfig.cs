using DynamicData;
using DynamicData.Binding;

using System.Runtime.Serialization;

namespace DivinityModManager.Models;

[DataContract]
public class DivinityModScriptExtenderConfig : ReactiveObject
{
	[DataMember][Reactive] public int RequiredVersion { get; set; }
	[DataMember][Reactive] public string ModTable { get; set; }

	[DataMember] public ObservableCollectionExtended<string> FeatureFlags { get; set; }

	private ObservableAsPropertyHelper<int> _totalFeatureFlags;
	public int TotalFeatureFlags => _totalFeatureFlags.Value;

	private ObservableAsPropertyHelper<bool> _hasAnySettings;
	public bool HasAnySettings => _hasAnySettings.Value;

	public bool Lua => FeatureFlags.Contains("Lua");

	public DivinityModScriptExtenderConfig()
	{
		RequiredVersion = -1;
		FeatureFlags = new ObservableCollectionExtended<string>();
		var featureFlagsConnection = FeatureFlags.ToObservableChangeSet();
		_totalFeatureFlags = featureFlagsConnection.Count().ToProperty(this, nameof(TotalFeatureFlags), 0);
		_hasAnySettings = this.WhenAnyValue(x => x.RequiredVersion, x => x.TotalFeatureFlags, x => x.ModTable)
			.Select(x => x.Item1 > -1 || x.Item2 > 0 || !String.IsNullOrEmpty(x.Item3)).ToProperty(this, nameof(HasAnySettings));
	}
}
