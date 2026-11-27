using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Assessment;
using Refit;

namespace LinguaPath.Mobile.Core.ViewModels;

// ===================================================================== question state (one per question in the player)

public sealed partial class ChoiceItem(int id, string text) : ObservableObject
{
    public int Id { get; } = id;
    public string Text { get; } = text;
    [ObservableProperty] private bool isSelected;
    /// <summary>After checking (daily quiz): true = this is a right answer, false = wrongly picked, null = neutral.</summary>
    [ObservableProperty] private bool? marked;
}

public sealed partial class MatchRow(int optionId, string left, IReadOnlyList<string> targets) : ObservableObject
{
    public int OptionId { get; } = optionId;
    public string Left { get; } = left;
    public IReadOnlyList<string> Targets { get; } = targets;
    [ObservableProperty] private string? selectedTarget;
    public Action? Changed { get; set; }
    partial void OnSelectedTargetChanged(string? value) => Changed?.Invoke();
}

public sealed partial class TileItem(int index, string word) : ObservableObject
{
    public int Index { get; } = index;
    public string Word { get; } = word;
    [ObservableProperty] private bool isUsed;
}

/// <summary>Everything the player needs for one question, whatever its type.</summary>
public sealed partial class QuestionState : ObservableObject
{
    public QuestionState(QuizQuestionDto dto, int total)
    {
        Dto = dto;
        Header = $"Question {dto.Number} of {total}" + (string.IsNullOrEmpty(dto.Section) ? "" : $" · {dto.Section}");
        foreach (var o in dto.Options) Choices.Add(new ChoiceItem(o.Id, o.Text));
        if (dto.Type == QuestionType.MatchPairs)
            foreach (var o in dto.Options) MatchRows.Add(new MatchRow(o.Id, o.Text, dto.MatchTargets) { Changed = MarkDirty });
        for (var i = 0; i < dto.WordTiles.Count; i++) Tiles.Add(new TileItem(i, dto.WordTiles[i]));
        Restore(dto.Answer);
        if (dto.Feedback is not null) ApplyFeedback(dto.Feedback);
        _dirty = false;
    }

    public QuizQuestionDto Dto { get; }
    public int QuestionId => Dto.QuestionId;
    public string Header { get; }
    public string NumberText => Dto.Number.ToString();
    public string Stem => Dto.Stem;
    public QuestionType Type => Dto.Type;
    public string SkillText => Dto.Skill.ToString();

    public bool IsChoice => Type is QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose;
    public bool IsListen => Type == QuestionType.ListenChoose;
    public bool IsTextAnswer => Type is QuestionType.FillBlank or QuestionType.Translate;
    public bool IsWriting => Type == QuestionType.Writing;
    public bool IsMatch => Type == QuestionType.MatchPairs;
    public bool IsReorder => Type == QuestionType.ReorderWords;
    public bool IsSpeak => Type == QuestionType.Speak;
    /// <summary>Translate and speak show the sentence; listening keeps it hidden.</summary>
    public bool ShowsPrompt => !string.IsNullOrWhiteSpace(Dto.Prompt) && Type is QuestionType.Translate or QuestionType.Speak;
    public string? Prompt => Dto.Prompt;
    public bool CanHear => Type is QuestionType.ListenChoose or QuestionType.Speak && (!string.IsNullOrWhiteSpace(Dto.Prompt) || !string.IsNullOrWhiteSpace(Dto.MediaUrl));
    public string Instruction => Type switch
    {
        QuestionType.MultipleChoice => Dto.AllowsMultiple ? "Choose all that apply." : "Choose one answer.",
        QuestionType.TrueFalse => "True or false?",
        QuestionType.FillBlank => "Type the missing word.",
        QuestionType.MatchPairs => "Match each item.",
        QuestionType.ReorderWords => "Tap the words in the right order.",
        QuestionType.Translate => "Translate the sentence.",
        QuestionType.ListenChoose => "Listen, then choose.",
        QuestionType.Speak => "Record yourself saying the sentence.",
        QuestionType.Writing => "Write your answer.",
        _ => ""
    };

