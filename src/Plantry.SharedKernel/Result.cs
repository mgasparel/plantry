namespace Plantry.SharedKernel;

/// <summary>Discriminated-union result type: Success or Failure with an Error.</summary>
public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T Value { get; }
    public Error Error { get; }

    private Result(T value) { IsSuccess = true; Value = value; Error = Error.None; }
    private Result(Error error) { IsSuccess = false; Value = default!; Error = error; }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);
}

public sealed class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    private Result() { IsSuccess = true; Error = Error.None; }
    private Result(Error error) { IsSuccess = false; Error = error; }

    public static Result Success() => new();
    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static readonly Error NotFound = new("NotFound", "The requested resource was not found.");
    public static readonly Error Unauthorized = new("Unauthorized", "Access denied.");
    public static readonly Error Conflict = new("Conflict", "A conflict occurred.");

    public static Error Custom(string code, string description) => new(code, description);
}
