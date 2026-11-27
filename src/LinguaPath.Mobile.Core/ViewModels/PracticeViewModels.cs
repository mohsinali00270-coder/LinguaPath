using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Practice;
using LinguaPath.Shared.Contracts.Translation;
using LinguaPath.Shared.Contracts.Words;
using Refit;

namespace LinguaPath.Mobile.Core.ViewModels;

// ===================================================================== shared state

/// <summary>The language the Practice tab works in, and the learner's native language for translations. Kept for the app session.</summary>
public sealed class PracticeLanguage
{
    public int? LanguageId { get; set; }
    public string LanguageName { get; set; } = "";
    public string? IsoCode { get; set; }
    public string? SpeechLocale { get; set; }
    public TextDirection Direction { get; set; }
    /// <summary>Translation target for tap-to-translate, e.g. "en".</summary>
    public string NativeCode { get; set; } = "en";
    public bool NativeResolved { get; set; }
}

public sealed record PracticeLanguageOption(int Id, string Name, string IsoCode)
{
    public override string ToString() => Name;
}

/// <summary>Resolves the learner's native language code once (for tap-to-translate and the translate screen).</summary>
internal static class NativeLanguageResolver
{
    public static async Task EnsureAsync(PracticeLanguage state, IAccountApi account, IPracticeApi api)
    {
        if (state.NativeResolved) return;
        try
        {
            var profile = (await account.GetProfileAsync()).Data;
            var languages = (await api.GetTranslationLanguagesAsync()).Data ?? [];
            var match = languages.FirstOrDefault(l => string.Equals(l.Name, profile?.NativeLanguageName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) state.NativeCode = match.Code;
            state.NativeResolved = true;
        }
        catch (Exception)
        {
            // English stays the default; try again next time.
        }
    }
}

// ===================================================================== hub (Practice tab)

public sealed partial class PracticeCard(PracticeType type, string icon, string title, string description) : ObservableObject
{
    public PracticeType Type { get; } = type;
    public string Icon { get; } = icon;
    public string Title { get; } = title;
    public string Description { get; } = description;
    [ObservableProperty] private string? stats;
}

/// <summary>Practice tab: read, write, speak, listen, free-talk, and "My words" (FR-L90 .. FR-L94).</summary>
public sealed partial class PracticeHubViewModel(
    IPracticeApi api,
    ILearningApi learning,
    PracticeLanguage state,
    INavigationService navigation) : BaseViewModel
{
    private bool _loadingLanguages;

    public ObservableCollection<PracticeCard> Cards { get; } =
    [
        new(PracticeType.Read, "📖", "Read", "Graded passages, tap a word to translate, questions after reading."),
        new(PracticeType.Write, "✍️", "Write", "Write on a prompt; the AI tutor corrects it and gives a score."),
        new(PracticeType.Speak, "🎤", "Speak", "Read a sentence aloud and see how well each word was pronounced."),
        new(PracticeType.Listen, "🎧", "Listen", "Dictation: type what you hear and see your mistakes."),
        new(PracticeType.Conversation, "💬", "Free talk", "Chat with an AI partner on a topic; mistakes are corrected.")
    ];

    public ObservableCollection<PracticeLanguageOption> Languages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private PracticeSummaryDto? summary;

    [ObservableProperty] private PracticeLanguageOption? selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WordsText))]
    private int wordsDue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoLanguage))]
    private bool hasLanguage = true;

    public bool HasNoLanguage => !HasLanguage;
    public string Title => Summary is null ? "Practice" : $"Practice {Summary.LanguageName} · {Summary.Level}";
    public string WordsText => WordsDue == 0 ? "No words due. Save words from lectures and translations." : $"{WordsDue} word(s) due for review";
    public string TranslationsText => Summary is null ? "" : $"{Summary.TranslationsLeftToday} translations left today";

    public override async Task OnAppearingAsync()
    {
        await LoadLanguagesAsync();
        await LoadAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadLanguagesAsync()
    {
        if (Languages.Count > 0) return;
        try
        {
            _loadingLanguages = true;
            var courses = (await learning.GetMyCoursesAsync()).Data ?? [];
            foreach (var c in courses.Where(c => c.Status != LearnerCourseStatus.Cancelled).GroupBy(c => c.LanguageId).Select(g => g.First()))
                Languages.Add(new PracticeLanguageOption(c.LanguageId, c.LanguageName, c.LanguageIsoCode));
            SelectedLanguage = Languages.FirstOrDefault(l => l.Id == state.LanguageId) ?? Languages.FirstOrDefault();
        }
        catch (Exception)
        {
            // The summary call below shows the error.
        }
        finally
        {
            _loadingLanguages = false;
        }
    }

    partial void OnSelectedLanguageChanged(PracticeLanguageOption? value)
    {
        if (value is null || _loadingLanguages || value.Id == state.LanguageId) return;
        state.LanguageId = value.Id;
        _ = LoadAsync();
    }

    private Task LoadAsync() => RunAsync(async () =>
    {
        var s = (await api.GetSummaryAsync(SelectedLanguage?.Id ?? state.LanguageId)).Data!;
        Summary = s;
        HasLanguage = true;
        state.LanguageId = s.LanguageId;
        state.LanguageName = s.LanguageName;
        WordsDue = s.WordsDue;
        OnPropertyChanged(nameof(TranslationsText));
        foreach (var card in Cards)
        {
            var t = s.Types.FirstOrDefault(x => x.Type == card.Type);
            card.Stats = t is null || t.Done == 0
                ? (t?.Available == 0 ? "No exercises yet" : "Not tried yet")
                : $"Done {t.Done}×" + (t.AverageScore is { } avg ? $" · average {avg}%" : "");
        }
    }, error =>
    {
        if (error.Code != "no_language") return Task.FromResult(false);
        HasLanguage = false;
        return Task.FromResult(true);
    });

    [RelayCommand]
    private Task Open(PracticeCard card)
    {
        if (state.LanguageId is null) return Task.CompletedTask;
        return card.Type == PracticeType.Conversation
            ? navigation.GoToAsync(Routes.Conversation)
            : navigation.GoToAsync(Routes.PracticeSession, new Dictionary<string, object> { ["type"] = card.Type.ToString() });
    }

    [RelayCommand]
    private Task OpenWords() => navigation.GoToAsync(Routes.Words);

    [RelayCommand]
    private Task Review() => navigation.GoToAsync(Routes.Flashcards);

    [RelayCommand]
    private Task Translate() => navigation.GoToAsync(Routes.Translate);

    [RelayCommand]
    private Task StartCourse() => navigation.GoToAsync(Routes.Onboarding);
}

