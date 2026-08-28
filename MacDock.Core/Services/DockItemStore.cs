using System.Text.Json;
using MacDock.Core.Models;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// Dock 项目持久化：读写 dock-items.json；文件不存在时提供首次运行默认预置。
/// </summary>
public sealed class DockItemStore
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly AtomicJsonFile<List<DockItem>> _file;

    /// <summary>默认预置项目（首次运行）。</summary>
    public static IReadOnlyList<DockItem> DefaultItems { get; } = CreateDefaultItems();

    public DockItemStore() : this(AppPaths.DockItemsFile)
    {
    }

    /// <summary>使用自定义文件路径（便于单元测试）。</summary>
    public DockItemStore(string filePath)
    {
        _filePath = filePath;
        _file = new AtomicJsonFile<List<DockItem>>(filePath, JsonOptions);
    }

    /// <summary>
    /// 读取 Dock 项目；文件不存在时返回默认预置，合法的空数组表示用户已清空 Dock，
    /// 非空配置则按安全自愈规则处理。
    /// </summary>
    public IReadOnlyList<DockItem> Load()
    {
        if (!File.Exists(_filePath))
            return DefaultItems.ToList();

        try
        {
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<DockItem>>(json, JsonOptions);
            if (items is null)
                return DefaultItems.ToList();

            // 空数组是用户明确保存的状态，不能被首次运行默认项或“全部坏死”回退规则覆盖。
            if (items.Count == 0)
                return items;

            return Heal(items);
        }
        catch
        {
            return DefaultItems.ToList();
        }
    }

    /// <summary>
    /// 配置自愈：修掉旧版本写入的坏死条目（死路径且既无 StoreAppName 也无 IconOverride，
    /// 表现为图标隐形）。按 Name 匹配默认项则原位替换，匹配不到则丢弃；
    /// 用户条目与正常条目一律不动；若非空配置中的条目全部被丢弃，则回退到默认预置。
    /// 仅在内存中处理，不回写文件。
    /// </summary>
    private static List<DockItem> Heal(List<DockItem> items)
    {
        var healed = new List<DockItem>(items.Count);

        foreach (var item in items)
        {
            if (!IsDead(item))
            {
                healed.Add(item);
                continue;
            }

            var replacement = DefaultItems.FirstOrDefault(
                d => string.Equals(d.Name, item.Name, StringComparison.OrdinalIgnoreCase));

            if (replacement is not null)
            {
                healed.Add(Clone(replacement));
                Logger.Info("配置自愈：坏死条目「{0}」已替换为默认项", item.Name);
            }
            else
            {
                Logger.Info("配置自愈：坏死条目「{0}」无匹配默认项，已丢弃", item.Name);
            }
        }

        return healed.Count > 0 ? healed : DefaultItems.Select(Clone).ToList();
    }

    /// <summary>坏死判定：路径不可用，且无商店应用名、无内置图标兜底。</summary>
    private static bool IsDead(DockItem item)
        => item.Kind != DockItemKind.Separator
           && (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
           && string.IsNullOrWhiteSpace(item.StoreAppName)
           && string.IsNullOrWhiteSpace(item.IconOverride);

    /// <summary>复制默认项，避免调用方修改静态 DefaultItems 实例。</summary>
    private static DockItem Clone(DockItem source) => new()
    {
        Kind = source.Kind,
        Name = source.Name,
        Path = source.Path,
        IconPath = source.IconPath,
        IconOverride = source.IconOverride,
        StoreAppName = source.StoreAppName,
        Arguments = source.Arguments,
        IsBuiltIn = source.IsBuiltIn,
    };

    /// <summary>保存 Dock 项目到磁盘。</summary>
    public void Save(IEnumerable<DockItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _file.Write(items.ToList());
    }

    // 内置图标（MacDock.UI 资源）兜底用：Win11 计算器/商店版记事本无本地 exe 可提取
    // 真实图标；Edge 缺失时的 URL 回退项无本地文件，也用内置图。其余项走 IconService 真实提取。
    private const string CalculatorIcon = "pack://application:,,,/Assets/Icons/calculator.png";
    private const string NotesIcon = "pack://application:,,,/Assets/Icons/notes.png";
    private const string SafariIcon = "pack://application:,,,/Assets/Icons/safari.png";

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
                Name = "计算器",
                Path = "calculator:",
                IconOverride = CalculatorIcon,
                IsBuiltIn = true,
            },
        };

        // 记事本：System32\notepad.exe 存在则用真实图标；缺失说明是 Win11 商店版，
        // 走 StoreAppResolver（"notepad"→Microsoft.WindowsNotepad）+ 内置图标兜底
        var notepadPath = Path.Combine(sys, "notepad.exe");
        if (File.Exists(notepadPath))
        {
            list.Add(new DockItem { Name = "记事本", Path = notepadPath, IsBuiltIn = true });
        }
        else
        {
            list.Add(new DockItem
            {
                Name = "记事本",
                Path = string.Empty,
                StoreAppName = "notepad",
                IconOverride = NotesIcon,
                IsBuiltIn = true,
            });
        }

        // 浏览器：优先 Edge（ProgramFilesX86 → ProgramFiles → 按用户安装），缺失则回退为 URL
        var edgeX86 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft\Edge\Application\msedge.exe");
        var edgeX64 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Microsoft\Edge\Application\msedge.exe");
        var edgeUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Edge\Application\msedge.exe");

        if (File.Exists(edgeX86))
            list.Add(new DockItem { Name = "浏览器", Path = edgeX86, IsBuiltIn = true });
        else if (File.Exists(edgeX64))
            list.Add(new DockItem { Name = "浏览器", Path = edgeX64, IsBuiltIn = true });
        else if (File.Exists(edgeUser))
            list.Add(new DockItem { Name = "浏览器", Path = edgeUser, IsBuiltIn = true });
        else
            list.Add(new DockItem
            {
                Name = "浏览器",
                Path = "https://www.bing.com",
                IconOverride = SafariIcon, // URL 无本地文件可提取图标，用内置图
                IsBuiltIn = true,
            });

        return list;
    }
}
