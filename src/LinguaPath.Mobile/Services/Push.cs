using LinguaPath.Mobile.Core.Services;
#if FIREBASE
using Plugin.Firebase.CloudMessaging;
using Microsoft.Maui.LifecycleEvents;
#endif

namespace LinguaPath.Mobile.Services;

/// <summary>
/// Crash reporting (NFR-14): unhandled errors are written to disk at once and sent to /api/v1/client-errors on the next start.
/// Unexpected exceptions inside ViewModels (not API or network failures) are reported the same way.
/// </summary>
public static class CrashReporting
{
    public static void Hook(IServiceProvider services)
    {
        var reporter = services.GetRequiredService<IClientErrorReporter>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) reporter.Record(ex, "unhandled");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            reporter.Record(e.Exception, "unobserved-task");
            e.SetObserved();
        };
        Core.ViewModels.BaseViewModel.UnexpectedError = (ex, page) => reporter.Record(ex, page);
    }
}

/// <summary>
/// Push token for FCM (Android) and APNs through Firebase (iOS). Builds without Firebase return null, so the app
/// works with in-app notifications only. Build with Firebase: add Platforms/Android/google-services.json and/or
/// Platforms/iOS/GoogleService-Info.plist (the project then sets UseFirebase=true) — see docs/RELEASE.md.
/// </summary>
public sealed class MauiPushTokenProvider : IPushTokenProvider
{
    public async Task<string?> GetTokenAsync()
    {
#if FIREBASE
        try
        {
            await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync(); // asks for notification permission
            return await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Push token unavailable: {ex.Message}");
            return null;
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}

/// <summary>Opens the right screen when a push is tapped (same rules as the inbox, see NotificationRouter).</summary>
public static class PushTaps
{
    public static void Attach(INavigationService navigation)
    {
#if FIREBASE
        CrossFirebaseCloudMessaging.Current.NotificationTapped += (_, e) =>
        {
            var data = e.Notification.Data is { } d ? new Dictionary<string, string>(d) : null;
            if (NotificationRouter.Resolve(data) is not { } target) return;
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await navigation.GoToAsync(target.Route, target.Parameters); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Push tap navigation failed: {ex.Message}"); }
            });
        };
#endif
    }
}

#if FIREBASE
public static class FirebasePushSetup
{
    /// <summary>Initialises Firebase on start-up (Plugin.Firebase.CloudMessaging 3.x).</summary>
    public static MauiAppBuilder UseFirebasePush(this MauiAppBuilder builder)
    {
        builder.ConfigureLifecycleEvents(events =>
        {
#if IOS
            events.AddiOS(ios => ios.WillFinishLaunching((_, _) =>
            {
                Plugin.Firebase.Core.Platforms.iOS.CrossFirebase.Initialize();
                FirebaseCloudMessagingImplementation.Initialize();
                return false;
            }));
#elif ANDROID
            events.AddAndroid(android => android.OnCreate((activity, _) =>
            {
                Plugin.Firebase.Core.Platforms.Android.CrossFirebase.Initialize(activity);
                FirebaseCloudMessagingImplementation.ChannelId = "linguapath";
            }));
#endif
        });
        return builder;
    }
}
#endif