// ===================================================================== one exercise: read, write, speak, listen

public sealed partial class ReadingOption(int index, string text) : ObservableObject
{
    public int Index { get; } = index;
    public string Text { get; } = text;
    [ObservableProperty] private bool isSelected;
    /// <summary>After checking: true = correct answer, false = wrong pick, null = neutral.</summary>
    [ObservableProperty] private bool? marked;
}

public sealed partial class ReadingQuestion(ReadingQuestionDto dto) : ObservableObject
{
    public int Index { get; } = dto.Index;
    public string Text { get; } = $"{dto.Index + 1}. {dto.Question}";
    public ObservableCollection<ReadingOption> Options { get; } = new(dto.Options.Select((o, i) => new ReadingOption(i, o)));
    [ObservableProperty] private string? explanation;
    public int Selected => Options.FirstOrDefault(o => o.IsSelected)?.Index ?? -1;
}

public sealed record WordToken(string Text, string Clean);

public sealed record CorrectionRow(string Original, string Corrected, string? Explanation)
{
    public bool HasExplanation => !string.IsNullOrWhiteSpace(Explanation);
}

public sealed record WordScoreRow(string Word, int Accuracy, string ErrorType)
{
    public string Label => ErrorType is "Omission" ? $"{Word} (missed)" : $"{Word} {Accuracy}";
    /// <summary>"good" ≥ 80, "ok" ≥ 60, else "poor" (the page maps these to colours).</summary>
    public string Band => ErrorType is "Omission" || Accuracy < 60 ? "poor" : Accuracy < 80 ? "ok" : "good";
}

public sealed record DiffRow(string Text, string? Expected, DiffKind Kind)
{
    public string Display => Kind switch
    {
        DiffKind.Correct => Text,
        DiffKind.Wrong => $"{Text} → {Expected}",
        DiffKind.Missing => $"+{Expected}",
        _ => $"−{Text}"
    };
}

