using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 设置文件中保存的日志设置。各项为 null 表示未配置，使用默认值；取值是否合法由读取方校验（文件可能被手工改过）。
/// </summary>
/// <param name="LogDirectory">日志目录；null 表示默认目录</param>
/// <param name="RetentionDays">保存天数</param>
/// <param name="FileSizeLimitMegabytes">单个文件上限（MB）</param>
public sealed record StoredLogSettings(string? LogDirectory, int? RetentionDays = null, int? FileSizeLimitMegabytes = null)
{
    // 必须写参数名：new(null) 会匹配到记录类型的复制构造函数
    public static StoredLogSettings NotConfigured { get; } = new(LogDirectory: null);
}

/// <summary>
/// 日志设置文件（config/log-location.json）：日志目录、保存天数、单个文件上限。
/// </summary>
/// <remarks>
/// <para>文件名沿用只保存目录时的版本，升级后原有设置照常读取；旧文件中没有的项按未配置处理。</para>
/// <para>不放进配置库：日志管线在打开配置库之前创建，配置库打不开时也必须能记录原因。</para>
/// <para>不放进 appsettings.json：那份文件由现场手工改级别并被热监视，程序改写会丢掉其中的注释、还会触发级别重载。</para>
/// </remarks>
public sealed class LogSettingsStore
{
    public const string FileName = "log-location.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 现场手工改成 "RetentionDays": "60" 也能读；写出时仍是数字
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // 中文路径保持原样，现场打开文件能直接看懂
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LogSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// 读取保存的设置。文件不存在时返回成功且各项未配置。
    /// </summary>
    public Result<StoredLogSettings> Load()
    {
        if (!File.Exists(FilePath))
            return Result<StoredLogSettings>.Success(StoredLogSettings.NotConfigured);

        try
        {
            var document = JsonSerializer.Deserialize<StoredLogSettings>(File.ReadAllText(FilePath), JsonOptions);
            if (document is null)
                return Result<StoredLogSettings>.Success(StoredLogSettings.NotConfigured);

            var directory = string.IsNullOrWhiteSpace(document.LogDirectory) ? null : document.LogDirectory.Trim();
            return Result<StoredLogSettings>.Success(document with { LogDirectory = directory });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<StoredLogSettings>.Failure(LogDirectoryErrors.LoadFailed(FilePath, ex.Message));
        }
    }

    /// <summary>
    /// 保存全部设置。先写临时文件再整体替换：写到一半断电也不会留下半个文件。
    /// </summary>
    public Result Save(StoredLogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(LogDirectoryErrors.SaveFailed(FilePath, ex.Message));
        }
    }
}
