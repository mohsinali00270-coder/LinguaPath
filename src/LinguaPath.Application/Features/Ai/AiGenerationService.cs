using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;

namespace LinguaPath.Application.Features.Ai;

/// <summary>Admin side of AI: requests and approvals, lecture assist, usage and prompt templates.</summary>
public interface IAiGenerationService
{
    // Requests (FR-A21 .. FR-A24, FR-A31, FR-A42, FR-A44, FR-A52, FR-A62)
    Task<Result<int>> RequestNewLanguageAsync(RoadmapAiInput input, CancellationToken ct = default);
    Task<Result<int>> RequestRoadmapAsync(RoadmapAiInput input, CancellationToken ct = default);
    Task<Result<int>> RequestLectureAsync(int lectureId, string? notes, CancellationToken ct = default);
    Task<Result<int>> RequestAutoFillAsync(int courseId, string? notes, CancellationToken ct = default);
    Task<Result<int>> RequestQuestionsAsync(QuestionsAiInput input, CancellationToken ct = default);
    Task<Result<int>> RequestPostsAsync(PostsAiInput input, CancellationToken ct = default);
    /// <summary>Reading passages, writing tasks, speaking and dictation sentences for the practice library (Phase 6).</summary>
    Task<Result<int>> RequestPracticeAsync(PracticeAiInput input, CancellationToken ct = default);

    /// <summary>Super Admin decision on a whole-language job; notes are added to every prompt (FR-A24).</summary>
    Task<Result> DecideAsync(int id, bool approve, string? notes, CancellationToken ct = default);
    /// <summary>Roadmap reviewed in the builder: go on with lectures and questions (FR-A23).</summary>
    Task<Result> ApproveRoadmapAsync(int id, CancellationToken ct = default);
    Task<Result> CancelAsync(int id, CancellationToken ct = default);
    Task<Result> RetryAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<AiRequestListItem>> ListAsync(AiRequestStatus? status, AiRequestType? type, CancellationToken ct = default);
    Task<AiRequestDetail?> GetAsync(int id, CancellationToken ct = default);
    Task<AiRequestListItem?> RoadmapAwaitingReviewAsync(int courseId, CancellationToken ct = default);
    Task<AiRequestListItem?> ActiveForLectureAsync(int lectureId, CancellationToken ct = default);
    Task<AiProvenance?> ProvenanceAsync(ReviewEntityType type, int entityId, CancellationToken ct = default);
    Task<AiEstimator.Estimate> EstimateAsync(int totalHours, bool withContent, CancellationToken ct = default);

    /// <summary>FR-A43: rewrite text in the editor (not saved until the admin saves the lecture).</summary>
    Task<Result<string>> AssistAsync(int lectureId, AssistAction action, string html, CancellationToken ct = default);

    // Usage and prompts (SRS 5.3, 5.4, FR-A110)
    Task<AiUsageReport> UsageAsync(int days, CancellationToken ct = default);
    Task<IReadOnlyList<PromptTemplateRow>> TemplatesAsync(CancellationToken ct = default);
    Task<PromptEditorModel> TemplateEditorAsync(string key, int? versionId, CancellationToken ct = default);
    Task<Result<int>> SaveTemplateAsync(PromptTemplateInput input, CancellationToken ct = default);
    Task<Result> ActivateTemplateAsync(int versionId, CancellationToken ct = default);
}

