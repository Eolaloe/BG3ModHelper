using Newtonsoft.Json.Converters;

using System.ComponentModel;

namespace DivinityModManager;

[JsonConverter(typeof(StringEnumConverter))]
public enum DivinityGameLaunchWindowAction
{
	[Description("None")]
	None,
	[Description("Minimize")]
	Minimize,
	[Description("Close")]
	Close
}
