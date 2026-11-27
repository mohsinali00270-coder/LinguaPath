using LinguaPath.Application.Features.Ai;
using LinguaPath.Infrastructure.Ai;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 5: prompt rendering, JSON validation, cost and estimates, mapping AI questions, offline provider.</summary>
public class AiPromptTests
{
    [Fact]
    public void Render_fills_known_placeholders_and_keeps_json_schemas()
    {
        var text = AiPrompts.Render("Teach {language} at {level}. Notes: {notes}. Schema: {\"title\": string} {unknown}",
            new Dictionary<string, string?> { ["language"] = "Spanish", ["level"] = "A1", ["notes"] = "" });
        Assert.Equal("Teach Spanish at A1. Notes: none. Schema: {\"title\": string} {unknown}", text);
    }

    [Fact]
    public void Every_feature_has_a_built_in_prompt()
    {
        foreach (var feature in Enum.GetValues<AiFeature>())
        {
            var d = AiPrompts.DefaultFor(AiPrompts.KeyFor(feature));
            Assert.Contains("JSON", d.System);
            Assert.Contains("Schema:", d.Prompt);
        }
    }

    [Fact]
    public void Quiz_prompt_is_grounded_in_the_lecture_text()
    {
        var prompt = AiPrompts.DefaultFor(AiPrompts.QuizFromLecture).Prompt;
        Assert.Contains("ONLY the lecture text", prompt);
        Assert.Contains("{lectureText}", prompt);
        Assert.Contains("sourceSentence", prompt);
    }

    [Fact]
    public void Lecture_text_strips_html_and_adds_examples()
    {
        var text = AiPrompts.LectureText("<h2>Ser</h2><p>Yo <b>soy</b> Ana.</p><table><tr><td>yo</td><td>soy</td></tr></table>",
            [("Soy de México.", "I'm from Mexico.")]);
        Assert.StartsWith("Ser", text);
        Assert.Contains("Yo soy Ana.", text);
        Assert.Contains("- Soy de México. = I'm from Mexico.", text);
        Assert.DoesNotContain("<", text);
    }
}

public class AiOutputParserTests
{
    [Fact]
    public void Extracts_json_from_markdown_fences_and_chatter()
    {
        Assert.Equal("{\"a\":1}", AiOutputParser.ExtractJson("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", AiOutputParser.ExtractJson("Sure! Here it is: {\"a\":1} Hope it helps."));
    }

    [Fact]
    public void Invalid_or_incomplete_output_is_rejected_with_a_reason()
    {
        var (bad, error) = AiOutputParser.Parse<AiRoadmap>("not json", AiOutputParser.ValidateRoadmap);
        Assert.Null(bad);
        Assert.Contains("not valid JSON", error);

        var (empty, why) = AiOutputParser.Parse<AiRoadmap>("{\"modules\": []}", AiOutputParser.ValidateRoadmap);
        Assert.Null(empty);
        Assert.Equal("The roadmap has no modules.", why);
    }

    [Fact]
    public void Valid_roadmap_parses_case_insensitively_with_string_numbers()
    {
        var (roadmap, error) = AiOutputParser.Parse<AiRoadmap>(
            "{\"Title\":\"X\",\"modules\":[{\"title\":\"M1\",\"level\":\"a1\",\"month\":\"1\",\"lectures\":[{\"title\":\"L1\",\"minutes\":\"15\",\"isCore\":true}]}]}",
            AiOutputParser.ValidateRoadmap);
        Assert.Null(error);
        Assert.Equal(15, roadmap!.Modules[0].Lectures[0].Minutes);
        Assert.Equal(CefrLevel.A1, AiOutputParser.Level(roadmap.Modules[0].Level));
    }

    [Theory]
    [InlineData(120, 0.5, "x", false)]
    [InlineData(80, 1.5, "x", false)]
    [InlineData(80, 0.9, "", false)]
    [InlineData(80, 0.9, "Good.", true)]
    public void Writing_grade_is_checked(double score, double confidence, string feedback, bool ok)
    {
        var error = AiOutputParser.ValidateWritingGrade(new AiWritingGrade { Score = score, Confidence = confidence, Feedback = feedback });
        Assert.Equal(ok, error is null);
    }
}

public class AiCostTests
{
    [Fact]
    public void Cost_uses_price_per_million_tokens() =>
        Assert.Equal(0.00082m, AiCost.For(1000, 500, 0.27m, 1.10m));    // 0.00027 + 0.00055

