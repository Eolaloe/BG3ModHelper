namespace DivinityModManager;

[Flags]
public enum DivinityExtenderModStatus
{
	None,
	Supports,
	Fulfilled,
	DisabledFromConfig,
	MissingRequiredVersion,
	MissingAppData,
	MissingUpdater,
}
