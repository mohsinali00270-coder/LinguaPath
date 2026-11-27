using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinguaPath.Application.Features.Progress;

// ===================================================================== PDF rendering (Infrastructure implements it with QuestPDF)

/// <summary>Everything printed on a certificate.</summary>
public sealed record CertificateDocument(
    string Heading,
    string LearnerName,
    string Body,
    string LanguageName,
    string Level,
    string Band,
    int Grade,
    string CompletedOn,
    string Number,
    string VerifyUrl,
    string VerificationCode,
    string AccentColor,
    string? SignatoryName,
    string? SignatoryTitle,
    byte[]? Logo,
    byte[]? Signature,
    byte[]? Background,
    /// <summary>Revoked certificates are still rendered for admins, crossed with "REVOKED".</summary>
    bool Revoked,
    /// <summary>Template previews carry a "SAMPLE" watermark.</summary>
    bool Sample);

public interface ICertificateRenderer
{
    byte[] Render(CertificateDocument document);
}

// ===================================================================== learner side

public interface ICertificateService
{
    /// <summary>Issues the certificate when BR-06 is met (FR-L80). Returns the certificate, existing or new; null when not eligible.</summary>
    Task<Certificate?> CheckAndIssueAsync(int learnerCourseId, CancellationToken ct = default);

    Task<Result<CertificateProgressDto>> ProgressAsync(Guid userId, int learnerCourseId, CancellationToken ct = default);
    Task<IReadOnlyList<CertificateDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<Result<CertificateDto>> GetAsync(Guid userId, int id, CancellationToken ct = default);
    Task<Result<CertificateFile>> PdfAsync(Guid userId, int id, CancellationToken ct = default);

    /// <summary>Public verification (FR-L82). Without the right code only validity is shown.</summary>
    Task<VerificationDto> VerifyAsync(string number, string? code, CancellationToken ct = default);
    Task<Result<CertificateFile>> PublicPdfAsync(string number, string code, CancellationToken ct = default);

    /// <summary>The public verification page as a small self-contained HTML document.</summary>
    string VerificationHtml(VerificationDto dto, string? code);
}

public sealed record CertificateFile(byte[] Content, string FileName);

// ===================================================================== admin side (FR-A82, FR-A83)

public record CertificateFilter(string? Search, int? LanguageId, bool? Revoked, int Page = 1);

public record CertificateListItem(
    int Id, string Number, string LearnerName, string Email, Guid UserId, string Language, string Course, CefrLevel Level, int Grade, GradeBand Band,
    DateTimeOffset IssuedAt, bool IsRevoked, int Version);

public record CertificateDetails(
    CertificateListItem Summary, DateOnly StartedOn, DateOnly CompletedOn, string VerificationCode, string VerifyUrl, string? Template,
    DateTimeOffset? ReissuedAt, DateTimeOffset? RevokedAt, string? RevokedReason, string? RevokedBy, int LearnerCourseId);

public class CertificateTemplateInput
{
    public int Id { get; set; }
    [System.ComponentModel.DataAnnotations.Display(Name = "Language (empty = default for all)")]
    public int? LanguageId { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(150)]
    public string Name { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(150)]
    public string Heading { get; set; } = "Certificate of Achievement";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(2000), System.ComponentModel.DataAnnotations.Display(Name = "Body text")]
    public string BodyText { get; set; } = CertificateService.DefaultBody;
    [System.ComponentModel.DataAnnotations.StringLength(150), System.ComponentModel.DataAnnotations.Display(Name = "Signatory name")]
    public string? SignatoryName { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(150), System.ComponentModel.DataAnnotations.Display(Name = "Signatory title")]
    public string? SignatoryTitle { get; set; }
    public string? SignatureUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? BackgroundUrl { get; set; }
    [System.ComponentModel.DataAnnotations.RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "Use a colour like #4F46E5."), System.ComponentModel.DataAnnotations.Display(Name = "Accent colour")]
    public string AccentColor { get; set; } = "#4F46E5";
    [System.ComponentModel.DataAnnotations.Display(Name = "Active")]
    public bool IsActive { get; set; } = true;
}

public record CertificateTemplateListItem(int Id, string Name, string? Language, bool IsActive, DateTimeOffset? UpdatedAt);

public interface ICertificateAdminService
{
    Task<IReadOnlyList<CertificateListItem>> ListAsync(CertificateFilter filter, CancellationToken ct = default);
    Task<CertificateDetails?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CertificateListItem>> ForLearnerAsync(Guid userId, CancellationToken ct = default);
    Task<byte[]?> PdfAsync(int id, CancellationToken ct = default);

