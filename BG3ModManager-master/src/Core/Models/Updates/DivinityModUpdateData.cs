using System.Windows;

namespace DivinityModManager.Models.Updates;

public class DivinityModUpdateData : ReactiveObject, ISelectable
{
	[Reactive] public DivinityModData LocalMod { get; set; }
	[Reactive] public DivinityModData UpdatedMod { get; set; }
	[Reactive] public bool IsSelected { get; set; }
	[Reactive] public bool IsNewMod { get; set; }
	[Reactive] public bool CanDrag { get; set; }
	[Reactive] public Visibility Visibility { get; set; }
	[Reactive] public ModSourceType Source { get; set; }

	private readonly ObservableAsPropertyHelper<DivinityModData> _primaryModData;
	public DivinityModData PrimaryModData => _primaryModData.Value;

	private readonly ObservableAsPropertyHelper<bool> _isEditorMod;
	public bool IsEditorMod => _isEditorMod.Value;

	private readonly ObservableAsPropertyHelper<string> _author;
	public string Author => _author.Value;

	private readonly ObservableAsPropertyHelper<string> _currentVersion;
	public string CurrentVersion => _currentVersion.Value;

	private readonly ObservableAsPropertyHelper<string> _updateVersion;
	public string UpdateVersion => _updateVersion.Value;

	private readonly ObservableAsPropertyHelper<string> _sourceText;
	public string SourceText => _sourceText.Value;

	private readonly ObservableAsPropertyHelper<string> _updateLink;
	public string UpdateLink => _updateLink.Value;

	private readonly ObservableAsPropertyHelper<string> _localFilePath;
	public string LocalFilePath => _localFilePath.Value;

	private readonly ObservableAsPropertyHelper<string> _updateFilePath;
	public string UpdateFilePath => _updateFilePath.Value;

	private readonly ObservableAsPropertyHelper<DateTime?> _lastModified;
	public DateTime? LastModified => _lastModified.Value;

	private DivinityModData GetNonNull(DivinityModData a, DivinityModData b)
	{
		return a ?? b;
	}

	private string SourceToLink(ValueTuple<DivinityModData, ModSourceType> data)
	{
		if (data.Item1 != null)
		{
			data.Item1.GetURL(data.Item2);
		}
		return "";
	}

	public DivinityModUpdateData()
	{
		Source = ModSourceType.NONE;
		CanDrag = true;
		Visibility = Visibility.Visible;

		//Get whichever mod data isn't null, prioritizing LocalMod
		_primaryModData = this.WhenAnyValue(x => x.LocalMod, x => x.UpdatedMod).Select(x => x.Item1 ?? x.Item2).ToProperty(this, nameof(PrimaryModData));

		_sourceText = this.WhenAnyValue(x => x.Source).Select(x => x.GetDescription()).ToProperty(this, nameof(SourceText));
		_updateLink = this.WhenAnyValue(x => x.UpdatedMod, x => x.Source).Select(SourceToLink).ToProperty(this, nameof(UpdateLink));

		_isEditorMod = this.WhenAnyValue(x => PrimaryModData.IsEditorMod).ToProperty(this, nameof(IsEditorMod));
		_author = this.WhenAnyValue(x => x.PrimaryModData.Author).ToProperty(this, nameof(Author));
		_currentVersion = this.WhenAnyValue(x => x.PrimaryModData.Version.Version).ToProperty(this, nameof(CurrentVersion));
		_localFilePath = this.WhenAnyValue(x => x.PrimaryModData.FilePath).ToProperty(this, nameof(LocalFilePath));

		_lastModified = this.WhenAnyValue(x => x.UpdatedMod.LastModified).ToProperty(this, nameof(LastModified));
		_updateVersion = this.WhenAnyValue(x => x.UpdatedMod.Version.Version).ToProperty(this, nameof(UpdateVersion));

		_updateFilePath = this.WhenAnyValue(x => x.UpdatedMod.FilePath).ToProperty(this, nameof(UpdateFilePath));
	}
}
