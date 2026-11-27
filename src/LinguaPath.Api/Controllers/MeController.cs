using System.Text.Json;
using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Profile;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace LinguaPath.Api.Controllers;

/// <summary>The signed-in learner's profile and data rights (FR-L05, FR-L06).</summary>
[Route("api/v1/me")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class MeController(IProfileService profiles, IMemoryCache cache) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<ApiResponse<UserProfileDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        FromResult(await profiles.GetAsync(CurrentUserId, ct));

    [HttpPut]
    [ProducesResponseType<ApiResponse<UserProfileDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(UpdateProfileRequest request, CancellationToken ct) =>
        FromResult(await profiles.UpdateAsync(CurrentUserId, request, ct), "Profile saved.");

    /// <summary>Upload a profile photo (JPG, PNG or WEBP, max 10 MB).</summary>
    [HttpPost("photo")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ApiResponse<PhotoUploadResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadPhoto(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return FromResult(await profiles.UploadPhotoAsync(CurrentUserId, stream, file.FileName, file.ContentType, file.Length, ct),
            "Photo updated.");
    }

    /// <summary>Change password, or set one for Google/Apple-only accounts.</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var result = await profiles.ChangePasswordAsync(CurrentUserId, request, ct);
        SecurityStampCheck.Invalidate(cache, CurrentUserId);
        return FromResult(result, "Password changed.");
    }

    /// <summary>Delete my account: personal data is anonymised immediately (FR-L06, NFR-08).</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete([FromBody] DeleteAccountRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var result = await profiles.DeleteAccountAsync(userId, request, ct);
        SecurityStampCheck.Invalidate(cache, userId);
        return FromResult(result, "Your account has been deleted.");
    }

    /// <summary>Download my data as a JSON file (FR-L06).</summary>
    [HttpGet("export")]
    [Produces("application/json")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var result = await profiles.ExportAsync(CurrentUserId, ct);
        if (result.IsFailure) return FromResult(result);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(bytes, "application/json", $"linguapath-my-data-{DateTime.UtcNow:yyyyMMdd}.json");
    }
}
