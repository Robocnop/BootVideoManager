using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BootVideoManager.App.Localization;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.App.Controls;

/// <summary>
/// Looping video preview rendered by libvlc into a bitmap. Unlike a native video surface, the bitmap composes with
/// the rest of the UI (overlays, dialogs, focus visuals), which matters for controller navigation on the Deck.
/// </summary>
public partial class VideoPreview : UserControl
{
    public static readonly StyledProperty<Uri?> SourceProperty =
        AvaloniaProperty.Register<VideoPreview, Uri?>(nameof(Source));

    /// <summary>0 to 100.</summary>
    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<VideoPreview, double>(nameof(Volume), 70, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<VideoPreview, bool>(nameof(IsMuted), defaultBindingMode: BindingMode.TwoWay);

    private static readonly string UserAgent = new RepoApiOptions().UserAgent;

    private VlcFrameRenderer? _renderer;
    private WriteableBitmap? _frame;
    private int _presentQueued;
    private bool _isAttached;

    public VideoPreview()
    {
        InitializeComponent();
        VolumeSlider.Value = Volume;
        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                Volume = VolumeSlider.Value;
            }
        };
    }

    public Uri? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
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
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && _isAttached)
        {
            Stop();
            Start();
        }
        else if (change.Property == VolumeProperty)
        {
            if (Math.Abs(VolumeSlider.Value - Volume) > 0.01)
            {
                VolumeSlider.Value = Volume;
            }

            _renderer?.SetVolume(VolumeLevel);
        }
        else if (change.Property == IsMutedProperty)
        {
            MuteButton.Content = IsMuted ? Strings.Unmute : Strings.Mute;
            _renderer?.SetMute(IsMuted);
        }
    }

    private int VolumeLevel => (int)Math.Round(Math.Clamp(Volume, 0, 100));

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

        ShowStatus(Strings.PreviewLoading);
        var renderer = new VlcFrameRenderer(libVlc, UserAgent);
        renderer.FrameReady += OnFrameReady;
        renderer.PlaybackFailed += OnPlaybackFailed;
        renderer.SetVolume(VolumeLevel);
        renderer.SetMute(IsMuted);
        _renderer = renderer;
        renderer.Play(source);
        PauseButton.Content = Strings.Pause;
        MuteButton.Content = IsMuted ? Strings.Unmute : Strings.Mute;
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
        Dispatcher.UIThread.Post(() => ShowStatus(Loc.T("Impossible de lire cette vidéo.", "This video cannot be played.")));

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void OnPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (_renderer is { } renderer)
        {
            PauseButton.Content = renderer.TogglePause() ? Strings.Play : Strings.Pause;
        }
    }

    private void OnMuteClicked(object? sender, RoutedEventArgs e) => IsMuted = !IsMuted;
}
