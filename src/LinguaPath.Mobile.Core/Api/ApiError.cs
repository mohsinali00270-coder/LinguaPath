using System.Net;
using System.Text.Json;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using Refit;

namespace LinguaPath.Mobile.Core.Api;

/// <summary>A failed API call turned into something a screen can show.</summary>
public sealed class ApiError
{
    public string Message { get; init; } = Localization.Localizer.T("Err_Generic");
    public string Code { get; init; } = ErrorCodes.ServerError;
    public HttpStatusCode? Status { get; init; }
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; init; } = new Dictionary<string, string[]>();

    public bool IsNetwork { get; init; }

    public static ApiError From(Exception ex) => ex switch
    {
        ApiException api => FromApiException(api),
        HttpRequestException or TaskCanceledException or TimeoutException => new ApiError
        {
            Message = Localization.Localizer.T("Err_Network"),
            Code = "network",
            IsNetwork = true
        },
        Services.ExternalSignInException ext => new ApiError
        {
            Message = ext.Message,
            Code = ErrorCodes.ExternalLoginFailed
        },
        SessionExpiredException => new ApiError
        {
            Message = Localization.Localizer.T("Err_SessionExpired"),
            Code = ErrorCodes.InvalidRefreshToken,
            Status = HttpStatusCode.Unauthorized
        },
        _ => new ApiError()
    };

    private static ApiError FromApiException(ApiException ex)
    {
        ApiProblem? problem = null;
        if (!string.IsNullOrWhiteSpace(ex.Content))
        {
            try
            {
                problem = JsonSerializer.Deserialize<ApiProblem>(ex.Content, JsonDefaults.Options);
            }
            catch (JsonException)
            {
                // Not a Problem Details body (proxy page, HTML error): fall back to a generic message.
            }
        }

        var fallback = ex.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => Localization.Localizer.T("Err_TooMany"),
            HttpStatusCode.Unauthorized => Localization.Localizer.T("Err_SignInAgain"),
            HttpStatusCode.Forbidden => Localization.Localizer.T("Err_Forbidden"),
            HttpStatusCode.NotFound => Localization.Localizer.T("Err_NotFound"),
            >= HttpStatusCode.InternalServerError => Localization.Localizer.T("Err_Server"),
            _ => Localization.Localizer.T("Err_Generic")
        };

        return new ApiError
        {
            Message = string.IsNullOrWhiteSpace(problem?.Detail) ? fallback : problem.Detail!,
            Code = problem?.Code ?? ex.StatusCode.ToString(),
            Status = ex.StatusCode,
            FieldErrors = problem?.Errors?.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, string[]>()
        };
    }
}

/// <summary>Thrown by the auth handler when the refresh token is no longer valid.</summary>
public sealed class SessionExpiredException() : Exception("The session has expired.");

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return o;
    }
}
