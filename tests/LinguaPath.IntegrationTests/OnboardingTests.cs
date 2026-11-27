using System.Net;
using System.Net.Http.Json;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Catalog;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 2 acceptance: onboarding, feasibility, roadmap, today, re-plan, pause/resume, language requests.</summary>
public sealed class OnboardingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    /// <summary>A published test language with one course: 2 A1 modules and 1 A2 module, 9 lectures (3 optional).</summary>
    private async Task<(int LanguageId, int CourseId)> SeedCourseAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "t" + Guid.NewGuid().ToString("N")[..6];
        var language = new Language { Name = "Lang " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published };
        var course = new Course
        {
            Language = language, Title = "Test course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, Status = ContentStatus.Published
        };
        var levels = new[] { CefrLevel.A1, CefrLevel.A1, CefrLevel.A2 };
        for (var m = 0; m < 3; m++)
        {
            var module = new CourseModule { Title = $"Module {m + 1}", Level = levels[m], MonthNo = m + 1, SortOrder = m + 1 };
            for (var l = 0; l < 3; l++)
                module.Lectures.Add(new Lecture { Title = $"Lecture {m + 1}.{l + 1}", EstimatedMinutes = 15, IsCore = l < 2, SortOrder = l + 1, Level = levels[m], Status = ContentStatus.Published, BodyHtml = "<p>Hola</p>" });
            course.Modules.Add(module);
        }
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return (language.Id, course.Id);
    }

    private async Task<HttpClient> LearnerAsync()
    {
        var email = $"onb-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Onboarding Learner", email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        return factory.CreateClient().WithBearer(auth.AccessToken);
    }

    private static LearningPlanRequest Plan(int courseId, int? months = 3, int minutes = 20, StudyDays days = StudyDays.Weekdays,
        CefrLevel target = CefrLevel.A2) =>
        new(courseId, CefrLevel.A1, target, LearningGoal.Travel, null, months, null, minutes, days, new TimeOnly(19, 0), FocusSkill.All);

    [Fact]
    public async Task Learner_enrols_and_gets_a_paced_roadmap()
    {
        var (languageId, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();

        var courses = await (await client.GetAsync($"/api/v1/languages/{languageId}/courses")).ReadDataAsync<List<CourseDto>>();
        Assert.Single(courses);
        Assert.Equal(9, courses[0].LectureCount);

        var check = await (await client.PostJsonAsync("/api/v1/learner-courses/feasibility", Plan(courseId))).ReadDataAsync<FeasibilityResponse>();
        Assert.Equal(FeasibilityStatus.Ok, check.Status);
        Assert.Equal(9, check.PlannedLectures);

        var enrol = await client.PostJsonAsync("/api/v1/learner-courses", Plan(courseId));
        Assert.Equal(HttpStatusCode.OK, enrol.StatusCode);
        var enrolled = await enrol.ReadDataAsync<EnrolResponse>();
        Assert.Equal(9, enrolled.Course.TotalLectures);
        Assert.Equal(0, enrolled.Course.ProgressPercent);

        var roadmap = await (await client.GetAsync($"/api/v1/learner-courses/{enrolled.Course.Id}/roadmap")).ReadDataAsync<RoadmapDto>();
        Assert.Equal(new[] { "Module 1", "Module 2", "Module 3" }, roadmap.Modules.Select(m => m.Title));
        var all = roadmap.Modules.SelectMany(m => m.Lectures).ToList();
        Assert.Equal(RoadmapItemState.Available, all[0].State);                       // FR-L31: first lecture open
        Assert.All(all.Skip(1), l => Assert.Equal(RoadmapItemState.Locked, l.State)); // the rest unlock in order
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.PlannedDate <= p.Second.PlannedDate));
        Assert.All(all, l => Assert.True(StudyDays.Weekdays.Includes(l.PlannedDate.DayOfWeek)));

        var today = await (await client.GetAsync($"/api/v1/learner-courses/{enrolled.Course.Id}/today")).ReadDataAsync<TodayDto>();
        Assert.NotEmpty(today.NextLectures);
        Assert.Equal("Lecture 1.1", today.NextLectures[0].Title);
        Assert.Single(today.Courses);
    }

    [Fact]
    public async Task Only_one_active_course_per_language()
    {
        var (_, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/v1/learner-courses", Plan(courseId))).StatusCode);

        var again = await client.PostJsonAsync("/api/v1/learner-courses", Plan(courseId));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("course_already_active", (await again.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Short_time_drops_optional_lectures_or_suggests_a_better_plan()
    {
        var (_, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();

        // 1 month, 15 min, one day a week ≈ 4-5 study days: 6 core lectures do not fit.
        var tight = await (await client.PostJsonAsync("/api/v1/learner-courses/feasibility",
            Plan(courseId, months: 1, minutes: 15, days: StudyDays.Saturday))).ReadDataAsync<FeasibilityResponse>();
        Assert.Equal(FeasibilityStatus.NotFeasible, tight.Status);
        Assert.NotNull(tight.SuggestedEndDate);
        Assert.NotNull(tight.SuggestedDailyMinutes);

        // 1 month, 15 min, 2 days a week ≈ 8-9 study days: core fits, some optional dropped (or all fit).
        var medium = await (await client.PostJsonAsync("/api/v1/learner-courses/feasibility",
            Plan(courseId, months: 1, minutes: 15, days: StudyDays.Saturday | StudyDays.Sunday))).ReadDataAsync<FeasibilityResponse>();
        Assert.NotEqual(FeasibilityStatus.NotFeasible, medium.Status);
        Assert.True(medium.PlannedLectures >= 6);
    }

    [Fact]
    public async Task Target_level_trims_the_roadmap()
    {
        var (_, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();
        var a1Only = await (await client.PostJsonAsync("/api/v1/learner-courses/feasibility", Plan(courseId, target: CefrLevel.A1)))
            .ReadDataAsync<FeasibilityResponse>();
        Assert.Equal(6, a1Only.TotalLectures); // the A2 module is left out

        var tooHigh = await client.PostJsonAsync("/api/v1/learner-courses/feasibility", Plan(courseId, target: CefrLevel.B2));
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
    }

    [Fact]
    public async Task Changing_the_plan_re_paces_the_roadmap()
    {
        var (_, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();
        var enrolled = await (await client.PostJsonAsync("/api/v1/learner-courses", Plan(courseId))).ReadDataAsync<EnrolResponse>();
        var id = enrolled.Course.Id;

        var update = new UpdateLearningPlanRequest(CefrLevel.A1, LearningGoal.Conversation, null, null,
            enrolled.Course.EndDate, 60, StudyDays.Everyday, null, FocusSkill.Speaking | FocusSkill.Listening);
        var response = await client.PutAsJsonAsync($"/api/v1/learner-courses/{id}", update, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadDataAsync<EnrolResponse>();

        Assert.Equal(6, updated.Course.TotalLectures);           // A2 module removed by the lower target
        Assert.Equal(60, updated.Course.DailyMinutes);
        Assert.Equal(LearningGoal.Conversation, updated.Course.Goal);
    }

    [Fact]
    public async Task Pause_and_resume_follow_the_30_day_rule()
    {
        var (_, courseId) = await SeedCourseAsync();
        var client = await LearnerAsync();
        var id = (await (await client.PostJsonAsync("/api/v1/learner-courses", Plan(courseId))).ReadDataAsync<EnrolResponse>()).Course.Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync($"/api/v1/learner-courses/{id}/pause", new PauseCourseRequest(31))).StatusCode);

        var paused = await (await client.PostJsonAsync($"/api/v1/learner-courses/{id}/pause", new PauseCourseRequest(7))).ReadDataAsync<LearnerCourseDto>();
        Assert.Equal(LearnerCourseStatus.Paused, paused.Status);
        Assert.NotNull(paused.PausedUntil);
        Assert.Equal(23, paused.RemainingPauseDays);

        // The plan can't change while paused.
        var update = new UpdateLearningPlanRequest(CefrLevel.A2, LearningGoal.Travel, null, 3, null, 20, StudyDays.Weekdays, null, FocusSkill.All);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/learner-courses/{id}", update, ApiFactory.Json)).StatusCode);

        var resumed = await (await client.PostAsync($"/api/v1/learner-courses/{id}/resume", null)).ReadDataAsync<LearnerCourseDto>();
        Assert.Equal(LearnerCourseStatus.Active, resumed.Status);
        Assert.Equal(paused.EndDate, resumed.EndDate); // resumed the same day: nothing shifted
    }

    [Fact]
    public async Task Coming_soon_languages_can_be_requested_once_per_learner()
    {
        var client = await LearnerAsync();
        var languages = await (await client.GetAsync("/api/v1/languages")).ReadDataAsync<List<LanguageDto>>();
        var comingSoon = languages.First(l => l.Status == LanguageStatus.ComingSoon);

        var first = await client.PostJsonAsync("/api/v1/languages/requests", new CreateLanguageRequest(comingSoon.Name, comingSoon.Id, null));
        var second = await client.PostJsonAsync("/api/v1/languages/requests", new CreateLanguageRequest(comingSoon.Name, comingSoon.Id, null));
        var free = await client.PostJsonAsync("/api/v1/languages/requests", new CreateLanguageRequest("Klingon", null, "for fun"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);

        var me = await (await client.GetAsync("/api/v1/me")).ReadDataAsync<LinguaPath.Shared.Contracts.Profile.UserProfileDto>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, db.LanguageRequests.Count(r => r.RequestedByUserId == me.Id && r.LanguageId == comingSoon.Id));
        Assert.Equal(1, db.LanguageRequests.Count(r => r.RequestedByUserId == me.Id && r.NormalizedName == "klingon"));
    }

    [Fact]
    public async Task Sign_in_reports_an_active_course()
    {
        var (_, courseId) = await SeedCourseAsync();
        var email = $"onb-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Learner", email, Password, null, "UTC", "en"));
        var first = await (await client.PostJsonAsync("/api/v1/auth/verify-email",
            new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null))).ReadDataAsync<AuthResponse>();
        Assert.False(first.User.HasActiveCourse);

        await factory.CreateClient().WithBearer(first.AccessToken).PostJsonAsync("/api/v1/learner-courses", Plan(courseId));

        var login = await (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password, null))).ReadDataAsync<AuthResponse>();
        Assert.True(login.User.HasActiveCourse);
    }
}
