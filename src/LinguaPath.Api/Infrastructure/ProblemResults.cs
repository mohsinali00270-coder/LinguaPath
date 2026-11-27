using LinguaPath.Application.Common;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Infrastructure;

/// <summary>Builds RFC 7807 Problem Details that also carry "success": false, "code" and "errors" (SRS 8.3).</summary>
public static class ProblemResults
{
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError
    };

    public static ProblemDetails Build(HttpContext http, Error error)
    {
        var status = StatusFor(error.Type);
        var problem = new ProblemDetails
        {
            Type = $"https://httpstatuses.io/{status}",
            Title = TitleFor(status),
            Status = status,
            Detail = error.Message,
            Instance = http.Request.Path
        };
        problem.Extensions["code"] = error.Code;
        problem.Extensions["success"] = false;
        if (error.Details is { Count: > 0 }) problem.Extensions["errors"] = error.Details;
        problem.Extensions["traceId"] = http.TraceIdentifier;
        return problem;
    }

    public static ObjectResult ToResult(HttpContext http, Error error)
    {
        var problem = Build(http, error);
        return new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" }
        };
    }

    public static async Task WriteAsync(HttpContext http, Error error)
    {
        var problem = Build(http, error);
        http.Response.StatusCode = problem.Status!.Value;
        await http.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }

    private static string TitleFor(int status) => status switch
    {
        400 => "Validation failed",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        429 => "Too many requests",
        _ => "Server error"
    };

    public static Error ModelStateError(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState) =>
        Error.Validation(modelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .ToDictionary(
                kv => string.IsNullOrEmpty(kv.Key) ? "Body" : kv.Key.TrimStart('$', '.'),
                kv => kv.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray()));

    public static Error RateLimited() =>
        Error.TooManyRequests(ErrorCodes.RateLimited, "Too many requests. Please slow down and try again shortly.");
}
