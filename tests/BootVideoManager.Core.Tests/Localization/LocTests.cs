using System.Globalization;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.Core.Tests.Localization;

public class LocTests
{
    [Theory]
    [InlineData(null, "fr-FR", "fr")]
    [InlineData(null, "fr-CA", "fr")]
    [InlineData(null, "en-US", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData("fr", "en-US", "fr")]
    [InlineData("en", "fr-FR", "en")]
    [InlineData("xx", "fr-FR", "fr")]
    public void Resolve_FollowsThePreference_ThenTheSystem(string? preference, string system, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(preference, CultureInfo.GetCultureInfo(system)));
    }
}
