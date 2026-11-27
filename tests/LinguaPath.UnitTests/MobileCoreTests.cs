using System.Net;
using System.Text;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;
using Refit;

namespace LinguaPath.UnitTests;

public class CheckTests
{
    [Theory]
    [InlineData("a@b.com", null)]
    [InlineData("", "Email is required.")]
    [InlineData("nope", "Enter a valid email address.")]
    public void Email(string value, string? expected) => Assert.Equal(expected, Check.Email(value));

    [Fact]
    public void Password_rules_match_the_server()
    {
        Assert.Null(Check.Password("Password1"));
        Assert.NotNull(Check.Password("password1"));
        Assert.NotNull(Check.Password("Pass1"));
    }

    [Fact]
    public void All_collects_field_errors()
    {
        var errors = new FieldErrors();
        var ok = Check.All(errors, ("Email", Check.Email("x")), ("Password", null));
        Assert.False(ok);
        Assert.Equal("Enter a valid email address.", errors["Email"]);
        Assert.Null(errors["Password"]);
    }
}

public class ApiErrorTests
{
    [Fact]
    public async Task Problem_details_become_message_code_and_field_errors()
    {
        const string body = """{"type":"x","title":"Validation failed","status":400,"detail":"One or more fields are invalid.","code":"validation_failed","success":false,"errors":{"Email":["Enter a valid email address."]}}""";
        var ex = await CreateApiException(HttpStatusCode.BadRequest, body);

        var error = ApiError.From(ex);

        Assert.Equal("One or more fields are invalid.", error.Message);
        Assert.Equal(ErrorCodes.Validation, error.Code);
        Assert.Equal("Enter a valid email address.", error.FieldErrors["email"][0]); // case-insensitive
    }

    [Fact]
    public async Task Non_json_bodies_fall_back_to_friendly_text()
    {
        var ex = await CreateApiException(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        Assert.Equal("The server had a problem. Please try again shortly.", ApiError.From(ex).Message);
    }

    [Fact]
    public void Network_failures_are_flagged()
    {
        var error = ApiError.From(new HttpRequestException("no route"));
        Assert.True(error.IsNetwork);
    }

    private static async Task<ApiException> CreateApiException(HttpStatusCode status, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/v1/auth/register");
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
            RequestMessage = request
        };
        return await ApiException.Create(request, HttpMethod.Post, response, new RefitSettings());
    }
}

public class AuthSessionTests
{
    [Fact]
    public async Task Expired_access_token_is_refreshed_once_and_saved()
    {
        var store = new MemoryStore();
        var api = new FakeAuthApi();
        var session = new AuthSession(store, api, new Lazy<IAccountApi>(() => throw new NotSupportedException()), new FakeDevice());

        await session.SignInWithTokensAsync("old-access", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddDays(30));

        // Two callers at once should cause only one refresh call.
        var tokens = await Task.WhenAll(session.GetAccessTokenAsync(), session.GetAccessTokenAsync());

        Assert.All(tokens, t => Assert.Equal("access-2", t));
        Assert.Equal(1, api.RefreshCalls);
        Assert.Equal("refresh-2", await store.GetAsync("lp.refresh"));
        Assert.Equal("Test", session.User?.FullName);
    }

