using System.Windows;
using BG3MM_UpdateHelper.Services;
using BG3MM_UpdateHelper.Views;

namespace BG3MM_UpdateHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Logger.Info("===== Application started =====");

        var settings = SettingsStore.Load();

        // ── Initialize LibraryLoader FIRST ───────────────────────────────
        // This must happen before any code that references LSLib types.
        // It registers the BG3MM _Lib folder as both a native dll search
        // path and a managed assembly resolver target.
        if (!string.IsNullOrEmpty(settings.BG3MMFolderPath))
        {
            var ok = LibraryLoader.Initialize(settings.BG3MMFolderPath);
            if (!ok)
                Logger.Warn("LibraryLoader: _Lib folder missing — .pak parsing disabled");
        }
        else
        {
            Logger.Info("LibraryLoader: BG3MM folder not set yet — will initialize after first-run setup");
        }

        // ── First-run setup ───────────────────────────────────────────────
        if (!SettingsStore.Exists())
        {
            Logger.Info("First run detected — showing setup dialog");
            var firstRun = new FirstRunDialog();

            if (firstRun.ShowDialog() != true || firstRun.Result == null)
            {
                Logger.Info("Setup cancelled — shutting down");
                Shutdown();
                return;
            }

            SettingsStore.Save(firstRun.Result);
            Logger.Info("Initial settings saved");

            // Now that we have a BG3MM path, initialize the library loader
            if (!LibraryLoader.IsInitialized &&
                !string.IsNullOrEmpty(firstRun.Result.BG3MMFolderPath))
            {
                LibraryLoader.Initialize(firstRun.Result.BG3MMFolderPath);
            }
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("===== Application exited =====");
        base.OnExit(e);
    }
}
