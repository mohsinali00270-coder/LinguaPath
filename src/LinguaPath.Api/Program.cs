using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LinguaPath.Api.Controllers;
using LinguaPath.Api.Infrastructure;
using LinguaPath.Application;
using LinguaPath.Application.Common;
using LinguaPath.Domain.Constants;
using LinguaPath.Infrastructure;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---------------------------------------------------------------- logging (NFR-14)
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
        .WriteTo.File("logs/api-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    // ---------------------------------------------------------------- layers
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // ---------------------------------------------------------------- MVC + validation + errors (SRS 8.3)
    builder.Services.AddScoped<ValidationFilter>();
    builder.Services.AddControllers(o => o.Filters.AddService<ValidationFilter>())
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
        .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
            ProblemResults.ToResult(ctx.HttpContext, ProblemResults.ModelStateError(ctx.ModelState)));
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddResponseCaching();
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);
    if (!builder.Environment.IsEnvironment("Testing"))
    {
        builder.Services.AddHostedService<LinguaPath.Api.Infrastructure.AttemptExpiryService>();
        // Phase 10: FR-L110 reminders and the daily NFR-08 clean-up.
        builder.Services.AddHostedService<ReminderWorker>();
        builder.Services.AddHostedService<MaintenanceWorker>();
    }

    // ---------------------------------------------------------------- response compression (NFR-01). WebSockets are not compressed.
    builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
        o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
        o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    });
    builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

    // ---------------------------------------------------------------- chat (FR-C01 .. FR-C05): SignalR hub and its real-time ports
    var signalR = builder.Services.AddSignalR(o =>
    {
        o.MaximumReceiveMessageSize = 32 * 1024;
        o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    }).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    // Phase 10: with Redis:ConnectionString set, hub messages reach clients on every API server (scale-out, see docs/OPERATIONS.md).
    var redis = builder.Configuration["Redis:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(redis))
    {
        signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("linguapath"));
        builder.Services.AddStackExchangeRedisCache(o => { o.Configuration = redis; o.InstanceName = "linguapath:"; });
    }
    builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, LinguaPath.Api.Chat.SubUserIdProvider>();
    builder.Services.AddSingleton<LinguaPath.Api.Chat.PresenceTracker>();
    builder.Services.AddSingleton<LinguaPath.Application.Features.Moderation.IPresenceTracker>(sp => sp.GetRequiredService<LinguaPath.Api.Chat.PresenceTracker>());
    builder.Services.AddSingleton<LinguaPath.Application.Features.Moderation.IChatNotifier, LinguaPath.Api.Chat.SignalRChatNotifier>();
    builder.Services.AddSingleton<LinguaPath.Application.Features.Moderation.IUserSessionCache, LinguaPath.Api.Chat.ApiUserSessionCache>();
    builder.Services.AddSingleton<LinguaPath.Api.Chat.ModerationScanQueue>();
    builder.Services.AddSingleton<LinguaPath.Application.Features.Moderation.IModerationQueue>(sp => sp.GetRequiredService<LinguaPath.Api.Chat.ModerationScanQueue>());
    if (!builder.Environment.IsEnvironment("Testing"))
        builder.Services.AddHostedService<LinguaPath.Api.Chat.ModerationScanWorker>();

    // ---------------------------------------------------------------- competitions (FR-Q03): hub, live events and the clock
    builder.Services.AddSingleton<LinguaPath.Application.Features.Competitions.ICompetitionNotifier, LinguaPath.Api.Chat.SignalRCompetitionNotifier>();
    if (!builder.Environment.IsEnvironment("Testing"))
        builder.Services.AddHostedService<LinguaPath.Api.Chat.CompetitionTicker>();

    // ---------------------------------------------------------------- authentication (FR-L02, FR-L03, NFR-05)
    var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
    var auth = builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = JwtKeys.ValidationParameters(jwt);
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = SecurityStampCheck.ValidateAsync,
                // WebSockets can't send headers: the chat hub takes the token from the query string.
                OnMessageReceived = ctx =>
                {
                    var token = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                    return Task.CompletedTask;
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    await ProblemResults.WriteAsync(ctx.HttpContext,
                        Error.Unauthorized(LinguaPath.Shared.ErrorCodes.Unauthorized, "Please sign in."));
                },
                OnForbidden = ctx => ProblemResults.WriteAsync(ctx.HttpContext,
                    Error.Forbidden(LinguaPath.Shared.ErrorCodes.Forbidden, "You do not have access to this."))
            };
        })
        .AddCookie(ExternalAuthController.ExternalCookieScheme, o =>
        {
            o.Cookie.Name = "lp.external";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        });

    // Browser-based Google sign-in for the app; enabled only when a web client id/secret is configured.
    var googleWebId = builder.Configuration["ExternalAuth:GoogleWeb:ClientId"];
    var googleWebSecret = builder.Configuration["ExternalAuth:GoogleWeb:ClientSecret"];
    if (!string.IsNullOrWhiteSpace(googleWebId) && !string.IsNullOrWhiteSpace(googleWebSecret))
    {
        auth.AddGoogle(ExternalAuthController.GoogleScheme, o =>
        {
            o.SignInScheme = ExternalAuthController.ExternalCookieScheme;
            o.ClientId = googleWebId;
            o.ClientSecret = googleWebSecret;
            o.CallbackPath = "/signin-google";
        });
    }

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Policies.LearnerOnly, p => p.RequireRole(Roles.Learner))
        .AddPolicy(Policies.AdminPanel, p => p.RequireRole(Roles.AdminRoles))
        .AddPolicy(Policies.SuperAdminOnly, p => p.RequireRole(Roles.SuperAdmin))
        .AddPolicy(Policies.ContentManagers, p => p.RequireRole(Roles.SuperAdmin, Roles.ContentAdmin))
        .AddPolicy(Policies.Reviewers, p => p.RequireRole(Roles.SuperAdmin, Roles.Reviewer))
        .AddPolicy(Policies.Moderators, p => p.RequireRole(Roles.SuperAdmin, Roles.Moderator));

    // ---------------------------------------------------------------- rate limiting (NFR-06)
    var authPerMinute = builder.Configuration.GetValue("RateLimiting:AuthPerMinute", 10);
    var globalPerMinute = builder.Configuration.GetValue("RateLimiting:GlobalPerMinute", 120);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = (ctx, _) => new ValueTask(ProblemResults.WriteAsync(ctx.HttpContext, ProblemResults.RateLimited()));

        // Sign-in style endpoints: 10 per minute per IP by default.
        o.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        // Public certificate checks: 30 per minute per IP, so numbers can't be walked quickly.
        o.AddPolicy(RateLimitPolicies.Verify, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        // Crash reports from the app: 20 per minute per IP.
        o.AddPolicy(RateLimitPolicies.ClientErrors, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

        // Everything: 120 per minute per user (or IP when anonymous) by default.
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = globalPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });

    // ---------------------------------------------------------------- CORS (only needed for browser clients)
    var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    }));

    // ---------------------------------------------------------------- Swagger
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "LinguaPath API",
            Version = "v1",
            Description = "Language learning platform API. Successful responses use { success, data, message, errors }; failures are RFC 7807 Problem Details."
        });
        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Paste the accessToken from /api/v1/auth/login"
        });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
        });
        var xml = Path.Combine(AppContext.BaseDirectory, "LinguaPath.Api.xml");
        if (File.Exists(xml)) o.IncludeXmlComments(xml);
    });

    // Behind IIS / a reverse proxy: trust X-Forwarded-For/Proto. Add your proxy's address to KnownProxies in production.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

    var app = builder.Build();

    // ---------------------------------------------------------------- pipeline
    app.UseForwardedHeaders();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
    {
        app.UseSwagger();
        app.UseSwaggerUI(o => o.DocumentTitle = "LinguaPath API");
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection(); // in development the Android emulator talks plain HTTP to 10.0.2.2:5080
    }
    app.UseResponseCompression();
    app.UseStaticFiles(); // wwwroot/uploads (local file storage) and wwwroot/legal (privacy, terms, account deletion)
    // A shared upload folder outside wwwroot (Storage:LocalRoot) is served at the same /uploads path.
    var storageOptions = app.Configuration.GetSection(LinguaPath.Infrastructure.Services.StorageOptions.Section)
        .Get<LinguaPath.Infrastructure.Services.StorageOptions>() ?? new();
    if (!string.IsNullOrWhiteSpace(storageOptions.LocalRoot))
    {
        var uploads = storageOptions.ResolveRoot(app.Environment.ContentRootPath, app.Environment.WebRootPath);
        Directory.CreateDirectory(uploads);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploads),
            RequestPath = "/" + storageOptions.LocalFolder
        });
    }
    app.UseCors();
    app.UseResponseCaching();
    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<LinguaPath.Api.Chat.ChatHub>("/hubs/chat");
    app.MapHub<LinguaPath.Api.Chat.CompetitionHub>("/hubs/competition");
    app.MapLinguaPathHealth();
    // Store listings link to these (Google Play and App Store require a privacy policy and an account-deletion page).
    app.MapGet("/privacy", () => Results.Redirect("/legal/privacy.html")).ExcludeFromDescription();
    app.MapGet("/terms", () => Results.Redirect("/legal/terms.html")).ExcludeFromDescription();
    app.MapGet("/delete-account", () => Results.Redirect("/legal/delete-account.html")).ExcludeFromDescription();
    app.MapGet("/", (IWebHostEnvironment env) => env.IsDevelopment() ? Results.Redirect("/swagger") : Results.Redirect("/health/live"))
        .ExcludeFromDescription();

    if (!app.Configuration.GetValue<bool>("Database:SkipInitialization"))
        await DbInitializer.InitialiseAsync(app.Services);

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException && ex.Source != "Microsoft.EntityFrameworkCore.Design")
{
    Log.Fatal(ex, "API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string Verify = "verify";
    public const string ClientErrors = "client-errors";
}

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
