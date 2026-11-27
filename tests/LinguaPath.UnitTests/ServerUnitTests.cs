using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Catalog;
using LinguaPath.Application.Validation;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Security;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace LinguaPath.UnitTests;

public class OtpTests
{
    [Fact]
    public void Codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = OtpService.GenerateCode(6);
            Assert.Equal(6, code.Length);
            Assert.True(code.All(char.IsDigit));
        }
    }

    [Fact]
    public void Hash_depends_on_user_and_code()
    {
        var user = Guid.NewGuid();
        Assert.Equal(OtpService.Hash(user, "123456"), OtpService.Hash(user, "123456"));
        Assert.NotEqual(OtpService.Hash(user, "123456"), OtpService.Hash(user, "123457"));
        Assert.NotEqual(OtpService.Hash(user, "123456"), OtpService.Hash(Guid.NewGuid(), "123456"));
    }
}

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "LinguaPath",
        Audience = "LinguaPath.Mobile",
        SigningKey = "unit-test-signing-key-0123456789abcdefghij",
        AccessTokenMinutes = 15
    };

    [Fact]
    public async Task Access_token_validates_and_carries_user_role_and_stamp()
    {
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.com", FullName = "A B", SecurityStamp = "stamp-1" };

        var token = service.CreateAccessToken(user, [Roles.Learner]);
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }
            .ValidateTokenAsync(token.Token, JwtKeys.ValidationParameters(Options));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(user.Id.ToString(), result.Claims["sub"]);
        Assert.Equal(Roles.Learner, result.Claims[AppClaims.Role]);
        Assert.Equal("stamp-1", result.Claims[AppClaims.SecurityStamp]);
        Assert.InRange(token.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    [Fact]
    public void Refresh_tokens_are_random_and_hash_is_stable()
    {
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System);
        var a = service.GenerateRefreshToken();
        var b = service.GenerateRefreshToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 80);
        Assert.Equal(service.HashToken(a), service.HashToken(a));
        Assert.NotEqual(a, service.HashToken(a));
    }

    [Fact]
    public void Short_signing_key_is_rejected()
    {
        var bad = new JwtOptions { SigningKey = "too-short" };
        Assert.Throws<InvalidOperationException>(() => JwtTokenService.SigningKey(bad));
    }
}

public class ValidatorTests
{
    [Theory]
    [InlineData("Password1", true)]
    [InlineData("password1", false)]   // no capital
    [InlineData("PASSWORD1", false)]   // no small letter
    [InlineData("Password", false)]    // no number
    [InlineData("Pa1", false)]         // too short
    public void Password_policy(string password, bool valid)
    {
        var result = new RegisterRequestValidator().Validate(new RegisterRequest("Name", "a@b.com", password, null, null, null));
        Assert.Equal(valid, !result.Errors.Any(e => e.PropertyName == nameof(RegisterRequest.Password)));
    }

    [Fact]
    public void Unknown_time_zone_and_ui_language_are_rejected()
    {
        var result = new RegisterRequestValidator().Validate(new RegisterRequest("Name", "a@b.com", "Password1", null, "Mars/Base", "fr"));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.TimeZone));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.UiLanguage));
    }

    [Fact]
    public void Otp_must_be_six_digits()
    {
        var validator = new VerifyEmailRequestValidator();
        Assert.False(validator.Validate(new VerifyEmailRequest("a@b.com", "12345", null)).IsValid);
        Assert.False(validator.Validate(new VerifyEmailRequest("a@b.com", "12a456", null)).IsValid);
        Assert.True(validator.Validate(new VerifyEmailRequest("a@b.com", "123456", null)).IsValid);
    }
}

public class AppVersionServiceTests
{
    private sealed class FakeSettings(Dictionary<string, string> values) : ISettingsService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(key));
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<IReadOnlyList<SystemSetting>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemSetting>>([]);
        public Task<Result> SetAsync(string key, string value, CancellationToken ct = default) => Task.FromResult(Result.Success());
    }

    private static AppVersionService Service() => new(new FakeSettings(new()
    {
        [SettingKeys.MinAppVersionAndroid] = "1.2.0",
        [SettingKeys.LatestAppVersionAndroid] = "1.5.0",
        [SettingKeys.ForceUpdateMessage] = "Please update"
    }));

    [Theory]
    [InlineData("1.1.9", true, true)]
    [InlineData("1.2.0", false, true)]
    [InlineData("1.5.0", false, false)]
    [InlineData("1.5.0-beta", false, false)]
    public async Task Compares_versions(string version, bool required, bool available)
    {
        var result = await Service().CheckAsync(DevicePlatform.Android, version);
        Assert.Equal(required, result.UpdateRequired);
        Assert.Equal(available, result.UpdateAvailable);
        Assert.Equal(required ? "Please update" : null, result.Message);
    }

    [Fact]
    public async Task Unknown_version_never_blocks()
    {
        var result = await Service().CheckAsync(DevicePlatform.Android, "garbage");
        Assert.False(result.UpdateRequired);
    }
}
