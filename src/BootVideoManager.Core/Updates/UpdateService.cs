using System.Buffers;
using System.IO.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.Core.Updates;

/// <summary>Where releases are published.</summary>
public sealed record UpdateOptions
{
    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    public string Owner { get; init; } = "Robocnop";

    public string Repository { get; init; } = "SteamBigStartup_launcher";

    /// <summary>GitHub requires a User-Agent on API calls.</summary>
    public string UserAgent { get; init; } = "BootVideoManager";

    /// <summary>Name of the checksum file attached to every release by the release workflow.</summary>
    public string ChecksumsAssetName { get; init; } = "SHA256SUMS.txt";

    public Uri ReleasesPageUri => new($"https://github.com/{Owner}/{Repository}/releases");
}

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUri, long Size);

/// <summary>A published release newer than the running application.</summary>
/// <param name="Installer">Windows installer for this machine's architecture, if the release has one.</param>
public sealed record AvailableUpdate(
    Version Version,
    string Tag,
    Uri PageUri,
    string Notes,
    ReleaseAsset? Installer,
    ReleaseAsset? Checksums)
{
    public string VersionText => Version.ToString(3);
}

public enum UpdateErrorKind
{
    /// <summary>GitHub unreachable, rate limited or HTTP error.</summary>
    Network,

    /// <summary>Unexpected answer from GitHub.</summary>
    InvalidResponse,

    /// <summary>Downloaded installer does not match the published size or checksum.</summary>
    Verification,

    /// <summary>The installer could not be written to disk.</summary>
    FileSystem,
}

public sealed class UpdateException(UpdateErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpdateErrorKind Kind { get; } = kind;
}

/// <summary>
/// Looks for a newer release on GitHub and downloads its installer, verified against the size and the SHA-256
/// published with the release, so a truncated or tampered file is never run.
/// </summary>
public sealed class UpdateService
{
    private const int BufferSize = 81_920;
    private const long MaxInstallerBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    private readonly IFileSystem _fileSystem;
    private readonly string _downloadDirectory;

    /// <param name="currentVersion">Version of the running application.</param>
    /// <param name="runtimeIdentifier">Selects the installer asset, e.g. <c>win-x64</c>.</param>
    public UpdateService(
        HttpClient http,
        UpdateOptions options,
        IFileSystem fileSystem,
        string downloadDirectory,
        Version currentVersion,
        string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        ArgumentNullException.ThrowIfNull(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);

        _http = http;
        _options = options;
        _fileSystem = fileSystem;
        _downloadDirectory = downloadDirectory;
        CurrentVersion = Normalize(currentVersion);
        RuntimeIdentifier = runtimeIdentifier;
    }

    public Version CurrentVersion { get; }

    public string RuntimeIdentifier { get; }

