using System.Net;
using System.Text.RegularExpressions;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Progress;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Certificates;
using LinguaPath.Shared.Contracts.Content;
using LinguaPath.Shared.Contracts.Learning;
using LinguaPath.Shared.Contracts.Practice;
using LinguaPath.Shared.Contracts.Progress;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 7 acceptance: points wallet, streak, badges, leaderboards, certificates and public verification.</summary>
public sealed class ProgressTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Seeded(int LanguageId, string Iso, int CourseId, int LectureId, int ExamId, int ListenId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "q" + string.Concat(Guid.NewGuid().ToByteArray().Take(5).Select(b => (char)('a' + b % 26)));
        var language = new Language { Name = "Lang " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published, MaxLevel = CefrLevel.A2 };
        var course = new Course { Language = language, Title = "Certificate course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, Status = ContentStatus.Published };
        var module = new CourseModule { Title = "Month one", Level = CefrLevel.A1, MonthNo = 1, SortOrder = 1 };
        var lecture = new Lecture { Title = "Only lecture", EstimatedMinutes = 1, IsCore = true, SortOrder = 1, Level = CefrLevel.A1, Status = ContentStatus.Published, BodyHtml = "<p>Hola</p>" };
        module.Lectures.Add(lecture);
        course.Modules.Add(module);
        db.Courses.Add(course);
        var exam = new Quiz { Course = course, Type = QuizType.Monthly, Title = "Month 1 exam", MonthNo = 1, Status = ContentStatus.Published };
        db.Quizzes.Add(exam);
        var listen = new PracticeItem
        {
            Language = language, Type = PracticeType.Listen, Level = CefrLevel.A1, Title = "Dictation", Text = "Hola amigo.", Status = ContentStatus.Published
        };
        db.PracticeItems.Add(listen);
        await db.SaveChangesAsync();
        return new Seeded(language.Id, iso, course.Id, lecture.Id, exam.Id, listen.Id);
    }

    private sealed record Learner(HttpClient Client, Guid UserId, int LearnerCourseId, string Name, string Email);

    private async Task<Learner> LearnerAsync(Seeded s, string? name = null)
    {
        var email = $"progress-{Guid.NewGuid():N}@test.local";
        name ??= "Points Learner " + email[9..13];
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest(name, email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        var authed = factory.CreateClient().WithBearer(auth.AccessToken);
        var plan = new LearningPlanRequest(s.CourseId, CefrLevel.A1, CefrLevel.A2, LearningGoal.Travel, null, 3, null, 10,
            StudyDays.Everyday, new TimeOnly(19, 0), FocusSkill.All);
        var enrolled = (await (await authed.PostJsonAsync("/api/v1/learner-courses", plan)).ReadDataAsync<EnrolResponse>()).Course;
        using var scope = factory.Services.CreateScope();
        var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return new Learner(authed, userId, enrolled.Id, name, email);
    }

    private static async Task<CompleteLectureResponse> CompleteLectureAsync(Learner l, int lectureId) =>
        await (await l.Client.PostJsonAsync($"/api/v1/lectures/{lectureId}/complete", new CompleteLectureRequest(l.LearnerCourseId, 120)))
            .ReadDataAsync<CompleteLectureResponse>();

    /// <summary>A graded, passed monthly exam (as if taken and marked), then the same hook the API runs after grading.</summary>
    private async Task<PointsEarnedDto> PassExamAsync(Learner l, Seeded s, int percent)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempt = new QuizAttempt
        {
            UserId = l.UserId, CourseId = s.CourseId, LearnerCourseId = l.LearnerCourseId, QuizId = s.ExamId, Type = QuizType.Monthly, Title = "Month 1 exam",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), SubmittedAt = DateTimeOffset.UtcNow, PassMark = 60, Status = AttemptStatus.Graded,
            IsPassed = percent >= 60, Percentage = percent, Score = percent, MaxScore = 100
        };
        db.QuizAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return await scope.ServiceProvider.GetRequiredService<IProgressService>().OnAttemptClosedAsync(attempt.Id);
    }

    private async Task SetAsync(string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(key, value)).IsSuccess);
    }

    // ------------------------------------------------------------------ points, streak, badges

    [Fact]
    public async Task Completing_a_lecture_gives_points_once_starts_the_streak_and_earns_the_first_badge()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);

        var done = await CompleteLectureAsync(l, s.LectureId);
        Assert.InRange(done.PointsEarned, 15, 30);   // 10 for the lecture + 5 streak day (+ any study-ahead bonus)
        Assert.Equal(1, done.Streak);
        Assert.Contains(done.NewBadges!, b => b.Contains("First lecture"));

        // Completing again pays nothing (FR-S05).
        var again = await CompleteLectureAsync(l, s.LectureId);
        Assert.Equal(0, again.PointsEarned);

        var summary = await (await l.Client.GetAsync("/api/v1/points")).ReadDataAsync<PointsSummaryDto>();
        Assert.Equal(done.PointsEarned, summary.Total);
        Assert.Equal(PointTier.Bronze, summary.Tier);
        Assert.Equal(1, summary.Streak.Current);
        Assert.True(summary.Streak.ActiveToday);
        Assert.Contains(summary.ByLanguage, x => x.LanguageId == s.LanguageId && x.Points >= 10);
        Assert.Contains(summary.ByLanguage, x => x.LanguageId == null && x.Points == 5);   // streak points are account-wide
        Assert.True(summary.Badges.Single(b => b.Key == BadgeKey.FirstLecture).Earned);
        Assert.False(summary.Badges.Single(b => b.Key == BadgeKey.Streak7).Earned);

        var history = await (await l.Client.GetAsync("/api/v1/points/history")).ReadDataAsync<List<PointTransactionDto>>();
        Assert.Contains(history, h => h.Action == PointAction.LectureCompleted && h.Points == 10);
        Assert.Contains(history, h => h.Action == PointAction.StreakDay && h.Points == 5);

        // Home shows the streak and points (FR-L21).
        var today = await (await l.Client.GetAsync($"/api/v1/learner-courses/{l.LearnerCourseId}/today")).ReadDataAsync<TodayDto>();
        Assert.Equal(1, today.StreakDays);
        Assert.Equal(summary.Total, today.Points);
    }

    [Fact]
    public async Task Daily_cap_and_per_action_limits_hold_and_admins_can_change_rules()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);

        // Practice: 5 points, 4 times a day (FR-S03).
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await l.Client.PostJsonAsync($"/api/v1/practice/listen/{s.ListenId}/check", new ListenCheckRequest("hola amigo"))).StatusCode);
        var afterPractice = await (await l.Client.GetAsync("/api/v1/points")).ReadDataAsync<PointsSummaryDto>();
        Assert.Equal(20, afterPractice.Total);

        // BR-22: the daily cap stops capped actions; exams still pay.
        await SetAsync(SettingKeys.PointsDailyCap, "22");
        try
        {
            var lecture = await CompleteLectureAsync(l, s.LectureId);
            Assert.Equal(2, lecture.PointsEarned);
            var exam = await PassExamAsync(l, s, 80);
            Assert.Equal(150, exam.Points);    // 100 + Merit 50, not capped
        }
        finally
        {
            await SetAsync(SettingKeys.PointsDailyCap, "500");
        }

        // FR-S03: a rule switched off pays nothing.
        using (var scope = factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IPointsAdminService>();
            Assert.True((await admin.UpdateRuleAsync(PointAction.CompetitionWin, 0, 0, false)).IsSuccess);
            Assert.True((await admin.UpdateRuleAsync(PointAction.PracticeSession, 5, 99, true)).IsSuccess);
            Assert.True((await admin.UpdateRuleAsync(PointAction.PracticeSession, 5, 4, true)).IsSuccess);
            Assert.True((await admin.UpdateRuleAsync(PointAction.CompetitionWin, 30, 0, true)).IsSuccess);
            Assert.True((await admin.UpdateRuleAsync(PointAction.PracticeSession, -1, 4, true)).IsFailure);
        }
    }

    [Fact]
    public async Task Exam_points_are_once_per_exam_and_distinction_doubles_the_bonus()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);

        var first = await PassExamAsync(l, s, 95);
        Assert.Equal(200, first.Points);   // 100 + Distinction (2 × 50)
        Assert.Contains(first.NewBadges, b => b.Key == BadgeKey.FirstExamPassed);
        var retake = await PassExamAsync(l, s, 99);
        Assert.Equal(0, retake.Points);

        using var scope = factory.Services.CreateScope();
        var progress = scope.ServiceProvider.GetRequiredService<IProgressService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attemptId = await db.QuizAttempts.Where(a => a.UserId == l.UserId).OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
        Assert.Equal(0, (await progress.OnAttemptClosedAsync(attemptId)).Points);   // a regrade doesn't pay twice
    }

    [Fact]
    public async Task Admin_adjustments_and_revokes_change_the_total_and_tier()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IPointsAdminService>();
        var points = scope.ServiceProvider.GetRequiredService<IPointsService>();

        Assert.True((await admin.AdjustAsync(l.UserId, 1200, "x", null)).IsFailure);   // reason too short
        var added = await admin.AdjustAsync(l.UserId, 1200, "Contest prize", null);
        Assert.True(added.IsSuccess);
        var summary = await points.SummaryAsync(l.UserId);
        Assert.Equal(1200, summary.Total);
        Assert.Equal(PointTier.Silver, summary.Tier);
        Assert.True(summary.Badges.Single(b => b.Key == BadgeKey.TierSilver).Earned);

        Assert.True((await admin.RevokeAsync(added.Value, "Given by mistake")).IsSuccess);
        Assert.True((await admin.RevokeAsync(added.Value, "Again")).IsFailure);
        summary = await points.SummaryAsync(l.UserId);
        Assert.Equal(0, summary.Total);
        Assert.Equal(PointTier.Bronze, summary.Tier);
        Assert.True(summary.Badges.Single(b => b.Key == BadgeKey.TierSilver).Earned);   // badges stay
        var history = await points.HistoryAsync(l.UserId, 1);
        Assert.True(Assert.Single(history).IsRevoked);
    }

    [Fact]
    public async Task Leaderboards_rank_by_period_and_language_and_hide_learners_who_opt_out()
    {
        var s = await SeedAsync();
        var top = await LearnerAsync(s, "Top Learner");
        var second = await LearnerAsync(s, "Second Learner");
        using (var scope = factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IPointsAdminService>();
            await admin.AdjustAsync(top.UserId, 90_000, "Leaderboard test", s.LanguageId);
            await admin.AdjustAsync(second.UserId, 80_000, "Leaderboard test", s.LanguageId);
        }

        var board = await (await second.Client.GetAsync($"/api/v1/leaderboard?period=Weekly&languageId={s.LanguageId}")).ReadDataAsync<LeaderboardDto>();
        Assert.Equal("Top Learner", board.Entries[0].DisplayName);
        Assert.Equal(1, board.Entries[0].Rank);
        Assert.Equal(PointTier.Diamond, board.Entries[0].Tier);
        Assert.True(board.Entries[1].IsMe);
        Assert.Equal(2, board.Me!.Rank);

        // FR-S07 / FR-S24: opting out hides the name from others, not from yourself.
        Assert.Equal(HttpStatusCode.OK, (await top.Client.PutAsync("/api/v1/me/leaderboard-visibility",
            System.Net.Http.Json.JsonContent.Create(new LeaderboardVisibilityRequest(false)))).StatusCode);
        board = await (await second.Client.GetAsync("/api/v1/leaderboard?period=AllTime")).ReadDataAsync<LeaderboardDto>();
        var hidden = board.Entries.First(e => e.Points == 90_000);
        Assert.Equal(PointsService.HiddenName, hidden.DisplayName);
        Assert.Null(hidden.PhotoUrl);
        var mine = await (await top.Client.GetAsync("/api/v1/leaderboard?period=AllTime")).ReadDataAsync<LeaderboardDto>();
        Assert.Equal("Top Learner", mine.Entries.First(e => e.IsMe).DisplayName);
        Assert.False((await (await top.Client.GetAsync("/api/v1/points")).ReadDataAsync<PointsSummaryDto>()).ShowOnLeaderboard);

        // Blocked learners leave the boards (FR-M14).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == top.UserId);
            user.IsBlocked = true;
            await db.SaveChangesAsync();
        }
        board = await (await second.Client.GetAsync($"/api/v1/leaderboard?period=Monthly&languageId={s.LanguageId}")).ReadDataAsync<LeaderboardDto>();
        Assert.True(board.Entries[0].IsMe);
    }

    // ------------------------------------------------------------------ certificates

    [Fact]
    public async Task Meeting_br06_issues_a_numbered_certificate_with_pdf_points_email_and_verification()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s, "Ana Certificada");

        var before = await (await l.Client.GetAsync($"/api/v1/learner-courses/{l.LearnerCourseId}/certificate-progress")).ReadDataAsync<CertificateProgressDto>();
        Assert.False(before.Eligible);
        Assert.Equal(1, before.CoreLecturesTotal);
        Assert.Equal(1, before.ExamsTotal);

        await PassExamAsync(l, s, 92);
        Assert.Empty(await (await l.Client.GetAsync("/api/v1/certificates")).ReadDataAsync<List<CertificateDto>>());
        await CompleteLectureAsync(l, s.LectureId);   // the last missing piece

        var certificate = Assert.Single(await (await l.Client.GetAsync("/api/v1/certificates")).ReadDataAsync<List<CertificateDto>>());
        Assert.Matches(new Regex(@"^LP-[A-Z]{1,3}-\d{4}-\d{6}$"), certificate.Number);
        Assert.Equal(GradeBand.Distinction, certificate.Band);
        Assert.Equal(92, certificate.FinalGrade);
        Assert.Equal(CefrLevel.A2, certificate.Level);
        Assert.Equal("Ana Certificada", certificate.LearnerName);
        Assert.Contains("/verify/", certificate.VerifyUrl);

        var after = await (await l.Client.GetAsync($"/api/v1/learner-courses/{l.LearnerCourseId}/certificate-progress")).ReadDataAsync<CertificateProgressDto>();
        Assert.Equal(certificate.Id, after.CertificateId);

        var wallet = await (await l.Client.GetAsync("/api/v1/points")).ReadDataAsync<PointsSummaryDto>();
        Assert.True(wallet.Badges.Single(b => b.Key == BadgeKey.FirstCertificate).Earned);
        var history = await (await l.Client.GetAsync("/api/v1/points/history")).ReadDataAsync<List<PointTransactionDto>>();
        Assert.Contains(history, h => h.Action == PointAction.CertificateEarned && h.Points == 500);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(LearnerCourseStatus.Completed, (await db.LearnerCourses.SingleAsync(c => c.Id == l.LearnerCourseId)).Status);
        }
        // FR-L80: the learner is told by email.
        Assert.Contains(factory.Emails.SubjectsFor(l.Email), subject => subject.Contains("certificate is ready"));

        // FR-L82: PDF download.
        var pdf = await l.Client.GetAsync($"/api/v1/certificates/{certificate.Id}/pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        // Public verification: validity without the code, details with it.
        var anonymous = factory.CreateClient();
        var code = new Uri(certificate.VerifyUrl).Query.Split("c=")[1];
        var page = await anonymous.GetAsync($"/verify/{certificate.Number}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Valid", html);
        Assert.DoesNotContain("Ana Certificada", html);
        var detailed = await (await anonymous.GetAsync($"/verify/{certificate.Number}?c={code}")).Content.ReadAsStringAsync();
        Assert.Contains("Ana Certificada", detailed);
        var json = await (await anonymous.GetAsync($"/api/v1/verify/{certificate.Number.ToLowerInvariant()}?c={code.ToLowerInvariant()}")).ReadDataAsync<VerificationDto>();
        Assert.True(json.Valid);
        Assert.True(json.Detailed);
        Assert.Equal(GradeBand.Distinction, json.Band);
        var wrong = await (await anonymous.GetAsync($"/api/v1/verify/{certificate.Number}?c=WRONGCODE1")).ReadDataAsync<VerificationDto>();
        Assert.False(wrong.Detailed);
        Assert.Null(wrong.LearnerName);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/verify/LP-XX-2000-999999")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/verify/{certificate.Number}/pdf?c={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/verify/{certificate.Number}/pdf?c=WRONGCODE1")).StatusCode);

        // Another learner in the same language gets the next number (never reused).
        var second = await LearnerAsync(s, "Second Graduate");
        await PassExamAsync(second, s, 70);
        await CompleteLectureAsync(second, s.LectureId);
        var next = Assert.Single(await (await second.Client.GetAsync("/api/v1/certificates")).ReadDataAsync<List<CertificateDto>>());
        // Same language and year: the next sequence number (numbers are never reused).
        static int Sequence(string number) => int.Parse(number[^6..]);
        Assert.Equal(next.Number[..^6], certificate.Number[..^6]);
        Assert.Equal(Sequence(certificate.Number) + 1, Sequence(next.Number));
        Assert.Equal(GradeBand.Pass, next.Band);

        // FR-A83: revoke (with points) → public page says revoked, PDF refused; reissue restores it with the same number.
        using (var scope = factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<ICertificateAdminService>();
            Assert.True((await admin.RevokeAsync(certificate.Id, "Exam misconduct", revokePoints: true)).IsSuccess);
        }
        var revoked = await (await anonymous.GetAsync($"/api/v1/verify/{certificate.Number}")).ReadDataAsync<VerificationDto>();
        Assert.True(revoked.Revoked);
        Assert.False(revoked.Valid);
        Assert.Contains("revoked", await (await anonymous.GetAsync($"/verify/{certificate.Number}")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await l.Client.GetAsync($"/api/v1/certificates/{certificate.Id}/pdf")).StatusCode);
        var revokedHistory = await (await l.Client.GetAsync("/api/v1/points/history")).ReadDataAsync<List<PointTransactionDto>>();
        Assert.True(revokedHistory.Single(h => h.Action == PointAction.CertificateEarned).IsRevoked);

        using (var scope = factory.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<ICertificateAdminService>();
            Assert.True((await admin.ReissueAsync(certificate.Id)).IsSuccess);
            var details = await admin.GetAsync(certificate.Id);
            Assert.False(details!.Summary.IsRevoked);
            Assert.Equal(2, details.Summary.Version);
            Assert.Equal(certificate.Number, details.Summary.Number);
            Assert.NotNull(await admin.PdfAsync(certificate.Id));
            Assert.True((await admin.IssueIfEligibleAsync(l.LearnerCourseId)).IsSuccess);   // already issued: returns it
            Assert.True(await admin.IssuedThisMonthAsync() >= 2);
        }
    }

    [Fact]
    public async Task Templates_need_the_name_placeholder_and_a_default_must_remain()
    {
        using var scope = factory.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<ICertificateAdminService>();

        Assert.True((await admin.SaveTemplateAsync(new CertificateTemplateInput { Name = "No name", BodyText = "Well done." })).IsFailure);
        Assert.True((await admin.SaveTemplateAsync(new CertificateTemplateInput { Name = "Bad colour", AccentColor = "blue" })).IsFailure);

        var saved = await admin.SaveTemplateAsync(new CertificateTemplateInput { Name = "Blue", AccentColor = "#1d4ed8" });
        Assert.True(saved.IsSuccess);
        Assert.Equal("#1D4ED8", (await admin.GetTemplateAsync(saved.Value))!.AccentColor);
        var pdf = await admin.PreviewTemplateAsync((await admin.GetTemplateAsync(saved.Value))!);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));

        // Deleting every default is refused; deleting one of two is fine.
        var defaults = (await admin.TemplatesAsync()).Where(t => t.Language is null && t.IsActive).ToList();
        foreach (var t in defaults.Skip(1)) Assert.True((await admin.DeleteTemplateAsync(t.Id)).IsSuccess);
        Assert.True((await admin.DeleteTemplateAsync(defaults[0].Id)).IsFailure);
    }
}
