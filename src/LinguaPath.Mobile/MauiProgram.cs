using LinguaPath.Mobile.Core;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Mobile.Pages;
using LinguaPath.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // NFR-10: the saved UI language is applied before any page (or the shell) is built.
        MauiLanguageService.ApplySaved();

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if FIREBASE
        builder.UseFirebasePush();
#endif

        // API, session and ViewModels (platform-independent).
        builder.Services.AddMobileCore(new ApiSettings { BaseUrl = AppConfig.ApiBaseUrl });

        // Platform services used by the ViewModels.
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<INavigationService, ShellNavigationService>();
        builder.Services.AddSingleton<IDialogService, MauiDialogService>();
        builder.Services.AddSingleton<IDeviceContext, MauiDeviceContext>();
        builder.Services.AddSingleton<IExternalSignIn, MauiExternalSignIn>();
        builder.Services.AddSingleton<IMediaService, MauiMediaService>();
        builder.Services.AddSingleton<ISpeechService, MauiSpeechService>();
        builder.Services.AddSingleton<IOfflineStore, MauiOfflineStore>();
        builder.Services.AddSingleton<IAppTheme, MauiAppTheme>();
        builder.Services.AddSingleton<IAudioService, MauiAudioService>();
        builder.Services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        builder.Services.AddSingleton<IPushTokenProvider, MauiPushTokenProvider>();
        // Phase 10: connectivity for offline sync, crash-report folder, UI language.
        builder.Services.AddSingleton<IConnectivityMonitor, MauiConnectivityMonitor>();
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();
        builder.Services.AddSingleton<ILanguageService, MauiLanguageService>();

        // Pages
        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<SplashPage>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<VerifyEmailPage>();
        builder.Services.AddTransient<ForgotPasswordPage>();
        builder.Services.AddTransient<ResetPasswordPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<ProfilePage>();
        builder.Services.AddTransient<EditProfilePage>();
        builder.Services.AddTransient<ChangePasswordPage>();
        builder.Services.AddTransient<ComingSoonPage>();
        builder.Services.AddTransient<OnboardingPage>();
        builder.Services.AddTransient<RoadmapPage>();
        builder.Services.AddTransient<CourseSettingsPage>();
        builder.Services.AddTransient<LecturePage>();
        builder.Services.AddTransient<PostsPage>();
        builder.Services.AddTransient<BookmarksPage>();
        builder.Services.AddTransient<QuizPage>();
        builder.Services.AddTransient<QuizResultPage>();
        builder.Services.AddTransient<ExamsPage>();
        builder.Services.AddTransient<MarksPage>();
        builder.Services.AddTransient<PracticePage>();
        builder.Services.AddTransient<PracticeSessionPage>();
        builder.Services.AddTransient<ConversationPage>();
        builder.Services.AddTransient<TranslatePage>();
        builder.Services.AddTransient<WordsPage>();
        builder.Services.AddTransient<FlashcardsPage>();
        builder.Services.AddTransient<PointsPage>();
        builder.Services.AddTransient<LeaderboardPage>();
        builder.Services.AddTransient<CertificatesPage>();
        builder.Services.AddTransient<ChatsPage>();
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<PeoplePage>();
        builder.Services.AddTransient<UserProfilePage>();
        builder.Services.AddTransient<GroupCreatePage>();
        builder.Services.AddTransient<GroupInfoPage>();
        builder.Services.AddTransient<PublicGroupsPage>();
        builder.Services.AddTransient<PrivacyPage>();
        builder.Services.AddTransient<CompetePage>();
        builder.Services.AddTransient<ChallengeSetupPage>();
        builder.Services.AddTransient<MatchPage>();
        builder.Services.AddTransient<MatchResultPage>();
        builder.Services.AddTransient<TournamentsPage>();
        builder.Services.AddTransient<TournamentPage>();
        builder.Services.AddTransient<LeaguePage>();
        builder.Services.AddTransient<NotificationsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        var app = builder.Build();
        CrashReporting.Hook(app.Services);
        return app;
    }
}

public static class AppConfig
{
    /// <summary>
    /// Debug builds talk to the API on your PC: the Android emulator reaches it at 10.0.2.2,
    /// the iOS simulator at localhost. For a real phone, use your PC's LAN IP (e.g. http://192.168.1.20:5080).
    /// </summary>
#if DEBUG
#if ANDROID
    public const string ApiBaseUrl = "http://10.0.2.2:5080";
#else
    public const string ApiBaseUrl = "http://localhost:5080";
#endif
#else
    public const string ApiBaseUrl = "https://api.linguapath.app";
#endif

    /// <summary>Custom URI scheme registered for the Google browser sign-in callback.</summary>
    public const string CallbackScheme = "linguapath";
}
