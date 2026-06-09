using System.Diagnostics;
using System.Text.RegularExpressions;
using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

// ── per-row ViewModel ─────────────────────────────────────────────────────────

public class InstalledModEntryViewModel : ViewModelBase
{
    private readonly InstalledMod     _mod;
    private readonly FileIdEntry?     _fileId;
    private readonly NexusIdDatabase  _nexusDb;
    private readonly UserModLinkStore _linkStore;

    public InstalledModEntryViewModel(
        InstalledMod     mod,
        FileIdEntry?     fileId,
        string           modioProfileUrl,
        int              loadOrderIndex,
        NexusIdDatabase  nexusDb,
        UserModLinkStore linkStore)
    {
        _mod       = mod;
        _fileId    = fileId;
        _nexusDb   = nexusDb;
        _linkStore = linkStore;

        ModioPageUrl   = modioProfileUrl;
        LoadOrderIndex = loadOrderIndex;

        OpenNexusCommand      = new RelayCommand(OpenNexus,      () => EffectiveNexusModId.HasValue);
        OpenModioCommand      = new RelayCommand(OpenModio,      () => CanOpenModio);
        OpenExternalCommand   = new RelayCommand(OpenExternal,   () => HasExternalLink);
        OpenLinkDialogCommand = new RelayCommand(OpenLinkDialog);
        EditLinkCommand       = new RelayCommand(EditLinkDialog);
    }

    public string ModName      => string.IsNullOrWhiteSpace(_mod.MetaModuleName) ? "(unknown)" : _mod.MetaModuleName;
    public string PakFile      => _mod.PakFileName;
    public string Author       => string.IsNullOrWhiteSpace(_mod.MetaAuthor)     ? "—"         : _mod.MetaAuthor;
    public int    LoadOrderIndex { get; }

    // ── Version display ──────────────────────────────────────────────────────
    public string MetaVersion    => string.IsNullOrWhiteSpace(_mod.MetaVersion) ? "—" : _mod.MetaVersion;
    public string VersionTooltip => _fileId != null && !string.IsNullOrWhiteSpace(_fileId.NexusFileName)
                                     ? $"Nexus file: {_fileId.NexusFileName}\nPAK meta: {_mod.MetaVersion}"
                                     : _mod.MetaVersion;

    // ── Effective Nexus mod ID: user override → scan result → fileId fallback ─
    private int? EffectiveNexusModId
    {
        get
        {
            // 1. User-supplied Nexus link always wins
            var userLink = _linkStore.GetNexusModId(PakFileNameNoExt);
            if (userLink.HasValue) return userLink;

            // 2. Scan result
            if (_mod.NexusModId.HasValue) return _mod.NexusModId;

            // 3. FileId store fallback
            if (_fileId?.NexusModId > 0) return _fileId!.NexusModId;

            return null;
        }
    }

    /// <summary>User-supplied external URL (Patreon, GitHub, etc.) — reference only, no auto-update.</summary>
    private string? ExternalUrl => _linkStore.GetExternalUrl(PakFileNameNoExt);
    public  bool    HasExternalLink    => !string.IsNullOrEmpty(ExternalUrl);
    public  string  ExternalUrlDisplay => ExternalUrl ?? "";

    private string PakFileNameNoExt =>
        _mod.PakFileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            ? _mod.PakFileName[..^4]
            : _mod.PakFileName;

    // ── Unlink detection ─────────────────────────────────────────────────────
    // "Unlink": PAK appears in community DB but no UUID match → user can manually link
    private bool IsDbMatchAvailable
    {
        get
        {
            // If we already have a Nexus ID resolved, no need to show Unlink badge
            if (EffectiveNexusModId.HasValue) return false;
            if (_mod.ModioPublishHandle > 0)  return false;

            // Check: PAK filename known to DB but could not be matched by UUID?
            // NOTE: LookupByPakFileName expects the filename as stored in DB (with .pak),
            //       same as all other callers (MainWindowViewModel, UpdateChecker).
            var entries = _nexusDb.LookupByPakFileName(_mod.PakFileName);
            return entries.Count > 0;
        }
    }

