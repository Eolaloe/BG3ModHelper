using System.IO;

namespace BG3ModHelper.Services;

/// <summary>
/// File I/O utilities shared across services.
/// </summary>
internal static class FileHelper
{
    /// <summary>
    /// Writes <paramref name="content"/> to a uniquely-named temp file alongside
    /// <paramref name="path"/>, then atomically replaces the target via a rename.
    /// Using a GUID suffix prevents concurrent saves to the same file from sharing
    /// the same temp path. Prevents partial-write corruption if the process is killed mid-save.
    /// Note: lost-update races (two concurrent readers each writing stale state) require
    /// per-store locking and are not addressed here.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content, System.Text.Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }
}
