# DeskBox 开机启动耗时审计 + z 序竞态评估

> 审计对象：`src/DeskBox`（HEAD `9ae0cb5`；`dist\DeskBox.exe` 构建于 2026-10-15 23:31，约等于该 HEAD，含 `[启动]` 埋点）。
> 数据来源：源码静态阅读；`dist\deskbox.log`（2026-10-17 08:39、2026-10-18 10:59/11:02 启动段）；Windows 事件日志（Shell-Core/Operational、Winlogon/Operational、Kernel-Boot/Power、User Profile Service）；只读注册表 Run 键。
> 约束：本次审计没有修改源码、没有构建、没有启动/结束 DeskBox 进程，也没有重启 Explorer。
> 标注约定：**[证据]** = 日志原文 / 事件日志 / 代码行可直接看到；**[推测]** = 由证据推导但未直接测量。行号基于 HEAD 源码；审计期间 Lead 的工作树正在加埋点并实现修复，行号会漂移（见 §7）。

---

## 1. 结论摘要（TL;DR）

1. **两次开机登录样本里，"盒子晚于桌面出现"的绝对大头是 Windows 启动项排队，不是 DeskBox 自身的 2 秒。** [证据] Shell-Core/Operational 事件显示：10-17 桌面切换（AfterDesktopSwitch）发生在 08:38:42；Explorer 08:39:01 才开始枚举 Run 键；`DeskBox.exe --startup` 直到 **08:39:20** 才被创建（执行序为 HKLM 5 项 + HKCU 5 项之后），盒子首帧约 **08:39:22.6**。即：盒子比桌面切换晚约 **40s**、比 Run 枚举开始晚约 **19s**。10-18 10:59 样本同构（桌面切换 10:58:57 → DeskBox 启动 10:59:34 → 盒子可见约 10:59:35，相隔约 36s / 23s）。
2. **DeskBox 自身从进程启动到盒子首帧约 2.0s（08:39 样本）/1.7s（10:59）/1.1s（11:02 暖样本）。** 其中首个盒子的「构造+显示」占 608–1295ms，是应用内最大单项；托盘/钩子在盒子显示之后还要占 80–110ms，且都压在第一次渲染之前。
3. **08:39 启动段没有出现「桌面挡住 X 个盒子」日志** [证据]。而且该样本桌面切换发生在 DeskBox 启动前 38s，z 序竞态在这一次根本不可能发生。z 序竞态是**真实存在**的代码风险（10-17 18:18:58 曾出现「桌面挡住 6 个盒子」并成功插回），但它不是本次「几十秒没盒子」的主因；一旦按建议把启动提前到桌面之前，它就会变成必须同时修的前置条件。
4. **盒内图标不是"晚几秒"的来源。** [证据] 两次开机样本图标批次都是磁盘缓存 62–63/63 全命中、批次耗时 47–52ms；[推测] 叠加 `LoadIconAsync` 里人为的 120ms `Task.Delay` 与 UI 线程占用，图标可见只比盒子首帧晚约 0.1–0.2s。只有磁盘缓存冷（换版本首次、缓存被清理）才会回退 shell 提取，实测 58 项 shell 提取 311ms，仍非"数秒"。
5. **满足"进入桌面时盒子已经显示"只有两条路**：① 把自启从 `HKCU\...\Run` 改为任务计划程序「登录时」触发，让 DeskBox 在 Explorer 处理启动项之前就跑起来（本次样本可提前约 19–46s）；② 若维持 Run 方式，则只能把应用内 2s 压到约 1s，盒子仍会晚于桌面数秒。①必须先有 z 序守护（§3），否则更早启动会直接被后创建的桌面窗口盖住。
6. **最小风险、立即可做**：启动完成瞬间强制 z 序自检 + 前 2~5s 短周期快速自检；玻璃引擎延后到首帧之后；盒子显示后先把 UI 让给渲染再创建托盘/钩子；首次加载图标去掉 120ms 人为 Delay；玻璃未就绪时给一个不透明度兜底（当前配置 `BoxOpacity=0`，玻璃没就绪时盒子背景是全透明的）。

---

## 2. 绝对时间线：从登录到盒子可见

### 2.1 系统侧（Windows 事件日志）

两次样本都是"唤醒/引导后首次登录"（Kernel-Power 记录 10-17 08:38:30 / 10-18 10:58:30 从低功耗恢复），因此可以代表真实开机登录路径。

| 事件 | 2026-10-17 开机样本 | 2026-10-18 开机样本 |
|---|---|---|
| 登录/认证（Winlogon 1/2、User Profile Service 1） | 08:38:33 | 10:58:31 |
| Explorer 外壳活动（Shell-Core `ImmersiveShellAndTaskbar`） | 08:38:37–38 | 10:58:54–55（explorer.exe StartTime = 10:58:53） |
| **桌面切换完成（`AfterDesktopSwitch`）** | **08:38:42** | **10:58:57** |
| 启动项阶段开始（`DesktopStartupApps`） | 08:38:42 | 10:58:57 |
| Explorer 开始枚举 Run 键（Shell-Core 9705） | 08:39:01（先 HKLM Run；08:39:14 起 HKCU Run） | 10:59:11 |
| **`DeskBox.exe --startup` 被 CreateProcess（9707）** | **08:39:20** | **10:59:34** |
| 进程创建返回（9708） | 08:39:21（PID 298564） | 10:59:35（PID 278560） |
| 启动项阶段结束（`DesktopStartupApps` 9649） | 08:39:41 | 10:59:45 |

