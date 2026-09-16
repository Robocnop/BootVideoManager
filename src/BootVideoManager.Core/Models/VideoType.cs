namespace BootVideoManager.Core.Models;

/// <summary>Kind of animation a post provides.</summary>
public enum VideoType
{
    /// <summary>Value not recognised (the site may add new kinds); such posts are hidden by default.</summary>
    Unknown = 0,

    /// <summary>Startup movie played when Steam / Big Picture launches (<c>boot_video</c>).</summary>
    BootVideo,

    /// <summary>Animation played when the device suspends or wakes (<c>suspend_video</c>).</summary>
    SuspendVideo,

    /// <summary>Post taken down by moderation (<c>boot_video_removed</c>); never shown.</summary>
    Removed,
}
