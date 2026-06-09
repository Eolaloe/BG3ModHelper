using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

/// <summary>
/// Represents a mod that has been manually linked to a Nexus mod ID (in UserModLinkStore)
/// but is NOT currently in the update-check list (e.g. already up to date, or not installed).
/// Shown in the Identified Mods popup alongside UpdateEntryViewModels.
/// </summary>
public class IdentifiedLinkEntryViewModel : ViewModelBase
{
    private readonly UserModLinkStore _linkStore;
    private readonly NexusIdDatabase? _nexusDb;

    public string PakFileName { get; }

    public int NexusModId { get; private set; }

    private string _displayName;
    public string DisplayName
    {
        get => _displayName;
        private set => SetField(ref _displayName, value);
    }

    private string _displayAuthor;
    public string DisplayAuthor
    {
        get => _displayAuthor;
        private set => SetField(ref _displayAuthor, value);
    }

    public string NexusPageUrl => $"https://www.nexusmods.com/baldursgate3/mods/{NexusModId}";

    public RelayCommand ChangeCommand { get; }

    /// <summary>
    /// Fired when the user clicks Change and candidates are available.
    /// The View should open ModDisambiguationDialog and call ApplyChange on confirmation.
    /// </summary>
    public event Action<IdentifiedLinkEntryViewModel, IReadOnlyList<NexusModCandidate>>? ChangeRequested;

    public IdentifiedLinkEntryViewModel(
        string           pakFileName,
        int              nexusModId,
        string?          modName,
        string?          author,
        UserModLinkStore linkStore,
        NexusIdDatabase? nexusDb)
    {
        PakFileName    = pakFileName;
        NexusModId     = nexusModId;
        _displayName   = !string.IsNullOrEmpty(modName) ? modName : pakFileName;
        _displayAuthor = !string.IsNullOrEmpty(author)  ? author  : "";
        _linkStore     = linkStore;
        _nexusDb       = nexusDb;

        ChangeCommand = new RelayCommand(ExecuteChange);
    }

    private void ExecuteChange()
    {
        if (_nexusDb == null) return;

        // UserModLinkStore keys have no extension; NexusIdDatabase is indexed WITH .pak
        var candidates = _nexusDb.LookupByPakFileName(PakFileName + ".pak")
            .Select(e => new NexusModCandidate(
                e.NexusModId,
                e.NexusModName    ?? "",
                e.NexusUploadedBy ?? "",
                e.NexusFileId,
                e.NexusFileName,
                e.NexusFileVersion ?? ""))
            .ToList();

        if (candidates.Count > 0)
            ChangeRequested?.Invoke(this, candidates);
    }

    /// <summary>
    /// Called by the View after the user confirms a new Nexus entry.
    /// Overwrites the existing link in UserModLinkStore and updates the display.
    /// </summary>
    public void ApplyChange(NexusModCandidate chosen)
    {
        _linkStore.SetNexusLink(PakFileName, chosen.ModId);
        NexusModId    = chosen.ModId;
        DisplayName   = !string.IsNullOrEmpty(chosen.ModName) ? chosen.ModName : PakFileName;
        DisplayAuthor = !string.IsNullOrEmpty(chosen.Author)  ? chosen.Author  : "";
        OnPropertyChanged(nameof(NexusModId));
        OnPropertyChanged(nameof(NexusPageUrl));
    }
}
