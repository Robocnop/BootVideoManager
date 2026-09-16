namespace BootVideoManager.App.ViewModels;

/// <summary>A labelled choice for combo boxes.</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Duration filter bounds in seconds (inclusive, open when null).</summary>
public readonly record struct DurationRange(int? MinSeconds, int? MaxSeconds);
