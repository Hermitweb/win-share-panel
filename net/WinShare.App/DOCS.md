# WinShare.App（WPF 壳 + 仪表板）扩展点文档

> T3a 交付。本文档是 T3b/c/d 接入后续模块（共享管理 / 用户权限 / 会话监控 / 服务配置 /
> 应用设置 / 命令面板）的唯一契约参考。

## 0. 目录结构

```
net/WinShare.App/
├── App.xaml(.cs)              组合根：单实例/托盘/Win11/采样器/窗口编排（对齐 electron/main.ts）
├── AppServices.cs             服务定位器（组合根装配；契约服务槽位在此换装）
├── MainWindow.xaml(.cs)       壳：1200×800 主窗口、主菜单、侧栏导航、ContentControl 页宿主、F5
├── Shell/
│   ├── AppShell.cs            生命周期（关闭=托盘常驻 / 退出清理）
│   ├── SingleInstanceGuard.cs 单实例 Mutex + 命名管道激活（对齐 requestSingleInstanceLock）
│   ├── TrayController.cs      WinForms NotifyIcon + ContextMenuStrip（对齐 createTray）
│   ├── Win11Detector.cs       isWin11 对齐（build≥22000）→ DwmBackdrop 亚克力
│   ├── CrashReporter.cs       crashReporter 替代（三面包全落 app.log，UI 线程不崩）
│   └── LogSink.cs             %APPDATA%\WinSharePanel\logs\app.log（对齐 electron logger.logDir）
├── State/
│   ├── AppSettingsStore.cs    C# 版 stateStore（appstate.json 原子写/缓存/容量上限/逐字段收敛）
│   └── AlertEvaluator.cs      C# 版 buildAttentionItems（四类告警纯函数）
├── Events/
│   ├── AppEventBus.cs         线程安全 pub/sub（IAppEventBus）
│   └── AppEvents.cs           事件 payload（ThemeChanged/RefreshRequested/AlertRaised/...）
├── Navigation/
│   ├── NavigationRegistry.cs  IPageFactory/AppPage/注册表（占位替换 API）
│   └── NavigationService.cs   路由切换 + attention 透传
├── Dashboard/
│   ├── DashboardViewModel.cs  全部仪表板界面逻辑（MVVM，不依赖 WPF 控件，xUnit 直测）
│   ├── CsvExporter.cs         CSV 逐字段对齐 TS exportCsv（BOM/引号/列序）
│   ├── TrendSeries.cs         recentHistoryPoints/toTimeSeries 纯算法（含 null 断点）
│   ├── HistoryRingBuffer.cs   288 点环形缓冲
│   ├── ProtocolPalette.cs     协议配色/标签（对齐 PROTOCOL_COLOR/LABEL）
│   └── DashboardDataFeed.cs   IDashboardDataFeed/契约服务馈送/空馈送
├── Services/TrendSampler.cs   5min 后台采样（首采/服务停跳过/失败跳过）
├── Views/
│   ├── DashboardPageView.xaml(.cs)   仪表板页（对齐 Dashboard.tsx 卡片布局）
│   ├── PlaceholderPage.cs            占位页（T3b/c/d 替换目标）
│   ├── Charts/TrendChartControl.cs   24h 趋势自绘（WPF Drawing；渲染器可换）
│   ├── Charts/TopSharesChartControl.cs 热门共享条形图
│   └── Charts/ChartPngCapture.cs    PNG 导出（RenderTargetBitmap+PngBitmapEncoder，pixelRatio 2）
├── Theming/ThemeTokens.{Light,Dark}.xaml + ThemeManager.cs
├── I18n/Strings.{zh,en}.xaml + I18nManager.cs
└── smoke.ps1                  冒烟脚本
```

## 1. 主题 token（Theming）

- 资源字典：`Theming/ThemeTokens.Light.xaml` / `ThemeTokens.Dark.xaml`，
  `ThemeManager.Apply(ThemeMode)` 整组热切换（MergedDictionaries 去旧换新，对齐 TS ThemeProvider 双轨整切语义）。
