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
    private readonly string           _uuid;
    private readonly string?          _pakFilePath;

    /// <summary>Pak file name including .pak extension.</summary>
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
        NexusIdDatabase? nexusDb,
        string           uuid        = "",
        string?          pakFilePath = null)
    {
        PakFileName    = pakFileName;
        NexusModId     = nexusModId;
        _displayName   = !string.IsNullOrEmpty(modName) ? modName : pakFileName;
        _displayAuthor = !string.IsNullOrEmpty(author)  ? author  : "";
        _linkStore     = linkStore;
        _nexusDb       = nexusDb;
        _uuid          = uuid;
        _pakFilePath   = pakFilePath;

        ChangeCommand = new RelayCommand(ExecuteChange);
    }

    private void ExecuteChange()
    {
        if (_nexusDb == null) return;

        // PakFileName already includes .pak — pass directly to DB lookup
        var candidates = _nexusDb.LookupByPakFileName(PakFileName)
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

    /// <summary>Fired when the user clicks Unlink; the View removes this entry from the identified list.</summary>
    public event Action<IdentifiedLinkEntryViewModel>? RemoveFromListRequested;

    /// <summary>
    /// Removes the stored Nexus link for this pak and asks the View to drop this entry from the list.
    /// </summary>
    public void ApplyUnlink()
    {
        _linkStore.RemoveLink(PakFileName);
        RemoveFromListRequested?.Invoke(this);
    }

    /// <summary>
    /// Called by the View after the user confirms a new Nexus entry.
    /// Overwrites the existing link in UserModLinkStore and updates the display.
    /// </summary>
    public void ApplyChange(NexusModCandidate chosen)
    {
        _linkStore.SetNexusLink(
            PakFileName,
            chosen.ModId,
            uuid:          _uuid,
            fileId:        chosen.FileId,
            nexusFileName: chosen.FileName,
            pakFilePath:   _pakFilePath);

        NexusModId    = chosen.ModId;
        DisplayName   = !string.IsNullOrEmpty(chosen.ModName) ? chosen.ModName : PakFileName;
        DisplayAuthor = !string.IsNullOrEmpty(chosen.Author)  ? chosen.Author  : "";
        OnPropertyChanged(nameof(NexusModId));
        OnPropertyChanged(nameof(NexusPageUrl));
    }
}
