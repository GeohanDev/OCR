namespace OcrSystem.Common;

public class Result<T>
{
    public bool IsSuccess { get; private set; }
    public T? Value { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorCode { get; private set; }

    private Result() { }

    public static Result<T> Success(T value) =>
        new() { IsSuccess = true, Value = value };

    public static Result<T> Failure(string error, string? errorCode = null) =>
        new() { IsSuccess = false, Error = error, ErrorCode = errorCode };
}

public class Result
{
    public bool IsSuccess { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorCode { get; private set; }

    private Result() { }

    public static Result Success() => new() { IsSuccess = true };

    public static Result Failure(string error, string? errorCode = null) =>
        new() { IsSuccess = false, Error = error, ErrorCode = errorCode };
}