    // ── Source detection ─────────────────────────────────────────────────────
    public bool HasNexus    => EffectiveNexusModId.HasValue;
    public bool HasModio    => _mod.ModioPublishHandle > 0;
    public bool IsUnlinked  => IsDbMatchAvailable;          // in DB, but not matched yet
    public bool IsOthers    => !HasNexus && !HasModio && !IsUnlinked;  // truly unknown

    // Keep IsUnknown as alias so any existing bindings don't break
    public bool IsUnknown   => IsOthers;

    // Sort key: Nexus=0, mod.io=1, Both=2, Others=3, Unlink=4
    public int SourceOrder =>
        HasNexus && !HasModio ?  0 :
        HasModio && !HasNexus ?  1 :
        HasNexus && HasModio  ?  2 :
        IsOthers              ?  3 : 4;  // IsUnlinked → 4

    // Sort key for Links column: manually linked=0, auto-linked=1, mod.io only=2, none=3
    public int LinkOrder =>
        IsManuallyLinked      ?  0 :
        HasNexus              ?  1 :
        HasModio              ?  2 : 3;

    // ── Links ────────────────────────────────────────────────────────────────
    public string NexusIdDisplay     => EffectiveNexusModId?.ToString() ?? "";
    public string ModioHandleDisplay => _mod.ModioPublishHandle > 0 ? _mod.ModioPublishHandle.ToString() : "";
    public bool   CanOpenNexus       => EffectiveNexusModId.HasValue;

    /// <summary>True when a Nexus ID or external URL was set manually by the user.</summary>
    public bool IsManuallyLinked => _linkStore.HasAnyLink(PakFileNameNoExt);

    public string ModioPageUrl { get; }
    public bool   CanOpenModio => HasModio && !string.IsNullOrEmpty(ModioPageUrl);

    public RelayCommand OpenNexusCommand      { get; }
    public RelayCommand OpenModioCommand      { get; }
    public RelayCommand OpenExternalCommand   { get; }
    public RelayCommand OpenLinkDialogCommand { get; }
    public RelayCommand EditLinkCommand       { get; }

    private void OpenNexus()
    {
        var id = EffectiveNexusModId;
        if (id.HasValue)
            Process.Start(new ProcessStartInfo(
                $"https://www.nexusmods.com/baldursgate3/mods/{id.Value}")
            { UseShellExecute = true });
    }

    private void OpenModio()
    {
        if (!string.IsNullOrEmpty(ModioPageUrl))
            Process.Start(new ProcessStartInfo(ModioPageUrl) { UseShellExecute = true });
    }

    private void OpenExternal()
    {
        var url = ExternalUrl;
        if (!string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenLinkDialog() => OpenLinkDialogCore(existingNexusId: null, existingExternalUrl: null);
    private void EditLinkDialog() => OpenLinkDialogCore(
        existingNexusId:     _linkStore.GetNexusModId(PakFileNameNoExt),
        existingExternalUrl: _linkStore.GetExternalUrl(PakFileNameNoExt));

    private void OpenLinkDialogCore(int? existingNexusId, string? existingExternalUrl)
    {
        var dlgVm = new NexusLinkDialogViewModel(
            ModName, _mod.PakFileName, _linkStore, existingNexusId, existingExternalUrl,
            uuid: _mod.MetaUuid ?? "", pakFilePath: _mod.PakFilePath);
        var dlg = new Views.NexusLinkDialog(dlgVm);
        dlg.ShowDialog();

        // Refresh all source-related properties after dialog closes
        OnPropertyChanged(nameof(HasNexus));
        OnPropertyChanged(nameof(IsUnlinked));
        OnPropertyChanged(nameof(IsOthers));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(IsManuallyLinked));
        OnPropertyChanged(nameof(HasExternalLink));
        OnPropertyChanged(nameof(NexusIdDisplay));
        OnPropertyChanged(nameof(CanOpenNexus));
        OpenNexusCommand.RaiseCanExecuteChanged();
        OpenExternalCommand.RaiseCanExecuteChanged();
    }
}

// ── window ViewModel ─────────────────────────────────────────────────────────

public enum SortMode { LoadOrder, NameAsc, NameDesc, AuthorAsc, AuthorDesc, SourceAsc, SourceDesc, LinkAsc, LinkDesc }

public class InstalledModsViewModel : ViewModelBase
{
    private readonly List<InstalledModEntryViewModel> _activeByLoadOrder;
    private readonly List<InstalledModEntryViewModel> _inactiveByName;
    private SortMode _sortMode          = SortMode.LoadOrder;
    private SortMode _inactiveSortMode  = SortMode.NameAsc;

