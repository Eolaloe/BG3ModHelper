using System.Windows;
using System.Windows.Media;
using BG3MM_UpdateHelper.Services;

namespace BG3MM_UpdateHelper.ViewModels;

public class InstallConfirmViewModel : ViewModelBase
{
    private readonly ArchiveSourceInfo _info;
    private string _selectedSource;
    private int    _total;

    public InstallConfirmViewModel(ArchiveSourceInfo info, int currentIndex = 1, int total = 1,
                                   IReadOnlyList<string>? pendingNames = null)
    {
        _info           = info;
        _total          = total;
        CurrentIndex    = currentIndex;
        _selectedSource = info.Source == "Both"
            ? (info.ZipHintSource ?? (info.NexusModId.HasValue ? "Nexus" : "ModIO"))
            : info.Source;

        if (pendingNames != null)
            _queueModNames.AddRange(pendingNames);

        InstallCommand     = new RelayCommand(() => { DialogResult = true;  CloseRequested?.Invoke(); });
        IgnoreCommand      = new RelayCommand(() => { DialogResult = false; CloseRequested?.Invoke(); });
        SwitchCommand      = new RelayCommand(SwitchSource, () => CanSwitch);
        InstallAllCommand  = new RelayCommand(OnInstallAll, () => ShowQueueInfo);
        IgnoreAllCommand   = new RelayCommand(OnIgnoreAll, () => ShowQueueInfo);
    }

    // === Queue info ===

    public int  CurrentIndex { get; }

    public int Total
    {
        get => _total;
        private set
        {
            _total = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QueueDisplay));
            OnPropertyChanged(nameof(ShowQueueInfo));
        }
    }

    private readonly List<string> _queueModNames = new();

    public string QueueDisplay  => Total > 1 ? $"({CurrentIndex}/{Total})" : "";
    public bool   ShowQueueInfo => Total > 1;

    public string QueueTooltip
    {
        get
        {
            if (_queueModNames.Count == 0) return "";
            var lines = _queueModNames
                .Select((name, i) => $"{CurrentIndex + 1 + i}. {name}");
            return "Pending:\n" + string.Join("\n", lines);
        }
    }

    /// <summary>Called when a new item is added to the queue while this popup is open.</summary>
    public void NotifyTotalChanged(int newTotal, IReadOnlyList<string> pendingNames)
    {
        _queueModNames.Clear();
        _queueModNames.AddRange(pendingNames);
        Total = newTotal;
        OnPropertyChanged(nameof(QueueTooltip));
        InstallAllCommand.RaiseCanExecuteChanged();
        IgnoreAllCommand.RaiseCanExecuteChanged();
    }

    // === Display ===

    public string  ModName     => _info.ModName;
    public string  ModVersion  => _info.ModVersion;
    public bool    IsRequired  => _info.Confidence == "Required";
    public bool    CanSwitch   => _info.Confidence != "Confirmed";

    public string HeaderText => _info.Confidence switch
    {
        "Required"  => "This mod is found on both platforms.\nWhere did you download it from?",
        "Estimated" => _info.Source == "Both"
            ? "This mod is registered on both platforms."
            : "A new mod has been detected.",
        _           => "A new mod has been detected."
    };

    public string SelectedSource
    {
        get => _selectedSource;
        private set
        {
            _selectedSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BadgeColor));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public Color BadgeColor => SelectedSource switch
    {
        "Nexus" => Color.FromRgb(0xd9, 0x82, 0x00),
        "ModIO" => Color.FromRgb(0x1a, 0x7f, 0xd4),
        _       => Color.FromRgb(0x88, 0x88, 0x88)
    };

    public string BadgeText => SelectedSource switch
    {
        "ModIO"  => "mod.io",
        "Others" => "Others",
        _        => SelectedSource
    };

    public bool NexusSelected => SelectedSource == "Nexus";
    public bool ModioSelected => SelectedSource == "ModIO";

    // === Result ===

    public bool   DialogResult    { get; private set; }
    public string FinalSource     => SelectedSource;
    public bool   InstallAllFired { get; private set; }

    // === Commands ===

    public RelayCommand InstallCommand    { get; }
    public RelayCommand IgnoreCommand     { get; }
    public RelayCommand SwitchCommand     { get; }
    public RelayCommand InstallAllCommand { get; }
    public RelayCommand IgnoreAllCommand  { get; }

    public event Action?       CloseRequested;
    public event Func<Task>?   InstallAllRequested;
    public event Action?       IgnoreAllRequested;

    // === Actions ===

    private void SwitchSource()
    {
        SelectedSource = SelectedSource switch
        {
            "Nexus"  => "ModIO",
            "ModIO"  => "Others",
            _        => "Nexus"
        };
        OnPropertyChanged(nameof(NexusSelected));
        OnPropertyChanged(nameof(ModioSelected));
    }

    public void SelectNexus()
    {
        SelectedSource = "Nexus";
        OnPropertyChanged(nameof(NexusSelected));
        OnPropertyChanged(nameof(ModioSelected));
    }

    public void SelectModio()
    {
        SelectedSource = "ModIO";
        OnPropertyChanged(nameof(NexusSelected));
        OnPropertyChanged(nameof(ModioSelected));
    }

    private void OnInstallAll()
    {
        InstallAllFired = true;
        DialogResult    = true;
        _ = InstallAllRequested?.Invoke();
    }

    private void OnIgnoreAll()
    {
        DialogResult = false;
        IgnoreAllRequested?.Invoke();
    }
}