- 界面一律用 `{DynamicResource <Key>}`，勿自造色值。Key 表：

  | Key | 语义 | Light | Dark |
  |---|---|---|---|
  | BrushBg / ColorBg | 窗体背景（对齐 main.ts backgroundColor #F4FAFD / exportPng dark 背景） | #F4FAFD | #141B23 |
  | BrushCard / ColorCard | 卡片底（antd glass-card 近似实色） | #FFFFFF | #1B2530 |
  | BrushText / ColorText | 主文字 | #1F2733 | #E8EDF4 |
  | BrushFog / ColorFog | 次级文字（45% alpha） | #731F2733 | #73E8EDF4 |
  | BrushPrimary | antd primary #1677FF（趋势连接数线色同源） | #1677FF | #4096FF |
  | BrushSuccess / Warning / Danger | antd 色板（#52C41A/#FAAD14/#FF4D4F 及暗色降饱和） | — | #49AA19/#D89614/#A62635 |
  | BrushBorder | 卡片描边 | #E4E9F0 | #A6525252 |
  | BrushChartBg | 图表背景 + PNG 导出背景（对齐 TS getDataURL backgroundColor 双值） | #F4FAFD | #141B23 |
  | SpacingSm/Md/Lg | 8 / 16 / 24（antd 间距密度） | | |
  | FontSizeCaption/Body/Title/Stat | 12 / 14 / 16 / 28（Statistic 数字 28px） | | |
  | RadiusCard / RadiusBtn | 6px 圆角（antd 默认） | | |

- 通用样式：`CardStyle`（Border 卡片）与 `DashButton`（圆角按钮）定义在 App.xaml，
  后续页面直接 `Style="{DynamicResource CardStyle}"`。
- Win11 检测（`Win11Detector.IsWin11()`，对齐 main.ts isWin11 逻辑 build≥22000）：
  命中时主窗口启用 DWM 系统背景（`DwmBackdrop.TryApply`，对齐 `setBackgroundMaterial('acrylic')`，失败静默回退）；
  背景色板仍走主题 token。
- 主题切换持久化：`ThemeManager` 已绑定 `AppSettingsStore`（写 appstate.json `theme` 键，与 TS 同字段）。

## 2. 事件总线 API（Events）

```csharp
IAppEventBus bus = AppServices.Bus;
IUnsubscribe sub = bus.Subscribe<RefreshRequested>(_ => { /* F5/托盘刷新 */ });
bus.Publish(new AlertRaised { kind = "smb1", level = "danger", text = "..." });
sub.Unsubscribe();
```

- 已定义事件（`Events/AppEvents.cs`，与 Electron IPC 事件面对齐）：
  - `ThemeChanged(ThemeMode)` 主题切换
  - `RefreshRequested()` 全局刷新（F5 / 菜单 / 托盘已接线 → 仪表板 VM 刷新）
  - `AlertRaised { kind, level, text }` 告警规则命中（仪表板刷新时自动发布；托盘气泡已接线）
  - `StateSaved()` 状态持久化完成
  - `NavigationRequested { route, attention }` 跨页跳转（「需要关注」动作按钮已用它）
  - `TrendSampled(HistoryPoint)` 采样成功（服务接上后由采样器发布）
- 语义：同步发布在调用线程；handler 异常隔离（单个 handler 抛错不影响其他订阅者）。

## 3. 导航注册 API（Navigation）——T3b/c/d 占位替换方式

占位路由与 T3 任务对应：

| 路由 | 侧栏标题（i18n key） | 占位页 | 替换任务 |
|---|---|---|---|
| `/` | Dash.Title | **已实现**（DashboardPageView） | — |
| `/shares` | Nav.Share | PlaceholderPage | T3b |
| `/users` | Nav.Users | PlaceholderPage | T3b/T3c |
| `/sessions` | Nav.Sessions | PlaceholderPage | T3c |
| `/settings` | Nav.Settings | PlaceholderPage | T3d |
| `/app-settings` | Nav.AppSettings | PlaceholderPage | T3d |

替换方式（在 `App.RegisterPages()` 中改一行）：

```csharp
// 替换前（占位）
reg.Register(new AppPage { route = "/shares", titleKey = "Nav.Share", glyph = "▤",
    factory = new PageFactory(() => new PlaceholderPage("/shares", AppServices.I18n.T("Nav.Share"))) });
// 替换后（真实模块）
reg.Register(new AppPage { route = "/shares", titleKey = "Nav.Share", glyph = "▤",
    factory = new PageFactory(() => new SharesPageView()) });   // 你的 UserControl
```

- 页面要求：`UserControl`；构造无参（依赖从 `AppServices` 取）；挂上 `Loaded` 后自行拉数据。
- 跳转/定位：`AppServices.Navigation.Navigate("/shares", attention: "disk:E:")`；
  目标页可读 `AppServices.Navigation.TakeAttention()`（一次性取走定位参数，对齐 TS
  `navigate(to, { state: attention })` 语义）。
