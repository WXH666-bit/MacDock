# MacDock 安全任务栏接管设计

## 背景与目标

当前未提交的任务栏实现会修改 `StuckRects3`、调用 `ABM_SETSTATE`，并按 `explorer.exe` / `ApplicationFrameHost.exe` 批量隐藏窗口。它没有保存完整原状态，也没有可跨进程执行的崩溃恢复，因此可能误隐藏普通窗口、覆盖用户设置或在异常退出后留下隐藏任务栏。

本设计只处理第一批高风险修复：建立一个默认关闭、精确作用于主屏任务栏、可在正常退出和主进程异常退出时回滚的任务栏接管机制。窗口运行状态、启动切换、持久化、UI 和清理问题在后续独立批次处理。

## 已批准的产品决策

- 不创建新 Git 分支，直接保留当前 `main` 工作树上的既有未提交修改。
- 子 agent 使用 Luna、`max` 推理、标准服务；子 agent 负责分任务实现，主 agent 审查每个 diff 并独立验证。
- 任务栏接管默认关闭，只能由设置页显式开启。
- 第一版只接管主显示器的 `Shell_TrayWnd`，不触碰副屏 `Shell_SecondaryTrayWnd`。
- 不写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3`。
- 不调用 `ABM_SETSTATE` 改变系统自动隐藏或置顶偏好。
- 不按进程枚举并隐藏 `explorer.exe` 或 `ApplicationFrameHost.exe` 的普通窗口。
- 增加一个非提权、非服务、非自启动的最小 watchdog 子进程，用于主进程异常退出后的任务栏恢复。
- 默认自动化测试不得操作真实 Explorer、注册表或任务栏；真实 Shell 集成测试只能在显式启用的一次性 Windows VM 中运行。
- 崩溃恢复优先：文件 journal 与 `ShowWindow` 无法组成原子事务，因此租约在调用前持久化 `HidePending`。租约活动期间由 MacDock 拥有该精确主屏任务栏窗口的可见性；无法区分的并发外部隐藏请求可能在释放时被恢复为租约前的可见状态。用户已于 2026-08-22 明确批准此高风险语义。

## 方案概览

任务栏接管由一个显式租约管理：

```text
读取设置（默认关闭）
  -> 创建并显示 DockWindow
  -> 设置开启时启动 watchdog 并等待 ready
  -> 精确发现主屏 Shell_TrayWnd
  -> 捕获窗口原状态
  -> 原子写入 lease journal
  -> 隐藏已记录的任务栏窗口
  -> 运行期间响应 TaskbarCreated / DisplayChange 并串行协调
  -> 正常退出时停止协调、恢复快照、删除 journal、通知 watchdog 退出
  -> 异常退出时 watchdog 读取 journal、恢复快照并删除 journal
```

任何前置步骤失败都采用失败关闭：保持 Windows 任务栏可见、记录错误并继续显示 MacDock，而不是升级到注册表或宽泛窗口枚举。

## 组件边界

### `TaskbarLease`

Core 层状态机，状态为：

```text
Released -> Acquiring -> Active -> Releasing -> Released
                   \-> RecoveryPending
