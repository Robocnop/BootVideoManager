using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootVideoManager.Core.Platform;

namespace BootVideoManager.Core.Account;

/// <summary>
/// Keeps the site session on disk. On Windows the file is encrypted for the current Windows user (DPAPI): the
/// cookies act as a password for the steamdeckrepo.com account, so they must not be readable by anyone else.
/// </summary>
public sealed class SiteSessionStore
{
    private static readonly byte[] Entropy = "BootVideoManager.SiteSession.v1"u8.ToArray();

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly bool _encrypt;

    /// <param name="encrypt">Use DPAPI; defaults to <c>true</c> on Windows (tests pass <c>false</c>).</param>
    public SiteSessionStore(IFileSystem fileSystem, string path, bool? encrypt = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _fileSystem = fileSystem;
        _path = path;
        _encrypt = encrypt ?? OperatingSystem.IsWindows();
    }

    /// <summary>A missing, unreadable or foreign file simply means "not signed in".</summary>
    public SiteSession? Load()
    {
        try
        {
            if (!_fileSystem.File.Exists(_path))
            {
                return null;
            }

            var bytes = _fileSystem.File.ReadAllBytes(_path);
            if (_encrypt && OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            }

            var session = JsonSerializer.Deserialize(bytes, SiteSessionJsonContext.Default.SiteSession);
            return session is { UserId: > 0, Cookies.Count: > 0 } ? session : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    /// <exception cref="IOException">The file could not be written.</exception>
    public void Save(SiteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, SiteSessionJsonContext.Default.SiteSession);
        if (_encrypt && OperatingSystem.IsWindows())
        {
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        }

        JsonFile.WriteAtomically(_fileSystem, _path, bytes);
    }

    public void Delete()
    {
        try
        {
            _fileSystem.File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an unreadable leftover is treated as "not signed in" anyway.
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SiteSession))]
internal sealed partial class SiteSessionJsonContext : JsonSerializerContext;
