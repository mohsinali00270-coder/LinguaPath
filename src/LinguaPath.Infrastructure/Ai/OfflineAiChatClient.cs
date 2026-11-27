using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Shared;

namespace LinguaPath.Infrastructure.Ai;

/// <summary>
/// Deterministic stand-in for a real model: answers every feature with valid, clearly-labelled sample JSON built from the prompt.
/// Used in tests, and in development when no API key is configured (Ai:OfflineWhenNoKey). Costs nothing.
/// </summary>
internal sealed class OfflineAiChatClient : IAiChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        object answer = request.Feature switch
        {
            AiFeature.Roadmap => Roadmap(request.User),
            AiFeature.GrammarLecture => Lecture(request.User),
            AiFeature.LectureAssist => Assist(request.User),
            AiFeature.QuizFromLecture => Questions(request.User),
            AiFeature.Posts => Posts(request.User),
            AiFeature.WritingGrading => Grade(request.User),
            AiFeature.WritingCheck => Check(request.User),
            AiFeature.Conversation => Converse(request.User),
            AiFeature.PracticeItems => Practice(request.User),
            AiFeature.Moderation => Moderate(request.User),
            _ => new { }
        };
        var content = JsonSerializer.Serialize(answer, Json);
        return Task.FromResult(new AiChatResult(content, AiProviders.Offline, AiProviders.OfflineModel,
            (request.System.Length + request.User.Length) / 4, content.Length / 4));
    }

    private static string Find(string text, string pattern, string fallback)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : fallback;
    }

    private static object Roadmap(string prompt)
    {
        var start = Enum.Parse<CefrLevel>(Find(prompt, @"from (A1|A2|B1|B2|C1|C2)", "A1"));
        var end = Enum.Parse<CefrLevel>(Find(prompt, @"to (A1|A2|B1|B2|C1|C2)", "A2"));
        var language = Find(prompt, @"in ([\p{L} ]+?) in about", "the language");
        var hours = int.Parse(Find(prompt, @"about (\d+) hours", "20"), CultureInfo.InvariantCulture);
        var levels = Enumerable.Range((int)start, Math.Max(1, end - start + 1)).Select(l => (CefrLevel)l).ToList();
        var lectures = Math.Clamp(hours * 60 / 20, 4, 60);
        var modules = Math.Max(1, (int)Math.Ceiling(lectures / 5.0));
        string[] topics = ["Greetings and introductions", "Numbers and time", "Family and friends", "Food and drink", "Daily routine",
            "Getting around", "Shopping", "Past events", "Plans and wishes", "Opinions"];

        return new
        {
            title = $"{language} {start} → {end} (sample AI roadmap)",
            description = $"Offline sample roadmap for {language}. Replace with a real AI provider in Settings.",
            modules = Enumerable.Range(0, modules).Select(m => new
            {
                title = topics[m % topics.Length],
                objective = $"Talk about {topics[m % topics.Length].ToLowerInvariant()}.",
                level = levels[Math.Min(levels.Count - 1, m * levels.Count / modules)].ToString(),
                month = 1 + m / 2,
                lectures = Enumerable.Range(1, Math.Min(5, lectures - m * 5)).Select(i => new
                {
                    title = $"{topics[m % topics.Length]} {i}",
                    objective = $"Use key phrases for {topics[m % topics.Length].ToLowerInvariant()}.",
                    grammarPoint = i % 2 == 0 ? "Present tense" : "Questions",
                    minutes = 15 + 5 * (i % 2),
                    isCore = i != 5
                }).ToList()
            }).Where(m => m.lectures.Count > 0).ToList()
        };
    }

    private static object Lecture(string prompt)
    {
        var title = Find(prompt, @"Lecture title:\s*(.+?)\.\s", "Sample lecture");
        var topic = Find(prompt, @"grammar point:\s*(.+?)\.\s", title);
        var level = Find(prompt, @"for (A1|A2|B1|B2|C1|C2) learners", "A1");
        return new
        {
            objectives = $"Understand and use: {topic}.",
            html = $"""
                <h2>{title}</h2>
                <p><em>Sample text from the offline AI generator ({level}). Connect a real AI provider to get a full lesson.</em></p>
                <p>This lecture explains <strong>{topic}</strong> with short rules and examples.</p>
                <h3>Rule</h3>
                <table><thead><tr><th>Form</th><th>Use</th></tr></thead><tbody><tr><td>Pattern A</td><td>Everyday statements</td></tr><tr><td>Pattern B</td><td>Questions</td></tr></tbody></table>
                <h3>Common mistakes</h3>
                <ul><li>Forgetting the article.</li><li>Using the wrong word order in questions.</li></ul>
                <h3>Summary</h3>
                <p>Practise the patterns with the examples below.</p>
                """,
            examples = Enumerable.Range(1, 10).Select(i => new { target = $"Sample sentence {i} for {topic}", translation = $"Translation {i}", note = "" }).ToList(),
            vocabulary = Enumerable.Range(1, 5).Select(i => new { word = $"word{i}", meaning = $"meaning {i}", pronunciation = "", partOfSpeech = "noun" }).ToList()
        };
    }

    private static object Assist(string prompt)
    {
        var text = Find(prompt, @"<<<([\s\S]*)>>>", "<p>Text</p>");
        var task = Find(prompt, @"Task:\s*(.+)", "Improve this text.");
        return new { html = text + $"<p><em>(Offline AI assist: {WebEncode(task)})</em></p>" };
    }

    private static string WebEncode(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static object Questions(string prompt)
    {
        var count = int.Parse(Find(prompt, @"Create (\d+) questions", "5"), CultureInfo.InvariantCulture);
        var lectureText = Find(prompt, @"<<<([\s\S]*)>>>", "");
        var sentences = Regex.Split(lectureText, @"(?<=[.!?])\s+|\n")
            .Select(s => s.Trim().TrimStart('-', ' ')).Where(s => s.Length > 12 && s.Split(' ').Length >= 3).Distinct().ToList();
        if (sentences.Count == 0) sentences = ["This lecture has very little text."];

        var questions = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var sentence = sentences[i % sentences.Count];
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var key = words.OrderByDescending(w => w.Length).First().Trim('.', ',', '!', '?', ';', ':', '"');
            switch (i % 3)
            {
                case 0:
                    questions.Add(new
                    {
                        type = "MultipleChoice", skill = "Reading", difficulty = 2,
                        stem = "Which sentence appears in the lecture?",
                        prompt = (string?)null,
                        options = new object[]
                        {
                            new { text = sentence, correct = true, match = (string?)null },
                            new { text = "None of these sentences.", correct = false, match = (string?)null },
                            new { text = "The lecture doesn't say.", correct = false, match = (string?)null }
                        },
                        explanation = "The sentence is quoted from the lecture.",
                        sourceSentence = sentence
                    });
                    break;
                case 1:
                    questions.Add(new
                    {
                        type = "FillBlank", skill = "Vocabulary", difficulty = 3,
                        stem = sentence.Replace(key, "___"),
                        prompt = (string?)null,
                        options = new object[] { new { text = key, correct = true, match = (string?)null } },
                        explanation = $"The missing word is \"{key}\".",
                        sourceSentence = sentence
                    });
                    break;
                default:
                    questions.Add(new
                    {
                        type = "TrueFalse", skill = "Reading", difficulty = 1,
                        stem = $"The lecture says: \"{sentence}\"",
                        prompt = (string?)null,
                        options = new object[]
                        {
                            new { text = "True", correct = true, match = (string?)null },
                            new { text = "False", correct = false, match = (string?)null }
                        },
                        explanation = "This sentence is in the lecture.",
                        sourceSentence = sentence
                    });
                    break;
            }
        }
        return new { questions };
    }

    private static object Posts(string prompt)
    {
        var count = int.Parse(Find(prompt, @"Write (\d+) different", "30"), CultureInfo.InvariantCulture);
        var kind = Find(prompt, "different \"([^\"]+)\" posts", "tip");
        return new
        {
            posts = Enumerable.Range(1, count).Select(i => new
            {
                title = $"Sample {kind} #{i}",
                body = $"Offline sample {kind} number {i}. Connect a real AI provider for real posts.",
                tags = new[] { "sample" }
            }).ToList()
        };
    }

    private static object Grade(string prompt)
    {
        var answer = Find(prompt, @"<<<([\s\S]*?)>>>", "");
        var words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        // Short answers get a low confidence, so they go to a human reviewer.
        return new
        {
            score = Math.Min(90, 40 + words * 5),
            confidence = words >= 8 ? 0.9 : 0.4,
            feedback = words >= 8 ? "Clear answer with good basic vocabulary (offline sample grade)." : "Too short to judge (offline sample grade).",
            corrections = Array.Empty<object>()
        };
    }

    private static object Check(string prompt)
    {
        var text = Find(prompt, @"<<<([\s\S]*?)>>>", "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A visible, deterministic "correction" so the screens can be tried without a provider: lower-case first letter.
        var corrections = text.Length > 0 && char.IsLower(text[0])
            ? new[] { new { original = words[0], corrected = char.ToUpperInvariant(words[0][0]) + words[0][1..], explanation = "Start a sentence with a capital letter (offline sample)." } }
            : Array.Empty<object>().Select(_ => new { original = "", corrected = "", explanation = "" }).ToArray();
        return new
        {
            score = Math.Min(95, 40 + words.Length * 3 - corrections.Length * 10),
            confidence = 0.8,
            feedback = words.Length < 10 ? "Good start. Try to write a little more (offline sample check)." : "Well organised text (offline sample check).",
            corrections
        };
    }

    private static object Converse(string prompt)
    {
        var message = Find(prompt, @"<<<([\s\S]*?)>>>", "");
        var topic = Find(prompt, @"Topic:\s*(.+?)\.\s", "your day");
        return new
        {
            reply = $"(offline sample) Interesting! You said: \"{(message.Length > 60 ? message[..60] + "…" : message)}\". Tell me more about {topic}?",
            correction = message.Length > 0 && char.IsLower(message[0]) ? char.ToUpperInvariant(message[0]) + message[1..] : null,
            explanation = message.Length > 0 && char.IsLower(message[0]) ? "Sentences start with a capital letter." : null
        };
    }

    /// <summary>Keyword heuristic so tests and offline development get realistic moderation verdicts.</summary>
    private static object Moderate(string prompt)
    {
        var message = Find(prompt, @"<<<([\s\S]*?)>>>", "").ToLowerInvariant();
        (string Category, double Score)[] rules =
        [
            (Regex.IsMatch(message, @"\b(i will|i'll|gonna) (kill|hurt|find) you\b") ? "threat" : "", 96),
            (Regex.IsMatch(message, @"\b(go back to your country|your kind)\b") ? "hate" : "", 93),
            (Regex.IsMatch(message, @"\b(send (me )?nudes|sexy pics)\b") ? "sexual" : "", 94),
            (Regex.IsMatch(message, @"\b(you are|you're|ur) (so )?(stupid|an idiot|worthless|ugly)\b") ? "insult" : "", 78),
            (Regex.IsMatch(message, @"\b(nobody likes you|shut up loser)\b") ? "harassment" : "", 74),
            (Regex.IsMatch(message, @"\b(wire me|gift card|crypto wallet)\b") ? "scam" : "", 72)
        ];
        var hit = rules.FirstOrDefault(r => r.Category.Length > 0);
        return hit.Category is { Length: > 0 }
            ? new { category = hit.Category, score = hit.Score, reason = $"(offline sample) Looks like {hit.Category}." }
            : new { category = "none", score = 3.0, reason = "(offline sample) Nothing harmful found." };
    }

    private static object Practice(string prompt)
    {
        var count = Math.Clamp(int.Parse(Find(prompt, @"Write (\d+)", "3"), CultureInfo.InvariantCulture), 1, 20);
        var reading = prompt.Contains("reading passages", StringComparison.OrdinalIgnoreCase);
        var writing = prompt.Contains("writing tasks", StringComparison.OrdinalIgnoreCase);
        var topic = Find(prompt, @"Topic:\s*(.+?)\.\s", "everyday life");
        var questions = new[]
        {
            new { question = "Where does Anna walk every morning?", options = new[] { "To school", "To the market", "To the park" }, correct = 1, explanation = "She walks to the market." },
            new { question = "Who does she visit on Saturday?", options = new[] { "Her friend", "Her teacher", "Her grandmother" }, correct = 2, explanation = "She visits her grandmother." },
            new { question = "What does she NOT buy?", options = new[] { "Bread", "Cheese", "Milk" }, correct = 1, explanation = "Cheese isn't mentioned." }
        };
        return new
        {
            items = Enumerable.Range(1, count).Select(i => new
            {
                title = $"{topic} {i} (sample)",
                text = reading
                    ? $"Anna lives in a small town. Every morning she walks to the market. She buys bread, apples and milk. On Saturday {i} she visits her grandmother and they cook together."
                    : writing ? $"Write 40-60 words to a friend about {topic}. Say what you did and how you felt."
                    : $"Today I am practising sentence number {i}.",
                translation = "(offline sample translation)",
                questions = reading ? questions : questions.Take(0).ToArray()
            }).ToList()
        };
    }
}
