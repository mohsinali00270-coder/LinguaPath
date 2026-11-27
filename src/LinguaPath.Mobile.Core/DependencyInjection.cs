using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Mobile.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace LinguaPath.Mobile.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers API clients, the session and all ViewModels. The MAUI project must also register
    /// ISecureStore, INavigationService, IDialogService, IDeviceContext, IExternalSignIn, IMediaService,
    /// ISpeechService, IOfflineStore, IAppTheme, IAudioService, IUiDispatcher, IPushTokenProvider and (Phase 10)
    /// IConnectivityMonitor, IAppPaths and ILanguageService.
    /// </summary>
    public static IServiceCollection AddMobileCore(this IServiceCollection services, ApiSettings api)
    {
        services.AddSingleton(api);
        var refit = new RefitSettings(new SystemTextJsonContentSerializer(JsonDefaults.Options));

        void Configure(HttpClient c)
        {
            c.BaseAddress = new Uri(api.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // Anonymous client (no auth handler) - also used by the session to refresh tokens.
        services.AddRefitClient<IAuthApi>(refit).ConfigureHttpClient(Configure);

        // Authenticated client.
        services.AddTransient<AuthHeaderHandler>();
        services.AddRefitClient<IAccountApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<ILearningApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<IContentApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<IAssessmentApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<IPracticeApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<IProgressApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<ISocialApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<ICompetitionApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        // Phase 10: inbox, crash reports (anonymous), offline sync, start-up work.
        services.AddRefitClient<INotificationsApi>(refit).ConfigureHttpClient(Configure).AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddRefitClient<IClientErrorsApi>(refit).ConfigureHttpClient(Configure);
        services.AddSingleton<IOfflineSyncQueue, OfflineSyncQueue>();
        services.AddSingleton<IClientErrorReporter, ClientErrorReporter>();
        services.AddSingleton<SessionStartup>();
        services.AddTransient<NotificationsViewModel>();
        services.AddSingleton<PlacementResults>();
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IAuthSession, AuthSession>();
        // AuthSession needs IAccountApi for logout, and IAccountApi's handler needs the session: break the cycle lazily.
        services.AddSingleton(sp => new Lazy<IAccountApi>(sp.GetRequiredService<IAccountApi>));
        services.AddTransient<ExternalAuthFlow>();
        services.AddSingleton<ICourseContext, CourseContext>();

        services.AddTransient<SplashViewModel>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<RegisterViewModel>();
        services.AddTransient<VerifyEmailViewModel>();
        services.AddTransient<ForgotPasswordViewModel>();
        services.AddTransient<ResetPasswordViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<ProfileViewModel>();
        services.AddTransient<EditProfileViewModel>();
        services.AddTransient<ChangePasswordViewModel>();
        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<RoadmapViewModel>();
        services.AddTransient<CourseSettingsViewModel>();
        services.AddTransient<LectureViewModel>();
        services.AddTransient<PostsViewModel>();
        services.AddTransient<BookmarksViewModel>();
        services.AddTransient<QuizViewModel>();
        services.AddTransient<QuizResultViewModel>();
        services.AddTransient<ExamsViewModel>();
        services.AddTransient<MarksViewModel>();
        services.AddSingleton<PracticeLanguage>();
        services.AddTransient<PracticeHubViewModel>();
        services.AddTransient<PracticeSessionViewModel>();
        services.AddTransient<ConversationViewModel>();
        services.AddTransient<TranslateViewModel>();
        services.AddTransient<WordsViewModel>();
        services.AddTransient<FlashcardsViewModel>();
        services.AddTransient<PointsViewModel>();
        services.AddTransient<LeaderboardViewModel>();
        services.AddTransient<CertificatesViewModel>();

        // Phase 8: social and chat
        services.AddSingleton<IChatConnection, ChatConnection>();
        services.AddSingleton<ModerationNotices>();
        services.AddSingleton<SocialBootstrap>();
        services.AddTransient<ChatsViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<PeopleViewModel>();
        services.AddTransient<UserProfileViewModel>();
        services.AddTransient<GroupCreateViewModel>();
        services.AddTransient<GroupInfoViewModel>();
        services.AddTransient<PublicGroupsViewModel>();
        services.AddTransient<PrivacyViewModel>();

        // Phase 9: competitions
        services.AddSingleton<ICompetitionConnection, CompetitionConnection>();
        services.AddTransient<CompeteViewModel>();
        services.AddTransient<ChallengeSetupViewModel>();
        services.AddTransient<MatchViewModel>();
        services.AddTransient<MatchResultViewModel>();
        services.AddTransient<TournamentsViewModel>();
        services.AddTransient<TournamentViewModel>();
        services.AddTransient<LeagueViewModel>();

        return services;
    }
}
