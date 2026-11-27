using FluentValidation;
using LinguaPath.Application.Common;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Infrastructure.Security;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Serilog.Context;

namespace LinguaPath.Api.Infrastructure;

/// <summary>Runs the FluentValidation validator for every action argument that has one.</summary>
public sealed class ValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;
            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (services.GetService(validatorType) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            foreach (var group in result.Errors.GroupBy(e => e.PropertyName))
                errors[group.Key] = group.Select(e => e.ErrorMessage).Distinct().ToArray();
        }

        if (errors.Count > 0)
        {
            context.Result = ProblemResults.ToResult(context.HttpContext, Error.Validation(errors));
            return;
        }
        await next();
    }
}

/// <summary>Unhandled exceptions become a 500 Problem Details; details are only shown in Development.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 499;
            return true;
        }

        if (exception is UnauthorizedAccessException)
        {
            await ProblemResults.WriteAsync(http, Error.Unauthorized(ErrorCodes.Unauthorized, "Please sign in again."));
            return true;
        }

        logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
        var message = env.IsDevelopment() ? exception.Message : "Something went wrong. Please try again.";
        await ProblemResults.WriteAsync(http, Error.Failure(message));
        return true;
    }
}

/// <summary>Reads or creates X-Correlation-ID, returns it, and adds it to every log line (NFR-14).</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[Header].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) id = Guid.NewGuid().ToString("N");

        context.TraceIdentifier = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
            await next(context);
    }
}

/// <summary>
/// Rejects access tokens whose user was blocked, suspended, deleted, or signed out everywhere
/// (security stamp changed). Cached for 60 seconds per user to avoid a DB hit on every request.
/// </summary>
public static class SecurityStampCheck
{
    private sealed record UserState(string? Stamp, bool CanUse);

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var sub = context.Principal?.FindFirst("sub")?.Value;
        var stamp = context.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
        if (!Guid.TryParse(sub, out var userId))
        {
            context.Fail("Missing subject.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var cache = services.GetRequiredService<IMemoryCache>();
        var state = await cache.GetOrCreateAsync($"user-state:{userId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            var db = services.GetRequiredService<AppDbContext>();
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.SecurityStamp, u.IsActive, u.IsBlocked, u.IsDeleted })
                .FirstOrDefaultAsync();
            return row is null
                ? new UserState(null, false)
                : new UserState(row.SecurityStamp, row.IsActive && !row.IsBlocked && !row.IsDeleted);
        });

        if (state is null || !state.CanUse || state.Stamp != stamp)
            context.Fail("Session is no longer valid.");
    }

    /// <summary>Call after changing a user's stamp or status so the next request sees it at once.</summary>
    public static void Invalidate(IMemoryCache cache, Guid userId) => cache.Remove($"user-state:{userId}");
}
