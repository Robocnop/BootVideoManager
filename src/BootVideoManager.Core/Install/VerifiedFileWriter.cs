using System.Buffers;
using System.IO.Abstractions;
using System.Security.Cryptography;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.Core.Install;

/// <summary>
/// Writes a video to its temporary file while hashing it and checking the WebM signature as early as possible.
/// Data can be appended over several transfers (resumed downloads) or discarded to start over.
/// </summary>
internal sealed class VerifiedFileWriter : IAsyncDisposable
{
    /// <summary>Every WebM (Matroska/EBML) file starts with these bytes.</summary>
    private static readonly byte[] WebmSignature = [0x1A, 0x45, 0xDF, 0xA3];

    private const int BufferSize = 81_920;

    private readonly Stream _destination;
    private readonly long _maxBytes;
    private readonly byte[] _signature = new byte[WebmSignature.Length];
    private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _signatureLength;

    private VerifiedFileWriter(Stream destination, long maxBytes)
    {
        _destination = destination;
        _maxBytes = maxBytes;
    }

    /// <summary>Bytes written so far.</summary>
    public long Length { get; private set; }

    /// <exception cref="InstallException">The temporary file cannot be created.</exception>
    public static VerifiedFileWriter Create(IFileSystem fileSystem, string path, long maxBytes)
    {
        try
        {
            return new VerifiedFileWriter(
                fileSystem.FileStream.New(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true),
                maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WriteFailure(ex);
        }
    }

    /// <summary>
    /// Appends everything <paramref name="source"/> yields. Read failures propagate as they are (the caller decides
    /// whether to resume); write failures become <see cref="InstallException"/>.
    /// </summary>
    public async Task CopyFromAsync(Stream source, long? expectedTotal, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                CheckSignature(buffer.AsSpan(0, read));

                if (Length + read > _maxBytes)
                {
                    throw new InstallException(InstallErrorKind.InvalidFile, Loc.T("La vidéo est anormalement volumineuse.", "The video is abnormally large."));
                }

                try
                {
                    await _destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw WriteFailure(ex);
                }

                _hash.AppendData(buffer, 0, read);
                Length += read;
                progress?.Report(new DownloadProgress(Length, expectedTotal));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Discards what was written, e.g. when the server ignored a resume request and sends the whole file.</summary>
    public void Reset()
    {
        try
        {
            _destination.SetLength(0);
            _destination.Position = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WriteFailure(ex);
        }

        _hash.Dispose();
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _signatureLength = 0;
        Length = 0;
    }

    /// <summary>Flushes the file and returns its SHA-256 and size.</summary>
    /// <exception cref="InstallException">The content is too short to be a WebM video, or cannot be flushed.</exception>
    public async Task<(string Sha256, long Size)> CompleteAsync()
    {
        if (_signatureLength < WebmSignature.Length)
        {
            throw NotWebm();
        }

        try
        {
            await _destination.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WriteFailure(ex);
        }

        return (Convert.ToHexStringLower(_hash.GetHashAndReset()), Length);
    }

    public async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        await _destination.DisposeAsync().ConfigureAwait(false);
    }

    private void CheckSignature(ReadOnlySpan<byte> data)
    {
        if (_signatureLength >= WebmSignature.Length)
        {
            return;
        }

        var count = Math.Min(data.Length, WebmSignature.Length - _signatureLength);
        data[..count].CopyTo(_signature.AsSpan(_signatureLength));
        _signatureLength += count;
        if (_signatureLength == WebmSignature.Length && !_signature.AsSpan().SequenceEqual(WebmSignature))
        {
            throw NotWebm();
        }
    }

    private static InstallException NotWebm() =>
        new(InstallErrorKind.InvalidFile, Loc.T("Ce fichier n'est pas une vidéo WebM.", "This file is not a WebM video."));

    private static InstallException WriteFailure(Exception ex) =>
        new(InstallErrorKind.FileSystem, Loc.T("Impossible d'écrire la vidéo sur le disque.", "Could not write the video to disk."), ex);
}
