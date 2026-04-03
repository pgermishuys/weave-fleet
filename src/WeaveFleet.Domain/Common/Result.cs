namespace WeaveFleet.Domain.Common;

/// <summary>
/// Discriminated union representing either a successful value or a domain error.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;
    private readonly FleetError? _error;

    internal Result(T value)
    {
        _value = value;
        _error = null;
        IsSuccess = true;
    }

    internal Result(FleetError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value of a failed Result: {_error}");

    public FleetError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Cannot access Error of a successful Result.");

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<FleetError, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(_error!);

    public override string ToString() =>
        IsSuccess ? $"Success({_value})" : $"Failure({_error})";

    public static implicit operator Result<T>(T value) => new(value);
    public static implicit operator Result<T>(FleetError error) => new(error);
}

/// <summary>
/// Non-generic factory for creating <see cref="Result{T}"/> instances (avoids CA1000).
/// </summary>
public static class Result
{
    public static Result<T> Success<T>(T value) => new(value);
    public static Result<T> Failure<T>(FleetError error) => new(error);
}
