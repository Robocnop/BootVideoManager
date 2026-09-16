using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BootVideoManager.Core.Api;
using LibVLCSharp.Shared;

namespace BootVideoManager.App.Controls;

/// <summary>
/// Looping video preview rendered by libvlc into a bitmap. Unlike a native video surface, the bitmap composes with
/// the rest of the UI (overlays, dialogs, focus visuals), which matters for controller navigation on the Deck.
/// </summary>
public partial class VideoPreview : UserControl
{
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<VideoPreview, Uri?>(nameof(Source));

    private static readonly string UserAgent = new RepoApiOptions().UserAgent;

    private VlcFrameRenderer? _renderer;
    private WriteableBitmap? _frame;
    private int _presentQueued;
    private bool _isAttached;

    public VideoPreview()
    {
        InitializeComponent();
    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && _isAttached)
        {
            Stop();
            Start();
        }
    }

    private void Start()
    {
        if (Source is not { } source)
        {
            return;
        }

        if (VlcRuntime.TryGet(out var error) is not { } libVlc)
        {
            ShowStatus(error);
            return;
        }

        ShowStatus("Chargement de l'aperçu…");
        var renderer = new VlcFrameRenderer(libVlc, UserAgent);
        renderer.FrameReady += OnFrameReady;
        renderer.PlaybackFailed += OnPlaybackFailed;
        _renderer = renderer;
        renderer.Play(source);
        PauseButton.Content = "Pause";
        MuteButton.Content = "Couper le son";
    }

    private void Stop()
    {
        var renderer = _renderer;
        _renderer = null;
        if (renderer is not null)
        {
            renderer.FrameReady -= OnFrameReady;
            renderer.PlaybackFailed -= OnPlaybackFailed;
            // Stopping libvlc blocks until its threads exit: never do it on the UI thread.
            _ = Task.Run(renderer.Dispose);
        }

        FrameImage.Source = null;
        _frame?.Dispose();
        _frame = null;
        Controls.IsVisible = false;
    }

    /// <summary>Called on a libvlc thread; coalesces frames so the UI thread is never flooded.</summary>
    private void OnFrameReady()
    {
        if (Interlocked.Exchange(ref _presentQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
        }
    }

    private void PresentFrame()
    {
        Interlocked.Exchange(ref _presentQueued, 0);
        if (_renderer is not { } renderer)
        {
            return;
        }

        var previous = _frame;
        if (!renderer.TryCopyFrame(ref _frame))
        {
            return;
        }

        if (!ReferenceEquals(previous, _frame))
        {
            FrameImage.Source = _frame;
            previous?.Dispose();
        }

        StatusText.IsVisible = false;
        Controls.IsVisible = true;
        FrameImage.InvalidateVisual();
    }

    private void OnPlaybackFailed() =>
        Dispatcher.UIThread.Post(() => ShowStatus("Impossible de lire cette vidéo."));

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void OnPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (_renderer is { } renderer)
        {
            PauseButton.Content = renderer.TogglePause() ? "Lecture" : "Pause";
        }
    }

    private void OnMuteClicked(object? sender, RoutedEventArgs e)
    {
        if (_renderer is { } renderer)
        {
            MuteButton.Content = renderer.ToggleMute() ? "Activer le son" : "Couper le son";
        }
    }
}
