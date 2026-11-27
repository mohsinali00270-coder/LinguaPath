using LinguaPath.Application.Features.Assessment;
using LinguaPath.Domain.Entities;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 4: answer grading, review scheduling, course grade and placement rules.</summary>
public class AnswerGraderTests
{
    private static Question Make(QuestionType type, params (string Text, bool Correct, string? Match)[] options)
    {
        var q = new Question { Type = type, Points = 1, Stem = "q", Prompt = "Buenos días" };
        for (var i = 0; i < options.Length; i++)
            q.Options.Add(new QuestionOption { Id = i + 1, Text = options[i].Text, IsCorrect = options[i].Correct, MatchKey = options[i].Match, SortOrder = i + 1 });
        return q;
    }

    private static AnswerInput Text(string text) => AnswerInput.Empty with { Text = text };
    private static AnswerInput Options(params int[] ids) => AnswerInput.Empty with { OptionIds = ids };

    [Fact]
    public void Multiple_choice_needs_exactly_the_correct_options()
    {
        var single = Make(QuestionType.MultipleChoice, ("hello", true, null), ("bye", false, null));
        Assert.True(AnswerGrader.Grade(single, Options(1)).IsCorrect);
        Assert.False(AnswerGrader.Grade(single, Options(2)).IsCorrect);
        Assert.False(AnswerGrader.Grade(single, Options(1, 2)).IsCorrect);

        var multi = Make(QuestionType.MultipleChoice, ("a", true, null), ("b", true, null), ("c", false, null));
        Assert.True(AnswerGrader.AllowsMultiple(multi));
        Assert.False(AnswerGrader.Grade(multi, Options(1)).IsCorrect);
        Assert.True(AnswerGrader.Grade(multi, Options(2, 1)).IsCorrect);
        Assert.Equal(1m, AnswerGrader.Grade(multi, Options(1, 2)).Fraction);
    }

    [Theory]
    [InlineData("días", true)]
    [InlineData(" Días! ", true)]
    [InlineData("dias", false)]   // accents change meaning, so they count
    [InlineData("noches", false)]
    public void Fill_in_ignores_case_and_punctuation_but_not_accents(string answer, bool correct)
    {
        var q = Make(QuestionType.FillBlank, ("días", true, null));
        Assert.Equal(correct, AnswerGrader.Grade(q, Text(answer)).IsCorrect);
    }

    [Fact]
    public void Translate_accepts_any_listed_translation()
    {
        var q = Make(QuestionType.Translate, ("Good morning", true, null), ("Morning", true, null));
        Assert.True(AnswerGrader.Grade(q, Text("good morning.")).IsCorrect);
        Assert.True(AnswerGrader.Grade(q, Text("Morning!")).IsCorrect);
        Assert.False(AnswerGrader.Grade(q, Text("Good night")).IsCorrect);
        Assert.Equal("Good morning", AnswerGrader.Grade(q, Text("x")).CorrectAnswer);
    }

    [Fact]
    public void Reorder_compares_the_built_sentence()
    {
        var q = Make(QuestionType.ReorderWords, ("¿Eres de México?", true, null));
        Assert.Equal(new[] { "¿Eres", "de", "México?" }, AnswerGrader.WordTiles(q));
        Assert.True(AnswerGrader.Grade(q, Text("¿Eres de México?")).IsCorrect);
        Assert.False(AnswerGrader.Grade(q, Text("de ¿Eres México?")).IsCorrect);
    }

    [Fact]
    public void Matching_gives_partial_credit()
    {
        var q = Make(QuestionType.MatchPairs, ("hola", true, "hello"), ("adiós", true, "goodbye"));
        var half = AnswerGrader.Grade(q, AnswerInput.Empty with { Pairs = new Dictionary<int, string> { [1] = "hello", [2] = "hello" } });
        Assert.False(half.IsCorrect);
        Assert.Equal(0.5m, half.Fraction);
        var all = AnswerGrader.Grade(q, AnswerInput.Empty with { Pairs = new Dictionary<int, string> { [1] = "Hello", [2] = "goodbye" } });
        Assert.True(all.IsCorrect);
        Assert.Equal("hola → hello; adiós → goodbye", all.CorrectAnswer);
    }

