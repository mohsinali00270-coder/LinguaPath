namespace LinguaPath.Admin.Infrastructure;

/// <summary>
/// Security headers for the admin panel (NFR-05). Scripts and styles come from this site and jsDelivr (Bootstrap, Quill).
/// 'unsafe-inline' is still needed for scripts because a few views use inline handlers; see docs/SECURITY.md.
/// Images and audio may come from the API's upload host, so any https source is allowed for media.
/// </summary>
public sealed class AdminSecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    private readonly string _csp = string.Join("; ",
        "default-src 'self'",
        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net",
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net",
        "font-src 'self' data: https://cdn.jsdelivr.net",
        env.IsDevelopment() ? "img-src 'self' data: blob: https: http:" : "img-src 'self' data: blob: https:",
        env.IsDevelopment() ? "media-src 'self' blob: https: http:" : "media-src 'self' blob: https:",
        "connect-src 'self'",
        "frame-ancestors 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "object-src 'none'");

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = _csp;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h.Remove("Server");
            return Task.CompletedTask;
        });
        return next(context);
    }
}
