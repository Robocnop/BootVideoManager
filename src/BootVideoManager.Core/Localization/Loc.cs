using System.Globalization;

namespace BootVideoManager.Core.Localization;

/// <summary>
/// Two-language text selection (French, English). The language is chosen once at startup; each message is written
/// next to its translation so the pair stays in sync.
/// </summary>
public static class Loc
{
    /// <summary>Setting value for French.</summary>
    public const string French = "fr";

    /// <summary>Setting value for English.</summary>
    public const string English = "en";

    /// <summary>True when the interface is displayed in English.</summary>
    public static bool IsEnglish { get; private set; }

    /// <summary>Two-letter code of the active language.</summary>
    public static string Current => IsEnglish ? English : French;

    /// <summary>Picks the French or English text.</summary>
    public static string T(string french, string english) => IsEnglish ? english : french;

    /// <summary>
    /// Applies <paramref name="preference"/> (<see cref="French"/>, <see cref="English"/>, or <c>null</c> to follow the
    /// system: French for French-speaking systems, English otherwise).
    /// </summary>
    public static void Apply(string? preference) =>
        IsEnglish = Resolve(preference, CultureInfo.CurrentUICulture) == English;

    /// <summary>Language actually used for a preference on a system whose UI culture is <paramref name="systemCulture"/>.</summary>
    public static string Resolve(string? preference, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);
        return preference switch
        {
            French => French,
            English => English,
            _ => systemCulture.TwoLetterISOLanguageName == French ? French : English,
        };
    }
}