关键差：**DeskBox 进程启动时刻 − 桌面切换 = 38.6s（10-17）/ 36.5s（10-18）；− Run 枚举开始 = 19.6s / 22.5s。** [证据]
盒子首帧 − 桌面切换 ≈ 40.7s（10-17：08:39:22.7 − 08:38:42）/ 38.2s（10-18：10:59:35.2 − 10:58:57）。
DeskBox 在 HKCU Run 的 15 个值里，是 Explorer 本次实际执行的第 11 个（前面已有 5 个 HKLM 项 + 5 个 HKCU 项），其中 10-18 的 `AMDNoiseSuppression.exe` 单项就让 Explorer 阻塞约 11s（9707→9708 间隔）。Run 项是串行 CreateProcess 的，所以"排在谁后面"直接决定我们被拉起的时刻。[证据 + 注册表只读核对]

### 2.2 DeskBox 内部阶段（`dist\deskbox.log`，单位 ms，自进程启动）

| 阶段 | 10-17 08:39 开机 | 10-18 10:59 开机 | 10-18 11:02 暖启 | 10-16 18:27 暖启 |
|---|---|---|---|---|
| 进程入口（App 初始化完成） | 420 | 456 | 270 | 309 |
| ├ 其中 App 内部初始化本阶段（含 App.xaml/Theme 加载） | 233 | 239 | 108 | 116 |
| 单实例锁完成 | 433 | 470 | 283 | 324 |
| 配置读取完成（6 个盒子） | 459 | 497 | 310 | 352 |
| 主题应用完成 | 460 | 498 | 311 | 353 |
| 桌面外壳准备完成 | 461 | 498 | 312 | 354 |
| **首个盒子「游戏」构造+显示完成** | **1757** | **1408** | **921** | **1014** |
| ├ 首盒本阶段耗时（含 XAML/首窗口/玻璃引擎） | 1295 | 909 | 608 | 659 |
| 其余 5 盒构造+显示 | 1781→1881（119） | 1433→1544（95） | 941→1033（112） | 1033→1125（91） |
| 全部盒子显示完成 | 1881 | 1545 | 1033 | 1125 |
| 托盘图标完成 | 1978 | 1651 | 1114 | 1212 |
| 前台监听完成（HEAD 最后一个阶段） | 1980 | 1653 | 1117 | 1215 |
| **启动完成（允许 z 序巡检）** | **1981** | **1653** | **1117** | **1215** |
| 首帧（推算 = 启动完成 + 1 个渲染周期，[推测] 16–50ms） | ≈2000–2050 | ≈1670–1720 | ≈1130–1180 | ≈1230–1280 |
| 图标批次结束（日志时间戳） | 08:39:23（63 项，磁盘 63/63，47ms） | 10:59:36（62/62/0，49ms） | 11:02:54（63/63/0，47ms） | 18:27:23（63/63/0，45ms） |
| 玻璃就绪（日志时间戳，6 盒同秒） | 08:39:23 | 10:59:36 | 11:02:54 | 18:27:23 |

数据取自日志 `[启动]` 行的「自进程启动 Xms」；`dist\deskbox.log` 行号见 §9。

### 2.3 冷启动 vs 暖启动：增量集中在"第一个盒子"

| 增量（对比暖启动 11:02 样本） | 10-17 08:39（冷） | 10-18 10:59（当日首启） |
|---|---|---|
| 进程入口（App 初始化） | +125ms | +131ms |
| 首盒构造+显示 | **+687ms** | **+301ms** |
| 其余 5 盒 | +7ms | −17ms |
| 托盘 | +16ms | +25ms |
| **进程启动→启动完成合计** | **+864ms** | **+536ms** |

[证据] 同一份源码、同一配置（6 盒 63 项）下，全部暖样本首盒 568–784ms（10/15–10/18 多次），冷样本 1295ms/909ms。差异只出现在"第一个窗口 + 首次资源/驱动加载"上。[推测] 冷增量主要来自：WPF 首个分层窗口初始化与 XAML/JIT、字体缓存冷、GPU 驱动首次初始化、`LiquidGlassEngine` 的 D3D11 设备 + D3D9Ex 设备 + 3 次着色器编译、Defender 扫描。Lead 新增的 `StartupTrace` 埋点（BoxWindow 初始化拆分、玻璃引擎 D3D11/D3D9Ex/着色器三段、首帧 ContentRendered、日志写入合计）正是用来把这些拆开——见 §8。
`pre-App`（进程创建 → "进程入口"日志，含 .NET 宿主、App 构造、base.OnStartup）反而只差 +25ms（187 vs 162），说明单文件解压/宿主启动在这台机器上不是冷启动的主要增量（解压缓存 `%TEMP%\.net\DeskBox\8tjAWo_7IuJZ` 30 个文件 49MB，构建后仅解压过一次）。[证据]

### 2.4 08:39 启动段逐条（`dist\deskbox.log:266–295`）

```
08:39:21 [启动] 进程入口：本阶段 233ms，累计 233ms，自进程启动 420ms
08:39:22 [启动] 盒子「游戏」构造+显示：1295ms，自进程启动 1757ms
08:39:22 [启动] 盒子「学习」构造+显示：20ms，自进程启动 1881ms
08:39:22 [启动] 盒子显示：本阶段 1421ms，累计 1696ms，自进程启动 1881ms
08:39:22 [启动] 托盘图标：本阶段 97ms，累计 1793ms，自进程启动 1978ms
08:39:22 [启动] 启动完成（已允许桌面保持巡检）：本阶段 0ms，累计 1795ms，自进程启动 1981ms
08:39:22 DeskBox 已启动。
08:39:22 Liquid Glass: 子窗口改由桌面宿主 65906 捕获。
08:39:23 Liquid Glass: 盒子「游戏」实时玻璃已就绪。   （其余 5 盒同秒）
08:39:23 [图标] 63 项 内存命中 0 / 磁盘命中 63 / shell 0 / 耗时 47ms
```
（中间各行省略，完整内容见日志。所有盒子显示完成到"启动完成"只差 100ms；首帧发生在 OnStartup 返回之后。）

### 2.5 「桌面挡住 X 个盒子」日志核对（任务要求）