    [Fact]
    public async Task Rejected_refresh_signs_out_and_raises_event()
    {
        var store = new MemoryStore();
        var api = new FakeAuthApi { RejectRefresh = true };
        var session = new AuthSession(store, api, new Lazy<IAccountApi>(() => throw new NotSupportedException()), new FakeDevice());
        var signedOut = false;
        session.SignedOut += (_, _) => signedOut = true;

        await session.SignInWithTokensAsync("old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow.AddDays(30));
        var token = await session.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.True(signedOut);
        Assert.False(session.HasTokens);
        Assert.Null(await store.GetAsync("lp.refresh"));
    }

    private sealed class MemoryStore : ISecureStore
    {
        private readonly Dictionary<string, string> _d = new();
        public Task<string?> GetAsync(string key) => Task.FromResult(_d.GetValueOrDefault(key));
        public Task SetAsync(string key, string value) { _d[key] = value; return Task.CompletedTask; }
        public void Remove(string key) => _d.Remove(key);
    }

    private sealed class FakeDevice : IDeviceContext
    {
        public string DeviceId => "dev-1";
        public DevicePlatform Platform => DevicePlatform.Android;
        public string AppVersion => "1.0.0";
        public string TimeZoneId => "UTC";
        public bool IsAppleSignInAvailable => false;
    }

    private sealed class FakeAuthApi : IAuthApi
    {
        public int RefreshCalls;
        public bool RejectRefresh;

        public async Task<MyApiResponse<AuthResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref RefreshCalls);
            await Task.Delay(50, ct);
            if (RejectRefresh)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/v1/auth/refresh");
                throw await ApiException.Create(req, HttpMethod.Post,
                    new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = req, Content = new StringContent("{}") },
                    new RefitSettings());
            }
            var user = new UserSummaryDto(Guid.NewGuid(), "t@t.com", "Test", null, null, "en", ["Learner"], false);
            return MyApiResponse<AuthResponse>.Ok(new AuthResponse("access-2", DateTimeOffset.UtcNow.AddMinutes(15), "refresh-2",
                DateTimeOffset.UtcNow.AddDays(30), user));
        }

        public Task<MyApiResponse<RegisterResponse>> RegisterAsync(RegisterRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<ResendOtpResponse>> ResendOtpAsync(ResendOtpRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<AuthResponse>> LoginAsync(LoginRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<AuthResponse>> GoogleAsync(ExternalLoginRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<AuthResponse>> AppleAsync(ExternalLoginRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiResponse> ForgotPasswordAsync(ForgotPasswordRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiResponse> ResetPasswordAsync(ResetPasswordRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<List<LanguageDto>>> GetLanguagesAsync(bool all = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MyApiResponse<VersionCheckResponse>> VersionCheckAsync(DevicePlatform platform, string version, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>Phase 10: English / Urdu resources (NFR-10) and where a tapped notification opens.</summary>
public class LocalizationAndRoutingTests
{
    [Fact]
    public void Every_key_has_an_urdu_text_and_urdu_is_right_to_left()
    {
        Assert.Equal("Sign in", LinguaPath.Mobile.Core.Localization.Localizer.In("en", "SignIn"));
        Assert.Equal("سائن اِن", LinguaPath.Mobile.Core.Localization.Localizer.In("ur", "SignIn"));
        Assert.Equal("ای میل ضروری ہے۔", LinguaPath.Mobile.Core.Localization.Localizer.In("ur-PK", "Val_EmailRequired"));
        Assert.Equal("NoSuchKey", LinguaPath.Mobile.Core.Localization.Localizer.In("ur", "NoSuchKey"));
        Assert.True(System.Globalization.CultureInfo.GetCultureInfo("ur").TextInfo.IsRightToLeft);
    }

    [Theory]
    [InlineData("ur-PK", "ur")]
    [InlineData("UR", "ur")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public void Languages_normalise_to_en_or_ur(string? code, string expected) =>
        Assert.Equal(expected, LinguaPath.Mobile.Core.Localization.Localizer.Normalise(code));

    [Fact]
    public void Notification_data_opens_the_matching_screen()
    {
        var quiz = NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "dailyQuiz", ["learnerCourseId"] = "7" })!.Value;
        Assert.Equal(Routes.Quiz, quiz.Route);
        Assert.Equal("daily", quiz.Parameters!["mode"]);
        Assert.Equal("7", quiz.Parameters["learnerCourseId"]);

        Assert.Equal(Routes.Chat, NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "chat", ["conversationId"] = "3" })!.Value.Route);
        Assert.Equal(Routes.Match, NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "competition", ["competitionId"] = "9" })!.Value.Route);
        Assert.Equal(Routes.Certificates, NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "certificate" })!.Value.Route);
        Assert.Equal(Routes.Home, NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "today" })!.Value.Route);
        Assert.Null(NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "exams" })); // needs the course
        Assert.Null(NotificationRouter.Resolve(null));
        Assert.Null(NotificationRouter.Resolve(new Dictionary<string, string> { ["type"] = "unknown" }));
    }

    [Fact]
    public void Inbox_times_read_short()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("5m", NotificationItem.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3h", NotificationItem.Ago(now.AddHours(-3), now));
        Assert.Equal("2d", NotificationItem.Ago(now.AddDays(-2), now));
    }
}
