using System.Globalization;
using System.Security.Cryptography;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Progress;

namespace LinguaPath.Application.Features.Progress;

/// <summary>Points rules that don't need the database (FR-S03 .. FR-S06, BR-20 .. BR-22).</summary>
public static class PointMath
{
    /// <summary>FR-S06 thresholds.</summary>
    public static readonly IReadOnlyList<(PointTier Tier, int From)> Tiers =
    [
        (PointTier.Bronze, 0), (PointTier.Silver, 1_000), (PointTier.Gold, 5_000), (PointTier.Platinum, 15_000), (PointTier.Diamond, 40_000)
    ];

    public static PointTier TierFor(int total) => Tiers.Last(t => total >= t.From).Tier;

    public static (PointTier? Next, int? ToGo, int ProgressPercent) NextTier(int total)
    {
        var current = TierFor(total);
        var index = Tiers.ToList().FindIndex(t => t.Tier == current);
        if (index == Tiers.Count - 1) return (null, null, 100);
        var from = Tiers[index].From;
        var to = Tiers[index + 1].From;
        return (Tiers[index + 1].Tier, to - total, (int)Math.Floor(100.0 * (total - from) / (to - from)));
    }

    /// <summary>BR-22: exams, certificates, tournament prizes and admin changes don't count toward (or stop at) the daily cap.</summary>
    public static bool IsCapped(PointAction action) => action is not (PointAction.MonthlyExamPassed or PointAction.ExamGradeBonus
        or PointAction.CertificateEarned or PointAction.TournamentPrize or PointAction.AdminAdjustment);

    /// <summary>How much of an award fits under the daily cap.</summary>
    public static int UnderCap(int points, int usedToday, int cap) =>
        cap <= 0 ? points : Math.Max(0, Math.Min(points, cap - usedToday));

    /// <summary>BR-21: +perDay for each day before the planned day, up to max; nothing on or after the day.</summary>
    public static int StudyAheadBonus(DateOnly planned, DateOnly completedOn, int perDay, int max)
    {
        var early = planned.DayNumber - completedOn.DayNumber;
        return early <= 0 ? 0 : Math.Min(early * perDay, max);
    }

    /// <summary>FR-S03: 2 per correct answer (rule points), +10 for 100%.</summary>
    public static int DailyQuiz(int correct, int perCorrect) => Math.Max(0, correct) * perCorrect;

    /// <summary>FR-S07: weekly boards reset every Monday 00:00 UTC.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset utcNow)
    {
        var d = utcNow.UtcDateTime.Date;
        var offset = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
        return new DateTimeOffset(d.AddDays(-offset), TimeSpan.Zero);
    }

    public static DateTimeOffset MonthStart(DateTimeOffset utcNow) => new(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public static string Key(PointAction action, string? referenceType, string? referenceId) =>
        $"{action}:{referenceType ?? "-"}:{referenceId ?? "-"}";

    public static string Describe(PointAction action, string? note) => action switch
    {
        PointAction.LectureCompleted => "Lecture completed",
        PointAction.StudyAheadBonus => "Study-ahead bonus",
        PointAction.DailyQuiz => "Daily quiz",
        PointAction.DailyQuizPerfect => "Daily quiz: 100%",
        PointAction.MonthlyExamPassed => "Monthly exam passed",
        PointAction.ExamGradeBonus => "Exam grade bonus",
        PointAction.PracticeSession => "Practice",
        PointAction.StreakDay => "Daily streak",
        PointAction.StreakWeek => "7-day streak",
        PointAction.StreakMonth => "30-day streak",
        PointAction.CertificateEarned => "Certificate earned",
        PointAction.Referral => "Friend joined",
        PointAction.CompetitionWin => "Competition won",
        PointAction.CompetitionDraw => "Competition draw",
        PointAction.CompetitionLoss => "Competition played",
        PointAction.TournamentPrize => "Tournament prize",
        PointAction.GroupBattleBonus => "Group battle top 3",
        _ => string.IsNullOrWhiteSpace(note) ? "Adjustment" : note!
    };
}

/// <summary>Daily streak (BR-09).</summary>
public static class StreakRules
{
    public sealed record Change(bool Counted, bool Continued, int Current);

    /// <summary>Records activity on a local date. Same day: nothing; next day: +1; a gap: starts again at 1. Older dates are ignored.</summary>
    public static Change Register(UserStreak streak, DateOnly day, DateTimeOffset now)
    {
        if (streak.LastActivityDate is { } last)
        {
            if (day <= last) return new Change(false, true, streak.CurrentStreak);
            if (day.DayNumber - last.DayNumber == 1)
            {
                streak.CurrentStreak++;
            }
            else
            {
                streak.CurrentStreak = 1;
                streak.StreakStartDate = day;
            }
        }
        else
        {
            streak.CurrentStreak = 1;
            streak.StreakStartDate = day;
        }
        streak.LastActivityDate = day;
        streak.LongestStreak = Math.Max(streak.LongestStreak, streak.CurrentStreak);
        streak.UpdatedAt = now;
        return new Change(true, streak.CurrentStreak > 1, streak.CurrentStreak);
    }

