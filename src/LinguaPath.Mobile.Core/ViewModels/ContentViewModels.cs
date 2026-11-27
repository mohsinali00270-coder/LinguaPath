using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaPath.Mobile.Core.Api;
using LinguaPath.Mobile.Core.Services;
using LinguaPath.Shared;
using LinguaPath.Shared.Contracts.Content;
using LinguaPath.Shared.Contracts.Learning;

namespace LinguaPath.Mobile.Core.ViewModels;

/// <summary>Opens a lecture from Home or the roadmap; locked lectures explain how to unlock them.</summary>
internal static class LectureOpener
{
    public static Task OpenAsync(RoadmapLectureDto lecture, int learnerCourseId, INavigationService navigation, IDialogService dialogs) =>
        lecture.State == RoadmapItemState.Locked
            ? dialogs.ToastAsync("Finish the lectures before this one to unlock it.")
            : navigation.GoToAsync(Routes.Lecture, new Dictionary<string, object>
            {
                ["id"] = lecture.LectureId,
                ["learnerCourseId"] = learnerCourseId
            });
}

/// <summary>A post in a list, with like/save state that updates in place (FR-L40 .. FR-L42).</summary>
public sealed partial class PostItem(PostDto post) : ObservableObject
{
    public PostDto Post { get; } = post;
    public int Id => Post.Id;
    public string Title => Post.Title;
    public string? Body => Post.Body;
    public string? ImageUrl => Post.ImageUrl;
    public bool HasImage => !string.IsNullOrWhiteSpace(Post.ImageUrl);
    public bool HasUrl => !string.IsNullOrWhiteSpace(Post.Url);
    public string Meta => $"{Post.PostType} · {Post.LanguageName}{(Post.Level is { } l ? $" · {l}" : "")} · {Post.PublishedAt.ToLocalTime():d MMM}";
    public string TagsText => Post.Tags.Count == 0 ? "" : string.Join("  ", Post.Tags.Select(t => "#" + t));
    public bool IsPinned => Post.IsPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeText))]
    private int likes = post.Likes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeText))]
    private bool likedByMe = post.LikedByMe;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    private bool savedByMe = post.SavedByMe;

    public string LikeText => $"{(LikedByMe ? "♥" : "♡")} {Likes}";
    public string SaveText => SavedByMe ? "Saved" : "Save";

    public void Apply(PostInteractionResponse r)
    {
        Likes = r.Likes;
        LikedByMe = r.LikedByMe;
        SavedByMe = r.SavedByMe;
    }
}