- **08:39 启动段：没有出现。** 08:39:21–08:39:23 只有 `[启动]`/玻璃/图标日志；下一次「桌面挡住」是 10-17 18:18:58（6 个盒子、前台=Progman、全部"已插回桌面之上（自校验通过）"）；10-17 00:27:42 有一次「工具」单盒、插回自校验未通过→退回压桌面。10-18 10:59/11:02 两段同样没有。
- **原因成立且可解释** [证据]：08:39 样本桌面切换在 08:38:42，DeskBox 08:39:20 才启动，桌面窗口早已存在并稳定，盒子创建时 `SetWindowPos(HWND_BOTTOM)` 落在"贴着桌面上方"，不存在被后创建桌面覆盖的窗口期。
- 历史证据表明"被桌面盖住"状态确实会发生：10-14（旧版本）日志 `dist\deskbox.log:1–99` 有整屏的「已把桌面压回最底层」刷屏；10-17 18:18:58（当前版本）一次性 6 个盒子全部被盖并成功恢复。
- 结论：**这次"隔几秒"不能归因于 08:39 的 z 序竞态**；但 z 序竞态仍是必须修的保险，理由与估算见 §3。

---

## 3. z 序竞态专项（代码证据 / 最坏延迟 / 修复建议）

### 3.1 机制与代码证据

盒子的 z 序策略是"常驻桌面之上、普通窗口之下"，实现方式是**把自己压到底**，再靠"桌面保持巡检"在需要时把自己插回桌面上方：

| 环节 | 代码位置（HEAD） | 说明 |
|---|---|---|
| 每个盒子构造即启动 1.5s 定时器 | `Views/BoxWindow.xaml.cs:179–193` | `_zOrderTimer` 在构造函数里 `Start()`，首 tick 早于 `_startupComplete` |
| 窗口初始化时压底 | `Views/BoxWindow.xaml.cs:218–227`、`535–558` | `SourceInitialized → PushDown()`，`PushToBottom`（`Interop/NativeMethods.cs:274–280`，即 `SetWindowPos(HWND_BOTTOM)`），有 300ms 重复保护 |
| 失焦再压底 | `Views/BoxWindow.xaml.cs:228–235` | `Deactivated` 且前台不是任务栏 → `PushDown()` |
| 定时器只"请求"巡检 | `Views/BoxWindow.xaml.cs:185–192` | `RestoreIfShellHidTheBox()` + `App.RequestDesktopSweep()` |
| 启动完成前一律忽略 | `App.xaml.cs:289–300` | `ScheduleDesktopSweep` 在 `!_startupComplete` 时直接 `return`（**请求被丢弃，不排队**） |
| 160ms 防抖 | `App.xaml.cs:15、302–318` | 每次请求重启计时器，只跑最后一次 |
| 巡检：判定被盖 → 动作 | `App.xaml.cs:327–401` | `IsCoveredByDesktopNow()`（5 个内容区采样点全部命中桌面类窗口，`NativeMethods.cs:433–455`）；`PlaceBoxAboveDesktop`（`NativeMethods.cs:488–535`）把盒子插到桌面根之上，自校验通过则保持；否则退化"压桌面"（`PushDesktopToBottom`，`NativeMethods.cs:337–353`） |
| **600ms 限流** | `App.xaml.cs:18、356–360` | 被限流的巡检直接丢弃，不补跑 |
| 只监听前台变化 | `App.xaml.cs:254–283`（hook 注册在 194） | `EVENT_SYSTEM_FOREGROUND`；**没有** `EVENT_OBJECT_CREATE/SHOW` 监听；注册时机在盒子显示、所有初始化之后 |
| `_startupComplete = true` 之后没有立即巡检 | `App.xaml.cs:197–199` | 首个 z 序检查只能等某个盒子的 1.5s 定时器 tick |

### 3.2 最坏延迟推演（[推测]，基于上面的代码路径）

- 单次被盖（运行期）：**≤ 1.5s（定时器周期）+ 160ms（防抖）≈ 1.66s**。因为 6 个盒子的 1.5s 定时器错开约 100ms 内成簇，每 1.5s 至少产生一次巡检请求；被 600ms 限流丢弃的那次会在下一簇补上，因此极限仍约 1.66s，不会叠加到 3s。
- 启动阶段被盖：`_startupComplete` 之前的请求全部丢弃（不排队）。因此恢复的绝对上界 = **启动完成时刻 + 1.5s（等下一次定时器 tick）+ 160ms（防抖）≈ 启动完成 + 1.66s**。08:39 样本启动完成 = 1981ms（自进程启动）→ **最坏恢复 ≈ 3.64s**；若桌面在启动完成之前就已出现，则"被盖住"的持续时间为恢复时刻 − 桌面出现时刻，样本场景**可达约 2.1s**（例如桌面 1.5s 出现、3.64s 恢复）。运行期（启动完成后才被盖）恢复 ≤1.66s。
- 反复竞态：`Deactivated → PushDown` 会把盒子再次压底；若此时桌面窗口恰好被系统提上来（Win+D、全屏切换、Explorer 重启），每个循环都可能出现一次 1.66s 级空窗。**"桌面已经出现、盒子被盖住数秒"与用户描述相符。**
- 检测盲区：判定要求 5 个采样点**全部**命中桌面窗口（`NativeMethods.cs:433–455`）；若只有桌面的一部分（某个 WorkerW）压住盒子的一角，会被判为"没被盖"而长期不修——只能等下一次前台变化或全盖。**这是比延迟更隐蔽的风险。**
- 触发概率：在两次 Run 方式开机样本中为 0（桌面早已就绪）；若按 §5-A 把启动提前，**是必现路径**，必须先有 §3.4 的守护。

### 3.3 触发条件对照（实测证据）

