namespace ManufacturingAI.Core.Common;

public record Result<T>(bool Success, T? Value = default, string? Error = null)
{
    public static Result<T> Ok(T value) => new(true, value);
    public static Result<T> Fail(string error) => new(false, Error: error);
}

public record Result(bool Success, string? Error = null)
{
    public static Result Ok() => new(true);
    public static Result Fail(string error) => new(false, error);
}