/// <summary>One practice exercise (FR-L90 .. FR-L93); the page shows the part for the exercise type.</summary>
public sealed partial class PracticeSessionViewModel(
    IPracticeApi api,
    IAccountApi account,
    PracticeLanguage state,
    ISpeechService speech,
    IAudioService audio,
    IDialogService dialogs) : BaseViewModel
{
    private RecordedAudio? _recording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRead), nameof(IsWrite), nameof(IsSpeak), nameof(IsListen), nameof(Heading))]
    private PracticeType exerciseType = PracticeType.Read;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(Title), nameof(Subtitle), nameof(ShowText), nameof(IsRtl), nameof(HasTranslation))]
    private PracticeItemDto? item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowText))]
    private bool isChecked;

    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool showTranslation;
    [ObservableProperty] private string? learnerText;
    [ObservableProperty] private int? score;
    [ObservableProperty] private string? feedback;
    [ObservableProperty] private string? remainingText;
    [ObservableProperty] private string? transcript;
    [ObservableProperty] private bool isSample;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double speed = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText))]
    private bool isRecording;

    public ObservableCollection<WordToken> Tokens { get; } = [];
    public ObservableCollection<ReadingQuestion> Questions { get; } = [];
    public ObservableCollection<CorrectionRow> Corrections { get; } = [];
    public ObservableCollection<WordScoreRow> WordScores { get; } = [];
    public ObservableCollection<DiffRow> Diff { get; } = [];

    public bool IsRead => ExerciseType == PracticeType.Read;
    public bool IsWrite => ExerciseType == PracticeType.Write;
    public bool IsSpeak => ExerciseType == PracticeType.Speak;
    public bool IsListen => ExerciseType == PracticeType.Listen;
    public bool HasItem => Item is not null;
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Item?.Translation);
    /// <summary>Dictation hides the sentence until it is checked.</summary>
    public bool ShowText => HasItem && (!IsListen || IsChecked);
    public bool IsRtl => Item?.Direction == TextDirection.Rtl;
    public string Heading => ExerciseType switch
    {
        PracticeType.Read => "Reading", PracticeType.Write => "Writing", PracticeType.Speak => "Speaking", _ => "Listening"
    };
    public string Title => Item?.Title ?? Heading;
    public string Subtitle => Item is null ? "" : $"{Item.LanguageName} · {Item.Level}";
    public string SpeedText => Speed switch { < 0.8 => "Slow", > 1.2 => "Fast", _ => "Normal" };
    public string RecordText => IsRecording ? "■ Stop and check" : "🎤 Record";
    public string ScoreText => Score is { } s ? $"{s}%" : "";

    partial void OnScoreChanged(int? value) => OnPropertyChanged(nameof(ScoreText));

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (Enum.TryParse<PracticeType>(Param(parameters, "type"), true, out var t)) ExerciseType = t;
    }

    public override async Task OnAppearingAsync()
    {
        if (Item is null && !IsEmpty) await LoadAsync(null);
    }

    public override Task OnDisappearingAsync()
    {
        speech.Stop();
        audio.StopPlayback();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task Next() => LoadAsync(Item?.Id);

    private Task LoadAsync(int? skip) => RunAsync(async () =>
    {
        speech.Stop();
        var next = (await api.NextAsync(ExerciseType, state.LanguageId, skip)).Data!;
        Reset();
        Item = next;
        foreach (var w in next.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [])
            Tokens.Add(new WordToken(w, w.Trim('.', ',', ';', ':', '!', '?', '¿', '¡', '"', '«', '»', '(', ')')));
        foreach (var q in next.Questions) Questions.Add(new ReadingQuestion(q));
        state.SpeechLocale = next.SpeechLocale;
        state.IsoCode = next.LanguageIsoCode;
        state.Direction = next.Direction;
        IsEmpty = false;
    }, error =>
    {
        if (error.Status != System.Net.HttpStatusCode.NotFound) return Task.FromResult(false);
        IsEmpty = true;
        ErrorMessage = $"There are no {Heading.ToLowerInvariant()} exercises for your language yet. Try another type, or check back soon.";
        return Task.FromResult(true);
    });

    private void Reset()
    {
        IsChecked = false;
        ShowTranslation = false;
        LearnerText = null;
        Score = null;
        Feedback = null;
        Transcript = null;
        IsSample = false;
        _recording = null;
        Tokens.Clear();
        Questions.Clear();
        Corrections.Clear();
        WordScores.Clear();
        Diff.Clear();
    }

    [RelayCommand]
    private void ToggleTranslation() => ShowTranslation = !ShowTranslation;

    // ------------------------------------------------------------------ listening and read-aloud with speed (FR-L90, FR-L93)

    [RelayCommand]
    private Task Play() => Item is null ? Task.CompletedTask : speech.SpeakAsync(Item.Text ?? "", Item.SpeechLocale, (float)Speed);

    [RelayCommand]
    private Task PlaySlow() => Item is null ? Task.CompletedTask : speech.SpeakAsync(Item.Text ?? "", Item.SpeechLocale, 0.6f);

    [RelayCommand]
    private void Stop() => speech.Stop();

    // ------------------------------------------------------------------ tap a word to translate (FR-L90, FR-L103)

    [RelayCommand]
    private async Task TapWord(WordToken token)
    {
        if (Item is null || string.IsNullOrWhiteSpace(token.Clean)) return;
        await NativeLanguageResolver.EnsureAsync(state, account, api);
        TranslationDto? result = null;
        await RunAsync(async () =>
            result = (await api.TranslateAsync(new TranslateRequest(token.Clean, Item.LanguageIsoCode, state.NativeCode))).Data);
        if (result is null) return;

        var choice = await dialogs.ConfirmAsync(token.Clean, result.TranslatedText + (result.IsSample ? "\n\n(sample translation)" : ""),
            "⭐ Save to My words", "Close");
        if (choice) await SaveWordAsync(token.Clean, result.TranslatedText, Item.Text);
    }

    private async Task SaveWordAsync(string word, string meaning, string? example)
    {
        if (Item is null) return;
        await RunAsync(async () =>
        {
            var sentence = example?.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(s => s.Contains(word, StringComparison.OrdinalIgnoreCase))?.Trim();
            await api.SaveWordAsync(new SaveWordRequest(Item.LanguageId, word, meaning, sentence));
            await dialogs.ToastAsync($"\"{word}\" saved to My words");
        });
    }

    // ------------------------------------------------------------------ read: comprehension questions

    [RelayCommand]
    private void SelectOption(ReadingOption option)
    {
        if (IsChecked) return;
        var question = Questions.FirstOrDefault(q => q.Options.Contains(option));
        if (question is null) return;
        foreach (var o in question.Options) o.IsSelected = ReferenceEquals(o, option);
    }

    [RelayCommand]
    private async Task CheckReading()
    {
        if (Item is null || IsChecked) return;
        if (Questions.Any(q => q.Selected < 0))
        {
            await dialogs.ToastAsync("Answer every question first.");
            return;
        }
        await RunAsync(async () =>
        {
            var result = (await api.AnswerReadingAsync(Item.Id, new ReadAnswersRequest(Questions.Select(q => q.Selected).ToList()))).Data!;
            foreach (var r in result.Results)
            {
                var q = Questions.FirstOrDefault(x => x.Index == r.Index);
                if (q is null) continue;
                foreach (var o in q.Options) o.Marked = o.Index == r.CorrectIndex ? true : o.IsSelected ? false : null;
                q.Explanation = r.Explanation;
            }
            Score = result.Score;
            Feedback = $"{result.Correct} of {result.Total} correct.";
            IsChecked = true;
        });
    }

    // ------------------------------------------------------------------ write (FR-L91)

    [RelayCommand]
    private async Task CheckWriting()
    {
        if (Item is null) return;
        if (string.IsNullOrWhiteSpace(LearnerText) || LearnerText.Trim().Length < 3)
        {
            Errors.Set("Text", "Write a few words first.");
            return;
        }
        await RunAsync(async () =>
        {
            var result = (await api.CheckWritingAsync(new WriteCheckRequest(Item.LanguageId, Item.Id, null, LearnerText!.Trim()))).Data!;
            Corrections.Clear();
            foreach (var c in result.Corrections) Corrections.Add(new CorrectionRow(c.Original, c.Corrected, c.Explanation));
            Score = result.Score;
            Feedback = result.Feedback;
            IsSample = result.IsSample;
            RemainingText = $"{result.RemainingToday} AI checks left today";
            IsChecked = true;
        });
    }

    // ------------------------------------------------------------------ speak (FR-L92)

    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (Item is null || IsBusy) return;
        if (!IsRecording)
        {
            speech.Stop();
            if (!await audio.StartRecordingAsync())
            {
                await dialogs.AlertAsync("Microphone", "Allow microphone access in your phone's settings to practise speaking.");
                return;
            }
            IsRecording = true;
            return;
        }

        IsRecording = false;
        var recording = await audio.StopRecordingAsync();
        if (recording is null || recording.Duration < TimeSpan.FromMilliseconds(500))
        {
            await dialogs.ToastAsync("That recording was too short. Try again.");
            return;
        }
        _recording = recording;
        await RunAsync(async () =>
        {
            await using var stream = await recording.OpenReadAsync();
            var result = (await api.AssessSpeakingAsync(Item.LanguageId.ToString(), Item.Id.ToString(), "",
                new StreamPart(stream, recording.FileName, recording.ContentType))).Data!;
            WordScores.Clear();
            foreach (var w in result.Words) WordScores.Add(new WordScoreRow(w.Word, w.Accuracy, w.ErrorType));
            Score = result.Score;
            Transcript = string.IsNullOrWhiteSpace(result.Transcript) ? "(nothing recognised)" : result.Transcript;
            Feedback = $"Accuracy {result.Accuracy} · fluency {result.Fluency} · completeness {result.Completeness}";
            IsSample = result.IsSample;
            IsChecked = true;
        });
    }

    [RelayCommand]
    private Task PlayMine() => _recording is null ? Task.CompletedTask : audio.PlayRecordingAsync(_recording);

    // ------------------------------------------------------------------ listen: dictation (FR-L93)

    [RelayCommand]
    private async Task CheckListening()
    {
        if (Item is null || IsChecked) return;
        if (string.IsNullOrWhiteSpace(LearnerText))
        {
            Errors.Set("Text", "Type what you heard.");
            return;
        }
        await RunAsync(async () =>
        {
            var result = (await api.CheckListeningAsync(Item.Id, new ListenCheckRequest(LearnerText!.Trim()))).Data!;
            Diff.Clear();
            foreach (var w in result.Words) Diff.Add(new DiffRow(w.Text, w.Expected, w.Kind));
            Score = result.Score;
            Feedback = result.Score == 100 ? "Perfect!" : "Green: right · orange: misspelt · red: missed · grey: extra.";
            IsChecked = true;
        });
    }
}

