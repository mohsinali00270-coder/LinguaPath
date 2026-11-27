using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Services;
using LinguaPath.Mobile.Core.ViewModels;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 3: lecture HTML safety, reader template, unlock rule and reader helpers.</summary>
public class HtmlSanitizerTests
{
    private readonly HtmlSanitizerService _sanitizer = new();

    [Fact]
    public void Removes_scripts_handlers_and_styles_but_keeps_lesson_markup()
    {
        var html = _sanitizer.Sanitize("""
            <h2 style="color:red">Greetings</h2>
            <p onclick="alert(1)">Say <b>hola</b>.<script>alert('x')</script></p>
            <table><tr><th>Word</th><td>hola</td></tr></table>
            <a href="javascript:alert(1)">bad</a> <a href="https://example.com">good</a>
            <img src="data:image/png;base64,AAAA" />
            """);

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("style=", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("data:image", html);
        Assert.Contains("<h2>Greetings</h2>", html);
        Assert.Contains("<b>hola</b>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void Empty_input_gives_empty_string() => Assert.Equal(string.Empty, _sanitizer.Sanitize("   "));
}

public class LectureHtmlTests
{
    [Fact]
    public void Rtl_and_language_are_set_on_the_page()
    {
        var page = LectureHtml.Build("<p>مرحبا</p>", TextDirection.Rtl, languageCode: "ar");
        Assert.Contains("dir=\"rtl\"", page);
        Assert.Contains("lang=\"ar\"", page);
        Assert.DoesNotContain("<script", page);
    }

    [Fact]
    public void Interactive_page_reports_tapped_words_to_the_app()
    {
        var page = LectureHtml.Build("<p>hola</p>", interactive: true);
        Assert.Contains("<script>", page);
        Assert.Contains(LectureHtml.WordScheme + "://word?w=", page);
    }

    [Fact]
    public void Dark_mode_changes_colours()
    {
        Assert.Contains("#080F22", LectureHtml.Build("x", dark: true));
        Assert.NotEqual(LectureHtml.Build("x"), LectureHtml.Build("x", dark: true));
    }
}

public class UnlockRuleTests
{
    private static List<LearnerRoadmapItem> Items(params RoadmapItemState[] states) =>
        states.Select((s, i) => new LearnerRoadmapItem { SortOrder = i + 1, State = s }).ToList();

    [Fact]
    public void Sequential_unlock_opens_only_the_first_unfinished_lecture()
    {
        var items = Items(RoadmapItemState.Completed, RoadmapItemState.Locked, RoadmapItemState.Locked);
        RoadmapRules.ApplyUnlock(items, sequential: true);
        Assert.Equal(new[] { RoadmapItemState.Completed, RoadmapItemState.Available, RoadmapItemState.Locked }, items.Select(i => i.State));
    }

    [Fact]
    public void In_progress_lectures_stay_in_progress()
    {
        var items = Items(RoadmapItemState.InProgress, RoadmapItemState.Locked);
        RoadmapRules.ApplyUnlock(items, sequential: true);
        Assert.Equal(new[] { RoadmapItemState.InProgress, RoadmapItemState.Locked }, items.Select(i => i.State));
    }

    [Fact]
    public void Free_order_opens_everything_not_finished()
    {
        var items = Items(RoadmapItemState.Completed, RoadmapItemState.Locked, RoadmapItemState.Locked);
        RoadmapRules.ApplyUnlock(items, sequential: false);
        Assert.Equal(new[] { RoadmapItemState.Completed, RoadmapItemState.Available, RoadmapItemState.Available }, items.Select(i => i.State));
    }
}

public class LectureReaderHelperTests
{
    [Fact]
    public void Plain_text_for_read_aloud_drops_tags_and_decodes_entities()
    {
        var text = LectureViewModel.PlainText("<h2>Greetings</h2><p>Say <b>hola</b> &amp; smile.</p><ul><li>adiós</li></ul>");
        Assert.Equal("Greetings. Say hola & smile. adiós", text);
    }

    [Theory]
    [InlineData("Hola", "hola")]
    [InlineData(" ¡Hola! ", "hola")]
    [InlineData("Adiós", "adiós")]
    public void Word_lookup_ignores_case_and_punctuation(string input, string expected) =>
        Assert.Equal(expected, LectureViewModel.Normalize(input));
}
