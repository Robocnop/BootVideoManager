using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace BootVideoManager.App.Controls;

/// <summary>Process-wide libvlc instance, created lazily so a missing libvlc only disables previews.</summary>
public static class VlcRuntime
{
    private static readonly Lock Gate = new();
    private static LibVLC? _libVlc;
    private static string? _error;

    /// <returns>The shared instance, or <c>null</c> with a user-facing <paramref name="error"/>.</returns>
    public static LibVLC? TryGet(out string? error)
    {
        lock (Gate)
        {
            if (_libVlc is null && _error is null)
            {
                try
                {
                    LibVLCSharp.Shared.Core.Initialize();
                    _libVlc = new LibVLC("--no-video-title-show", "--quiet");
                }
                catch (Exception ex) when (ex is VLCException or DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
                {
                    _error = OperatingSystem.IsWindows()
                        ? "Aperçu indisponible : impossible de charger les composants de VLC."
                        : "Aperçu indisponible : libvlc est introuvable. Installez VLC avec le gestionnaire de paquets de votre système ou utilisez la version Flatpak de l'application.";
                    Console.Error.WriteLine(ex);
                }
            }

            error = _error;
            return _libVlc;
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}

/// <summary>
/// Plays a media with libvlc's video callbacks: frames are decoded into an unmanaged BGRA buffer, then copied into a
/// <see cref="WriteableBitmap"/> on the UI thread.
/// </summary>
public sealed class VlcFrameRenderer : IDisposable
{
    /// <summary>Frames wider than this are downscaled by libvlc; previews do not need more.</summary>
    private const uint MaxWidth = 1280;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly string _userAgent;
    private readonly SemaphoreSlim _bufferLock = new(1, 1);

    // Delegates are stored in fields so the garbage collector never frees them while native code holds them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

    private IntPtr _buffer;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private bool _hasNewFrame;
    private int _disposed;

    public VlcFrameRenderer(LibVLC libVlc, string userAgent)
    {
        _libVlc = libVlc;
        _userAgent = userAgent;
        _player = new MediaPlayer(libVlc) { Volume = 70 };

        _formatCallback = OnFormat;
        _cleanupCallback = OnCleanup;
        _lockCallback = OnLock;
        _unlockCallback = OnUnlock;
        _displayCallback = OnDisplay;

        _player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        _player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        _player.EncounteredError += (_, _) => PlaybackFailed?.Invoke();
    }

    /// <summary>Raised on a libvlc thread when a new frame is available.</summary>
    public event Action? FrameReady;

    /// <summary>Raised on a libvlc thread when the media cannot be played.</summary>
    public event Action? PlaybackFailed;

    public void Play(Uri uri)
    {
        using var media = new Media(_libVlc, uri, ":input-repeat=65535", $":http-user-agent={_userAgent}");
        _player.Play(media);
    }

    /// <returns><c>true</c> when paused.</returns>
    public bool TogglePause()
    {
        _player.SetPause(_player.IsPlaying);
        return !_player.IsPlaying;
    }

    /// <returns><c>true</c> when muted.</returns>
    public bool ToggleMute()
    {
        _player.Mute = !_player.Mute;
        return _player.Mute;
    }

    /// <summary>Copies the latest decoded frame into <paramref name="bitmap"/>, replacing it if the size changed.</summary>
    /// <returns><c>false</c> if no new frame was available.</returns>
    public unsafe bool TryCopyFrame(ref WriteableBitmap? bitmap)
    {
        if (Volatile.Read(ref _disposed) == 1 || !_bufferLock.Wait(0))
        {
            return false;
        }

        try
        {
            if (!_hasNewFrame || _buffer == IntPtr.Zero)
            {
                return false;
            }

            var size = new PixelSize((int)_width, (int)_height);
            if (bitmap is null || bitmap.PixelSize != size)
            {
                bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            using var framebuffer = bitmap.Lock();
            var source = (byte*)_buffer;
            var destination = (byte*)framebuffer.Address;
            var bytesPerRow = (long)_width * 4;
            for (var row = 0; row < _height; row++)
            {
                Buffer.MemoryCopy(source + (row * _pitch), destination + (row * framebuffer.RowBytes), framebuffer.RowBytes, bytesPerRow);
            }

            _hasNewFrame = false;
            return true;
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        FrameReady = null;
        PlaybackFailed = null;
        _player.Stop();
        _player.Dispose();

        _bufferLock.Wait();
        try
        {
            FreeBuffer();
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (width > MaxWidth)
        {
            height = (uint)Math.Max(2, Math.Round(height * (double)MaxWidth / width));
            width = MaxWidth;
        }

        // RV32 = 32-bit BGRA on little-endian hosts; sizes aligned to 32 as libvlc recommends.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        var alignedWidth = Align32(width);
        var alignedHeight = Align32(height);
        pitches = alignedWidth * 4;
        lines = alignedHeight;

        _bufferLock.Wait();
        try
        {
            FreeBuffer();
            _buffer = Marshal.AllocHGlobal((nint)((long)pitches * alignedHeight));
            _width = width;
            _height = height;
            _pitch = pitches;
            _hasNewFrame = false;
        }
        finally
        {
            _bufferLock.Release();
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        _bufferLock.Wait();
        try
        {
            FreeBuffer();
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        _bufferLock.Wait();
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        _hasNewFrame = true;
        _bufferLock.Release();
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture) => FrameReady?.Invoke();

    private void FreeBuffer()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    private static uint Align32(uint value) => (value + 31) & ~31u;
}
