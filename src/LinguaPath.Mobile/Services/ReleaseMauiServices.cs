using LinguaPath.Mobile.Core.Localization;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile.Services;

public sealed class MauiConnectivityMonitor : IConnectivityMonitor
{
    public MauiConnectivityMonitor() =>
        Connectivity.Current.ConnectivityChanged += (_, e) => Changed?.Invoke(this, e.NetworkAccess == NetworkAccess.Internet);

    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    public event EventHandler<bool>? Changed;
}

public sealed class MauiAppPaths : IAppPaths
{
    public string DataDirectory => FileSystem.AppDataDirectory;
}

/// <summary>
/// NFR-10 English / Urdu. The choice is kept on the phone (so the right language shows before sign-in) and in the profile.
/// Switching rebuilds the shell: every page re-reads its {l:Tr} text and Urdu flips the layout right to left.
/// </summary>
public sealed class MauiLanguageService : ILanguageService
{
    public const string PreferenceKey = "ui_language";

    public string Current => Localizer.Language;

    /// <summary>Called once in MauiProgram, before any page is built.</summary>
    public static void ApplySaved()
    {
        var saved = Preferences.Default.Get<string?>(PreferenceKey, null);
        // First run: follow the phone when it is set to Urdu.
        Localizer.SetLanguage(saved ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    public Task<bool> ApplyAsync(string language)
    {
        var lang = Localizer.Normalise(language);
        Preferences.Default.Set(PreferenceKey, lang);
        if (!Localizer.SetLanguage(lang)) return Task.FromResult(false);

        var done = new TaskCompletionSource<bool>();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var window = Application.Current?.Windows.FirstOrDefault();
                if (window is not null)
                {
                    window.Page = new AppShell();
                    await Shell.Current.GoToAsync(Routes.Home);
                }
                done.SetResult(true);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }
}