    /// <summary>FR-A83. Optionally takes back the certificate's points (e.g. for cheating).</summary>
    Task<Result> RevokeAsync(int id, string reason, bool revokePoints, CancellationToken ct = default);

    /// <summary>FR-A83: recompute grade, level and name from current data, apply the current template, restore if revoked. Same number.</summary>
    Task<Result> ReissueAsync(int id, CancellationToken ct = default);

    Task<Result<CertificateProgressDto>> EligibilityAsync(int learnerCourseId, CancellationToken ct = default);
    Task<Result<int>> IssueIfEligibleAsync(int learnerCourseId, CancellationToken ct = default);
    Task<int> IssuedThisMonthAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CertificateTemplateListItem>> TemplatesAsync(CancellationToken ct = default);
    Task<CertificateTemplateInput?> GetTemplateAsync(int id, CancellationToken ct = default);
    Task<Result<int>> SaveTemplateAsync(CertificateTemplateInput input, CancellationToken ct = default);
    Task<Result> DeleteTemplateAsync(int id, CancellationToken ct = default);
    Task<byte[]> PreviewTemplateAsync(CertificateTemplateInput input, CancellationToken ct = default);
}

/// <summary>Public certificate defaults, so the database seeder can use them without the internal service.</summary>
public static class CertificateDefaults
{
    public const string Body =
        "This certifies that {Name} has successfully completed the {Course} course in {Language} and reached level {Level} " +
        "with a final grade of {Grade}% ({Band}). Study period: {Duration}, completed on {CompletedOn}.";
}

