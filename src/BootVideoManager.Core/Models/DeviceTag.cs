namespace BootVideoManager.Core.Models;

/// <summary>Hardware a video was designed for, as tagged by its author.</summary>
public enum DeviceTag
{
    /// <summary>Tag not recognised by this version of the app.</summary>
    Unknown = 0,

    /// <summary><c>steam_deck</c> (LCD and OLED are not distinguished by the site).</summary>
    SteamDeck,

    /// <summary><c>steam_machine</c> — desktop / TV Big Picture.</summary>
    SteamMachine,

    /// <summary><c>steam_frame</c> — known by the site front-end, not used by any post yet.</summary>
    SteamFrame,
}
