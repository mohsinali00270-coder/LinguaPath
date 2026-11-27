namespace LinguaPath.Shared;

// Phase 9: quiz competitions, tournaments and leagues (SRS 11.4, FR-Q01 .. FR-Q11, FR-S08, BR-29).

public enum CompetitionMode
{
    /// <summary>1 v 1 against a friend or chat contact, live or in your own time.</summary>
    Challenge = 1,
    /// <summary>Random opponent at ± 1 CEFR level; a bot after 30 seconds.</summary>
    QuickMatch = 2,
    /// <summary>Started by a group admin for 3–50 members of a group chat.</summary>
    GroupBattle = 3,
    /// <summary>One round of an admin-scheduled tournament.</summary>
    Tournament = 4
}

public enum CompetitionStatus
{
    /// <summary>A challenge waiting for the opponent (24 hours).</summary>
    Pending = 1,
    /// <summary>Quick match looking for an opponent, or a group battle lobby.</summary>
    Waiting = 2,
    /// <summary>Players found; the countdown is running.</summary>
    Scheduled = 3,
    Running = 4,
    Finished = 5,
    Declined = 6,
    Expired = 7,
    Cancelled = 8
}

public enum PlayerState
{
    Invited = 1,
    Joined = 2,
    Playing = 3,
    Finished = 4,
    Left = 5,
    Declined = 6
}

public enum CompetitionResult
{
    Win = 1,
    Draw = 2,
    Loss = 3
}

public enum TournamentStatus
{
    Draft = 1,
    /// <summary>Published: learners can join until it starts.</summary>
    Open = 2,
    Running = 3,
    Finished = 4,
    Cancelled = 5
}

/// <summary>Weekly league divisions (FR-S08).</summary>
public enum LeagueTier
{
    Bronze = 1,
    Silver = 2,
    Gold = 3,
    Platinum = 4,
    Diamond = 5
}

public enum LeagueOutcome
{
    Stayed = 0,
    Promoted = 1,
    Relegated = 2
}
