using System.Text.Json;
using Solve.Core.Domain.Drivers;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 驱动包清单 package.json（已校验）。示例：
/// <code>
/// {
///   "id": "acme.power",
///   "name": "Acme 程控电源",
///   "version": "1.0.0",
///   "author": "Acme",
///   "sdkVersion": 1,
///   "entryAssembly": "Acme.Power.dll",
///   "entryType": "Acme.Power.PowerDriverPackage"
/// }
/// </code>
/// </summary>
/// <param name="Id">包标识，格式同驱动标识（小写字母、数字、'.'、'-'、'_'，最长 64）</param>
/// <param name="EntryAssembly">入口程序集文件名，位于包目录根部，不含路径</param>
/// <param name="EntryType">实现 IDriverPackage 的公开类型全名</param>
internal sealed record PackageManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    int SdkVersion,
    string EntryAssembly,
    string EntryType)
{
    public const int MaxTextLength = 64;
    public const int MaxEntryTypeLength = 256;

    /// <summary>
    /// 解析并校验清单。失败时返回全部问题（一次说清，方便驱动作者一次改好）。
    /// </summary>
    public static PackageManifest? TryParse(ReadOnlySpan<byte> json, out IReadOnlyList<string> problems)
    {
        var errors = new List<string>();
        problems = errors;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            errors.Add($"清单不是有效的 JSON：{ex.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("清单的根必须是 JSON 对象。");
                return null;
            }

            var id = Text(root, "id", MaxTextLength, errors);
            var name = Text(root, "name", MaxTextLength, errors);
            var version = Text(root, "version", MaxTextLength, errors);
            var author = Text(root, "author", MaxTextLength, errors);
            var entryAssembly = Text(root, "entryAssembly", MaxTextLength * 2, errors);
            var entryType = Text(root, "entryType", MaxEntryTypeLength, errors);
            var sdkVersion = Integer(root, "sdkVersion", errors);

            if (id is not null && !DriverId.TryParse(id, out _))
                errors.Add($"id「{id}」格式无效：只能含小写字母、数字、'.'、'-'、'_'，最长 {DriverId.MaxLength} 个字符。");

            // 入口程序集只能是包目录根部的文件名，不能借路径跳出包目录
            if (entryAssembly is not null
                && (entryAssembly.IndexOfAny(['/', '\\', ':']) >= 0
                    || entryAssembly.Contains("..", StringComparison.Ordinal)
                    || !entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"entryAssembly「{entryAssembly}」必须是包目录根部的 .dll 文件名，不能含路径。");
            }

            return errors.Count == 0
                ? new PackageManifest(id!, name!, version!, author!, sdkVersion!.Value, entryAssembly!, entryType!)
                : null;
        }
    }

    private static string? Text(JsonElement root, string property, int maxLength, List<string> errors)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"缺少文本字段「{property}」。");
            return null;
        }

        var value = element.GetString()!.Trim();
        if (value.Length == 0)
        {
            errors.Add($"字段「{property}」不能为空。");
            return null;
        }

        if (value.Length > maxLength)
        {
            errors.Add($"字段「{property}」不能超过 {maxLength} 个字符。");
            return null;
        }

        return value;
    }

    private static int? Integer(JsonElement root, string property, List<string> errors)
    {
        if (root.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value))
        {
            return value;
        }

        errors.Add($"缺少整数字段「{property}」。");
        return null;
    }
}
