using System.IO.Abstractions;

namespace BootVideoManager.Core.Platform;

/// <summary>Crash-safe small file writes.</summary>
internal static class JsonFile
{
    /// <summary>
    /// Writes to a sibling temporary file and renames it over the target, so readers only ever see the
    /// old or the new content, never a truncated file.
    /// </summary>
    public static void WriteAtomically(IFileSystem fileSystem, string path, byte[] bytes)
    {
        var directory = fileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.Directory.CreateDirectory(directory);
        }

        // Unique name: two app instances (or a crash leftover) never collide on the same temporary file.
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            fileSystem.File.WriteAllBytes(temporaryPath, bytes);
            fileSystem.File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                fileSystem.File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Harmless leftover; the original error matters more.
            }

            throw;
        }
    }
}
