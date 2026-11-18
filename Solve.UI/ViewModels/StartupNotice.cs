using System.Collections.Generic;

namespace Solve.UI.ViewModels;

/// <summary>
/// 启动阶段需要操作员知道的问题（配置无效、注册失败、启动失败），由组合根根据启动报告生成。
/// </summary>
/// <param name="Title">一句话概述</param>
/// <param name="Details">逐条说明</param>
/// <param name="IsError">true = 总线系统未能启动（危险色）；false = 部分配置未加载（警告色）</param>
public sealed record StartupNotice(string Title, IReadOnlyList<string> Details, bool IsError);
