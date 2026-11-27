using LinguaPath.Application.Features.Ai;
using LinguaPath.Shared;

namespace LinguaPath.Application.Features.Moderation;

public enum LadderStep
{
    Warning = 1,
    Suspension = 2,
    Block = 3
}

/// <summary>Pure moderation and chat rules (BR-23 .. BR-31, FR-M04, FR-M12).</summary>
public static class ModerationRules
{
    /// <summary>AI categories that are blocked at once above the severe threshold (FR-M02).</summary>
    public static readonly string[] SevereCategories = ["threat", "hate", "sexual"];

    /// <summary>BR-26: 1st active strike = warning, 2nd = chat suspension, 3rd = permanent block. Severe content blocks at once.</summary>
    public static LadderStep StepFor(int activeStrikes, ModerationSeverity severity) =>
        severity == ModerationSeverity.Severe || activeStrikes >= 3 ? LadderStep.Block
        : activeStrikes == 2 ? LadderStep.Suspension
        : LadderStep.Warning;

    /// <summary>BR-25: every Nth blocked link/contact attempt in a day is a strike.</summary>
    public static bool LinkStrikeDue(int attemptsToday, int perStrike) => perStrike > 0 && attemptsToday > 0 && attemptsToday % perStrike == 0;

    /// <summary>FR-M04: the same message in this many different chats within the window is spam. Short greetings don't count.</summary>
    public static bool IsBroadcastSpam(IEnumerable<(int ConversationId, string Normalized)> recent, int conversationId, string normalized, int threshold)
    {
        if (threshold <= 1 || normalized.Length < 12) return false;
        var chats = recent.Where(r => r.Normalized == normalized).Select(r => r.ConversationId).Append(conversationId).Distinct().Count();
        return chats >= threshold;
    }

    /// <summary>FR-M02: maps an AI verdict to a severity. Borderline scores (within 20 of the abusive line) go to review as Mild.</summary>
    public static ModerationSeverity AiSeverity(string? category, double score, int abusivePercent, int severePercent)
    {
        var cat = (category ?? "none").Trim().ToLowerInvariant();
        if (cat is "none" or "" ) return ModerationSeverity.None;
        if (SevereCategories.Contains(cat) && score >= severePercent) return ModerationSeverity.Severe;
        if (score >= abusivePercent) return ModerationSeverity.Abusive;
        if (score >= abusivePercent - 20) return ModerationSeverity.Mild;
        return ModerationSeverity.None;
    }

    public static ModerationSeverity AiSeverity(AiModerationResult r, int abusivePercent, int severePercent) =>
        AiSeverity(r.Category, r.Score, abusivePercent, severePercent);

    /// <summary>One direct chat per pair of learners.</summary>
    public static string DirectKey(Guid a, Guid b) => a.CompareTo(b) < 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";

    /// <summary>FR-C11: "delete for everyone" is allowed for this many hours after sending.</summary>
    public static bool CanDeleteForEveryone(DateTimeOffset sentAt, DateTimeOffset now, int hours) => now - sentAt <= TimeSpan.FromHours(Math.Max(0, hours));

    /// <summary>FR-S24 privacy: Everyone / FriendsOnly / Nobody.</summary>
    public static bool AudienceAllows(AudienceSetting setting, bool areFriends) => setting switch
    {
        AudienceSetting.Everyone => true,
        AudienceSetting.FriendsOnly => areFriends,
        _ => false
    };

    /// <summary>FR-C29 slow mode: seconds the member must still wait (0 = can send). Owners and admins aren't limited.</summary>
    public static int SlowModeWait(int slowModeSec, DateTimeOffset? lastSentAt, DateTimeOffset now, MemberRole role)
    {
        if (slowModeSec <= 0 || lastSentAt is null || role != MemberRole.Member) return 0;
        var wait = slowModeSec - (now - lastSentAt.Value).TotalSeconds;
        return wait > 0 ? (int)Math.Ceiling(wait) : 0;
    }

    /// <summary>FR-C26: members mentioned as "@Display Name" (longest names first so "@Ali Khan" beats "@Ali").</summary>
    public static IReadOnlyList<Guid> Mentions(string text, IEnumerable<(Guid UserId, string Name)> members)
    {
        var found = new List<Guid>();
        if (!text.Contains('@')) return found;
        var taken = new bool[text.Length];
        foreach (var (id, name) in members.Where(m => !string.IsNullOrWhiteSpace(m.Name)).OrderByDescending(m => m.Name.Trim().Length))
        {
            var token = "@" + name.Trim();
            var from = 0;
            while (from < text.Length)
            {
                var at = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var end = at + token.Length;
                from = at + 1;
                if (taken[at] || (end < text.Length && char.IsLetterOrDigit(text[end]))) continue;
                for (var i = at; i < end; i++) taken[i] = true;
                if (!found.Contains(id)) found.Add(id);
                break;
            }
        }
        return found;
    }

    /// <summary>Text shown in chat lists and push notifications.</summary>
    public static string Preview(string? text, int max = 80)
    {
        var t = (text ?? "").ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    /// <summary>Learner-facing explanation of a strike step (FR-M12).</summary>
    public static string Explain(LadderStep step, int suspensionHours) => step switch
    {
        LadderStep.Warning => "Your message broke the community rules. This is a warning: the next strike suspends chat for "
                              + $"{suspensionHours} hours, and a third strike permanently blocks the account.",
        LadderStep.Suspension => $"Your chat is suspended for {suspensionHours} hours. One more strike permanently blocks the account.",
        _ => "Your account has been permanently blocked for breaking the community rules."
    };
}
