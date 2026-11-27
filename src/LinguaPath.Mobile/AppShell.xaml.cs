using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Mobile.Pages;

namespace LinguaPath.Mobile;

public partial class AppShell : Shell
{
    private static bool _routesRegistered;

    public AppShell()
    {
        InitializeComponent();
        // NFR-10: Urdu is right to left; every page in the shell inherits the direction.
        FlowDirection = Core.Localization.Localizer.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // The shell is rebuilt when the app language changes; routes are global and registered once.
        if (_routesRegistered) return;
        _routesRegistered = true;

        // Pages pushed on top of the current root.
        Routing.RegisterRoute(Routes.Register, typeof(RegisterPage));
        Routing.RegisterRoute(Routes.VerifyEmail, typeof(VerifyEmailPage));
        Routing.RegisterRoute(Routes.ForgotPassword, typeof(ForgotPasswordPage));
        Routing.RegisterRoute(Routes.ResetPassword, typeof(ResetPasswordPage));
        Routing.RegisterRoute(Routes.EditProfile, typeof(EditProfilePage));
        Routing.RegisterRoute(Routes.ChangePassword, typeof(ChangePasswordPage));
        Routing.RegisterRoute(Routes.Onboarding, typeof(OnboardingPage));
        Routing.RegisterRoute(Routes.CourseSettings, typeof(CourseSettingsPage));
        Routing.RegisterRoute(Routes.Lecture, typeof(LecturePage));
        Routing.RegisterRoute(Routes.Posts, typeof(PostsPage));
        Routing.RegisterRoute(Routes.Bookmarks, typeof(BookmarksPage));
        Routing.RegisterRoute(Routes.Quiz, typeof(QuizPage));
        Routing.RegisterRoute(Routes.QuizResult, typeof(QuizResultPage));
        Routing.RegisterRoute(Routes.Exams, typeof(ExamsPage));
        Routing.RegisterRoute(Routes.Marks, typeof(MarksPage));
        Routing.RegisterRoute(Routes.PracticeSession, typeof(PracticeSessionPage));
        Routing.RegisterRoute(Routes.Conversation, typeof(ConversationPage));
        Routing.RegisterRoute(Routes.Words, typeof(WordsPage));
        Routing.RegisterRoute(Routes.Flashcards, typeof(FlashcardsPage));
        Routing.RegisterRoute(Routes.Points, typeof(PointsPage));
        Routing.RegisterRoute(Routes.Leaderboard, typeof(LeaderboardPage));
        Routing.RegisterRoute(Routes.Certificates, typeof(CertificatesPage));
        Routing.RegisterRoute(Routes.Chats, typeof(ChatsPage));
        Routing.RegisterRoute(Routes.Chat, typeof(ChatPage));
        Routing.RegisterRoute(Routes.People, typeof(PeoplePage));
        Routing.RegisterRoute(Routes.UserProfile, typeof(UserProfilePage));
        Routing.RegisterRoute(Routes.GroupCreate, typeof(GroupCreatePage));
        Routing.RegisterRoute(Routes.GroupInfo, typeof(GroupInfoPage));
        Routing.RegisterRoute(Routes.PublicGroups, typeof(PublicGroupsPage));
        Routing.RegisterRoute(Routes.Privacy, typeof(PrivacyPage));
        Routing.RegisterRoute(Routes.Compete, typeof(CompetePage));
        Routing.RegisterRoute(Routes.ChallengeSetup, typeof(ChallengeSetupPage));
        Routing.RegisterRoute(Routes.Match, typeof(MatchPage));
        Routing.RegisterRoute(Routes.MatchResult, typeof(MatchResultPage));
        Routing.RegisterRoute(Routes.Tournaments, typeof(TournamentsPage));
        Routing.RegisterRoute(Routes.Tournament, typeof(TournamentPage));
        Routing.RegisterRoute(Routes.League, typeof(LeaguePage));
        Routing.RegisterRoute(Routes.Notifications, typeof(NotificationsPage));
    }
}
