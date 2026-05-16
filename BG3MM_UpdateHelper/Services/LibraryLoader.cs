using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Dynamically loads LSLib and its native dependencies from the BG3MM _Lib folder
/// at runtime, so the helper ships as a single .exe with no bundled dlls.
/// 
/// Must be initialized at the very start of App.OnStartup(), before any code
/// that references LSLib types — otherwise the CLR will fail to resolve them.
/// </summary>
public static class LibraryLoader
{
    private static bool _initialized = false;
    private static string _libFolder = "";

    /// <summary>
    /// Registers the BG3MM _Lib folder as a native dll search path and
    /// hooks AssemblyResolve so managed dlls (LSLib, SharpGLTF, etc.)
    /// are loaded from there on demand.
    /// </summary>
    /// <param name="bg3mmFolder">Folder containing BG3ModManager.exe.</param>
    /// <returns>True if the _Lib folder was found and registered.</returns>
    public static bool Initialize(string bg3mmFolder)
    {
        if (_initialized) return true;
        if (string.IsNullOrWhiteSpace(bg3mmFolder)) return false;

        var libFolder = Path.Combine(bg3mmFolder, "_Lib");
        if (!Directory.Exists(libFolder))
        {
            Logger.Warn($"LibraryLoader: _Lib folder not found at {libFolder}");
            return false;
        }

        _libFolder = libFolder;

        // ── Native dlls (LSLibNative.dll, Ijwhost.dll, LZ4.dll …) ──────────
        // SetDllDirectory tells Windows to search this folder when loading
        // any native dll, which covers all P/Invoke and C++/CLI loads.
        SetDllDirectory(libFolder);

        // ── Managed dlls (LSLib.dll, SharpGLTF.*.dll …) ─────────────────────
        // AssemblyResolve fires when the CLR cannot locate an assembly via
        // its normal probing paths. We redirect it to _Lib.
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        _initialized = true;
        Logger.Info($"LibraryLoader: initialized from {libFolder}");
        return true;
    }

    public static bool IsInitialized => _initialized;

    // ── Private helpers ───────────────────────────────────────────────────

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name)) return null;

        var path = Path.Combine(_libFolder, $"{name}.dll");
        if (!File.Exists(path)) return null;

        try
        {
            return Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            Logger.Error($"LibraryLoader: failed to load {name}.dll — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Adds a directory to the native dll search path for the current process.
    /// This is the simplest and most reliable way to cover all native loads
    /// including C++/CLI hosting (Ijwhost) and LSLibNative.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}
