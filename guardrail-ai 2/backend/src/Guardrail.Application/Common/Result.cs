using Guardrail.Domain.Common;

namespace Guardrail.Application.Common;

/// <summary>Use-case outcome: success value or a stable <see cref="DomainError"/> (ERROR_HANDLING.md §3).</summary>
public readonly record struct Result<T>
{
    private Result(T? value, DomainError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(DomainError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(DomainError error) => Failure(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) => IsSuccess ? Result<TOut>.Success(map(Value!)) : Result<TOut>.Failure(Error!);
}

public sealed record Unit
{
    public static readonly Unit Value = new();
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record PageRequest(int Page = 1, int PageSize = 20)
{
    public int SafePage => Math.Max(Page, 1);

    public int SafePageSize => Math.Clamp(PageSize, 1, 100);

    public int Skip => (SafePage - 1) * SafePageSize;
}
