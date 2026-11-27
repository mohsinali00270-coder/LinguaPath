using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Application.Features.Ai;
using LinguaPath.Domain.Constants;
using Microsoft.Extensions.Options;

namespace LinguaPath.Infrastructure.Ai;

/// <summary>
/// "Ai" configuration. Provider and model come from Settings (FR-A110) so they can change without a deploy;
/// API keys stay in configuration (user-secrets / Key Vault), never in the database.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Always use the offline generator (tests, demos).</summary>
    public bool ForceOffline { get; set; }
    /// <summary>Use the offline generator when the chosen provider has no API key (handy in development).</summary>
    public bool OfflineWhenNoKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [AiProviders.DeepSeek] = new() { BaseUrl = "https://api.deepseek.com" },
        [AiProviders.OpenAI] = new() { BaseUrl = "https://api.openai.com/v1" }
    };
    public AiRunnerOptions Runner { get; set; } = new();
}

public sealed class AiProviderOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
}

public sealed class AiRunnerOptions
{
    /// <summary>Run the AI job queue in this host. The admin panel enables it; the API does not.</summary>
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 10;
}

internal sealed class AiChatClientFactory(
    IOptions<AiOptions> options,
    ISettingsService settings,
    IHttpClientFactory http,
    OfflineAiChatClient offline) : IAiChatClientFactory
{
    public async Task<(IAiChatClient Client, string Provider, string Model)> CreateAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var provider = (await settings.GetAsync(SettingKeys.AiProvider, ct))?.Trim();
        var model = (await settings.GetAsync(SettingKeys.AiModel, ct))?.Trim();
        if (string.IsNullOrEmpty(provider)) provider = AiProviders.DeepSeek;
        if (string.IsNullOrEmpty(model)) model = "deepseek-chat";

        if (o.ForceOffline || provider.Equals(AiProviders.Offline, StringComparison.OrdinalIgnoreCase))
            return (offline, AiProviders.Offline, "offline-sample");

        if (!o.Providers.TryGetValue(provider, out var p) || string.IsNullOrWhiteSpace(p.BaseUrl))
            throw new AiProviderException($"Unknown AI provider \"{provider}\". Configure Ai:Providers:{provider}:BaseUrl, or set Ai.Provider to DeepSeek, OpenAI or Offline.");
        if (string.IsNullOrWhiteSpace(p.ApiKey))
        {
            if (o.OfflineWhenNoKey) return (offline, AiProviders.Offline, "offline-sample");
            throw new AiProviderException($"No API key for {provider}. Set Ai:Providers:{provider}:ApiKey with user-secrets or Key Vault.");
        }

        var client = http.CreateClient("ai");
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 10, 600));
        return (new OpenAiCompatibleChatClient(client, provider, p.BaseUrl, p.ApiKey!), provider, model);
    }
}

/// <summary>Chat completions for any OpenAI-compatible API: DeepSeek, OpenAI, Azure OpenAI gateways, local servers.</summary>
internal sealed class OpenAiCompatibleChatClient(HttpClient http, string provider, string baseUrl, string apiKey) : IAiChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<AiChatResult> CompleteAsync(AiChatRequest request, CancellationToken ct = default)
    {
        var body = new ChatRequest(
            request.Model ?? "deepseek-chat",
            [new("system", request.System), new("user", request.User)],
            request.Temperature,
            request.MaxTokens,
            request.JsonMode ? new ResponseFormat("json_object") : null);

        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new AiProviderException($"{provider} returned {(int)response.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}");
        }

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, ct)
                     ?? throw new AiProviderException($"{provider} returned an empty response.");
        var content = result.Choices?.FirstOrDefault()?.Message?.Content
                      ?? throw new AiProviderException($"{provider} returned no message.");
        return new AiChatResult(content, provider, result.Model ?? body.Model, result.Usage?.PromptTokens ?? 0, result.Usage?.CompletionTokens ?? 0);
    }

    private sealed record ChatRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private sealed record ChatMessage(string Role, string Content);
    private sealed record ResponseFormat(string Type);

    private sealed class ChatResponse
    {
        public string? Model { get; set; }
        public List<Choice>? Choices { get; set; }
        public UsageInfo? Usage { get; set; }
    }

    private sealed class Choice
    {
        public ChatMessage? Message { get; set; }
    }

    private sealed class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }
}