    public ObservableCollection<ChoiceItem> Choices { get; } = [];
    public ObservableCollection<MatchRow> MatchRows { get; } = [];
    public ObservableCollection<TileItem> Tiles { get; } = [];
    public ObservableCollection<TileItem> Built { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    private string? answerText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer), nameof(RecordingText))]
    private bool hasRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingText), nameof(RecordButtonText))]
    private bool isRecording;

    public RecordedAudio? Recording { get; set; }

    public string RecordButtonText => IsRecording ? "■ Stop" : HasRecording ? "🎙 Record again" : "🎙 Record";
    public string RecordingText => IsRecording ? "Recording… speak now" : HasRecording ? "Recording saved ✓" : "Not recorded yet";

    // ------------------------------------------------------------------ feedback (daily quiz, placement)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked), nameof(FeedbackTitle), nameof(IsLocked))]
    private AnswerFeedbackDto? feedback;

    public bool IsChecked => Feedback is not null;
    public bool IsLocked => IsChecked && !Feedback!.NeedsReview;
    public bool? IsCorrect => Feedback?.IsCorrect;
    public string FeedbackTitle => Feedback switch
    {
        null => "",
        { NeedsReview: true } => "Sent for grading",
        { IsCorrect: true } => "Correct! 🎉",
        { Score: > 0 } => "Partly right",
        _ => "Not quite"
    };
    public string? CorrectAnswerText => Feedback?.CorrectAnswer is { Length: > 0 } c && Feedback.IsCorrect != true ? $"Answer: {c}" : null;
    public string? ExplanationText => Feedback?.Explanation;

    public bool HasAnswer => Type switch
    {
        QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose => Choices.Any(c => c.IsSelected),
        QuestionType.MatchPairs => MatchRows.Any(r => !string.IsNullOrEmpty(r.SelectedTarget)),
        QuestionType.ReorderWords => Built.Count > 0,
        QuestionType.Speak => HasRecording,
        _ => !string.IsNullOrWhiteSpace(AnswerText)
    };

    public string BuiltText => string.Join(" ", Built.Select(t => t.Word));

    private bool _dirty;
    public bool IsDirty => _dirty;
    public void MarkDirty()
    {
        _dirty = true;
        OnPropertyChanged(nameof(HasAnswer));
    }

    partial void OnHasRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));
    public void MarkSaved() => _dirty = false;

    partial void OnAnswerTextChanged(string? value) => _dirty = true;

    public void Select(ChoiceItem choice)
    {
        if (IsLocked) return;
        if (Dto.AllowsMultiple) choice.IsSelected = !choice.IsSelected;
        else foreach (var c in Choices) c.IsSelected = c == choice;
        MarkDirty();
    }

    public void AddTile(TileItem tile)
    {
        if (IsLocked || tile.IsUsed) return;
        tile.IsUsed = true;
        Built.Add(tile);
        OnPropertyChanged(nameof(BuiltText));
        MarkDirty();
    }

    public void RemoveTile(TileItem tile)
    {
        if (IsLocked) return;
        tile.IsUsed = false;
        Built.Remove(tile);
        OnPropertyChanged(nameof(BuiltText));
        MarkDirty();
    }

    public SaveAnswerRequest ToRequest(int seconds) => Type switch
    {
        QuestionType.MultipleChoice or QuestionType.TrueFalse or QuestionType.ListenChoose =>
            new(QuestionId, null, Choices.Where(c => c.IsSelected).Select(c => c.Id).ToList(), null, seconds),
        QuestionType.MatchPairs =>
            new(QuestionId, null, null, MatchRows.Where(r => !string.IsNullOrEmpty(r.SelectedTarget))
                .Select(r => new MatchPairDto(r.OptionId, r.SelectedTarget!)).ToList(), seconds),
        QuestionType.ReorderWords => new(QuestionId, BuiltText, null, null, seconds),
        _ => new(QuestionId, AnswerText, null, null, seconds)
    };

    public void ApplyFeedback(AnswerFeedbackDto feedback)
    {
        Feedback = feedback;
        OnPropertyChanged(nameof(IsCorrect));
        OnPropertyChanged(nameof(CorrectAnswerText));
        OnPropertyChanged(nameof(ExplanationText));
        if (!IsChoice || feedback.CorrectAnswer is null) return;
        var correctTexts = feedback.CorrectAnswer.Split(", ");
        foreach (var c in Choices)
            c.Marked = correctTexts.Contains(c.Text) ? true : c.IsSelected ? false : null;
    }

    private void Restore(SavedAnswerDto? saved)
    {
        if (saved is null) return;
        foreach (var c in Choices) c.IsSelected = saved.OptionIds.Contains(c.Id);
        foreach (var r in MatchRows) r.SelectedTarget = saved.Pairs.FirstOrDefault(p => p.OptionId == r.OptionId)?.Target;
        if (IsReorder && !string.IsNullOrWhiteSpace(saved.Text))
        {
            foreach (var word in saved.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Tiles.FirstOrDefault(t => !t.IsUsed && t.Word == word) is { } tile)
                {
                    tile.IsUsed = true;
                    Built.Add(tile);
                }
        }
        else AnswerText = saved.Text;
        HasRecording = !string.IsNullOrEmpty(saved.AudioUrl);
    }
}

