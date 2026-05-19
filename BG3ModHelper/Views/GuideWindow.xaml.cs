using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BG3ModHelper.Views;

public partial class GuideWindow : Window
{
    private readonly StringBuilder _plainText = new();

    public GuideWindow()
    {
        InitializeComponent();
        BuildContent();
    }

    private void Translate_Click(object sender, RoutedEventArgs e)
    {
        var encoded = Uri.EscapeDataString(_plainText.ToString());
        var url = $"https://translate.google.com/?sl=en&op=translate&text={encoded}";
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void BuildContent()
    {
        Header("BG3 Mod Helper  —  Quick Guide");
        Line("A companion tool for BG3ModManager (BG3MM) that manages mod updates and installations across both Nexus Mods and mod.io from a single interface.");
        Line("Uses API keys from each platform and a community-maintained database to identify installed mods and track their update status without requiring manual input.");
        Spacer();

        Section("Getting Started");
        Line("1.  Open Settings and set your BG3MM folder (where BG3ModManager.exe lives)");
        Line("2.  Add API keys for Nexus Mods and/or mod.io in Settings");
        Line("    (Adding both is strongly recommended — mods are spread across both platforms)");
        ImageLink("    → ", "Nexus Mods API key location", "pack://application:,,,/Assets/nexus.png", "api_nexus.png");
        ImageLink("    → ", "mod.io API key location",   "pack://application:,,,/Assets/modio.png", "api_modio.png");
        Line("3.  Enable the NXM link handler in Settings → Mod Manager Download Links");
        Line("    (Allows 'Mod Manager Download' on Nexus to route files directly to this app)");
        Line("4.  Click [Check for Updates] — installed mods will be scanned and compared");
        Line("5.  Select mods to update and click Download");
        Line("6.  Downloaded mods are installed automatically.");
        Line("    This app does not manage load order — use BG3MM to adjust load order and enjoy. (load order management planned for a future release)");
        Spacer();

        Section("NXM Link Handler");
        Line("Enable in Settings → Mod Manager Download Links.");
        Line("When downloading from Nexus, use 'Mod Manager Download' instead of 'Manual Download' —");
        Line("it routes the file directly to this app for automatic install.");
        Line("Manual Download also works via Folder Watch or Drag & Drop, but Mod Manager Download is the recommended workflow.");
        Line("A Secondary handler forwards non-BG3 links to another manager (e.g. Vortex).");
        Spacer();

        Section("Update Check");
        Line("Scans your mods folder, looks up each mod on Nexus / mod.io, and lists newer versions.");
        Bullet("mod.io / Nexus Premium", "automatic download");
        Bullet("Nexus Free", "mod page opens for manual download");
        Spacer();

        Section("Local Install  (Drag & Drop)");
        Line("Drop .zip / .7z / .rar / .pak files onto the window (or Compact Mode).");
        Line("Archives are extracted automatically; .pak files are placed directly.");
        Spacer();

        Section("Folder Watch");
        Line("Monitors a folder (e.g. your browser download folder) for new archives.");
        Line("Any supported file that appears is queued for install automatically.");
        Line("Enable and set the folder in Settings.");
        Spacer();

        Section("Compact Mode");
        Line("A small floating window for quick drag-and-drop installs.");
        Line("All features available in the main window are also accessible in Compact Mode.");
        Line("Right-click the compact window for options (opacity, size, main window).");
        Spacer();

        Section("Other Features");
        FeatureItem("Auto-backup .pak before overwriting",
                    "Saves a .pak.bak copy alongside the original before it gets replaced. Enable in Settings.");
        FeatureItem("Auto-delete source after install",
                    "Moves the source archive/pak to Recycle Bin after a successful install. Applies to Folder Watch and Drag & Drop. Enable in Settings.");
        Bullet("History", "full log of every download and install ([History] button)");
        Spacer();

        Warning();
        WarnItem("Do NOT rename downloaded archive files (.zip / .7z / .rar) or installed .pak files.",
                 "Both the archive filename and the pak's internal metadata are used to identify mods. Renaming either will break detection.");
        WarnItem("Do NOT modify mod internals (UUID, version metadata, lsx files, etc.).",
                 "Altered mods will not match update data and may cause incorrect behavior.");
        WarnItem("Creating a modified variant of a mod?",
                 "Change its UUID and treat it as a completely separate, independent mod. Never mix it with the original mod's update tracking.");
        Spacer();

        Note();
        NoteItem("Inaccurate update detection",
                 "Update detection uses a combination of factors including the pak filename and version metadata entered by the mod author on Nexus. " +
                 "Author mistakes or upload practices can cause false positives (mod appears in update list when already up-to-date) or false negatives (mod needs updating but does not appear). " +
                 "Common causes include: incorrect version numbers entered on the mod page, " +
                 "or translation mods uploaded under the same pak filename and UUID as the original mod, making the two indistinguishable. " +
                 "Both issues are largely resolved once the mod has been downloaded through this app — " +
                 "either by updating it here, or by using the Mod Manager Download button on its Nexus page to re-download it through this app. " +
                 "After that, detection switches to exact file ID comparison, which is significantly more reliable.");
    }

    // === Layout helpers ===

    private void Add(UIElement element) => GuidePanel.Children.Add(element);

    private void Spacer() => Add(new TextBlock { Height = 6 });

    private void Header(string text)
    {
        Add(new TextBlock
        {
            Text       = text,
            FontSize   = 15,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 2)
        });
        Add(new Separator { Margin = new Thickness(0, 0, 0, 6) });
        _plainText.AppendLine(text).AppendLine();
    }