- 侧栏项显示 = `AppPage.titleKey` 经 `I18nManager.T()` 解析；i18n 缺键回退 key 本身。

## 4. 契约服务接入（组合根扩展点）

当前 `AppServices.SystemService`（`ISystemService` 槽位）为 null：
- 仪表板数据馈送走 `ContractServicesDataFeed(null,...)` → 全 null → 界面如实空态（对齐 TS skippedSources 语义，不崩不臆造）。
- 趋势采样器**未启动**（日志记录「ISystemService 未注册」）。

服务实现（T-core）落地后：

```csharp
AppServices.SystemService = systemServiceInstance;   // 任意时机（OnStartup 前）
```

即自动：① 采样器启动（`SystemServiceTrendSource`：`GetDashboardStatsAsync` →
serviceStatus==Running 才写点；失败/停服跳过不写垃圾 0 点；5min 周期 + 首采一次，
288 点上限由 AppSettingsStore.PushHistory 兜住，对齐 main.ts startHistorySampler）；
② 把 `BuildDashboardFeed()` 换成
`new ContractServicesDataFeed(sys, diskSvc, smbSvc, secSvc)`（可任意子集 null）。

## 5. 图表渲染器扩展点（LiveCharts2.SkiaSharpWPF 接入位）

- 本环境 NuGet 代理**无 LiveCharts2 族包**（flatcontainer 404 / NU1101 实证），
  故 24h 趋势与热门共享条形图用 WPF DrawingContext 自绘
  （`Views/Charts/TrendChartControl`、`TopSharesChartControl`），数据/断点算法全部复用
  纯函数 `Dashboard.TrendSeries`（与 Dashboard.tsx 逐行对齐，含 null 断点）。
- 换用 LiveCharts2(SkiaSharpWPF) 时：保持控件 `Points/Mode/Height`（趋势）与
  `Items/Height`（条形）属性面不变，仅替换 `OnRender` 后端 +
  `ChartPngCapture` 的渲染源；PNG 导出（`RenderTargetBitmap` + `PngBitmapEncoder`，
  pixelRatio=2、背景色随主题）不变。
- 趋势图交互（tooltip/图例点选）当前为只读渲染；如需交互在控件内补（不影响 VM）。

## 6. 状态层 / 告警（State）

- `AppSettingsStore`（`AppServices.SettingsStore`）= C# 版 stateStore：
  文件 `%APPDATA%\WinSharePanel\state\appstate.json`，原子写（tmp+rename）、进程内缓存、
  容量上限（history 288 / journal 200 / pinned 50）、逐字段收敛（对齐 TS normalize）。
  JSON 字段名与 TS 版 appstate.json 逐字段兼容（camelCase；枚举落 TS 小写字面量）。
- 扩展键 `refreshInterval`（1|5|10|30，默认 10）：TS 版没有；仪表板自动刷新间隔用它。
- 告警评估：`AlertEvaluator.Evaluate(input)` 纯函数（四类：SMB1/访客/弱口令/磁盘，
  门控=契约 AlertRules；idleAlertMinutes 有意不参与，对齐 TS「不臆造告警」原则）。
  命中项经事件总线 `AlertRaised` 发布（托盘气泡面已接线）。
- **契约缺口**：`AppStatePatch.alertRules` 为整体 `AlertRules`（无逐键 Partial），
  C# 侧 store 做整体替换 + normalize（TS 是逐键浅合并）；服务层实现需按全量传。

## 7. 可测试性约定

- ViewModel 不依赖 WPF 控件类型（只 `INotifyPropertyChanged` + `ICommand`）：
  `DashboardViewModelTests` 全量 xUnit 直测。
- 界面逻辑 = 纯函数（TrendSeries / CsvExporter / AlertEvaluator / HistoryRingBuffer）
  + AppSettingsStore（目录可注入）：均已覆盖。
- 图表/PNG 捕获在 Views 层（WPF 依赖面），不写单测；换 LiveCharts2 后同一接口面。

## 8. i18n

- `I18n/Strings.zh.xaml`（默认）/ `Strings.en.xaml`；`AppServices.I18n.SetCulture("zh"|"en")`
  整组换字典（DynamicResource 热更）。界面文案一律 `{DynamicResource Key}`；
  代码取值 `I18nManager.T(key)`（缺键回退 key）。键名约定：`Nav.*` / `Menu.*` /
  `Dash.*` / `Placeholder.*`，新页面按 `<页面>.<元素>` 扩展。
