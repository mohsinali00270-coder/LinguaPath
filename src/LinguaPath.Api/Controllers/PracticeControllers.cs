using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Practice;
using LinguaPath.Shared.Contracts.Translation;
using LinguaPath.Shared.Contracts.Words;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Practice tab: reading, writing, speaking, listening and free-talk (FR-L90 .. FR-L93).</summary>
[Route("api/v1/practice")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class PracticeController(IPracticeService practice) : ApiControllerBase
{
    /// <summary>Done / average score per exercise type, words due, translations left today and conversation topics.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<ApiResponse<PracticeSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] int? languageId, CancellationToken ct) =>
        FromResult(await practice.GetSummaryAsync(CurrentUserId, languageId, ct));

    /// <summary>Next exercise of a type (read, write, speak, listen) at the learner's level. skip = the one just shown.</summary>
    [HttpGet("{type}/next")]
    [ProducesResponseType<ApiResponse<PracticeItemDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Next(PracticeType type, [FromQuery] int? languageId, [FromQuery] int? skip, CancellationToken ct) =>
        FromResult(await practice.NextAsync(CurrentUserId, type, languageId, skip, ct));

    /// <summary>Answers to a passage's comprehension questions (option index per question, in order).</summary>
    [HttpPost("read/{itemId:int}/answers")]
    [ProducesResponseType<ApiResponse<ReadResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Read(int itemId, ReadAnswersRequest request, CancellationToken ct) =>
        FromResult(await practice.AnswerReadingAsync(CurrentUserId, itemId, request, ct));

    /// <summary>AI check of the learner's writing: score, feedback and corrections (FR-L91).</summary>
    [HttpPost("write/check")]
    [ProducesResponseType<ApiResponse<WriteCheckDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Write(WriteCheckRequest request, CancellationToken ct) =>
        FromResult(await practice.CheckWritingAsync(CurrentUserId, request, ct));

    /// <summary>
    /// Pronunciation assessment (FR-L92). Multipart: audio (WAV, up to 60 s), languageId, and itemId or referenceText.
    /// Returns 0-100 scores and per-word accuracy.
    /// </summary>
    [HttpPost("speak/assess")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ApiResponse<SpeakResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Speak([FromForm] int languageId, [FromForm] int? itemId, [FromForm] string? referenceText, IFormFile audio,
        CancellationToken ct)
    {
        var bytes = await AudioUpload.ReadAsync(audio, ct);
        if (bytes is null) return ProblemResults.ToResult(HttpContext, AudioUpload.Invalid);
        return FromResult(await practice.AssessSpeakingAsync(CurrentUserId, languageId, itemId, referenceText, bytes, ct));
    }

    /// <summary>Dictation check: the sentence and a word-by-word diff of mistakes (FR-L93).</summary>
    [HttpPost("listen/{itemId:int}/check")]
    [ProducesResponseType<ApiResponse<ListenResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listen(int itemId, ListenCheckRequest request, CancellationToken ct) =>
        FromResult(await practice.CheckListeningAsync(CurrentUserId, itemId, request, ct));

    /// <summary>Free-talk with the AI partner. The app sends the earlier turns; replies include a correction when needed.</summary>
    [HttpPost("conversation")]
    [ProducesResponseType<ApiResponse<ConversationReplyDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Conversation(ConversationRequest request, CancellationToken ct) =>
        FromResult(await practice.ConverseAsync(CurrentUserId, request, ct));

    [HttpGet("history")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<PracticeHistoryDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History([FromQuery] PracticeType? type, CancellationToken ct) =>
        FromResult(await practice.HistoryAsync(CurrentUserId, type, ct));
}

/// <summary>Online translation (FR-L100 .. FR-L104): auto-detect, voice input, history, daily limit (BR-15).</summary>
[Route("api/v1/translate")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class TranslateController(ITranslationService translation) : ApiControllerBase
{
    /// <summary>Leave "from" empty to detect the language.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<TranslationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Translate(TranslateRequest request, CancellationToken ct) =>
        FromResult(await translation.TranslateAsync(CurrentUserId, request, ct));

    /// <summary>Voice input (FR-L101). Multipart: audio (WAV, up to 60 s), from (spoken language), to.</summary>
    [HttpPost("speech")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ApiResponse<SpeechTranslationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Speech([FromForm] string from, [FromForm] string to, IFormFile audio, CancellationToken ct)
    {
        var bytes = await AudioUpload.ReadAsync(audio, ct);
        if (bytes is null) return ProblemResults.ToResult(HttpContext, AudioUpload.Invalid);
        return FromResult(await translation.TranslateSpeechAsync(CurrentUserId, from, to, bytes, ct));
    }

    /// <summary>The last 100 translations, newest first (FR-L103).</summary>
    [HttpGet("history")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<TranslationDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(CancellationToken ct) => FromResult(await translation.HistoryAsync(CurrentUserId, ct));

    [HttpDelete("history")]
    public async Task<IActionResult> ClearHistory(CancellationToken ct) => FromResult(await translation.ClearHistoryAsync(CurrentUserId, ct));

    [HttpGet("languages")]
    [ProducesResponseType<ApiResponse<IReadOnlyList<TranslationLanguageDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Languages(CancellationToken ct) => OkData(await translation.LanguagesAsync(ct));
}

/// <summary>"My words" and flashcards (FR-L94, FR-L103).</summary>
[Route("api/v1/words")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class WordsController(IWordsService words) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<UserWordDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int? languageId, [FromQuery] bool due, [FromQuery] string? search, CancellationToken ct) =>
        FromResult(await words.ListAsync(CurrentUserId, languageId, due, search, ct));

    [HttpGet("summary")]
    [ProducesResponseType<ApiResponse<WordsSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] int? languageId, CancellationToken ct) =>
        FromResult(await words.SummaryAsync(CurrentUserId, languageId, ct));

    /// <summary>Save a word (from a lecture, a translation or by hand). Saving the same word again updates its meaning.</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<UserWordDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(SaveWordRequest request, CancellationToken ct) =>
        FromResult(await words.SaveAsync(CurrentUserId, request, ct));

    /// <summary>Flashcard answer: Again, Hard, Good or Easy (SM-2).</summary>
    [HttpPost("{id:int}/review")]
    [ProducesResponseType<ApiResponse<UserWordDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Review(int id, ReviewWordRequest request, CancellationToken ct) =>
        FromResult(await words.ReviewAsync(CurrentUserId, id, request, ct));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) => FromResult(await words.DeleteAsync(CurrentUserId, id, ct));
}

internal static class AudioUpload
{
    public static readonly Application.Common.Error Invalid =
        Application.Common.Error.Validation("Audio", "Send a WAV recording (up to 10 MB, 60 seconds).");

    public static async Task<byte[]?> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > PracticeService.MaxAudioBytes) return null;
        using var memory = new MemoryStream((int)file.Length);
        await using var stream = file.OpenReadStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }
}