/// <summary>Shared like/save/open/report actions for post lists.</summary>
internal static class PostActions
{
    public static async Task ToggleLikeAsync(IContentApi api, IDialogService dialogs, PostItem item)
    {
        try
        {
            var r = (await api.ToggleLikeAsync(item.Id)).Data;
            if (r is not null) item.Apply(r);
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    public static async Task ToggleSaveAsync(IContentApi api, IDialogService dialogs, PostItem item)
    {
        try
        {
            var r = (await api.ToggleSaveAsync(item.Id)).Data;
            if (r is not null)
            {
                item.Apply(r);
                await dialogs.ToastAsync(r.SavedByMe ? "Saved for later." : "Removed from saved.");
            }
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    public static async Task ReportAsync(IContentApi api, IDialogService dialogs, PostItem item)
    {
        var message = await dialogs.PromptAsync("Report a problem", "What's wrong with this post?", "Send", "e.g. the link is broken");
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Trim().Length < 5)
        {
            await dialogs.ToastAsync("Please describe the problem in a few words.");
            return;
        }
        try
        {
            await api.ReportPostAsync(item.Id, new ReportContentRequest(message.Trim()));
            await dialogs.ToastAsync("Thanks! The team will check it.");
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }
}

public sealed record VocabularyRow(LectureVocabularyDto Item)
{
    public string Word => Item.Word;
    public string Meaning => Item.Meaning;
    public string Detail => string.Join(" · ", new[] { Item.Pronunciation, Item.PartOfSpeech }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool HasDetail => Detail.Length > 0;
}

public sealed record ExampleRow(LectureExampleDto Item)
{
    public string TargetText => Item.TargetText;
    public string? Translation => Item.Translation;
    public string? Note => Item.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);
}

public sealed record LinkRow(LectureLinkDto Item)
{
    public string Title => Item.Title;
    public string Url => Item.Url;
    public string Kind => Item.LinkType switch
    {
        LinkType.Video => "▶ Video",
        LinkType.Pdf => "PDF",
        LinkType.Podcast => "Podcast",
        LinkType.Article => "Article",
        _ => "Link"
    };
}

/// <summary>
/// Lecture reader (FR-L32 .. FR-L38): lesson text, tap a word, read aloud, examples, vocabulary, links,
/// bookmark, note, rating, error report, mark complete (with a minimum reading time) and offline copies.
/// </summary>
public sealed partial class LectureViewModel(
    IContentApi contentApi,
    INavigationService navigation,
    IDialogService dialogs,
    IMediaService media,
    ISpeechService speech,
    IOfflineStore offline,
    IAppTheme theme,
    TimeProvider clock,
    IPracticeApi practiceApi,
    IAccountApi accountApi,
    PracticeLanguage practiceLanguage,
    IOfflineSyncQueue offlineSync) : BaseViewModel
{
    private int _lectureId;
    private int? _learnerCourseId;

    // Reading time: seconds already stored on the server + time visible on screen since the last flush.
    private long? _visibleSince;
    private int _unsentSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLecture), nameof(Header), nameof(Subtitle), nameof(HasObjectives), nameof(IsCompleted),
        nameof(HasExamples), nameof(HasVocabulary), nameof(HasLinks), nameof(HasRelatedPosts), nameof(BookmarkText),
        nameof(RatingText), nameof(HasCover), nameof(IsRtl))]
    private LectureDto? lecture;

    [ObservableProperty] private string? html;
    [ObservableProperty] private string? noteText;
    [ObservableProperty] private bool isBookmarked;
    [ObservableProperty] private int myRating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineText))]
    private bool isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineText))]
    private bool isOffline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete), nameof(CompleteText))]
    private int secondsRead;

    [ObservableProperty] private bool isSpeaking;

    public ObservableCollection<ExampleRow> Examples { get; } = [];
    public ObservableCollection<VocabularyRow> Vocabulary { get; } = [];
    public ObservableCollection<LinkRow> Links { get; } = [];
    public ObservableCollection<PostItem> RelatedPosts { get; } = [];

    public bool HasLecture => Lecture is not null;
    public string Header => Lecture?.Title ?? "Lecture";
    public string Subtitle => Lecture is null ? "" : $"{Lecture.ModuleTitle} · {Lecture.Level} · about {Lecture.EstimatedMinutes} min";
    public bool HasObjectives => !string.IsNullOrWhiteSpace(Lecture?.Objectives);
    public bool HasCover => !string.IsNullOrWhiteSpace(Lecture?.CoverUrl);
    public bool IsCompleted => Lecture?.State == RoadmapItemState.Completed;
    public bool HasExamples => Examples.Count > 0;
    public bool HasVocabulary => Vocabulary.Count > 0;
    public bool HasLinks => Links.Count > 0;
    public bool HasRelatedPosts => RelatedPosts.Count > 0;
    public bool IsRtl => Lecture?.Direction == TextDirection.Rtl;
    public string BookmarkText => IsBookmarked ? "★ Bookmarked" : "☆ Bookmark";
    public string RatingText => Lecture?.AverageRating is { } avg ? $"{avg:0.0} ★ from {Lecture.RatingCount} rating(s)" : "No ratings yet";
    public string OfflineText => IsOffline ? "You're offline — showing your downloaded copy." : IsDownloaded ? "Downloaded for offline reading" : "";

    public int SecondsLeft => Lecture is null ? 0 : Math.Max(0, Lecture.MinimumSecondsToComplete - SecondsRead);
    public bool CanComplete => Lecture is not null && !IsCompleted && SecondsLeft == 0;
    public string CompleteText => IsCompleted ? "Completed ✓"
        : SecondsLeft > 0 ? $"Keep reading… ({FormatSeconds(SecondsLeft)})"
        : "Mark as complete";

    partial void OnIsBookmarkedChanged(bool value) => OnPropertyChanged(nameof(BookmarkText));

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (int.TryParse(Param(parameters, "id"), out var id)) _lectureId = id;
        if (int.TryParse(Param(parameters, "learnerCourseId"), out var lc)) _learnerCourseId = lc;
    }

    public override async Task OnAppearingAsync()
    {
        if (Lecture is null || Lecture.Id != _lectureId) await LoadAsync();
        StartVisibleClock();
    }

    public override async Task OnDisappearingAsync()
    {
        speech.Stop();
        IsSpeaking = false;
        StopVisibleClock();
        await FlushProgressAsync();
    }

    /// <summary>Called once a second by the page while it is visible, to update the reading timer.</summary>
    public void Tick()
    {
        if (Lecture is null || _visibleSince is null) return;
        SecondsRead = Lecture.TimeSpentSec + _unsentSeconds + VisibleSeconds();
        OnPropertyChanged(nameof(SecondsLeft));
    }

    private int VisibleSeconds() => _visibleSince is { } since ? (int)clock.GetElapsedTime(since).TotalSeconds : 0;

    private void StartVisibleClock() => _visibleSince ??= clock.GetTimestamp();

    private void StopVisibleClock()
    {
        _unsentSeconds += VisibleSeconds();
        _visibleSince = null;
    }

    private string CacheKey(int id) => $"lecture-{id}.json";

    private async Task LoadAsync()
    {
        IsOffline = false;
        await RunAsync(async () =>
        {
            var dto = (await contentApi.GetLectureAsync(_lectureId, _learnerCourseId)).Data
                      ?? throw new InvalidOperationException("Lecture not found.");
            Show(dto);
            IsDownloaded = offline.Exists(CacheKey(dto.Id));
            if (IsDownloaded) await offline.SaveAsync(CacheKey(dto.Id), JsonSerializer.Serialize(dto, JsonDefaults.Options));
        }, async error =>
        {
            if (error.Code == "lecture_locked")
            {
                await dialogs.AlertAsync("Locked", error.Message);
                await navigation.GoBackAsync();
                return true;
            }
            if (!error.IsNetwork) return false;
            var cached = await offline.LoadAsync(CacheKey(_lectureId));
            if (cached is null) return false;
            Show(JsonSerializer.Deserialize<LectureDto>(cached, JsonDefaults.Options)!);
            IsDownloaded = true;
            IsOffline = true;
            return true;
        });
    }

    private void Show(LectureDto dto)
    {
        _unsentSeconds = 0;
        _learnerCourseId ??= dto.LearnerCourseId;
        Examples.Clear();
        foreach (var e in dto.Examples) Examples.Add(new ExampleRow(e));
        Vocabulary.Clear();
        foreach (var v in dto.Vocabulary) Vocabulary.Add(new VocabularyRow(v));
        Links.Clear();
        foreach (var l in dto.Links) Links.Add(new LinkRow(l));
        RelatedPosts.Clear();
        foreach (var p in dto.RelatedPosts) RelatedPosts.Add(new PostItem(p));

        IsBookmarked = dto.IsBookmarked;
        NoteText = dto.Note;
        MyRating = dto.MyRating ?? 0;
        SecondsRead = dto.TimeSpentSec;
        Html = LectureHtml.Build(dto.BodyHtml, dto.Direction, theme.IsDark, dto.LanguageIsoCode, interactive: true);
        Lecture = dto;
        OnPropertyChanged(nameof(SecondsLeft));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CompleteText));
    }

    private async Task FlushProgressAsync()
    {
        if (Lecture is null || IsOffline || _learnerCourseId is not { } lc || _unsentSeconds <= 0 || IsCompleted) return;
        var seconds = Math.Min(_unsentSeconds, 3600);
        try
        {
            await contentApi.AddProgressAsync(Lecture.Id, new LectureProgressRequest(lc, seconds));
            _unsentSeconds -= seconds;
            Lecture = Lecture with { TimeSpentSec = Lecture.TimeSpentSec + seconds };
        }
        catch
        {
            // Best effort: the seconds stay counted locally and are sent with the next save or completion.
        }
    }

    // ------------------------------------------------------------------ tap a word, read aloud

    /// <summary>The page calls this when the reader HTML reports a tapped word (linguapath://word?w=...).</summary>
    public Task LookupWordAsync(string word)
    {
        var key = Normalize(word);
        var match = Vocabulary.FirstOrDefault(v => Normalize(v.Word) == key)
                    ?? Vocabulary.FirstOrDefault(v => Normalize(v.Word).Split(' ').Contains(key));
        return match is not null ? ShowWordAsync(match) : ShowUnknownWordAsync(word);
    }

    private const string ListenOption = "🔊 Listen";
    private const string TranslateOption = "🌐 Translate";
    private const string SaveOption = "⭐ Save to My words";

    private async Task ShowWordAsync(VocabularyRow v)
    {
        var detail = v.HasDetail ? $"{v.Detail}\n{v.Meaning}" : v.Meaning;
        var choice = await dialogs.ChooseAsync(v.Word, detail, "Close", ListenOption, SaveOption);
        if (choice == ListenOption) await SpeakAsync(v.Word);
        else if (choice == SaveOption) await SaveWordAsync(v.Word, v.Meaning);
    }

    /// <summary>Not in the word list: offer read-aloud, a translation (FR-L100) and saving it (FR-L103).</summary>
    private async Task ShowUnknownWordAsync(string word)
    {
        var choice = await dialogs.ChooseAsync(word, null, "Close", ListenOption, TranslateOption);
        if (choice == ListenOption)
        {
            await SpeakAsync(word);
            return;
        }
        if (choice != TranslateOption || Lecture is null) return;

        await NativeLanguageResolver.EnsureAsync(practiceLanguage, accountApi, practiceApi);
        Shared.Contracts.Translation.TranslationDto? translation = null;
        await RunAsync(async () => translation = (await practiceApi.TranslateAsync(
            new Shared.Contracts.Translation.TranslateRequest(word, Lecture.LanguageIsoCode, practiceLanguage.NativeCode))).Data);
        if (translation is null) return;
        if (await dialogs.ConfirmAsync(word, translation.TranslatedText, SaveOption, "Close"))
            await SaveWordAsync(word, translation.TranslatedText);
    }

    private async Task SaveWordAsync(string word, string meaning)
    {
        if (Lecture is null) return;
        if (Lecture.LanguageId == 0)
        {
            // Offline copies saved before this version don't know their language.
            await dialogs.ToastAsync("Open this lecture online once to save words from it.");
            return;
        }
        var example = Examples.FirstOrDefault(e => e.TargetText.Contains(word, StringComparison.OrdinalIgnoreCase))?.TargetText;
        await RunAsync(async () =>
        {
            await practiceApi.SaveWordAsync(new Shared.Contracts.Words.SaveWordRequest(Lecture.LanguageId, word, meaning, example));
            await dialogs.ToastAsync($"\"{word}\" saved to My words");
        });
    }

    [RelayCommand]
    private Task Word(VocabularyRow row) => ShowWordAsync(row);

    [RelayCommand]
    private Task SpeakExample(ExampleRow row) => SpeakAsync(row.TargetText);

    [RelayCommand]
    private Task SpeakWord(VocabularyRow row) => SpeakAsync(row.Word);

    /// <summary>Reads the whole lesson text aloud; tap again to stop (FR-L34).</summary>
    [RelayCommand]
    private async Task ReadAloud()
    {
        if (IsSpeaking)
        {
            speech.Stop();
            IsSpeaking = false;
            return;
        }
        if (Lecture is null) return;
        await SpeakAsync(PlainText(Lecture.BodyHtml), trackState: true);
    }

    private async Task SpeakAsync(string text, bool trackState = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (trackState) IsSpeaking = true;
            await speech.SpeakAsync(text, Lecture?.SpeechLocale ?? Lecture?.LanguageIsoCode);
        }
        catch (OperationCanceledException) { }
        catch
        {
            await dialogs.ToastAsync("Read-aloud isn't available on this device.");
        }
        finally
        {
            if (trackState) IsSpeaking = false;
        }
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand]
    private async Task ToggleBookmark()
    {
        if (Lecture is null || await BlockedOfflineAsync()) return;
        var wanted = !IsBookmarked;
        try
        {
            if (wanted) await contentApi.AddBookmarkAsync(Lecture.Id);
            else await contentApi.RemoveBookmarkAsync(Lecture.Id);
            IsBookmarked = wanted;
            await dialogs.ToastAsync(wanted ? "Bookmarked." : "Bookmark removed.");
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    [RelayCommand]
    private async Task SaveNote()
    {
        if (Lecture is null || await BlockedOfflineAsync()) return;
        if (NoteText is { Length: > 2000 })
        {
            Errors.Set("Text", "Notes can be up to 2,000 characters.");
            return;
        }
        Errors.Clear();
        try
        {
            await contentApi.SaveNoteAsync(Lecture.Id, new SaveNoteRequest(string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim()));
            await dialogs.ToastAsync("Note saved.");
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    [RelayCommand]
    private async Task Rate(string? stars)
    {
        if (Lecture is null || !int.TryParse(stars, out var value) || value is < 1 or > 5 || await BlockedOfflineAsync()) return;
        try
        {
            await contentApi.RateAsync(Lecture.Id, new RateLectureRequest(value, null));
            MyRating = value;
            await dialogs.ToastAsync("Thanks for rating!");
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    [RelayCommand]
    private async Task Report()
    {
        if (Lecture is null || await BlockedOfflineAsync()) return;
        var message = await dialogs.PromptAsync("Report a mistake", "What's wrong in this lecture?", "Send", "e.g. the translation of 'hola' is wrong");
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Trim().Length < 5)
        {
            await dialogs.ToastAsync("Please describe the problem in a few words.");
            return;
        }
        try
        {
            await contentApi.ReportLectureAsync(Lecture.Id, new ReportContentRequest(message.Trim()));
            await dialogs.ToastAsync("Thanks! The team will check it.");
        }
        catch (Exception ex) { await dialogs.ToastAsync(ApiError.From(ex).Message); }
    }

    [RelayCommand]
    private Task OpenLink(LinkRow link) => media.OpenUrlAsync(link.Url);

    [RelayCommand]
    private Task OpenPost(PostItem post) => post.HasUrl ? media.OpenUrlAsync(post.Post.Url!) : dialogs.AlertAsync(post.Title, post.Body ?? "");

    [RelayCommand]
    private Task LikePost(PostItem post) => PostActions.ToggleLikeAsync(contentApi, dialogs, post);

    /// <summary>Keeps a copy on the device for reading without internet (FR-L38).</summary>
    [RelayCommand]
    private async Task ToggleDownload()
    {
        if (Lecture is null) return;
        if (IsDownloaded)
        {
            offline.Remove(CacheKey(Lecture.Id));
            IsDownloaded = false;
            await dialogs.ToastAsync("Offline copy removed.");
            return;
        }
        await offline.SaveAsync(CacheKey(Lecture.Id), JsonSerializer.Serialize(Lecture, JsonDefaults.Options));
        IsDownloaded = true;
        await dialogs.ToastAsync("Saved for offline reading.");
    }

    private async Task QueueCompletionAsync(int learnerCourseId, int seconds)
    {
        if (Lecture is null) return;
        await offlineSync.EnqueueAsync(new OfflineOp(OfflineOp.LectureComplete, Lecture.Id, learnerCourseId, seconds, null, clock.GetUtcNow()));
        _unsentSeconds = 0;
        Lecture = Lecture with { State = RoadmapItemState.Completed, TimeSpentSec = Lecture.TimeSpentSec + seconds };
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CompleteText));
        await dialogs.ToastAsync(Localization.Localizer.T("Offline_Queued"));
    }

    /// <summary>Mark complete (FR-L35): needs the minimum reading time; unlocks and offers the next lecture.</summary>
    [RelayCommand]
    private async Task Complete()
    {
        if (Lecture is null || IsCompleted) return;
        Tick();
        if (SecondsLeft > 0)
        {
            await dialogs.ToastAsync($"Take a little more time with this lecture ({FormatSeconds(SecondsLeft)} left).");
            return;
        }
        if (_learnerCourseId is not { } lc) return;

        StopVisibleClock();
        var seconds = Math.Min(_unsentSeconds, 3600);
        // Phase 10 (NFR-09): offline, the completion is queued and sent when the phone is back online.
        if (IsOffline)
        {
            await QueueCompletionAsync(lc, seconds);
            return;
        }
        CompleteLectureResponse? result = null;
        var queued = false;
        await RunAsync(async () =>
        {
            result = (await contentApi.CompleteAsync(Lecture.Id, new CompleteLectureRequest(lc, seconds))).Data;
            _unsentSeconds = 0;
        }, error =>
        {
            queued = error.IsNetwork;
            return Task.FromResult(queued);
        });
        if (queued)
        {
            await QueueCompletionAsync(lc, seconds);
            return;
        }
        StartVisibleClock();
        if (result is null) return;

        Lecture = Lecture with { State = RoadmapItemState.Completed, TimeSpentSec = Lecture.TimeSpentSec + seconds };
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(CompleteText));

        // Phase 7: "+15 points · 🔥 4-day streak", and any new badge.
        var reward = Rewards(result);
        if (result.NewBadges is { Count: > 0 } badges)
            await dialogs.AlertAsync("New badge! 🎉", string.Join("\n", badges));

        if (result.AllLecturesDone)
        {
            await dialogs.AlertAsync("All lectures done! 🎉", $"You've finished every lecture in this course ({result.ProgressPercent}%).{reward}");
            await navigation.GoBackAsync();
            return;
        }

        var progress = $"{result.CompletedLectures} of {result.TotalLectures} lectures · {result.ProgressPercent}%{reward}";
        if (result.NextLectureId is { } next &&
            await dialogs.ConfirmAsync("Lecture complete ✓", $"{progress}\n\nNext: {result.NextLectureTitle}", "Start next", "Later"))
        {
            await FlushProgressAsync();
            _lectureId = next;
            _visibleSince = clock.GetTimestamp();
            await LoadAsync();
        }
        else if (result.NextLectureId is null)
        {
            await dialogs.ToastAsync($"Lecture complete ✓ {progress}");
        }
    }

    private static string Rewards(CompleteLectureResponse r)
    {
        var parts = new List<string>();
        if (r.PointsEarned > 0) parts.Add($"+{r.PointsEarned} points");
        if (r.Streak > 1) parts.Add($"🔥 {r.Streak}-day streak");
        return parts.Count == 0 ? "" : "\n" + string.Join(" · ", parts);
    }

    private async Task<bool> BlockedOfflineAsync()
    {
        if (!IsOffline) return false;
        await dialogs.ToastAsync("You're offline. Try again when you're connected.");
        return true;
    }

    // ------------------------------------------------------------------ helpers

    internal static string Normalize(string word) =>
        new string(word.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC)
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark).ToArray());

    /// <summary>Lesson HTML → text for read-aloud (tags removed, entities decoded).</summary>
    internal static string PlainText(string html)
    {
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p|/li|/h\d|/tr|/blockquote)\s*/?>", ". ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var text = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return System.Text.RegularExpressions.Regex.Replace(text, @"(\.\s*){2,}", ". ").Trim(' ', '.');
    }

    private static string FormatSeconds(int s) => s >= 60 ? $"{s / 60}:{s % 60:00}" : $"{s}s";
}