// ===================================================================== the player (FR-L50 .. FR-L65, FR-L11)

/// <summary>
/// Plays a daily quiz, monthly exam or placement test. Daily quiz and placement check each answer straight away;
/// exams auto-save, show a server-side countdown and can be resumed after a crash.
/// Parameters: attemptId, or mode=daily + learnerCourseId (+ practice), or mode=placement + courseId.
/// </summary>
public sealed partial class QuizViewModel(
    IAssessmentApi api,
    INavigationService navigation,
    IDialogService dialogs,
    ISpeechService speech,
    IAudioService audio,
    TimeProvider clock) : BaseViewModel
{
    private int? _attemptId;
    private string? _mode;
    private int _learnerCourseId;
    private int _courseId;
    private bool _practice;
    private bool _loaded;
    private bool _finishing;
    private long _questionShownAt;
    private DateTimeOffset? _deadline;
    private int _ticksSinceSave;

    public ObservableCollection<QuestionState> Questions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttempt), nameof(Title), nameof(IsExam), nameof(InstantFeedback))]
    private AttemptDto? attempt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(ProgressText), nameof(Progress), nameof(CanGoBack), nameof(IsLast),
        nameof(PrimaryText), nameof(CanPrimary))]
    private int position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimerText), nameof(TimerWarning))]
    private int? secondsLeft;

    public QuestionState? Current => Position >= 0 && Position < Questions.Count ? Questions[Position] : null;
    public bool HasAttempt => Attempt is not null;
    public string Title => Attempt?.Title ?? "Quiz";
    public bool IsExam => Attempt?.Type == QuizType.Monthly;
    public bool InstantFeedback => Attempt?.InstantFeedback == true;
    public string ProgressText => Questions.Count == 0 ? "" : $"{Position + 1} / {Questions.Count}";
    public double Progress => Questions.Count == 0 ? 0 : (Position + 1) / (double)Questions.Count;
    public bool CanGoBack => IsExam && Position > 0;
    public bool IsLast => Position >= Questions.Count - 1;
    public bool HasTimer => SecondsLeft is not null;
    public string TimerText => SecondsLeft is { } s ? $"⏱ {s / 60:00}:{s % 60:00}" : "";
    public bool TimerWarning => SecondsLeft is < 300;
    public int AnsweredCount => Questions.Count(q => q.HasAnswer || q.IsChecked);

    /// <summary>Daily quiz: Check → Next. Exam: Next … Finish.</summary>
    public string PrimaryText => Current is null ? "" :
        InstantFeedback && !Current.IsChecked ? "Check" :
        IsLast ? (IsExam ? "Finish exam" : "See result") : "Next";

    public bool CanPrimary => Current is not null && !IsBusy && (!InstantFeedback || Current.IsChecked || Current.HasAnswer);

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "attemptId"), out var id)) _attemptId = id;
        _mode = Param(parameters, "mode");
        if (int.TryParse(Param(parameters, "learnerCourseId"), out var lc)) _learnerCourseId = lc;
        if (int.TryParse(Param(parameters, "courseId"), out var c)) _courseId = c;
        _practice = Param(parameters, "practice") == "true";
    }

    public override async Task OnAppearingAsync()
    {
        if (_loaded)
        {
            // Back from the background or another page: the server clock decides the time left (FR-L62).
            if (IsExam && Attempt is not null) await ReloadAsync();
            return;
        }
        await RunAsync(async () =>
        {
            var response = _attemptId is { } id ? await api.GetAttemptAsync(id)
                : _mode == "daily" ? await api.StartDailyQuizAsync(_learnerCourseId, _practice)
                : _mode == "placement" ? await api.StartPlacementAsync(_courseId)
                : throw new InvalidOperationException("Nothing to open.");
            Show(response.Data!);
            _loaded = true;
        });
        if (Attempt is { Status: not AttemptStatus.InProgress }) await OpenResultAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            var dto = (await api.GetAttemptAsync(Attempt!.Id)).Data;
            if (dto is null) return;
            if (dto.Status != AttemptStatus.InProgress)
            {
                await dialogs.AlertAsync("Time is up", "Your answers were submitted.");
                await OpenResultAsync();
                return;
            }
            SetDeadline(dto.SecondsLeft);
        }
        catch
        {
            // Keep the local countdown; answers are saved when the connection comes back.
        }
    }

    private void Show(AttemptDto dto)
    {
        Questions.Clear();
        foreach (var q in dto.Questions) Questions.Add(new QuestionState(q, dto.Questions.Count));
        Attempt = dto;
        // Resume at the first question still open.
        var first = Questions.ToList().FindIndex(q => dto.InstantFeedback ? !q.IsChecked : !q.HasAnswer);
        Position = first < 0 ? 0 : first;
        SetDeadline(dto.SecondsLeft);
        OnQuestionShown();
        OnPropertyChanged(nameof(HasTimer));
    }

    private void SetDeadline(int? seconds)
    {
        _deadline = seconds is { } s ? clock.GetUtcNow().AddSeconds(s) : null;
        SecondsLeft = seconds;
    }

    private void OnQuestionShown()
    {
        _questionShownAt = clock.GetTimestamp();
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(CanPrimary));
        OnPropertyChanged(nameof(AnsweredCount));
    }

    private int SecondsOnQuestion() => (int)Math.Min(3600, clock.GetElapsedTime(_questionShownAt).TotalSeconds);

    /// <summary>Called every second by the page: exam countdown and periodic auto-save (FR-L62).</summary>
    public async Task TickAsync()
    {
        OnPropertyChanged(nameof(CanPrimary));
        if (_deadline is not { } deadline || Attempt is null || _finishing) return;
        SecondsLeft = Math.Max(0, (int)Math.Ceiling((deadline - clock.GetUtcNow()).TotalSeconds));
        if (SecondsLeft == 0)
        {
            await FinishAsync(confirm: false);
            return;
        }
        if (++_ticksSinceSave >= 20)
        {
            _ticksSinceSave = 0;
            await SaveCurrentAsync(quiet: true);
        }
    }

    // ------------------------------------------------------------------ answering

    [RelayCommand]
    private void SelectChoice(ChoiceItem choice)
    {
        Current?.Select(choice);
        Refresh();
    }

    [RelayCommand]
    private void AddTile(TileItem tile)
    {
        Current?.AddTile(tile);
        Refresh();
    }

    [RelayCommand]
    private void RemoveTile(TileItem tile)
    {
        Current?.RemoveTile(tile);
        Refresh();
    }

    /// <summary>Plays the listening audio, or reads the sentence with the device voice.</summary>
    [RelayCommand]
    private async Task Listen()
    {
        if (Current is null) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(Current.Dto.MediaUrl)) await audio.PlayUrlAsync(Current.Dto.MediaUrl);
            else if (!string.IsNullOrWhiteSpace(Current.Prompt)) await speech.SpeakAsync(Current.Prompt, Attempt?.SpeechLocale ?? Attempt?.LanguageIsoCode);
        }
        catch
        {
            await dialogs.ToastAsync("Audio isn't available right now.");
        }
    }

    [RelayCommand]
    private async Task ToggleRecording()
    {
        var q = Current;
        if (q is null || q.IsLocked) return;
        if (!q.IsRecording)
        {
            if (!await audio.StartRecordingAsync())
            {
                await dialogs.AlertAsync("Microphone", "Allow microphone access in your phone's settings to answer speaking questions.");
                return;
            }
            q.IsRecording = true;
            return;
        }

        q.IsRecording = false;
        var recording = await audio.StopRecordingAsync();
        if (recording is null || recording.Duration < TimeSpan.FromMilliseconds(500))
        {
            await dialogs.ToastAsync("That recording was too short. Try again.");
            return;
        }
        q.Recording = recording;
        await UploadRecordingAsync(q);
    }

    [RelayCommand]
    private Task PlayRecording() =>
        Current?.Recording is { } r ? audio.PlayRecordingAsync(r) : Task.CompletedTask;

    private async Task UploadRecordingAsync(QuestionState q)
    {
        if (Attempt is null || q.Recording is null) return;
        await RunAsync(async () =>
        {
            await using var stream = await q.Recording.OpenReadAsync();
            var feedback = (await api.UploadAudioAsync(Attempt.Id, q.QuestionId, new StreamPart(stream, q.Recording.FileName, q.Recording.ContentType))).Data;
            q.HasRecording = true;
            q.MarkSaved();
            if (InstantFeedback && feedback is not null) q.ApplyFeedback(feedback);
        }, async error =>
        {
            if (error.Code == "attempt_closed") { await OpenResultAsync(); return true; }
            return false;
        });
        Refresh();
    }

    /// <summary>Check (daily quiz) or move on (exam).</summary>
    [RelayCommand]
    private async Task Primary()
    {
        var q = Current;
        if (q is null) return;

        if (InstantFeedback && !q.IsChecked)
        {
            if (!q.HasAnswer) return;
            await RunAsync(async () =>
            {
                var feedback = (await api.SaveAnswerAsync(Attempt!.Id, q.ToRequest(SecondsOnQuestion()))).Data;
                if (feedback is not null) q.ApplyFeedback(feedback);
                q.MarkSaved();
            }, CloseOnError);
            Refresh();
            return;
        }

        if (IsLast)
        {
            await FinishAsync(confirm: IsExam);
            return;
        }
        await MoveAsync(Position + 1);
    }

    [RelayCommand]
    private Task Previous() => Position > 0 ? MoveAsync(Position - 1) : Task.CompletedTask;

    /// <summary>Exam navigator: jump to a question.</summary>
    [RelayCommand]
    private Task GoTo(QuestionState question) => IsExam ? MoveAsync(Questions.IndexOf(question)) : Task.CompletedTask;

    private async Task MoveAsync(int target)
    {
        if (target < 0 || target >= Questions.Count) return;
        if (IsExam) await SaveCurrentAsync(quiet: true);
        Position = target;
        OnQuestionShown();
    }

    /// <summary>Exam auto-save: sends the current answer when it changed.</summary>
    private async Task SaveCurrentAsync(bool quiet)
    {
        var q = Current;
        if (q is null || Attempt is null || !IsExam || !q.IsDirty || q.IsSpeak) return;
        try
        {
            await api.SaveAnswerAsync(Attempt.Id, q.ToRequest(SecondsOnQuestion()));
            q.MarkSaved();
        }
        catch (Exception ex) when (quiet)
        {
            var error = ApiError.From(ex);
            if (error.Code == "attempt_closed") await OpenResultAsync();
        }
    }

    [RelayCommand]
    private Task Finish() => FinishAsync(confirm: true);

    private async Task FinishAsync(bool confirm)
    {
        if (Attempt is null || _finishing) return;
        if (confirm)
        {
            var open = Questions.Count(q => !q.HasAnswer && !q.IsChecked);
            var message = open > 0 ? $"{open} question(s) have no answer. Submit anyway?" : "Submit your answers?";
            if (!await dialogs.ConfirmAsync(IsExam ? "Finish exam" : "Finish quiz", message, "Submit", "Keep going")) return;
        }
        _finishing = true;
        try
        {
            if (IsExam) await SaveCurrentAsync(quiet: true);
            await RunAsync(async () => await api.SubmitAsync(Attempt.Id));
            if (!HasError) await OpenResultAsync();
        }
        finally
        {
            _finishing = false;
        }
    }

    private async Task<bool> CloseOnError(ApiError error)
    {
        if (error.Code != "attempt_closed") return false;
        await dialogs.AlertAsync("Closed", error.Message);
        await OpenResultAsync();
        return true;
    }

    private bool _resultOpened;

    private async Task OpenResultAsync()
    {
        if (Attempt is null || _resultOpened) return;
        _resultOpened = true;
        _deadline = null;
        // Replace the player with the result page so Back returns to where the quiz started.
        await navigation.GoToAsync("../" + Routes.QuizResult, new Dictionary<string, object>
        {
            ["attemptId"] = Attempt.Id,
            ["courseId"] = _courseId
        });
    }

    /// <summary>Integrity log (FR-L65): the app went to the background during an exam.</summary>
    public async Task OnBackgroundedAsync()
    {
        if (!IsExam || Attempt is not { Status: AttemptStatus.InProgress } || _resultOpened) return;
        try
        {
            await SaveCurrentAsync(quiet: true);
            await api.LogEventAsync(Attempt.Id, new AttemptEventRequest("background"));
        }
        catch
        {
            // Not worth bothering the learner about.
        }
    }

    public override async Task OnDisappearingAsync()
    {
        speech.Stop();
        audio.StopPlayback();
        if (Current is { IsRecording: true } q)
        {
            q.IsRecording = false;
            await audio.StopRecordingAsync();
        }
        await SaveCurrentAsync(quiet: true);
    }
}

