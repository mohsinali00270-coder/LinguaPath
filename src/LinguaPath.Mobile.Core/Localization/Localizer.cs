using System.Globalization;
using System.Resources;

namespace LinguaPath.Mobile.Core.Localization;

/// <summary>
/// English / Urdu UI text (NFR-10). Strings live in Resources/AppResources.resx and AppResources.ur.resx;
/// XAML uses {l:Tr Key} and C# uses <see cref="T"/>. Changing the language rebuilds the shell, so pages read fresh text.
/// Lecture content is not translated here: it comes from the course in the language being learned.
/// </summary>
public static class Localizer
{
    public static readonly string[] Supported = ["en", "ur"];

    private static readonly ResourceManager Resources =
        new("LinguaPath.Mobile.Core.Resources.AppResources", typeof(Localizer).Assembly);

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    public static string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>Urdu is written right to left; the shell flips its layout when this is true.</summary>
    public static bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;

    public static event EventHandler? LanguageChanged;

    /// <summary>The text for a key in the current language, then English, then the key itself (so a missing key shows up in testing).</summary>
    public static string T(string key)
    {
        try
        {
            return Resources.GetString(key, Culture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>The text for a key in a given language, without changing the app's language (tests, previews).</summary>
    public static string In(string language, string key)
    {
        try
        {
            return Resources.GetString(key, CultureInfo.GetCultureInfo(Normalise(language))) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    /// <summary>"ur", "ur-PK", "UR" → "ur"; anything unsupported → "en".</summary>
    public static string Normalise(string? code)
    {
        var two = (code ?? "").Trim().Split('-', '_')[0].ToLowerInvariant();
        return Supported.Contains(two) ? two : "en";
    }

    /// <summary>Returns true when the language actually changed.</summary>
    public static bool SetLanguage(string? code)
    {
        var lang = Normalise(code);
        if (lang == Language) return false;
        Culture = CultureInfo.GetCultureInfo(lang);
        // UI culture only: number and date formats keep following the phone's region.
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }
}