    [Fact]
    public void Writing_and_speaking_wait_for_a_reviewer_unless_empty()
    {
        var writing = Make(QuestionType.Writing);
        var graded = AnswerGrader.Grade(writing, Text("Me llamo Ana."));
        Assert.True(graded.NeedsReview);
        Assert.Null(graded.IsCorrect);
        Assert.False(AnswerGrader.Grade(writing, AnswerInput.Empty).NeedsReview);
        Assert.False(AnswerGrader.Grade(writing, AnswerInput.Empty).IsCorrect);

        var speak = Make(QuestionType.Speak);
        Assert.True(AnswerGrader.Grade(speak, AnswerInput.Empty with { AudioUrl = "/files/a.wav" }).NeedsReview);
        Assert.False(AnswerGrader.Grade(speak, AnswerInput.Empty).IsCorrect);
        Assert.True(AnswerGrader.IsManuallyGraded(QuestionType.Speak));
    }

    [Theory]
    [InlineData("  ¡Hola,   Ana! ", "hola ana")]
    [InlineData("Don't", "dont")]
    [InlineData("¿Qué tal?", "qué tal")]
    public void Normalize_drops_case_punctuation_and_extra_spaces(string input, string expected) =>
        Assert.Equal(expected, AnswerGrader.Normalize(input));
}

public class ReviewSchedulerTests
{
    private static readonly DateOnly Today = new(2026, 11, 14);
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wrong_answer_comes_back_tomorrow()
    {
        var card = new ReviewCard { Box = 3 };
        Assert.True(ReviewScheduler.Apply(card, false, Today, Now));
        Assert.Equal(1, card.Box);
        Assert.Equal(1, card.Lapses);
        Assert.Equal(Today.AddDays(1), card.DueDate);
    }

    [Fact]
    public void Right_answers_space_the_card_out_until_it_is_learnt()
    {
        var card = new ReviewCard { Box = 1 };
        Assert.True(ReviewScheduler.Apply(card, true, Today, Now));
        Assert.Equal(2, card.Box);
        Assert.Equal(Today.AddDays(2), card.DueDate);
        card.Box = ReviewScheduler.MaxBox;
        Assert.False(ReviewScheduler.Apply(card, true, Today, Now)); // learnt: remove it
    }
}

public class GradeRuleTests
{
    [Theory]
    [InlineData(80, 70, 30, 70, 73)]   // 0.3*80 + 0.7*70
    [InlineData(null, 70, 30, 70, 70)]
    [InlineData(90, null, 30, 70, 90)]
    public void Course_grade_weights_quizzes_and_exams(int? daily, int? exam, int quizWeight, int examWeight, int expected) =>
        Assert.Equal(expected, GradeRules.CourseGrade(daily, exam, quizWeight, examWeight));

    [Fact]
    public void Course_grade_is_empty_without_marks() => Assert.Null(GradeRules.CourseGrade(null, null, 30, 70));

    [Fact]
    public void Placement_stops_at_the_first_level_below_the_pass_percent()
    {
        var answers = new[]
        {
            (CefrLevel.A1, true), (CefrLevel.A1, true),
            (CefrLevel.A2, true), (CefrLevel.A2, false),
            (CefrLevel.B1, false), (CefrLevel.B1, false)
        };
        Assert.Equal(CefrLevel.A2, GradeRules.PlacementLevel(answers, 60, CefrLevel.A1));
        Assert.Equal(CefrLevel.B1, GradeRules.PlacementLevel(answers, 50, CefrLevel.A1));
        Assert.Equal(CefrLevel.A1, GradeRules.PlacementLevel([], 60, CefrLevel.A1));
        Assert.Equal(CefrLevel.B1, GradeRules.PlacementLevel(answers.Select(a => (a.Item1, true)), 60, CefrLevel.A1));
    }

    [Fact]
    public void Daily_quiz_ends_at_the_learners_midnight()
    {
        var utcNow = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), GradeRules.NextMidnightUtc(utcNow, "UTC"));

        // Karachi is UTC+5: 20:00 UTC on the 14th is 01:00 on the 15th there, so the quiz ends at local midnight of the 16th.
        var late = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 19, 0, 0, TimeSpan.Zero), GradeRules.NextMidnightUtc(late, "Asia/Karachi"));
    }

    [Fact]
    public void Match_answers_round_trip()
    {
        var pairs = new Dictionary<int, string> { [3] = "hello", [7] = "good = bye" };
        Assert.Equal(pairs, GradeRules.ParsePairs(GradeRules.PairsToText(pairs)));
        Assert.Equal(new[] { 1, 22 }, GradeRules.ParseCsv(GradeRules.Csv([1, 22])));
        Assert.Equal(67, GradeRules.Percent(2, 3));
    }
}
