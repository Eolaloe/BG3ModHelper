using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BG3ModHelper.Services;

/// <summary>
/// Manages LSLib initialization and BG3MM folder tracking.
///
/// LSLib is a C++/CLI assembly and cannot be loaded from a memory buffer —
/// it must live at a stable on-disk path. Strategy:
///
///   1. LSLib.dll + its 5 companion DLLs are embedded as EmbeddedResources.
///   2. On startup, any missing files are extracted to
///      %LocalAppData%\BG3ModHelper\Lib\ (handles first run and upgrades).
///   3. LibraryLoader loads everything from that fixed path.
///
/// BG3MM folder is stored separately and used only as a launch shortcut.
/// </summary>
public static class LibraryLoader
{
    private static bool   _initialized  = false;
    private static string _bg3mmFolder  = "";

    public static string Bg3mmFolder   => _bg3mmFolder;
    public static bool   IsInitialized => _initialized;

    // The six DLLs that make up LSLib's runtime.
    private static readonly string[] _lslibFiles =
    [
        "LSLib.dll", "LSLibNative.dll", "Ijwhost.dll",
        "LZ4.dll",   "LZ4pn.dll",       "ZstdSharp.dll",
        "WebView2Loader.dll"
    ];

    /// <summary>
    /// Stable on-disk directory where LSLib DLLs are extracted.
    /// Matches the existing logs/settings directory root.
    /// </summary>
    private static string LibDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BG3ModHelper", "Lib");

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Must be called at the very start of App.OnStartup(), before any LSLib usage.
    /// Extracts missing DLLs from embedded resources, then loads LSLib.
    /// </summary>
    public static void InitializeLSLib()
    {
        if (_initialized) return;

        var libDir = LibDir;

        // 1. Extract any missing LSLib files from embedded resources.
        ExtractLibsIfNeeded(libDir);

        // 2. Pre-load LSLibNative.dll (C++/CLI native component).
        //    Must happen before LSLib.dll is loaded so the IJW host finds it.
        var nativePath = Path.Combine(libDir, "LSLibNative.dll");
        if (File.Exists(nativePath))
        {
            try   { NativeLibrary.Load(nativePath); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load LSLibNative failed — {ex.Message}"); }
        }
        else
        {
            Logger.Warn($"LibraryLoader: LSLibNative.dll not found at {nativePath}");
        }

        // 3. Pre-load WebView2Loader.dll from libDir BEFORE SetDllDirectory.
        //    WebView2 is loaded lazily but SetDllDirectory must already point here.
        var webview2Path = Path.Combine(libDir, "WebView2Loader.dll");
        if (File.Exists(webview2Path))
        {
            try   { NativeLibrary.Load(webview2Path); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load WebView2Loader failed — {ex.Message}"); }
        }

        // 4. SetDllDirectory so Win32 native loading also searches libDir.
        //    This covers Ijwhost.dll and any other native deps LSLib needs.
        SetDllDirectory(libDir);

        // 5. Force-load LSLib.dll from libDir.
        var lslibPath = Path.Combine(libDir, "LSLib.dll");
        if (File.Exists(lslibPath))
        {
            try   { Assembly.LoadFrom(lslibPath); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load LSLib failed — {ex.Message}"); }
        }
        else
        {
            Logger.Warn($"LibraryLoader: LSLib.dll not found at {lslibPath}");
        }

        // 6. AssemblyResolve fallback for managed DLLs in libDir.
        //    Native-only DLLs must be skipped — Assembly.LoadFrom on a native DLL
        //    throws BadImageFormatException.
        var nativeOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "LSLibNative", "Ijwhost", "WebView2Loader" };

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(name)) return null;
            if (nativeOnly.Contains(name)) return null;   // Win32 handles these
            var path = Path.Combine(libDir, $"{name}.dll");
            if (!File.Exists(path)) return null;
            try   { return Assembly.LoadFrom(path); }
            catch (Exception ex)
            {
                Logger.Error($"LibraryLoader: failed to load {name}.dll — {ex.Message}");
                return null;
            }
        };

        _initialized = true;

        // Diagnostic summary: confirm all DLLs are present on disk
        var missing = _lslibFiles.Where(f => !File.Exists(Path.Combine(libDir, f))).ToList();
        if (missing.Count == 0)
            Logger.Info($"LibraryLoader: initialized — libDir={libDir} — all {_lslibFiles.Length} DLLs present");
        else
            Logger.Warn($"LibraryLoader: initialized with missing DLLs — {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Records the BG3MM folder path (used only to launch BG3MM).
    /// </summary>
    public static bool SetBg3mmFolder(string bg3mmFolder)
    {
        if (string.IsNullOrWhiteSpace(bg3mmFolder)) return false;
        if (!Directory.Exists(bg3mmFolder))
        {
            Logger.Warn($"LibraryLoader: BG3MM folder not found at {bg3mmFolder}");
            return false;
        }
        _bg3mmFolder = bg3mmFolder;
        Logger.Info($"LibraryLoader: BG3MM folder set to {bg3mmFolder}");
        return true;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts any missing LSLib DLLs from embedded resources to <paramref name="libDir"/>.
    /// Runs on every startup — no-ops instantly when all files are already present.
    /// </summary>
    private static void ExtractLibsIfNeeded(string libDir)
    {
        // Fast path: all files present → nothing to do.
        if (_lslibFiles.All(f => File.Exists(Path.Combine(libDir, f)))) return;

        Logger.Info($"LibraryLoader: extracting LSLib files to {libDir} ...");

        try
        {
            Directory.CreateDirectory(libDir);
        }
        catch (Exception ex)
        {
            Logger.Error($"LibraryLoader: failed to create lib dir — {ex.Message}");
            return;
        }

        var asm = Assembly.GetExecutingAssembly();

        foreach (var fileName in _lslibFiles)
        {
            var dest = Path.Combine(libDir, fileName);
            if (File.Exists(dest)) continue;   // already extracted

            using var stream = asm.GetManifestResourceStream(fileName);
            if (stream == null)
            {
                Logger.Warn($"LibraryLoader: embedded resource '{fileName}' not found");
                continue;
            }

            try
            {
                using var file = File.Create(dest);
                stream.CopyTo(file);
                Logger.Info($"LibraryLoader: extracted {fileName}");
            }
            catch (Exception ex)
            {
                Logger.Error($"LibraryLoader: failed to extract {fileName} — {ex.Message}");
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
