using System.Net;
using System.Net.Http.Json;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;
using LinguaPath.Shared.Contracts.Profile;

namespace LinguaPath.IntegrationTests;

/// <summary>End-to-end checks of the Phase 1 acceptance points (FR-L01 .. FR-L06, FR-A112).</summary>
public sealed class AuthFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private static string NewEmail() => $"learner-{Guid.NewGuid():N}@test.local";

    private async Task<AuthResponse> RegisterAndVerifyAsync(string email)
    {
        var client = factory.CreateClient();
        var register = await client.PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest("Test Learner", email, Password, null, "UTC", "en"));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var code = factory.Emails.LatestCodeFor(email);
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email",
            new VerifyEmailRequest(email, code, new DeviceInfoDto("device-1", DevicePlatform.Android, "1.0.0", null)));
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        return await verify.ReadDataAsync<AuthResponse>();
    }

    [Fact]
    public async Task Register_verify_then_profile_works()
    {
        var email = NewEmail();
        var auth = await RegisterAndVerifyAsync(email);

        Assert.False(string.IsNullOrEmpty(auth.AccessToken));
        Assert.Contains("Learner", auth.User.Roles);

        var me = await factory.CreateClient().WithBearer(auth.AccessToken).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var profile = await me.ReadDataAsync<UserProfileDto>();
        Assert.Equal(email, profile.Email);
        Assert.True(profile.HasPassword);
    }

    [Fact]
    public async Task Login_before_verifying_email_is_refused_with_code()
    {
        var email = NewEmail();
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Unverified", email, Password, null, null, null));

        var login = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password, null));

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal(ErrorCodes.EmailNotVerified, (await login.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Wrong_password_returns_invalid_credentials()
    {
        var email = NewEmail();
        await RegisterAndVerifyAsync(email);

        var login = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Wrong12345", null));

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var problem = await login.ReadProblemAsync();
        Assert.Equal(ErrorCodes.InvalidCredentials, problem.Code);
        Assert.False(problem.Success);
    }

    [Fact]
    public async Task Wrong_otp_counts_attempts_and_fails()
    {
        var email = NewEmail();
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Otp Test", email, Password, null, null, null));
        var real = factory.Emails.LatestCodeFor(email);
        var wrong = real == "000000" ? "111111" : "000000";

        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, wrong, null));

        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        Assert.Equal(ErrorCodes.InvalidOtp, (await verify.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Refresh_rotates_and_reusing_an_old_token_revokes_the_session()
    {
        var auth = await RegisterAndVerifyAsync(NewEmail());
        var client = factory.CreateClient();

        var first = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(auth.RefreshToken, "device-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = await first.ReadDataAsync<AuthResponse>();
        Assert.NotEqual(auth.RefreshToken, rotated.RefreshToken);

        // Reusing the original token is treated as theft...
        var reuse = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(auth.RefreshToken, "device-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // ...so the newest token from the same sign-in is revoked too.
        var afterTheft = await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(rotated.RefreshToken, "device-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Logout_all_invalidates_existing_access_tokens()
    {
        var auth = await RegisterAndVerifyAsync(NewEmail());
        var client = factory.CreateClient().WithBearer(auth.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/auth/logout-all", null)).StatusCode);

        var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Forgot_and_reset_password_lets_the_learner_sign_in_with_the_new_password()
    {
        var email = NewEmail();
        await RegisterAndVerifyAsync(email);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email))).StatusCode);
        var code = factory.Emails.LatestCodeFor(email);

        var reset = await client.PostJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, code, "NewPass123"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var login = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "NewPass123", null));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Invalid_registration_returns_field_errors()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest("", "not-an-email", "short", null, "Not/AZone", "fr"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(ErrorCodes.Validation, problem.Code);
        Assert.Contains("Email", problem.Errors!.Keys);
        Assert.Contains("Password", problem.Errors.Keys);
        Assert.Contains("FullName", problem.Errors.Keys);
    }

    [Fact]
    public async Task Admin_accounts_cannot_sign_in_to_the_learner_app()
    {
        var login = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login",
            new LoginRequest("admin@linguapath.local", "Admin@12345", null));

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Me_requires_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Profile_update_validates_and_saves()
    {
        var auth = await RegisterAndVerifyAsync(NewEmail());
        var client = factory.CreateClient().WithBearer(auth.AccessToken);
        var notifications = new NotificationPreferencesDto(true, false, true, true, true, false);

        var bad = await client.PutAsJsonAsync("/api/v1/me",
            new UpdateProfileRequest("Name", null, null, null, "xx", "UTC", null, notifications), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await client.PutAsJsonAsync("/api/v1/me",
            new UpdateProfileRequest("New Name", "nn", "Learning Spanish", null, "ur", "Asia/Karachi", new TimeOnly(19, 30), notifications),
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var saved = await good.ReadDataAsync<UserProfileDto>();
        Assert.Equal("New Name", saved.FullName);
        Assert.Equal("ur", saved.UiLanguage);
        Assert.False(saved.Notifications.QuizReady);
    }

    [Fact]
    public async Task Deleted_account_can_no_longer_sign_in()
    {
        var email = NewEmail();
        var auth = await RegisterAndVerifyAsync(email);
        var client = factory.CreateClient().WithBearer(auth.AccessToken);

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(Password, "DELETE"), options: ApiFactory.Json)
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);

        var login = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password, null));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Languages_list_shows_coming_soon_and_all_for_native_picker()
    {
        var client = factory.CreateClient();

        var onboarding = await (await client.GetAsync("/api/v1/languages")).ReadDataAsync<List<LanguageDto>>();
        Assert.Contains(onboarding, l => l.IsoCode == "es" && l.Status == LanguageStatus.ComingSoon);
        Assert.DoesNotContain(onboarding, l => l.IsoCode == "ur"); // seeded as Draft

        var all = await (await client.GetAsync("/api/v1/languages?all=true")).ReadDataAsync<List<LanguageDto>>();
        Assert.Contains(all, l => l.IsoCode == "ur" && l.Direction == TextDirection.Rtl);
    }

    [Fact]
    public async Task Version_check_flags_old_versions()
    {
        var client = factory.CreateClient();

        var old = await (await client.GetAsync("/api/v1/app/version-check?platform=Android&version=0.9.0")).ReadDataAsync<VersionCheckResponse>();
        var current = await (await client.GetAsync("/api/v1/app/version-check?platform=Android&version=1.0.0")).ReadDataAsync<VersionCheckResponse>();

        Assert.True(old.UpdateRequired);
        Assert.False(current.UpdateRequired);
    }

    [Fact]
    public async Task Responses_carry_a_correlation_id()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }
}
