namespace PiSharp.Abstractions;

/// <summary>
/// A fallible operation result that avoids exceptions for expected failures.
/// </summary>
public readonly struct Result<TValue, TError>
{
    private readonly TValue? _value;
    private readonly TError? _error;

    public Result(TValue value)
    {
        _value = value;
        _error = default;
        IsOk = true;
    }

    public Result(TError error)
    {
        _value = default;
        _error = error;
        IsOk = false;
    }

    public bool IsOk { get; }
    public bool IsErr => !IsOk;

    public TValue Value => IsOk ? _value! : throw new InvalidOperationException("Accessed Value on an Err result.");
    public TError Error => !IsOk ? _error! : throw new InvalidOperationException("Accessed Error on an Ok result.");

    public static Result<TValue, TError> Ok(TValue value) => new(value);
    public static Result<TValue, TError> Err(TError error) => new(error);

    public bool TryUnwrap(out TValue value, out TError error)
    {
        if (IsOk)
        {
            value = _value!;
            error = default!;
            return true;
        }

        value = default!;
        error = _error!;
        return false;
    }

    public TValue GetOrThrow(Func<TError, Exception> exceptionFactory)
    {
        if (IsOk)
        {
            return _value!;
        }

        throw exceptionFactory(_error!);
    }
}

/// <summary>
/// Unit value for operations that can fail but do not return a meaningful payload.
/// </summary>
public readonly record struct Unit
{
    public static Unit Value { get; } = new();
}

/// <summary>
/// Non-generic factory helpers for <see cref="Result{TValue, TError}"/>.
/// </summary>
public static class Result
{
    public static Result<TValue, TError> Ok<TValue, TError>(TValue value) => Result<TValue, TError>.Ok(value);

    public static Result<TValue, TError> Err<TValue, TError>(TError error) => Result<TValue, TError>.Err(error);
}
