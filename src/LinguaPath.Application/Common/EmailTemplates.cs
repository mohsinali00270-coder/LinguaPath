using System.Net;

namespace LinguaPath.Application.Common;

/// <summary>Plain, inline-styled HTML emails. Editable templates move to the admin panel in a later phase.</summary>
internal static class EmailTemplates
{
    public static (string Subject, string Html) VerifyEmail(string appName, string name, string code, int minutes) =>
        ($"{appName}: your verification code is {code}",
         Wrap(appName, $"""
            <p>Hi {Enc(name)},</p>
            <p>Welcome to {Enc(appName)}! Enter this code in the app to verify your email:</p>
            {CodeBlock(code)}
            <p>The code expires in {minutes} minutes. If you did not sign up, you can ignore this email.</p>
            """));

    public static (string Subject, string Html) ResetPassword(string appName, string name, string code, int minutes) =>
        ($"{appName}: password reset code {code}",
         Wrap(appName, $"""
            <p>Hi {Enc(name)},</p>
            <p>We received a request to reset your password. Enter this code in the app:</p>
            {CodeBlock(code)}
            <p>The code expires in {minutes} minutes. If you did not ask for this, your password stays unchanged.</p>
            """));

    public static (string Subject, string Html) AdminAccountCreated(string appName, string name, string loginUrl) =>
        ($"{appName}: your admin account",
         Wrap(appName, $"""
            <p>Hi {Enc(name)},</p>
            <p>An admin account was created for you. Sign in at <a href="{Enc(loginUrl)}">{Enc(loginUrl)}</a>
            with the temporary password your Super Admin gave you, then change it from <b>My account</b>.</p>
            """));

    private static string CodeBlock(string code) =>
        $"""<p style="font-size:28px;letter-spacing:8px;font-weight:bold;margin:24px 0">{Enc(code)}</p>""";

    private static string Wrap(string appName, string body) => $"""
        <div style="font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:auto;color:#1f2937">
          <h2 style="color:#4f46e5">{Enc(appName)}</h2>
          {body}
          <hr style="border:none;border-top:1px solid #e5e7eb;margin-top:32px" />
          <p style="font-size:12px;color:#6b7280">This is an automated message, please do not reply.</p>
        </div>
        """;

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
