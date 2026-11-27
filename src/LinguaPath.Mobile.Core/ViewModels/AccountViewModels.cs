using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared.Contracts.Profile;
using Refit;

namespace LinguaPath.Mobile.Core.ViewModels;

public sealed partial class ProfileViewModel(
    IAccountApi accountApi,
    IAuthSession session,
    INavigationService navigation,
    IDialogService dialogs,
    IMediaService media) : BaseViewModel
{
    [ObservableProperty] private UserProfileDto? profile;
    [ObservableProperty] private string? photoUrl;

    public string Initials => string.Concat((Profile?.FullName ?? session.User?.FullName ?? "?")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    partial void OnProfileChanged(UserProfileDto? value)
    {
        PhotoUrl = value?.PhotoUrl;
        OnPropertyChanged(nameof(Initials));
    }

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        var response = await accountApi.GetProfileAsync();
        Profile = response.Data;
        await session.UpdateUserAsync(SessionUser.From(response.Data!));
    });

    [RelayCommand]
    private Task EditProfile() => navigation.GoToAsync(Routes.EditProfile);

    // Phase 7: My learning
    [RelayCommand]
    private Task OpenPoints() => navigation.GoToAsync(Routes.Points);

    [RelayCommand]
    private Task OpenLeaderboard() => navigation.GoToAsync(Routes.Leaderboard);

    // Phase 8: social
    [RelayCommand]
    private Task OpenChats() => navigation.GoToAsync(Routes.Chats);

    [RelayCommand]
    private Task OpenPeople() => navigation.GoToAsync(Routes.People);

    [RelayCommand]
    private Task OpenPrivacy() => navigation.GoToAsync(Routes.Privacy);

    // Phase 9: competitions
    [RelayCommand]
    private Task OpenCompete() => navigation.GoToAsync(Routes.Compete);

    [RelayCommand]
    private Task OpenLeague() => navigation.GoToAsync(Routes.League);

    [RelayCommand]
    private Task OpenCertificates() => navigation.GoToAsync(Routes.Certificates);

    [RelayCommand]
    private Task ChangePassword() => navigation.GoToAsync(Routes.ChangePassword,
        new Dictionary<string, object> { ["hasPassword"] = Profile?.HasPassword ?? true });

    [RelayCommand]
    private Task ChangePhoto() => RunAsync(async () =>
    {
        var file = await media.PickPhotoAsync();
        if (file is null) return;

        await using var stream = await file.OpenReadAsync();
        var response = await accountApi.UploadPhotoAsync(new StreamPart(stream, file.FileName, file.ContentType));
        PhotoUrl = response.Data!.PhotoUrl;
        if (session.User is not null) await session.UpdateUserAsync(session.User with { PhotoUrl = PhotoUrl });
        await dialogs.ToastAsync("Photo updated.");
    });

    [RelayCommand]
    private async Task SignOut()
    {
        if (!await dialogs.ConfirmAsync("Sign out", "Sign out of LinguaPath on this device?", "Sign out")) return;
        await session.SignOutAsync(callServer: true); // App listens for SignedOut and shows Welcome
    }

    [RelayCommand]
    private async Task SignOutEverywhere()
    {
        if (!await dialogs.ConfirmAsync("Sign out everywhere", "This signs you out on all your phones and tablets.", "Sign out all")) return;
        await RunAsync(async () =>
        {
            await accountApi.LogoutAllAsync();
            await session.SignOutAsync(callServer: false);
        });
    }

    [RelayCommand]
    private Task ExportData() => RunAsync(async () =>
    {
        var json = await accountApi.ExportMyDataAsync();
        await media.ShareTextFileAsync($"linguapath-my-data-{DateTime.Now:yyyyMMdd}.json", json, "My LinguaPath data");
    });

    [RelayCommand]
    private async Task DeleteAccount()
    {
        if (!await dialogs.ConfirmAsync("Delete account",
                "Your profile, progress and certificates will be removed. This cannot be undone.", "Continue")) return;

        string? password = null;
        string confirmation;
        if (Profile?.HasPassword ?? true)
        {
            password = await dialogs.PromptAsync("Confirm with your password", "Enter your password to delete your account.",
                "Delete", isPassword: true);
            if (string.IsNullOrEmpty(password)) return;
            confirmation = "DELETE";
        }
        else
        {
            confirmation = await dialogs.PromptAsync("Type DELETE", "Type DELETE to confirm.", "Delete", "DELETE") ?? string.Empty;
            if (confirmation != "DELETE") return;
        }

        await RunAsync(async () =>
        {
            await accountApi.DeleteAccountAsync(new DeleteAccountRequest(password, confirmation));
            await dialogs.AlertAsync("Account deleted", "Your account has been deleted. We're sorry to see you go.");
            await session.SignOutAsync(callServer: false);
        });
    }
}

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class EditProfileViewModel(
    IAccountApi accountApi,
    IAuthApi authApi,
    IAuthSession session,
    IDeviceContext device,
    INavigationService navigation,
    IDialogService dialogs,
    ILanguageService language) : BaseViewModel
{
    [ObservableProperty] private string fullName = string.Empty;
    [ObservableProperty] private string? displayName;
    /// <summary>BR-32: can be added once; changing it later goes through support.</summary>
    [ObservableProperty] private bool hasDateOfBirth;
    [ObservableProperty] private DateTime dateOfBirth = new(2005, 1, 1);
    [ObservableProperty] private bool canEditDateOfBirth = true;
    public DateTime MaxBirthDate => DateTime.Today;
    public DateTime MinBirthDate => DateTime.Today.AddYears(-120);
    [ObservableProperty] private string? bio;
    [ObservableProperty] private LanguageOption? nativeLanguage;
    [ObservableProperty] private Choice? uiLanguage;
    [ObservableProperty] private string timeZone = "UTC";
    [ObservableProperty] private bool hasStudyTime;
    [ObservableProperty] private TimeSpan studyTime = new(19, 0, 0);
    [ObservableProperty] private bool studyReminders = true;
    [ObservableProperty] private bool quizReady = true;
    [ObservableProperty] private bool examReminders = true;
    [ObservableProperty] private bool streakAlerts = true;
    [ObservableProperty] private bool certificates = true;
    [ObservableProperty] private bool newPosts = true;

    private bool _loaded;

    public ObservableCollection<LanguageOption> Languages { get; } = [];
    public IReadOnlyList<Choice> UiLanguages { get; } = [new("en", "English"), new("ur", "اردو (Urdu)")];
    public string DeviceTimeZone => device.TimeZoneId;

    public override Task OnAppearingAsync() => _loaded ? Task.CompletedTask : RunAsync(async () =>
    {
        var languages = await authApi.GetLanguagesAsync(all: true);
        Languages.Clear();
        Languages.Add(new LanguageOption(null, "Prefer not to say"));
        foreach (var l in languages.Data ?? []) Languages.Add(new LanguageOption(l.Id, $"{l.Name} · {l.NativeName}"));

        var p = (await accountApi.GetProfileAsync()).Data!;
        FullName = p.FullName;
        DisplayName = p.DisplayName;
        Bio = p.Bio;
        NativeLanguage = Languages.FirstOrDefault(l => l.Id == p.NativeLanguageId) ?? Languages[0];
        UiLanguage = UiLanguages.FirstOrDefault(u => u.Value == p.UiLanguage) ?? UiLanguages[0];
        TimeZone = p.TimeZone;
        HasStudyTime = p.PreferredStudyTime is not null;
        if (p.PreferredStudyTime is { } t) StudyTime = t.ToTimeSpan();
        StudyReminders = p.Notifications.StudyReminders;
        QuizReady = p.Notifications.QuizReady;
        ExamReminders = p.Notifications.ExamReminders;
        StreakAlerts = p.Notifications.StreakAlerts;
        Certificates = p.Notifications.Certificates;
        NewPosts = p.Notifications.NewPosts;
        HasDateOfBirth = p.DateOfBirth is not null;
        if (p.DateOfBirth is { } dob) DateOfBirth = dob.ToDateTime(TimeOnly.MinValue);
        CanEditDateOfBirth = p.DateOfBirth is null;
        _loaded = true;
    });

    [RelayCommand]
    private void UseDeviceTimeZone() => TimeZone = device.TimeZoneId;

    [RelayCommand]
    private Task Save()
    {
        DateOnly? dob = HasDateOfBirth && CanEditDateOfBirth ? DateOnly.FromDateTime(DateOfBirth) : null;
        if (!Check.All(Errors, (nameof(FullName), Check.Required(FullName, "Name")),
                (nameof(DateOfBirth), Check.DateOfBirth(dob, DateOnly.FromDateTime(DateTime.Today)))))
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var request = new UpdateProfileRequest(
                FullName.Trim(), DisplayName, Bio, NativeLanguage?.Id, UiLanguage?.Value ?? "en", TimeZone,
                HasStudyTime ? TimeOnly.FromTimeSpan(StudyTime) : null,
                new NotificationPreferencesDto(StudyReminders, QuizReady, ExamReminders, StreakAlerts, Certificates, NewPosts),
                dob);
            var response = await accountApi.UpdateProfileAsync(request);
            await session.UpdateUserAsync(SessionUser.From(response.Data!));
            CanEditDateOfBirth = response.Data!.DateOfBirth is null;
            await dialogs.ToastAsync("Profile saved.");
            // NFR-10: a new app language rebuilds the shell (and flips to right-to-left for Urdu).
            if (await language.ApplyAsync(response.Data.UiLanguage)) return;
            await navigation.GoBackAsync();
        });
    }
}

public sealed partial class ChangePasswordViewModel(
    IAccountApi accountApi,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    [ObservableProperty] private bool hasPassword = true;
    [ObservableProperty] private string currentPassword = string.Empty;
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;

    public string Title => HasPassword ? "Change password" : "Set a password";

    partial void OnHasPasswordChanged(bool value) => OnPropertyChanged(nameof(Title));

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (bool.TryParse(Param(parameters, "hasPassword"), out var has)) HasPassword = has;
    }

    [RelayCommand]
    private Task Save()
    {
        var ok = Check.All(Errors,
            (nameof(CurrentPassword), HasPassword ? Check.Required(CurrentPassword, "Current password") : null),
            (nameof(NewPassword), Check.Password(NewPassword)),
            (nameof(ConfirmPassword), NewPassword == ConfirmPassword ? null : "The passwords do not match."));
        if (!ok) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await accountApi.ChangePasswordAsync(new ChangePasswordRequest(CurrentPassword, NewPassword));
            await dialogs.ToastAsync(HasPassword ? "Password changed." : "Password set.");
            await navigation.GoBackAsync();
        });
    }
}