    public InstalledModsViewModel(
        IEnumerable<InstalledMod> mods,
        ModFileIdStore            fileIdStore,
        NexusIdDatabase           nexusDb,
        UserModLinkStore          linkStore)
    {
        // 1. Parse load order from modsettings.lsx
        var lsxPath     = ModSettingsParser.GetDefaultPath();
        var orderList   = ModSettingsParser.GetLoadOrder(lsxPath);
        var uuidToOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < orderList.Count; i++)
            uuidToOrder[orderList[i].ToLowerInvariant()] = i;

        // 2. Load mod.io cache
        var modioCache = ModioApi.LoadCache();

        // 3. Build all entry VMs
        var allEntries = mods.Select(m =>
        {
            var modioUrl = modioCache.Mods.TryGetValue(m.MetaUuid, out var cached)
                           ? cached.ModioProfileUrl : "";
            var idx = uuidToOrder.TryGetValue((m.MetaUuid ?? "").ToLowerInvariant(), out var order)
                      ? order : -1;
            return new InstalledModEntryViewModel(
                m, fileIdStore.GetEntry(m.MetaUuid ?? ""), modioUrl, idx, nexusDb, linkStore);
        }).ToList();

        // 4. Split into active (in load order) and unactivated
        _activeByLoadOrder = allEntries
            .Where(e => e.LoadOrderIndex >= 0)
            .OrderBy(e => e.LoadOrderIndex)
            .ToList();

        _inactiveByName = allEntries
            .Where(e => e.LoadOrderIndex == -1)
            .OrderBy(e => e.ModName)
            .ToList();

        _activeEntries   = _activeByLoadOrder;
        _inactiveEntries = _inactiveByName;

        CloseCommand                      = new RelayCommand(() => CloseRequested?.Invoke());
        ToggleNameSortCommand             = new RelayCommand(ToggleNameSort);
        ToggleAuthorSortCommand           = new RelayCommand(ToggleAuthorSort);
        ToggleSourceSortCommand           = new RelayCommand(ToggleSourceSort);
        ToggleLinkSortCommand             = new RelayCommand(ToggleLinkSort);
        ResetSortCommand                  = new RelayCommand(ResetSort);
        ToggleInactiveNameSortCommand     = new RelayCommand(ToggleInactiveNameSort);
        ToggleInactiveAuthorSortCommand   = new RelayCommand(ToggleInactiveAuthorSort);
        ToggleInactiveSourceSortCommand   = new RelayCommand(ToggleInactiveSourceSort);
        ToggleInactiveLinkSortCommand     = new RelayCommand(ToggleInactiveLinkSort);
    }

    // ── ActiveEntries ────────────────────────────────────────────────────────

    private List<InstalledModEntryViewModel> _activeEntries = [];
    public List<InstalledModEntryViewModel> ActiveEntries
    {
        get => _activeEntries;
        private set => SetField(ref _activeEntries, value);
    }

    // ── InactiveEntries ──────────────────────────────────────────────────────

    private List<InstalledModEntryViewModel> _inactiveEntries = [];
    public List<InstalledModEntryViewModel> InactiveEntries
    {
        get => _inactiveEntries;
        private set => SetField(ref _inactiveEntries, value);
    }

    public bool   HasInactive    => _inactiveByName.Count > 0;
    public string ActiveHeader   => $"Active Mods ({_activeByLoadOrder.Count})";
    public string InactiveHeader => $"Inactive Mods ({_inactiveByName.Count})";

