using CommunityToolkit.Mvvm.ComponentModel;
using LinguaPath.Shared;

namespace LinguaPath.Mobile.Core.ViewModels;

/// <summary>A selectable card or chip. Non-generic so XAML templates can bind to it.</summary>
public partial class SelectableOption : ObservableObject
{
    public SelectableOption(string title, string? description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    [ObservableProperty] private bool isSelected;

    public override string ToString() => Title;
}

/// <summary>A selectable option carrying a value (levels, goals, durations, minutes, days, skills).</summary>
public class Option<T>(T value, string title, string? description = null) : SelectableOption(title, description)
{
    public T Value { get; } = value;
}

public static class LearningText
{
    /// <summary>Plain self-assessment descriptions (FR-L11).</summary>
    public static IReadOnlyList<Option<CefrLevel>> Levels() =>
    [
        new(CefrLevel.A1, "Beginner (A1)", "I know a few words or nothing at all."),
        new(CefrLevel.A2, "Elementary (A2)", "I can handle simple, everyday situations."),
        new(CefrLevel.B1, "Intermediate (B1)", "I can talk about familiar topics and get by when travelling."),
        new(CefrLevel.B2, "Upper intermediate (B2)", "I can follow most conversations and discuss many topics."),
        new(CefrLevel.C1, "Advanced (C1)", "I express myself fluently, even on complex topics."),
        new(CefrLevel.C2, "Proficient (C2)", "I understand almost everything with ease.")
    ];

    public static string LevelName(CefrLevel level) => level switch
    {
        CefrLevel.A1 => "Beginner (A1)",
        CefrLevel.A2 => "Elementary (A2)",
        CefrLevel.B1 => "Intermediate (B1)",
        CefrLevel.B2 => "Upper intermediate (B2)",
        CefrLevel.C1 => "Advanced (C1)",
        _ => "Proficient (C2)"
    };

    /// <summary>FR-L12 goals.</summary>
    public static IReadOnlyList<Option<LearningGoal>> Goals() =>
    [
        new(LearningGoal.Travel, "Travel", "Get around, order food, meet people."),
        new(LearningGoal.JobBusiness, "Job or business", "Work, meetings and emails."),
        new(LearningGoal.ExamPrep, "Exam preparation", "IELTS, DELF, JLPT, DELE and others."),
        new(LearningGoal.StudyAbroad, "Study abroad", "Lectures, campus life, paperwork."),
        new(LearningGoal.Conversation, "Conversation", "Talk with friends, family or a partner."),
        new(LearningGoal.Hobby, "Just for fun", "Films, music, books, culture.")
    ];

    public static string GoalName(LearningGoal goal) => Goals().FirstOrDefault(g => g.Value == goal)?.Title ?? goal.ToString();

    /// <summary>FR-L13 durations in months; 0 = custom end date.</summary>
    public static IReadOnlyList<Option<int>> Durations() =>
    [
        new(1, "1 month"),
        new(3, "3 months"),
        new(6, "6 months"),
        new(12, "12 months"),
        new(0, "Pick a date")
    ];

    /// <summary>FR-L13 daily study time.</summary>
    public static IReadOnlyList<Option<int>> DailyMinutes() =>
    [
        new(10, "10 min", "Casual"),
        new(20, "20 min", "Regular"),
        new(30, "30 min", "Serious"),
        new(60, "60 min", "Intense")
    ];

    public static IReadOnlyList<Option<DayOfWeek>> Days() =>
    [
        new(DayOfWeek.Monday, "Mon"), new(DayOfWeek.Tuesday, "Tue"), new(DayOfWeek.Wednesday, "Wed"),
        new(DayOfWeek.Thursday, "Thu"), new(DayOfWeek.Friday, "Fri"), new(DayOfWeek.Saturday, "Sat"), new(DayOfWeek.Sunday, "Sun")
    ];

    /// <summary>FR-L14 focus skills.</summary>
    public static IReadOnlyList<Option<FocusSkill>> Skills() =>
    [
        new(FocusSkill.Speaking, "Speaking"), new(FocusSkill.Listening, "Listening"), new(FocusSkill.Reading, "Reading"),
        new(FocusSkill.Writing, "Writing"), new(FocusSkill.Grammar, "Grammar"), new(FocusSkill.Vocabulary, "Vocabulary")
    ];

    public static StudyDays ToStudyDays(IEnumerable<Option<DayOfWeek>> days) =>
        days.Where(d => d.IsSelected).Aggregate(StudyDays.None, (acc, d) => acc | (StudyDays)(1 << (int)d.Value));

    public static FocusSkill ToSkills(IEnumerable<Option<FocusSkill>> skills) =>
        skills.Where(s => s.IsSelected).Aggregate(FocusSkill.None, (acc, s) => acc | s.Value);

    public static void Select<T>(IEnumerable<Option<T>> options, T value) where T : notnull
    {
        foreach (var o in options) o.IsSelected = EqualityComparer<T>.Default.Equals(o.Value, value);
    }
}
