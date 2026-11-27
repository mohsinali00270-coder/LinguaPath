using LinguaPath.Application.Common;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence.Seed;
using LinguaPath.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds roles, the first Super Admin, starter languages and default settings.
/// Safe to run on every start-up: it only inserts what is missing.
/// </summary>
public static class DbInitializer
{
    public static async Task InitialiseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");

        if (db.Database.IsSqlServer() && db.Database.GetMigrations().Any())
        {
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            // SQLite (tests) or SQL Server before the first migration was generated.
            await db.Database.EnsureCreatedAsync(ct);
            if (db.Database.IsSqlServer())
                logger.LogWarning("No EF Core migrations found: schema was created with EnsureCreated. Generate migrations before production (see README).");
        }

        await SeedRolesAsync(sp, ct);
        await SeedSuperAdminAsync(sp, logger);
        await SeedLanguagesAsync(db, ct);
        await SeedSettingsAsync(db, ct);
        await SeedPointRulesAsync(db, ct);
        await SeedCertificateTemplateAsync(db, ct);
        await SeedAbusiveWordsAsync(db, ct);

        var seed = sp.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (seed.SampleContent)
        {
            await SeedSampleCourseAsync(db, logger, ct);
            await SeedSampleLectureContentAsync(db, ct);
            await SeedSampleQuestionsAsync(db, logger, ct);
            await SeedSamplePracticeAsync(db, logger, ct);
        }
    }

    /// <summary>FR-S03 defaults; admins change them in Points rules. Missing rules are added, edited ones are left alone.</summary>
    private static async Task SeedPointRulesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.PointRules.Select(r => r.Action).ToListAsync(ct)).ToHashSet();
        var added = false;
        foreach (var rule in SeedData.PointRules().Where(r => !existing.Contains(r.Action)))
        {
            db.PointRules.Add(rule);
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    /// <summary>A short starter list (FR-M01) so the filter works out of the box; moderators import full lists per language.</summary>
    private static async Task SeedAbusiveWordsAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.AbusiveWords.AnyAsync(ct)) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (term, severity) in SeedData.AbusiveWords())
            db.AbusiveWords.Add(new AbusiveWord
            {
                Term = term, NormalizedTerm = Application.Features.Moderation.ContentFilter.NormalizeText(term), Severity = severity, CreatedAt = now
            });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A plain default certificate design (FR-A82) so certificates can be issued before anyone designs one.</summary>
    private static async Task SeedCertificateTemplateAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.CertificateTemplates.IgnoreQueryFilters().AnyAsync(ct)) return;
        db.CertificateTemplates.Add(new CertificateTemplate
        {
            Name = "Default", Heading = "Certificate of Achievement", BodyText = Application.Features.Progress.CertificateDefaults.Body,
            SignatoryName = "LinguaPath Academic Team", SignatoryTitle = "Head of Learning", AccentColor = "#4F46E5", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Questions, a month 1 exam and placement questions for the sample course (Phase 4).</summary>
    private static async Task SeedSampleQuestionsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var course = await db.Courses.FirstOrDefaultAsync(c => c.Title == "Spanish Essentials (A1 → A2)" && c.Language.IsoCode == "es", ct);
        if (course is null || await db.Questions.AnyAsync(q => q.LanguageId == course.LanguageId, ct)) return;

        var lectures = await db.Lectures.Include(l => l.Examples).Include(l => l.Vocabulary)
            .Where(l => l.Module.CourseId == course.Id).OrderBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder).ToListAsync(ct);
        var questions = SampleQuestions.ForCourse(course, lectures);
        db.Questions.AddRange(questions);
        db.Questions.AddRange(SampleQuestions.Placement(course.LanguageId));
        if (!await db.Quizzes.AnyAsync(q => q.CourseId == course.Id, ct))
            db.Quizzes.Add(SampleQuestions.MonthOneExam(course.Id, questions));
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Sample content: added {Count} questions and a month 1 exam for '{Course}'.", questions.Count, course.Title);
    }

    /// <summary>A small practice library for the sample language (Phase 6).</summary>
    private static async Task SeedSamplePracticeAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var language = await db.Languages.FirstOrDefaultAsync(l => l.IsoCode == "es", ct);
        if (language is null || await db.PracticeItems.IgnoreQueryFilters().AnyAsync(p => p.LanguageId == language.Id, ct)) return;
        var items = SamplePractice.ForSpanish(language.Id);
        db.PracticeItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Sample content: added {Count} practice exercises for {Language}.", items.Count, language.Name);
    }

    /// <summary>Lesson text, examples and posts for the sample course (also fills a Phase 2 database).</summary>
    private static async Task SeedSampleLectureContentAsync(AppDbContext db, CancellationToken ct)
    {
        var course = await db.Courses.Include(c => c.Language)
            .FirstOrDefaultAsync(c => c.Title == "Spanish Essentials (A1 → A2)" && c.Language.IsoCode == "es", ct);
        if (course is null) return;

        var lectures = await db.Lectures.Include(l => l.Examples).Include(l => l.Links)
            .Where(l => l.Module.CourseId == course.Id).ToListAsync(ct);
        if (lectures.Any(l => string.IsNullOrWhiteSpace(l.BodyHtml)))
            SampleLectureContent.Fill(lectures);

        if (!await db.Posts.IgnoreQueryFilters().AnyAsync(p => p.LanguageId == course.LanguageId, ct))
            db.Posts.AddRange(SampleLectureContent.Posts(course.LanguageId, DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Publishes Spanish with a small A1 → A2 roadmap (titles and timings only; lecture text arrives in Phase 3).</summary>
    private static async Task SeedSampleCourseAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var spanish = await db.Languages.FirstOrDefaultAsync(l => l.IsoCode == "es", ct);
        if (spanish is null || await db.Courses.IgnoreQueryFilters().AnyAsync(c => c.LanguageId == spanish.Id, ct)) return;

        var course = SeedData.SampleSpanishCourse(spanish.Id);
        db.Courses.Add(course);
        spanish.Status = LanguageStatus.Published;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Sample content: published Spanish with the course '{Course}'.", course.Title);
    }

    private static async Task SeedRolesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var roleManager = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new ApplicationRole(role, Roles.Describe(role)));
        }
    }

    private static async Task SeedSuperAdminAsync(IServiceProvider sp, ILogger logger)
    {
        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await userManager.GetUsersInRoleAsync(Roles.SuperAdmin)).Count > 0) return;

        var seed = sp.GetRequiredService<IOptions<SeedOptions>>().Value;
        var admin = await userManager.FindByEmailAsync(seed.SuperAdminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = seed.SuperAdminEmail,
                Email = seed.SuperAdminEmail,
                EmailConfirmed = true,
                FullName = seed.SuperAdminName,
                UserType = UserType.Admin,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var result = await userManager.CreateAsync(admin, seed.SuperAdminPassword);
            if (!result.Succeeded)
            {
                logger.LogError("Could not create the Super Admin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
        }
        await userManager.AddToRoleAsync(admin, Roles.SuperAdmin);
        logger.LogWarning("Super Admin {Email} created with the seed password. Change it after the first sign-in.", seed.SuperAdminEmail);
    }

    private static async Task SeedLanguagesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.Languages.IgnoreQueryFilters().Select(l => l.IsoCode).ToListAsync(ct);
        var missing = SeedData.Languages().Where(l => !existing.Contains(l.IsoCode)).ToList();
        if (missing.Count == 0) return;
        db.Languages.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedSettingsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.SystemSettings.Select(s => s.Key).ToListAsync(ct);
        var missing = SeedData.Settings().Where(s => !existing.Contains(s.Key)).ToList();
        if (missing.Count == 0) return;
        db.SystemSettings.AddRange(missing);
        await db.SaveChangesAsync(ct);
    }
}