    public string HeaderTitle       => "Installed Mods";
    public string HeaderCountLine   =>
        $"{_activeByLoadOrder.Count + _inactiveByName.Count} mods  ·  " +
        $"Active {_activeByLoadOrder.Count}  ·  " +
        $"Inactive {_inactiveByName.Count}";
    public string HeaderSourceLine
    {
        get
        {
            var all       = _activeByLoadOrder.Concat(_inactiveByName).ToList();
            var nexusOnly = all.Count(e =>  e.HasNexus && !e.HasModio);
            var modioOnly = all.Count(e => !e.HasNexus &&  e.HasModio);
            var both      = all.Count(e =>  e.HasNexus &&  e.HasModio);
            var unlinked  = all.Count(e =>  e.IsUnlinked);
            var others    = all.Count(e =>  e.IsOthers);
            var parts     = new List<string>
            {
                $"Nexus {nexusOnly}",
                $"mod.io {modioOnly}",
                $"Both {both}",
                $"Others {others}",
            };
            if (unlinked > 0)
                parts.Add($"Unlink {unlinked}");
            return string.Join("  ·  ", parts);
        }
    }

    // ── Active sort header texts (↕ = no sort applied to this column) ────────
    public string NameHeaderText => _sortMode switch
    {
        SortMode.NameAsc  => "Name ↑",
        SortMode.NameDesc => "Name ↓",
        _                 => "Name ⇅"
    };
    public string AuthorHeaderText => _sortMode switch
    {
        SortMode.AuthorAsc  => "Author ↑",
        SortMode.AuthorDesc => "Author ↓",
        _                   => "Author ⇅"
    };
    public string SourceHeaderText => _sortMode switch
    {
        SortMode.SourceAsc  => "Source ↑",
        SortMode.SourceDesc => "Source ↓",
        _                   => "Source ⇅"
    };
    public string LinkHeaderText => _sortMode switch
    {
        SortMode.LinkAsc  => "Links ↑",
        SortMode.LinkDesc => "Links ↓",
        _                 => "Links ⇅"
    };

    // ── Inactive sort header texts ───────────────────────────────────────────
    public string InactiveNameHeaderText => _inactiveSortMode switch
    {
        SortMode.NameAsc  => "Name ↑",
        SortMode.NameDesc => "Name ↓",
        _                 => "Name ⇅"
    };
    public string InactiveAuthorHeaderText => _inactiveSortMode switch
    {
        SortMode.AuthorAsc  => "Author ↑",
        SortMode.AuthorDesc => "Author ↓",
        _                   => "Author ⇅"
    };
    public string InactiveSourceHeaderText => _inactiveSortMode switch
    {
        SortMode.SourceAsc  => "Source ↑",
        SortMode.SourceDesc => "Source ↓",
        _                   => "Source ⇅"
    };
    public string InactiveLinkHeaderText => _inactiveSortMode switch
    {
        SortMode.LinkAsc  => "Links ↑",
        SortMode.LinkDesc => "Links ↓",
        _                 => "Links ⇅"
    };

    public string CountDisplay => $"{_activeByLoadOrder.Count + _inactiveByName.Count} mod(s)";

    public RelayCommand CloseCommand                      { get; }
    public RelayCommand ToggleNameSortCommand             { get; }
    public RelayCommand ToggleAuthorSortCommand           { get; }
    public RelayCommand ToggleSourceSortCommand           { get; }
    public RelayCommand ToggleLinkSortCommand             { get; }
    public RelayCommand ResetSortCommand                  { get; }
    public RelayCommand ToggleInactiveNameSortCommand     { get; }
    public RelayCommand ToggleInactiveAuthorSortCommand   { get; }
    public RelayCommand ToggleInactiveSourceSortCommand   { get; }
    public RelayCommand ToggleInactiveLinkSortCommand     { get; }

    public event Action? CloseRequested;

    // ── Active sort ──────────────────────────────────────────────────────────

    private void ToggleNameSort()
    {
        _sortMode = _sortMode == SortMode.NameAsc ? SortMode.NameDesc : SortMode.NameAsc;
        ApplySort();
    }

    private void ToggleAuthorSort()
    {
        _sortMode = _sortMode == SortMode.AuthorAsc ? SortMode.AuthorDesc : SortMode.AuthorAsc;
        ApplySort();
    }

