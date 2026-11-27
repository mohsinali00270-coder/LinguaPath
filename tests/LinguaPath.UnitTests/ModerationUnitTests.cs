using LinguaPath.Application.Features.Moderation;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 8: word filter, link/contact filter (FR-M01, FR-M05 .. FR-M09, BR-25).</summary>
public class ContentFilterTests
{
    private static readonly ContentFilter Filter = new(
    [
        new FilterWord("damn", ModerationSeverity.Mild),
        new FilterWord("stupid", ModerationSeverity.Abusive),
        new FilterWord("idiot", ModerationSeverity.Abusive),
        new FilterWord("ass", ModerationSeverity.Abusive),
        new FilterWord("kill yourself", ModerationSeverity.Severe),
        new FilterWord("tonto", ModerationSeverity.Mild, LanguageId: 2)
    ]);

    [Theory]
    [InlineData("you are stupid")]
    [InlineData("you are STUPID!!")]
    [InlineData("you are stuuuupid")]
    [InlineData("you are s t u p i d")]
    [InlineData("you are s.t.u.p.i.d")]
    [InlineData("you are 5tup1d")]
    [InlineData("what an idiots move")]
    [InlineData("stúpid")]
    public void Finds_disguised_abusive_words(string text) => Assert.Equal(ModerationSeverity.Abusive, Filter.Check(text).Severity);

    [Theory]
    [InlineData("I'm in class now")]
    [InlineData("an assistant helped me")]
    [InlineData("I passed the exam")]
    [InlineData("Hello, how are you?")]
    public void Leaves_innocent_words_alone(string text) => Assert.Equal(ModerationSeverity.None, Filter.Check(text).Severity);

    [Fact]
    public void Phrases_and_severity()
    {
        var r = Filter.Check("just kill yourself");
        Assert.Equal(ModerationSeverity.Severe, r.Severity);
        Assert.Equal("kill yourself", r.Words[0].Term);
        Assert.Equal(ModerationSeverity.Severe, Filter.Check("kill   your-self").Severity);
        Assert.Equal(ModerationSeverity.Severe, Filter.Check("killyourself").Severity);
        Assert.Equal(ModerationSeverity.Abusive, Filter.Check("so stu pid").Severity);
    }

    [Fact]
    public void Mild_words_are_masked_keeping_the_first_letter()
    {
        var r = Filter.Check("Damn, that was hard");
        Assert.Equal(ModerationSeverity.Mild, r.Severity);
        Assert.Equal("D***, that was hard", r.MaskedText);
    }

    [Fact]
    public void Language_specific_words_only_apply_to_that_language()
    {
        Assert.Equal(ModerationSeverity.Mild, Filter.Check("eres tonto", 2).Severity);
        Assert.Equal(ModerationSeverity.None, Filter.Check("eres tonto", 3).Severity);
        // No chat language known: every word counts.
        Assert.Equal(ModerationSeverity.Mild, Filter.Check("eres tonto").Severity);
    }

    [Theory]
    [InlineData("visit https://example.com now", ContactKind.Url)]
    [InlineData("go to www.spam.net", ContactKind.Url)]
    [InlineData("see example.com", ContactKind.Domain)]
    [InlineData("see example dot com", ContactKind.Domain)]
    [InlineData("see example (dot) org", ContactKind.Domain)]
    [InlineData("mail me: ali.khan@mail.com", ContactKind.Email)]
    [InlineData("ali.khan at gmail", ContactKind.Email)]
    [InlineData("call +92 300 1234567", ContactKind.Phone)]
    [InlineData("my number 0300-123-4567", ContactKind.Phone)]
    [InlineData("zero three zero zero one two three four five six seven", ContactKind.Phone)]
    [InlineData("add me on snapchat", ContactKind.SocialHandle)]
    [InlineData("my insta is ali_k99", ContactKind.SocialHandle)]
    [InlineData("join t.me/mygroup", ContactKind.Url)]
    public void Finds_links_and_contacts(string text, ContactKind kind)
    {
        var contacts = Filter.FindContacts(text);
        Assert.Contains(contacts, c => c.Kind == kind);
    }

    [Theory]
    [InlineData("I was born in 1990-2000, no wait 1995")]
    [InlineData("one two three four five six seven eight")]
    [InlineData("I was happy.In the morning I ran")]
    [InlineData("I'm at home now")]
    [InlineData("@Ali Khan thanks for the help")]
    [InlineData("We met at 10.30 and stayed until 12")]
    public void Normal_sentences_are_not_contacts(string text) => Assert.Empty(Filter.FindContacts(text));

    [Fact]
    public void Admin_patterns_and_exceptions()
    {
        var f = new ContentFilter([], [new FilterPattern(@"\bdiscord\b", false), new FilterPattern(@"linguapath\.com", true)]);
        Assert.Contains(f.FindContacts("let's talk on discord"), c => c.Kind == ContactKind.Custom);
        Assert.Empty(f.FindContacts("read linguapath.com/help"));
        Assert.NotEmpty(f.FindContacts("read other.com/help"));
        // A broken pattern is ignored rather than crashing the filter.
        Assert.Null(ContentFilter.TryRegex("(unclosed"));
    }

