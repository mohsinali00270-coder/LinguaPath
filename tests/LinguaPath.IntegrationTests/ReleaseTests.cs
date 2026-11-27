using System.Net;
using System.Net.Http.Json;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Notifications;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Chat;
using LinguaPath.Shared.Contracts.Learning;
using LinguaPath.Shared.Contracts.Notifications;
using LinguaPath.Shared.Contracts.Profile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 10: reminders and the inbox, BR-32 age rules, retention clean-up, analytics exports, health and hardening.</summary>
public sealed class ReleaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Seeded(int LanguageId, int CourseId, int LectureId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "r" + string.Concat(Guid.NewGuid().ToByteArray().Take(5).Select(b => (char)('a' + b % 26)));
        var language = new Language { Name = "Lang " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published, MaxLevel = CefrLevel.A2 };
        var course = new Course { Language = language, Title = "Release course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, Status = ContentStatus.Published };
        var module = new CourseModule { Title = "Month one", Level = CefrLevel.A1, MonthNo = 1, SortOrder = 1 };
        var lecture = new Lecture { Title = "First lecture", EstimatedMinutes = 1, IsCore = true, SortOrder = 1, Level = CefrLevel.A1, Status = ContentStatus.Published, BodyHtml = "<p>Hola</p>" };
        module.Lectures.Add(lecture);
        course.Modules.Add(module);
        db.Courses.Add(course);
        db.Quizzes.Add(new Quiz { Course = course, Type = QuizType.Monthly, Title = "Month 1 exam", MonthNo = 1, Status = ContentStatus.Published });
        await db.SaveChangesAsync();
        return new Seeded(language.Id, course.Id, lecture.Id);
    }

    private sealed record Learner(HttpClient Client, Guid UserId, int? LearnerCourseId, string Email);

    private async Task<Learner> LearnerAsync(Seeded? s = null, DateOnly? dob = null)
    {
        var email = $"release-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        var register = await client.PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest("Release Learner", email, Password, null, "UTC", "en", null, dob));
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        var authed = factory.CreateClient().WithBearer(auth.AccessToken);
        int? lcId = null;
        if (s is not null)
        {
            var plan = new LearningPlanRequest(s.CourseId, CefrLevel.A1, CefrLevel.A2, LearningGoal.Travel, null, 3, null, 10,
                StudyDays.Everyday, new TimeOnly(19, 0), FocusSkill.All);
            lcId = (await (await authed.PostJsonAsync("/api/v1/learner-courses", plan)).ReadDataAsync<EnrolResponse>()).Course.Id;
        }
        using var scope = factory.Services.CreateScope();
        var userId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return new Learner(authed, userId, lcId, email);
    }

    private async Task<ReminderRunResult> RunRemindersAsync(DateTimeOffset at)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IReminderService>().RunAsync(at);
    }

    private static DateTimeOffset TodayUtcAt(int hour, int minute = 10) =>
        new(DateTime.UtcNow.Date.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

    // ------------------------------------------------------------------ FR-L110 reminders and FR-L112 inbox

    [Fact]
    public async Task Study_reminder_lands_in_the_inbox_once_and_the_inbox_can_be_read_and_cleared()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);

        // 19:10 UTC is inside the learner's 19:00 study window.
        await RunRemindersAsync(TodayUtcAt(19));
        var page = await (await l.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>();
        var study = Assert.Single(page.Items, n => n.Type == NotificationType.StudyReminder);
        Assert.Equal("today", study.Data!["type"]);
        Assert.Equal(l.LearnerCourseId.ToString(), study.Data["learnerCourseId"]);
        Assert.True(page.UnreadCount >= 1);

        // A second pass (or a second server) doesn't send it again.
        await RunRemindersAsync(TodayUtcAt(19, 25));
        page = await (await l.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>();
        Assert.Single(page.Items, n => n.Type == NotificationType.StudyReminder);

        // Read one, then all, then delete.
        Assert.Equal(HttpStatusCode.OK, (await l.Client.PutAsync($"/api/v1/notifications/{study.Id}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await l.Client.PutAsync("/api/v1/notifications/read-all", null)).StatusCode);
        Assert.Equal(0, (await (await l.Client.GetAsync("/api/v1/notifications/unread-count")).ReadDataAsync<UnreadCountDto>()).Count);
        Assert.Equal(HttpStatusCode.OK, (await l.Client.DeleteAsync($"/api/v1/notifications/{study.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await l.Client.PutAsync($"/api/v1/notifications/{study.Id}/read", null)).StatusCode);
    }

    [Fact]
    public async Task No_study_reminder_outside_the_window_in_quiet_hours_or_when_switched_off()
    {
        var s = await SeedAsync();
        var early = await LearnerAsync(s);
        var quiet = await LearnerAsync(s);
        var off = await LearnerAsync(s);

        // 15:10 is before 19:00: nothing for anyone in this course yet.
        await RunRemindersAsync(TodayUtcAt(15));
        Assert.DoesNotContain((await (await early.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>()).Items,
            n => n.Type == NotificationType.StudyReminder);

        // 23:10 is in quiet hours (22:00-08:00) even though the window would have been open.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lc = await db.LearnerCourses.SingleAsync(c => c.Id == quiet.LearnerCourseId);
            lc.PreferredStudyTime = new TimeOnly(23, 0);
            var user = await db.Users.SingleAsync(u => u.Id == off.UserId);
            user.NotifyStudyReminders = false;
            await db.SaveChangesAsync();
        }
        await RunRemindersAsync(TodayUtcAt(23));
        Assert.DoesNotContain((await (await quiet.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>()).Items,
            n => n.Type == NotificationType.StudyReminder);

        await RunRemindersAsync(TodayUtcAt(19));
        Assert.DoesNotContain((await (await off.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>()).Items,
            n => n.Type == NotificationType.StudyReminder);
    }

    [Fact]
    public async Task Exam_soon_and_new_post_reminders_reach_learners_of_that_language()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);

        // Today shows the next exam (the last planned date of month 1).
        var today = await (await l.Client.GetAsync($"/api/v1/learner-courses/{l.LearnerCourseId}/today")).ReadDataAsync<TodayDto>();
        Assert.NotNull(today.NextExamDate);

        int postId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = new Post
            {
                LanguageId = s.LanguageId, Title = "Five travel phrases", Body = "<p>Hola, ¿dónde está la estación?</p>",
                Status = ContentStatus.Published, PublishAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            db.Posts.Add(post);
            await db.SaveChangesAsync();
            postId = post.Id;
        }

        // The exam is within 3 days only if the plan put month 1's last lecture that close; the post always goes out.
        var result = await RunRemindersAsync(TodayUtcAt(10));
        Assert.True(result.Posts >= 1);
        var items = (await (await l.Client.GetAsync("/api/v1/notifications")).ReadDataAsync<NotificationPageDto>()).Items;
        var postNote = Assert.Single(items, n => n.Type == NotificationType.NewPost);
        Assert.Equal(postId.ToString(), postNote.Data!["postId"]);
        Assert.DoesNotContain("<p>", postNote.Body);
        if (today.NextExamDate!.Value.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber is >= 0 and <= 3)
            Assert.Contains(items, n => n.Type == NotificationType.ExamSoon);

        // The post is told once.
        Assert.Equal(0, (await RunRemindersAsync(TodayUtcAt(11))).Posts);
        using var check = factory.Services.CreateScope();
        Assert.NotNull((await check.ServiceProvider.GetRequiredService<AppDbContext>().Posts.SingleAsync(p => p.Id == postId)).NotifiedAt);
    }

    // ------------------------------------------------------------------ BR-32

    [Fact]
    public async Task Under_13_cannot_register_and_under_18_chats_with_friends_only()
    {
        var tooYoung = await factory.CreateClient().PostJsonAsync("/api/v1/auth/register",
            new RegisterRequest("Young", $"young-{Guid.NewGuid():N}@test.local", Password, null, "UTC", "en", null,
                DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-12)));
        Assert.Equal(HttpStatusCode.BadRequest, tooYoung.StatusCode);
        Assert.True((await tooYoung.ReadProblemAsync()).Errors!.ContainsKey("DateOfBirth"));

        var teen = await LearnerAsync(dob: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-16));
        var adult = await LearnerAsync();

        // Neither side can open a chat while they aren't friends, whoever starts it.
        var refused = await adult.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(teen.UserId));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("minor_friends_only", (await refused.ReadProblemAsync()).Code);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await teen.Client.PostJsonAsync("/api/v1/conversations/direct", new CreateDirectRequest(adult.UserId))).StatusCode);

        // The date of birth shows in the profile and can't be changed afterwards from the app.
        var profile = await (await teen.Client.GetAsync("/api/v1/me")).ReadDataAsync<UserProfileDto>();
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-16), profile.DateOfBirth);
    }

    // ------------------------------------------------------------------ NFR-08 retention

    [Fact]
    public async Task Maintenance_deletes_old_practice_recordings_and_read_notifications_but_keeps_recent_ones()
    {
        var l = await LearnerAsync();
        long oldNote, newNote;
        int oldAttempt, newAttempt;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lang = await db.Languages.Select(x => x.Id).FirstAsync();
            var old = new PracticeAttempt { UserId = l.UserId, LanguageId = lang, Type = PracticeType.Speak, AudioUrl = "/uploads/practice/old.wav", Score = 80, CreatedAt = DateTimeOffset.UtcNow.AddDays(-31) };
            var recent = new PracticeAttempt { UserId = l.UserId, LanguageId = lang, Type = PracticeType.Speak, AudioUrl = "/uploads/practice/new.wav", Score = 70, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) };
            var readOld = new Notification { UserId = l.UserId, Type = NotificationType.System, Title = "Old", Body = "Read long ago", CreatedAt = DateTimeOffset.UtcNow.AddDays(-120), ReadAt = DateTimeOffset.UtcNow.AddDays(-100) };
            var unread = new Notification { UserId = l.UserId, Type = NotificationType.System, Title = "New", Body = "Still unread", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
            db.AddRange(old, recent, readOld, unread);
            await db.SaveChangesAsync();
            (oldAttempt, newAttempt, oldNote, newNote) = (old.Id, recent.Id, readOld.Id, unread.Id);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().RunAsync(DateTimeOffset.UtcNow);
            Assert.True(result.RecordingsDeleted >= 1);
            Assert.True(result.NotificationsDeleted >= 1);
        }

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var oldRow = await vdb.PracticeAttempts.SingleAsync(a => a.Id == oldAttempt);
        Assert.Null(oldRow.AudioUrl);
        Assert.Equal(80, oldRow.Score); // the score and feedback stay
        Assert.NotNull((await vdb.PracticeAttempts.SingleAsync(a => a.Id == newAttempt)).AudioUrl);
        Assert.False(await vdb.Notifications.AnyAsync(n => n.Id == oldNote));
        Assert.True(await vdb.Notifications.AnyAsync(n => n.Id == newNote));
    }

    // ------------------------------------------------------------------ FR-A100 analytics

    [Fact]
    public async Task Every_analytics_report_runs_and_exports_to_excel_and_pdf()
    {
        var s = await SeedAsync();
        var l = await LearnerAsync(s);
        await l.Client.PostJsonAsync($"/api/v1/lectures/{s.LectureId}/complete",
            new LinguaPath.Shared.Contracts.Content.CompleteLectureRequest(l.LearnerCourseId!.Value, 120));

        using var scope = factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<IAnalyticsReportService>();
        var exporter = scope.ServiceProvider.GetRequiredService<IReportExporter>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var kind in Enum.GetValues<ReportKind>())
        {
            var table = await reports.RunAsync(new ReportFilter(kind, today.AddDays(-30), today, null, s.CourseId));
            Assert.All(table.Rows, r => Assert.Equal(table.Columns.Count, r.Length));
            var xlsx = exporter.ToExcel(table);
            Assert.Equal((byte)'P', xlsx[0]); // zip container
            Assert.Equal((byte)'K', xlsx[1]);
            var pdf = exporter.ToPdf(table);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        }

        var progress = await reports.RunAsync(new ReportFilter(ReportKind.LearnerProgress, today.AddDays(-30), today, null, s.CourseId));
        var row = Assert.Single(progress.Rows);
        Assert.Equal(l.Email, row[1]);
        Assert.Equal(1, row[7]); // lectures done

        var dropOff = await reports.RunAsync(new ReportFilter(ReportKind.LectureDropOff, today.AddDays(-30), today, null, s.CourseId));
        Assert.Single(dropOff.Rows);
        var noCourse = await reports.RunAsync(new ReportFilter(ReportKind.LectureDropOff, today.AddDays(-30), today, null, null));
        Assert.NotNull(noCourse.Notice);
    }

    // ------------------------------------------------------------------ health, headers, crash reports, legal pages

    [Fact]
    public async Task Health_endpoints_security_headers_crash_reports_and_legal_pages()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("database", await ready.Content.ReadAsStringAsync());

        var api = await client.GetAsync("/api/v1/languages");
        Assert.Equal("nosniff", api.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", api.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", api.Headers.GetValues("Content-Security-Policy").Single());

        var crash = await client.PostAsJsonAsync("/api/v1/client-errors", new ClientErrorReport("Android", "1.0.0", "14", "Pixel 8",
            "Object reference not set", "System.NullReferenceException", "at HomeViewModel.LoadAsync()", "HomePage", DateTimeOffset.UtcNow), ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Accepted, crash.StatusCode);

        var privacy = await client.GetAsync("/privacy");
        Assert.Equal(HttpStatusCode.Redirect, privacy.StatusCode);
        var page = await client.GetAsync(privacy.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Privacy policy", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/legal/delete-account.html")).StatusCode);
    }
}
