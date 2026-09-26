namespace BootVideoManager.Core.Account;

/// <summary>steamdeckrepo.com account the user signed in with (through Steam).</summary>
/// <param name="Id">Site user id, used in <c>/user/{id}/liked</c>.</param>
/// <param name="Name">Steam display name.</param>
/// <param name="AvatarUri">Steam avatar, when the site provides one.</param>
public sealed record SiteUser(long Id, string Name, Uri? AvatarUri);

/// <summary>One cookie of the site session, as captured from the sign-in browser.</summary>
public sealed record SiteCookie
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public string Path { get; set; } = "/";

    /// <summary><c>null</c> for a browser-session cookie.</summary>
    public DateTimeOffset? Expires { get; set; }
}

/// <summary>What is kept on disk between runs: the site cookies and who they belong to. Never the Steam password.</summary>
public sealed record SiteSession
{
    public List<SiteCookie> Cookies { get; set; } = [];

    public long UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public SiteUser User => new(UserId, UserName, Uri.TryCreate(AvatarUrl, UriKind.Absolute, out var avatar) ? avatar : null);
}
