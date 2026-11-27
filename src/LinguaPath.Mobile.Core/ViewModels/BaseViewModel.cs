using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using LinguaPath.Mobile.Core.Api;
using static LinguaPath.Mobile.Core.Localization.Localizer;

namespace LinguaPath.Mobile.Core.ViewModels;

public static class Routes
{
    // Roots (absolute Shell routes)
    public const string Splash = "//splash";
    public const string Welcome = "//welcome";
    public const string Login = "//login";
    public const string Home = "//main/home";
    public const string Profile = "//main/profile";

    // Pushed pages
    public const string Register = "register";
    public const string VerifyEmail = "verify-email";
    public const string ForgotPassword = "forgot-password";
    public const string ResetPassword = "reset-password";
    public const string EditProfile = "edit-profile";
    public const string ChangePassword = "change-password";
    public const string Onboarding = "onboarding";
    public const string CourseSettings = "course-settings";
    public const string Learn = "//main/learn";
    public const string Lecture = "lecture";
    public const string Posts = "posts";
    public const string Bookmarks = "bookmarks";
    public const string Quiz = "quiz";
    public const string QuizResult = "quiz-result";
    public const string Exams = "exams";
    public const string Marks = "marks";
    public const string Practice = "//main/practice";
    public const string Translate = "//main/translate";
    public const string PracticeSession = "practice-session";
    public const string Conversation = "conversation";
    public const string Words = "words";
    public const string Flashcards = "flashcards";
    public const string Points = "points";
    public const string Leaderboard = "leaderboard";
    public const string Certificates = "certificates";

    // Phase 8: social and chat
    public const string Chats = "chats";
    public const string Chat = "chat";
    public const string People = "people";
    public const string UserProfile = "user-profile";
    public const string GroupCreate = "group-create";
    public const string GroupInfo = "group-info";
    public const string PublicGroups = "public-groups";
    public const string Privacy = "privacy";

    // Phase 9: competitions
    public const string Compete = "compete";
    public const string ChallengeSetup = "challenge-setup";
    public const string Match = "match";
    public const string MatchResult = "match-result";
    public const string Tournaments = "tournaments";
    public const string Tournament = "tournament";
    public const string League = "league";

    // Phase 10
    public const string Notifications = "notifications";
}

/// <summary>Per-field error messages, bindable in XAML as {Binding Errors[Email]}.</summary>
public sealed class FieldErrors : ObservableObject
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string field] => _errors.TryGetValue(field, out var m) ? m : null;
    public bool HasAny => _errors.Count > 0;

    public void Set(string field, string message)
    {
        _errors[field] = message;
        Notify();
    }

    public void Set(IReadOnlyDictionary<string, string[]> errors)
    {
        foreach (var (field, messages) in errors)
            if (messages.Length > 0) _errors[field] = messages[0];
        Notify();
    }

    public void Clear()
    {
        if (_errors.Count == 0) return;
        _errors.Clear();
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged("Item");
        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(HasAny));
    }
}

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorMessage;

    /// <summary>Set by the app at start-up to the crash reporter.</summary>
    public static Action<Exception, string>? UnexpectedError { get; set; }

    public bool IsNotBusy => !IsBusy;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public FieldErrors Errors { get; } = new();

    /// <summary>Called by the page when it appears.</summary>
    public virtual Task OnAppearingAsync() => Task.CompletedTask;

    /// <summary>Called by the page when it is hidden (navigated away or app backgrounded).</summary>
    public virtual Task OnDisappearingAsync() => Task.CompletedTask;

    /// <summary>Navigation query parameters (Shell route parameters).</summary>
    public virtual void ApplyParameters(IDictionary<string, object> parameters) { }

    /// <summary>Runs an action with busy state and turns API failures into messages on screen.</summary>
    protected async Task RunAsync(Func<Task> action, Func<ApiError, Task<bool>>? onError = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        Errors.Clear();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var error = ApiError.From(ex);
            // Anything that isn't an API or network failure is a bug: report it (NFR-14).
            if (error.Code == LinguaPath.Shared.ErrorCodes.ServerError && error.Status is null) UnexpectedError?.Invoke(ex, this.GetType().Name);
            if (onError is not null && await onError(error)) return;

            Errors.Set(error.FieldErrors);
            // Field errors show beside their inputs; the banner shows the general message.
            ErrorMessage = error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected static string? Param(IDictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) ? Uri.UnescapeDataString(v?.ToString() ?? string.Empty) : null;
}

/// <summary>Client-side checks that mirror the API's validators, so mistakes show before a round trip.</summary>
public static partial class Check
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    public static string? Email(string? value) =>
        string.IsNullOrWhiteSpace(value) ? T("Val_EmailRequired")
        : !EmailRegex().IsMatch(value.Trim()) ? T("Val_EmailInvalid")
        : null;

    public static string? Password(string? value)
    {
        if (string.IsNullOrEmpty(value)) return T("Val_PasswordRequired");
        if (value.Length < 8) return T("Val_PasswordLength");
        if (!value.Any(char.IsUpper)) return T("Val_PasswordUpper");
        if (!value.Any(char.IsLower)) return T("Val_PasswordLower");
        if (!value.Any(char.IsDigit)) return T("Val_PasswordDigit");
        return null;
    }

    public static string? Required(string? value, string label) =>
        string.IsNullOrWhiteSpace(value) ? T("Val_Required", label) : null;

    public static string? Otp(string? value) =>
        string.IsNullOrWhiteSpace(value) ? T("Val_OtpRequired")
        : value.Trim().Length != 6 || !value.Trim().All(char.IsDigit) ? T("Val_OtpDigits")
        : null;

    /// <summary>BR-32: optional; when given it must be a real date and the learner at least 13.</summary>
    public static string? DateOfBirth(DateOnly? value, DateOnly today) =>
        value is not { } d ? null
        : !LinguaPath.Shared.AgeRules.IsPlausible(d, today) ? T("Val_DobInvalid")
        : LinguaPath.Shared.AgeRules.IsTooYoung(d, today) ? T("Val_DobTooYoung", LinguaPath.Shared.AgeRules.MinimumAge)
        : null;

    /// <summary>Adds each non-null message to the field errors; returns true when everything passed.</summary>
    public static bool All(FieldErrors errors, params (string Field, string? Message)[] checks)
    {
        errors.Clear();
        foreach (var (field, message) in checks)
            if (message is not null) errors.Set(field, message);
        return !errors.HasAny;
    }
}
