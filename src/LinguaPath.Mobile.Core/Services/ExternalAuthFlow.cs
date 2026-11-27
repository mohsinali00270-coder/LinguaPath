using LinguaPath.Mobile.Core.Api;
using LinguaPath.Shared.Contracts.Auth;

namespace LinguaPath.Mobile.Core.Services;

/// <summary>Google / Apple sign-in shared by the Welcome and Login screens (FR-L02).</summary>
public sealed class ExternalAuthFlow(
    IExternalSignIn external,
    IAuthApi authApi,
    IAccountApi accountApi,
    IAuthSession session,
    IDeviceContext device)
{
    /// <returns>True when signed in; false when the person cancelled. Throws on API errors.</returns>
    public async Task<bool> GoogleAsync()
    {
        var result = await external.GoogleAsync();
        return await CompleteAsync(result, isApple: false);
    }

    public async Task<bool> AppleAsync()
    {
        var result = await external.AppleAsync();
        return await CompleteAsync(result, isApple: true);
    }

    private async Task<bool> CompleteAsync(ExternalSignInResult result, bool isApple)
    {
        if (result.Cancelled) return false;
        if (!string.IsNullOrEmpty(result.Error)) throw new ExternalSignInException(result.Error);

        if (!string.IsNullOrEmpty(result.AccessToken) && !string.IsNullOrEmpty(result.RefreshToken))
        {
            // Browser flow: the API already issued our tokens. Load the profile to fill the session.
            await session.SignInWithTokensAsync(result.AccessToken, result.RefreshToken,
                result.AccessTokenExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
                result.RefreshTokenExpiresAt ?? DateTimeOffset.UtcNow.AddDays(30));
            var profile = await accountApi.GetProfileAsync();
            await session.UpdateUserAsync(SessionUser.From(profile.Data!));
            return true;
        }

        if (!string.IsNullOrEmpty(result.IdToken))
        {
            var request = new ExternalLoginRequest(result.IdToken, result.FullName, DeviceInfo());
            var response = isApple ? await authApi.AppleAsync(request) : await authApi.GoogleAsync(request);
            await session.SignInAsync(response.Data!);
            return true;
        }

        throw new ExternalSignInException("Sign-in did not return any credentials.");
    }

    private DeviceInfoDto DeviceInfo() => new(device.DeviceId, device.Platform, device.AppVersion, null);
}

public sealed class ExternalSignInException(string message) : Exception(message);
