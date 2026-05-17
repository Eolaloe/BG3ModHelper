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

        if (!string.IsNullOrEmpty(settings.BG3MMFolderPath))
        {
            var ok = LibraryLoader.Initialize(settings.BG3MMFolderPath);
            if (!ok)
                Logger.Warn("LibraryLoader: _Lib folder missing — .pak parsing disabled");
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // First run: open Settings immediately
        if (!SettingsStore.Exists() || string.IsNullOrEmpty(settings.BG3MMFolderPath))
        {
            Logger.Info("First run detected — opening Settings");
            mainWindow.OpenSettingsOnStartup();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("===== Application exited =====");
        base.OnExit(e);
    }
}