// ===================================================================== free talk (FR-L92)

public sealed partial class ChatMessage(bool isLearner, string text, string? correction = null, string? explanation = null) : ObservableObject
{
    public bool IsLearner { get; } = isLearner;
    public bool IsPartner => !IsLearner;
    public string Text { get; } = text;
    public string? Correction { get; } = correction;
    public string? Explanation { get; } = explanation;
    public bool HasCorrection => !string.IsNullOrWhiteSpace(Correction);
    public string CorrectionText => HasCorrection ? $"✓ {Correction}" + (string.IsNullOrWhiteSpace(Explanation) ? "" : $"\n{Explanation}") : "";
}

public sealed partial class ConversationViewModel(IPracticeApi api, PracticeLanguage state, ISpeechService speech) : BaseViewModel
{
    public ObservableCollection<string> Topics { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStarted), nameof(HasNotStarted))]
    private string? topic;

    [ObservableProperty] private string? draft;
    [ObservableProperty] private bool readReplies = true;
    [ObservableProperty] private string? remainingText;
    [ObservableProperty] private bool isSample;

    public bool HasStarted => !string.IsNullOrWhiteSpace(Topic);
    public bool HasNotStarted => !HasStarted;
    public string Title => $"Free talk · {state.LanguageName}";

    /// <summary>Raised after a message is added so the page can scroll to the end.</summary>
    public event EventHandler? MessageAdded;

    public override async Task OnAppearingAsync()
    {
        if (Topics.Count > 0) return;
        await RunAsync(async () =>
        {
            var summary = (await api.GetSummaryAsync(state.LanguageId)).Data!;
            foreach (var t in summary.Topics) Topics.Add(t);
        });
    }

    public override Task OnDisappearingAsync()
    {
        speech.Stop();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Start(string topicName)
    {
        Topic = topicName;
        Messages.Clear();
    }

    [RelayCommand]
    private void ChangeTopic()
    {
        speech.Stop();
        Topic = null;
        Messages.Clear();
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft?.Trim();
        if (string.IsNullOrEmpty(text) || Topic is null || state.LanguageId is not { } languageId) return;
        if (text.Length > 500)
        {
            Errors.Set("Message", "Keep messages under 500 characters.");
            return;
        }

        var history = Messages.Select(m => new ChatTurnDto(m.IsLearner ? "learner" : "partner", m.Text)).ToList();
        Draft = null;
        await RunAsync(async () =>
        {
            var reply = (await api.ConverseAsync(new ConversationRequest(languageId, Topic, history, text))).Data!;
            Messages.Add(new ChatMessage(true, text, reply.Correction, reply.Explanation));
            Messages.Add(new ChatMessage(false, reply.Reply));
            RemainingText = $"{reply.RemainingToday} messages left today";
            IsSample = reply.IsSample;
            MessageAdded?.Invoke(this, EventArgs.Empty);
            if (ReadReplies) _ = speech.SpeakAsync(reply.Reply, state.SpeechLocale);
        }, error =>
        {
            Draft = text; // keep what they typed
            return Task.FromResult(false);
        });
    }

    [RelayCommand]
    private Task Speak(ChatMessage message) => speech.SpeakAsync(message.Text, state.SpeechLocale);
}

