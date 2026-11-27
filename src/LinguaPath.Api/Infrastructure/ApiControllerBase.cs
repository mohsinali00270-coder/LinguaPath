using LinguaPath.Application.Common;
using LinguaPath.Shared.Common;
using Microsoft.AspNetCore.Mvc;

namespace LinguaPath.Api.Infrastructure;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Signed-in user id from the "sub" claim.</summary>
    protected Guid CurrentUserId =>
        Guid.TryParse(User.FindFirst("sub")?.Value, out var id)
            ? id
            : throw new UnauthorizedAccessException("No user id in token.");

    protected IActionResult OkData<T>(T data, string? message = null) => Ok(ApiResponse<T>.Ok(data, message));

    protected IActionResult FromResult<T>(Result<T> result, string? message = null) =>
        result.IsSuccess ? OkData(result.Value, message) : ProblemResults.ToResult(HttpContext, result.Error!);

    protected IActionResult FromResult(Result result, string? message = null) =>
        result.IsSuccess ? Ok(ApiResponse.Ok(message)) : ProblemResults.ToResult(HttpContext, result.Error!);
}
