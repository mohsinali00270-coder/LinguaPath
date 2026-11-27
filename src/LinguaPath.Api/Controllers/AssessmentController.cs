using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Assessment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Daily quiz, monthly exams, placement test, answers, results and marks (FR-L11, FR-L50 .. FR-L73).</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class AssessmentController(IAssessmentService assessment) : ApiControllerBase
{
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mp4"] = ".m4a", ["audio/m4a"] = ".m4a", ["audio/x-m4a"] = ".m4a", ["audio/aac"] = ".aac", ["audio/mpeg"] = ".mp3",
        ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav", ["audio/wave"] = ".wav", ["audio/webm"] = ".webm", ["audio/ogg"] = ".ogg",
        ["audio/3gpp"] = ".3gp"
    };

    /// <summary>Today's daily quiz: state, question count or today's result.</summary>
    [HttpGet("learner-courses/{id:int}/daily-quiz")]
    [ProducesResponseType<MyApiResponse<DailyQuizDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DailyQuiz(int id, CancellationToken ct) =>
        FromResult(await assessment.GetDailyQuizAsync(CurrentUserId, id, ct));

    /// <summary>Start (or resume) today's quiz. practice=true starts an extra round that doesn't count toward marks.</summary>
    [HttpPost("learner-courses/{id:int}/daily-quiz/start")]
    [ProducesResponseType<MyApiResponse<AttemptDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartDailyQuiz(int id, [FromQuery] bool practice, CancellationToken ct) =>
        FromResult(await assessment.StartDailyQuizAsync(CurrentUserId, id, practice, ct));

    /// <summary>Monthly exams of the course with lock / retake state.</summary>
    [HttpGet("learner-courses/{id:int}/exams")]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<ExamSummaryDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Exams(int id, CancellationToken ct) =>
        FromResult(await assessment.ListExamsAsync(CurrentUserId, id, ct));

    /// <summary>Start (or resume) a monthly exam. The timer runs on the server.</summary>
    [HttpPost("learner-courses/{id:int}/exams/{quizId:int}/start")]
    [ProducesResponseType<MyApiResponse<AttemptDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartExam(int id, int quizId, CancellationToken ct) =>
        FromResult(await assessment.StartExamAsync(CurrentUserId, id, quizId, ct));

    [HttpGet("learner-courses/{id:int}/marks")]
    [ProducesResponseType<MyApiResponse<MarksDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Marks(int id, CancellationToken ct) =>
        FromResult(await assessment.GetMarksAsync(CurrentUserId, id, ct));

    /// <summary>Optional placement test before enrolling (FR-L11).</summary>
    [HttpPost("courses/{courseId:int}/placement-test")]
    [ProducesResponseType<MyApiResponse<AttemptDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartPlacement(int courseId, CancellationToken ct) =>
        FromResult(await assessment.StartPlacementAsync(CurrentUserId, courseId, ct));

    /// <summary>The attempt with questions and saved answers; used to resume after a crash (FR-L62).</summary>
    [HttpGet("attempts/{id:int}")]
    [ProducesResponseType<MyApiResponse<AttemptDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Attempt(int id, CancellationToken ct) =>
        FromResult(await assessment.GetAttemptAsync(CurrentUserId, id, ct));

    /// <summary>Save (exams) or check (daily quiz, placement) one answer.</summary>
    [HttpPost("attempts/{id:int}/answers")]
    [ProducesResponseType<MyApiResponse<AnswerFeedbackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Answer(int id, SaveAnswerRequest request, CancellationToken ct) =>
        FromResult(await assessment.SaveAnswerAsync(CurrentUserId, id, request, ct));

    /// <summary>Recording for a speaking question (m4a, aac, mp3, wav, webm, ogg; up to 10 MB).</summary>
    [HttpPost("attempts/{id:int}/answers/{questionId:int}/audio")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<MyApiResponse<AnswerFeedbackDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Audio(int id, int questionId, IFormFile file, CancellationToken ct)
    {
        var type = file.ContentType.Split(';')[0].Trim();
        if (file.Length == 0 || file.Length > 10 * 1024 * 1024 || !AudioTypes.TryGetValue(type, out var extension))
            return ProblemResults.ToResult(HttpContext,
                Application.Common.Error.Validation("File", "Send an audio recording (m4a, aac, mp3, wav, webm or ogg) up to 10 MB."));
        await using var stream = file.OpenReadStream();
        return FromResult(await assessment.SaveAudioAnswerAsync(CurrentUserId, id, questionId, stream, extension, type, ct));
    }

    [HttpPost("attempts/{id:int}/submit")]
    [ProducesResponseType<MyApiResponse<AttemptResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Submit(int id, CancellationToken ct) =>
        FromResult(await assessment.SubmitAsync(CurrentUserId, id, ct));

    /// <summary>Integrity log: the app went to the background during an exam (FR-L65).</summary>
    [HttpPost("attempts/{id:int}/events")]
    public async Task<IActionResult> Event(int id, AttemptEventRequest request, CancellationToken ct) =>
        FromResult(await assessment.LogEventAsync(CurrentUserId, id, request, ct));

    [HttpGet("attempts/{id:int}/result")]
    [ProducesResponseType<MyApiResponse<AttemptResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetResult(int id, CancellationToken ct) =>
        FromResult(await assessment.GetResultAsync(CurrentUserId, id, ct));

    /// <summary>Each question with your answer, the correct answer and the explanation (FR-L72).</summary>
    [HttpGet("attempts/{id:int}/review")]
    [ProducesResponseType<MyApiResponse<AttemptReviewDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Review(int id, CancellationToken ct) =>
        FromResult(await assessment.GetReviewAsync(CurrentUserId, id, ct));
}
