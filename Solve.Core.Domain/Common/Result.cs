namespace Solve.Core.Domain.Common;

/// <summary>
/// 可预期失败的错误描述。Code 是稳定的机器可读标识（用于日志检索与告警映射），Message 面向人读。
/// </summary>
public sealed record Error(string Code, string Message)
{
    /// <summary>
    /// default(Result&lt;T&gt;) 未经构造时返回的错误，用来暴露"忘记赋值"的编程错误，而不是返回 null
    /// </summary>
    public static readonly Error Uninitialized = new("Result.Uninitialized", "Result 未初始化。");

    public override string ToString() => $"[{Code}] {Message}";
}

/// <summary>
/// 无返回值操作（打开、写入、关闭）的结果。语义同 <see cref="Result{T}"/>。
/// </summary>
public readonly struct Result
{
    private readonly Error? _error;

    private Result(Error? error, bool isSuccess)
    {
        _error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error => IsSuccess
        ? throw new InvalidOperationException("成功的 Result 没有错误。")
        : _error ?? Error.Uninitialized;

    public static Result Success() => new(null, true);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)), false);

    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}

/// <summary>
/// 表示可预期失败（校验失败、非法迁移、通信失败）的返回值；不可预期的编程错误仍然抛出异常。
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// 成功值。失败时访问属于编程错误，直接抛出。
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"失败的 Result 没有值：{Error}");

    /// <summary>
    /// 失败原因。成功时访问属于编程错误，直接抛出。
    /// </summary>
    public Error Error => IsSuccess
        ? throw new InvalidOperationException("成功的 Result 没有错误。")
        : _error ?? Error.Uninitialized;

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({Error})";
}