internal sealed class CertificateService(
    IApplicationDbContext db,
    ISettingsService settings,
    ICertificateRenderer renderer,
    IFileStorage storage,
    IEmailSender email,
    Notifications.INotificationService notifications,
    IPointsService points,
    ICurrentUser currentUser,
    IAuditLogger audit,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<CertificateService> logger) : ICertificateService, ICertificateAdminService
{
    public const string DefaultBody = CertificateDefaults.Body;

    private const int MaxImageBytes = 2 * 1024 * 1024;

    // ================================================================== eligibility (BR-06)

    private sealed record Snapshot(LearnerCourse Course, string Iso, string LanguageName, CertificateRules.Facts Facts, CertificateProgressDto Progress);

    private async Task<Snapshot?> SnapshotAsync(int learnerCourseId, CancellationToken ct)
    {
        var lc = await db.LearnerCourses.Include(c => c.Course).Include(c => c.User)
            .FirstOrDefaultAsync(c => c.Id == learnerCourseId, ct);
        if (lc is null) return null;
        var language = await db.Languages.AsNoTracking().Where(l => l.Id == lc.LanguageId).Select(l => new { l.IsoCode, l.Name }).FirstAsync(ct);

        var core = await db.LearnerRoadmapItems.AsNoTracking().Where(i => i.LearnerCourseId == lc.Id && i.Lecture.IsCore)
            .Select(i => i.State).ToListAsync(ct);
        var examIds = await db.Quizzes.AsNoTracking()
            .Where(q => q.CourseId == lc.CourseId && q.Type == QuizType.Monthly && q.Status == ContentStatus.Published)
            .Select(q => q.Id).ToListAsync(ct);

        var attempts = await db.QuizAttempts.AsNoTracking()
            .Where(a => a.LearnerCourseId == lc.Id && a.Status == AttemptStatus.Graded && (a.Type == QuizType.Daily || a.Type == QuizType.Monthly))
            .Select(a => new { a.Type, a.QuizId, a.Percentage, a.IsPassed, a.IsPractice }).ToListAsync(ct);
        var passed = attempts.Where(a => a.Type == QuizType.Monthly && a.IsPassed && a.QuizId != null && examIds.Contains(a.QuizId.Value))
            .Select(a => a.QuizId!.Value).Distinct().Count();

        // BR-05 the same way the marks screen does: daily quizzes that count, best attempt per monthly exam.
        var daily = attempts.Where(a => a.Type == QuizType.Daily && !a.IsPractice).Select(a => a.Percentage).ToList();
        var bestExams = attempts.Where(a => a.Type == QuizType.Monthly).GroupBy(a => a.QuizId).Select(g => g.Max(a => a.Percentage)).ToList();
        int? dailyAvg = daily.Count == 0 ? null : (int)Math.Round(daily.Average());
        int? examAvg = bestExams.Count == 0 ? null : (int)Math.Round(bestExams.Average());
        var grade = GradeRules.CourseGrade(dailyAvg, examAvg,
            await settings.GetIntAsync(SettingKeys.CourseGradeQuizWeight, 30, ct), await settings.GetIntAsync(SettingKeys.CourseGradeExamWeight, 70, ct));
        var minGrade = await settings.GetIntAsync(SettingKeys.CertificateMinGrade, 60, ct);
        var standing = lc.User.IsActive && !lc.User.IsBlocked && !lc.User.IsDeleted;

        var facts = new CertificateRules.Facts(core.Count(s => s == RoadmapItemState.Completed), core.Count, passed, examIds.Count, grade, minGrade, standing);
        var existing = await db.Certificates.AsNoTracking().Where(c => c.LearnerCourseId == lc.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct);

        var checklist = new List<EligibilityItemDto>
        {
            new("Finish every core lecture", facts.CoreTotal > 0 && facts.CoreDone >= facts.CoreTotal, $"{facts.CoreDone} of {facts.CoreTotal}"),
            new("Pass every monthly exam", facts.ExamsPassed >= facts.ExamsTotal, $"{facts.ExamsPassed} of {facts.ExamsTotal}"),
            new($"Course grade of {minGrade}% or more", grade >= minGrade, grade is null ? "No marks yet" : $"{grade}%")
        };
        if (!standing) checklist.Add(new EligibilityItemDto("Account in good standing", false, "Contact support"));

        var progress = new CertificateProgressDto(lc.Id, lc.Course.Title, CertificateRules.Eligible(facts), existing, facts.CoreDone, facts.CoreTotal,
            facts.ExamsPassed, facts.ExamsTotal, grade, minGrade, checklist);
        return new Snapshot(lc, language.IsoCode, language.Name, facts, progress);
    }

    public async Task<Result<CertificateProgressDto>> ProgressAsync(Guid userId, int learnerCourseId, CancellationToken ct = default)
    {
        if (!await db.LearnerCourses.AnyAsync(c => c.Id == learnerCourseId && c.UserId == userId, ct)) return Error.NotFound("Course");
        var snapshot = await SnapshotAsync(learnerCourseId, ct);
        return snapshot!.Progress;
    }

    // ================================================================== issuing (FR-L80, BR-07, BR-08, SRS 6 numbering)

    public async Task<Certificate?> CheckAndIssueAsync(int learnerCourseId, CancellationToken ct = default)
    {
        var existing = await db.Certificates.FirstOrDefaultAsync(c => c.LearnerCourseId == learnerCourseId, ct);
        if (existing is not null) return existing.IsRevoked ? null : existing;

        var s = await SnapshotAsync(learnerCourseId, ct);
        if (s is null || !CertificateRules.Eligible(s.Facts)) return null;

        var lc = s.Course;
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, PointsService.Zone(lc.User.TimeZone)).DateTime);
        var band = CertificateRules.Band(s.Facts.CourseGrade!.Value, await settings.GetIntAsync(SettingKeys.GradeBandDistinction, 90, ct),
            await settings.GetIntAsync(SettingKeys.GradeBandMerit, 75, ct));
        var prefix = (await settings.GetAsync(SettingKeys.CertificateNumberPrefix, ct)) is { Length: > 0 } p ? p.Trim().ToUpperInvariant() : "LP";
        var templateId = await TemplateIdAsync(lc.LanguageId, ct);

        Certificate? certificate = null;
        for (var attempt = 0; attempt < 5 && certificate is null; attempt++)
        {
            var numberPrefix = CertificateRules.NumberPrefix(prefix, s.Iso, now.Year);
            var numbers = await db.Certificates.AsNoTracking().Where(c => c.CertificateNo.StartsWith(numberPrefix))
                .Select(c => c.CertificateNo).ToListAsync(ct);
            var next = numbers.Select(n => CertificateRules.SequenceOf(n, numberPrefix)).DefaultIfEmpty(0).Max() + 1 + attempt;

            var candidate = new Certificate
            {
                CertificateNo = CertificateRules.Number(prefix, s.Iso, now.Year, next), UserId = lc.UserId, LearnerCourseId = lc.Id,
                LanguageId = lc.LanguageId, TemplateId = templateId, LearnerName = Clip(lc.User.FullName, 200), CourseTitle = Clip(lc.Course.Title, 200),
                LevelAchieved = lc.TargetLevel, FinalGrade = s.Facts.CourseGrade.Value, GradeBand = band, StartedOn = lc.StartDate,
                CompletedOn = today, IssuedAt = now, VerificationCode = CertificateRules.NewVerificationCode()
            };
            db.Certificates.Add(candidate);
            try
            {
                await db.SaveChangesAsync(ct);
                certificate = candidate;
            }
            catch (DbUpdateException)
            {
                db.Certificates.Remove(candidate);
                // Another request issued this course's certificate, or took the number: look again.
                var raced = await db.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.LearnerCourseId == learnerCourseId, ct);
                if (raced is not null) return raced.IsRevoked ? null : raced;
            }
        }
        if (certificate is null) throw new InvalidOperationException("Couldn't allocate a certificate number.");

        lc.Status = LearnerCourseStatus.Completed;
        lc.CompletedAt = now;
        lc.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await points.AwardAsync(lc.UserId, lc.LanguageId, PointAction.CertificateEarned, "Certificate", certificate.Id.ToString(), ct: ct);
        await points.GrantBadgeAsync(lc.UserId, BadgeKey.FirstCertificate, ct);
        await NotifyAsync(lc.User, certificate, s.LanguageName, ct);
        logger.LogInformation("Certificate {Number} issued for learner course {LearnerCourseId}", certificate.CertificateNo, lc.Id);
        return certificate;
    }

    private async Task NotifyAsync(ApplicationUser user, Certificate c, string language, CancellationToken ct)
    {
        if (user.IsDeleted) return;
        try
        {
            // Phase 10: inbox entry always, push when the learner keeps certificate alerts on (FR-L110, FR-L112).
            await notifications.NotifyAsync([c.UserId], LinguaPath.Shared.NotificationType.Certificate, "Certificate earned 🎓",
                $"Your {language} {c.LevelAchieved} certificate is ready.",
                new Dictionary<string, string> { ["type"] = "certificate", ["certificateId"] = c.Id.ToString() },
                $"certificate:{c.Id}", ct);

            if (!user.NotifyCertificates || string.IsNullOrWhiteSpace(user.Email)) return;
            var url = VerifyUrl(c);
            var body = $"""
                <p>Congratulations, {WebUtility.HtmlEncode(c.LearnerName)}!</p>
                <p>You have earned your <b>{WebUtility.HtmlEncode(language)} {c.LevelAchieved}</b> certificate with a final grade of
                <b>{c.FinalGrade}% ({c.GradeBand})</b>.</p>
                <p>Certificate number: <b>{c.CertificateNo}</b><br/>Download it in the app under Profile → My certificates.</p>
                <p>Anyone can check it here: <a href="{WebUtility.HtmlEncode(url)}">{WebUtility.HtmlEncode(url)}</a></p>
                """;
            await email.SendAsync(user.Email, $"Your {language} certificate is ready", body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The certificate is issued; the email is a courtesy.
            logger.LogWarning(ex, "Certificate email failed for {Number}", c.CertificateNo);
        }
    }

    // ================================================================== learner views and files

    public async Task<IReadOnlyList<CertificateDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Certificates.AsNoTracking().Include(c => c.Language).Where(c => c.UserId == userId)
            .OrderByDescending(c => c.IssuedAt).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<CertificateDto>> GetAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var c = await db.Certificates.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        return c is null ? Error.NotFound("Certificate") : ToDto(c);
    }

    public async Task<Result<CertificateFile>> PdfAsync(Guid userId, int id, CancellationToken ct = default)
    {
        var c = await db.Certificates.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (c is null) return Error.NotFound("Certificate");
        if (c.IsRevoked) return Error.Conflict("certificate_revoked", "This certificate has been revoked.");
        return new CertificateFile(await RenderAsync(c, ct), FileName(c));
    }

    public async Task<VerificationDto> VerifyAsync(string number, string? code, CancellationToken ct = default)
    {
        var no = (number ?? "").Trim().ToUpperInvariant();
        var c = no.Length is 0 or > 40 ? null
            : await db.Certificates.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.CertificateNo == no, ct);
        if (c is null) return new VerificationDto(no, false, false, false, false, null, null, null, null, null, null, null, null);

        var detailed = CodeMatches(c.VerificationCode, code);
        return detailed
            ? new VerificationDto(c.CertificateNo, true, !c.IsRevoked, c.IsRevoked, true, c.LearnerName, c.Language.Name, c.CourseTitle,
                c.LevelAchieved, c.GradeBand, c.FinalGrade, c.CompletedOn, c.IssuedAt)
            : new VerificationDto(c.CertificateNo, true, !c.IsRevoked, c.IsRevoked, false, null, null, null, null, null, null, null, null);
    }

    public async Task<Result<CertificateFile>> PublicPdfAsync(string number, string code, CancellationToken ct = default)
    {
        var no = (number ?? "").Trim().ToUpperInvariant();
        var c = await db.Certificates.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.CertificateNo == no, ct);
        if (c is null || !CodeMatches(c.VerificationCode, code)) return Error.NotFound("Certificate");
        if (c.IsRevoked) return Error.Conflict("certificate_revoked", "This certificate has been revoked.");
        return new CertificateFile(await RenderAsync(c, ct), FileName(c));
    }

    public string VerificationHtml(VerificationDto v, string? code)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var appName = E(app.Value.Name);
        string status, colour;
        if (!v.Found) (status, colour) = ("No certificate with this number was found.", "#B91C1C");
        else if (v.Revoked) (status, colour) = ("This certificate has been revoked and is no longer valid.", "#B91C1C");
        else (status, colour) = ("Valid", "#15803D");

        var details = new StringBuilder();
        if (v is { Found: true, Detailed: true })
        {
            details.Append("<table>");
            void Row(string label, string? value) => details.Append($"<tr><th>{label}</th><td>{E(value)}</td></tr>");
            Row("Name", v.LearnerName);
            Row("Language", v.LanguageName);
            Row("Course", v.CourseTitle);
            Row("Level", v.Level?.ToString());
            Row("Grade", v.FinalGrade is { } g ? $"{g}% ({v.Band})" : null);
            Row("Completed", v.CompletedOn?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            Row("Issued", v.IssuedAt?.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            details.Append("</table>");
            if (!v.Revoked)
                details.Append($"<p><a class=\"btn\" href=\"/verify/{E(v.Number)}/pdf?c={E(code)}\">Download PDF</a></p>");
        }
        else if (v.Found)
        {
            details.Append("""
                <form method="get"><p>Enter the verification code printed on the certificate to see the details.</p>
                <input name="c" maxlength="20" autocomplete="off" placeholder="Verification code" required />
                <button type="submit">Check</button></form>
                """);
            if (!string.IsNullOrEmpty(code)) details.Append("<p class=\"warn\">That code doesn't match this certificate.</p>");
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex"><title>Certificate {{E(v.Number)}} · {{appName}}</title>
            <style>
              body{font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:#F5F5F7;color:#111827;margin:0;padding:24px}
              main{max-width:560px;margin:40px auto;background:#fff;border-radius:16px;padding:28px;box-shadow:0 1px 3px rgba(0,0,0,.08)}
              h1{font-size:18px;margin:0 0 4px} .no{color:#6B7280;font-family:ui-monospace,monospace}
              .status{font-size:22px;font-weight:700;margin:18px 0;color:{{colour}}}
              table{width:100%;border-collapse:collapse} th{text-align:left;color:#6B7280;font-weight:500;padding:6px 12px 6px 0;width:35%} td{padding:6px 0}
              input{padding:10px;border:1px solid #D1D5DB;border-radius:8px;font-size:16px;text-transform:uppercase}
              button,.btn{display:inline-block;padding:10px 16px;border-radius:8px;background:#4F46E5;color:#fff;border:0;text-decoration:none;font-size:15px}
              .warn{color:#B45309} footer{margin-top:24px;color:#9CA3AF;font-size:12px}
            </style></head>
            <body><main>
            <h1>{{appName}} certificate check</h1><div class="no">{{E(v.Number)}}</div>
            <div class="status">{{E(status)}}</div>
            {{details}}
            <footer>Certificates are issued automatically when a learner completes every core lecture, passes every monthly exam and reaches the minimum course grade.</footer>
            </main></body></html>
            """;
    }

    // ================================================================== admin

    public async Task<IReadOnlyList<CertificateListItem>> ListAsync(CertificateFilter filter, CancellationToken ct = default)
    {
        var q = db.Certificates.AsNoTracking().AsQueryable();
        if (filter.LanguageId is { } l) q = q.Where(c => c.LanguageId == l);
        if (filter.Revoked is { } r) q = q.Where(c => c.IsRevoked == r);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(c => c.CertificateNo.Contains(s) || c.LearnerName.Contains(s) || (c.User.Email != null && c.User.Email.Contains(s)));
        }
        return await Project(q.OrderByDescending(c => c.IssuedAt).Skip((Math.Max(1, filter.Page) - 1) * 100).Take(100)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CertificateListItem>> ForLearnerAsync(Guid userId, CancellationToken ct = default) =>
        await Project(db.Certificates.AsNoTracking().Where(c => c.UserId == userId).OrderByDescending(c => c.IssuedAt)).ToListAsync(ct);

    private static IQueryable<CertificateListItem> Project(IQueryable<Certificate> q) =>
        q.Select(c => new CertificateListItem(c.Id, c.CertificateNo, c.LearnerName, c.User.Email ?? "", c.UserId, c.Language.Name, c.CourseTitle,
            c.LevelAchieved, c.FinalGrade, c.GradeBand, c.IssuedAt, c.IsRevoked, c.Version));

    public async Task<CertificateDetails?> GetAsync(int id, CancellationToken ct = default)
    {
        var summary = await Project(db.Certificates.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct);
        if (summary is null) return null;
        var c = await db.Certificates.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var template = c.TemplateId is null ? null
            : await db.CertificateTemplates.IgnoreQueryFilters().Where(t => t.Id == c.TemplateId).Select(t => t.Name).FirstOrDefaultAsync(ct);
        return new CertificateDetails(summary, c.StartedOn, c.CompletedOn, c.VerificationCode, VerifyUrl(c), template, c.ReissuedAt, c.RevokedAt,
            c.RevokedReason, c.RevokedByEmail, c.LearnerCourseId);
    }

    public async Task<byte[]?> PdfAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Certificates.AsNoTracking().Include(x => x.Language).FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : await RenderAsync(c, ct);
    }

    public async Task<Result> RevokeAsync(int id, string reason, bool revokePoints, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) return Error.Validation("Reason", "A reason is required.");
        var c = await db.Certificates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Error.NotFound("Certificate");
        if (c.IsRevoked) return Error.Conflict("already_revoked", "This certificate is already revoked.");
        c.IsRevoked = true;
        c.RevokedAt = clock.GetUtcNow();
        c.RevokedReason = reason.Trim();
        c.RevokedByEmail = currentUser.Email;
        await db.SaveChangesAsync(ct);

        if (revokePoints)
        {
            var key = PointMath.Key(PointAction.CertificateEarned, "Certificate", c.Id.ToString());
            var tx = await db.PointTransactions.FirstOrDefaultAsync(t => t.UserId == c.UserId && t.ReferenceKey == key && !t.IsRevoked, ct);
            if (tx is not null)
            {
                tx.IsRevoked = true;
                tx.RevokedAt = clock.GetUtcNow();
                tx.RevokedReason = "Certificate revoked: " + reason.Trim();
                await db.SaveChangesAsync(ct);
                await points.RecalculateAsync(c.UserId, ct);
            }
        }
        await audit.LogAsync("Revoke", "Certificate", c.CertificateNo, null, new { reason, revokePoints }, ct);
        return Result.Success();
    }

    public async Task<Result> ReissueAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Certificates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Error.NotFound("Certificate");
        var s = await SnapshotAsync(c.LearnerCourseId, ct);
        if (s is null) return Error.NotFound("Learner course");
        if (s.Facts.CourseGrade is not { } grade) return Error.Conflict("no_grade", "The course has no marks to put on the certificate.");

        var old = new { c.LearnerName, c.FinalGrade, Band = c.GradeBand.ToString(), c.LevelAchieved, c.IsRevoked, c.Version };
        c.LearnerName = Clip(s.Course.User.FullName, 200);
        c.CourseTitle = Clip(s.Course.Course.Title, 200);
        c.LevelAchieved = s.Course.TargetLevel;
        c.FinalGrade = grade;
        c.GradeBand = CertificateRules.Band(grade, await settings.GetIntAsync(SettingKeys.GradeBandDistinction, 90, ct),
            await settings.GetIntAsync(SettingKeys.GradeBandMerit, 75, ct));
        c.TemplateId = await TemplateIdAsync(c.LanguageId, ct);
        c.Version++;
        c.ReissuedAt = clock.GetUtcNow();
        c.IsRevoked = false;
        c.RevokedAt = null;
        c.RevokedReason = null;
        c.RevokedByEmail = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Reissue", "Certificate", c.CertificateNo, old,
            new { c.LearnerName, c.FinalGrade, Band = c.GradeBand.ToString(), c.LevelAchieved, c.Version }, ct);
        return Result.Success();
    }

    public async Task<Result<CertificateProgressDto>> EligibilityAsync(int learnerCourseId, CancellationToken ct = default)
    {
        var s = await SnapshotAsync(learnerCourseId, ct);
        return s is null ? Error.NotFound("Learner course") : s.Progress;
    }

    public async Task<Result<int>> IssueIfEligibleAsync(int learnerCourseId, CancellationToken ct = default)
    {
        var revoked = await db.Certificates.AnyAsync(c => c.LearnerCourseId == learnerCourseId && c.IsRevoked, ct);
        if (revoked) return Error.Conflict("certificate_revoked", "This course's certificate was revoked. Reissue it from the certificate page.");
        var c = await CheckAndIssueAsync(learnerCourseId, ct);
        if (c is null) return Error.Conflict("not_eligible", "The learner doesn't meet the certificate rule yet.");
        await audit.LogAsync("Issue", "Certificate", c.CertificateNo, null, new { learnerCourseId }, ct);
        return c.Id;
    }

    public Task<int> IssuedThisMonthAsync(CancellationToken ct = default)
    {
        var start = PointMath.MonthStart(clock.GetUtcNow());
        return db.Certificates.CountAsync(c => c.IssuedAt >= start && !c.IsRevoked, ct);
    }

    // ------------------------------------------------------------------ templates (FR-A82)

    public async Task<IReadOnlyList<CertificateTemplateListItem>> TemplatesAsync(CancellationToken ct = default) =>
        await db.CertificateTemplates.AsNoTracking().OrderBy(t => t.LanguageId == null ? 0 : 1).ThenBy(t => t.Name)
            .Select(t => new CertificateTemplateListItem(t.Id, t.Name, t.Language != null ? t.Language.Name : null, t.IsActive, t.UpdatedAt ?? t.CreatedAt))
            .ToListAsync(ct);

    public async Task<CertificateTemplateInput?> GetTemplateAsync(int id, CancellationToken ct = default)
    {
        var t = await db.CertificateTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? null : new CertificateTemplateInput
        {
            Id = t.Id, LanguageId = t.LanguageId, Name = t.Name, Heading = t.Heading, BodyText = t.BodyText, SignatoryName = t.SignatoryName,
            SignatoryTitle = t.SignatoryTitle, SignatureUrl = t.SignatureUrl, LogoUrl = t.LogoUrl, BackgroundUrl = t.BackgroundUrl,
            AccentColor = t.AccentColor, IsActive = t.IsActive
        };
    }

    public async Task<Result<int>> SaveTemplateAsync(CertificateTemplateInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return Error.Validation(nameof(input.Name), "Enter a name.");
        if (!input.BodyText.Contains("{Name}", StringComparison.OrdinalIgnoreCase))
            return Error.Validation(nameof(input.BodyText), "The body text must include {Name}.");
        if (input.LanguageId is { } l && !await db.Languages.AnyAsync(x => x.Id == l, ct))
            return Error.Validation(nameof(input.LanguageId), "Unknown language.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(input.AccentColor ?? "", "^#[0-9A-Fa-f]{6}$"))
            return Error.Validation(nameof(input.AccentColor), "Use a colour like #4F46E5.");

        CertificateTemplate t;
        if (input.Id == 0)
        {
            t = new CertificateTemplate();
            db.CertificateTemplates.Add(t);
        }
        else
        {
            t = await db.CertificateTemplates.FirstOrDefaultAsync(x => x.Id == input.Id, ct) ?? throw new InvalidOperationException();
        }
        t.LanguageId = input.LanguageId;
        t.Name = input.Name.Trim();
        t.Heading = input.Heading.Trim();
        t.BodyText = input.BodyText.Trim();
        t.SignatoryName = Blank(input.SignatoryName);
        t.SignatoryTitle = Blank(input.SignatoryTitle);
        t.SignatureUrl = Blank(input.SignatureUrl);
        t.LogoUrl = Blank(input.LogoUrl);
        t.BackgroundUrl = Blank(input.BackgroundUrl);
        t.AccentColor = input.AccentColor!.ToUpperInvariant();
        t.IsActive = input.IsActive;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(input.Id == 0 ? "Create" : "Update", "CertificateTemplate", t.Id.ToString(), null, new { t.Name, t.LanguageId, t.IsActive }, ct);
        return t.Id;
    }

    public async Task<Result> DeleteTemplateAsync(int id, CancellationToken ct = default)
    {
        var t = await db.CertificateTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Error.NotFound("Template");
        if (t.LanguageId is null && !await db.CertificateTemplates.AnyAsync(x => x.Id != id && x.LanguageId == null && x.IsActive, ct))
            return Error.Conflict("last_default", "Keep at least one active default template.");
        t.IsDeleted = true;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Delete", "CertificateTemplate", id.ToString(), new { t.Name }, null, ct);
        return Result.Success();
    }

    public async Task<byte[]> PreviewTemplateAsync(CertificateTemplateInput input, CancellationToken ct = default)
    {
        var language = input.LanguageId is { } l ? await db.Languages.Where(x => x.Id == l).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var t = new CertificateTemplate
        {
            Heading = input.Heading, BodyText = input.BodyText, SignatoryName = input.SignatoryName, SignatoryTitle = input.SignatoryTitle,
            SignatureUrl = input.SignatureUrl, LogoUrl = input.LogoUrl, BackgroundUrl = input.BackgroundUrl, AccentColor = input.AccentColor
        };
        var sample = new Certificate
        {
            CertificateNo = "LP-XX-0000-000000", LearnerName = "Alex Sample", CourseTitle = "Everyday Conversation", LevelAchieved = CefrLevel.B1,
            FinalGrade = 86, GradeBand = GradeBand.Merit, StartedOn = new DateOnly(2026, 1, 5), CompletedOn = new DateOnly(2026, 7, 2),
            VerificationCode = "SAMPLECODE", IssuedAt = clock.GetUtcNow(), Language = new Language { Name = language ?? "Spanish" }
        };
        return renderer.Render(await DocumentAsync(sample, t, sample: true, ct));
    }

    // ------------------------------------------------------------------ rendering helpers

    private async Task<int?> TemplateIdAsync(int languageId, CancellationToken ct) =>
        await db.CertificateTemplates.Where(t => t.IsActive && t.LanguageId == languageId).OrderByDescending(t => t.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
        ?? await db.CertificateTemplates.Where(t => t.IsActive && t.LanguageId == null).OrderByDescending(t => t.Id).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);

    private async Task<byte[]> RenderAsync(Certificate c, CancellationToken ct)
    {
        // The template chosen at issue; if it was deleted since, the current one for the language.
        var template = (c.TemplateId is { } tid ? await db.CertificateTemplates.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, ct) : null)
                       ?? (await TemplateIdAsync(c.LanguageId, ct) is { } fallback ? await db.CertificateTemplates.AsNoTracking().FirstAsync(t => t.Id == fallback, ct) : null)
                       ?? new CertificateTemplate { BodyText = DefaultBody };
        return renderer.Render(await DocumentAsync(c, template, sample: false, ct));
    }

    private async Task<CertificateDocument> DocumentAsync(Certificate c, CertificateTemplate t, bool sample, CancellationToken ct)
    {
        var values = new Dictionary<string, string>
        {
            ["Name"] = c.LearnerName, ["Language"] = c.Language?.Name ?? "", ["Course"] = c.CourseTitle, ["Level"] = c.LevelAchieved.ToString(),
            ["Grade"] = c.FinalGrade.ToString(CultureInfo.InvariantCulture), ["Band"] = c.GradeBand.ToString(),
            ["Duration"] = CertificateRules.Duration(c.StartedOn, c.CompletedOn),
            ["CompletedOn"] = c.CompletedOn.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), ["Number"] = c.CertificateNo
        };
        return new CertificateDocument(t.Heading, c.LearnerName, CertificateRules.Fill(t.BodyText, values), c.Language?.Name ?? "",
            c.LevelAchieved.ToString(), c.GradeBand.ToString(), c.FinalGrade, values["CompletedOn"], c.CertificateNo, VerifyUrl(c),
            c.VerificationCode, t.AccentColor, t.SignatoryName, t.SignatoryTitle, await ImageAsync(t.LogoUrl, ct), await ImageAsync(t.SignatureUrl, ct),
            await ImageAsync(t.BackgroundUrl, ct), c.IsRevoked, sample);
    }

    private async Task<byte[]?> ImageAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return await storage.ReadAsync(url, MaxImageBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Certificate image {Url} couldn't be loaded", url);
            return null;
        }
    }

    private string VerifyUrl(Certificate c) =>
        $"{app.Value.PublicBaseUrl.TrimEnd('/')}/verify/{Uri.EscapeDataString(c.CertificateNo)}?c={Uri.EscapeDataString(c.VerificationCode)}";

    private CertificateDto ToDto(Certificate c) => new(c.Id, c.CertificateNo, c.LearnerCourseId, c.LearnerName, c.Language.Name, c.CourseTitle,
        c.LevelAchieved, c.FinalGrade, c.GradeBand, c.StartedOn, c.CompletedOn, CertificateRules.Duration(c.StartedOn, c.CompletedOn), c.IssuedAt,
        c.IsRevoked, VerifyUrl(c));

    private static string FileName(Certificate c) => $"{c.CertificateNo}.pdf";

    private static bool CodeMatches(string expected, string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return false;
        var a = Encoding.UTF8.GetBytes(expected.ToUpperInvariant());
        var b = Encoding.UTF8.GetBytes(given.Trim().ToUpperInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