// ===================================================================== result and answer review (FR-L52, FR-L72)

public sealed record ReviewRow(ReviewedAnswerDto Item)
{
    public string Header => $"{Item.Number}. {Item.Stem}";
    public string? Prompt => Item.Prompt;
    public bool HasPrompt => !string.IsNullOrWhiteSpace(Item.Prompt);
    public string YourAnswer => $"Your answer: {Item.YourAnswer}";
    public string CorrectAnswer => $"Answer: {Item.CorrectAnswer}";
    public bool ShowCorrect => Item.IsCorrect != true && !Item.NeedsReview && Item.Type != QuestionType.Writing;
    public string? Explanation => Item.Explanation;
    public bool HasExplanation => !string.IsNullOrWhiteSpace(Item.Explanation);
    public string? Feedback => Item.Feedback is { Length: > 0 } f ? $"Teacher: {f}" : null;
    public bool HasFeedback => Feedback is not null;
    public string Mark => Item.NeedsReview ? "⏳" : Item.IsCorrect == true ? "✓" : Item.Score > 0 ? "½" : "✗";
    public string ScoreText => Item.NeedsReview ? "being graded" : $"{Item.Score:0.#} / {Item.MaxScore}";
    public bool IsCorrect => Item.IsCorrect == true;
}

public sealed record SkillBar(string Name, int Percentage)
{
    public double Fraction => Math.Clamp(Percentage / 100.0, 0, 1);
    public string Text => $"{Percentage}%";
}

