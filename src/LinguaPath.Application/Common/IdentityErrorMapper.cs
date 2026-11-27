using Microsoft.AspNetCore.Identity;

namespace LinguaPath.Application.Common;

internal static class IdentityErrorMapper
{
    /// <summary>Turns ASP.NET Identity errors into a validation error keyed by the field they belong to.</summary>
    public static Error ToError(this IdentityResult result, string passwordField = "Password")
    {
        var details = result.Errors
            .GroupBy(e => FieldFor(e.Code, passwordField))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return Error.Validation(details);
    }

    private static string FieldFor(string code, string passwordField) =>
        code.StartsWith("Password", StringComparison.Ordinal) ? passwordField
        : code.Contains("Email", StringComparison.Ordinal) || code.Contains("UserName", StringComparison.Ordinal) ? "Email"
        : "General";
}