    private void Section(string text)
    {
        Add(new TextBlock
        {
            Text       = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
            Margin     = new Thickness(0, 0, 0, 2)
        });
        _plainText.AppendLine($"--- {text} ---");
    }

    private void Line(string text)
    {
        Add(new TextBlock
        {
            Text        = text,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 0, 0, 1)
        });
        _plainText.AppendLine(text);
    }

    private void Bullet(string label, string desc)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
        tb.Inlines.Add(new Run("  •  "));
        tb.Inlines.Add(new Run(label) { FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new Run("  —  " + desc));
        Add(tb);
        _plainText.AppendLine($"  • {label} — {desc}");
    }

    private void FeatureItem(string title, string detail)
    {
        var titleTb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
        titleTb.Inlines.Add(new Run("  •  "));
        titleTb.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
        Add(titleTb);

        Add(new TextBlock
        {
            Text         = detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin       = new Thickness(20, 0, 0, 4)
        });
        _plainText.AppendLine($"  • {title}");
        _plainText.AppendLine($"    {detail}");
    }

    private void Warning()
    {
        Add(new TextBlock
        {
            Text       = "⚠  Important Warnings",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x70, 0x20)),
            Margin     = new Thickness(0, 0, 0, 2)
        });
        _plainText.AppendLine("--- Important Warnings ---");
    }

    private void WarnItem(string title, string detail)
    {
        var titleTb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
        titleTb.Inlines.Add(new Run("  •  ") { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x70, 0x20)) });
        titleTb.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
        Add(titleTb);

        Add(new TextBlock
        {
            Text         = detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin       = new Thickness(20, 0, 0, 4)
        });
        _plainText.AppendLine($"  • {title}");
        _plainText.AppendLine($"    {detail}");
    }

    private void Note()
    {
        Add(new TextBlock
        {
            Text       = "ℹ  Notes",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xA0, 0x60)),
            Margin     = new Thickness(0, 0, 0, 2)
        });
        _plainText.AppendLine("--- Notes ---");
    }

    private void NoteItem(string title, string detail)
    {
        var titleTb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
        titleTb.Inlines.Add(new Run("  •  ") { Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0xA0, 0x60)) });
        titleTb.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold });
        Add(titleTb);

        Add(new TextBlock
        {
            Text         = detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin       = new Thickness(20, 0, 0, 4)
        });
        _plainText.AppendLine($"  • {title}");
        _plainText.AppendLine($"    {detail}");
    }

    private void ImageLink(string prefix, string label, string logoUri, string assetFileName)
    {
        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", assetFileName);

        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
        tb.Inlines.Add(new Run(prefix));

        var logo = new System.Windows.Controls.Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(logoUri)),
            Width  = 16, Height = 16,
            Margin = new Thickness(0, 0, 4, -3),
            Stretch = System.Windows.Media.Stretch.Uniform,
        };

        var link = new Hyperlink { Cursor = System.Windows.Input.Cursors.Help };
        link.Inlines.Add(new InlineUIContainer(logo));
        link.Inlines.Add(new Run(label));

        if (System.IO.File.Exists(path))
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(path));
            var img = new System.Windows.Controls.Image
            {
                Source  = bmp,
                Stretch = System.Windows.Media.Stretch.None,
            };
            link.ToolTip = new ToolTip
            {
                Content           = img,
                Padding           = new Thickness(4),
                PlacementTarget   = tb,
                HasDropShadow     = true,
            };
            ToolTipService.SetShowDuration(link, 30000);
            ToolTipService.SetInitialShowDelay(link, 200);
        }

        tb.Inlines.Add(link);
        Add(tb);
        _plainText.AppendLine(prefix + label);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
