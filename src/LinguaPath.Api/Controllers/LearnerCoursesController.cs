using LinguaPath.Api.Infrastructure;
using LinguaPath.Application.Features.Learning;
using LinguaPath.Domain.Constants;
using LinguaPath.Shared.Common;
using LinguaPath.Shared.Contracts.Learning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Controllers;

/// <summary>Onboarding, the learner's courses, roadmap and plan (FR-L11 .. FR-L31, FR-L120, FR-L121).</summary>
[Route("api/v1/learner-courses")]
[Authorize(Policy = Policies.LearnerOnly)]
public sealed class LearnerCoursesController(ILearnerCourseService courses) : ApiControllerBase
{
    /// <summary>Check a plan before enrolling: does the goal fit the duration and daily time? (FR-L15)</summary>
    [HttpPost("feasibility")]
    [ProducesResponseType<ApiResponse<FeasibilityResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Feasibility(LearningPlanRequest request, CancellationToken ct) =>
        FromResult(await courses.PreviewAsync(CurrentUserId, request, ct));

    /// <summary>Enrol and build the personal roadmap (FR-L16).</summary>
    [HttpPost]
    [ProducesResponseType<ApiResponse<EnrolResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Enrol(LearningPlanRequest request, CancellationToken ct) =>
        FromResult(await courses.EnrolAsync(CurrentUserId, request, ct), "Your roadmap is ready.");

    [HttpGet]
    [ProducesResponseType<ApiResponse<IReadOnlyList<LearnerCourseDto>>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => OkData(await courses.ListAsync(CurrentUserId, ct));

    [HttpGet("{id:int}")]
    [ProducesResponseType<ApiResponse<LearnerCourseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await courses.GetAsync(CurrentUserId, id, ct));

    /// <summary>Change goal, duration, daily time or study days; the roadmap is re-paced, progress kept (FR-L120).</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<ApiResponse<EnrolResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, UpdateLearningPlanRequest request, CancellationToken ct) =>
        FromResult(await courses.UpdatePlanAsync(CurrentUserId, id, request, ct), "Plan updated.");

    /// <summary>Home screen: today's lectures, progress, days left, course switcher (FR-L20 .. FR-L24).</summary>
    [HttpGet("{id:int}/today")]
    [ProducesResponseType<ApiResponse<TodayDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Today(int id, CancellationToken ct) => FromResult(await courses.TodayAsync(CurrentUserId, id, ct));

    /// <summary>Modules and lectures with their states (FR-L30, FR-L31).</summary>
    [HttpGet("{id:int}/roadmap")]
    [ProducesResponseType<ApiResponse<RoadmapDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Roadmap(int id, CancellationToken ct) => FromResult(await courses.RoadmapAsync(CurrentUserId, id, ct));

    /// <summary>Vacation mode, up to 30 days per course; deadlines move by the paused days (FR-L121, BR-11).</summary>
    [HttpPost("{id:int}/pause")]
    [ProducesResponseType<ApiResponse<LearnerCourseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Pause(int id, PauseCourseRequest request, CancellationToken ct) =>
        FromResult(await courses.PauseAsync(CurrentUserId, id, request, ct), "Course paused.");

    [HttpPost("{id:int}/resume")]
    [ProducesResponseType<ApiResponse<LearnerCourseDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(int id, CancellationToken ct) =>
        FromResult(await courses.ResumeAsync(CurrentUserId, id, ct), "Welcome back! Your plan has moved by the days you were away.");
}
