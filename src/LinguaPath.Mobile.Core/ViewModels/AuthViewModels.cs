using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;

namespace LinguaPath.Mobile.Core.ViewModels;

public sealed partial class LoginViewModel(
    IAuthApi authApi,
    IAuthSession session,
    ExternalAuthFlow external,
    IDeviceContext device,
    INavigationService navigation) : BaseViewModel
{
    [ObservableProperty] private string email = string.Empty;
    [ObservableProperty] private string password = string.Empty;

    public bool IsAppleAvailable => device.IsAppleSignInAvailable;

    [RelayCommand]
    private Task Login()
    {
        if (!Check.All(Errors, (nameof(Email), Check.Email(Email)), (nameof(Password), Check.Required(Password, "Password"))))
            return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var response = await authApi.LoginAsync(new LoginRequest(Email.Trim(), Password, DeviceInfo()));
            await session.SignInAsync(response.Data!);
            Password = string.Empty;
            await navigation.GoToAsync(Routes.Home);
        }, async error =>
        {
            if (error.Code != ErrorCodes.EmailNotVerified) return false;

            // Unverified account: send a fresh code and go to the verification screen.
            var wait = 60;
            try
            {
                var resend = await authApi.ResendOtpAsync(new ResendOtpRequest(Email.Trim(), OtpPurpose.VerifyEmail));
                wait = resend.Data?.ResendAfterSeconds ?? wait;
            }
            catch (Exception) { /* a code was sent recently; the user can resend from the next screen */ }

            await navigation.GoToAsync(Routes.VerifyEmail, new Dictionary<string, object>
            {
                ["email"] = Email.Trim(),
                ["resendAfter"] = wait
            });
            return true;
        });
    }

    [RelayCommand]
    private Task ForgotPassword() => navigation.GoToAsync(Routes.ForgotPassword, new Dictionary<string, object> { ["email"] = Email.Trim() });

    [RelayCommand]
    private Task CreateAccount() => navigation.GoToAsync(Routes.Register);

    [RelayCommand]
    private Task Google() => RunAsync(async () =>
    {
        if (await external.GoogleAsync()) await navigation.GoToAsync(Routes.Home);
    });

    [RelayCommand]
    private Task Apple() => RunAsync(async () =>
    {
        if (await external.AppleAsync()) await navigation.GoToAsync(Routes.Home);
    });

    private DeviceInfoDto DeviceInfo() => new(device.DeviceId, device.Platform, device.AppVersion, null);
}

public sealed record LanguageOption(int? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class RegisterViewModel(
    IAuthApi authApi,
    IDeviceContext device,
    INavigationService navigation,
    IMediaService media,
    ApiSettings api) : BaseViewModel
{
    [ObservableProperty] private string fullName = string.Empty;
    /// <summary>BR-32: optional. Under 13 can't sign up; under 18 chats and competes with friends only.</summary>
    [ObservableProperty] private bool hasDateOfBirth;
    [ObservableProperty] private DateTime dateOfBirth = new(2005, 1, 1);
    public DateTime MaxBirthDate => DateTime.Today;
    public DateTime MinBirthDate => DateTime.Today.AddYears(-120);
    [ObservableProperty] private string email = string.Empty;
    [ObservableProperty] private string password = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;
    [ObservableProperty] private LanguageOption? nativeLanguage;
    [ObservableProperty] private bool acceptTerms;

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    public override async Task OnAppearingAsync()
    {
        if (Languages.Count > 0) return;
        try
        {
            var response = await authApi.GetLanguagesAsync(all: true);
            Languages.Add(new LanguageOption(null, "Prefer not to say"));
            foreach (var l in response.Data ?? [])
                Languages.Add(new LanguageOption(l.Id, $"{l.Name} · {l.NativeName}"));
        }
        catch (Exception)
        {
            // The picker is optional; registration still works without it.
        }
    }

    [RelayCommand]
    private Task Register()
    {
        var ok = Check.All(Errors,
            (nameof(FullName), Check.Required(FullName, "Name")),
            (nameof(Email), Check.Email(Email)),
            (nameof(Password), Check.Password(Password)),
            (nameof(ConfirmPassword), Password == ConfirmPassword ? null : "The passwords do not match."),
            (nameof(AcceptTerms), AcceptTerms ? null : "Please accept the terms to continue."),
            (nameof(DateOfBirth), Check.DateOfBirth(Dob, DateOnly.FromDateTime(DateTime.Today))));
        if (!ok) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var request = new RegisterRequest(FullName.Trim(), Email.Trim(), Password, NativeLanguage?.Id, device.TimeZoneId,
                Localization.Localizer.Language, new DeviceInfoDto(device.DeviceId, device.Platform, device.AppVersion, null), Dob);
            var response = await authApi.RegisterAsync(request);
            await navigation.GoToAsync(Routes.VerifyEmail, new Dictionary<string, object>
            {
                ["email"] = response.Data!.Email,
                ["resendAfter"] = response.Data.ResendAfterSeconds
            });
        });
    }

    [RelayCommand]
    private Task OpenTerms() => media.OpenUrlAsync($"{api.BaseUrl.TrimEnd('/')}/terms");

    private DateOnly? Dob => HasDateOfBirth ? DateOnly.FromDateTime(DateOfBirth) : null;
}

