# MacDock

MacDock 是一款 Windows 桌面增强软件，目标是让 Windows 获得接近原生 macOS 的桌面体验（Dock 栏、顶部菜单栏、任务栏接管、最小化飞入动画、启动台/控制中心/主题）。

当前进度：**M1.5 —— Dock 栏体验修复**。

## 技术栈

- C# 12 / WPF / .NET 8（LTS，TFM `net8.0-windows10.0.22621.0`，内置 WinRT 投影）
- MVVM：CommunityToolkit.Mvvm
- 托盘：H.NotifyIcon.Wpf
- 日志：NLog

## 解决方案结构

```
MacDock.sln
├── MacDock.Core/         # 类库，无 UI：Win32 封装与系统服务
│   ├── Interop/          # 所有 P/Invoke 声明（NativeMethods.cs）
│   ├── Services/         # 图标、快捷方式、进程启动、商店应用解析、自启、窗口定位等
│   └── Models/           # DockItem、AppInfo
├── MacDock.UI/           # WPF 主程序
│   ├── Views/            # DockWindow / SettingsWindow
│   ├── Controls/         # 鱼眼放大面板、图标控件
│   ├── ViewModels/
│   ├── Assets/Icons/     # 内置 macOS 风格图标（finder/notes/calculator/safari）
│   └── Themes/           # 深浅色主题资源字典
├── MacDock.Animations/   # 动画引擎（缓动函数、点击弹跳）
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

运行后屏幕底部居中会出现 Dock：Win11 22H2+ 为 DWM 亚克力毛玻璃（壁纸透过有磨砂感），Win10 降级为半透明渐变。悬停图标有鱼眼放大动画与 Tooltip，点击图标弹跳并启动应用，可拖入 `.lnk` / `.exe` 固定（右键移除）。

数据持久化于 `%AppData%\MacDock\dock-items.json`，日志位于 `%AppData%\MacDock\logs\`。

## 已实现功能（M1 + M1.5）

- 无边框 Dock 主窗口：置顶、点击不抢焦点、不进 Alt+Tab
- 背景：Win11 22H2+ 走 DWM 亚克力（`DWMWA_SYSTEMBACKDROP_TYPE`）+ 系统圆角；Win10（build < 22621）降级为分层窗口半透明渐变
- 鱼眼放大：中心图标放大至 1.6x 并沿弧线上抬，相邻按距离余弦衰减，约 200ms EaseOut；首尾内边距防止放大溢出裁剪
- 图标视觉统一：macOS 风格 squircle 底板（圆角 + 浅色渐变 + 细描边）；四个预置项内置 macOS 风格图标
- Dock 项目管理：默认预置资源管理器/记事本/计算器/浏览器；拖入 `.lnk` / `.exe` 固定（`.lnk` 用快捷方式自身文件名）；右键移除；JSON 持久化
- 点击启动：URI 协议（`calculator:` 等）与商店应用（`shell:AppsFolder\AUMID`）兜底；已运行时激活主窗口到前台而非重复启动；失败托盘气泡提示 + NLog 记录
- 交互反馈：悬停 Tooltip（300ms 延迟）、点击图标弹跳（BounceEase）
- 图标异步加载：先占位图标，后台提取完成后更新
- 单实例：命名 Mutex，重复启动提示气泡后退出
- 开机自启：设置窗口开关，写注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；设置窗口 Owner 置顶 + 淡入
- 托盘图标 + 右键菜单（设置 / 退出）

## 已知限制

- Win11 亚克力要求非分层窗口；Win10 降级路径无模糊效果（仅半透明渐变）
- 托盘图标暂用系统默认图标，后续替换为自定义 `.ico`
- 内置四枚图标为程序生成的风格化图形，后续可替换为精细设计稿
- 亮度（M2）、蓝牙（M5）等特性在部分硬件上不支持，届时降级处理
