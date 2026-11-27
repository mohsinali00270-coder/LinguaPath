using LinguaPath.Mobile.Core.Services;
using LinguaPath.Mobile.Core.ViewModels;

namespace LinguaPath.Mobile;

public partial class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell, IAuthSession session, INavigationService navigation)
    {
        InitializeComponent();
        // Light / dark / system choice saved on the device (Profile → Appearance).
        LinguaPath.Mobile.Services.ThemePreference.Apply();
        _shell = shell;
        LinguaPath.Mobile.Services.PushTaps.Attach(navigation);

        // Whenever the session ends (sign-out, refresh token revoked, account blocked) go back to Welcome.
        session.SignedOut += (_, _) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Shell.Current is not null) await Shell.Current.GoToAsync(Routes.Welcome);
        });
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_shell);
}
