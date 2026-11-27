using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared.Contracts.Catalog;
using Refit;

namespace LinguaPath.Mobile.Core.ViewModels;

/// <summary>Version check, then restore the saved session or show Welcome.</summary>
public sealed partial class SplashViewModel(
    IAuthSession session,
    IAuthApi authApi,
    IAccountApi accountApi,
    IDeviceContext device,
    INavigationService navigation,
    IMediaService media) : BaseViewModel
{
    [ObservableProperty]
    private VersionCheckResponse? updateInfo;

    public bool UpdateRequired => UpdateInfo?.UpdateRequired == true;

    partial void OnUpdateInfoChanged(VersionCheckResponse? value) => OnPropertyChanged(nameof(UpdateRequired));

    public override async Task OnAppearingAsync()
    {
        // FR-A112: block old app versions.
        try
        {
            var check = await authApi.VersionCheckAsync(device.Platform, device.AppVersion);
            UpdateInfo = check.Data;
            if (UpdateRequired) return;
        }
        catch (Exception)
        {
            // Offline or server down: do not block the app on the version check.
        }

        await session.LoadAsync();
        if (!session.HasTokens)
        {
            await navigation.GoToAsync(Routes.Welcome);
            return;
        }

        try
        {
            var profile = await accountApi.GetProfileAsync();
            await session.UpdateUserAsync(SessionUser.From(profile.Data!));
            _ = RegisterDeviceQuietlyAsync();
            await navigation.GoToAsync(Routes.Home);
        }
        catch (Exception ex) when (ex is SessionExpiredException || (ex is ApiException api && api.StatusCode == System.Net.HttpStatusCode.Unauthorized))
        {
            await navigation.GoToAsync(Routes.Welcome);
        }
        catch (Exception)
        {
            // Offline: continue with the cached user if we have one.
            await navigation.GoToAsync(session.User is null ? Routes.Welcome : Routes.Home);
        }
    }

    [RelayCommand]
    private Task OpenStore() =>
        string.IsNullOrEmpty(UpdateInfo?.StoreUrl) ? Task.CompletedTask : media.OpenUrlAsync(UpdateInfo.StoreUrl);

    private async Task RegisterDeviceQuietlyAsync()
    {
        try
        {
            await accountApi.RegisterDeviceAsync(new RegisterDeviceRequest(device.DeviceId, device.Platform, device.AppVersion, null));
        }
        catch (Exception)
        {
            // Not critical; retried on next start. Push tokens are added in a later phase.
        }
    }
}

public sealed partial class WelcomeViewModel(
    ExternalAuthFlow external,
    IDeviceContext device,
    INavigationService navigation) : BaseViewModel
{
    public bool IsAppleAvailable => device.IsAppleSignInAvailable;

    [RelayCommand]
    private Task CreateAccount() => navigation.GoToAsync(Routes.Register);

    [RelayCommand]
    private Task SignIn() => navigation.GoToAsync(Routes.Login);

    [RelayCommand]
    private Task Google() => RunAsync(async () =>
    {
        if (await external.GoogleAsync()) await navigation.GoToAsync(Routes.Home);
    }, ExternalErrorAsync);

    [RelayCommand]
    private Task Apple() => RunAsync(async () =>
    {
        if (await external.AppleAsync()) await navigation.GoToAsync(Routes.Home);
    }, ExternalErrorAsync);

    private Task<bool> ExternalErrorAsync(ApiError _) => Task.FromResult(false);
}