// ===================================================================== online translation (FR-L100 .. FR-L104)

public sealed record TranslationRow(TranslationDto Dto)
{
    public string Pair => $"{Dto.From} → {Dto.To}";
    public string Source => Dto.SourceText;
    public string Result => Dto.TranslatedText;
}

public sealed record TranslateLanguageChoice(string Code, string Name, string? SpeechLocale)
{
    public const string Detect = "";
    public bool IsDetect => Code == Detect;
    public override string ToString() => Name;
}

public sealed partial class TranslateViewModel(
    IPracticeApi api,
    IAccountApi account,
    ILearningApi learning,
    PracticeLanguage state,
    ISpeechService speech,
    IAudioService audio,
    IDialogService dialogs) : BaseViewModel
{
    private List<Shared.Contracts.Catalog.LanguageDto>? _catalog;

    public ObservableCollection<TranslateLanguageChoice> FromLanguages { get; } = [];
    public ObservableCollection<TranslateLanguageChoice> ToLanguages { get; } = [];
    public ObservableCollection<TranslationRow> History { get; } = [];

    [ObservableProperty] private TranslateLanguageChoice? from;
    [ObservableProperty] private TranslateLanguageChoice? to;
    [ObservableProperty] private string? sourceText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(DetectedText))]
    private TranslationDto? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceText))]
    private bool isRecording;

    [ObservableProperty] private string? remainingText;

    public bool HasResult => Result is not null;
    public string DetectedText => Result is null ? "" :
        (Result.Detected ? $"Detected: {NameOf(Result.From)}" : "") + (Result.Cached ? " · from cache" : "") + (Result.IsSample ? " · sample" : "");
    public string VoiceText => IsRecording ? "■ Stop" : "🎤 Speak";

    public override async Task OnAppearingAsync()
    {
        if (FromLanguages.Count > 0)
        {
            await LoadHistoryAsync();
            return;
        }
        await RunAsync(async () =>
        {
            await NativeLanguageResolver.EnsureAsync(state, account, api);
            var languages = (await api.GetTranslationLanguagesAsync()).Data ?? [];
            FromLanguages.Add(new TranslateLanguageChoice(TranslateLanguageChoice.Detect, "Detect language", null));
            foreach (var l in languages)
            {
                FromLanguages.Add(new TranslateLanguageChoice(l.Code, l.Name, l.SpeechLocale));
                ToLanguages.Add(new TranslateLanguageChoice(l.Code, l.Name, l.SpeechLocale));
            }
            From = FromLanguages[0];
            // Default: the language being learned → the learner's native language.
            To = ToLanguages.FirstOrDefault(l => l.Code.Equals(state.NativeCode, StringComparison.OrdinalIgnoreCase)) ?? ToLanguages.FirstOrDefault();
            await LoadHistoryAsync();
        });
    }

    public override Task OnDisappearingAsync()
    {
        speech.Stop();
        return Task.CompletedTask;
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var rows = (await api.GetTranslationHistoryAsync()).Data ?? [];
            History.Clear();
            foreach (var r in rows) History.Add(new TranslationRow(r));
        }
        catch (Exception)
        {
            // History is a nice-to-have; the translate box still works.
        }
    }

    [RelayCommand]
    private async Task Translate()
    {
        var text = SourceText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Errors.Set("Text", "Type something to translate.");
            return;
        }
        if (To is null) return;
        await RunAsync(async () =>
        {
            var dto = (await api.TranslateAsync(new TranslateRequest(text, From?.IsDetect == false ? From.Code : null, To.Code))).Data!;
            Result = dto;
            AfterTranslation(dto);
        });
    }

    private void AfterTranslation(TranslationDto dto)
    {
        RemainingText = dto.RemainingToday >= 0 ? $"{dto.RemainingToday} translations left today" : null;
        History.Insert(0, new TranslationRow(dto));
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
    }

    [RelayCommand]
    private void Swap()
    {
        if (Result is not null)
        {
            SourceText = Result.TranslatedText;
            var newTo = ToLanguages.FirstOrDefault(l => l.Code.Equals(Result.From, StringComparison.OrdinalIgnoreCase));
            From = FromLanguages.FirstOrDefault(l => l.Code.Equals(Result.To, StringComparison.OrdinalIgnoreCase)) ?? From;
            if (newTo is not null) To = newTo;
            Result = null;
            return;
        }
        if (From is { IsDetect: false } f && To is { } t)
        {
            From = FromLanguages.FirstOrDefault(l => l.Code == t.Code) ?? From;
            To = ToLanguages.FirstOrDefault(l => l.Code == f.Code) ?? To;
        }
    }

    /// <summary>Voice input (FR-L101): needs a spoken language, so "Detect" isn't allowed here.</summary>
    [RelayCommand]
    private async Task Voice()
    {
        if (!IsRecording)
        {
            if (From is null || From.IsDetect)
            {
                await dialogs.AlertAsync("Voice input", "Choose the language you will speak in the \"From\" list first.");
                return;
            }
            speech.Stop();
            if (!await audio.StartRecordingAsync())
            {
                await dialogs.AlertAsync("Microphone", "Allow microphone access in your phone's settings to use voice input.");
                return;
            }
            IsRecording = true;
            return;
        }

        IsRecording = false;
        var recording = await audio.StopRecordingAsync();
        if (recording is null || recording.Duration < TimeSpan.FromMilliseconds(500) || From is null || To is null) return;
        await RunAsync(async () =>
        {
            await using var stream = await recording.OpenReadAsync();
            var speechResult = (await api.TranslateSpeechAsync(From.SpeechLocale ?? From.Code, To.Code,
                new StreamPart(stream, recording.FileName, recording.ContentType))).Data!;
            SourceText = speechResult.Transcript;
            Result = speechResult.Translation;
            AfterTranslation(speechResult.Translation);
        });
    }

    [RelayCommand]
    private Task SpeakResult() =>
        Result is null ? Task.CompletedTask : speech.SpeakAsync(Result.TranslatedText, To?.SpeechLocale ?? Result.To);

    [RelayCommand]
    private Task SpeakSource() =>
        string.IsNullOrWhiteSpace(SourceText) ? Task.CompletedTask
            : speech.SpeakAsync(SourceText, From?.SpeechLocale ?? Result?.From);

    [RelayCommand]
    private void Use(TranslationRow row)
    {
        SourceText = row.Dto.SourceText;
        Result = row.Dto;
        From = FromLanguages.FirstOrDefault(l => l.Code.Equals(row.Dto.From, StringComparison.OrdinalIgnoreCase)) ?? From;
        To = ToLanguages.FirstOrDefault(l => l.Code.Equals(row.Dto.To, StringComparison.OrdinalIgnoreCase)) ?? To;
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!await dialogs.ConfirmAsync("Clear history", "Remove your translation history?", "Clear", "Cancel")) return;
        await RunAsync(async () =>
        {
            await api.ClearTranslationHistoryAsync();
            History.Clear();
        });
    }

    /// <summary>
    /// FR-L103: words belong to a language the learner studies. The side that is a course language is the word;
    /// the other side is its meaning.
    /// </summary>
    [RelayCommand]
    private async Task SaveWord()
    {
        if (Result is null) return;
        await RunAsync(async () =>
        {
            _catalog ??= (await learning.GetLanguagesAsync()).Data ?? [];
            var source = _catalog.FirstOrDefault(l => l.IsoCode.Equals(Result.From, StringComparison.OrdinalIgnoreCase));
            var target = _catalog.FirstOrDefault(l => l.IsoCode.Equals(Result.To, StringComparison.OrdinalIgnoreCase));
            var learning1 = new[] { source, target }.FirstOrDefault(l => l is not null && l.Id == state.LanguageId)
                            ?? (source?.Status == LanguageStatus.Published ? source : target?.Status == LanguageStatus.Published ? target : null);
            if (learning1 is null)
            {
                await dialogs.AlertAsync("My words", "Words are saved for languages you are learning. Translate to or from one of them.");
                return;
            }
            var (word, meaning) = learning1 == source ? (Result.SourceText, Result.TranslatedText) : (Result.TranslatedText, Result.SourceText);
            await api.SaveWordAsync(new SaveWordRequest(learning1.Id, Clip(word, 200), Clip(meaning, 500), null));
            await dialogs.ToastAsync("Saved to My words");
        });
    }

    private string NameOf(string code) =>
        ToLanguages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? code;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

