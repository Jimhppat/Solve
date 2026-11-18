using System.Text.Json;
using System.Text.Json.Serialization;
using Solve.Core.Domain.Buses;

namespace Solve.Adapters.Persistence.Buses;

/// <summary>
/// BusId 在 JSON 中直接序列化为 GUID 字符串。
/// </summary>
/// <remarks>
/// 领域类型不再带 [JsonConverter] 特性，使用方必须显式注册：
/// <c>options.Converters.Add(new BusIdJsonConverter());</c>
/// 否则 BusId 会被序列化为 {"Value":"..."}。
/// </remarks>
public sealed class BusIdJsonConverter : JsonConverter<BusId>
{
    public override BusId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 配置文件是不可信输入：类型错误、格式错误、空 GUID 一律转为 JsonException，由调用方统一处理
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var guid))
            throw new JsonException("BusId 必须是 GUID 字符串。");

        if (guid == Guid.Empty)
            throw new JsonException("BusId 不能为空 GUID。");

        return new BusId(guid);
    }

    public override void Write(Utf8JsonWriter writer, BusId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