public sealed partial class QuizResultViewModel(
    IAssessmentApi api,
    INavigationService navigation,
    PlacementResults placement) : BaseViewModel
{
    private int _attemptId;
    private int _courseId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ScoreText), nameof(Headline), nameof(Detail), nameof(IsPlacement), nameof(LevelText),
        nameof(IsPending), nameof(PassText), nameof(IsPassed))]
    private AttemptResultDto? result;

    [ObservableProperty] private bool showReview;

    public ObservableCollection<SkillBar> Skills { get; } = [];
    public ObservableCollection<ReviewRow> ReviewRows { get; } = [];

    public bool HasResult => Result is not null;
    public bool IsPlacement => Result?.Type == QuizType.Placement;
    public bool IsPending => Result?.Status == AttemptStatus.PendingReview;
    public bool IsPassed => Result?.IsPassed == true;
    public string ScoreText => Result is null ? "" : IsPending ? "⏳" : $"{Result.Percentage}%";
    public string Headline => Result switch
    {
        null => "",
        { Type: QuizType.Placement } => "Placement test done",
        { Status: AttemptStatus.PendingReview } => "Submitted!",
        { Type: QuizType.Monthly, IsPassed: true } => "Exam passed 🎉",
        { Type: QuizType.Monthly } => "Not passed this time",
        { Percentage: >= 80 } => "Great work! 🎉",
        { IsPassed: true } => "Well done!",
        _ => "Keep practising"
    };
    public string Detail => Result is null ? "" : IsPending
        ? $"{Result.Correct} of {Result.Total - Result.PendingReview} objective answers correct. A teacher is grading {Result.PendingReview} writing/speaking answer(s); your mark updates when they're done."
        : $"{Result.Correct} of {Result.Total} correct · {Result.Score:0.#} / {Result.MaxScore} points" +
          (Result.IsPractice ? " · practice, not counted in your marks" : "");
    public string PassText => Result is null || IsPlacement || IsPending ? "" : $"Pass mark {Result.PassMark}%";
    public string LevelText => Result?.SuggestedLevel is { } l ? $"Suggested level: {l}" : "";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "attemptId"), out var id)) _attemptId = id;
        if (int.TryParse(Param(parameters, "courseId"), out var c)) _courseId = c;
    }

    public override Task OnAppearingAsync() => Result is not null ? Task.CompletedTask : RunAsync(async () =>
    {
        var r = (await api.GetResultAsync(_attemptId)).Data;
        Skills.Clear();
        foreach (var s in r?.Skills ?? []) Skills.Add(new SkillBar(s.Skill.ToString(), s.Percentage));
        Result = r;
    });

    [RelayCommand]
    private async Task ToggleReview()
    {
        if (ShowReview)
        {
            ShowReview = false;
            return;
        }
        if (ReviewRows.Count == 0)
        {
            await RunAsync(async () =>
            {
                var review = (await api.GetReviewAsync(_attemptId)).Data;
                foreach (var a in review?.Answers ?? []) ReviewRows.Add(new ReviewRow(a));
            });
        }
        ShowReview = ReviewRows.Count > 0;
    }

    /// <summary>Placement: hand the level back to onboarding (FR-L11).</summary>
    [RelayCommand]
    private Task UseLevel()
    {
        if (Result?.SuggestedLevel is { } level && _courseId > 0) placement.Set(_courseId, level);
        return navigation.GoBackAsync();
    }

    [RelayCommand]
    private Task Done() => navigation.GoBackAsync();
}