- 当前版本被动触发场景确实存在：Win+D（10-17 18:18:58 日志，前台=Progman，6 盒全被盖，`PlaceBoxAboveDesktop` 全部自校验通过），说明"插回桌面上方"这条主路径是可靠的。
- 退化路径（压桌面）也在用：10-17 00:27:42「「工具」插回后自校验未通过 → 已退回压桌面」。退化会触发整屏重绘（旧日志 10-14 的刷屏就是它），启动阶段应尽量避免。
- 08:39 样本无触发，符合"桌面先于 DeskBox 40s 就绪"的时序。

### 3.4 修复建议（最小风险，按顺序）

1. **【必须】启动完成瞬间先做一次强制巡检**：`_startupComplete = true` 之后立刻 `RequestDesktopSweep()`（或直接 `RunDesktopSweep()`）。只多 ~36 次 `WindowFromPoint`，成本亚毫秒级；已有 600ms 限流保证不会抖。
2. **【必须】启动后 2–5s 短周期快速自检**：App 级单一定时器（如 90–200ms × 10–25 次）调用 `RunDesktopSweep()`，覆盖"桌面窗口比我们晚出现"的窗口期；到点自动停，随后交回各盒 1.5s 定时器。代价是每 tick 一次只读命中测试，几乎无成本。
3. **【必须】监听 `EVENT_OBJECT_CREATE/SHOW` 的桌面窗口**（`Progman`/`WorkerW`/`SHELLDLL_DefView`，用 `WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`），收到事件即调度一次巡检；这是"而不是只监听前台变化"的关键——桌面窗口出现/z 序被提上来时不一定会触发 `EVENT_SYSTEM_FOREGROUND`。注意：`SetWinEventHook` 用范围 `EVENT_OBJECT_CREATE..EVENT_OBJECT_SHOW` 会在启动期收到所有窗口事件，回调里先做类名过滤、再 `BeginInvoke` 到 UI 线程并走防抖，避免抖动。
4. **【建议】把前台 hook 的注册提前到 `RestoreBoxes()` 之前**：现在 `WatchForegroundChanges()` 在全部盒子、托盘、鼠标钩子之后（`App.xaml.cs:194`），启动期间的前台变化全部丢失；对"盒子先出现、桌面后出现"场景，这类事件正是恢复信号。
5. **【建议】`PushDown` 条件化**：桌面根存在时用 `PlaceBoxAboveDesktop(box, DesktopRoot(), 采样点)` 代替盲目的 `HWND_BOTTOM`；桌面根还不存在时（开机早期）不要压底，改为标记"待桌面出现后自检"（由第 2/3 条兜底）。这直接消除"启动早期把盒子压到桌面之下"的路径。
6. **【建议】补齐部分覆盖检测**：巡检时若 `DesktopIsForeground()` 为真且 ≥1 个采样点命中桌面窗口，也执行一次 `PlaceBoxAboveDesktop`（动作带自校验，失败会自动退化），避免"只盖住一角"长期不被发现。

**风险**：z 序动作已有自校验 + 退化路径 + 限流，额外事件监听的风险主要是"桌面窗口频繁 show/hide 引起多次巡检"——用类名过滤 + 160ms 防抖即可；最坏观感是启动期一次 ≤16ms 的窗口重排。

### 3.5 验证清单

- 正常启动：日志应出现"启动完成 → 桌面现场（被桌面盖住的盒子=0/6）"，且不出现「桌面挡住」。
- 模拟竞态：在 DeskBox 启动后才结束/重启 Explorer（`explorer.exe` 结束会连带重启壳层；注意别在用户正在用的机器上做），或用一个 `HWND_TOP` 的 WorkerW 模拟桌面后创建；期望 ≤200ms 内出现「桌面窗口显示（WorkerW），立刻巡检」并恢复。可用 `tools\probe-startup.ps1` 同思路加窗口 z 序断言。
- 观察 `[启动] 桌面窗口显示`/「桌面挡住 X 个盒子」日志频率，确认启动后不再每分钟刷屏。

---

## 4. 图标与玻璃：是否晚于盒子数秒？

### 4.1 图标（结论：不是"数秒"级问题，只有 120ms 可省 + 冷缓存 ~0.5s）

- 磁盘缓存命中率 100%：10-17 08:39 的 63 项、10-18 10:59 的 62 项、11:02 的 63 项全部 `磁盘命中`，批次 47–52ms [证据，日志]。
- `LoadIconAsync` 在取图前固定 `await Task.Delay(120)`（`Views/BoxWindow.xaml.cs:712–742`，注释说明是给设置面板拖动尺寸时去抖用的）——**首次加载也吃这 120ms**。叠加 2 个常驻 STA worker（`Services/IconLoader.cs:16–17、69–84`）与 UI 线程启动期占用，盒子首帧后约 0.1–0.2s 图标就位。
- 只有磁盘缓存冷时才会走 shell 提取：10-15 18:33 首次运行 58 项 `shell 58 / 311ms` [证据]；这仍是 0.3–0.5s，不是"数秒"。
- 建议：① `ReloadItems` 的"首次加载"传 `debounce:false`（或把 120ms 只用于 `RefreshFromConfig`/尺寸变化路径）；② 启动前把各盒目录图标 `IconCache.Prewarm`（后台，已有 API）；③ 首次渲染用占位图不影响正确性，但可以在首帧后立刻触发加载。
- 风险：低。唯一注意是拖动设置面板时仍要去抖，否则会向 shell 发大量取图请求。

### 4.2 玻璃（结论：值待测，但"延后到首帧后"是明确正确的方向）

