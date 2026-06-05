using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BG3ModHelper.Services;

/// <summary>
/// Initializes LSLib for pak parsing and stores the BG3MM folder path.
///
/// LSLib is a C++/CLI assembly (cannot be embedded in a single-file exe).
/// It is loaded either from BG3MM's _Lib folder (preferred) or from the
/// exe directory as a fallback when companion DLLs are distributed alongside.
///
/// Loading order:
///   1. Pre-load LSLibNative.dll (native C++/CLI dep) via NativeLibrary.Load.
///   2. Force-load LSLib.dll via Assembly.LoadFrom so the correct path wins.
///   3. SetDllDirectory so Win32 probing also finds the native deps.
///   4. AssemblyResolve fallback for any remaining managed-assembly lookups.
/// </summary>
public static class LibraryLoader
{
    private static bool _initialized = false;
    private static string _bg3mmFolder = "";

    public static string Bg3mmFolder => _bg3mmFolder;
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Must be called at the very start of App.OnStartup(), before any LSLib usage.
    /// </summary>
    /// <param name="preferredLibDir">
    /// Directory to load LSLib from — typically BG3MM's _Lib folder.
    /// Falls back to the exe directory if null or the folder does not exist.
    /// </param>
    public static void InitializeLSLib(string? preferredLibDir = null)
    {
        if (_initialized) return;

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";

        // Prefer the caller-supplied dir (BG3MM _Lib); fall back to exe dir.
        var libDir = !string.IsNullOrEmpty(preferredLibDir) && Directory.Exists(preferredLibDir)
                     ? preferredLibDir
                     : exeDir;

        // 1. Pre-load LSLibNative.dll (native C++/CLI dep) before LSLib.dll loads.
        //    Without this, C++/CLI IJW host can't find the native component.
        var nativePath = Path.Combine(libDir, "LSLibNative.dll");
        if (File.Exists(nativePath))
        {
            try { NativeLibrary.Load(nativePath); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load LSLibNative failed — {ex.Message}"); }
        }
        else
        {
            Logger.Warn($"LibraryLoader: LSLibNative.dll not found at {nativePath}");
        }

        // 2. Force-load LSLib.dll from libDir so it takes priority.
        var lslibPath = Path.Combine(libDir, "LSLib.dll");
        if (File.Exists(lslibPath))
        {
            try { Assembly.LoadFrom(lslibPath); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load LSLib failed — {ex.Message}"); }
        }
        else
        {
            Logger.Warn($"LibraryLoader: LSLib.dll not found at {lslibPath}");
        }

        // 3. Pre-load WebView2Loader.dll from the single-file extraction temp dir
        //    (AppContext.BaseDirectory) BEFORE SetDllDirectory changes the search path.
        //    Without this, Win32 LoadLibrary would look in libDir (BG3MM _Lib) instead
        //    of the temp dir where the bundled DLL was extracted.
        var webview2Path = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
        if (File.Exists(webview2Path))
        {
            try { NativeLibrary.Load(webview2Path); }
            catch (Exception ex) { Logger.Warn($"LibraryLoader: pre-load WebView2Loader failed — {ex.Message}"); }
        }

        // 4. SetDllDirectory so Win32 native loading searches libDir.
        SetDllDirectory(libDir);

        // 5. AssemblyResolve fallback for managed DLLs in libDir.
        //    Native-only DLLs must be skipped — Assembly.LoadFrom on a native DLL
        //    throws BadImageFormatException.
        var nativeOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "LSLibNative", "Ijwhost", "LZ4", "LZ4pn", "ZstdSharp", "WebView2Loader" };

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(name)) return null;
            if (nativeOnly.Contains(name)) return null;   // Win32 handles these
            var path = Path.Combine(libDir, $"{name}.dll");
            if (!File.Exists(path)) return null;
            try { return Assembly.LoadFrom(path); }
            catch (Exception ex)
            {
                Logger.Error($"LibraryLoader: failed to load {name}.dll — {ex.Message}");
                return null;
            }
        };

        _initialized = true;
        Logger.Info($"LibraryLoader: initialized — libDir={libDir}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    /// <summary>
    /// Records the BG3MM folder path (used to launch BG3MM).
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
}
