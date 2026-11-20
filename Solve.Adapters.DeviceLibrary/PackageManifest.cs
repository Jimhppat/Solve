using System.Text.Json;
using Solve.Core.Domain.Drivers;
using Solve.Device.Sdk;

namespace Solve.Adapters.DeviceLibrary;

/// <summary>
/// 清单中声明的一个驱动。扫描时只读这份声明（不执行代码）就能在设备库页面列出驱动；载入后内核核对代码与声明一致。
/// </summary>
/// <param name="Category">SDK 分类（<see cref="DeviceCategory"/> 的成员名，如 "PowerAndLoad"）</param>
/// <param name="Manufacturer">厂家，可选，未写时为空串</param>
/// <param name="Model">型号，可选，未写时为空串</param>
internal sealed record ManifestDriver(DriverId Id, string Name, DeviceCategory Category, string Manufacturer, string Model);

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
///   "entryType": "Acme.Power.PowerDriverPackage",
///   "drivers": [
///     { "id": "acme.psu-3000", "name": "Acme PSU-3000", "category": "PowerAndLoad", "manufacturer": "Acme", "model": "PSU-3000" }
///   ]
/// }
/// </code>
/// </summary>
/// <param name="Id">包标识，格式同驱动标识（小写字母、数字、'.'、'-'、'_'，最长 64）</param>
/// <param name="EntryAssembly">入口程序集文件名，位于包目录根部，不含路径</param>
/// <param name="EntryType">实现 IDriverPackage 的公开类型全名</param>
/// <param name="Drivers">包内全部驱动的声明，至少一个；必须与代码中 IDriverPackage.Drivers 的驱动一一对应</param>
internal sealed record PackageManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    int SdkVersion,
    string EntryAssembly,
    string EntryType,
    IReadOnlyList<ManifestDriver> Drivers)
{
    public const int MaxTextLength = 64;
    public const int MaxEntryTypeLength = 256;

    /// <summary>
    /// 一个包最多声明的驱动数
    /// </summary>
    public const int MaxDrivers = 64;

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

            var id = Text(root, "id", MaxTextLength, errors, "");
            var name = Text(root, "name", MaxTextLength, errors, "");
            var version = Text(root, "version", MaxTextLength, errors, "");
            var author = Text(root, "author", MaxTextLength, errors, "");
            var entryAssembly = Text(root, "entryAssembly", MaxTextLength * 2, errors, "");
            var entryType = Text(root, "entryType", MaxEntryTypeLength, errors, "");
            var sdkVersion = Integer(root, "sdkVersion", errors);
            var drivers = ParseDrivers(root, errors);

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
                ? new PackageManifest(id!, name!, version!, author!, sdkVersion!.Value, entryAssembly!, entryType!, drivers)
                : null;
        }
    }

    private static List<ManifestDriver> ParseDrivers(JsonElement root, List<string> errors)
    {
        var drivers = new List<ManifestDriver>();
        if (!root.TryGetProperty("drivers", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("缺少数组字段「drivers」：清单必须声明包内的全部驱动。");
            return drivers;
        }

        var count = array.GetArrayLength();
        if (count == 0 || count > MaxDrivers)
        {
            errors.Add($"「drivers」必须声明 1–{MaxDrivers} 个驱动，当前为 {count} 个。");
            return drivers;
        }

        var seen = new HashSet<DriverId>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            var where = $"drivers 第 {index} 项的";
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{where}内容必须是 JSON 对象。");
                continue;
            }

            var before = errors.Count;
            var idText = Text(element, "id", MaxTextLength, errors, where);
            var name = Text(element, "name", MaxTextLength, errors, where);
            var categoryText = Text(element, "category", MaxTextLength, errors, where);
            var manufacturer = OptionalText(element, "manufacturer", errors, where);
            var model = OptionalText(element, "model", errors, where);

            DriverId driverId = default;
            if (idText is not null && !DriverId.TryParse(idText, out driverId))
                errors.Add($"{where}id「{idText}」格式无效。");
            else if (idText is not null && !seen.Add(driverId))
                errors.Add($"{where}id「{idText}」与前面的驱动重复。");

            var category = default(DeviceCategory);
            if (categoryText is not null && !TryParseCategory(categoryText, out category))
            {
                errors.Add($"{where}category「{categoryText}」未定义，只能取：{string.Join("、", Enum.GetNames<DeviceCategory>())}。");
            }

            if (errors.Count == before)
                drivers.Add(new ManifestDriver(driverId, name!, category, manufacturer, model));
        }

        return drivers;
    }

    /// <summary>
    /// 分类只接受 SDK 枚举的成员名（不区分大小写），不接受数字：数字在清单里看不出含义，也容易与将来追加的分类混淆
    /// </summary>
    private static bool TryParseCategory(string text, out DeviceCategory category)
    {
        category = default;
        return text.Length > 0
               && char.IsLetter(text[0])
               && Enum.TryParse(text, ignoreCase: true, out category)
               && Enum.IsDefined(category);
    }

    private static string? Text(JsonElement element, string property, int maxLength, List<string> errors, string where)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{where}缺少文本字段「{property}」。");
            return null;
        }

        var text = value.GetString()!.Trim();
        if (text.Length == 0)
        {
            errors.Add($"{where}字段「{property}」不能为空。");
            return null;
        }

        if (text.Length > maxLength)
        {
            errors.Add($"{where}字段「{property}」不能超过 {maxLength} 个字符。");
            return null;
        }

        return text;
    }

    private static string OptionalText(JsonElement element, string property, List<string> errors, string where)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{where}字段「{property}」必须是文本。");
            return string.Empty;
        }

        var text = value.GetString()!.Trim();
        if (text.Length > MaxTextLength)
        {
            errors.Add($"{where}字段「{property}」不能超过 {MaxTextLength} 个字符。");
            return string.Empty;
        }

        return text;
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
