using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Solve.Core.Application.Logging;
using Solve.Core.Domain.Common;

namespace Solve.Desktop.Logging;

/// <summary>
/// 日志目录设置文件（config/log-location.json），只保存一项：日志目录。未配置表示使用默认目录。
/// </summary>
/// <remarks>
/// <para>不放进配置库：日志管线在打开配置库之前创建，配置库打不开时也必须能记录原因。</para>
/// <para>不放进 appsettings.json：那份文件由现场手工改级别并被热监视，程序改写会丢掉其中的注释、还会触发级别重载。</para>
/// </remarks>
public sealed class LogLocationStore
{
    public const string FileName = "log-location.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 中文路径保持原样，现场打开文件能直接看懂
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LogLocationStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// 读取保存的目录。文件不存在或未配置时返回成功且值为 null。
    /// </summary>
    public Result<string?> Load()
    {
        if (!File.Exists(FilePath))
            return Result<string?>.Success(null);

        try
        {
            var document = JsonSerializer.Deserialize<LogLocationDocument>(File.ReadAllText(FilePath), JsonOptions);
            var directory = document?.LogDirectory;
            return Result<string?>.Success(string.IsNullOrWhiteSpace(directory) ? null : directory.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<string?>.Failure(LogDirectoryErrors.LoadFailed(FilePath, ex.Message));
        }
    }

    /// <summary>
    /// 保存目录；null 表示恢复默认。先写临时文件再整体替换：写到一半断电也不会留下半个文件。
    /// </summary>
    public Result Save(string? directory)
    {
        var temp = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new LogLocationDocument(directory), JsonOptions);
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

    private sealed record LogLocationDocument(string? LogDirectory);
}
