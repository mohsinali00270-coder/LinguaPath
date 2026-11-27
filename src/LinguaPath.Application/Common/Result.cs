using LinguaPath.Shared;

namespace LinguaPath.Application.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Unauthorized,
    Forbidden,
    Conflict,
    TooManyRequests,
    Failure
}

/// <summary>A business error. Hosts translate it to HTTP Problem Details (API) or ModelState (admin panel).</summary>
public sealed record Error(string Code, string Message, ErrorType Type, IDictionary<string, string[]>? Details = null)
{
    public static Error Validation(string field, string message) =>
        new(ErrorCodes.Validation, message, ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    public static Error Validation(IDictionary<string, string[]> details) =>
        new(ErrorCodes.Validation, "One or more fields are invalid.", ErrorType.Validation, details);

    public static Error NotFound(string what) => new(ErrorCodes.NotFound, $"{what} was not found.", ErrorType.NotFound);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error TooManyRequests(string code, string message) => new(code, message, ErrorType.TooManyRequests);
    public static Error Failure(string message) => new(ErrorCodes.ServerError, message, ErrorType.Failure);
}

public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(true, null) => _value = value;
    private Result(Error error) : base(false, error) { }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static Result<T> Success(T value) => new(value);
    public new static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);
    public static implicit operator Result<T>(Error error) => new(error);
}
