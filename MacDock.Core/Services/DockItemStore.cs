using System.Text.Json;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

/// <summary>
/// Dock 项目持久化：读写 dock-items.json，首次运行写入默认预置。
/// </summary>
public sealed class DockItemStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;

    /// <summary>默认预置项目（首次运行）。</summary>
    public static IReadOnlyList<DockItem> DefaultItems { get; } = CreateDefaultItems();

    public DockItemStore() : this(AppPaths.DockItemsFile)
    {
    }

    /// <summary>使用自定义文件路径（便于单元测试）。</summary>
    public DockItemStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>读取 Dock 项目；文件不存在时返回默认预置。</summary>
    public IReadOnlyList<DockItem> Load()
    {
        if (!File.Exists(_filePath))
            return DefaultItems.ToList();

        try
        {
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<DockItem>>(json, JsonOptions);
            return items ?? DefaultItems.ToList();
        }
        catch
        {
            return DefaultItems.ToList();
        }
    }

    /// <summary>保存 Dock 项目到磁盘。</summary>
    public void Save(IEnumerable<DockItem> items)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    // 内置图标（MacDock.UI 资源）：仅商店应用兜底用——Win11 计算器是商店应用，
    // System32\calc.exe 不存在、真实图标提取不到；其余项走 IconService 真实提取。
    private const string CalculatorIcon = "pack://application:,,,/Assets/Icons/calculator.png";

    private static List<DockItem> CreateDefaultItems()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<DockItem>
        {
            new()
            {
                Name = "资源管理器",
                Path = Path.Combine(windows, "explorer.exe"),
                IsBuiltIn = true,
            },
            new()
            {
                Name = "记事本",
                Path = Path.Combine(sys, "notepad.exe"),
                IsBuiltIn = true,
            },
            new()
            {
                Name = "计算器",
                Path = "calculator:",
                IconOverride = CalculatorIcon,
                IsBuiltIn = true,
            },
        };

        // 浏览器：优先 Edge，缺失则回退为 URL（由默认浏览器打开）
        var edgeX86 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft\Edge\Application\msedge.exe");
        var edgeX64 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Microsoft\Edge\Application\msedge.exe");

        if (File.Exists(edgeX86))
            list.Add(new DockItem { Name = "浏览器", Path = edgeX86, IsBuiltIn = true });
        else if (File.Exists(edgeX64))
            list.Add(new DockItem { Name = "浏览器", Path = edgeX64, IsBuiltIn = true });
        else
            list.Add(new DockItem { Name = "浏览器", Path = "https://www.bing.com", IsBuiltIn = true });

        return list;
    }
}
