using LinguaPath.Application.Common.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LinguaPath.Infrastructure.Services;

public class EmailOptions
{
    public const string Section = "Email";

    /// <summary>"Smtp" to send real mail; "Pickup" writes .html files to PickupDirectory (development).</summary>
    public string Mode { get; set; } = "Pickup";
    public string FromName { get; set; } = "LinguaPath";
    public string FromAddress { get; set; } = "no-reply@linguapath.app";
    public string PickupDirectory { get; set; } = "App_Data/emails";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

internal sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _o = options.Value;

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_o.FromName, _o.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_o.Host, _o.Port, _o.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(_o.UserName))
            await client.AuthenticateAsync(_o.UserName, _o.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
        logger.LogInformation("Email '{Subject}' sent to {To}", subject, to);
    }
}

/// <summary>Development sender: writes each email to an .html file and logs the subject (which contains the OTP).</summary>
internal sealed class PickupEmailSender(IOptions<EmailOptions> options, IHostEnvironment env, ILogger<PickupEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var dir = Path.Combine(env.ContentRootPath, options.Value.PickupDirectory);
        Directory.CreateDirectory(dir);
        var safeTo = string.Concat(to.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safeTo}.html");
        await File.WriteAllTextAsync(file, $"<!-- To: {to} | Subject: {subject} -->\n{htmlBody}", ct);
        logger.LogWarning("DEV EMAIL to {To}: {Subject} (saved to {File})", to, subject, file);
    }
}
