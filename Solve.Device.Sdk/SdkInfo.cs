namespace Solve.Device.Sdk;

/// <summary>
/// SDK 版本。驱动包清单 package.json 的 sdkVersion 必须等于 <see cref="MajorVersion"/>，否则设备库拒绝加载。
/// </summary>
public static class SdkInfo
{
    /// <summary>
    /// 契约主版本。只增不减；接口或记录出现不兼容修改（删除成员、改签名、改语义）时加一。
    /// </summary>
    public const int MajorVersion = 1;

    /// <summary>
    /// 驱动包清单文件名，位于每个驱动包目录的根部
    /// </summary>
    public const string ManifestFileName = "package.json";
}
