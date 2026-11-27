using System.Net;
using System.Net.Http.Json;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Auth;
using LinguaPath.Shared.Contracts.Content;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 3 acceptance: lecture reader, completion rules, bookmarks/notes/ratings/reports, posts, review and publishing.</summary>
public sealed class ContentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Learner123";

    private sealed record Seeded(int LanguageId, int CourseId, int[] LectureIds, int DraftLectureId);

    /// <summary>One module with 3 published lectures (10 min each) and 1 draft lecture.</summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "c" + Guid.NewGuid().ToString("N")[..6];
        var language = new Language { Name = "Lang " + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Published };
        var course = new Course { Language = language, Title = "Reader course", StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A1, Status = ContentStatus.Published };
        var module = new CourseModule { Title = "Basics", Level = CefrLevel.A1, MonthNo = 1, SortOrder = 1 };
        for (var i = 1; i <= 3; i++)
        {
            module.Lectures.Add(new Lecture
            {
                Title = $"Lecture {i}", EstimatedMinutes = 10, IsCore = true, SortOrder = i, Level = CefrLevel.A1,
                Status = ContentStatus.Published, BodyHtml = $"<p>Hola {i}</p>",
                Vocabulary = { new LectureVocabulary { Word = "hola", Meaning = "hello", SortOrder = 1 } },
                Examples = { new LectureExample { TargetText = "¡Hola, Ana!", Translation = "Hi, Ana!", SortOrder = 1 } }
            });
        }
        var draft = new Lecture { Title = "Draft lecture", EstimatedMinutes = 10, IsCore = true, SortOrder = 4, Level = CefrLevel.A1, BodyHtml = "<p>Soon</p>" };
        module.Lectures.Add(draft);
        course.Modules.Add(module);
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return new Seeded(language.Id, course.Id, module.Lectures.Where(l => l != draft).OrderBy(l => l.SortOrder).Select(l => l.Id).ToArray(), draft.Id);
    }

    private async Task<HttpClient> LearnerAsync()
    {
        var email = $"content-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("Content Learner", email, Password, null, "UTC", "en"));
        var verify = await client.PostJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(email, factory.Emails.LatestCodeFor(email), null));
        var auth = await verify.ReadDataAsync<AuthResponse>();
        return factory.CreateClient().WithBearer(auth.AccessToken);
    }

    private static async Task<int> EnrolAsync(HttpClient client, int courseId)
    {
        var plan = new LearningPlanRequest(courseId, CefrLevel.A1, CefrLevel.A1, LearningGoal.Travel, null, 3, null, 30,
            StudyDays.Everyday, new TimeOnly(19, 0), FocusSkill.All);
        var enrolled = await (await client.PostJsonAsync("/api/v1/learner-courses", plan)).ReadDataAsync<EnrolResponse>();
        return enrolled.Course.Id;
    }

    [Fact]
    public async Task Draft_lectures_stay_out_of_roadmaps()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);

        var roadmap = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/roadmap")).ReadDataAsync<RoadmapDto>();
        var ids = roadmap.Modules.SelectMany(m => m.Lectures).Select(l => l.LectureId).ToList();
        Assert.Equal(seeded.LectureIds, ids);
        Assert.DoesNotContain(seeded.DraftLectureId, ids);
    }

    [Fact]
    public async Task Reader_opens_available_lecture_and_blocks_locked_ones()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);

        var first = await (await client.GetAsync($"/api/v1/lectures/{seeded.LectureIds[0]}?learnerCourseId={lc}")).ReadDataAsync<LectureDto>();
        Assert.Equal("Lecture 1", first.Title);
        Assert.Contains("Hola 1", first.BodyHtml);
        Assert.Equal(RoadmapItemState.InProgress, first.State);       // opening starts it
        Assert.Equal(300, first.MinimumSecondsToComplete);              // 50% of 10 minutes
        Assert.Single(first.Vocabulary);
        Assert.Single(first.Examples);
        Assert.Equal(seeded.LectureIds[1], first.NextLectureId);

        var locked = await client.GetAsync($"/api/v1/lectures/{seeded.LectureIds[1]}?learnerCourseId={lc}");
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.Equal("lecture_locked", (await locked.ReadProblemAsync()).Code);
    }

    [Fact]
    public async Task Completing_needs_minimum_reading_time_and_unlocks_the_next_lecture()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);
        var id = seeded.LectureIds[0];
        await client.GetAsync($"/api/v1/lectures/{id}?learnerCourseId={lc}");

        var tooQuick = await client.PostJsonAsync($"/api/v1/lectures/{id}/complete", new CompleteLectureRequest(lc, 60));
        Assert.Equal(HttpStatusCode.BadRequest, tooQuick.StatusCode);
        Assert.Equal("too_quick", (await tooQuick.ReadProblemAsync()).Code);

        // Reading time is kept between attempts: 60 s + 200 s progress + 40 s = 300 s.
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/api/v1/lectures/{id}/progress", new LectureProgressRequest(lc, 200))).StatusCode);
        var done = await client.PostJsonAsync($"/api/v1/lectures/{id}/complete", new CompleteLectureRequest(lc, 40));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var result = await done.ReadDataAsync<CompleteLectureResponse>();
        Assert.Equal(seeded.LectureIds[1], result.NextLectureId);
        Assert.Equal(1, result.CompletedLectures);
        Assert.Equal(3, result.TotalLectures);
        Assert.Equal(33, result.ProgressPercent);
        Assert.False(result.AllLecturesDone);

        var next = await client.GetAsync($"/api/v1/lectures/{seeded.LectureIds[1]}?learnerCourseId={lc}");
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);

        // Completing again is harmless and returns the same progress.
        var again = await (await client.PostJsonAsync($"/api/v1/lectures/{id}/complete", new CompleteLectureRequest(lc, 0))).ReadDataAsync<CompleteLectureResponse>();
        Assert.Equal(1, again.CompletedLectures);
    }

    [Fact]
    public async Task Bookmarks_notes_ratings_and_reports_round_trip()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);
        var id = seeded.LectureIds[0];

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/lectures/{id}/bookmark", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/lectures/{id}/note", new SaveNoteRequest("Hola = hello"), ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/api/v1/lectures/{id}/rating", new RateLectureRequest(4, null))).StatusCode);

        var badRating = await client.PostJsonAsync($"/api/v1/lectures/{id}/rating", new RateLectureRequest(9, null));
        Assert.Equal(HttpStatusCode.BadRequest, badRating.StatusCode);

        var lecture = await (await client.GetAsync($"/api/v1/lectures/{id}?learnerCourseId={lc}")).ReadDataAsync<LectureDto>();
        Assert.True(lecture.IsBookmarked);
        Assert.Equal("Hola = hello", lecture.Note);
        Assert.Equal(4, lecture.MyRating);
        Assert.Equal(1, lecture.RatingCount);

        var bookmarks = await (await client.GetAsync("/api/v1/lectures/bookmarks")).ReadDataAsync<List<BookmarkDto>>();
        Assert.Contains(bookmarks, b => b.LectureId == id);

        var report = await client.PostJsonAsync($"/api/v1/lectures/{id}/report", new ReportContentRequest("The example translation is wrong."));
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.ContentReports.AnyAsync(r => r.EntityType == ReportEntityType.Lecture && r.EntityId == id && r.Status == ReportStatus.Open));

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/lectures/{id}/bookmark")).StatusCode);
        var after = await (await client.GetAsync("/api/v1/lectures/bookmarks")).ReadDataAsync<List<BookmarkDto>>();
        Assert.DoesNotContain(after, b => b.LectureId == id);
    }

    [Fact]
    public async Task Feed_shows_published_posts_for_my_languages_and_tracks_likes_and_saves()
    {
        var seeded = await SeedAsync();
        int publishedId, draftId, scheduledId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var published = new Post { LanguageId = seeded.LanguageId, Title = "Say hola", Status = ContentStatus.Published, PublishAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
            var draft = new Post { LanguageId = seeded.LanguageId, Title = "Not yet", Status = ContentStatus.Draft };
            var scheduled = new Post { LanguageId = seeded.LanguageId, Title = "Tomorrow", Status = ContentStatus.Published, PublishAt = DateTimeOffset.UtcNow.AddDays(1) };
            db.Posts.AddRange(published, draft, scheduled);
            await db.SaveChangesAsync();
            (publishedId, draftId, scheduledId) = (published.Id, draft.Id, scheduled.Id);
        }

        var client = await LearnerAsync();
        await EnrolAsync(client, seeded.CourseId);

        var feed = await (await client.GetAsync($"/api/v1/posts?languageId={seeded.LanguageId}")).ReadDataAsync<PagedResult<PostDto>>();
        var ids = feed.Items.Select(p => p.Id).ToList();
        Assert.Contains(publishedId, ids);
        Assert.DoesNotContain(draftId, ids);
        Assert.DoesNotContain(scheduledId, ids);

        var liked = await (await client.PostAsync($"/api/v1/posts/{publishedId}/like", null)).ReadDataAsync<PostInteractionResponse>();
        Assert.True(liked.LikedByMe);
        Assert.Equal(1, liked.Likes);
        var unliked = await (await client.PostAsync($"/api/v1/posts/{publishedId}/like", null)).ReadDataAsync<PostInteractionResponse>();
        Assert.False(unliked.LikedByMe);
        Assert.Equal(0, unliked.Likes);

        var saved = await (await client.PostAsync($"/api/v1/posts/{publishedId}/save", null)).ReadDataAsync<PostInteractionResponse>();
        Assert.True(saved.SavedByMe);
        var savedFeed = await (await client.GetAsync("/api/v1/posts?saved=true")).ReadDataAsync<PagedResult<PostDto>>();
        Assert.Equal(new[] { publishedId }, savedFeed.Items.Select(p => p.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/posts/{draftId}/like", null)).StatusCode);
    }

    [Fact]
    public async Task Review_then_publish_adds_the_lecture_to_enrolled_roadmaps()
    {
        var seeded = await SeedAsync();
        var client = await LearnerAsync();
        var lc = await EnrolAsync(client, seeded.CourseId);

        using (var scope = factory.Services.CreateScope())
        {
            var reviews = scope.ServiceProvider.GetRequiredService<IReviewService>();
            var lectures = scope.ServiceProvider.GetRequiredService<ILectureContentAdminService>();

            // Only Approved lectures can be published.
            Assert.True((await lectures.PublishAsync(seeded.DraftLectureId)).IsFailure);

            Assert.True((await reviews.SubmitAsync(ReviewEntityType.Lecture, seeded.DraftLectureId)).IsSuccess);
            var pending = (await reviews.PendingAsync(ReviewEntityType.Lecture)).Single(r => r.EntityId == seeded.DraftLectureId);

            // A reviewer can't approve their own submission (here: the same, anonymous, user).
            Assert.True((await reviews.ApproveAsync(pending.Id, null)).IsFailure);

            // Sending it back returns it to Draft with the comment in its history.
            Assert.True((await reviews.RejectAsync(pending.Id, "Add an example")).IsSuccess);
            var history = await reviews.HistoryAsync(ReviewEntityType.Lecture, seeded.DraftLectureId);
            Assert.Equal("Add an example", history[0].Comment);

            // Approval by another reviewer is simulated directly, then the lecture is published.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var lecture = await db.Lectures.SingleAsync(l => l.Id == seeded.DraftLectureId);
            Assert.Equal(ContentStatus.Draft, lecture.Status);
            lecture.Status = ContentStatus.Approved;
            await db.SaveChangesAsync();

            Assert.True((await lectures.PublishAsync(seeded.DraftLectureId)).IsSuccess);
        }

        var roadmap = await (await client.GetAsync($"/api/v1/learner-courses/{lc}/roadmap")).ReadDataAsync<RoadmapDto>();
        var ids = roadmap.Modules.SelectMany(m => m.Lectures).Select(l => l.LectureId).ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(seeded.DraftLectureId, ids[^1]);
    }
}
