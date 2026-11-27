using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

// ===================================================================== quiz competitions (SRS 11.4, 11.7)

public class Competition
{
    public int Id { get; set; }
    public CompetitionMode Mode { get; set; }
    public CompetitionStatus Status { get; set; }
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public CefrLevel Level { get; set; }
    public int? ModuleId { get; set; }
    public int QuestionCount { get; set; } = 10;
    public int SecondsPerQuestion { get; set; } = 15;
    /// <summary>False = each player plays in their own time (asynchronous challenge).</summary>
    public bool IsLive { get; set; } = true;
    /// <summary>Counts for ratings and the BR-29 daily limit: human 1 v 1 only.</summary>
    public bool IsRanked { get; set; }
    public int? ConversationId { get; set; }
    public int? TournamentId { get; set; }
    public int? Round { get; set; }
    public int? RematchOfId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Challenge accept deadline, quick-match search end, or lobby start.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    // Live timeline (shared by all players).
    public int CurrentIndex { get; set; } = -1;
    public DateTimeOffset? QuestionOpensAt { get; set; }
    public DateTimeOffset? QuestionClosesAt { get; set; }

    /// <summary>When the clock next has something to do (start, close a question, expire). Indexed for the ticker.</summary>
    public DateTimeOffset? NextEventAt { get; set; }

    /// <summary>Optimistic concurrency: two learners can't grab the same waiting quick match.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    public ICollection<CompetitionPlayer> Players { get; set; } = new List<CompetitionPlayer>();
    public ICollection<CompetitionQuestion> Questions { get; set; } = new List<CompetitionQuestion>();
}

public class CompetitionPlayer
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public Competition Competition { get; set; } = null!;
    /// <summary>Null for the bot.</summary>
    public Guid? UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public bool IsBot { get; set; }
    public string? BotName { get; set; }
    public PlayerState State { get; set; } = PlayerState.Joined;
    public DateTimeOffset JoinedAt { get; set; }

    // Own-time timeline (asynchronous challenges).
    public DateTimeOffset? StartedAt { get; set; }
    public int CurrentIndex { get; set; } = -1;
    public DateTimeOffset? QuestionOpensAt { get; set; }
    public DateTimeOffset? QuestionClosesAt { get; set; }

    public int Score { get; set; }
    public int Correct { get; set; }
    public int Answered { get; set; }
    /// <summary>Tie-breaker (FR-Q04): answer time, with unanswered questions counted as the full time.</summary>
    public long TotalAnswerMs { get; set; }
    public int? Rank { get; set; }
    public CompetitionResult? Result { get; set; }
    public int PointsAwarded { get; set; }
    public bool LeftEarly { get; set; }
    public int? RatingBefore { get; set; }
    public int? RatingAfter { get; set; }
}

public class CompetitionQuestion
{
    public int CompetitionId { get; set; }
    public int SortOrder { get; set; }
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;
}

public class CompetitionAnswer
{
    public long Id { get; set; }
    public int CompetitionId { get; set; }
    public int PlayerId { get; set; }
    public int QuestionIndex { get; set; }
    public int QuestionId { get; set; }
    public string? AnswerText { get; set; }
    public int? OptionId { get; set; }
    public bool IsCorrect { get; set; }
    public int AnswerMs { get; set; }
    public int Score { get; set; }
    public DateTimeOffset AnsweredAt { get; set; }
}

/// <summary>Per learner and language (FR-Q08): Elo rating from 1,000 and the stats screen.</summary>
public class CompetitionRating
{
    public Guid UserId { get; set; }
    public int LanguageId { get; set; }
    public int Rating { get; set; } = 1000;
    public int Played { get; set; }
    public int Won { get; set; }
    public int Drawn { get; set; }
    public int Lost { get; set; }
    public int CurrentStreak { get; set; }
    public int BestStreak { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ===================================================================== tournaments (FR-Q11)

public class Tournament
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LanguageId { get; set; }
    public Language Language { get; set; } = null!;
    public CefrLevel Level { get; set; }
    /// <summary>Question pool: optional module of the course.</summary>
    public int? ModuleId { get; set; }
    public DateTimeOffset EntryOpensAt { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int Rounds { get; set; } = 1;
    public int CurrentRound { get; set; }
    public int QuestionCount { get; set; } = 10;
    public int SecondsPerQuestion { get; set; } = 15;
    public int? MaxEntrants { get; set; }
    public int PrizeFirst { get; set; } = 300;
    public int PrizeSecond { get; set; } = 200;
    public int PrizeThird { get; set; } = 100;
    public TournamentStatus Status { get; set; } = TournamentStatus.Draft;
    public string CreatedByEmail { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? NextEventAt { get; set; }
    public ICollection<TournamentEntry> Entries { get; set; } = new List<TournamentEntry>();
}

public class TournamentEntry
{
    public int Id { get; set; }
    public int TournamentId { get; set; }
    public Tournament Tournament { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; }
    /// <summary>The last round reached; eliminated players keep theirs.</summary>
    public int RoundReached { get; set; }
    public int LastRoundRank { get; set; }
    public int LastRoundScore { get; set; }
    public int? FinalPlace { get; set; }
    public int PrizePoints { get; set; }
    public bool IsDisqualified { get; set; }
    public string? DisqualifiedReason { get; set; }
    public string? DisqualifiedBy { get; set; }
}

// ===================================================================== weekly leagues (FR-S08)

public class League
{
    public int Id { get; set; }
    public DateOnly WeekStart { get; set; }
    public LeagueTier Tier { get; set; }
    public int Number { get; set; }
    public bool IsClosed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<LeagueMember> Members { get; set; } = new List<LeagueMember>();
}

public class LeagueMember
{
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; }
    /// <summary>Filled when the week closes.</summary>
    public int? WeeklyPoints { get; set; }
    public int? Rank { get; set; }
    public LeagueOutcome? Outcome { get; set; }
}