/// <summary>Posts feed (FR-L40 .. FR-L42): tips, articles, videos; like, save, filter, report.</summary>
public sealed partial class PostsViewModel(IContentApi contentApi, IMediaService media, IDialogService dialogs) : BaseViewModel
{
    private int _page = 1;
    private bool _hasMore = true;

    public ObservableCollection<PostItem> Posts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllActive), nameof(SavedActive))]
    private bool showSaved;

    [ObservableProperty] private bool isRefreshing;
    [ObservableProperty] private bool isLoaded;

    public bool AllActive => !ShowSaved;
    public bool SavedActive => ShowSaved;
    public bool IsEmpty => IsLoaded && Posts.Count == 0;
    public string EmptyText => ShowSaved ? "Posts you save show up here." : "No posts yet for your languages. Check back soon!";

    public override Task OnAppearingAsync() => Posts.Count == 0 ? ReloadAsync() : Task.CompletedTask;

    private async Task ReloadAsync()
    {
        _page = 1;
        _hasMore = true;
        Posts.Clear();
        await LoadPageAsync();
    }

    private Task LoadPageAsync() => RunAsync(async () =>
    {
        var page = (await contentApi.GetFeedAsync(saved: ShowSaved ? true : null, page: _page)).Data;
        foreach (var p in page?.Items ?? []) Posts.Add(new PostItem(p));
        _hasMore = page is not null && _page < page.TotalPages;
        _page++;
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    });

    [RelayCommand]
    private async Task Refresh()
    {
        await ReloadAsync();
        IsRefreshing = false;
    }

    [RelayCommand]
    private Task LoadMore() => _hasMore && !IsBusy ? LoadPageAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task ShowAll()
    {
        if (!ShowSaved) return Task.CompletedTask;
        ShowSaved = false;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task ShowSavedOnly()
    {
        if (ShowSaved) return Task.CompletedTask;
        ShowSaved = true;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task Open(PostItem post) => post.HasUrl ? media.OpenUrlAsync(post.Post.Url!) : dialogs.AlertAsync(post.Title, post.Body ?? "");

    [RelayCommand]
    private Task Like(PostItem post) => PostActions.ToggleLikeAsync(contentApi, dialogs, post);

    [RelayCommand]
    private async Task Save(PostItem post)
    {
        await PostActions.ToggleSaveAsync(contentApi, dialogs, post);
        if (ShowSaved && !post.SavedByMe)
        {
            Posts.Remove(post);
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private Task Report(PostItem post) => PostActions.ReportAsync(contentApi, dialogs, post);
}

/// <summary>Bookmarked lectures (FR-L36).</summary>
public sealed partial class BookmarksViewModel(IContentApi contentApi, INavigationService navigation) : BaseViewModel
{
    public ObservableCollection<BookmarkDto> Items { get; } = [];
    [ObservableProperty] private bool isLoaded;
    public bool IsEmpty => IsLoaded && Items.Count == 0;

    public override Task OnAppearingAsync() => RunAsync(async () =>
    {
        Items.Clear();
        foreach (var b in (await contentApi.GetBookmarksAsync()).Data ?? []) Items.Add(b);
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    });

    [RelayCommand]
    private Task Open(BookmarkDto bookmark) =>
        navigation.GoToAsync(Routes.Lecture, new Dictionary<string, object> { ["id"] = bookmark.LectureId });
}