- HEAD 版本在 `GlassBackdrop` 的 `Loaded` 里同步 `Attach()`（`Rendering/GlassBackdrop.cs:25–53`），首次会创建 `LiquidGlassEngine`：D3D11 设备、WinRT 设备、**D3D9Ex 设备（带窗口句柄）**、3 次 `D3DCompiler` 着色器编译、30Hz `DispatcherTimer`，全部在 UI 线程、在首个盒子的 `Show()` 关键路径附近（`Rendering/LiquidGlassEngine.cs:59–103`）。[推测] 这是首盒 608–1295ms 里最可疑的可优化段之一——是否落在首帧之前，取决于 WPF `Loaded` 与首帧的先后，Lead 新加的 `ContentRendered` 日志会直接给出答案。
- 之后的捕获初始化在首个 30Hz tick 里 `Discover()`（`LiquidGlassEngine.cs:113–140、142–181`）：`EnumWindows`+`EnumChildWindows` 扫全系统窗口、创建 `GraphicsCaptureItem`/Session，也在 UI 线程。[证据] 日志时间戳显示「子窗口改由桌面宿主 … 捕获」「已开始捕获 Wallpaper Engine 窗口」出现在「DeskBox 已启动」**之后**（秒级分辨率内），即启动完成后才开始真正出图；具体延迟（0–300ms，[推测]）需新埋点确认。
- 观感风险：配置 `BoxOpacity=0`（`dist\config.json`），玻璃不可用时 `Backdrop.Opacity = BoxOpacity = 0`（`Views/BoxWindow.xaml.cs:65–73` 的 `AvailabilityChanged` 回调、`:428–438` 的 `ApplyVisualSettings`；`Rendering/GlassBackdrop.cs:79–85` 的 `SetAvailable`）→ 盒背景完全透明，只剩边框与图标，看起来像"盒子没出现"。若壁纸引擎（Wallpaper Engine）比 DeskBox 晚启动，日志会退化成每 3s 重新 Discover、失败 15s 重试（`LiquidGlassEngine.cs:120–139` 的 `_failed` 退避），玻璃可能晚数秒到十几秒才出现。建议给一个兜底不透明度（如 `Math.Max(0.28, BoxOpacity)`）直到玻璃就绪，或首帧后一次性补上。
- 建议：① 启动阶段把 `Attach()` 推迟到首帧 `ContentRendered` 之后（或 `Dispatcher.BeginInvoke(Background)` + 200–300ms）；② 捕获初始化移到后台线程 / 降低 Discover 频率；③ 玻璃就绪前用兜底背景。风险：低（玻璃本来就有 `AvailabilityChanged` 降级路径）。
- 收益：若引擎初始化在首帧前，可省 100–400ms（冷启动更多）；若在首帧后，则收益是"首帧后不卡顿"，仍值得做。

---

## 5. 发现清单（按影响预估 ms 排序）

### A. Windows 启动项排队导致 DeskBox 被很晚拉起（影响：~19,000–45,000ms，本次样本）【P0 结构性】
- **影响预估**：以 08:39 样本计，从桌面切换到盒子可见 40.7s，其中 DeskBox 自身只占 ~2.0s；DeskBox 等待被拉起就占 ~38s。10-18 样本同构（~38s）。不同机器上取决于 Run 队列长度与单项耗时，**通常仍是数秒到数十秒**。
- **证据**：§2.1 事件日志；HKCU Run 共 15 个值、DeskBox 是本次执行的第 11 项；Shell-Core 9707/9708 的串行执行时间戳。
- **建议改法**：把自启从 `HKCU\...\Run\DeskBox` 换成任务计划程序「用户登录时」触发（设置面板的"开机自启"开关改为注册/删除计划任务；保留 Run 作为注册失败回退，靠现有单实例互斥避免双启动）。也可二者并存：计划任务优先，Run 项保留但进程检测到已有实例立即退出（现状 `AppInstance` 已保证这一点）。
- **风险**：中。① 计划任务可能在 Explorer 之前创建窗口，桌面后出现会盖住盒子——**必须以 §3.4 的守护为前提**；② 托盘 `NotifyIcon` 在 Shell_TrayWnd 未就绪时可能创建失败/不显示（WinForms `NotifyIcon` 会在 `TaskbarCreated` 后自愈，但需实测）；③ 任务计划的创建/删除需要额外的注册表/任务权限，公司策略可能禁止；④ 用户设置迁移（从 Run 到 Task）要写清楚。
- **验收**：`LogBootContext` + Shell-Core 事件对比：计划任务触发时间应早于 Run 枚举，盒子首帧应 ≤ 桌面切换时间（或紧随其后 <0.5s）。

### B. 首个盒子的"构造+显示"占 608–1295ms，冷启动时是最大单项（影响：~300–700ms 可优化，待埋点确认）
- **影响预估**：08:39 冷样本 1295ms；暖样本 568–784ms；同一配置。启动完成合计 1117ms（暖）→1981ms（冷），冷增量 864ms 中 687ms 在这一项。
- **证据**：§2.2/2.3 表格（日志）；`CreateBoxWindow` 同步构造+`window.Show()`（`App.xaml.cs:494–516`）。
- **建议改法**：先测后改。Lead 的 `StartupTrace` 已把 BoxWindow 构造拆成 XAML/外观/内容/事件，把玻璃引擎拆成 D3D11/D3D9Ex/着色器，并加了 `ContentRendered` 首帧日志——按结果优先砍"首帧之前"的部分：延后玻璃（§4.2）、延后/异步化首盒内容装载、把 XAML 资源（`Themes/Theme.xaml`）做启动期懒加载。构建层可试 `PublishReadyToRun=true`（估计省 JIT 100–300ms，风险中）。
- **风险**：中低。延后内容装载会短暂出现空盒子；玻璃延后会让首帧后 0.2–0.5s 才"变玻璃"（可用兜底背景掩盖）。

