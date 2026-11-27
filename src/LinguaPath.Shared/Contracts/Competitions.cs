namespace LinguaPath.Shared.Contracts.Competitions;

// ------------------------------------------------------------------ setting up (FR-Q01)

/// <param name="Live">True = both play at the same time; false = each plays the same questions in their own time.</param>
public record ChallengeRequest(Guid OpponentId, int LanguageId, CefrLevel? Level, int? ModuleId, int QuestionCount = 10, int SecondsPerQuestion = 15,
    bool Live = true);

public record QuickMatchRequest(int LanguageId, CefrLevel? Level, int QuestionCount = 10, int SecondsPerQuestion = 15);

/// <param name="StartInSeconds">Lobby time for members to join (30–600). Admins can also start early.</param>
public record GroupBattleRequest(int? LanguageId, CefrLevel? Level, int? ModuleId, int QuestionCount = 10, int SecondsPerQuestion = 15,
    int StartInSeconds = 120);

public record ShareRequest(int ConversationId);

// ------------------------------------------------------------------ playing (FR-Q02 .. FR-Q06)

public record CompetitionOptionDto(int Id, string Text);

/// <summary>A question as players see it: never the correct answer (FR-Q05). Times are server times; use ServerNow to correct for clock skew.</summary>
public record CompetitionQuestionDto(
    int CompetitionId,
    int Index,
    int Count,
    QuestionType Type,
    string Stem,
    string? Prompt,
    string? MediaUrl,
    IReadOnlyList<CompetitionOptionDto> Options,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    DateTimeOffset ServerNow);

/// <param name="OptionId">Multiple choice, true/false, listen-and-choose.</param>
/// <param name="Text">Fill in the blank.</param>
public record SubmitAnswerRequest(int QuestionIndex, int? OptionId, string? Text);

public record PlayerDto(
    int PlayerId,
    Guid? UserId,
    string DisplayName,
    string? PhotoUrl,
    bool IsBot,
    bool IsMe,
    PlayerState State,
    int Score,
    int Correct,
    int Answered,
    long TotalAnswerMs,
    int? Rank,
    CompetitionResult? Result,
    int PointsAwarded,
    int? RatingChange);

public record PlayerScoreDto(int PlayerId, Guid? UserId, string DisplayName, bool Answered, bool Correct, int QuestionScore, int Score, int Rank);

/// <summary>After each question closes: the right answer and how everyone did (FR-Q03, FR-Q10 live leaderboard).</summary>
public record QuestionResultDto(int CompetitionId, int Index, string CorrectAnswer, string? Explanation, IReadOnlyList<PlayerScoreDto> Leaderboard);

/// <summary>Answer of the hub's SubmitAnswer. In your-own-time play the result and the next question come back at once.</summary>
public record AnswerAckDto(bool Accepted, string? Code, string? Message, QuestionResultDto? Result, CompetitionQuestionDto? Next, bool Finished);

public record OpponentAnsweredDto(int CompetitionId, int PlayerId, Guid? UserId, int Index, int Answered);

public record CompetitionSummaryDto(
    int Id,
    CompetitionMode Mode,
    CompetitionStatus Status,
    string LanguageName,
    CefrLevel Level,
    int QuestionCount,
    int SecondsPerQuestion,
    bool IsLive,
    bool IsRanked,
    string Title,
    string? OpponentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt,
    CompetitionResult? MyResult,
    int? MyRank,
    int? MyScore,
    int PointsAwarded,
    bool NeedsMyAction,
    string? LanguageCode = null);

/// <summary>Everything a screen needs to show a competition: lobby, play or result.</summary>
public record CompetitionDto(
    CompetitionSummaryDto Summary,
    IReadOnlyList<PlayerDto> Players,
    CompetitionQuestionDto? CurrentQuestion,
    bool IAnsweredCurrent,
    bool CanJoin,
    bool CanStart,
    bool CanAccept,
    bool CanPlay,
    bool CanBot,
    int? ConversationId,
    int? TournamentId,
    int? Round,
    DateTimeOffset ServerNow);

public record ReviewItemDto(int Index, string Stem, string? MyAnswer, bool Correct, string CorrectAnswer, int Score, long? AnswerMs);

public record CompetitionResultDto(CompetitionDto Competition, IReadOnlyList<ReviewItemDto> Review, bool CanRematch);

// ------------------------------------------------------------------ stats (FR-Q08)

public record RatingDto(int LanguageId, string LanguageName, int Rating, int Played, int Won, int Drawn, int Lost, int WinRatePercent,
    int CurrentStreak, int BestStreak);

public record CompetitionStatsDto(int Played, int Won, int WinRatePercent, int BestStreak, int RankedToday, int RankedPerDay,
    IReadOnlyList<RatingDto> Ratings);

// ------------------------------------------------------------------ tournaments (FR-Q11)

public record TournamentDto(
    int Id,
    string Title,
    string? Description,
    string LanguageName,
    int LanguageId,
    CefrLevel Level,
    TournamentStatus Status,
    DateTimeOffset EntryOpensAt,
    DateTimeOffset StartsAt,
    int Rounds,
    int CurrentRound,
    int QuestionCount,
    int SecondsPerQuestion,
    int PrizeFirst,
    int PrizeSecond,
    int PrizeThird,
    int Entrants,
    int? MaxEntrants,
    bool IJoined,
    bool CanJoin,
    bool IDisqualified,
    int? MyCompetitionId,
    int? MyPlace,
    IReadOnlyList<TournamentStandingDto> Standings);

public record TournamentStandingDto(int Place, Guid UserId, string DisplayName, int RoundReached, int Score, int PrizePoints, bool IsMe);

// ------------------------------------------------------------------ leagues (FR-S08)

public record LeagueMemberDto(int Rank, Guid UserId, string DisplayName, string? PhotoUrl, int WeeklyPoints, bool IsMe, LeagueOutcome Zone);

public record LeagueDto(
    int Id,
    LeagueTier Tier,
    DateOnly WeekStart,
    DateTimeOffset EndsAt,
    int PromoteCount,
    int RelegateCount,
    int MyRank,
    int MyPoints,
    LeagueOutcome? LastWeekOutcome,
    LeagueTier? LastWeekTier,
    IReadOnlyList<LeagueMemberDto> Members);
