using BootVideoManager.App.Services;
using BootVideoManager.Core.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BootVideoManager.App.ViewModels;

/// <summary>Preview volume and mute, shared by every preview and remembered between sessions.</summary>
public sealed partial class PreviewSoundViewModel : ViewModelBase
{
    private readonly SettingsStore _settings;
    private readonly bool _loaded;

    public PreviewSoundViewModel(SettingsStore settings)
    {
        _settings = settings;
        var saved = settings.Load().Preview;
        Volume = Math.Clamp(saved.Volume, 0, 100);
        IsMuted = saved.Muted;
        _loaded = true;
    }

    /// <summary>0 to 100 (a double so it binds directly to a slider).</summary>
    [ObservableProperty]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    partial void OnVolumeChanged(double value) => Save();

    partial void OnIsMutedChanged(bool value) => Save();

    private void Save()
    {
        if (!_loaded)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with
            {
                Preview = new PreviewPreferences { Volume = (int)Math.Round(Math.Clamp(Volume, 0, 100)), Muted = IsMuted },
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the preview sound settings.", ex);
        }
    }
}
