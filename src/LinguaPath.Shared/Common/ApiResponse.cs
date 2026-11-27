namespace LinguaPath.Shared.Common;

/// <summary>
/// Success envelope used by every API endpoint (SRS 8.3): { success, data, message, errors }.
/// Failures are returned as RFC 7807 Problem Details that also carry "success": false and "errors".
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; } = true;
    public T? Data { get; set; }
    public string? Message { get; set; }
    public IDictionary<string, string[]>? Errors { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null) => new() { Data = data, Message = message };
}

/// <summary>Envelope for endpoints that return no data.</summary>
public class ApiResponse : ApiResponse<object?>
{
    public static ApiResponse Ok(string? message = null) => new() { Message = message };
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Shape of a Problem Details error body, for clients that need to read it.</summary>
public class ApiProblem
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int? Status { get; set; }
    public string? Detail { get; set; }
    public string? Code { get; set; }
    public bool Success { get; set; }
    public IDictionary<string, string[]>? Errors { get; set; }
    public string? TraceId { get; set; }
}
