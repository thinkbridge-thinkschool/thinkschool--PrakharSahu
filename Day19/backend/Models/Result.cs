namespace QuotesApi.Models;

public class Result<T>
{
    public T? Value { get; }
    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;

    private Result(T value) => Value = value;
    private Result(DomainError error) => Error = error;

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(DomainError error) => new(error);
}