// ===================================================================== exams list (FR-L60, FR-L64)

public sealed record ExamItem(ExamSummaryDto Dto)
{
    public string Title => Dto.Title;
    public string Status => Dto.StatusText;
    public string Meta => $"Month {Dto.MonthNo} · {Dto.QuestionCount} questions · {Dto.TimeLimitMinutes} min · pass {Dto.PassMark}%";
    public bool CanStart => Dto.Availability is ExamAvailability.Available or ExamAvailability.RetakeAvailable or ExamAvailability.InProgress;
    public bool HasResult => Dto.LastAttemptId is not null && !CanStart || Dto.Availability is ExamAvailability.Passed or ExamAvailability.PendingReview;
    public string ActionText => Dto.Availability switch
    {
        ExamAvailability.InProgress => "Resume",
        ExamAvailability.RetakeAvailable => "Retake",
        _ => "Start"
    };
    public string Progress => Dto.LecturesTotal == 0 ? "" : $"{Dto.LecturesDone}/{Dto.LecturesTotal} lectures of this month done";
    public bool IsPassed => Dto.Availability == ExamAvailability.Passed;
    public bool IsLocked => Dto.Availability == ExamAvailability.Locked;
}

public sealed partial class ExamsViewModel(
    IAssessmentApi api,
    ICourseContext courseContext,
    INavigationService navigation,
    IDialogService dialogs) : BaseViewModel
{
    private int _learnerCourseId;

    public ObservableCollection<ExamItem> Exams { get; } = [];
    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isRefreshing;
    public bool IsEmpty => IsLoaded && Exams.Count == 0;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "learnerCourseId"), out var id)) _learnerCourseId = id;
    }

    public override Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (_learnerCourseId == 0) _learnerCourseId = await courseContext.GetAsync() ?? 0;
        var items = (await api.GetExamsAsync(_learnerCourseId)).Data ?? [];
        Exams.Clear();
        foreach (var e in items) Exams.Add(new ExamItem(e));
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    });

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync();
        IsRefreshing = false;
    }

    [RelayCommand]
    private async Task Start(ExamItem exam)
    {
        if (!exam.CanStart) return;
        if (exam.Dto.Availability != ExamAvailability.InProgress &&
            !await dialogs.ConfirmAsync(exam.Title,
                $"{exam.Dto.QuestionCount} questions, {exam.Dto.TimeLimitMinutes} minutes. The timer starts now and keeps running if you leave the app. " +
                $"Answers save automatically. Attempt {exam.Dto.AttemptsUsed + 1} of {exam.Dto.MaxAttempts}.", "Start exam", "Not now"))
            return;

        AttemptDto? attempt = null;
        await RunAsync(async () => attempt = (await api.StartExamAsync(_learnerCourseId, exam.Dto.QuizId)).Data);
        if (attempt is not null)
            await navigation.GoToAsync(Routes.Quiz, new Dictionary<string, object> { ["attemptId"] = attempt.Id });
    }

    [RelayCommand]
    private Task OpenResult(ExamItem exam) => exam.Dto.LastAttemptId is { } id
        ? navigation.GoToAsync(Routes.QuizResult, new Dictionary<string, object> { ["attemptId"] = id })
        : Task.CompletedTask;
}

