using System.Buffers.Binary;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Application.Features.Practice;
using LinguaPath.Domain.Entities;
using LinguaPath.Infrastructure.Ai;
using LinguaPath.Infrastructure.LanguageServices;
using LinguaPath.Shared;

namespace LinguaPath.UnitTests;

/// <summary>Phase 6: SM-2 flashcards (FR-L94).</summary>
public class Sm2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Good_answers_follow_1_then_6_then_interval_times_ease()
    {
        var w = new UserWord { NextReviewAt = Now };
        Sm2.Review(w, RecallGrade.Good, Now);
        Assert.Equal(1, w.IntervalDays);
        Assert.Equal(Now.AddDays(1), w.NextReviewAt);
        Sm2.Review(w, RecallGrade.Good, Now);
        Assert.Equal(6, w.IntervalDays);
        Sm2.Review(w, RecallGrade.Good, Now);
        Assert.Equal(15, w.IntervalDays);      // round(6 × 2.5)
        Assert.Equal(2.5, w.EaseFactor);       // q = 4 leaves the ease unchanged
        Assert.Equal(3, w.Repetitions);
    }

    [Fact]
    public void Again_resets_repetitions_counts_a_lapse_and_lowers_ease_but_not_below_1_3()
    {
        var w = new UserWord { Repetitions = 4, IntervalDays = 30, EaseFactor = 1.4 };
        Sm2.Review(w, RecallGrade.Again, Now);
        Assert.Equal(0, w.Repetitions);
        Assert.Equal(1, w.IntervalDays);
        Assert.Equal(1, w.Lapses);
        Assert.Equal(Sm2.MinEase, w.EaseFactor);
    }

    [Fact]
    public void Easy_raises_ease_and_stretches_the_interval()
    {
        var easy = new UserWord { Repetitions = 1, IntervalDays = 1 };
        var good = new UserWord { Repetitions = 1, IntervalDays = 1 };
        Sm2.Review(easy, RecallGrade.Easy, Now);
        Sm2.Review(good, RecallGrade.Good, Now);
        Assert.True(easy.IntervalDays > good.IntervalDays);
        Assert.Equal(2.6, easy.EaseFactor);
        // Hard keeps the card but lowers the ease.
        var hard = new UserWord();
        Sm2.Review(hard, RecallGrade.Hard, Now);
        Assert.Equal(1, hard.Repetitions);
        Assert.Equal(2.36, hard.EaseFactor);
    }

    [Fact]
    public void Intervals_are_capped_at_a_year()
    {
        var w = new UserWord { Repetitions = 10, IntervalDays = 300, EaseFactor = 2.5 };
        Sm2.Review(w, RecallGrade.Good, Now);
        Assert.Equal(365, w.IntervalDays);
    }
}

/// <summary>Phase 6: dictation diff (FR-L93).</summary>
public class DictationTests
{
    [Fact]
    public void Perfect_answer_ignores_case_and_punctuation()
    {
        var r = Dictation.Compare("Me levanto a las siete.", "me levanto a las siete");
        Assert.Equal(100, r.Score);
        Assert.All(r.Words, w => Assert.Equal(DiffKind.Correct, w.Kind));
    }

    [Fact]
    public void Accents_count_and_a_misspelling_is_shown_against_the_expected_word()
    {
        var r = Dictation.Compare("El tren sale del andén", "El tren sale del anden");
        var wrong = Assert.Single(r.Words, w => w.Kind == DiffKind.Wrong);
        Assert.Equal("anden", wrong.Text);
        Assert.Equal("andén", wrong.Expected);
        Assert.Equal(80, r.Score);
    }

    [Fact]
    public void Missing_and_extra_words_are_marked()
    {
        var r = Dictation.Compare("Mi hermana tiene dos gatos negros", "Mi hermana tiene muy dos gatos");
        Assert.Contains(r.Words, w => w.Kind == DiffKind.Extra && w.Text == "muy");
        Assert.Contains(r.Words, w => w.Kind == DiffKind.Missing && w.Expected == "negros");
        Assert.Equal(5, r.Words.Count(w => w.Kind == DiffKind.Correct));
        Assert.Equal((int)Math.Round(100.0 * 5 / 6), r.Score);
    }

    [Fact]
    public void Empty_answer_scores_zero()
    {
        var r = Dictation.Compare("Hola amigo", "");
        Assert.Equal(0, r.Score);
        Assert.Equal(2, r.Words.Count(w => w.Kind == DiffKind.Missing));
    }
}

