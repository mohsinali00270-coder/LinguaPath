using LinguaPath.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LinguaPath.Api.Infrastructure;

/// <summary>
/// Security headers for every response (NFR-05, OWASP ASVS V14). The API returns JSON, so the content security
/// policy blocks everything except the small legal pages' own styles and images.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ApiCsp = "default-src 'none'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            // Swagger UI needs its own scripts; it is only on in development or when Swagger:Enabled is set.
            if (!path.StartsWithSegments("/swagger")) h["Content-Security-Policy"] = ApiCsp;
            // Uploaded files can be fetched by the app; nothing else should cache personal JSON.
            if (path.StartsWithSegments("/api") && !h.ContainsKey("Cache-Control")) h.CacheControl = "no-store";
            h.Remove("Server");
            h.Remove("X-Powered-By");
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>Readiness: can we reach the database? (/health/ready). Liveness (/health/live) checks nothing.</summary>
internal sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("Database reachable")
                : HealthCheckResult.Unhealthy("Database not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database check failed", ex);
        }
    }
}

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>/health (all checks), /health/live (process is up) and /health/ready (database reachable), as JSON.</summary>
    public static void MapLinguaPathHealth(this IEndpointRouteBuilder app)
    {
        var options = new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { ResponseWriter = WriteAsync };
        app.MapHealthChecks("/health", options);
        app.MapHealthChecks("/health/live", new() { Predicate = _ => false, ResponseWriter = WriteAsync });
        app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
    }

    private static Task WriteAsync(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalMs = (int)report.TotalDuration.TotalMilliseconds,
            // Names and states only: exception details stay in the log.
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), ms = (int)e.Value.Duration.TotalMilliseconds })
        });
    }
}
