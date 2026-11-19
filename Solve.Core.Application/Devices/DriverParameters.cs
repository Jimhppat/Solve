using Solve.Core.Domain.Common;
using Solve.Core.Domain.Devices;
using Solve.Device.Sdk;

namespace Solve.Core.Application.Devices;

/// <summary>
/// 按驱动声明处理模块参数：保存前校验并精简（<see cref="Normalize"/>），绑定时补全默认值（<see cref="Resolve"/>）。
/// </summary>
/// <remarks>
/// 存储只保留与默认值不同的参数：驱动新版本调整默认值后，没有改过该参数的模块自动采用新默认值。
/// </remarks>
public static class DriverParameters
{
    /// <summary>
    /// 校验操作员填写的值并规范化；只允许驱动声明过的参数，等于默认值的不保存
    /// </summary>
    public static Result<ParameterValues> Normalize(DriverInfo driver, ParameterValues values)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(values);

        var declared = driver.Parameters.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var problems = new List<string>();
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, raw) in values)
        {
            if (!declared.TryGetValue(key, out var parameter))
            {
                problems.Add($"{driver.Name}没有参数 {key}。");
                continue;
            }

            var normalized = parameter.Normalize(raw, out var error);
            if (normalized is null)
            {
                problems.Add(error!);
                continue;
            }

            if (!string.Equals(normalized, parameter.Normalize(parameter.DefaultValue, out _), StringComparison.Ordinal))
                kept[key] = normalized;
        }

        return problems.Count == 0
            ? Result<ParameterValues>.Success(new ParameterValues(kept))
            : Result<ParameterValues>.Failure(DeviceErrors.InvalidField("Parameters", string.Join("；", problems)));
    }

    /// <summary>
    /// 绑定时得到驱动会话用的完整参数：每个声明的参数都有值（填写值优先，否则默认值）。
    /// 已保存的值不再合法（如驱动升级后收窄了范围）时失败，模块不绑定，等待操作员修正。
    /// 驱动已不再声明的参数被忽略，不阻止绑定。
    /// </summary>
    public static Result<DeviceParameters> Resolve(DriverInfo driver, ParameterValues values)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(values);

        var problems = new List<string>();
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in driver.Parameters)
        {
            var raw = values.TryGetValue(parameter.Key, out var stored) ? stored : parameter.DefaultValue;
            var normalized = parameter.Normalize(raw, out var error);
            if (normalized is null)
                problems.Add(error!);
            else
                resolved[parameter.Key] = normalized;
        }

        return problems.Count == 0
            ? Result<DeviceParameters>.Success(new DeviceParameters(resolved))
            : Result<DeviceParameters>.Failure(DeviceErrors.InvalidField("Parameters", string.Join("；", problems)));
    }
}