/// <summary>Phase 6: reading questions, daily limits, cache keys, language codes, speaking marks.</summary>
public class PracticeRulesTests
{
    [Fact]
    public void Reading_questions_hide_answers_for_learners_and_grade_by_index()
    {
        var json = ReadingQuestions.Serialize(
        [
            new ReadingQuestions.Stored { Question = "Q1", Options = ["a", "b"], Correct = 1, Explanation = "b!" },
            new ReadingQuestions.Stored { Question = "Q2", Options = ["x", "y", "z"], Correct = 0 },
            new ReadingQuestions.Stored { Question = "", Options = ["only"], Correct = 0 } // dropped: no text, one option
        ]);
        var forLearner = ReadingQuestions.ForLearner(json);
        Assert.Equal(2, forLearner.Count);
        Assert.Equal(new[] { "x", "y", "z" }, forLearner[1].Options);

        var result = ReadingQuestions.Grade(json, [1, 2]);
        Assert.Equal(1, result.Correct);
        Assert.Equal(50, result.Score);
        Assert.Equal(0, result.Results[1].CorrectIndex);
        Assert.Equal("b!", result.Results[0].Explanation);
        Assert.Empty(ReadingQuestions.Parse("not json"));
    }

    [Fact]
    public void Day_starts_at_the_learners_local_midnight()
    {
        var now = new DateTimeOffset(2026, 11, 14, 3, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), PracticeRules.DayStartUtc(now, "UTC"));
        // Karachi is UTC+5: 08:00 local on the 14th, so the day began at 19:00 UTC on the 13th.
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 19, 0, 0, TimeSpan.Zero), PracticeRules.DayStartUtc(now, "Asia/Karachi"));
        Assert.Equal(PracticeRules.DayStartUtc(now, "UTC"), PracticeRules.DayStartUtc(now, "Not/AZone"));
    }

    [Fact]
    public void Cache_key_ignores_extra_spaces_and_code_case_but_not_text_case()
    {
        var a = PracticeRules.CacheKey("es", "EN", "Hola   amigo ");
        Assert.Equal(a, PracticeRules.CacheKey("ES", "en", "Hola amigo"));
        Assert.NotEqual(a, PracticeRules.CacheKey("es", "en", "hola amigo"));
        Assert.NotEqual(a, PracticeRules.CacheKey("auto", "en", "Hola amigo"));
        Assert.Equal(64, a.Length);
    }

    [Theory]
    [InlineData("es-MX", "es")]
    [InlineData("EN_us", "en")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("ur", "ur")]
    public void Language_code_drops_the_region(string input, string expected) => Assert.Equal(expected, PracticeRules.LanguageCode(input));

    [Fact]
    public void Speaking_marks_use_the_overall_score_and_never_trust_samples()
    {
        var real = new PronunciationResult("hola", 80, 90, 100, 85, 0.9, [], false);
        Assert.Equal(85, PracticeRules.SpeakScore(real));
        Assert.Equal(0.9, PracticeRules.SpeakConfidence(real));

        var noOverall = real with { Overall = 0 };
        Assert.Equal(86, PracticeRules.SpeakScore(noOverall)); // 80×0.6 + 90×0.2 + 100×0.2

        Assert.Equal(0, PracticeRules.SpeakConfidence(real with { IsSample = true }));
        Assert.Equal(0.5, PracticeRules.SpeakConfidence(real with { Completeness = 40 }));
    }
}

/// <summary>Phase 6: WAV reading and conversion to 16 kHz mono for speech services.</summary>
public class WavAudioTests
{
    /// <summary>A stereo 16-bit 44.1 kHz WAV like a phone recording.</summary>
    private static byte[] StereoWav(double seconds, int rate = 44100)
    {
        var frames = (int)(seconds * rate);
        var data = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 10000);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4), v);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 4 + 2), v);
        }
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + data.Length);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), rate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), data.Length);
        return [.. header, .. data];
    }

    [Fact]
    public void Reads_a_stereo_recording_and_converts_it_to_16k_mono()
    {
        var input = StereoWav(2);
        var read = WavAudio.Read(input);
        Assert.NotNull(read);
        Assert.Equal(44100, read.Value.Info.SampleRate);
        Assert.Equal(2, read.Value.Info.Channels);
        Assert.Equal(2, read.Value.Info.Duration.TotalSeconds, 2);

        var output = WavAudio.ToSpeechFormat(input)!;
        var converted = WavAudio.Read(output)!.Value;
        Assert.Equal(16000, converted.Info.SampleRate);
        Assert.Equal(1, converted.Info.Channels);
        Assert.Equal(16, converted.Info.BitsPerSample);
        Assert.Equal(2, converted.Info.Duration.TotalSeconds, 1);
        Assert.Equal(44 + 32000 * 2, output.Length);
        // The tone survives: loudness is about the same.
        Assert.InRange(converted.Mono.Max(), 0.25f, 0.35f);
    }

    [Fact]
    public void Rejects_files_that_are_not_wav()
    {
        Assert.Null(WavAudio.ToSpeechFormat("ftypM4A some m4a bytes"u8.ToArray()));
        Assert.Null(WavAudio.ToSpeechFormat([]));
        Assert.False(WavAudio.IsWav("RIFFxxxxAVI "u8));
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var samples = Enumerable.Range(0, 1600).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        var back = WavAudio.Read(WavAudio.Write(samples, 16000))!.Value;
        Assert.Equal(samples.Length, back.Mono.Length);
        Assert.Equal(samples[123], back.Mono[123], 3);
    }
}