    [Fact]
    public void Estimate_scales_with_hours_and_content()
    {
        var roadmapOnly = AiEstimator.ForCourse(40, withContent: false, questionsPerLecture: 5, 0.27m, 1.10m);
        Assert.Equal(120, roadmapOnly.Lectures);       // 40 h × 60 / 20 min
        Assert.Equal(20, roadmapOnly.Modules);         // 6 per module
        Assert.Equal(0, roadmapOnly.Questions);

        var full = AiEstimator.ForCourse(40, withContent: true, questionsPerLecture: 5, 0.27m, 1.10m);
        Assert.Equal(600, full.Questions);
        Assert.True(full.Cost > roadmapOnly.Cost);
        Assert.Equal(4, AiEstimator.ForCourse(1, false, 5, 1, 1).Lectures);   // never fewer than 4
    }
}

public class AiQuestionMapperTests
{
    private static readonly QuestionType[] All = [];

    [Fact]
    public void True_false_is_normalised()
    {
        var input = AiQuestionMapper.ToInput(new AiQuestionDraft
        {
            Type = "true_false", Stem = "Soy means I am.", Options = [new() { Text = "Yes", Correct = true }, new() { Text = "No" }]
        }, 1, 2, CefrLevel.A1, All)!;
        Assert.Equal(QuestionType.TrueFalse, input.Type);
        Assert.Equal(new[] { "True", "False" }, input.Options.Select(o => o.Text));
        Assert.True(input.Options[0].IsCorrect);    // "Yes" was marked correct, so "True" is the answer
        Assert.False(input.Options[1].IsCorrect);
    }

    [Fact]
    public void Writing_unknown_and_not_allowed_types_are_dropped()
    {
        Assert.Null(AiQuestionMapper.ToInput(new AiQuestionDraft { Type = "Writing", Stem = "x" }, 1, 2, CefrLevel.A1, All));
        Assert.Null(AiQuestionMapper.ToInput(new AiQuestionDraft { Type = "Essay", Stem = "x" }, 1, 2, CefrLevel.A1, All));
        Assert.Null(AiQuestionMapper.ToInput(new AiQuestionDraft { Type = "Translate", Stem = "x", Prompt = "Hola" }, 1, 2, CefrLevel.A1,
            [QuestionType.MultipleChoice]));
    }

    [Fact]
    public void Skill_defaults_follow_the_type()
    {
        var input = AiQuestionMapper.ToInput(new AiQuestionDraft
        {
            Type = "ListenChoose", Stem = "What do you hear?", Prompt = "hola", Options = [new() { Text = "hola", Correct = true }, new() { Text = "adiós" }]
        }, 1, 2, CefrLevel.A2, All)!;
        Assert.Equal(Skill.Listening, input.Skill);
        Assert.Equal(CefrLevel.A2, input.Level);
        Assert.Equal(2, input.LectureId);
    }
}

public class OfflineAiProviderTests
{
    private readonly OfflineAiChatClient _client = new();

    private async Task<(T? Value, string? Error)> AskAsync<T>(AiFeature feature, string prompt, Func<T, string?> validate) where T : class
    {
        var result = await _client.CompleteAsync(new AiChatRequest(feature, "system", prompt, null, 0.3));
        Assert.Equal(AiProviders.Offline, result.Provider);
        return AiOutputParser.Parse(result.Content, validate);
    }

    [Fact]
    public async Task Offline_answers_pass_the_same_validation_as_real_ones()
    {
        var (roadmap, e1) = await AskAsync<AiRoadmap>(AiFeature.Roadmap,
            "Design a course roadmap that takes learners from A1 to B1 in Spanish in about 10 hours of study.", AiOutputParser.ValidateRoadmap);
        Assert.Null(e1);
        Assert.Equal(30, roadmap!.Modules.Sum(m => m.Lectures.Count));
        Assert.Contains("Spanish", roadmap.Title);

        var (lecture, e2) = await AskAsync<AiLectureDraft>(AiFeature.GrammarLecture,
            "Write a lecture for A1 learners of Spanish whose native language is English.\nLecture title: Ser. Topic or grammar point: To be. Objectives: x.",
            AiOutputParser.ValidateLecture);
        Assert.Null(e2);
        Assert.Equal(10, lecture!.Examples.Count);
        Assert.Contains("<table>", lecture.Html);

        var (grade, e3) = await AskAsync<AiWritingGrade>(AiFeature.WritingGrading, "Answer: <<<Hola>>>", AiOutputParser.ValidateWritingGrade);
        Assert.Null(e3);
        Assert.True(grade!.Confidence < 0.6);   // too short: goes to a human
    }

    [Fact]
    public async Task Offline_questions_come_from_the_lecture_text()
    {
        var (set, error) = await AskAsync<AiQuestionSet>(AiFeature.QuizFromLecture,
            "Create 3 questions … Lecture text: <<<Yo soy estudiante de español. Nosotros somos amigos desde niños.>>>", AiOutputParser.ValidateQuestions);
        Assert.Null(error);
        Assert.Equal(3, set!.Questions.Count);
        Assert.All(set.Questions, q => Assert.Contains(q.SourceSentence!, "Yo soy estudiante de español. Nosotros somos amigos desde niños."));
        Assert.Contains("___", set.Questions.Single(q => q.Type == "FillBlank").Stem);
    }
}