    [Fact]
    public void Normalisation()
    {
        Assert.Equal("stupid", ContentFilter.NormalizeWord("STUUUP1D"));
        Assert.Equal("2024", ContentFilter.NormalizeWord("2024"));
        // Runs of a letter collapse to one on both sides, so "hello" on the list still matches "hellooo".
        Assert.Equal("helo world", ContentFilter.NormalizeText("Hellooo,   World!!"));
    }
}

/// <summary>Phase 8: strike ladder, spam, AI thresholds and chat rules (BR-25 .. BR-31, FR-M02, FR-M04).</summary>
public class ModerationRulesTests
{
    [Theory]
    [InlineData(1, ModerationSeverity.Abusive, LadderStep.Warning)]
    [InlineData(2, ModerationSeverity.Abusive, LadderStep.Suspension)]
    [InlineData(3, ModerationSeverity.Abusive, LadderStep.Block)]
    [InlineData(5, ModerationSeverity.Abusive, LadderStep.Block)]
    [InlineData(1, ModerationSeverity.Severe, LadderStep.Block)]
    public void Strike_ladder(int strikes, ModerationSeverity severity, LadderStep expected) =>
        Assert.Equal(expected, ModerationRules.StepFor(strikes, severity));

    [Theory]
    [InlineData(1, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, false)]
    [InlineData(10, 5, true)]
    [InlineData(3, 0, false)]
    public void Link_attempts_turn_into_strikes(int attempts, int perStrike, bool due) =>
        Assert.Equal(due, ModerationRules.LinkStrikeDue(attempts, perStrike));

    [Fact]
    public void Broadcast_spam()
    {
        const string text = "buy cheap followers now";
        var recent = new[] { (1, text), (2, text), (3, text), (4, text) };
        Assert.True(ModerationRules.IsBroadcastSpam(recent, 5, text, 5));
        Assert.False(ModerationRules.IsBroadcastSpam(recent, 4, text, 5));        // same chat again: still 4 chats
        Assert.False(ModerationRules.IsBroadcastSpam([(1, "hi"), (2, "hi"), (3, "hi"), (4, "hi")], 5, "hi", 5)); // short greetings
    }

    [Theory]
    [InlineData("none", 99, ModerationSeverity.None)]
    [InlineData("insult", 75, ModerationSeverity.Abusive)]
    [InlineData("insult", 95, ModerationSeverity.Abusive)]
    [InlineData("threat", 95, ModerationSeverity.Severe)]
    [InlineData("threat", 80, ModerationSeverity.Abusive)]
    [InlineData("hate", 60, ModerationSeverity.Mild)]
    [InlineData("insult", 30, ModerationSeverity.None)]
    public void Ai_thresholds(string category, double score, ModerationSeverity expected) =>
        Assert.Equal(expected, ModerationRules.AiSeverity(category, score, 70, 90));

    [Fact]
    public void Direct_key_is_order_independent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.Equal(ModerationRules.DirectKey(a, b), ModerationRules.DirectKey(b, a));
    }

    [Fact]
    public void Delete_for_everyone_window()
    {
        var sent = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.True(ModerationRules.CanDeleteForEveryone(sent, sent.AddHours(23), 24));
        Assert.False(ModerationRules.CanDeleteForEveryone(sent, sent.AddHours(25), 24));
    }

    [Theory]
    [InlineData(AudienceSetting.Everyone, false, true)]
    [InlineData(AudienceSetting.FriendsOnly, false, false)]
    [InlineData(AudienceSetting.FriendsOnly, true, true)]
    [InlineData(AudienceSetting.Nobody, true, false)]
    public void Privacy_audience(AudienceSetting setting, bool friends, bool allowed) =>
        Assert.Equal(allowed, ModerationRules.AudienceAllows(setting, friends));

    [Fact]
    public void Slow_mode()
    {
        var now = new DateTimeOffset(2026, 1, 1, 10, 0, 30, TimeSpan.Zero);
        Assert.Equal(30, ModerationRules.SlowModeWait(60, now.AddSeconds(-30), now, MemberRole.Member));
        Assert.Equal(0, ModerationRules.SlowModeWait(60, now.AddSeconds(-30), now, MemberRole.Admin));
        Assert.Equal(0, ModerationRules.SlowModeWait(60, now.AddSeconds(-61), now, MemberRole.Member));
        Assert.Equal(0, ModerationRules.SlowModeWait(0, now, now, MemberRole.Member));
    }

    [Fact]
    public void Mentions_prefer_longer_names()
    {
        var ali = Guid.NewGuid();
        var aliKhan = Guid.NewGuid();
        var sara = Guid.NewGuid();
        var found = ModerationRules.Mentions("@Ali Khan and @sara, see you", [(ali, "Ali"), (aliKhan, "Ali Khan"), (sara, "Sara")]);
        Assert.Contains(aliKhan, found);
        Assert.Contains(sara, found);
        Assert.DoesNotContain(ali, found);
        Assert.Empty(ModerationRules.Mentions("no mentions here", [(ali, "Ali")]));
    }
}