/// <summary>Phase 6: offline stand-ins and the new AI outputs.</summary>
public class OfflineLanguageServiceTests
{
    [Theory]
    [InlineData("Hola, ¿cómo estás? Gracias por la ayuda", "es")]
    [InlineData("Bonjour, je suis content et vous", "fr")]
    [InlineData("Ich bin müde und das ist gut", "de")]
    [InlineData("Hello, how are you and the family", "en")]
    [InlineData("مرحبا كيف حالك", "ar")]
    [InlineData("こんにちは", "ja")]
    [InlineData("xyz qqq", "en")]
    public void Detects_common_languages(string text, string expected) => Assert.Equal(expected, OfflineTranslator.Detect(text));

    [Fact]
    public async Task Offline_translation_uses_the_glossary_both_ways_and_is_marked_as_sample()
    {
        var forward = await OfflineTranslator.TranslateAsync("Hello friend!", null, "es");
        Assert.Equal("hola amigo! [es, sample]", forward.Text);
        Assert.Equal("en", forward.From);
        Assert.True(forward.Detected);
        Assert.True(forward.IsSample);

        var back = await OfflineTranslator.TranslateAsync("gracias amigo", "es", "en");
        Assert.StartsWith("thank friend", back.Text);
        Assert.False(back.Detected);
    }

    [Fact]
    public void Offline_speech_scores_every_reference_word_and_silence_scores_low()
    {
        var tone = Enumerable.Range(0, 16000 * 2).Select(i => (float)Math.Sin(i / 5.0) * 0.3f).ToArray();
        var spoken = OfflineSpeech.Assess(WavAudio.Write(tone, 16000), "Buenos días, ¿cómo estás?");
        Assert.True(spoken.IsSample);
        Assert.Equal(new[] { "Buenos", "días", "cómo", "estás" }, spoken.Words.Select(w => w.Word));
        Assert.Equal(100, spoken.Completeness);

        var silent = OfflineSpeech.Assess(WavAudio.Write(new float[16000 * 2], 16000), "Buenos días");
        Assert.Equal("", silent.Transcript);
        Assert.True(silent.Accuracy < spoken.Accuracy);
    }

    [Fact]
    public async Task Offline_ai_answers_the_new_features_with_valid_json()
    {
        var client = new OfflineAiChatClient();

        var practice = await client.CompleteAsync(new AiChatRequest(AiFeature.PracticeItems, "s",
            "Write 2 reading passages for A1 learners of Spanish whose native language is English. Topic: Food. Admin notes: none", null, 0.5));
        var (set, error) = AiOutputParser.Parse<AiPracticeSet>(practice.Content, AiOutputParser.ValidatePractice);
        Assert.Null(error);
        Assert.Equal(2, set!.Items.Count);
        Assert.Equal(3, set.Items[0].Questions.Count);

        var chat = await client.CompleteAsync(new AiChatRequest(AiFeature.Conversation, "s",
            "Topic: Travel. The learner's native language is English.\nLearner's new message: <<<i like trains>>>", null, 0.7));
        var (reply, _) = AiOutputParser.Parse<AiConversationReply>(chat.Content, AiOutputParser.ValidateConversation);
        Assert.Equal("I like trains", reply!.Correction);

        var check = await client.CompleteAsync(new AiChatRequest(AiFeature.WritingCheck, "s", "Text: <<<me llamo Ana>>>", null, 0.2));
        var (grade, gradeError) = AiOutputParser.Parse<AiWritingGrade>(check.Content, AiOutputParser.ValidateWritingCheck);
        Assert.Null(gradeError);
        Assert.Equal("Me", Assert.Single(grade!.Corrections).Corrected);
    }

    [Fact]
    public void Practice_validation_rejects_bad_answer_indexes()
    {
        var bad = new AiPracticeSet
        {
            Items = [new AiPracticeDraft { Text = "x", Questions = [new AiReadingQuestion { Question = "q", Options = ["a", "b"], Correct = 2 }] }]
        };
        Assert.NotNull(AiOutputParser.ValidatePractice(bad));
        Assert.NotNull(AiOutputParser.ValidatePractice(new AiPracticeSet()));
    }
}