### C. 启动尾部（托盘 81–106ms + 鼠标钩子 + 前台监听 + 首次运行收尾）全部压在第一次渲染之前（影响：~80–220ms）
- **影响预估**：盒子显示完成 → 启动完成 = 100ms（08:39）/108ms（10:59）/84ms（11:02）；再加上 `ShowSettings/HandleArgs`（`--startup` 时很短）。首帧必须等 `OnStartup` 返回，故这段全部计入"看不见盒子"的时间。
- **证据**：`App.xaml.cs:154–214` 的顺序；日志中"全部盒子显示"与"启动完成"的差值；`TrayService` 构造函数创建 NotifyIcon + GDI+ 绘制图标（`Services/TrayService.cs:12–46、153–200`）。
- **建议改法**：`RestoreBoxes()` 后立刻 `Dispatcher.BeginInvoke(Loaded/Background)` 或 `await Dispatcher.Yield(DispatcherPriority.Render)` 再把托盘/鼠标钩子/前台 hook/首次运行收尾放到后续回调；`_startupComplete = true` 与 z 序守护仍在显示后立即执行（不要被延后）。
- **风险**：低。托盘晚 1 帧出现无感知。

### D. 玻璃引擎同步初始化位置（影响：待测，估计 100–400ms；冷启动更高）【P1】
- 见 §4.2。**建议**：`GlassBackdrop` 启动期改为首帧后 `BeginInvoke` 附着；保持运行期（设置开关）同步行为。**风险**：低。

### E. 图标首次加载的人为 120ms Delay（影响：~120ms）【P2】
- 见 §4.1。**建议**：首载免 Delay。**风险**：低。

### F. 玻璃未就绪期背景全透明（`BoxOpacity=0`）（影响：视觉 0.2–1s，可能被感知为"没显示"）【P2】
- 见 §4.2。**建议**：兜底不透明度。**风险**：低。

### G. 启动段同步逐条写日志（影响：待实测，估计 20–150ms）【P2】
- `Log.Write` 每条 `File.AppendAllText`（`Services/Log.cs:16–37`），启动段 [启动]≈12 条 + 配置/玻璃 8 条 + 图标 1 条 ≈ 20–30 次 open/write/close；开机冷盘 + Defender 时单条可能 1–10ms。Lead 已加 `StartupTrace.LogWriteMs` 统计，按实测决定是否改为"启动期先缓冲、首帧后统一落盘"或异步队列。
- **风险**：低（缓冲区在崩溃时会丢日志，可接受）。

### H. 单文件发布配置（影响：估计 100–300ms JIT；首启解压另有一次性成本）【P3】
- publish 参数为 self-contained + `PublishSingleFile` + `EnableCompressionInSingleFile` + `IncludeNativeLibrariesForSelfExtract`，未开 ReadyToRun（`README.md` 编译段），产物 73MB。`pre-App` = 162–217ms（暖/冷差异 25–55ms），解压目录已缓存时不再解压；若 `%TEMP%` 被清理/换版本，首次启动需重新解压 ~49MB（[推测] 0.5–2s，未测）。
- **建议**：评估 `PublishReadyToRun=true`；评估关闭压缩换启动 CPU（体积显著变大）；验收里加"清理 %TEMP% 后首次启动"场景。
- **风险**：中（体积/构建时间/兼容性；单文件 + R2R 体积会明显增大）。

### I. 启动期后台 IO/COM 竞争（影响：<100ms 直连；间接拖慢 Explorer）【P3】
- `SystemIconGuard.EnsureHidden` 每个盒子 `Task.Run` 解析 .lnk 目标、写 HKCU、`SHChangeNotify(SHCNF_FLUSH)`（`Services/SystemIconGuard.cs:78–145`、`Services/DesktopSystemIcon.cs:88–109、142–166`）；`IconDiskCache` 读 PNG（`IconDiskCache.cs:41–70`）；6 个 `FileSystemWatcher`；Defender 扫描。这些不阻塞 UI 线程，但在开机冷盘上会与 Explorer/自身首帧抢 IO。08:39 的「系统图标桌面隐藏」日志就夹在盒子构造之间 [证据]。
- **建议**：启动完成后 1–2s 再跑系统图标自愈/预热（或 `Task.Delay` 到首帧后）；`FileSystemWatcher` 可延后到 `_startupComplete`。
- **风险**：低（只是延后自愈时机）。

### J. 运行期渲染成本（非启动关键，但影响"启动后卡顿"观感）【P3】
- 每个盒子 `AllowsTransparency=True` 分层窗口 + `DropShadowEffect`（`Views/BoxWindow.xaml:7、106–112`）+ `D3DImage` 玻璃；玻璃 30Hz `DispatcherTimer` 在 UI 线程重绘（`LiquidGlassEngine.cs:89–91、142–181`），每次 `Discover()` 还做全窗口枚举。启动后第一次 tick 可能卡 10–50ms [推测]。
- **建议**：Discover 降频/后台；`DropShadowEffect` 视情况改用图片阴影；非悬停态降低玻璃帧率。**风险**：中（视觉取舍）。

---

## 6. 优先级清单（可直接排期）

