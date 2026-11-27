using LinguaPath.Mobile.Core.Api;
using LinguaPath.Shared.Contracts.Competitions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.Mobile.Core.Services;

/// <summary>The live competition connection (/hubs/competition). Events are raised on the UI thread; answers fall back to REST.</summary>
public interface ICompetitionConnection
{
    bool IsConnected { get; }
    event EventHandler<CompetitionQuestionDto>? QuestionStarted;
    event EventHandler<OpponentAnsweredDto>? OpponentAnswered;
    event EventHandler<QuestionResultDto>? QuestionResult;
    event EventHandler<int>? MatchUpdated;
    event EventHandler<int>? MatchEnded;
    event EventHandler<CompetitionSummaryDto>? ChallengeReceived;

    Task StartAsync(CancellationToken ct = default);
    Task<AnswerAckDto> SubmitAsync(int competitionId, SubmitAnswerRequest request, CancellationToken ct = default);
}

public sealed class CompetitionConnection(ApiSettings api, IAuthSession session, IUiDispatcher ui, IServiceProvider services)
    : ICompetitionConnection, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private bool _watchingSession;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    public event EventHandler<CompetitionQuestionDto>? QuestionStarted;
    public event EventHandler<OpponentAnsweredDto>? OpponentAnswered;
    public event EventHandler<QuestionResultDto>? QuestionResult;
    public event EventHandler<int>? MatchUpdated;
    public event EventHandler<int>? MatchEnded;
    public event EventHandler<CompetitionSummaryDto>? ChallengeReceived;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!session.HasTokens) return;
        if (!_watchingSession)
        {
            _watchingSession = true;
            session.SignedOut += async (_, _) =>
            {
                if (_hub is not null) await _hub.DisposeAsync();
                _hub = null;
            };
        }
        await _gate.WaitAsync(ct);
        try
        {
            _hub ??= Build();
            if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync(ct);
        }
        catch (Exception)
        {
            // Offline: the match screen polls over REST instead.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AnswerAckDto> SubmitAsync(int competitionId, SubmitAnswerRequest request, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            try
            {
                return await _hub!.InvokeAsync<AnswerAckDto>("SubmitAnswer", competitionId, request, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Fall through to REST: the server ignores a second answer to the same question.
            }
        }
        try
        {
            return (await services.GetRequiredService<ICompetitionApi>().AnswerAsync(competitionId, request, ct)).Data!;
        }
        catch (Exception ex)
        {
            var error = ApiError.From(ex);
            return new AnswerAckDto(false, error.Code, error.Message, null, null, false);
        }
    }

    private HubConnection Build()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(api.BaseUrl), "/hubs/competition"), o => o.AccessTokenProvider = () => session.GetAccessTokenAsync())
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .AddJsonProtocol(o => o.PayloadSerializerOptions = JsonDefaults.Options)
            .Build();
        hub.On<CompetitionQuestionDto>("QuestionStarted", x => Raise(QuestionStarted, x));
        hub.On<OpponentAnsweredDto>("OpponentAnswered", x => Raise(OpponentAnswered, x));
        hub.On<QuestionResultDto>("QuestionResult", x => Raise(QuestionResult, x));
        hub.On<int>("MatchUpdated", x => Raise(MatchUpdated, x));
        hub.On<int>("MatchEnded", x => Raise(MatchEnded, x));
        hub.On<CompetitionSummaryDto>("ChallengeReceived", x => Raise(ChallengeReceived, x));
        return hub;
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        if (handler is null) return;
        ui.Post(() => handler(this, value));
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
