using System.IO;
using System.IO.Pipes;
using System.Windows;
using BG3ModHelper.Services;
using BG3ModHelper.Views;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BG3ModHelper;

public partial class App : Application
{
    private const string MutexName = "BG3ModHelper_SingleInstance";
    private const string PipeName  = "BG3MMUH_IPC";

    // Mutex must be a field (not local) — local would be GC'd, releasing the mutex
    private Mutex?                   _instanceMutex;
    private CancellationTokenSource? _ipcCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        // === Single instance check ===
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Second instance — we don't own the mutex, dispose it and exit
            _instanceMutex.Dispose();
            _instanceMutex = null;

            TrySendArgsToFirstInstance(e.Args);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        JsonConvert.DefaultSettings = () => new JsonSerializerSettings {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented
        };

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Error($"Unhandled UI exception: {ex.Exception}");
            ex.Handled = true;
            System.Windows.MessageBox.Show(
                $"Unexpected error:\n{ex.Exception.Message}",
                "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        };

        Logger.Info("===== Application started =====");

        // === Start IPC server (background) ===
        _ipcCts = new CancellationTokenSource();
        _ = Task.Run(() => IpcServerLoop(_ipcCts.Token));

        var settings = SettingsStore.Load();

        if (!string.IsNullOrEmpty(settings.BG3MMFolderPath))
        {
            var ok = LibraryLoader.Initialize(settings.BG3MMFolderPath);
            if (!ok)
                Logger.Warn("LibraryLoader: _Lib folder missing === .pak parsing disabled");
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
        mainWindow.Activate();

        // First run: open Settings immediately
        if (!SettingsStore.Exists() || string.IsNullOrEmpty(settings.BG3MMFolderPath))
        {
            Logger.Info("First run detected === opening Settings");
            mainWindow.OpenSettingsOnStartup();
        }

        // Restore last mode
        if (settings.LastModeIsCompact)
        {
            Logger.Info("Restoring compact mode from last session");
            (mainWindow.DataContext as BG3ModHelper.ViewModels.MainWindowViewModel)?
                .EnterCompactCommand.Execute(null);
        }

        // First-instance args may contain nxm URL (clicked nxm:// while app not running)
        if (e.Args.Length > 0)
            HandleIncomingArgs(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("===== Application exited =====");

        try
        {
            _ipcCts?.Cancel();
            _ipcCts?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"OnExit cleanup: {ex.Message}");
        }

        base.OnExit(e);
    }

    // === Second instance — forward args to first ===

    private static void TrySendArgsToFirstInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName,
                PipeDirection.Out, PipeOptions.None);
            client.Connect(2000);

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(string.Join("|", args));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to forward args to first instance: {ex.Message}");
        }
    }

    // === First instance — IPC server loop ===

    private async Task IpcServerLoop(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName,
                    PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancel);

                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(cancel);
                if (string.IsNullOrEmpty(line)) continue;

                var args = line.Split('|');

                // Dispatch to UI thread before handling
                await Dispatcher.InvokeAsync(() => HandleIncomingArgs(args));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warn($"IPC server error: {ex.Message}");
                try { await Task.Delay(500, cancel); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Handles incoming command-line args (first-instance startup or second-instance IPC).
    /// Dispatches nxm:// URLs to NxmDispatcher.
    /// </summary>
    private static void HandleIncomingArgs(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info($"Received nxm URL: {arg}");
                NxmDispatcher.Dispatch(arg);
            }
            else
            {
                Logger.Info($"Received arg: {arg}");
            }
        }
    }
}
