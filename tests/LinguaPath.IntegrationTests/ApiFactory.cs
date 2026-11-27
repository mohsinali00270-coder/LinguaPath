using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinguaPath.Application.Common.Interfaces;
using LinguaPath.Shared.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaPath.IntegrationTests;

/// <summary>
/// Runs the real API in memory against a private in-memory SQLite database.
/// Emails are captured so tests can read the one-time codes.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keepAlive;
    public CapturingEmailSender Emails { get; } = new();

    public static readonly JsonSerializerOptions Json = CreateJson();

    public ApiFactory()
    {
        var connectionString = $"Data Source=lp-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        // The in-memory database lives as long as one connection stays open.
        _keepAlive = new SqliteConnection(connectionString);
        _keepAlive.Open();

        // Environment variables are read by WebApplication.CreateBuilder before Program.cs uses configuration.
        Environment.SetEnvironmentVariable("Database__Provider", "Sqlite");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-signing-key-0123456789abcdef");
        Environment.SetEnvironmentVariable("RateLimiting__AuthPerMinute", "10000");
        Environment.SetEnvironmentVariable("RateLimiting__GlobalPerMinute", "10000");
        // AI calls go to the offline generator: deterministic, free and no network.
        Environment.SetEnvironmentVariable("Ai__ForceOffline", "true");
        Environment.SetEnvironmentVariable("LanguageServices__ForceOffline", "true");
        // Competitions start at once and move straight to the next question, so tests don't wait on the clock.
        Environment.SetEnvironmentVariable("Competitions__CountdownSeconds", "0");
        Environment.SetEnvironmentVariable("Competitions__ResultPauseSeconds", "0");
        Environment.SetEnvironmentVariable("Competitions__TournamentBreakSeconds", "0");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Emails);
            services.AddScoped<IEmailSender>(sp => sp.GetRequiredService<CapturingEmailSender>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _keepAlive.Dispose();
    }

    private static JsonSerializerOptions CreateJson()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<(string To, string Subject, string Html)> _sent = new();

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        _sent.Enqueue((to, subject, htmlBody));
        return Task.CompletedTask;
    }

    /// <summary>Subjects of every email sent to this address, oldest first.</summary>
    public IReadOnlyList<string> SubjectsFor(string email) =>
        _sent.Where(e => e.To.Equals(email, StringComparison.OrdinalIgnoreCase)).Select(e => e.Subject).ToList();

    /// <summary>The most recent 6-digit code sent to this address.</summary>
    public string LatestCodeFor(string email)
    {
        var last = _sent.Where(e => e.To.Equals(email, StringComparison.OrdinalIgnoreCase)).LastOrDefault();
        var match = Regex.Match(last.Subject ?? string.Empty, @"\b(\d{6})\b");
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException($"No code was emailed to {email}.");
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadDataAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<MyApiResponse<T>>(ApiFactory.Json);
        return body!.Data!;
    }

    public static async Task<ApiProblem> ReadProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiProblem>(ApiFactory.Json))!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiFactory.Json);

    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
