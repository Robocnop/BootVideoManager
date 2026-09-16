using Avalonia.Media.Imaging;
using BootVideoManager.Core.Caching;

namespace BootVideoManager.App.Services;

/// <summary>Loads cached thumbnails as bitmaps, decoded off the UI thread at display size.</summary>
public static class BitmapLoader
{
    /// <summary>Returns <c>null</c> when there is no image or it cannot be decoded; the UI shows a placeholder.</summary>
    public static async Task<Bitmap?> LoadAsync(ThumbnailCache cache, Uri? uri, int decodeWidth)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (uri is null || await cache.GetAsync(uri) is not { } path)
        {
            return null;
        }

        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality);
            });
        }
#pragma warning disable CA1031 // A corrupt or unsupported image must only cost a placeholder, whatever the decoder throws.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