| 优先级 | 改动 | 预期收益 | 风险 | 主要文件 |
|---|---|---|---|---|
| **P0-1** | 启动后 z 序守护三件套：启动完成立即巡检 + 前 2–5s 短周期自检（90–200ms）+ `EVENT_OBJECT_CREATE/SHOW` 桌面窗口监听（类名过滤 + 防抖） | 把"被桌面盖住"空窗从最坏 ~1.7–2.2s 压到 <0.2–0.3s，并让"提前启动"方案可行 | 低–中 | `App.xaml.cs`、`Interop/NativeMethods.cs` |
| **P0-2** | 自启改为任务计划程序「登录时」（保留 Run 回退；依赖 P0-1） | 本次样本：盒子可从"桌面切换后 ~40s"提前到"桌面切换前几秒"（约 -40s 量级），普通机器数秒–数十秒 | 中 | 设置面板 + `ContextMenuRegistrar.AutoStart` |
| **P1-1** | 首帧优先：`RestoreBoxes` 后让出渲染，再建托盘/钩子/执行收尾；`_startupComplete` 与 z 序守护前移 | **-80~-220ms** | 低 | `App.xaml.cs`、`TrayService` |
| **P1-2** | 玻璃引擎延后到首帧后（ContentRendered/200–300ms 后 Background 附着） | **-100~-400ms**（若在首帧前，待埋点确认） | 低 | `Rendering/GlassBackdrop.cs` |
| **P1-3** | 用新埋点定位首盒 1295→608ms 的冷增量，砍首帧前的部分（XAML/字体/玻璃/驱动） | **-300~-700ms** 冷启动 | 中低 | `Views/BoxWindow.*`、`Rendering/*` |
| **P2-1** | 图标首载去掉 120ms 人为 Delay；启动前 `IconCache.Prewarm` | **-120ms**（图标可见） | 低 | `Views/BoxWindow.xaml.cs`、`App.xaml.cs` |
| **P2-2** | 玻璃就绪前给兜底背景不透明度（避免 BoxOpacity=0 时像"没显示"） | 视觉 0.2–1s | 低 | `Views/BoxWindow.xaml.cs`、`GlassBackdrop` |
| **P2-3** | 启动期日志缓冲/异步写（按 `LogWriteMs` 实测决定） | 20–150ms | 低 | `Services/Log.cs` |
| **P3-1** | 发布参数评估：ReadyToRun / 关闭压缩 / 首启解压场景 | 100–300ms + 一次性 | 中 | `README`/发布脚本 |
| **P3-2** | 启动期后台自愈/预热延后到首帧后；Discover 降频 | <100ms + 观感 | 低–中 | `SystemIconGuard`、`IconLoader`、`LiquidGlassEngine` |
| **P3-3** | 部分覆盖检测补齐（角落被 WorkerW 盖住时也能发现） | 消除长期隐蔽空窗 | 低 | `NativeMethods.IsCoveredByDesktop`、`App.RunDesktopSweep` |

---

## 7. 对在研修复的审计意见（工作树快照，2026-10-18 11:10 前后；不是最终结论）

审计期间 Lead 已在工作树实现：`StartupTrace` 埋点、`LogBootContext`、`RaiseStartupPriority`、`PrewarmBoxIcons`、`WatchDesktopWindowAppearance`（`EVENT_OBJECT_SHOW`）、`BeginStartupSweeps`（24×90ms）、`LogStartupDesktopState`、启动完成即 `RequestDesktopSweep`、`RestoreBoxes` 先构造全部再统一 Show、玻璃 `ScheduleAttach`（启动期 220ms 后 Background 附着）、`DesktopShell` 路径 Lazy 化。方向与本文建议一致，以下几点建议在合并前确认：

1. **`EVENT_OBJECT_SHOW` 只在"显示"时触发**：若桌面窗口在我们的 hook 注册之前就已显示、但随后又被系统提到盒子上面（SetWindowPos，不产生 SHOW），或只盖住部分采样点，仍依赖 1.5s 定时器/短周期巡检。建议同时接受 `EVENT_OBJECT_CREATE`（或在 `IsCoveredByDesktop` 上补"部分覆盖"分支）。
2. **短周期自检 24×90ms ≈ 2.16s**：若走 P0-2（计划任务提前启动），桌面可能比启动完成晚几秒到几十秒出现，2.16s 窗口会漏；`EVENT_OBJECT_SHOW` 可兜住，但建议把窗口期延长到 5s 或"直到第一次确认桌面根已存在且 0 盒被盖"再停（成本仍是每 tick 一次只读命中测试）。
3. **构造全部盒子再统一 Show** 会把 6 个盒子的 XAML/事件初始化集中在首帧前（首盒一次性成本 + 其余 5×15–40ms），比"逐个显示"更"齐"，但首帧时间不变；若要再压首帧，可考虑只先 Show 1 个盒子、其余在首帧后 1 帧内补上。
4. **玻璃 `ScheduleAttach` 的 220ms 固定延迟**在正常启动时没问题，但若首帧本身 >220ms（冷启动），附着仍可能落在首帧前；建议改为 `ContentRendered` 一次性触发或 `Dispatcher.BeginInvoke(Background)` 双保险。
5. **`RaiseStartupPriority`** 只提升本进程 CPU 优先级，对"等 Windows 拉起"（P0-2）无帮助，但对首盒 1.3s 的冷启动有效，保留。记得异常路径也要还原（当前在 `_startupComplete` 处还原，若启动中途抛异常会停留在 AboveNormal——可接受但建议 `try/finally`）。
6. **`BoxOpacity=0` + 玻璃延后**：延迟附着期间盒子背景全透明，建议同步做 P2-2 的兜底不透明度，否则"盒子先亮出来"可能只亮出边框。
7. 工作树正在改 `App.xaml.cs`，本文 §3.1 的行号已失效；合并后建议重新核对 `ScheduleDesktopSweep`/`RunDesktopSweep` 的入口条件是否仍满足 §3.4。

---

## 8. 待实测确认清单（对应 Lead 新埋点）

| 待确认项 | 对应日志/埋点 | 影响什么结论 |
|---|---|---|
| 首盒 XAML/外观/内容/事件各占多少 | `[启动] 盒子「X」初始化：XAML … / 外观 … / 内容 … / 事件 …` | 决定 §5-B 从哪一段砍 |
| 首帧是否早于玻璃接入 | `[启动] 盒子「X」首帧（ContentRendered）…` vs `[启动] 盒子「X」玻璃背景接入 …` | 决定玻璃延后的收益是"首帧提前"还是"首帧后不卡" |
| D3D11 / D3D9Ex / 着色器编译各占多少 | `[启动] Liquid Glass 引擎初始化 …（D3D11 … / D3D9Ex … / 着色器 …）` | §5-D 的可省上限 |
| 启动段同步写日志总耗时 | `[启动] 启动阶段同步写日志合计 Xms` | §5-G 是否值得改异步 |
| 首个图标就绪时刻 | `[启动] 首个图标就绪，自进程启动 Xms` | 验证 §4.1 的 ~120ms 结论 |
| 启动完成时被桌面盖住的盒子数 | `[启动] 桌面现场：… 被桌面盖住的盒子=N/6` | 确认 z 序竞态在真实开机中的出现率 |
| 开机上下文（Explorer 已运行多久） | `[启动] 开机上下文：… Explorer 已运行 Xms` | 验证 §5-A（Run 排队）在每次开机的占比 |
| 计划任务方案的实际触发时刻 | Shell-Core 9705/9707 + `LogBootContext` 对比 | P0-2 的收益与风险 |

