using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Infrastructure.Security;
using LinguaPath.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence, ASP.NET Identity (stores + managers), security and platform services.
    /// Each host adds its own authentication schemes (JWT for the API, cookies for the admin panel).
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.Configure<OtpOptions>(config.GetSection(OtpOptions.Section));
        services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
        services.Configure<ExternalAuthOptions>(config.GetSection(ExternalAuthOptions.Section));
        services.Configure<SeedOptions>(config.GetSection(SeedOptions.Section));
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));

        // ---- database: SQL Server by default; "Sqlite" is for tests and quick local trials.
        var provider = config["Database:Provider"] ?? "SqlServer";
        var connection = config.GetConnectionString("DefaultConnection")
                         ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is missing.");
        services.AddDbContext<AppDbContext>(options =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                options.UseSqlite(connection);
            else
                options.UseSqlServer(connection, sql =>
                {
                    sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                    sql.EnableRetryOnFailure(3);
                });
        });
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // ---- identity (NFR-05: hashed passwords, lockout)
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // ---- services
        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IOtpService, OtpService>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IExternalAuthValidator, ExternalAuthValidator>();
        services.AddScoped<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IHtmlSanitizerService, HtmlSanitizerService>();
        services.AddSingleton<IQuestionSpreadsheet, QuestionSpreadsheet>();
        services.AddSingleton<Application.Features.Moderation.IWordListSpreadsheet, WordListSpreadsheet>();

        // ---- AI (SRS 5): provider chosen in Settings, keys from configuration
        services.Configure<Ai.AiOptions>(config.GetSection(Ai.AiOptions.Section));
        services.AddHttpClient("ai");
        services.AddSingleton<Ai.OfflineAiChatClient>();
        services.AddScoped<Application.Features.Ai.IAiChatClientFactory, Ai.AiChatClientFactory>();

        // Azure Translator and Azure Speech, with offline stand-ins (Phase 6).
        services.Configure<LanguageServices.LanguageServicesOptions>(config.GetSection(LanguageServices.LanguageServicesOptions.Section));
        services.AddHttpClient("language");
        services.AddHttpClient("storage", c => c.Timeout = TimeSpan.FromSeconds(30));

        // Certificates (Phase 7)
        services.AddSingleton<Application.Features.Progress.ICertificateRenderer, Certificates.QuestPdfCertificateRenderer>();
        services.AddScoped<Application.Features.Practice.ITranslator, LanguageServices.TranslatorSelector>();
        services.AddScoped<Application.Features.Practice.ISpeechAssessor, LanguageServices.SpeechSelector>();

        // Phase 8: push notifications (FCM HTTP v1, or the log when Push:FcmProjectId/FcmServiceAccount aren't set).
        services.Configure<Push.PushOptions>(config.GetSection(Push.PushOptions.Section));
        // Phase 9: live competition timings.
        services.Configure<Application.Features.Competitions.CompetitionOptions>(config.GetSection(Application.Features.Competitions.CompetitionOptions.Section));
        services.AddHttpClient("push", c => c.Timeout = TimeSpan.FromSeconds(15));
        // Phase 10: reminder timings, data retention and report exports.
        services.Configure<Application.Features.Notifications.ReminderOptions>(config.GetSection(Application.Features.Notifications.ReminderOptions.Section));
        services.Configure<Application.Features.Notifications.RetentionOptions>(config.GetSection(Application.Features.Notifications.RetentionOptions.Section));
        services.AddSingleton<Application.Features.Admin.IReportExporter, Services.ReportExporter>();
        services.AddSingleton<Push.FcmPushSender>();
        services.AddSingleton<Push.LoggingPushSender>();
        services.AddSingleton<Application.Features.Moderation.IPushSender, Push.PushSenderSelector>();

        if ((config["Email:Mode"] ?? "Pickup").Equals("Smtp", StringComparison.OrdinalIgnoreCase))
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        else
            services.AddScoped<IEmailSender, PickupEmailSender>();

        return services;
    }

    /// <summary>Runs the AI job queue in this host (the admin panel). Off when Ai:Runner:Enabled is false.</summary>
    public static IServiceCollection AddAiJobRunner(this IServiceCollection services, IConfiguration config)
    {
        if (config.GetValue("Ai:Runner:Enabled", true)) services.AddHostedService<Ai.AiJobRunner>();
        return services;
    }
}
