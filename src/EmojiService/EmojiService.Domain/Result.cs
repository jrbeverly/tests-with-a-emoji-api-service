namespace EmojiService.Domain;

public sealed record Result<T>
{
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    private Result(T? value, Error? error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error);
}

public sealed record Result
{
    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsFailure => !IsSuccess;

    private Result()
    {
        Error = null;
    }

    private Result(Error error)
    {
        Error = error;
    }

    public static Result Success() => new();

    public static Result Failure(Error error) => new(error);
}
