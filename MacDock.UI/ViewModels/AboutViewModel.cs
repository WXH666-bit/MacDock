using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Services;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 「关于本机」视图模型：窗口先弹出，机器信息在后台线程读取后回填，避免弹窗卡顿。
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private const string Loading = "读取中…";

    /// <summary>CPU 型号。</summary>
    [ObservableProperty]
    private string _processorName = Loading;

    /// <summary>内存总量显示文本（如「16 GB」）。</summary>
    [ObservableProperty]
    private string _memoryText = Loading;

    /// <summary>系统版本。</summary>
    [ObservableProperty]
    private string _operatingSystem = Loading;

    /// <summary>主机名。</summary>
    [ObservableProperty]
    private string _machineName = Loading;

    /// <summary>后台读取机器信息并回填（调用方在 UI 线程 await）。</summary>
    public async Task LoadAsync()
    {
        var info = await Task.Run(SystemInfoService.Read).ConfigureAwait(true);

        ProcessorName = info.ProcessorName;
        MemoryText = info.TotalMemoryGb > 0
            ? $"{info.TotalMemoryGb:0.#} GB"
            : "未知";
        OperatingSystem = info.OperatingSystem;
        MachineName = info.MachineName;
    }
}