```

职责：

- 串行执行 `Acquire`、`Reconcile`、`Release` 和 `Dispose`。
- 调用抽象的任务栏原生接口发现精确句柄、捕获状态和隐藏/恢复。
- 在任何系统变更前写入 journal。
- 只恢复由当前租约实际修改、仍满足任务栏身份校验的窗口。
- `Release` 和 `Dispose` 幂等；释放开始后不允许新的隐藏操作。
- 不使用终结器执行系统恢复。
- 回滚或恢复无法验证时进入 `RecoveryPending`，保留 journal、watchdog 和跨进程锁直到进程退出。

### `ITaskbarNativeApi` / `TaskbarNativeApi`

所有真实 Win32 调用集中在 Core 层。只允许：

- `FindWindow("Shell_TrayWnd", null)` 或等价的精确顶层类名枚举。
- 校验窗口类名、所属 PID、所属进程名为 `explorer`，并确认它属于主显示器。
- 查询可见性和 `WINDOWPLACEMENT.showCmd`。
- 使用 `ShowWindow` 隐藏或恢复已验证句柄。
- 注册和识别 `TaskbarCreated`、`WM_DISPLAYCHANGE` 消息。

禁止：

- 枚举并操作 `ApplicationFrameHost` 窗口。
- 按 Explorer 进程批量选择顶层窗口。
- 修改 `StuckRects3`。
- 调用 `ABM_SETSTATE`。
- 操作 `TrayButton`、`TrayNotifyWnd` 等子窗口。

### `TaskbarLeaseJournal`

journal 位于 `%AppData%\MacDock\taskbar-lease.json`，包含：

- schema 版本；
- 随机 lease ID；
- 主进程 PID 和启动时间；
- watchdog PID；
- 租约状态；
- 每个窗口的 HWND、Explorer PID、类名、显示器标识、原始可见性、原始 `showCmd`、是否已经由本租约修改；
- 最后更新时间和 generation。

写入采用同目录临时文件、刷新、原子替换。顺序必须是“先记录原状态，再执行隐藏”。恢复前重新校验句柄类名、Explorer PID 和租约身份，避免 HWND 复用后误操作其他窗口。

每个窗口记录 `Unchanged`、`HidePending` 或 `HiddenByLease`。`ShowWindow` 的返回值只表示调用前的可见性，成功与否必须通过调用后重新查询可见性确认。

若当前窗口状态已经被用户或其他程序改变，租约不得覆盖该外部修改；记录冲突并保留外部状态。

上述约束适用于可观察到的状态变化。若外部程序在租约已隐藏窗口后提出同样的“保持隐藏”请求，该请求与租约状态不可区分；按已批准的崩溃恢复优先语义，释放时恢复租约前状态。

### 跨进程恢复锁

UI、watchdog 和下次启动恢复共享 `%AppData%\MacDock\taskbar-lease.lock` 的独占文件句柄。UI 在活动租约期间持有它；异常退出后由操作系统释放。watchdog 或新 UI 谁先取得锁谁执行恢复，另一方随后幂等观察 journal 已处理或 lease ID 不匹配。使用文件锁而不是线程相关的命名 Mutex，避免异步续体线程切换导致错误释放。

### `MacDock.Watchdog`

新增一个 `net8.0-windows`、`WinExe` 输出的小型项目，不提权、不注册服务、不设置自启动。它只由主程序为活动租约启动。

职责：

- 接收父进程 PID、父进程启动时间、lease journal 路径和随机 lease ID。
- 校验 journal 路径位于当前用户的 `%AppData%\MacDock` 下。
- 启动后通过一次性 ready 信号通知主程序；主程序在 ready 前不得隐藏任务栏。
- 等待父进程退出，并区分 journal 中的正常释放与异常退出。
- 异常退出时读取最新完整 journal，只恢复经过身份校验且由该租约修改的主屏任务栏窗口。
- 恢复完成后删除 journal；恢复失败时保留 journal 并写日志，供下次 MacDock 启动时重试。

watchdog 不负责窗口监控、Dock UI、设置管理或副屏任务栏。watchdog 自身被强制结束时无法提供保证；下次 MacDock 启动必须先处理残留 journal，形成第二层恢复保障。

### `TaskbarCoordinator`

UI 层生命周期适配器，由 `App` 持有，不由 `MainViewModel` 持有。

- `MainViewModel` 构造函数不再安装任务栏副作用。
- `DockWindow` 成功创建并取得 HWND 后，`App` 根据设置决定是否获取租约。
- `DockWindow.OnSourceInitialized` 安装 `HwndSource` 消息钩子；消息回调只把 `TaskbarCreated` / `WM_DISPLAYCHANGE` 协调请求排入串行执行器，不直接执行系统操作。
- `App.OnExit` 先停止消息协调和窗口监控，再释放任务栏租约，最后显式释放单实例 Mutex。
- 窗口初始化失败时，`App` 在 `catch/finally` 中释放已经获取的资源。

### 设置

新增 `settings.json` 和 `AppSettingsStore`：

- `HideWindowsTaskbar` 默认 `false`。
- 设置页提供明确开关和风险说明。
- 开启时尝试获取租约；失败则自动把 UI 开关恢复为关闭并显示错误，不修改系统设置。
- 关闭时同步释放租约并确认任务栏恢复后再保存关闭状态。
- 设置保存从第一版开始使用临时文件和原子替换。

## Explorer 重启与显示器变化

- `TaskbarCreated` 到达后，旧 HWND 不再视为有效。
- `Reconcile` 重新发现主屏 `Shell_TrayWnd`，先捕获并写入新快照，再隐藏新句柄。
- 新旧句柄都保留在 journal 历史中，但恢复时只操作仍有效且身份匹配的句柄。
- `WM_DISPLAYCHANGE` 只重新确认哪个 `Shell_TrayWnd` 属于主屏；第一版不接管副屏。
- 当前 Dock 仍只位于主屏；多屏 Dock 和副屏任务栏接管不属于本批次。

## 并发与失败处理

- `Acquire/Reconcile/Release` 共用一个异步串行门，不允许轮询线程与退出线程并发修改系统状态。
- 不保留当前 3 秒 `ThreadPool` 轮询；以 Shell 消息驱动协调。
- 每个异步操作携带 lease ID、generation 和取消令牌。
- `Release` 先禁止新协调请求，再等待正在执行的协调完成，然后恢复。
- 事件回调和日志回调不得在内部锁中调用外部订阅者。
- 任一窗口隐藏失败时，立即回滚本次已经隐藏的窗口；journal 保留到回滚完成。
- watchdog 未 ready、journal 写入失败、身份校验失败或找不到精确任务栏时，均保持任务栏可见。

## Win32 ABI 修正

删除任务栏不再使用的 `ABM_GETSTATE` / `ABM_SETSTATE` 路径及对应私有注册表声明。保留的 P/Invoke 必须满足：

- 指针大小返回值使用 `nuint` / `UIntPtr`。
- `LPARAM` 使用 `nint` / `IntPtr`。
- `BOOL` 明确映射并检查返回值。
- 结构体布局、字段偏移和 `Marshal.SizeOf` 通过 x64 测试固定。
- 所有新增系统调用都有微软官方签名来源注释。

## 测试策略

生产代码先抽象系统边界，随后用 TDD 完成：

1. `TaskbarHandleScopeTests`：只接受主屏 `Shell_TrayWnd`，拒绝副屏、普通 Explorer 和 `ApplicationFrameHost` 窗口。
2. `TaskbarLeaseTests`：获取、部分失败回滚、幂等释放、外部状态冲突、重复调用。
3. `TaskbarJournalTests`：先 journal 后隐藏、原子替换、损坏文件、schema 不兼容、残留恢复。
4. `ExplorerRestartTests`：旧 HWND 失效、新 HWND 捕获、generation 协调、释放新句柄。
5. `TaskbarRaceTests`：`Reconcile` 与 `Release` 并发、取消后不得再次隐藏。
6. `TaskbarLifecycleTests`：窗口构造失败、正常退出、异常退出、Mutex 只释放一次。
7. `WatchdogTests`：ready 前不得隐藏、正常退出不重复恢复、父进程异常退出恢复、错误 lease ID 拒绝执行。
8. `NativeAbiTests`：结构体大小、字段偏移和指针宽度。
9. `TaskbarLeaseFileLockTests`：跨进程恢复互斥、释放后重试与取消失败关闭。

默认测试全部使用 fake，不启动 watchdog 真进程、不调用真实 `ShowWindow`。单独的 Shell 集成测试项目或测试类别必须显式 opt-in，并只在一次性 Windows VM / 测试用户配置文件中运行。

## 验收标准

- 设置默认关闭时，启动 MacDock 不改变 Windows 任务栏。
- 开启后只隐藏主屏任务栏，不影响桌面、资源管理器、UWP 窗口或副屏任务栏。
- 正常关闭、关闭设置、Dock 初始化失败时均恢复原可见状态。
- 主进程异常退出时 watchdog 恢复任务栏；watchdog 同时失败时，下次启动通过残留 journal 恢复。
- Explorer 重启后新主屏任务栏被纳入当前租约，退出时可恢复。
- 不写任务栏注册表，不改变系统自动隐藏/置顶偏好。
- 所有新增单元测试通过；全解决方案构建为零错误、零警告。
- 不在开发机默认测试中隐藏真实任务栏。

## 明确不在本批次中的内容

- 副屏任务栏接管或每屏一个 Dock。
- 注册表或 `ABM_SETSTATE` 兼容回退。
- Windows 服务、计划任务、提权或开机启动 watchdog。
- 窗口运行状态、点击切换、持久化旧数据、鱼眼/UI 修复。
- 自动删除当前工作树中的用户修改或项目诊断脚本。

## 后续实施顺序

本设计获确认后生成实施计划，并按以下顺序执行：

1. 抽象 Win32 边界和精确句柄筛选，先写失败测试。
2. 实现 journal 与租约状态机，先写失败测试。
3. 实现 watchdog 协议和异常恢复，先写失败测试。
4. 将所有权从 `MainViewModel` 移到 `App`，接入窗口消息与设置开关。
5. 删除旧注册表、ABM、宽泛枚举和轮询实现。
6. 子 agent 自审，主 agent 审查 diff，运行针对性测试和全量构建。
7. 仅在用户另行允许的隔离 Windows VM 中执行真实任务栏集成验证。