---

## 9. 证据索引

**日志 `dist\deskbox.log`（构建 2026-10-15 23:31）**
- `:266–295` 10-17 08:39 开机启动段（含首盒 1295ms、启动完成 1981ms、玻璃/图标同秒）
- `:299–328` 10-18 10:59 开机启动段（首盒 909ms、启动完成 1653ms）
- `:330–360` 10-18 11:02 暖启动段（首盒 608ms、启动完成 1117ms）
- `:160` 10-15 18:33 首次运行：`[图标] 58 项 … shell 58 / 耗时 311ms`（冷 shell 提取上限样本）
- `:189` 10-15 18:34 `[图标] 58 项 … 磁盘命中 58 / 耗时 20ms`
- `:265` 10-17 00:27:42「桌面挡住 1 个盒子…插回后自校验未通过 → 已退回压桌面」
- `:296` 10-17 18:18:58「桌面挡住 6 个盒子（前台=Progman）…全部已插回桌面之上（自校验通过）」
- `:1–99` 10-14 旧版本「已把桌面压回最底层」刷屏（证明旧策略会闪屏，现策略已收敛）
- 08:39 段**无**「桌面挡住」、**无**「桌面诊断」行（§2.5 核对结论）

**系统事件日志**
- `Microsoft-Windows-Shell-Core/Operational`：9648/9649 `AfterDesktopSwitch`（10-17 08:38:42 / 10-18 10:58:57）、`DesktopStartupApps`（08:38:42→08:39:41 / 10:58:57→10:59:45）、9705/9707/9708（Run 枚举与 `DeskBox.exe --startup` 创建时刻）
- `Microsoft-Windows-Winlogon/Operational`：事件 1/2（10-17 08:38:33 身份验证）；`User Profile Service/Operational` 事件 1/2/5/67（登录/配置加载）
- `Microsoft-Windows-Kernel-Boot/Power`：10-17 08:38:30 唤醒/引导、10-18 10:58:30 唤醒（说明两次样本都接近冷/唤醒后首启）

**源码（HEAD `9ae0cb5`；审计时 Lead 工作树已在改动，行号会漂移）**
- `src/DeskBox/App.xaml.cs`：15/18 常量、53–217 `OnStartup` 顺序、194 前台监听注册、197–199 `_startupComplete`、220–225 `LogStartupStage`、254–283 `WatchForegroundChanges`、289–318 `Request/ScheduleDesktopSweep`、327–401 `RunDesktopSweep`、479–516 `RestoreBoxes/CreateBoxWindow`
- `src/DeskBox/Views/BoxWindow.xaml.cs`：56–90 构造函数、85–89 `AttachWatcher/ReloadItems/SetupTimers`、179–214 定时器、216–261 `SourceInitialized/Deactivated/Loaded`、292–325 `RestoreIfShellHidTheBox`、535–558 `PushDown`、623–742 `ReloadItems/LoadIconAsync`
- `src/DeskBox/Views/BoxWindow.xaml`：6–8 分层透明窗口、106–112 背景/阴影/玻璃层
- `src/DeskBox/Rendering/GlassBackdrop.cs`：25–53 `Loaded→Attach`、79–85 `SetAvailable`
- `src/DeskBox/Rendering/LiquidGlassEngine.cs`：59–103 构造函数+`Attach`、113–181 `Discover/Tick`
- `src/DeskBox/Services/IconLoader.cs`：16–17/69–84 worker、123–167 `LoadAsync`、184–218 `Prewarm`、512–542 批量日志
- `src/DeskBox/Services/TrayService.cs`：12–46 构造、153–200 图标绘制
- `src/DeskBox/Services/Log.cs`：16–37 同步 `AppendAllText`
- `src/DeskBox/Services/SystemIconGuard.cs`：78–145 `EnsureHidden`、49–75 `Reconcile`
- `src/DeskBox/Interop/NativeMethods.cs`：274–280 `PushToBottom`、337–353 `PushDesktopToBottom`、433–455 覆盖判定、488–535 `PlaceBoxAboveDesktop`
- `src/DeskBox/Services/ContextMenuRegistrar.cs`：114–161 `AutoStart`（HKCU Run）
- `README.md`：单文件发布参数（self-contained + PublishSingleFile + 压缩 + IncludeNativeLibrariesForSelfExtract，未开 ReadyToRun）

---

## 10. 推测与不确定项（明确标注）

1. [推测] 首帧时刻 = 启动完成 + 一个渲染周期（16–50ms）；HEAD 没有 `ContentRendered` 埋点，Lead 新埋点可精确确认。
2. [推测] 冷启动首盒 1295ms 的增量主要来自 WPF 首窗口/XAML/JIT、字体缓存、GPU 驱动/玻璃引擎、Defender；具体占比需 §8 埋点确认。
3. [推测] "用户感知的桌面出现时刻"可能晚于 `AfterDesktopSwitch`（图标/壁纸/任务栏就绪），因此"隔几秒"与日志的 38–40s 并不矛盾；需要用桌面可见性探针核实。
4. [推测] 计划任务「登录时」能稳定早于 Explorer Run 枚举（本文未实测）；需 Lead 用 `probe-startup.ps1` + Shell-Core 事件验证。
5. [推测] `%TEMP%\.net\DeskBox` 被清理后的首启解压成本 0.5–2s（未实测）。
6. 工作树中的在研修复尚未提交/构建，本文 §7 只作为审计意见，不构成对最终实现的结论。
