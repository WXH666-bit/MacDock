# MacDock

MacDock 是一款 Windows 桌面增强软件，目标是让 Windows 获得接近原生 macOS 的桌面体验（Dock 栏、顶部菜单栏、任务栏接管、最小化飞入动画、启动台/控制中心/主题）。

当前进度：**M1 —— Dock 栏骨架**。

## 技术栈

- C# 12 / WPF / .NET 8（LTS）
- MVVM：CommunityToolkit.Mvvm
- 托盘：H.NotifyIcon.Wpf
- 日志：NLog

## 解决方案结构

```
MacDock.sln
├── MacDock.Core/         # 类库，无 UI：Win32 封装与系统服务
│   ├── Interop/          # 所有 P/Invoke 声明（NativeMethods.cs）
│   ├── Services/         # 图标、快捷方式、进程启动、自启、窗口定位等
│   └── Models/           # DockItem、AppInfo
├── MacDock.UI/           # WPF 主程序
│   ├── Views/            # DockWindow / SettingsWindow
│   ├── Controls/         # 鱼眼放大面板、图标控件
│   ├── ViewModels/
│   └── Themes/           # 深浅色主题资源字典
├── MacDock.Animations/   # 动画引擎（缓动函数，M4 复用）
└── MacDock.Tests/        # Core 层单元测试
```

## 环境要求

- Visual Studio 2022（或仅 .NET 8 SDK + 任意编辑器）
- .NET 8 SDK

## 编译与运行

```bash
# 编译
dotnet build MacDock.sln

# 运行单元测试
dotnet test MacDock.Tests

# 运行
dotnet run --project MacDock.UI
```

运行后屏幕底部居中会出现半透明毛玻璃 Dock，悬停图标有鱼眼放大动画，点击图标启动应用，可拖入 `.lnk` / `.exe` 固定（右键移除）。

数据持久化于 `%AppData%\MacDock\dock-items.json`，日志位于 `%AppData%\MacDock\logs\`。

## 已实现功能（M1）

- 无边框透明 Dock 主窗口：置顶、点击不抢焦点、不进 Alt+Tab
- 鱼眼放大：中心图标放大至 1.6x，相邻按距离余弦衰减，约 200ms EaseOut
- Dock 项目管理：默认预置资源管理器/记事本/计算器/浏览器；拖入 `.lnk` / `.exe` 固定；右键移除；JSON 持久化
- 点击启动：Process.Start，同一程序不重复启动
- 开机自启：设置窗口开关，写注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- 托盘图标 + 右键菜单（设置 / 退出）

## 已知限制

- M1 的毛玻璃为分层窗口半透明自绘（兼容 Win10/11）；Win11 的 DWM 亚克力（`SYSTEMBACKDROP`）将在 M2 菜单栏接入
- 托盘图标暂用系统默认图标，后续替换为自定义 `.ico`
- 亮度（M2）、蓝牙（M5）等特性在部分硬件上不支持，届时降级处理