internal sealed class AiGenerationService(
    IApplicationDbContext db,
    IAiContentService ai,
    IAiChatClientFactory clients,
    ICurrentUser currentUser,
    ISettingsService settings,
    IHtmlSanitizerService sanitizer,
    IAuditLogger audit,
    TimeProvider clock) : IAiGenerationService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // ================================================================== creating requests

    public async Task<Result<int>> RequestNewLanguageAsync(RoadmapAiInput input, CancellationToken ct = default)
    {
        var language = await db.Languages.FirstOrDefaultAsync(l => l.Id == input.LanguageId, ct);
        if (language is null) return Error.Validation("LanguageId", "Choose a language.");
        if (await db.Courses.AnyAsync(c => c.LanguageId == language.Id, ct))
            return Error.Validation("LanguageId", "This language already has courses. Use \"Generate roadmap with AI\" to add another one.");
        if (await db.AiGenerationRequests.AnyAsync(r => r.LanguageId == language.Id && r.Type == AiRequestType.NewLanguage &&
                r.Status != AiRequestStatus.Rejected && r.Status != AiRequestStatus.Cancelled && r.Status != AiRequestStatus.Failed, ct))
            return Error.Conflict("ai_request_exists", "There is already an AI request for this language.");
        var levels = CheckLevels(input);
        if (levels is not null) return levels;

        input.GenerateContent = true;
        var request = await NewRequestAsync(AiRequestType.NewLanguage, AiRequestStatus.PendingApproval, input, ct);
        request.LanguageId = language.Id;
        request.Stage = "roadmap";
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    public async Task<Result<int>> RequestRoadmapAsync(RoadmapAiInput input, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation("LanguageId", "Choose a language.");
        var levels = CheckLevels(input);
        if (levels is not null) return levels;
        var request = await NewRequestAsync(AiRequestType.Roadmap, AiRequestStatus.Queued, input, ct);
        request.LanguageId = input.LanguageId;
        request.Stage = "roadmap";
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    private static Error? CheckLevels(RoadmapAiInput input) =>
        input.EndLevel < input.StartLevel ? Error.Validation("EndLevel", "The end level must be the same as or above the start level.") : null;

    private async Task<AiGenerationRequest> NewRequestAsync(AiRequestType type, AiRequestStatus status, RoadmapAiInput input, CancellationToken ct)
    {
        var estimate = await EstimateAsync(input.TotalHours, input.GenerateContent, ct);
        return new AiGenerationRequest
        {
            Type = type,
            Status = status,
            Notes = Clean(input.Notes),
            ParametersJson = JsonSerializer.Serialize(new RoadmapParameters
            {
                CourseTitle = Clean(input.CourseTitle), StartLevel = input.StartLevel, EndLevel = input.EndLevel, TotalHours = input.TotalHours,
                Focus = Clean(input.Focus), NativeLanguage = input.NativeLanguage.Trim(), GenerateContent = input.GenerateContent
            }, Json),
            EstimatedModules = estimate.Modules,
            EstimatedLectures = estimate.Lectures,
            EstimatedQuestions = estimate.Questions,
            EstimatedCost = estimate.Cost
        };
    }

    public async Task<Result<int>> RequestLectureAsync(int lectureId, string? notes, CancellationToken ct = default)
    {
        var lecture = await db.Lectures.AsNoTracking().Where(l => l.Id == lectureId)
            .Select(l => new { l.Id, l.Status, l.Module.CourseId, l.Module.Course.LanguageId }).FirstOrDefaultAsync(ct);
        if (lecture is null) return Error.NotFound("Lecture");
        if (lecture.Status is ContentStatus.Published or ContentStatus.Archived)
            return Error.Validation("Status", "Unpublish the lecture first: AI drafts always go through review (BR-13).");
        if (await ActiveForLectureAsync(lectureId, ct) is not null)
            return Error.Conflict("ai_request_exists", "AI is already working on this lecture.");

        var request = new AiGenerationRequest
        {
            Type = AiRequestType.LectureContent, Status = AiRequestStatus.Queued, LanguageId = lecture.LanguageId, CourseId = lecture.CourseId,
            LectureId = lectureId, Notes = Clean(notes), ParametersJson = JsonSerializer.Serialize(new LectureParameters(), Json),
            EstimatedLectures = 1, ProgressTotal = 1, EstimatedCost = await LectureCostAsync(1, ct)
        };
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    public async Task<Result<int>> RequestAutoFillAsync(int courseId, string? notes, CancellationToken ct = default)
    {
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == courseId, ct);
        if (course is null) return Error.NotFound("Course");
        var empty = await db.Lectures.CountAsync(l => l.Module.CourseId == courseId && (l.BodyHtml == null || l.BodyHtml == "") &&
                                                     l.Status != ContentStatus.Archived, ct);
        if (empty == 0) return Error.Validation("CourseId", "Every lecture of this course already has text.");
        if (await db.AiGenerationRequests.AnyAsync(r => r.CourseId == courseId && r.Type == AiRequestType.AutoFillLectures &&
                (r.Status == AiRequestStatus.Queued || r.Status == AiRequestStatus.Running), ct))
            return Error.Conflict("ai_request_exists", "AI is already filling this course.");

        var request = new AiGenerationRequest
        {
            Type = AiRequestType.AutoFillLectures, Status = AiRequestStatus.Queued, LanguageId = course.LanguageId, CourseId = courseId,
            Stage = "content", Notes = Clean(notes), ParametersJson = JsonSerializer.Serialize(new LectureParameters(), Json),
            EstimatedLectures = empty, ProgressTotal = empty, EstimatedCost = await LectureCostAsync(empty, ct)
        };
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    public async Task<Result<int>> RequestQuestionsAsync(QuestionsAiInput input, CancellationToken ct = default)
    {
        var lecture = await db.Lectures.AsNoTracking().Where(l => l.Id == input.LectureId)
            .Select(l => new { l.Id, l.BodyHtml, l.Module.CourseId, l.Module.Course.LanguageId }).FirstOrDefaultAsync(ct);
        if (lecture is null) return Error.Validation("LectureId", "Choose a lecture.");
        if (string.IsNullOrWhiteSpace(lecture.BodyHtml))
            return Error.Validation("LectureId", "This lecture has no text yet. Questions are written only from the lecture text.");
        var types = input.Types.Where(t => t is not (QuestionType.Writing or QuestionType.Speak)).Distinct().ToList();
        if (types.Count == 0) return Error.Validation("Types", "Choose at least one question type (writing and speaking are written by hand).");

        var request = new AiGenerationRequest
        {
            Type = AiRequestType.Questions, Status = AiRequestStatus.Queued, LanguageId = lecture.LanguageId, CourseId = lecture.CourseId,
            LectureId = lecture.Id, Notes = Clean(input.Notes), EstimatedQuestions = input.Count, ProgressTotal = 1,
            ParametersJson = JsonSerializer.Serialize(new QuestionParameters
            {
                Count = Math.Clamp(input.Count, 1, 30), Types = types,
                Difficulty = input.Difficulty is "easy" or "hard" ? input.Difficulty : "mixed"
            }, Json),
            EstimatedCost = await PriceAsync(2500, 300 * input.Count, ct)
        };
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    public async Task<Result<int>> RequestPostsAsync(PostsAiInput input, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation("LanguageId", "Choose a language.");
        if (input.PostType is PostType.Article or PostType.Video)
            return Error.Validation("PostType", "AI drafts tips, cultural notes and words of the day; article and video posts need a real link.");
        var month = new DateOnly(input.Month.Year, input.Month.Month, 1);
        var count = Math.Clamp(input.Count, 1, DateTime.DaysInMonth(month.Year, month.Month));
        var request = new AiGenerationRequest
        {
            Type = AiRequestType.Posts, Status = AiRequestStatus.Queued, LanguageId = input.LanguageId, Notes = Clean(input.Notes),
            ProgressTotal = 1,
            ParametersJson = JsonSerializer.Serialize(new PostParameters
            {
                PostType = input.PostType, Level = input.Level, Month = month, Count = count, NativeLanguage = input.NativeLanguage.Trim()
            }, Json),
            EstimatedCost = await PriceAsync(500, 120 * count, ct)
        };
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    public async Task<Result<int>> RequestPracticeAsync(PracticeAiInput input, CancellationToken ct = default)
    {
        if (!await db.Languages.AnyAsync(l => l.Id == input.LanguageId, ct)) return Error.Validation("LanguageId", "Choose a language.");
        if (input.Type == PracticeType.Conversation) return Error.Validation("Type", "Conversation needs no library items.");
        var count = Math.Clamp(input.Count, 1, 20);
        var perItem = input.Type == PracticeType.Read ? 600 : 80;
        var request = new AiGenerationRequest
        {
            Type = AiRequestType.PracticeItems, Status = AiRequestStatus.Queued, LanguageId = input.LanguageId, Notes = Clean(input.Notes),
            ProgressTotal = 1,
            ParametersJson = JsonSerializer.Serialize(new PracticeParameters
            {
                Type = input.Type, Level = input.Level, Count = count, Topic = Clean(input.Topic), NativeLanguage = input.NativeLanguage.Trim()
            }, Json),
            EstimatedCost = await PriceAsync(600, perItem * count, ct)
        };
        await SaveNewAsync(request, ct);
        return request.Id;
    }

    private async Task SaveNewAsync(AiGenerationRequest request, CancellationToken ct)
    {
        request.RequestedBy = currentUser.UserId;
        request.RequestedByEmail = currentUser.Email;
        request.CreatedAt = clock.GetUtcNow();
        db.AiGenerationRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Create", "AiGenerationRequest", request.Id.ToString(), null, new { request.Type, request.EstimatedCost }, ct);
    }

    // ================================================================== decisions

    public async Task<Result> DecideAsync(int id, bool approve, string? notes, CancellationToken ct = default)
    {
        var request = await db.AiGenerationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Error.NotFound("AI request");
        if (request.Status != AiRequestStatus.PendingApproval) return Error.Conflict("ai_not_pending", "This request is not waiting for approval.");
        if (!approve && string.IsNullOrWhiteSpace(notes)) return Error.Validation("Notes", "Say why it is rejected.");

        request.Status = approve ? AiRequestStatus.Queued : AiRequestStatus.Rejected;
        request.DecidedBy = currentUser.UserId;
        request.DecidedByEmail = currentUser.Email;
        request.DecidedAt = clock.GetUtcNow();
        request.DecisionComment = Clean(notes);
        if (approve && !string.IsNullOrWhiteSpace(notes))
            request.Notes = string.IsNullOrWhiteSpace(request.Notes) ? notes.Trim() : $"{request.Notes}\n{notes.Trim()}";
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(approve ? "Approve" : "Reject", "AiGenerationRequest", id.ToString(), null, new { notes }, ct);
        return Result.Success();
    }

    public async Task<Result> ApproveRoadmapAsync(int id, CancellationToken ct = default)
    {
        var request = await db.AiGenerationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Error.NotFound("AI request");
        if (request.Status != AiRequestStatus.AwaitingRoadmapReview)
            return Error.Conflict("ai_not_awaiting", "This request isn't waiting for a roadmap review.");
        var lectures = await db.Lectures.CountAsync(l => l.Module.CourseId == request.CourseId && (l.BodyHtml == null || l.BodyHtml == ""), ct);
        request.Status = AiRequestStatus.Queued;
        request.Stage = "content";
        request.ProgressDone = 0;
        request.ProgressTotal = lectures;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("ApproveRoadmap", "AiGenerationRequest", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> CancelAsync(int id, CancellationToken ct = default)
    {
        var request = await db.AiGenerationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Error.NotFound("AI request");
        if (request.Status is AiRequestStatus.Completed or AiRequestStatus.Rejected or AiRequestStatus.Cancelled)
            return Error.Conflict("ai_closed", "This request is already closed.");
        request.Status = AiRequestStatus.Cancelled;
        request.CompletedAt = clock.GetUtcNow();
        request.LockedBy = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Cancel", "AiGenerationRequest", id.ToString(), ct: ct);
        return Result.Success();
    }

    public async Task<Result> RetryAsync(int id, CancellationToken ct = default)
    {
        var request = await db.AiGenerationRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Error.NotFound("AI request");
        if (request.Status is not (AiRequestStatus.Failed or AiRequestStatus.Paused))
            return Error.Conflict("ai_not_retryable", "Only failed or paused requests can be resumed.");
        request.Status = AiRequestStatus.Queued;
        request.Error = null;
        request.LockedBy = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Retry", "AiGenerationRequest", id.ToString(), ct: ct);
        return Result.Success();
    }

    // ================================================================== reading

    private IQueryable<AiGenerationRequest> Requests() => db.AiGenerationRequests.AsNoTracking();

    private static AiRequestListItem ToItem(AiGenerationRequest r, string? language, string? course, string? lecture) => new(
        r.Id, r.Type, r.Status, Describe(r, language, course, lecture), language, course, r.CourseId, r.ProgressDone, r.ProgressTotal,
        r.EstimatedCost, r.ActualCost, r.RequestedByEmail, r.CreatedAt, r.Error);

    private static string Describe(AiGenerationRequest r, string? language, string? course, string? lecture) => r.Type switch
    {
        AiRequestType.NewLanguage => $"New language: {language}",
        AiRequestType.Roadmap => $"Roadmap: {course ?? language}",
        AiRequestType.LectureContent => $"Lecture: {lecture}",
        AiRequestType.AutoFillLectures => $"Fill empty lectures: {course}",
        AiRequestType.Questions => $"Questions: {lecture}",
        AiRequestType.Posts => $"Posts: {language}",
        AiRequestType.WritingGrading => $"Writing pre-grading (attempt #{r.AttemptId})",
        _ => r.Type.ToString()
    };

    public async Task<IReadOnlyList<AiRequestListItem>> ListAsync(AiRequestStatus? status, AiRequestType? type, CancellationToken ct = default)
    {
        var q = Requests();
        if (status is { } s) q = q.Where(r => r.Status == s);
        if (type is { } t) q = q.Where(r => r.Type == t);
        var rows = await q.OrderByDescending(r => r.Id).Take(200)
            .Select(r => new { R = r, Language = r.Language != null ? r.Language.Name : null, Course = r.Course != null ? r.Course.Title : null })
            .ToListAsync(ct);
        var lectureIds = rows.Where(r => r.R.LectureId != null).Select(r => r.R.LectureId!.Value).Distinct().ToList();
        var lectures = await db.Lectures.AsNoTracking().IgnoreQueryFilters().Where(l => lectureIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title }).ToListAsync(ct);
        return rows.Select(r => ToItem(r.R, r.Language, r.Course, lectures.FirstOrDefault(l => l.Id == r.R.LectureId)?.Title)).ToList();
    }

    public async Task<AiRequestDetail?> GetAsync(int id, CancellationToken ct = default)
    {
        var row = await Requests().Where(r => r.Id == id)
            .Select(r => new { R = r, Language = r.Language != null ? r.Language.Name : null, Course = r.Course != null ? r.Course.Title : null })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var r = row.R;
        var lectureTitle = r.LectureId is { } lid
            ? await db.Lectures.AsNoTracking().IgnoreQueryFilters().Where(l => l.Id == lid).Select(l => l.Title).FirstOrDefaultAsync(ct)
            : null;

        var items = await db.AiGeneratedItems.AsNoTracking().Where(i => i.RequestId == id).OrderBy(i => i.Id).Take(500).ToListAsync(ct);
        var rowsOut = new List<AiItemRow>();
        var lectureIds = items.Where(i => i.EntityType == ReviewEntityType.Lecture).Select(i => i.EntityId).ToList();
        var questionIds = items.Where(i => i.EntityType == ReviewEntityType.Question).Select(i => i.EntityId).ToList();
        var postIds = items.Where(i => i.EntityType == ReviewEntityType.Post).Select(i => i.EntityId).ToList();
        var courseIds = items.Where(i => i.EntityType == ReviewEntityType.Course).Select(i => i.EntityId).ToList();
        var practiceIds = items.Where(i => i.EntityType == ReviewEntityType.PracticeItem).Select(i => i.EntityId).ToList();
        var practice = await db.PracticeItems.AsNoTracking().Where(p => practiceIds.Contains(p.Id)).Select(p => new { p.Id, p.Title, p.Status }).ToListAsync(ct);
        var lectures = await db.Lectures.AsNoTracking().Where(l => lectureIds.Contains(l.Id)).Select(l => new { l.Id, l.Title, l.Status }).ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking().Where(q => questionIds.Contains(q.Id) && !q.IsDeleted)
            .Select(q => new { q.Id, q.Stem, q.Status }).ToListAsync(ct);
        var posts = await db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)).Select(p => new { p.Id, p.Title, p.Status }).ToListAsync(ct);
        var courses = await db.Courses.AsNoTracking().Where(c => courseIds.Contains(c.Id)).Select(c => new { c.Id, c.Title, c.Status }).ToListAsync(ct);
        foreach (var i in items)
        {
            (string Title, ContentStatus Status)? found = i.EntityType switch
            {
                ReviewEntityType.Lecture => lectures.Where(x => x.Id == i.EntityId).Select(x => (x.Title, x.Status)).FirstOrDefault(),
                ReviewEntityType.Question => questions.Where(x => x.Id == i.EntityId).Select(x => (x.Stem, x.Status)).FirstOrDefault(),
                ReviewEntityType.Post => posts.Where(x => x.Id == i.EntityId).Select(x => (x.Title, x.Status)).FirstOrDefault(),
                ReviewEntityType.Course => courses.Where(x => x.Id == i.EntityId).Select(x => (x.Title, x.Status)).FirstOrDefault(),
                ReviewEntityType.PracticeItem => practice.Where(x => x.Id == i.EntityId).Select(x => (x.Title, x.Status)).FirstOrDefault(),
                _ => null
            };
            if (found is { Title: not null } f)
                rowsOut.Add(new AiItemRow(i.EntityType, i.EntityId, f.Title, f.Status, i.NeedsNativeCheck, i.Model, i.CreatedAt));
            else
                rowsOut.Add(new AiItemRow(i.EntityType, i.EntityId, "(deleted)", null, i.NeedsNativeCheck, i.Model, i.CreatedAt));
        }

        var usage = await db.AiUsageLogs.AsNoTracking().Where(l => l.RequestId == id)
            .Select(l => new { l.InputTokens, l.OutputTokens }).ToListAsync(ct);
        return new AiRequestDetail(ToItem(r, row.Language, row.Course, lectureTitle), Pretty(r.ParametersJson), r.Notes, r.Stage,
            r.EstimatedModules, r.EstimatedLectures, r.EstimatedQuestions, r.Summary, r.DecisionComment, r.DecidedByEmail, r.DecidedAt,
            r.StartedAt, r.CompletedAt, r.LectureId, lectureTitle, r.AttemptId, rowsOut, usage.Count,
            usage.Sum(u => u.InputTokens), usage.Sum(u => u.OutputTokens));
    }

    public async Task<AiRequestListItem?> RoadmapAwaitingReviewAsync(int courseId, CancellationToken ct = default)
    {
        var r = await Requests().Where(x => x.CourseId == courseId && x.Status == AiRequestStatus.AwaitingRoadmapReview)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return r is null ? null : ToItem(r, null, null, null);
    }

    public async Task<AiRequestListItem?> ActiveForLectureAsync(int lectureId, CancellationToken ct = default)
    {
        var r = await Requests().Where(x => x.LectureId == lectureId && x.Type == AiRequestType.LectureContent &&
                                            (x.Status == AiRequestStatus.Queued || x.Status == AiRequestStatus.Running))
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return r is null ? null : ToItem(r, null, null, null);
    }

    public async Task<AiProvenance?> ProvenanceAsync(ReviewEntityType type, int entityId, CancellationToken ct = default)
    {
        var i = await db.AiGeneratedItems.AsNoTracking().Where(x => x.EntityType == type && x.EntityId == entityId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return i is null ? null : new AiProvenance(i.RequestId, i.Feature, i.Model, i.Prompt, i.NeedsNativeCheck, i.CreatedAt);
    }

    public async Task<AiEstimator.Estimate> EstimateAsync(int totalHours, bool withContent, CancellationToken ct = default) =>
        AiEstimator.ForCourse(totalHours, withContent, await settings.GetIntAsync(SettingKeys.AiQuestionsPerLecture, 5, ct),
            await DecimalAsync(SettingKeys.AiInputPricePerMillion, 0.27m, ct), await DecimalAsync(SettingKeys.AiOutputPricePerMillion, 1.10m, ct));

    // ================================================================== assist (FR-A43)

    public async Task<Result<string>> AssistAsync(int lectureId, AssistAction action, string html, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > 30000) return Error.Validation("Html", "Write some text first (up to 30,000 characters).");
        var lecture = await db.Lectures.AsNoTracking().Where(l => l.Id == lectureId)
            .Select(l => new { l.Level, Language = l.Module.Course.Language.Name }).FirstOrDefaultAsync(ct);
        if (lecture is null) return Error.NotFound("Lecture");
        var result = await ai.AssistAsync(lecture.Language, lecture.Level, action, html, "English", new AiCallContext(currentUser.UserId, null), ct);
        if (result.IsFailure) return result.Error!;
        return sanitizer.Sanitize(result.Value.Value.Html);
    }

    // ================================================================== usage (5.4)

    public async Task<AiUsageReport> UsageAsync(int days, CancellationToken ct = default)
    {
        var since = clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 366));
        var logs = await db.AiUsageLogs.AsNoTracking().Where(l => l.CreatedAt >= since).OrderByDescending(l => l.Id).Take(5000).ToListAsync(ct);
        var byFeature = logs.GroupBy(l => l.Feature).OrderBy(g => g.Key)
            .Select(g => new AiFeatureUsage(g.Key, g.Count(), g.Count(l => l.Status != AiCallStatus.Success),
                g.Sum(l => (long)l.InputTokens), g.Sum(l => (long)l.OutputTokens), g.Sum(l => l.Cost))).ToList();
        var byDay = logs.GroupBy(l => DateOnly.FromDateTime(l.CreatedAt.UtcDateTime)).OrderBy(g => g.Key)
            .Select(g => new AiDayUsage(g.Key, g.Sum(l => l.Cost), g.Count())).ToList();
        var recent = logs.Take(50).Select(l => new AiUsageRow(l.CreatedAt, l.Feature, l.Provider, l.Model, l.InputTokens, l.OutputTokens,
            l.Cost, l.DurationMs, l.Status, l.Error, l.RequestId)).ToList();

        string provider, model;
        try
        {
            (_, provider, model) = await clients.CreateAsync(ct);
        }
        catch (AiProviderException ex)
        {
            provider = "not configured";
            model = ex.Message;
        }
        return new AiUsageReport(await ai.BudgetAsync(ct), provider, model, byFeature, byDay, recent);
    }

    // ================================================================== prompt templates (SRS 5.3)

    public async Task<IReadOnlyList<PromptTemplateRow>> TemplatesAsync(CancellationToken ct = default)
    {
        var stored = await db.AiPromptTemplates.AsNoTracking().ToListAsync(ct);
        return AiPrompts.Defaults.Select(d =>
        {
            var versions = stored.Where(t => t.Key == d.Key).ToList();
            var active = versions.Where(v => v.IsActive).MaxBy(v => v.Version);
            return new PromptTemplateRow(d.Key, active?.Version ?? 0, versions.Count, active is null, active?.Notes ?? d.Notes,
                versions.Count == 0 ? null : versions.Max(v => v.CreatedAt));
        }).ToList();
    }

    public async Task<PromptEditorModel> TemplateEditorAsync(string key, int? versionId, CancellationToken ct = default)
    {
        var builtIn = AiPrompts.Defaults.FirstOrDefault(d => d.Key == key) ?? throw new KeyNotFoundException(key);
        var versions = await db.AiPromptTemplates.AsNoTracking().Where(t => t.Key == key).OrderByDescending(t => t.Version).ToListAsync(ct);
        var shown = versionId is { } vid ? versions.FirstOrDefault(v => v.Id == vid) : versions.FirstOrDefault(v => v.IsActive);
        var input = shown is null
            ? new PromptTemplateInput { Key = key, SystemText = builtIn.System, PromptText = builtIn.Prompt, Temperature = builtIn.Temperature }
            : new PromptTemplateInput { Key = key, SystemText = shown.SystemText, PromptText = shown.PromptText, Model = shown.Model, Temperature = shown.Temperature };
        var placeholders = Regex.Matches(builtIn.System + builtIn.Prompt, @"\{([a-zA-Z]+)\}").Select(m => "{" + m.Groups[1].Value + "}").Distinct().ToList();
        return new PromptEditorModel(input, versions.FirstOrDefault(v => v.IsActive)?.Version ?? 0,
            versions.Select(v => new PromptVersionRow(v.Id, v.Version, v.IsActive, v.Model, v.Temperature, v.Notes, v.CreatedByEmail, v.CreatedAt)).ToList(),
            placeholders);
    }

    public async Task<Result<int>> SaveTemplateAsync(PromptTemplateInput input, CancellationToken ct = default)
    {
        if (AiPrompts.Defaults.All(d => d.Key != input.Key)) return Error.NotFound("Prompt");
        var builtIn = AiPrompts.DefaultFor(input.Key);
        var required = Regex.Matches(builtIn.Prompt, @"\{(lectureText|answer|text)\}").Select(m => m.Value).Distinct().ToList();
        var missing = required.Where(r => !input.PromptText.Contains(r)).ToList();
        if (missing.Count > 0) return Error.Validation("PromptText", $"The prompt must keep {string.Join(", ", missing)}.");

        var versions = await db.AiPromptTemplates.Where(t => t.Key == input.Key).ToListAsync(ct);
        foreach (var v in versions) v.IsActive = false;
        var template = new AiPromptTemplate
        {
            Key = input.Key, Version = versions.Count == 0 ? 1 : versions.Max(v => v.Version) + 1, SystemText = input.SystemText.Trim(),
            PromptText = input.PromptText.Trim(), Model = Clean(input.Model), Temperature = Math.Clamp(input.Temperature, 0, 2),
            IsActive = true, Notes = Clean(input.Notes), CreatedAt = clock.GetUtcNow(), CreatedByEmail = currentUser.Email
        };
        db.AiPromptTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Update", "AiPromptTemplate", $"{template.Key} v{template.Version}", ct: ct);
        return template.Id;
    }

    public async Task<Result> ActivateTemplateAsync(int versionId, CancellationToken ct = default)
    {
        var template = await db.AiPromptTemplates.FirstOrDefaultAsync(t => t.Id == versionId, ct);
        if (template is null) return Error.NotFound("Prompt version");
        foreach (var v in await db.AiPromptTemplates.Where(t => t.Key == template.Key).ToListAsync(ct)) v.IsActive = v.Id == versionId;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Activate", "AiPromptTemplate", $"{template.Key} v{template.Version}", ct: ct);
        return Result.Success();
    }

    // ================================================================== helpers

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Pretty(string json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private async Task<decimal> DecimalAsync(string key, decimal fallback, CancellationToken ct) =>
        decimal.TryParse(await settings.GetAsync(key, ct), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private async Task<decimal> PriceAsync(int input, int output, CancellationToken ct) =>
        AiCost.For(input, output, await DecimalAsync(SettingKeys.AiInputPricePerMillion, 0.27m, ct),
            await DecimalAsync(SettingKeys.AiOutputPricePerMillion, 1.10m, ct));

    private Task<decimal> LectureCostAsync(int lectures, CancellationToken ct) => PriceAsync(900 * lectures, 3000 * lectures, ct);
}
