using System.Diagnostics;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

/// <summary>
/// 进程启动服务：Process.Start + 同一程序不重复启动。
/// </summary>
public static class ProcessLauncher
{
    /// <summary>启动 Dock 项目对应的应用。</summary>
    public static void Launch(DockItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
            return;

        var path = item.Path;

        // URL：直接用默认浏览器打开，不做重复检测
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(path);

        // explorer.exe 特殊处理：始终打开新窗口（explorer 进程始终存在）
        if (string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        // 同一程序不重复启动
        if (Process.GetProcessesByName(exeName).Length > 0)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = item.Arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        });
    }
}