// ===================================================================== marks (FR-L70 .. FR-L73)

public sealed record TrendBar(string Label, int Percentage, bool IsExam)
{
    /// <summary>Bar height in device units for a 120-unit chart.</summary>
    public double Height => Math.Max(4, 120 * Percentage / 100.0);
}

public sealed record MarkItem(MarkRowDto Row)
{
    public string Title => Row.Title;
    public string Date => Row.Date.ToLocalTime().ToString("d MMM yyyy");
    public string Score => Row.Status == AttemptStatus.PendingReview ? "grading…" : $"{Row.Percentage}%";
    public string Badge => Row.Status == AttemptStatus.PendingReview ? "" : Row.Type == QuizType.Daily
        ? (Row.IsPractice ? "practice" : "")
        : (Row.IsPassed ? "passed" : "not passed") + (Row.Counts ? "" : " · not best");
    public bool IsPassed => Row.IsPassed;
    public bool IsExam => Row.Type == QuizType.Monthly;
}

public sealed partial class MarksViewModel(
    IAssessmentApi api,
    ICourseContext courseContext,
    INavigationService navigation) : BaseViewModel
{
    private int _learnerCourseId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GradeText), nameof(DailyText), nameof(ExamText), nameof(RuleText), nameof(HasMarks))]
    private MarksDto? marks;

    [ObservableProperty] private bool isRefreshing;

    public ObservableCollection<TrendBar> Trend { get; } = [];
    public ObservableCollection<SkillBar> Skills { get; } = [];
    public ObservableCollection<MarkItem> Rows { get; } = [];

    public bool HasMarks => Rows.Count > 0;
    public string GradeText => Marks?.CourseGrade is { } g ? $"{g}%" : "—";
    public string DailyText => Marks?.DailyAverage is { } d ? $"{d}%" : "—";
    public string ExamText => Marks?.ExamAverage is { } e ? $"{e}%" : "—";
    public string RuleText => Marks is null ? "" : $"Course grade = {Marks.QuizWeight}% daily quizzes + {Marks.ExamWeight}% monthly exams (best attempt).";

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "learnerCourseId"), out var id)) _learnerCourseId = id;
    }

    public override Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (_learnerCourseId == 0) _learnerCourseId = await courseContext.GetAsync() ?? 0;
        var m = (await api.GetMarksAsync(_learnerCourseId)).Data;
        Trend.Clear();
        foreach (var t in m?.Trend ?? []) Trend.Add(new TrendBar(t.Date.ToString("d/M"), t.Percentage, t.Type == QuizType.Monthly));
        Skills.Clear();
        foreach (var s in m?.Skills ?? []) Skills.Add(new SkillBar(s.Skill.ToString(), s.Percentage));
        Rows.Clear();
        foreach (var r in m?.Rows ?? []) Rows.Add(new MarkItem(r));
        Marks = m;
    });

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadAsync();
        IsRefreshing = false;
    }

    [RelayCommand]
    private Task Open(MarkItem item) =>
        navigation.GoToAsync(Routes.QuizResult, new Dictionary<string, object> { ["attemptId"] = item.Row.AttemptId });
}
