namespace LinguaPath.Mobile.Services;

/// <summary>
/// The learner's light / dark / system theme choice, kept on the device. Every colour in the app uses
/// AppThemeBinding, so setting Application.UserAppTheme restyles all open pages straight away.
/// </summary>
public static class ThemePreference
{
    private const string Key = "app_theme";

    public static AppTheme Current
    {
        get
        {
            try
            {
                return Enum.TryParse<AppTheme>(Preferences.Default.Get(Key, nameof(AppTheme.Unspecified)), out var t) ? t : AppTheme.Unspecified;
            }
            catch
            {
                return AppTheme.Unspecified;
            }
        }
    }

    public static void Apply() => Set(Current, save: false);

    public static void Set(AppTheme theme, bool save = true)
    {
        if (Application.Current is { } app) app.UserAppTheme = theme;
        if (!save) return;
        try
        {
            Preferences.Default.Set(Key, theme.ToString());
        }
        catch
        {
            // Preferences can be unavailable in previews; the choice still applies for this session.
        }
    }

    /// <summary>System → Light → Dark → System.</summary>
    public static AppTheme Next(AppTheme theme) => theme switch
    {
        AppTheme.Unspecified => AppTheme.Light,
        AppTheme.Light => AppTheme.Dark,
        _ => AppTheme.Unspecified
    };
}
