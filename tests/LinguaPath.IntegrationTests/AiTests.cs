using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Persistence;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>Phase 5 acceptance with the offline AI provider: new-language workflow, lectures, questions, posts, writing pre-grading.</summary>
public sealed class AiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<int> NewLanguageAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var iso = "a" + Guid.NewGuid().ToString("N")[..6];
        var language = new Language { Name = "Lang" + iso, NativeName = iso, IsoCode = iso, Status = LanguageStatus.Draft };
        db.Languages.Add(language);
        await db.SaveChangesAsync();
        return language.Id;
    }

    /// <summary>Runs queued jobs until the queue is empty (the admin host does this in the background).</summary>
    private async Task RunJobsAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.Services.CreateScope();
            if (!await scope.ServiceProvider.GetRequiredService<IAiJobProcessor>().RunNextAsync("test")) return;
        }
    }

    [Fact]
    public async Task New_language_needs_approval_then_roadmap_review_then_writes_lectures_and_questions_in_draft()
    {
        var languageId = await NewLanguageAsync();
        int requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            var created = await ai.RequestNewLanguageAsync(new RoadmapAiInput
            {
                LanguageId = languageId, StartLevel = CefrLevel.A1, EndLevel = CefrLevel.A2, TotalHours = 4, Notes = "Business context"
            });
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error!.Message : null);
            requestId = created.Value;

            var detail = await ai.GetAsync(requestId);
            Assert.Equal(AiRequestStatus.PendingApproval, detail!.Item.Status);
            Assert.Equal(12, detail.EstimatedLectures);          // FR-A22 estimate: 4 h × 60 / 20 min
            Assert.Equal(60, detail.EstimatedQuestions);
            Assert.True(detail.Item.EstimatedCost > 0);

            // A second request for the same language is refused.
            Assert.True((await ai.RequestNewLanguageAsync(new RoadmapAiInput { LanguageId = languageId, TotalHours = 4 })).IsFailure);
        }

        // Nothing runs before a Super Admin approves (FR-A21).
        await RunJobsAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Courses.AnyAsync(c => c.LanguageId == languageId));
            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            Assert.True((await ai.DecideAsync(requestId, approve: true, notes: "Focus on travel too")).IsSuccess);
        }

        // Step 1: the roadmap (FR-A23).
        await RunJobsAsync();
        int courseId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId);
            Assert.Equal(AiRequestStatus.AwaitingRoadmapReview, request.Status);
            Assert.Contains("Focus on travel too", request.Notes);
            var course = await db.Courses.Include(c => c.Modules).ThenInclude(m => m.Lectures).SingleAsync(c => c.LanguageId == languageId);
            courseId = course.Id;
            Assert.Equal(ContentStatus.Draft, course.Status);
            Assert.Equal(ContentSource.Ai, course.Source);
            Assert.Equal(12, course.Modules.Sum(m => m.Lectures.Count));
            Assert.All(course.Modules.SelectMany(m => m.Lectures), l =>
            {
                Assert.Equal(ContentSource.Ai, l.Source);
                Assert.Null(l.BodyHtml);
            });
            Assert.True(await db.AiGeneratedItems.AnyAsync(i => i.RequestId == requestId && i.EntityType == ReviewEntityType.Course && i.EntityId == courseId));

            // The admin edits the roadmap (deletes one lecture) and approves it.
            var drop = course.Modules.First().Lectures.OrderBy(l => l.SortOrder).Last();
            db.Lectures.Remove(drop);
            await db.SaveChangesAsync();
            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            Assert.True((await ai.ApproveRoadmapAsync(requestId)).IsSuccess);
        }

        // Step 2: lectures, then questions, module by module.
        await RunJobsAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId);
            Assert.Equal(AiRequestStatus.Completed, request.Status);
            Assert.Equal(11, request.ProgressDone);
            Assert.Equal(11, request.ProgressTotal);

            var lectures = await db.Lectures.Include(l => l.Examples).Where(l => l.Module.CourseId == courseId).ToListAsync();
            Assert.Equal(11, lectures.Count);
            Assert.All(lectures, l =>
            {
                Assert.False(string.IsNullOrWhiteSpace(l.BodyHtml));
                Assert.DoesNotContain("<script", l.BodyHtml);
                Assert.Equal(10, l.Examples.Count);
                Assert.Equal(ContentStatus.Draft, l.Status);   // BR-13: never published without review
                Assert.Equal(2, l.Version);                    // saved as a new version
            });

            var questions = await db.Questions.Where(q => q.CourseId == courseId).ToListAsync();
            Assert.Equal(55, questions.Count);                 // 5 per lecture
            Assert.All(questions, q =>
            {
                Assert.Equal(ContentSource.Ai, q.Source);
                Assert.Equal(ContentStatus.Draft, q.Status);
                Assert.False(string.IsNullOrWhiteSpace(q.SourceText));
            });

            // FR-A72: each draft knows its prompt and model; usage is logged (offline calls are free).
            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            var provenance = await ai.ProvenanceAsync(ReviewEntityType.Lecture, lectures[0].Id);
            Assert.NotNull(provenance);
            Assert.Equal("offline-sample", provenance.Model);
            Assert.Contains("Business context", provenance.Prompt);
            var logs = await db.AiUsageLogs.Where(l => l.RequestId == requestId).ToListAsync();
            Assert.Equal(1 + 11 + 11, logs.Count);             // roadmap + lecture + questions per lecture
            Assert.All(logs, l => Assert.Equal(AiCallStatus.Success, l.Status));
            Assert.Equal(0m, logs.Sum(l => l.Cost));
        }
    }

    [Fact]
    public async Task Questions_need_lecture_text_and_posts_are_scheduled_one_a_day()
    {
        var languageId = await NewLanguageAsync();
        int lectureId, emptyLectureId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var module = new CourseModule { Title = "M", MonthNo = 1, SortOrder = 1 };
            module.Lectures.Add(new Lecture { Title = "With text", SortOrder = 1, BodyHtml = "<p>Yo soy estudiante de español. Tú eres mi profesor favorito.</p>" });
            module.Lectures.Add(new Lecture { Title = "Empty", SortOrder = 2 });
            db.Courses.Add(new Course { LanguageId = languageId, Title = "C", Modules = { module } });
            await db.SaveChangesAsync();
            lectureId = module.Lectures.First(l => l.Title == "With text").Id;
            emptyLectureId = module.Lectures.First(l => l.Title == "Empty").Id;

            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            Assert.True((await ai.RequestQuestionsAsync(new QuestionsAiInput { LectureId = emptyLectureId })).IsFailure);
            Assert.True((await ai.RequestQuestionsAsync(new QuestionsAiInput
            {
                LectureId = lectureId, Count = 4, Types = [QuestionType.MultipleChoice, QuestionType.FillBlank, QuestionType.TrueFalse]
            })).IsSuccess);
            Assert.True((await ai.RequestPostsAsync(new PostsAiInput
            {
                LanguageId = languageId, PostType = PostType.WordOfTheDay, Month = new DateTime(2027, 2, 1), Count = 5
            })).IsSuccess);
            Assert.True((await ai.RequestPostsAsync(new PostsAiInput { LanguageId = languageId, PostType = PostType.Video })).IsFailure);

            // Lecture assist returns sanitized text without saving it (FR-A43).
            var assisted = await ai.AssistAsync(lectureId, AssistAction.Simplify, "<p>Hola</p><script>alert(1)</script>");
            Assert.True(assisted.IsSuccess);
            Assert.Contains("<p>Hola</p>", assisted.Value);
            Assert.DoesNotContain("<script", assisted.Value);
        }

        await RunJobsAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var questions = await db.Questions.Include(q => q.Options).Where(q => q.LectureId == lectureId).ToListAsync();
            Assert.Equal(4, questions.Count);
            Assert.All(questions, q => Assert.Equal(ContentSource.Ai, q.Source));
            Assert.Contains(questions, q => q.Type == QuestionType.FillBlank && q.Stem.Contains("___"));

            var posts = await db.Posts.Where(p => p.LanguageId == languageId).OrderBy(p => p.PublishAt).ToListAsync();
            Assert.Equal(5, posts.Count);
            Assert.All(posts, p => Assert.Equal(ContentStatus.Draft, p.Status));
            Assert.Equal(new DateTimeOffset(2027, 2, 1, 6, 0, 0, TimeSpan.Zero), posts[0].PublishAt);
            Assert.Equal(new DateTimeOffset(2027, 2, 5, 6, 0, 0, TimeSpan.Zero), posts[4].PublishAt);
        }
    }

    [Fact]
    public async Task Writing_is_pre_graded_confident_answers_are_applied_the_rest_wait_for_a_reviewer()
    {
        var email = $"ai-{Guid.NewGuid():N}@test.local";
        var client = factory.CreateClient();
        await client.PostJsonAsync("/api/v1/auth/register", new RegisterRequest("AI Learner", email, "Learner123", null, "UTC", "en"));

        int attemptId, longId, shortId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            var languageId = await NewLanguageAsync();
            var course = new Course { LanguageId = languageId, Title = "W", Status = ContentStatus.Published };
            db.Courses.Add(course);
            Question Writing(string stem) => new() { LanguageId = languageId, Type = QuestionType.Writing, Skill = Skill.Writing, Stem = stem, Points = 4, Status = ContentStatus.Published };
            var q1 = Writing("Describe your city.");
            var q2 = Writing("Say hello.");
            db.Questions.AddRange(q1, q2);
            await db.SaveChangesAsync();

            var attempt = new QuizAttempt
            {
                UserId = userId, CourseId = course.Id, Type = QuizType.Monthly, Title = "Exam", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                SubmittedAt = DateTimeOffset.UtcNow, PassMark = 50, Status = AttemptStatus.PendingReview, MaxScore = 8,
                Answers =
                {
                    new AttemptAnswer { QuestionId = q1.Id, SortOrder = 1, MaxScore = 4, NeedsReview = true, AnsweredAt = DateTimeOffset.UtcNow,
                        AnswerText = "Mi ciudad es grande y bonita, tiene muchos parques y museos." },
                    new AttemptAnswer { QuestionId = q2.Id, SortOrder = 2, MaxScore = 4, NeedsReview = true, AnsweredAt = DateTimeOffset.UtcNow, AnswerText = "Hola" }
                }
            };
            db.QuizAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
            longId = attempt.Answers.First(a => a.QuestionId == q1.Id).Id;
            shortId = attempt.Answers.First(a => a.QuestionId == q2.Id).Id;
            db.AiGenerationRequests.Add(new AiGenerationRequest
            {
                Type = AiRequestType.WritingGrading, Status = AiRequestStatus.Queued, AttemptId = attemptId, CourseId = course.Id, CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await RunJobsAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var confident = await db.AttemptAnswers.SingleAsync(a => a.Id == longId);
            Assert.False(confident.NeedsReview);           // offline grader: 11 words → confidence 0.9 ≥ 80%
            Assert.Equal(0.9, confident.AiConfidence);
            Assert.Equal(confident.AiScore, confident.Score);
            Assert.StartsWith("AI (", confident.ReviewedByEmail);

            var unsure = await db.AttemptAnswers.SingleAsync(a => a.Id == shortId);
            Assert.True(unsure.NeedsReview);               // low confidence: stays in the grading queue with the suggestion
            Assert.NotNull(unsure.AiScore);
            Assert.Equal(0m, unsure.Score);

            var attempt = await db.QuizAttempts.SingleAsync(a => a.Id == attemptId);
            Assert.Equal(AttemptStatus.PendingReview, attempt.Status);

            var grading = scope.ServiceProvider.GetRequiredService<Application.Features.Admin.IGradingService>();
            var item = (await grading.QueueAsync()).Single(i => i.AnswerId == shortId);
            Assert.NotNull(item.AiFeedback);
        }
    }
}