    private void ToggleSourceSort()
    {
        _sortMode = _sortMode == SortMode.SourceAsc ? SortMode.SourceDesc : SortMode.SourceAsc;
        ApplySort();
    }

    private void ToggleLinkSort()
    {
        _sortMode = _sortMode == SortMode.LinkAsc ? SortMode.LinkDesc : SortMode.LinkAsc;
        ApplySort();
    }

    private void ResetSort()
    {
        _sortMode        = SortMode.LoadOrder;
        _inactiveSortMode = SortMode.NameAsc;
        ApplySort();
        ApplyInactiveSort();
    }

    private void ApplySort()
    {
        ActiveEntries = _sortMode switch
        {
            SortMode.NameAsc    => _activeByLoadOrder.OrderBy(e => e.ModName).ToList(),
            SortMode.NameDesc   => _activeByLoadOrder.OrderByDescending(e => e.ModName).ToList(),
            SortMode.AuthorAsc  => _activeByLoadOrder.OrderBy(e => e.Author).ToList(),
            SortMode.AuthorDesc => _activeByLoadOrder.OrderByDescending(e => e.Author).ToList(),
            SortMode.SourceAsc  => _activeByLoadOrder.OrderBy(e => e.SourceOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.SourceDesc => _activeByLoadOrder.OrderByDescending(e => e.SourceOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.LinkAsc    => _activeByLoadOrder.OrderBy(e => e.LinkOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.LinkDesc   => _activeByLoadOrder.OrderByDescending(e => e.LinkOrder).ThenBy(e => e.ModName).ToList(),
            _                   => _activeByLoadOrder
        };
        OnPropertyChanged(nameof(NameHeaderText));
        OnPropertyChanged(nameof(AuthorHeaderText));
        OnPropertyChanged(nameof(SourceHeaderText));
        OnPropertyChanged(nameof(LinkHeaderText));
    }

    // ── Inactive sort ────────────────────────────────────────────────────────

    private void ToggleInactiveNameSort()
    {
        _inactiveSortMode = _inactiveSortMode == SortMode.NameAsc
            ? SortMode.NameDesc : SortMode.NameAsc;
        ApplyInactiveSort();
    }

    private void ToggleInactiveAuthorSort()
    {
        _inactiveSortMode = _inactiveSortMode == SortMode.AuthorAsc
            ? SortMode.AuthorDesc : SortMode.AuthorAsc;
        ApplyInactiveSort();
    }

    private void ToggleInactiveSourceSort()
    {
        _inactiveSortMode = _inactiveSortMode == SortMode.SourceAsc
            ? SortMode.SourceDesc : SortMode.SourceAsc;
        ApplyInactiveSort();
    }

    private void ToggleInactiveLinkSort()
    {
        _inactiveSortMode = _inactiveSortMode == SortMode.LinkAsc
            ? SortMode.LinkDesc : SortMode.LinkAsc;
        ApplyInactiveSort();
    }

    private void ApplyInactiveSort()
    {
        InactiveEntries = _inactiveSortMode switch
        {
            SortMode.NameAsc    => _inactiveByName,
            SortMode.NameDesc   => _inactiveByName.OrderByDescending(e => e.ModName).ToList(),
            SortMode.AuthorAsc  => _inactiveByName.OrderBy(e => e.Author).ToList(),
            SortMode.AuthorDesc => _inactiveByName.OrderByDescending(e => e.Author).ToList(),
            SortMode.SourceAsc  => _inactiveByName.OrderBy(e => e.SourceOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.SourceDesc => _inactiveByName.OrderByDescending(e => e.SourceOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.LinkAsc    => _inactiveByName.OrderBy(e => e.LinkOrder).ThenBy(e => e.ModName).ToList(),
            SortMode.LinkDesc   => _inactiveByName.OrderByDescending(e => e.LinkOrder).ThenBy(e => e.ModName).ToList(),
            _                   => _inactiveByName
        };
        OnPropertyChanged(nameof(InactiveNameHeaderText));
        OnPropertyChanged(nameof(InactiveAuthorHeaderText));
        OnPropertyChanged(nameof(InactiveSourceHeaderText));
        OnPropertyChanged(nameof(InactiveLinkHeaderText));
    }
}
