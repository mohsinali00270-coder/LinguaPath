using LinguaPath.Admin.Infrastructure;
using LinguaPath.Application;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure;
using LinguaPath.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File("logs/admin-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddAiJobRunner(builder.Configuration); // SRS 5.3: AI jobs run in the admin host

    // Cookie sign-in with ASP.NET Identity; the stamp is re-checked every minute so
    // deactivated admins or removed roles take effect quickly.
    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
    builder.Services.ConfigureApplicationCookie(o =>
    {
        o.Cookie.Name = "lp.admin";
        o.Cookie.HttpOnly = true;
        // Phase 10: HTTPS-only cookie outside development; SameSite=Lax (links from emails still open signed in; forms are covered by antiforgery).
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
    builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
    builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AdminClaimsPrincipalFactory>();

    var adminPanel = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(Roles.AdminRoles).Build();
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(adminPanel) // every page needs an admin role unless marked [AllowAnonymous]
        .AddPolicy(Policies.AdminPanel, adminPanel)
        .AddPolicy(Policies.SuperAdminOnly, p => p.RequireRole(Roles.SuperAdmin))
        .AddPolicy(Policies.ContentManagers, p => p.RequireRole(Roles.SuperAdmin, Roles.ContentAdmin))
        .AddPolicy(Policies.Reviewers, p => p.RequireRole(Roles.SuperAdmin, Roles.Reviewer))
        .AddPolicy(Policies.Moderators, p => p.RequireRole(Roles.SuperAdmin, Roles.Moderator));

    builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
    builder.Services.AddAntiforgery(o =>
    {
        o.Cookie.Name = "lp.admin.af";
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    });
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }
    app.UseMiddleware<LinguaPath.Admin.Infrastructure.AdminSecurityHeadersMiddleware>();
    app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapStaticAssets().AllowAnonymous();
    app.MapHealthChecks("/health/live").AllowAnonymous();
    app.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}")
        .WithStaticAssets();

    if (!app.Configuration.GetValue<bool>("Database:SkipInitialization"))
        await DbInitializer.InitialiseAsync(app.Services);

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Admin panel terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