// ===================================================================== My words and flashcards (FR-L94)

public sealed record WordRow(UserWordDto Dto)
{
    public string Word => Dto.Word;
    public string Meaning => Dto.Meaning;
    public string? Example => Dto.Example;
    public bool HasExample => !string.IsNullOrWhiteSpace(Dto.Example);
    public string Due => Dto.IsDue ? "Due now" : $"Next review {Dto.NextReviewAt.ToLocalTime():d MMM}";
}

public sealed partial class WordsViewModel(
    IPracticeApi api,
    PracticeLanguage state,
    ISpeechService speech,
    IDialogService dialogs,
    INavigationService navigation) : BaseViewModel
{
    public ObservableCollection<WordRow> Words { get; } = [];

    [ObservableProperty] private string? search;
    [ObservableProperty] private bool dueOnly;
    [ObservableProperty] private string? summaryText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReview))]
    private int due;

    [ObservableProperty] private bool isEmpty;

    public bool CanReview => Due > 0;

    public override Task OnAppearingAsync() => LoadAsync();

    partial void OnDueOnlyChanged(bool value) => _ = LoadAsync();

    [RelayCommand]
    private Task Find() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        var summary = (await api.GetWordsSummaryAsync(state.LanguageId)).Data!;
        Due = summary.Due;
        SummaryText = $"{summary.Total} words · {summary.Due} due · {summary.LearnedThisWeek} learned this week";
        var rows = (await api.GetWordsAsync(state.LanguageId, DueOnly, string.IsNullOrWhiteSpace(Search) ? null : Search.Trim())).Data ?? [];
        Words.Clear();
        foreach (var w in rows) Words.Add(new WordRow(w));
        IsEmpty = Words.Count == 0;
    });

    [RelayCommand]
    private async Task Add()
    {
        if (state.LanguageId is not { } languageId) return;
        var word = await dialogs.PromptAsync("New word", $"A {state.LanguageName} word or phrase:", "Next");
        if (string.IsNullOrWhiteSpace(word)) return;
        var meaning = await dialogs.PromptAsync(word.Trim(), "What does it mean?", "Save");
        if (string.IsNullOrWhiteSpace(meaning)) return;
        await RunAsync(async () =>
        {
            await api.SaveWordAsync(new SaveWordRequest(languageId, word.Trim(), meaning.Trim(), null));
            await dialogs.ToastAsync("Word saved");
        });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Delete(WordRow row)
    {
        if (!await dialogs.ConfirmAsync("Delete word", $"Remove \"{row.Word}\" from your list?", "Delete", "Cancel")) return;
        await RunAsync(async () =>
        {
            await api.DeleteWordAsync(row.Dto.Id);
            Words.Remove(row);
        });
    }

    [RelayCommand]
    private Task Speak(WordRow row) => speech.SpeakAsync(row.Word, state.SpeechLocale ?? state.IsoCode);

    [RelayCommand]
    private Task Review() => navigation.GoToAsync(Routes.Flashcards);
}