    /// <summary>The streak as shown today: still alive if the last active day was today or yesterday.</summary>
    public static StreakDto View(UserStreak? streak, DateOnly today)
    {
        if (streak?.LastActivityDate is not { } last) return new StreakDto(0, streak?.LongestStreak ?? 0, null, false, false);
        var gap = today.DayNumber - last.DayNumber;
        var alive = gap <= 1;
        return new StreakDto(alive ? streak.CurrentStreak : 0, streak.LongestStreak, last, gap == 0, alive && gap == 1 && streak.CurrentStreak > 0);
    }
}

/// <summary>Badges (FR-L111, FR-S06).</summary>
public static class Badges
{
    public sealed record Info(BadgeKey Key, string Icon, string Title, string Description);

    public static readonly IReadOnlyList<Info> All =
    [
        new(BadgeKey.FirstLecture, "📘", "First lecture", "Completed your first lecture."),
        new(BadgeKey.Streak7, "🔥", "7-day streak", "Studied seven days in a row."),
        new(BadgeKey.Streak30, "🏆", "30-day streak", "Studied thirty days in a row."),
        new(BadgeKey.FirstExamPassed, "🎓", "First exam passed", "Passed a monthly exam."),
        new(BadgeKey.FirstCertificate, "📜", "Certified", "Earned your first certificate."),
        new(BadgeKey.PracticeRegular, "🎯", "Practice regular", "Did 25 practice exercises."),
        new(BadgeKey.TierSilver, "🥈", "Silver", "Reached 1,000 points."),
        new(BadgeKey.TierGold, "🥇", "Gold", "Reached 5,000 points."),
        new(BadgeKey.TierPlatinum, "💠", "Platinum", "Reached 15,000 points."),
        new(BadgeKey.TierDiamond, "💎", "Diamond", "Reached 40,000 points.")
    ];

    public static Info Get(BadgeKey key) => All.First(b => b.Key == key);

    public static BadgeDto ToDto(BadgeKey key, DateTimeOffset? earnedAt)
    {
        var b = Get(key);
        return new BadgeDto(b.Key, b.Icon, b.Title, b.Description, earnedAt);
    }

    public static IEnumerable<BadgeKey> ForTier(PointTier tier) => tier switch
    {
        PointTier.Diamond => [BadgeKey.TierSilver, BadgeKey.TierGold, BadgeKey.TierPlatinum, BadgeKey.TierDiamond],
        PointTier.Platinum => [BadgeKey.TierSilver, BadgeKey.TierGold, BadgeKey.TierPlatinum],
        PointTier.Gold => [BadgeKey.TierSilver, BadgeKey.TierGold],
        PointTier.Silver => [BadgeKey.TierSilver],
        _ => []
    };
}

/// <summary>Certificate rules (BR-06 .. BR-08, SRS 6 numbering).</summary>
public static class CertificateRules
{
    public sealed record Facts(int CoreDone, int CoreTotal, int ExamsPassed, int ExamsTotal, int? CourseGrade, int MinGrade, bool GoodStanding);

    public static bool Eligible(Facts f) =>
        f.CoreTotal > 0 && f.CoreDone >= f.CoreTotal && f.ExamsPassed >= f.ExamsTotal && f.CourseGrade is { } g && g >= f.MinGrade && f.GoodStanding;

    public static GradeBand Band(int grade, int distinction, int merit) =>
        grade >= distinction ? GradeBand.Distinction : grade >= merit ? GradeBand.Merit : GradeBand.Pass;

    /// <summary>LP-ES-2026-000123.</summary>
    public static string Number(string prefix, string isoCode, int year, int sequence) =>
        $"{prefix}-{LanguagePart(isoCode)}-{year.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString("D6", CultureInfo.InvariantCulture)}";

    public static string NumberPrefix(string prefix, string isoCode, int year) => $"{prefix}-{LanguagePart(isoCode)}-{year.ToString(CultureInfo.InvariantCulture)}-";

    /// <summary>Sequence part of a number with the given prefix, or 0.</summary>
    public static int SequenceOf(string number, string numberPrefix) =>
        number.StartsWith(numberPrefix, StringComparison.Ordinal) &&
        int.TryParse(number.AsSpan(numberPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string LanguagePart(string isoCode)
    {
        var letters = new string(isoCode.Where(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
        return letters.Length == 0 ? "XX" : letters.Length > 3 ? letters[..3] : letters;
    }

    /// <summary>10 characters without look-alikes (0/O, 1/I/L) for typing from paper.</summary>
    public static string NewVerificationCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Create(10, alphabet, static (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }

    /// <summary>"6 months", "1 year 2 months", "3 weeks" between start and completion.</summary>
    public static string Duration(DateOnly start, DateOnly end)
    {
        var days = Math.Max(1, end.DayNumber - start.DayNumber + 1);
        if (days < 60) return days < 14 ? $"{days} day{(days == 1 ? "" : "s")}" : $"{days / 7} weeks";
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month + (end.Day >= start.Day ? 0 : -1);
        months = Math.Max(2, months);
        var years = months / 12;
        var rest = months % 12;
        var y = years == 0 ? "" : $"{years} year{(years == 1 ? "" : "s")}";
        var m = rest == 0 ? "" : $"{rest} month{(rest == 1 ? "" : "s")}";
        return string.Join(" ", new[] { y, m }.Where(s => s.Length > 0));
    }

    public static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        var text = template;
        foreach (var (key, value) in values) text = text.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
