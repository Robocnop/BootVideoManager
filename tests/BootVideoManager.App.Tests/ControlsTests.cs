using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BootVideoManager.App.Controls;
using BootVideoManager.App.Localization;

namespace BootVideoManager.App.Tests;

public class ControlsTests
{
    [AvaloniaFact]
    public void VideoPreview_KeepsItsSliderAndMuteButtonInSync()
    {
        var preview = new VideoPreview();
        var window = new Window { Content = preview };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var slider = preview.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "VolumeSlider");
        var mute = preview.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MuteButton");

        preview.Volume = 25;
        Assert.Equal(25, slider.Value);

        slider.Value = 80;
        Assert.Equal(80, preview.Volume);

        preview.IsMuted = true;
        Assert.Equal(Strings.Unmute, mute.Content);

        mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(preview.IsMuted);
        Assert.Equal(Strings.Mute, mute.Content);
    }

    [Fact]
    public void EveryInterfaceText_IsDefined()
    {
        var empty = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => string.IsNullOrWhiteSpace((string?)p.GetValue(null)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(empty);
    }
}