public sealed partial class FlashcardsViewModel(IPracticeApi api, PracticeLanguage state, ISpeechService speech,
    IOfflineSyncQueue offlineSync, IDialogService dialogs, TimeProvider clock) : BaseViewModel
{
    private readonly Queue<UserWordDto> _queue = new();
    private int _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard), nameof(Front), nameof(Back), nameof(Example), nameof(HasExample))]
    private UserWordDto? current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFront))]
    private bool isFlipped;

    [ObservableProperty] private bool isDone;
    [ObservableProperty] private string? progressText;

    public bool HasCard => Current is not null;
    public bool IsFront => !IsFlipped;
    public string Front => Current?.Word ?? "";
    public string Back => Current?.Meaning ?? "";
    public string? Example => Current?.Example;
    public bool HasExample => !string.IsNullOrWhiteSpace(Current?.Example);

    public override Task OnAppearingAsync() => Current is null && !IsDone ? LoadAsync() : Task.CompletedTask;

    private Task LoadAsync() => RunAsync(async () =>
    {
        _queue.Clear();
        foreach (var w in (await api.GetWordsAsync(state.LanguageId, due: true)).Data ?? []) _queue.Enqueue(w);
        _total = _queue.Count;
        ShowNext();
    });

    private void ShowNext()
    {
        IsFlipped = false;
        Current = _queue.Count > 0 ? _queue.Dequeue() : null;
        IsDone = Current is null;
        ProgressText = Current is null ? $"All done: {_total} card(s) reviewed." : $"{_total - _queue.Count} of {_total}";
    }

    [RelayCommand]
    private void Flip() => IsFlipped = !IsFlipped;

    [RelayCommand]
    private Task Listen() => Current is null ? Task.CompletedTask : speech.SpeakAsync(Current.Word, state.SpeechLocale ?? state.IsoCode);

    [RelayCommand]
    private Task Grade(string grade)
    {
        if (Current is null || !Enum.TryParse<RecallGrade>(grade, true, out var g)) return Task.CompletedTask;
        var card = Current;
        return RunAsync(async () =>
        {
            var updated = (await api.ReviewWordAsync(card.Id, new ReviewWordRequest(g))).Data!;
            // "Again" comes back in this session so it's practised until remembered.
            if (g == RecallGrade.Again) _queue.Enqueue(updated);
            ShowNext();
        }, async error =>
        {
            // Phase 10 (NFR-09): offline reviews are queued and sent when the phone is back online.
            if (!error.IsNetwork) return false;
            await offlineSync.EnqueueAsync(new OfflineOp(OfflineOp.WordReview, card.Id, null, 0, g, clock.GetUtcNow()));
            if (offlineSync.Count == 1) await dialogs.ToastAsync(Localization.Localizer.T("Offline_Queued"));
            ShowNext();
            return true;
        });
    }

    [RelayCommand]
    private Task Restart()
    {
        IsDone = false;
        return LoadAsync();
    }
}
