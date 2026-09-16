namespace Dispatch.SharedKernel;

/// <summary>A rule that was broken, named so callers can branch on it.</summary>
/// <remarks>
/// The <see cref="Code"/> is the stable part. Message text gets rewritten for tone, translated,
/// or shortened; a caller that switches on message text breaks when someone fixes a typo. HTTP
/// status mapping, retry decisions and client-side handling all key off the code.
/// </remarks>
public sealed record Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// The outcome of an operation that is allowed to fail for business reasons.
/// </summary>
/// <remarks>
/// <para>
/// Used instead of exceptions for <em>rule violations</em>, and only for those. "You cannot
/// complete a work order that was never started" is not exceptional — it is the domain working
/// correctly, and it will happen thousands of times a day from a stale UI. Exceptions stay for
/// things that genuinely should not happen: a null argument, a lost database connection, a bug.
/// </para>
/// <para>
/// The practical difference: a <c>Result</c> is in the method signature, so a caller cannot
/// forget it exists. A thrown <c>InvalidOperationException</c> is invisible until production.
/// </para>
/// </remarks>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        // A failed result with no error, or a successful one carrying an error, means the caller
        // will read a field that says nothing. Better to fail loudly at construction than to let
        // a "successful" operation return an error message nobody looks at.
        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    /// <summary>Stops at the first failure. Used to check a batch of preconditions in order.</summary>
    public static Result FirstFailureOr(params Result[] results)
    {
        foreach (var result in results)
        {
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Success();
    }
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error) => _value = value;

    /// <summary>The value. Throws if the result is a failure.</summary>
    /// <remarks>
    /// Deliberately throws rather than returning <c>default</c>. Reading the value of a failed
    /// result is a bug in the caller, and silently handing back <c>null</c> moves the crash to
    /// somewhere with no useful stack trace.
    /// </remarks>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error}).");

    public static implicit operator Result<TValue>(TValue value) => Success(value);
}
