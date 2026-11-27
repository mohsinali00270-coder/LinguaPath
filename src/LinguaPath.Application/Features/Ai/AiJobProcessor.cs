using System.Globalization;
using System.Text.Json;
using LinguaPath.Application.Common;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Admin;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Constants;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinguaPath.Application.Features.Ai;

/// <summary>Runs queued AI jobs one at a time (SRS 5.3 "long jobs run in a background queue with progress").</summary>
public interface IAiJobProcessor
{
    /// <summary>Claims the oldest queued job and works on it. Returns false when there was nothing to do.</summary>
    Task<bool> RunNextAsync(string runnerId, CancellationToken ct = default);
}

internal sealed class AiJobProcessor(
    IApplicationDbContext db,
    IAiContentService ai,
    ILectureContentAdminService lectureEditor,
    IQuestionAdminService questionEditor,
    IPostAdminService postEditor,
    ISettingsService settings,
    ISpeechAssessor speech,
    IFileStorage storage,
    Progress.IProgressService progress,
    TimeProvider clock,
    ILogger<AiJobProcessor> logger) : IAiJobProcessor
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>Thrown inside a job to stop it with a status (budget used up, provider down…).</summary>
    private sealed class JobStop(AiRequestStatus status, string message) : Exception(message)
    {
        public AiRequestStatus Status { get; } = status;
    }

    public async Task<bool> RunNextAsync(string runnerId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // Queued jobs, plus "Running" ones whose runner died (lease expired).
        var request = await db.AiGenerationRequests
            .Where(r => r.Status == AiRequestStatus.Queued || (r.Status == AiRequestStatus.Running && r.LockedUntil != null && r.LockedUntil < now))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (request is null) return false;

        request.Status = AiRequestStatus.Running;
        request.LockedBy = runnerId;
        request.LockedUntil = now.Add(Lease);
        request.StartedAt ??= now;
        request.Attempts++;
        await db.SaveChangesAsync(ct);

        try
        {
            switch (request.Type)
            {
                case AiRequestType.NewLanguage or AiRequestType.Roadmap when request.Stage != "content":
                    await RoadmapStageAsync(request, ct);
                    break;
                case AiRequestType.NewLanguage or AiRequestType.Roadmap or AiRequestType.AutoFillLectures:
                    await ContentStageAsync(request, withQuestions: request.Type != AiRequestType.AutoFillLectures, ct);
                    break;
                case AiRequestType.LectureContent:
                    await SingleLectureAsync(request, ct);
                    break;
                case AiRequestType.Questions:
                    await QuestionsJobAsync(request, ct);
                    break;
                case AiRequestType.Posts:
                    await PostsJobAsync(request, ct);
                    break;
                case AiRequestType.PracticeItems:
                    await PracticeJobAsync(request, ct);
                    break;
                case AiRequestType.WritingGrading:
                    await WritingGradingAsync(request, ct);
                    break;
                default:
                    throw new JobStop(AiRequestStatus.Failed, "Unknown job type.");
            }
        }
        catch (JobStop stop)
        {
            request.Status = stop.Status;
            request.Error = stop.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI job {Id} failed", request.Id);
            request.Status = AiRequestStatus.Failed;
            request.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }

        request.LockedBy = null;
        request.LockedUntil = null;
        request.ActualCost = await CostOfAsync(request.Id, ct);
        if (request.Status is AiRequestStatus.Completed or AiRequestStatus.Failed) request.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ================================================================== roadmap (FR-A31, FR-A23 step 1)

    private async Task RoadmapStageAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var p = Params<RoadmapParameters>(request);
        var language = await db.Languages.FirstAsync(l => l.Id == request.LanguageId, ct);
        var outcome = Ok(await ai.RoadmapAsync(language.Name, p, request.Notes, Context(request), ct));
        var roadmap = outcome.Value;

        var course = new Course
        {
            LanguageId = language.Id,
            Title = Trim(p.CourseTitle ?? roadmap.Title ?? $"{language.Name} {p.StartLevel} → {p.EndLevel}", 150),
            Description = Trim(roadmap.Description, 2000),
            StartLevel = p.StartLevel,
            EndLevel = p.EndLevel,
            Status = ContentStatus.Draft,
            Source = ContentSource.Ai,
            SortOrder = await db.Courses.CountAsync(c => c.LanguageId == language.Id, ct) + 1
        };

        var month = 1;
        var moduleOrder = 0;
        foreach (var m in roadmap.Modules.Take(40))
        {
            month = Math.Clamp(Math.Max(month, m.Month), 1, 24); // months never go backwards
            var level = Clamp(AiOutputParser.Level(m.Level) ?? p.StartLevel, p.StartLevel, p.EndLevel);
            var module = new CourseModule { Title = Trim(m.Title, 150), Objective = Trim(m.Objective, 1000), Level = level, MonthNo = month, SortOrder = ++moduleOrder };
            var lectureOrder = 0;
            foreach (var l in m.Lectures.Take(20))
            {
                module.Lectures.Add(new Lecture
                {
                    Title = Trim(l.Title, 200), Objectives = Trim(l.Objective, 1000), Topic = Trim(l.GrammarPoint, 150), Level = level,
                    EstimatedMinutes = Math.Clamp(l.Minutes, 5, 60), IsCore = l.IsCore, SortOrder = ++lectureOrder,
                    Status = ContentStatus.Draft, Source = ContentSource.Ai
                });
            }
            course.Modules.Add(module);
        }
        db.Courses.Add(course);
        await db.SaveChangesAsync(ct);
        await RecordAsync(request, ReviewEntityType.Course, course.Id, AiFeature.Roadmap, outcome, language.IsoCode, ct);

        var lectures = course.Modules.Sum(m => m.Lectures.Count);
        request.CourseId = course.Id;
        request.Summary = $"Roadmap drafted: {course.Modules.Count} modules, {lectures} lectures. Review it in the course builder.";
        request.ProgressDone = request.ProgressTotal = 1;
        request.Status = p.GenerateContent ? AiRequestStatus.AwaitingRoadmapReview : AiRequestStatus.Completed;
    }

    // ================================================================== lectures (+ questions), module by module (FR-A23 step 2, FR-A44)

    private async Task ContentStageAsync(AiGenerationRequest request, bool withQuestions, CancellationToken ct)
    {
        var lectures = await db.Lectures.Where(l => l.Module.CourseId == request.CourseId && l.Status != ContentStatus.Archived)
            .OrderBy(l => l.Module.SortOrder).ThenBy(l => l.SortOrder).Select(l => l.Id).ToListAsync(ct);
        var perLecture = await settings.GetIntAsync(SettingKeys.AiQuestionsPerLecture, 5, ct);
        var p = Params<LectureParameters>(request);
        var native = request.Type == AiRequestType.AutoFillLectures ? p.NativeLanguage : Params<RoadmapParameters>(request).NativeLanguage;

        request.ProgressTotal = lectures.Count;
        var done = 0;
        var filled = 0;
        var questions = 0;
        foreach (var lectureId in lectures)
        {
            ct.ThrowIfCancellationRequested();
            if (await StoppedAsync(request, ct)) return;

            var lecture = await db.Lectures.AsNoTracking().FirstAsync(l => l.Id == lectureId, ct);
            // Resumable: lectures that already have text are skipped.
            if (string.IsNullOrWhiteSpace(lecture.BodyHtml))
            {
                await FillLectureAsync(request, lectureId, native, ct);
                filled++;
            }
            if (withQuestions && !await db.AiGeneratedItems.AnyAsync(i => i.RequestId == request.Id && i.EntityType == ReviewEntityType.Question &&
                    db.Questions.Any(q => q.Id == i.EntityId && q.LectureId == lectureId), ct))
            {
                questions += await GenerateQuestionsAsync(request, lectureId, new QuestionParameters { Count = perLecture }, ct);
            }

            request.ProgressDone = ++done;
            request.LockedUntil = clock.GetUtcNow().Add(Lease);
            await db.SaveChangesAsync(ct);
        }

        request.Summary = withQuestions
            ? $"Wrote {filled} lecture(s) and {questions} question(s). Everything is in Draft: send it for review."
            : $"Wrote {filled} lecture(s). They are in Draft: send them for review.";
        request.Status = AiRequestStatus.Completed;
    }

    private async Task SingleLectureAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var p = Params<LectureParameters>(request);
        await FillLectureAsync(request, request.LectureId!.Value, p.NativeLanguage, ct);
        request.ProgressDone = request.ProgressTotal = 1;
        request.Summary = "Lecture written. It is in Draft: check it and send it for review.";
        request.Status = AiRequestStatus.Completed;
    }

    /// <summary>FR-A42: explanation, rule table, 10 examples, common mistakes, summary; saved as a new Draft version.</summary>
    private async Task FillLectureAsync(AiGenerationRequest request, int lectureId, string nativeLanguage, CancellationToken ct)
    {
        var editor = await lectureEditor.GetEditorAsync(lectureId, ct) ?? throw new JobStop(AiRequestStatus.Failed, "The lecture was deleted.");
        if (editor.Status is ContentStatus.Published or ContentStatus.Archived)
            throw new JobStop(AiRequestStatus.Failed, $"\"{editor.Input.Title}\" is {editor.Status}; unpublish it before generating new text.");

        var outcome = Ok(await ai.LectureAsync(editor.LanguageName, editor.Input.Level, editor.Input.Title, editor.Input.Topic,
            editor.Input.Objectives, nativeLanguage, request.Notes, Context(request), ct));
        var draft = outcome.Value;

        var input = editor.Input;
        input.BodyHtml = draft.Html;
        if (string.IsNullOrWhiteSpace(input.Objectives)) input.Objectives = Trim(draft.Objectives, 1000);
        input.Examples = draft.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Target)).Take(20)
            .Select(e => new ExampleInput { TargetText = Trim(e.Target, 500)!, Translation = Trim(e.Translation, 500), Note = Trim(e.Note, 300) }).ToList();
        input.Vocabulary = draft.Vocabulary.Where(v => !string.IsNullOrWhiteSpace(v.Word) && !string.IsNullOrWhiteSpace(v.Meaning)).Take(30)
            .Select(v => new VocabularyInput { Word = Trim(v.Word, 100)!, Meaning = Trim(v.Meaning, 300)!, Pronunciation = Trim(v.Pronunciation, 100), PartOfSpeech = Trim(v.PartOfSpeech, 50) })
            .ToList();
        input.ChangeNote = $"AI draft ({outcome.Model})";

        var saved = await lectureEditor.SaveAsync(input, ct);
        if (saved.IsFailure) throw new JobStop(AiRequestStatus.Failed, $"Couldn't save \"{input.Title}\": {saved.Error!.Message}");

        var lecture = await db.Lectures.FirstAsync(l => l.Id == lectureId, ct);
        lecture.Source = ContentSource.Ai;
        await db.SaveChangesAsync(ct);
        await RecordAsync(request, ReviewEntityType.Lecture, lectureId, AiFeature.GrammarLecture, outcome, editor.LanguageIsoCode, ct);
    }

    // ================================================================== questions (FR-A62)

    private async Task QuestionsJobAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var count = await GenerateQuestionsAsync(request, request.LectureId!.Value, Params<QuestionParameters>(request), ct);
        request.ProgressDone = request.ProgressTotal = 1;
        request.Summary = $"{count} question(s) drafted from the lecture. Edit, send for review or delete each one.";
        request.Status = AiRequestStatus.Completed;
    }

    /// <summary>Grounded in the lecture text only; every question keeps the sentence it came from. Invalid ones are skipped.</summary>
    private async Task<int> GenerateQuestionsAsync(AiGenerationRequest request, int lectureId, QuestionParameters p, CancellationToken ct)
    {
        var lecture = await db.Lectures.AsNoTracking().Include(l => l.Examples).Where(l => l.Id == lectureId)
            .Select(l => new
            {
                l.Id, l.BodyHtml, l.Level, l.Title, Examples = l.Examples.OrderBy(e => e.SortOrder).Select(e => new { e.TargetText, e.Translation }).ToList(),
                LanguageId = l.Module.Course.LanguageId, Language = l.Module.Course.Language.Name, Iso = l.Module.Course.Language.IsoCode
            }).FirstOrDefaultAsync(ct) ?? throw new JobStop(AiRequestStatus.Failed, "The lecture was deleted.");
        if (string.IsNullOrWhiteSpace(lecture.BodyHtml)) return 0;

        var text = AiPrompts.LectureText(lecture.BodyHtml, lecture.Examples.Select(e => (e.TargetText, e.Translation)));
        var outcome = Ok(await ai.QuestionsAsync(lecture.Language, lecture.Level, text, p, Context(request), ct));

        var created = 0;
        foreach (var draft in outcome.Value.Questions.Take(Math.Max(p.Count, 1) + 2))
        {
            var input = AiQuestionMapper.ToInput(draft, lecture.LanguageId, lecture.Id, lecture.Level, p.Types);
            if (input is null || questionEditor.Validate(input).Count > 0) continue;
            var saved = await questionEditor.SaveAsync(input, ct);
            if (saved.IsFailure) continue;

            var question = await db.Questions.FirstAsync(q => q.Id == saved.Value, ct);
            question.Source = ContentSource.Ai;
            question.SourceText = Trim(draft.SourceSentence, 1000);
            await db.SaveChangesAsync(ct);
            await RecordAsync(request, ReviewEntityType.Question, question.Id, AiFeature.QuizFromLecture, outcome, lecture.Iso, ct);
            if (++created >= p.Count) break;
        }
        return created;
    }

    // ================================================================== posts (FR-A52)

    private async Task PostsJobAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var p = Params<PostParameters>(request);
        var language = await db.Languages.AsNoTracking().FirstAsync(l => l.Id == request.LanguageId, ct);
        var outcome = Ok(await ai.PostsAsync(language.Name, p, request.Notes, Context(request), ct));

        var day = 0;
        foreach (var draft in outcome.Value.Posts.Take(p.Count))
        {
            var publishAt = p.Month.AddDays(day++).ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc);
            var saved = await postEditor.SaveAsync(new PostInput
            {
                LanguageId = language.Id, PostType = p.PostType, Title = Trim(draft.Title, 200)!, Body = Trim(draft.Body, 4000),
                Level = p.Level, Tags = Trim(string.Join(", ", draft.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(3)), 300),
                PublishAtUtc = publishAt
            }, ct);
            if (saved.IsFailure) continue;
            var post = await db.Posts.FirstAsync(x => x.Id == saved.Value, ct);
            post.Source = ContentSource.Ai;
            await db.SaveChangesAsync(ct);
            await RecordAsync(request, ReviewEntityType.Post, post.Id, AiFeature.Posts, outcome, language.IsoCode, ct);
        }
        request.ProgressDone = request.ProgressTotal = 1;
        request.Summary = $"{day} post(s) drafted and scheduled one a day from {p.Month:d MMM}. Send them for review to publish.";
        request.Status = AiRequestStatus.Completed;
    }

    // ================================================================== practice library (Phase 6)

    private async Task PracticeJobAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var p = Params<PracticeParameters>(request);
        var language = await db.Languages.AsNoTracking().FirstAsync(l => l.Id == request.LanguageId, ct);
        var outcome = Ok(await ai.PracticeItemsAsync(language.Name, p, request.Notes, Context(request), ct));

        var saved = 0;
        foreach (var draft in outcome.Value.Items.Take(p.Count))
        {
            var text = Trim(draft.Text, p.Type == PracticeType.Speak ? 300 : 6000);
            if (text is null) continue;
            var questions = p.Type != PracticeType.Read ? null : ReadingQuestions.Serialize(draft.Questions
                .Where(q => q.Options.Count >= 2 && q.Correct >= 0 && q.Correct < q.Options.Count)
                .Take(10)
                .Select(q => new ReadingQuestions.Stored
                {
                    Question = Trim(q.Question, 500) ?? "", Options = q.Options.Select(o => Trim(o, 200) ?? "").Where(o => o.Length > 0).ToList(),
                    Correct = q.Correct, Explanation = Trim(q.Explanation, 500)
                }));
            var item = new PracticeItem
            {
                LanguageId = language.Id, Type = p.Type, Level = p.Level, Title = Trim(draft.Title, 200) ?? $"{p.Type} {saved + 1}",
                Topic = Trim(p.Topic, 100), Text = text, Translation = Trim(draft.Translation, 6000), QuestionsJson = questions,
                Status = ContentStatus.Draft, Source = ContentSource.Ai
            };
            db.PracticeItems.Add(item);
            await db.SaveChangesAsync(ct);
            await RecordAsync(request, ReviewEntityType.PracticeItem, item.Id, AiFeature.PracticeItems, outcome, language.IsoCode, ct);
            saved++;
        }
        request.ProgressDone = request.ProgressTotal = 1;
        request.Summary = $"{saved} {p.Type.ToString().ToLowerInvariant()} item(s) drafted for the practice library. Send them for review to publish.";
        request.Status = AiRequestStatus.Completed;
    }

    // ================================================================== writing and speaking pre-grading (FR-L63)

    private async Task WritingGradingAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var attempt = await db.QuizAttempts.Include(a => a.Answers).ThenInclude(a => a.Question)
            .FirstOrDefaultAsync(a => a.Id == request.AttemptId, ct) ?? throw new JobStop(AiRequestStatus.Failed, "The attempt was deleted.");
        var language = await db.Courses.AsNoTracking().Where(c => c.Id == attempt.CourseId).Select(c => c.Language).FirstAsync(ct);
        var auto = await settings.GetBoolAsync(SettingKeys.AiAutoGradeWriting, true, ct);
        var autoSpeaking = await settings.GetBoolAsync(SettingKeys.PracticeAutoGradeSpeaking, true, ct);
        var threshold = await settings.GetIntAsync(SettingKeys.AiGradingConfidence, 80, ct);

        var writing = attempt.Answers.Where(a => a.NeedsReview && a.Question.Type == QuestionType.Writing && a.AiScore is null &&
                                                 !string.IsNullOrWhiteSpace(a.AnswerText)).ToList();
        var speaking = attempt.Answers.Where(a => a.NeedsReview && a.Question.Type == QuestionType.Speak && a.AiScore is null &&
                                                  !string.IsNullOrWhiteSpace(a.AudioUrl) && !string.IsNullOrWhiteSpace(a.Question.Prompt)).ToList();
        request.ProgressTotal = writing.Count + speaking.Count;
        request.ProgressDone = 0;
        var graded = 0;
        var skipped = 0;

        foreach (var answer in writing)
        {
            var outcome = Ok(await ai.GradeWritingAsync(language.Name, answer.Question.Level, answer.Question.Stem, answer.AnswerText!, "English",
                Context(request), ct));
            var g = outcome.Value;
            answer.AiScore = Math.Round((decimal)g.Score / 100m * answer.MaxScore, 1);
            answer.AiConfidence = Math.Round(g.Confidence, 2);
            answer.AiFeedback = Trim(AiFeedbackText(g), 2000);
            if (auto && g.Confidence * 100 >= threshold && AutoGrade(attempt, answer, g.Feedback, $"AI ({outcome.Model})")) graded++;
            request.ProgressDone++;
        }

        // Speaking: pronunciation scoring against the sentence the learner was asked to say.
        foreach (var answer in speaking)
        {
            var audio = await storage.ReadAsync(answer.AudioUrl!, PracticeService.MaxAudioBytes, ct);
            var wav = audio is null ? null : WavAudio.ToSpeechFormat(audio);
            if (wav is null)
            {
                // Not WAV (older app versions recorded m4a) or the file is gone: a reviewer listens as before.
                skipped++;
                request.ProgressDone++;
                continue;
            }

            PronunciationResult r;
            try
            {
                r = await speech.AssessAsync(wav, PracticeService.Locale(language), answer.Question.Prompt!, ct);
            }
            catch (LanguageServiceException ex)
            {
                logger.LogWarning(ex, "Speaking pre-grade skipped for answer {AnswerId}", answer.Id);
                skipped++;
                request.ProgressDone++;
                continue;
            }

            var score = PracticeRules.SpeakScore(r);
            var confidence = PracticeRules.SpeakConfidence(r);
            answer.AiScore = Math.Round(score / 100m * answer.MaxScore, 1);
            answer.AiConfidence = Math.Round(confidence, 2);
            answer.AiFeedback = Trim(SpeakingFeedbackText(r, score), 2000);
            var label = r.IsSample ? "sample speech scores" : "pronunciation scoring";
            if (autoSpeaking && confidence * 100 >= threshold &&
                AutoGrade(attempt, answer, $"Pronunciation {score}/100.", $"Speech ({label})")) graded++;
            request.ProgressDone++;
        }

        if (graded > 0 && attempt.Status == AttemptStatus.PendingReview) AttemptScoring.Recalculate(attempt);
        if (graded > 0 && attempt.Status == AttemptStatus.Graded)
        {
            await db.SaveChangesAsync(ct);
            await progress.OnAttemptClosedAsync(attempt.Id, ct);
        }

        var total = writing.Count + speaking.Count;
        request.Summary = $"{total} answer(s) pre-graded ({writing.Count} writing, {speaking.Count} speaking); {graded} graded automatically " +
                          $"(confidence ≥ {threshold}%), {total - graded} left for a reviewer" +
                          (skipped > 0 ? $" ({skipped} recording(s) couldn't be scored)." : " with the suggestion.");
        request.Status = AiRequestStatus.Completed;
    }

    private bool AutoGrade(QuizAttempt attempt, AttemptAnswer answer, string? feedback, string by)
    {
        if (attempt.Status != AttemptStatus.PendingReview || answer.AiScore is not { } score) return false;
        answer.Score = score;
        answer.IsCorrect = answer.Score * 2 >= answer.MaxScore;
        answer.NeedsReview = false;
        answer.Feedback = Trim(feedback, 1000);
        answer.ReviewedByEmail = Trim(by, 256);
        answer.ReviewedAt = clock.GetUtcNow();
        AttemptScoring.CountForAnalytics(answer);
        return true;
    }

    private static string SpeakingFeedbackText(PronunciationResult r, int score)
    {
        var weak = r.Words.Where(w => w.Accuracy < 60 || w.ErrorType is "Omission" or "Mispronunciation").Select(w => w.Word).Take(8).ToList();
        var text = $"Pronunciation {score}/100 (accuracy {r.Accuracy:0}, fluency {r.Fluency:0}, completeness {r.Completeness:0}). Heard: \"{r.Transcript}\".";
        if (weak.Count > 0) text += " Words to check: " + string.Join(", ", weak) + ".";
        if (r.IsSample) text += " Sample scores: speech service not configured.";
        return text;
    }

    private static string AiFeedbackText(AiWritingGrade g)
    {
        var lines = new List<string> { g.Feedback ?? "" };
        lines.AddRange(g.Corrections.Take(5).Select(c => $"• {c.Original} → {c.Corrected}" + (string.IsNullOrWhiteSpace(c.Explanation) ? "" : $" ({c.Explanation})")));
        return string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
    }

    // ================================================================== helpers

    private static AiCallContext Context(AiGenerationRequest request) => new(request.RequestedBy, request.Id);

    /// <summary>Turns an AI error into a job status: an exhausted budget pauses the job so it can be resumed (5.4).</summary>
    private static AiOutcome<T> Ok<T>(Result<AiOutcome<T>> result) => result.IsSuccess
        ? result.Value
        : throw new JobStop(result.Error!.Code == "ai_budget_exceeded" ? AiRequestStatus.Paused : AiRequestStatus.Failed, result.Error.Message);

    /// <summary>An admin may cancel a long job while it runs.</summary>
    private async Task<bool> StoppedAsync(AiGenerationRequest request, CancellationToken ct)
    {
        var status = await db.AiGenerationRequests.AsNoTracking().Where(r => r.Id == request.Id).Select(r => r.Status).FirstAsync(ct);
        if (status != AiRequestStatus.Cancelled) return false;
        request.Status = AiRequestStatus.Cancelled;
        return true;
    }

    private async Task RecordAsync<T>(AiGenerationRequest request, ReviewEntityType type, int entityId, AiFeature feature, AiOutcome<T> outcome,
        string isoCode, CancellationToken ct)
    {
        var native = (await settings.GetAsync(SettingKeys.AiNativeCheckLanguages, ct) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(isoCode, StringComparer.OrdinalIgnoreCase);
        db.AiGeneratedItems.Add(new AiGeneratedItem
        {
            RequestId = request.Id, EntityType = type, EntityId = entityId, Feature = feature, Model = Trim(outcome.Model, 100) ?? "",
            Prompt = Trim(outcome.Prompt, 16000) ?? "", NeedsNativeCheck = native, CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<decimal> CostOfAsync(int requestId, CancellationToken ct) =>
        (await db.AiUsageLogs.AsNoTracking().Where(l => l.RequestId == requestId).Select(l => l.Cost).ToListAsync(ct)).Sum();

    private static T Params<T>(AiGenerationRequest request) where T : new() =>
        JsonSerializer.Deserialize<T>(request.ParametersJson, AiGenerationService.Json) ?? new T();

    private static CefrLevel Clamp(CefrLevel level, CefrLevel min, CefrLevel max) => level < min ? min : level > max ? max : level;

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}

/// <summary>Converts an AI question into the bank's input model (validated by the same rules as manual questions).</summary>
public static class AiQuestionMapper
{
    public static QuestionInput? ToInput(AiQuestionDraft draft, int languageId, int lectureId, CefrLevel level, IReadOnlyCollection<QuestionType> allowed)
    {
        if (!Enum.TryParse<QuestionType>(draft.Type?.Replace(" ", "").Replace("_", ""), true, out var type) || !Enum.IsDefined(type)) return null;
        if (allowed.Count > 0 && !allowed.Contains(type)) return null;
        if (type is QuestionType.Writing or QuestionType.Speak || string.IsNullOrWhiteSpace(draft.Stem)) return null;

        var skill = Enum.TryParse<Skill>(draft.Skill, true, out var s) && Enum.IsDefined(s) ? s : type switch
        {
            QuestionType.ListenChoose => Skill.Listening,
            QuestionType.Translate => Skill.Reading,
            QuestionType.ReorderWords or QuestionType.FillBlank => Skill.Grammar,
            _ => Skill.Vocabulary
        };

        var options = draft.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)).Take(8)
            .Select(o => new OptionInput { Text = Cut(o.Text, 500), IsCorrect = o.Correct, MatchKey = type == QuestionType.MatchPairs ? Cut(o.Match, 300) : null })
            .ToList();
        if (type == QuestionType.TrueFalse)
        {
            // Normalise to exactly True/False.
            var trueCorrect = options.FirstOrDefault(o => string.Equals(o.Text, "True", StringComparison.OrdinalIgnoreCase))?.IsCorrect
                              ?? (options.FirstOrDefault(o => o.IsCorrect)?.Text is { } t
                                  ? t.StartsWith("t", StringComparison.OrdinalIgnoreCase) || t.StartsWith("y", StringComparison.OrdinalIgnoreCase)
                                  : true);
            options = [new() { Text = "True", IsCorrect = trueCorrect }, new() { Text = "False", IsCorrect = !trueCorrect }];
        }

        return new QuestionInput
        {
            LanguageId = languageId, LectureId = lectureId, Type = type, Skill = skill, Level = level,
            Difficulty = Math.Clamp(draft.Difficulty, 1, 5), Points = 1,
            Stem = Cut(draft.Stem, 1000)!, Prompt = Cut(draft.Prompt, 500), Explanation = Cut(draft.Explanation, 1000), Options = options
        };
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