/// <summary>Separate database: spending is counted per month across the whole database.</summary>
public sealed class AiBudgetTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Jobs_pause_when_the_monthly_budget_is_used_up_and_resume_later()
    {
        int requestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            Assert.True((await settings.SetAsync(SettingKeys.AiMonthlyBudgetUsd, "1")).IsSuccess);
            db.AiUsageLogs.Add(new AiUsageLog { Feature = AiFeature.GrammarLecture, Provider = "DeepSeek", Model = "deepseek-chat", Cost = 1.5m, CreatedAt = DateTimeOffset.UtcNow });
            var language = new Language { Name = "Budget", NativeName = "b", IsoCode = "bx" + Guid.NewGuid().ToString("N")[..4] };
            db.Languages.Add(language);
            await db.SaveChangesAsync();

            var ai = scope.ServiceProvider.GetRequiredService<IAiGenerationService>();
            var budget = await scope.ServiceProvider.GetRequiredService<IAiContentService>().BudgetAsync();
            Assert.True(budget.IsExceeded);
            Assert.Equal(150, budget.Percent);
            requestId = (await ai.RequestRoadmapAsync(new RoadmapAiInput { LanguageId = language.Id, TotalHours = 4 })).Value;
        }

        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAiJobProcessor>().RunNextAsync("test"));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId);
            Assert.Equal(AiRequestStatus.Paused, request.Status);
            Assert.Contains("budget", request.Error);
            Assert.True(await db.AiUsageLogs.AnyAsync(l => l.Status == AiCallStatus.BudgetExceeded));

            // Raise the budget and resume.
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            await settings.SetAsync(SettingKeys.AiMonthlyBudgetUsd, "100");
            Assert.True((await scope.ServiceProvider.GetRequiredService<IAiGenerationService>().RetryAsync(requestId)).IsSuccess);
        }

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IAiJobProcessor>().RunNextAsync("test");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(AiRequestStatus.Completed, (await db.AiGenerationRequests.SingleAsync(r => r.Id == requestId)).Status);
        }
    }
}
