using Foundation;

namespace LinguaPath.Mobile;

/// <summary>
/// iOS 15+ completes WebAuthenticator through ASWebAuthenticationSession, so no OpenUrl override is needed.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
