using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Catalog;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Contracts.Learning;
using LinguaPath.Shared;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

[Route("api/v1/languages")]
public sealed class LanguagesController(ILanguageCatalogService catalog, ICourseCatalogService courses) : ApiControllerBase
{
    /// <summary>Published courses of a published language (onboarding).</summary>
    [HttpGet("{id:int}/courses")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<CourseDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Courses(int id, CancellationToken ct) => FromResult(await courses.GetCoursesAsync(id, ct));

    /// <summary>"Request this language" for one that is not available yet (FR-L10).</summary>
    [HttpPost("requests")]
    [Authorize(Policy = Policies.LearnerOnly)]
    public async Task<IActionResult> RequestLanguage(CreateLanguageRequest request, CancellationToken ct) =>
        FromResult(await courses.RequestLanguageAsync(CurrentUserId, request, ct), "Thanks! We'll let you know when it's ready.");

    /// <summary>
    /// Published and "Coming soon" languages (FR-L10). Pass all=true for every language (native-language picker).
    /// Anonymous so sign-up can use it.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["all"])]
    [ProducesResponseType<MyApiResponse<IReadOnlyList<LanguageDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] bool all, CancellationToken ct) => OkData(await catalog.GetPublicAsync(all, ct));
}

[Route("api/v1/devices")]
[Authorize]
public sealed class DevicesController(IDeviceService devices) : ApiControllerBase
{
    /// <summary>Register or update this device and its push token.</summary>
    [HttpPost]
    public async Task<IActionResult> Register(RegisterDeviceRequest request, CancellationToken ct) =>
        FromResult(await devices.RegisterAsync(CurrentUserId, request, ct));
}

[Route("api/v1/app")]
public sealed class AppController(IAppVersionService versions) : ApiControllerBase
{
    /// <summary>Is this app version still supported? (FR-A112)</summary>
    [HttpGet("version-check")]
    [AllowAnonymous]
    [ProducesResponseType<MyApiResponse<VersionCheckResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VersionCheck([FromQuery] DevicePlatform platform, [FromQuery] string? version, CancellationToken ct) =>
        OkData(await versions.CheckAsync(platform, version, ct));
}