    /// <summary>Latest stable release if newer than <see cref="CurrentVersion"/>, else <c>null</c>.</summary>
    /// <exception cref="UpdateException">GitHub could not be queried.</exception>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_options.ApiBaseUri, $"repos/{_options.Owner}/{_options.Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        GitHubRelease? release;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // No stable release published yet.
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(
                    UpdateErrorKind.Network,
                    Loc.T($"GitHub a répondu par une erreur HTTP {(int)response.StatusCode}.", $"GitHub answered HTTP {(int)response.StatusCode}."));
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            release = JsonSerializer.Deserialize(body, UpdateJsonContext.Default.GitHubRelease);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorKind.Network, Loc.T("Impossible de joindre GitHub.", "Could not reach GitHub."), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Network, Loc.T("GitHub n'a pas répondu à temps.", "GitHub did not answer in time."), ex);
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorKind.InvalidResponse, Loc.T("La réponse de GitHub est illisible.", "GitHub's answer could not be read."), ex);
        }

        if (release is null || release.Draft || release.Prerelease || TryParseVersion(release.TagName) is not { } version)
        {
            return null;
        }

        if (version <= CurrentVersion)
        {
            return null;
        }

        var assets = (release.Assets ?? [])
            .Where(a => !string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.DownloadUrl, UriKind.Absolute, out _))
            .Select(a => new ReleaseAsset(a.Name!, new Uri(a.DownloadUrl!), a.Size))
            .ToList();

        var installerSuffix = $"-{RuntimeIdentifier}-setup.exe";
        return new AvailableUpdate(
            version,
            release.TagName!,
            Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page) ? page : _options.ReleasesPageUri,
            release.Body ?? string.Empty,
            assets.FirstOrDefault(a => a.Name.EndsWith(installerSuffix, StringComparison.OrdinalIgnoreCase)),
            assets.FirstOrDefault(a => string.Equals(a.Name, _options.ChecksumsAssetName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Downloads the installer of <paramref name="update"/> and checks its size and SHA-256.</summary>
    /// <returns>Local path of the verified installer.</returns>
    /// <exception cref="UpdateException">Missing asset, download failure or verification failure (the file is deleted).</exception>
    public async Task<string> DownloadInstallerAsync(
        AvailableUpdate update,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Installer is not { } installer || update.Checksums is not { } checksums)
        {
            throw new UpdateException(
                UpdateErrorKind.InvalidResponse,
                Loc.T("Cette version ne propose pas d'installateur vérifiable pour ce PC.", "This release has no verifiable installer for this PC."));
        }

        if (installer.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || installer.Size is <= 0 or > MaxInstallerBytes)
        {
            throw new UpdateException(UpdateErrorKind.InvalidResponse, Loc.T("L'installateur publié est invalide.", "The published installer is invalid."));
        }

        var expectedHash = await GetPublishedHashAsync(checksums, installer.Name, cancellationToken).ConfigureAwait(false);
        var path = _fileSystem.Path.Combine(_downloadDirectory, installer.Name);

        try
        {
            if (_fileSystem.Directory.Exists(_downloadDirectory))
            {
                _fileSystem.Directory.Delete(_downloadDirectory, recursive: true); // Old installers are never reused.
            }

            _fileSystem.Directory.CreateDirectory(_downloadDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, Loc.T("Impossible de préparer le dossier de téléchargement.", "Could not prepare the download folder."), ex);
        }

        try
        {
            var hash = await DownloadToFileAsync(installer, path, progress, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(
                    UpdateErrorKind.Verification,
                    Loc.T("L'installateur téléchargé ne correspond pas à l'empreinte publiée : il n'a pas été lancé.", "The downloaded installer does not match the published checksum: it was not run."));
            }

            return path;
        }
        catch
        {
            DeleteQuietly(path);
            throw;
        }
    }

    /// <summary>Parses <c>v1.2.3</c>, <c>1.2</c>…; pre-release suffixes (<c>-beta</c>) and build metadata are ignored.</summary>
    public static Version? TryParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            text = text[..end];
        }

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private async Task<string> GetPublishedHashAsync(ReleaseAsset checksums, string fileName, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, checksums.DownloadUri);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorKind.Network, Loc.T("Impossible de télécharger les empreintes de la version.", "Could not download the release checksums."), ex);
        }

        // sha256sum format: "<hex>  <name>" (a '*' before the name marks binary mode).
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && string.Equals(parts[1].Trim().TrimStart('*'), fileName, StringComparison.Ordinal)
                && parts[0].Length == 64
                && parts[0].All(Uri.IsHexDigit))
            {
                return parts[0];
            }
        }

        throw new UpdateException(
            UpdateErrorKind.Verification,
            Loc.T("L'empreinte de l'installateur est absente de la version publiée.", "The installer checksum is missing from the release."));
    }

    private async Task<string> DownloadToFileAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(
                    UpdateErrorKind.Network,
                    Loc.T($"Le téléchargement de la mise à jour a échoué (HTTP {(int)response.StatusCode}).", $"The update download failed (HTTP {(int)response.StatusCode})."));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long total = 0;
            try
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = _fileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > asset.Size)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Report(new DownloadProgress(total, asset.Size));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (total != asset.Size)
            {
                throw new UpdateException(
                    UpdateErrorKind.Verification,
                    Loc.T("L'installateur téléchargé n'a pas la taille attendue.", "The downloaded installer does not have the expected size."));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateErrorKind.Network, Loc.T("Le téléchargement de la mise à jour a été interrompu.", "The update download was interrupted."), ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, Loc.T("Impossible d'enregistrer la mise à jour sur le disque.", "Could not save the update to disk."), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UpdateException(UpdateErrorKind.FileSystem, Loc.T("Impossible d'enregistrer la mise à jour sur le disque.", "Could not save the update to disk."), ex);
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (_fileSystem.File.Exists(path))
            {
                _fileSystem.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next download.
        }
    }
}

internal sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("assets")]
    public IReadOnlyList<GitHubAsset>? Assets { get; init; }
}

internal sealed record GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
