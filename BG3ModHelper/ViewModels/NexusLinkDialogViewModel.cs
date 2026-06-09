using System.Diagnostics;
using System.Text.RegularExpressions;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

public class NexusLinkDialogViewModel : ViewModelBase
{
    private readonly string           _pakFileName; // with .pak
    private readonly string           _uuid;
    private readonly string?          _pakFilePath;
    private readonly UserModLinkStore _linkStore;

    private string _urlInput  = "";
    private string _errorText = "";

    /// <param name="existingNexusId">Pre-fill input when editing an existing Nexus link.</param>
    /// <param name="existingExternalUrl">Pre-fill input when editing an existing external URL link.</param>
    public NexusLinkDialogViewModel(
        string           modName,
        string           pakFileName,
        UserModLinkStore linkStore,
        int?             existingNexusId     = null,
        string?          existingExternalUrl = null,
        string           uuid               = "",
        string?          pakFilePath        = null)
    {
        ModName      = modName;
        _pakFileName = pakFileName;
        _uuid        = uuid;
        _pakFilePath = pakFilePath;
        _linkStore   = linkStore;
        IsEditing         = existingNexusId.HasValue || !string.IsNullOrEmpty(existingExternalUrl);

        // Pre-fill with existing value when editing
        if (existingNexusId.HasValue)
            _urlInput = existingNexusId.Value.ToString();
        else if (!string.IsNullOrEmpty(existingExternalUrl))
            _urlInput = existingExternalUrl;

        SearchCommand = new RelayCommand(OpenSearch);
        LinkCommand   = new RelayCommand(TryLink,   () => IsValidInput(UrlInput));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
        RemoveCommand = new RelayCommand(RemoveLink, () => IsEditing);
    }

    public string ModName   { get; }
    /// <summary>True when opened to edit an existing link (shows Remove button).</summary>
    public bool   IsEditing { get; }

    public string UrlInput
    {
        get => _urlInput;
        set
        {
            if (SetField(ref _urlInput, value))
            {
                ErrorText = "";
                LinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            SetField(ref _errorText, value);
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public RelayCommand SearchCommand { get; }
    public RelayCommand LinkCommand   { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }

    /// <summary>Invoked when the dialog should close. true = linked/removed, false = cancelled.</summary>
    public event Action<bool>? CloseRequested;

    // ── Commands ──────────────────────────────────────────────────────────────

    private void OpenSearch()
    {
        var keyword = BuildSearchKeyword(ModName);
        Process.Start(new ProcessStartInfo(
            $"https://www.nexusmods.com/games/baldursgate3/mods?keyword={keyword}")
        { UseShellExecute = true });
    }

    private void TryLink()
    {
        var input = UrlInput.Trim();

        // Case 1: Nexus mod ID (number or Nexus URL)
        var nexusId = ParseModId(input);
        if (nexusId.HasValue)
        {
            // fileId/nexusFileName not known from URL alone — stored as 0/"" until user identifies
            _linkStore.SetNexusLink(_pakFileName, nexusId.Value, uuid: _uuid, pakFilePath: _pakFilePath);
            CloseRequested?.Invoke(true);
            return;
        }

        // Case 2: External URL
        if (IsExternalUrl(input))
        {
            _linkStore.SetExternalLink(_pakFileName, input);
            CloseRequested?.Invoke(true);
            return;
        }

        ErrorText = "Enter a Nexus URL, mod ID, or any external URL (https://...).";
    }

    private void RemoveLink()
    {
        _linkStore.RemoveLink(_pakFileName);
        CloseRequested?.Invoke(true);
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the input is usable: a Nexus mod ID, Nexus URL, or any https:// URL.
    /// </summary>
    private static bool IsValidInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return ParseModId(input).HasValue || IsExternalUrl(input);
    }

    /// <summary>
    /// Returns true for any URL that isn't a Nexus URL (Patreon, GitHub, etc.).
    /// Nexus URLs are handled by ParseModId instead.
    /// </summary>
    private static bool IsExternalUrl(string input) =>
        (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
        !input.Contains("nexusmods.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a Nexus mod ID from:
    ///   - Bare integer: "12345"
    ///   - Any Nexus URL variant: https://www.nexusmods.com/baldursgate3/mods/12345[?...]
    /// Returns null for non-Nexus input.
    /// </summary>
    public static int? ParseModId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Bare integer
        if (int.TryParse(input.Trim(), out var direct) && direct > 0)
            return direct;

        // Nexus URL: /mods/<digits>
        var m = Regex.Match(input, @"/mods/(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var fromUrl) && fromUrl > 0)
            return fromUrl;

        return null;
    }

    /// <summary>
    /// Converts a CamelCase module name into a Nexus-friendly search keyword.
    ///
    /// Strategy:
    ///   1. Split on CamelCase boundaries (including digit transitions and consecutive-caps)
    ///   2. Drop noise tokens: purely numeric, single char, or version-like (v1, v2, 1.0 …)
    ///   3. Take first MaxSearchWords meaningful words (Nexus AND-search: more words = fewer results)
    ///   4. Join with '+' (Nexus AND-search format)
    ///
    /// Examples:
    ///   "AuntieEthelRedone"    → "Auntie+Ethel"
    ///   "ImprovedUI"           → "Improved+UI"
    ///   "BG3Tweaks"            → "BG3+Tweaks"
    ///   "SomeModV2Fix"         → "Some+Mod"      (V2 dropped, capped at 2)
    /// </summary>
    internal static string BuildSearchKeyword(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName)) return "";

        // ── 1. CamelCase split ───────────────────────────────────────────────
        var withSpaces = Regex.Replace(moduleName,
            @"(?<=[a-z])(?=[A-Z])" +
            @"|(?<=[A-Z]{2,})(?=[A-Z][a-z])" +
            @"|(?<=[A-Za-z])(?=[0-9])" +
            @"|(?<=[0-9])(?=[A-Za-z])",
            " ");

        var tokens = withSpaces.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ── 2. Filter noise tokens ───────────────────────────────────────────
        var meaningful = tokens.Where(t => !IsNoiseToken(t)).ToList();
        if (meaningful.Count == 0)
            meaningful = tokens.Take(1).ToList();

        // ── 3. Limit word count ──────────────────────────────────────────────
        // Nexus search is AND-based: more words = fewer (potentially zero) results.
        // Using only the leading N words keeps the query broad enough to get hits.
        // TODO: expose as a user setting, or bump to 3 after real-world testing.
        const int MaxSearchWords = 2;
        meaningful = meaningful.Take(MaxSearchWords).ToList();

        // ── 4. URL-encode each word and join with '+' ────────────────────────
        return string.Join("+", meaningful.Select(Uri.EscapeDataString));
    }

    private static bool IsNoiseToken(string t) =>
        t.Length <= 1 ||
        Regex.IsMatch(t, @"^\d+$") ||
        Regex.IsMatch(t, @"^[Vv]\d+(\.\d+)*$") ||
        Regex.IsMatch(t, @"^\d+\.\d+");
}