/// <summary>Shared countdown for "Resend code".</summary>
public abstract partial class OtpViewModelBase : BaseViewModel
{
    private CancellationTokenSource? _countdown;

    [ObservableProperty] private string email = string.Empty;
    [ObservableProperty] private string code = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResend), nameof(ResendText))]
    private int resendSeconds;

    public bool CanResend => ResendSeconds <= 0;

    partial void OnResendSecondsChanged(int value) => ResendStateChanged();

    /// <summary>Derived classes refresh their Resend command's enabled state here.</summary>
    protected virtual void ResendStateChanged() { }
    public string ResendText => CanResend ? "Resend code" : $"Resend code in {ResendSeconds}s";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        Email = Param(parameters, "email") ?? Email;
        if (int.TryParse(Param(parameters, "resendAfter"), out var s)) StartCountdown(s);
    }

    protected void StartCountdown(int seconds)
    {
        _countdown?.Cancel();
        _countdown = new CancellationTokenSource();
        var token = _countdown.Token;
        ResendSeconds = seconds;
        _ = CountdownAsync(token);
    }

    private async Task CountdownAsync(CancellationToken token)
    {
        try
        {
            while (ResendSeconds > 0 && !token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                ResendSeconds--;
            }
        }
        catch (TaskCanceledException) { }
    }
}

public sealed partial class VerifyEmailViewModel(
    IAuthApi authApi,
    IAuthSession session,
    IDeviceContext device,
    INavigationService navigation,
    IDialogService dialogs) : OtpViewModelBase
{
    [RelayCommand]
    private Task Verify()
    {
        if (!Check.All(Errors, (nameof(Code), Check.Otp(Code)))) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            var response = await authApi.VerifyEmailAsync(new VerifyEmailRequest(Email, Code.Trim(),
                new DeviceInfoDto(device.DeviceId, device.Platform, device.AppVersion, null)));
            await session.SignInAsync(response.Data!);
            await navigation.GoToAsync(Routes.Home);
        });
    }

    protected override void ResendStateChanged() => ResendCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanResend))]
    private Task Resend() => RunAsync(async () =>
    {
        var response = await authApi.ResendOtpAsync(new ResendOtpRequest(Email, OtpPurpose.VerifyEmail));
        StartCountdown(response.Data?.ResendAfterSeconds ?? 60);
        await dialogs.ToastAsync("A new code is on its way.");
    });
}

public sealed partial class ForgotPasswordViewModel(IAuthApi authApi, INavigationService navigation) : BaseViewModel
{
    [ObservableProperty] private string email = string.Empty;

    public override void ApplyParameters(IDictionary<string, object> parameters) =>
        Email = Param(parameters, "email") ?? Email;

    [RelayCommand]
    private Task SendCode()
    {
        if (!Check.All(Errors, (nameof(Email), Check.Email(Email)))) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await authApi.ForgotPasswordAsync(new ForgotPasswordRequest(Email.Trim()));
            await navigation.GoToAsync(Routes.ResetPassword, new Dictionary<string, object>
            {
                ["email"] = Email.Trim(),
                ["resendAfter"] = 60
            });
        });
    }
}

public sealed partial class ResetPasswordViewModel(
    IAuthApi authApi,
    INavigationService navigation,
    IDialogService dialogs) : OtpViewModelBase
{
    [ObservableProperty] private string newPassword = string.Empty;
    [ObservableProperty] private string confirmPassword = string.Empty;

    [RelayCommand]
    private Task Reset()
    {
        var ok = Check.All(Errors,
            (nameof(Code), Check.Otp(Code)),
            (nameof(NewPassword), Check.Password(NewPassword)),
            (nameof(ConfirmPassword), NewPassword == ConfirmPassword ? null : "The passwords do not match."));
        if (!ok) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await authApi.ResetPasswordAsync(new ResetPasswordRequest(Email, Code.Trim(), NewPassword));
            await dialogs.AlertAsync("Password changed", "You can now sign in with your new password.");
            await navigation.GoToAsync(Routes.Login);
        });
    }

    protected override void ResendStateChanged() => ResendCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanResend))]
    private Task Resend() => RunAsync(async () =>
    {
        var response = await authApi.ResendOtpAsync(new ResendOtpRequest(Email, OtpPurpose.ResetPassword));
        StartCountdown(response.Data?.ResendAfterSeconds ?? 60);
        await dialogs.ToastAsync("A new code is on its way.");
    });
}
