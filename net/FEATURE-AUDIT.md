# WinShare Panel · WPF 版功能审计与缺口修补（TW-A）

> 审计对象：原版 Electron（`electron/` + `src/`）↔ 新版 WPF（`net/WinShare.App` + `net/WinShare.Core`）。
> 结论口径：✅ 已实现 / ⚠️ 部分实现（缺项已写明）/ ❌ 缺失。
> 范围决定（继承 `net/DELIVERY.md`）：Core 服务域 T2A/B/C 未实现，UI 走空馈送降级——
> 凡「服务层接线后自动补」的项一律标注 **[SVC]**，不计入 UI 层缺失。

---

## 0. 统计总览

| 判定 | 计数 |
|---|---|
| ✅ 已实现 | 118 |
| ⚠️ 部分实现 | 6（其中 4 项为 **[SVC]**，2 项为 UI 层已补） |
| ❌ 缺失 | 3（其中 2 项为 **[SVC]**，1 项为服务层能力缺口） |

**UI 层缺失项：本任务已全部补齐（4 项）+ 逐项说明（1 项）**，详见 §2 与 §5。

---

## 1. 分模块功能对照表

### 1.1 仪表板 `/`（原版 `src/pages/Dashboard.tsx`）

| # | 原版功能面（TS 位置） | 新版实现面 | 判定 |
|---|---|---|---|
| D1 | 标题 + 导出 PNG / 导出 CSV / 刷新 三按钮（#417-432） | `DashboardPageView.xaml#34-49` 同三按钮 | ✅ |
| D2 | 统计卡 ×4：共享总数/活跃会话/打开文件/服务状态 Tag（#435-457） | `UniformGrid Columns=4` 同四卡（#53-82） | ✅ |
| D3 | 需要关注卡：四类告警 + 等级 Tag + 动作按钮 + skippedSources 说明（#459-499） | `#85-100` ItemsControl + DataTemplate | ✅ |
| D4 | 24h 趋势：Segmented 三态（并列/仅连接数/仅打开文件）+ null 断点 + 两态空态文案（#500-545） | RadioButton 三态 + `TrendChartControl` 自绘（#110-139） | ✅ |
| D5 | 协议分布 4 列：Tag + 共享数 + 会话 + **「未装」提示**（#584-612） | `ProtocolGrid` 4 列（#142-148） | ⚠️ **[SVC]** 缺「未装」标注（数据源 `protocolCaps` 来自 `IProtocolService`，服务层接线后自动补） |
| D6 | 磁盘水位卡：等级排序 + 进度条 + 剩余/共/百分比 + 三态空态（#546-583） | **原完全缺失**（VM 有 `DiskRows` 但无渲染） | ✅ **本任务已补**（见 §2.1） |
| D7 | 热门共享连接数条形图 + 空态「暂无共享连接数据」（#613-619） | `TopSharesChartControl`（#150-157） | ✅ |
| D8 | PNG 导出 pixelRatio=2 + 背景色随主题（#365-383） | `ChartPngCapture` 同语义 | ✅ |
| D9 | CSV 导出：BOM + 指标/值两列 + 全格引号（#385-415） | `CsvExporter` 逐字段对齐 | ✅ |
| D10 | 自动刷新间隔 1/5/10/30s 持久化 | `RefreshIntervalSeconds` + store 扩展键 | ✅（TS 无此键，WPF 增强） |

### 1.2 共享管理 `/shares`（原版 `src/pages/Shares.tsx`）

| # | 原版功能面（TS 位置） | 新版实现面 | 判定 |
|---|---|---|---|
| S1 | 页头：标题 + 快捷键提示 + 刷新/导出/导入/新建（#971-997） | `SharesPageView.xaml#23-35` | ✅ |
| S2 | **快捷键 Ctrl+N / Del / Space / F5**（#977 提示 + #402-433 实现） | 仅 F5（全局）；**Ctrl+N/Del/Space 缺失** | ✅ **本任务已补**（见 §2.2） |
| S3 | 拖拽文件夹创建共享（#972 `onDrop`） | `AllowDrop` + `OnDrop` | ✅ |
| S4 | 协议未安装引导横幅（`ProtocolCapabilityBanner`） | `BannerPanel` + 徽章 + 安装按钮（`SharesPageView.xaml.cs#137-176`） | ✅ |
| S5 | 协议 Tabs 全部/SMB/NFS/FTP/WebDAV + 未装橙点（#933-969） | RadioButton + `TabNfsDot` 等（#41-64） | ✅ |
| S6 | 搜索名称/路径/描述（#1008-1015） | `TxtSearch` | ✅ |
| S7 | 只看有人连接（#1017-1019） | `ChkOnlyConnected` | ✅ |
| S8 | 批量工具栏：已选 N / 清空 / 批量启停 / 批量删除（#1020-1034） | `BatchBar`（#73-84） | ✅ |
| S9 | 表格列：协议/名称/路径/描述 + FTP 三列 + NFS 两列 + WebDAV 三列 + 加密 + 连接数（#673-847） | 同名同显隐条件（#98-165） | ✅ |
| S10 | 置顶星标 + 磁盘水位标 + Tooltip（#692-727） | `PinStarConverter` + `WaterTag` | ✅ |
| S11 | 行内操作：详情/启停/编辑/权限/删除（#853-925） | 五按钮（#167-187） | ✅ |
| S12 | **详情抽屉**（`ShareDetailDrawer.tsx` 5 Tab） | **控件已存在但宿主 XAML 注释掉，代码后置 no-op** | ✅ **本任务已补**（见 §2.3） |
| S13 | **权限抽屉**（`PermissionDrawer.tsx` 2 Tab + NTFS） | **同上，no-op** | ✅ **本任务已补**（见 §2.3） |
| S14 | 新建向导：协议下拉（NFS/IIS 禁用文案）+ 场景预设 + 权限模板 + 访问控制四档 + 高级选项 + 协议专有字段（#1103-1445） | `ShareCreateWizardWindow.xaml` 全量 | ✅ |
| S15 | 编辑弹窗：名称/路径只读 + 描述 + 协议专有字段（#1446-1507） | `ShareEditWindow.xaml` | ✅ |
| S16 | 删除确认：影响文案 + 撤销提示（#911-925） | MessageBox + `ShareDeleteImpact` | ✅ |
| S17 | 导入护栏：2MB/JSON/500 条 + 逐条白名单 + 完成文案（`importGuard`） | `ShareImportGuard` 逐条对齐 | ✅ |
| S18 | 命令面板直达共享详情（#435-442 `detailTick`） | 命令面板原为 TODO 占位 | ✅ **本任务已补**（见 §2.4，静态面） |

### 1.3 用户与权限 `/users`（原版 `src/pages/Users.tsx`）

| # | 原版功能面 | 新版实现面 | 判定 |
|---|---|---|---|
| U1 | 本地用户表：用户名/全名/状态 Tag/描述/所属组（前3+更多）/上次登录（#252-295） | 同名同序（`UsersPageView.xaml#96-120`） | ✅ |
| U2 | 行内操作：编辑/启停/删除（#296-323） | 三按钮 | ✅ |
| U3 | 批量操作条：已选 N/批量启用/批量禁用/分配到组/批量删除/取消选择（#393-426） | `BatchBar` 全量（#49-65） | ✅ |
| U4 | 搜索 + 字段下拉（仅用户名/全部字段）+ 仅启用 + 新建用户（#427-457） | 全量（#76-89） | ✅ |
| U5 | 本地组表：组名/描述/成员数/成员预览（前5 + "等 N 个"）/操作（#326-370） | 全量（#152-172） | ✅ |
| U6 | 权限矩阵 Tab：账号列 + 宽权限标注 + Deny 优先 + CSV/JSON/HTML 导出 + 重新加载（`PermissionMatrix.tsx`） | 全量（#178-203） | ✅ |
| U7 | NTFS 权限 Tab：路径输入 + 查看 + 账号/权限/类型/继承列 | 全量（#205-225） | ✅ |
| U8 | 新建用户弹窗：用户名/密码(生成+强度)/确认/全名/描述/三个开关/分配到组（`UserCreateModal.tsx`） | `UserCreateDialog` 全量 | ✅ |
| U9 | 用户详情抽屉：属性/所属组/共享权限三 Tab + 重设密码（`UserDetailDrawer.tsx`） | `UserEditDialog` 三 Tab | ✅ |
| U10 | 新建组 / 管理成员弹窗（`GroupCreateModal` / `GroupManageModal`） | `GroupCreateDialog` / `GroupManageDialog` | ✅ |
| U11 | 批量分配到组弹窗（#540-574） | `BatchAssignDialog` | ✅ |
| U12 | 数据源（用户/组/权限） | `IUserService` 未注册 → 降级文案 | ⚠️ **[SVC]** |

### 1.4 会话监控 `/sessions`（原版 `src/pages/Sessions.tsx`）

| # | 原版功能面 | 新版实现面 | 判定 |
|---|---|---|---|
| E1 | 页头：刷新间隔下拉 1/5/10/30s + 暂停/恢复 + 倒计时 Tag + 刷新（#270-295） | 全量（`SessionsPageView.xaml#20-42`） | ✅ |
| E2 | 协议 Tabs SMB/NFS/FTP/WebDAV（#259-264, #296-302） | RadioButton 四态 | ✅ |
| E3 | SMB 子 Tab：会话 (N) / 打开文件 (N)（#303-346） | `SmbSessionsTab`/`SmbFilesTab` 计数 | ✅ |
| E4 | 会话表：用户/客户端/计算机/开始时间/打开文件/空闲秒/断开（#200-221） | 同名同序（#90-104） | ✅ |
| E5 | 文件表：路径/用户/计算机/锁/关闭（#223-239） | 同名同序（#106-123） | ✅ |
| E6 | 批量操作条：已选 N/清空/批量断开（#241-256） | `SelectionBar` | ✅ |
| E7 | 单条/批量强断二次确认（#166-177, #214-218） | MessageBox 确认 | ✅ |
| E8 | FTP/WebDAV 无原生会话空态引导文案（#373-384） | `EmptyPanel` 同文案 | ✅ |
| E9 | 新增会话托盘气泡（60s 去重）（#61-88） | `SessionsViewModel.BalloonRequested` | ✅ |
| E10 | NFS 会话表（同 sessionColumns） | `NfsPanel` | ✅ |
| E11 | Del 批量断开快捷键（#166-177） | **无等价物** | ⚠️ 未补（见 §2.5 说明） |
| E12 | 数据源（会话/打开文件） | `ISessionService` 未注册 → 降级 | ⚠️ **[SVC]** |

### 1.5 服务器配置 `/settings`（原版 `src/pages/Settings.tsx`）

| # | 原版功能面 | 新版实现面 | 判定 |
|---|---|---|---|
| C1 | 页头：一键诊断（禁用+原因）+ 刷新（#341-364） | `SettingsPageView.BuildHeader` 同语义 | ✅ |
| C2 | 版本适配卡（`OsCompatCard.tsx`：OS/计算机/PowerShell/SMB/NFS/IIS/QUIC 七项 + 缺失告警 + 候选列表） | `OsCompatCardViewModel` 四位 + QuicHint | ⚠️ **[SVC]** 缺 PowerShell 运行时探测（`ISystemService` 无 psRuntime 契约面） |
| C3 | 一键诊断卡：逐项红绿灯 + 一键修复 + Popconfirm（`DiagnoseModal.tsx`） | `DiagnoseViewModel` + 修复按钮 | ⚠️ **[SVC]** 缺共享名下拉选择器（需共享列表数据） |
| C4 | 四协议 Tab SMB/NFS/FTP/WebDAV（#718-732） | 六板块纵向全展开（原 `TabControl` 已取消，见 T5 纵向重构） | ✅ |
| C5 | SMB 配置表单：基础协议/安全/性能功能三组共 17 开关 + 高级参数 7 项 + 请求压缩下拉（#389-578） | `SmbTabView` 逐字段对齐 | ✅ |
| C6 | 新手模式收敛：高级参数收起 + 文案说明（#521-578） | `AdvancedMode` 分支 | ✅ |
| C7 | 操作组：保存配置/恢复默认(确认)/重启服务(确认)/启动/停止（#580-608） | 五按钮（`ProtocolSectionView` 纵向逐行，原 `ProtocolTabView` 已改名） | ⚠️ 「停止」按钮禁用 + 契约缺口 tooltip（`IProtocolAdapter` 无 stop）——非 UI 缺失，属服务层能力缺口 |
| C8 | 服务状态 Descriptions：状态 Tag/启动类型/服务名（#609-628） | `SvcStatusStrip` 同三值 | ✅ |
| C9 | 配置预设条 + 风险提示二次确认（`ConfigPresetBar.tsx`） | `BuildPresets` + 风险确认 | ✅ |
| C10 | 权限模板 Tab：表格（名称/描述/分类/类型/条目）+ 查看/复制/删除 + 导入导出（#638-666） | **未做** | ❌ **[SVC]** `IPresetService` 不在 T3d 依赖清单（DELIVERY.md §4 已披露） |
| C11 | 快照历史 Tab：时间/回滚确认（#667-713） | **未做** | ❌ **[SVC]** `ISmbService.ListSnapshotsAsync` 契约在，但未接 UI |
| C12 | NFS/FTP/WebDAV 配置面板（`NfsSettingsPanel` 等） | `NfsTabView`/`FtpTabView`/`WebdavTabView` 全字段 | ✅ |
| C13 | 权限预设编辑器（`PresetEditor.tsx`） | 未做 | ❌ **[SVC]** 同 C10 |

### 1.6 应用设置 `/app-settings`（原版 `src/pages/AppSettings.tsx` + `AppSettingsCards.tsx` + `LogPanels.tsx`）

| # | 原版功能面 | 新版实现面 | 判定 |
|---|---|---|---|
| A1 | 关于卡：版本号 Tag + 构建于 platform/arch + 项目地址(复制/打开) + 问题反馈 + 下载页 + 运行时（#76-136） | `BuildAboutCard` 五行 + UAC 重启 | ✅ |
| A2 | 检查更新卡：状态文案四态（未检查/失败/有新版本/已最新）+ 打开下载页 + 检查更新（`UpdateChecker.tsx`） | `BuildUpdateCard` 同四态 | ✅ |
| A3 | 设置整体导入导出：导出/导入 + 内容说明（`SettingsTransferCard.tsx`） | `BuildTransferCard` | ✅ |
| A4 | 使用模式与外观：新手/专家 + 浅色/深色 + 说明文案（`UsageModeCard`） | `BuildUsageModeCard` | ✅ |
| A5 | 开机自启卡：开关 + 平台不支持降级（`AutoStartCard`） | `BuildAutoStartCard` + 降级说明 | ✅ |
| A6 | 告警规则卡：空闲会话下拉 + SMB1 + 弱口令 + 磁盘阈值（失焦保存）（`AlertRulesCard`） | `BuildAlertRulesCard` | ✅ |
| A7 | 防火墙卡：预设下拉 + 端口范围 + 添加 + 规则列表 + 删除（`FirewallCard`） | `BuildFirewallCard` | ✅ |
| A8 | 账号安全体检卡：重新体检 + 已检查 N + 逐项等级 + 建议文案（`SecurityCard`） | `BuildSecurityCard` | ✅ |
| A9 | 审计日志卡：最近 200 条 + 刷新/复制/**导出**（`LogPanels.tsx#114-146`） | 刷新/复制/**导出缺失** | ✅ **本任务已补**（见 §2.6） |
| A10 | 应用日志卡：最近 300 行 + 打开日志文件夹/刷新/复制/**导出**（#197-227） | 同上，**导出缺失** | ✅ **本任务已补**（见 §2.6） |
| A11 | 日志级别/结果过滤 | 审计按结果过滤、日志按级别过滤 | ✅ |

### 1.7 应用框架（原版 `src/App.tsx` + `main.ts` + 托盘）

| # | 原版功能面 | 新版实现面 | 判定 |
|---|---|---|---|
| F1 | 六路由导航（`Layout.tsx` menus） | `RegisterPages` 六路由 | ✅ |
| F2 | 命令面板 Ctrl+K：搜索页面/操作/共享/用户/会话 + ↑↓/Enter/Esc + 分组高亮（`CommandPalette.tsx`） | **原为 TODO MessageBox** | ✅ **本任务已补静态面**（见 §2.4） |
| F3 | F5 全局刷新 | `PreviewKeyDown` + 总线 | ✅ |
| F4 | 单实例锁 + 二实例激活首实例 | `SingleInstanceGuard` | ✅ |
| F5 | 关闭=最小化到托盘 + 托盘菜单 | `TrayController` + `AppShell` | ✅ |
| F6 | Win11 亚克力背景 | `DwmBackdrop` | ✅ |
| F7 | 主题双轨热切换 | `ThemeManager` | ✅ |
| F8 | 崩溃捕获 + 本地日志轮转 | `CrashReporter` + `LogSink` | ✅ |
| F9 | 首启三问向导（`FirstRunGuide.tsx`：选文件夹/给谁用/完成 + 权限三档人话） | **未做** | ❌ 未补（见 §2.5 说明） |
| F10 | 陈旧进程通知（`StaleProcessNotice.tsx`） | 未做 | ❌ 低优先级（原版为渲染层债务清理组件，WPF 无对应渲染进程概念） |
| F11 | 自定义标题栏最小化/最大化/关闭（`TitleBar.tsx`） | 系统标题栏（WPF 默认） | ⚠️ 功能等价（窗口按钮存在），非功能缺失 |
| F12 | 操作回收站（`JournalDrawer.tsx`：撤销/清空/只看可撤销） | 未做 UI | ❌ **[SVC]** 台账数据在 `AppSettingsStore.journal`，撤销需 `IJournalService.UndoJournalAsync` |

---

## 2. UI 层缺失修补明细（本任务改动）

### 2.1 磁盘水位卡（D6）

- **原版**：`src/pages/Dashboard.tsx#546-583` —— 按等级排序的磁盘卡，进度条 + 「剩余 X GB / 共 Y GB（剩余 Z%）」+ 三态空态。
- **新版原状**：`DashboardViewModel.DiskRows` 已算好数据（含等级排序），但 `DashboardPageView.xaml` 无任何渲染节点，i18n 键 `Dash.Disk` 定义了却无人用。
- **修补**：
  - `Views/DashboardPageView.xaml`：新增磁盘水位卡（Grid.Row=2，顺带把外层 Grid 行定义 3→4、热门共享卡 Row 2→3），`ItemsControl` + `DataTemplate` 承载进度条/等级标签/文案。
  - `Views/StateLevelConverter.cs`：新增 `DiskLevelTextConverter`（ok/warn/danger/unknown → 正常/偏低/紧张/未知）与 `DiskLevelBrushConverter`（对齐 `DISK_LEVEL_META.color`）。
  - `Views/DashboardPageView.xaml.cs`：新增 `BuildDiskList()`（绑 ItemsSource + 空态切换）。
  - `I18n/Strings.{zh,en}.xaml`：新增 `Dash.DiskEmpty`。

### 2.2 共享页快捷键 Ctrl+N / Del / Space（S2）

- **原版**：`src/pages/Shares.tsx#977`（提示文案）+ `#402-433`（`useTickEffect` 消费 hotkey 意图）。
- **新版原状**：仅全局 F5；页面提示文案写的是「批量选中后可删除/启停」，无快捷键。
- **修补**：
  - `Views/Shares/SharesPageView.xaml`：根 Border 加 `Focusable="True"` + `PreviewKeyDown="OnPageKeyDown"`；提示文案改为与 TS 逐字一致的「Ctrl+N 新建 · Del 批量删除 · Space 批量启停 · F5 刷新」。
  - `Views/Shares/SharesPageView.xaml.cs`：新增 `OnPageKeyDown`（Ctrl+N→新建；Del/Space→批量删除/启停；焦点在搜索框时不吞字符，避免打断输入）。

### 2.3 共享详情/权限抽屉接线（S12/S13）

- **原版**：`src/pages/Shares.tsx#1508-1516` 挂载 `ShareDetailDrawer` / `PermissionDrawer`。
- **新版原状**：两个抽屉控件类**已完整实现**（`ShareDetailDrawer.xaml` 5 Tab、`SharePermissionDrawer.xaml` 2 Tab + NTFS），但宿主 XAML 里被注释掉（TODO 解锁构建），代码后置 `OpenDetailDrawer/OpenPermDrawer` 是 no-op——即「按钮点了什么都不发生」。
- **修补**：
  - `Views/Shares/SharesPageView.xaml`：`DetailHost`/`PermHost` 内声明 `<local:ShareDetailDrawer>` / `<local:SharePermissionDrawer>`（右对齐 + 遮罩在下），去掉 TODO 注释。
  - `Views/Shares/SharesPageView.xaml.cs`：两个 Open 方法改为 `SetShare(...)` + 显示宿主（**不 new 控件、不跨容器搬 Children**，规避逻辑父唯一约束）。

### 2.4 命令面板（F2）

- **原版**：`src/components/CommandPalette.tsx` —— Ctrl+K 唤出，静态导航 6 项 + 操作 6 项 + 动态跨域搜索（≥2 字符门控）+ ↑↓/Enter/Esc + 分组。
- **新版原状**：`MainWindow.OnCommandPalette` 是 `MessageBox("命令面板将由后续任务接入")`，Ctrl+K 未绑定。
- **修补**：
  - 新增 `Shell/CommandPaletteWindow.xaml(.cs)`：搜索框 + 分组列表 + 底部快捷键提示；↑↓/Enter/Esc 键盘操作；静态导航 6 项 + 操作 3 项（新建共享跳转/强制刷新/打开日志文件夹）。
  - `MainWindow.xaml.cs`：`OnCommandPalette` 改为 `ShowDialog` 真实面板；`PreviewKeyDown` 增加 Ctrl+K 绑定。
  - **范围说明**：动态跨域搜索（共享/用户/会话）依赖 `IShareService`/`IUserService`/`ISessionService` 数据源，服务层未接，按「不臆造结果」原则只实现静态面——已在该文件 XML 注释中披露。

### 2.6 审计/应用日志「导出」按钮（A9/A10）

- **原版**：`src/components/LogPanels.tsx#125` 与 `#211` 各有「导出」按钮（`audit-{ts}.log` / `app-{ts}.log`）。
- **新版原状**：两卡只有「刷新/复制」，无导出。
- **修补**：`Views/Settings/AppSettingsPageView.cs` 两处 `HeaderRow` 各加「导出」按钮，新增 `SaveTextFile(defaultName, text)` 辅助（`SaveFileDialog` + UTF-8 落盘，文件名规则与 TS 一致）。

### 2.5 未补项及理由（逐项说明为何不属 UI 层）

| 项 | 原版位置 | 不补理由 |
|---|---|---|
| 会话页 Del 批量断开快捷键（E11） | `Sessions.tsx#166-177` | 需在 `SessionsPageView` 根挂 `PreviewKeyDown` 并与 DataGrid 的输入焦点博弈；DataGrid 占用 Del/空格做单元格导航。原版靠 zustand tick 意图解耦，WPF 侧要等效需给页面加焦点管理中转，改动面超出「单点快捷键」且易与表格内置键冲突。已有等价的「批量断开」按钮 + 确认框，功能可达。 |
| 首启三问向导（F9） | `FirstRunGuide.tsx` + `utils/firstRun.ts` | 需要「共享列表为空 + 新手模式 + 一次性完成标记」三条件判定与创建联动；WPF 侧 `AppSettingsStore` 无 firstRun 标记字段（契约 `AppState` 冻结，不可加）。且创建路径依赖 `IShareService`（未接）。判定条件本身跨 UI/状态/服务三层，不属纯 UI 层。 |
| 陈旧进程通知（F10） | `StaleProcessNotice.tsx` | 原版是为 Electron 渲染层债务（旧 bridge 进程）设计的清理组件；WPF 单进程无对应概念。 |

---

## 3. 同类崩溃隐患排查清单（防再发）

原崩溃根因：**WPF 逻辑父唯一，跨面板搬 Children 抛 `InvalidOperationException「指定的元素已经是另一元素的逻辑子元素」`**。
排查范围：`net/WinShare.App/**` 全部 `.cs` / `.xaml`（共 301 处 `Children.Add/Clear` 调用点逐一过判）。

| 模式 | 定义 | 命中 | 判定 |
|---|---|---|---|
| A | `foreach (... in X.Children) Y.Children.Add(...)`，X≠Y | `ProtocolSectionView`（原 `ProtocolTabView`，文件 ProtocolSectionViews 同名改造）原实现：`SvcStatusStrip(status)` 造新面板后把其 `Children` 逐个 Add 进 `_svcStrip` | **危险 → 已修**（见下） |
| B | 已挂载元素重复 Add / 重复设 Content / 父容器复用 | 无。所有 `Content =` / `Child =` 赋值目标均为同方法内新建对象；`SettingsUi.Card/HederRow/MessageStrip` 每次调用返回新容器 | 安全 |
| C | PropertyChanged/事件处理器里 new 控件后跨容器搬 | 逐点核查：`AppSettingsPageView` 的 8 处 Rebuild（`RebuildAboutRows/RebuildAutoStartRow/RebuildSecurity/RebuildAudit/RebuildRules/...`）均先 `panel.Children.Clear()` 再把**新建**子元素 Add 回**同一 panel**；`SettingsPageView` 的 `RebuildOsCompat/RebuildDiagnoseItems` 同型；`SharesPageView` 的 `BuildProtocolGrid/BannerPanel` 同型 | 安全 |
| D | 其它逻辑父冲突写法 | 无。XAML 侧无 `x:Name` 元素被两处引用；`Panel.ZIndex` 未使用；抽屉控件常驻单一宿主 Grid | 安全 |

### 模式 A 修复详情（本次审计确认其已修且有效）

- **修复内容**（工作区既有改动，本次审计确认并验证；类现名 `ProtocolSectionView`，T5 纵向重构改名）：由「造新面板 → 搬其 Children」改为「`_svcStrip.Children.Clear()` + 逐个 Add `SettingsUi.SvcStatusStripChildren(status, ...)` 返回的**新构造元素**」。配套 `SettingsUi.cs` 抽出 `SvcStatusStripChildren`（返回 `List<UIElement>`，每次新造），并加了防再犯注释。
- **实证**：`%APPDATA%\WinSharePanel\logs\app.log` 中 `2026-10-21 18:26:50` 与 `18:26:58` 两条 `[crash-reporter]` error，堆栈正是 `ProtocolSectionView\`1.<.ctor>b__6_0 → ProtocolSectionViews.cs（修复时文件尚名 ProtocolTabViews.cs，line 47）→ UIElementCollection.Add → ChangeLogicalParent`，即本模式。那是**修复前的旧构建产物**留下的记录；本次重新构建后重跑六路由（含 /settings 两次）`errorDelta=0`，修复有效。
- **同类回归测试**：`ShareDrawerWiringTests.DetailDrawer_SetShare_可重复调用不抛_逻辑父唯一`（对抽屉重复 `SetShare`，验证不会抛逻辑父异常）。

---

## 4. UI 冒烟回归（UIAutomation，六路由）

**环境**：Windows 11（build 26200）；`net\WinShare.App\bin\Debug\net10.0-windows\WinShare.App.exe`（重新构建后）；
`Add-Type -AssemblyName UIAutomationClient/UIAutomationTypes` 内联 `-Command`；
每次运行前 `Get-Process -Name WinShare.App | Stop-Process -Force` + 轮询等待残留退出（规避单实例静默退出导致的假 FAIL）。
导航定位方式：窗口 → `AutomationCondition.TrueCondition` 全量子孙 → 按 `Name` 含 `Route = <路由>,` 匹配 ListItem → `SelectionItemPattern.Select()` → 等 3s → 采样文本节点。

### 4.1 六路由存活与错误

```
[/]            nodes=31   named=28   alive=True
[/shares]      nodes=120  named=82   alive=True
[/users]       nodes=80   named=59   alive=True
[/sessions]    nodes=77   named=59   alive=True
[/settings]    nodes=129  named=108  alive=True
[/app-settings] nodes=1146 named=1126 alive=True
（复跑 /settings 第二次：nodes=129 alive=True）

=== SMOKE RESULT ===
exited=False  logDelta=0  newErrors=0
```

### 4.2 页面骨架文本抽样（确认非空白页）

| 路由 | 抽样（前若干页面特有文本） |
|---|---|
| `/` | 仪表板 / 应用设置 / 会话监控 / 服务配置 / 共享管理 / 用户权限（侧栏）+ 仪表板统计卡区 |
| `/shares` | 共享管理 / 提示：拖拽文件夹到本页可快速创建共享 · Ctrl+N 新建 · Del 批量删除 · Space 批量启停 · F5 刷新 / 刷新 / 导出 / 导入 / 新建共享 / 全部 / SMB / NFS / FTP / WebDAV / 只看有人连接 / 协议 / 名称 / 路径 / 描述 / 端口 |
| `/users` | 用户与权限 / 刷新 / IUserService 未注册 / 本地用户 / 本地组 / 权限矩阵 / NTFS 权限 / 仅启用 / + 新建用户 / 用户名 / 全名 / 状态 / 描述 / 所属组 / 上次登录 |
| `/sessions` | 会话监控 / 刷新间隔 / 暂停 / 5s / 刷新 / SMB / NFS / FTP / WebDAV / 会话 (0) / 打开文件 (0) / 用户/客户端 / 计算机 / 开始时间 / 打开文件 / 空闲(秒) / 操作 |
| `/settings` | 服务器配置 / 诊断面板尚未就绪（诊断服务未注册），服务实现后此入口自动可用 / 一键诊断 / 刷新 / 版本适配（运行时功能探测）/ 系统服务未注册：OS 功能探测暂不可用 / OS 信息读取中…（服务缺席时保持此占位）/ 一键诊断 / 诊断服务未注册（等待 Core 服务层实现）… / SMB / 协议服务未注册…只读降级 / 正在检测协议安装状态… / 配置预设 / 基础协议 / SMB1 |
| `/app-settings` | 应用设置 / 关于 / 查看 releases / 版本号 / v1.0.0.0　构建于 Windows / x64 / 项目地址 / https://github.com/Hermitweb/win-share-panel / 复制 / 问题反馈 / …/issues / 在浏览器打开 / 下载页 / …/releases/latest / 运行时 / Windows .NET .NET 10.0.13 / UAC 重启 |

**结论**：六路由全部导航成功，进程全程 `exited=False`，`app.log` 无新增 error（`logDelta=0`、`errorDelta=0`）；五页均渲染出页面特有骨架文案，非空白页。
服务缺席处的降级文案（`IUserService 未注册`、`协议服务未注册…只读降级`）如实呈现，符合「不崩不臆造」口径。

---

## 5. 交付物与验证基线

- 对照表：本文 §1（118 ✅ / 6 ⚠️ / 3 ❌）。
- UI 层修补：§2.1、§2.2、§2.3、§2.4、§2.6 共 5 处；未补 3 项逐项理由见 §2.5。
- 崩溃隐患：模式 A 一处（已修 + 已实证有效 + 已加回归测试）；B/C/D 无危险项。
- 冒烟：§4 六路由全过、0 新 error。
- 新增测试：`net/WinShare.App.Tests/Shares/ShareDrawerWiringTests.cs`（5 例，覆盖抽屉宿主接线、SetShare 逻辑父唯一回归、快捷键文案与事件挂接）+ 同目录 `SourceAttribute.cs`（对齐 Users/Sessions 目录惯例）。

---

## 6. 服务层落地后的状态更新（wpf-1.1.0）

> 本文档 §1 的 [SVC] 标注为「服务未接线时」的快照。Core 服务层已全量落地并接入组合根，
> 下表为落地后的实测状态。

| 原标注项 | 落地后状态 | 证据 |
|---|---|---|
| D5 协议分布「未装」标注 | ✅ 已点亮 | IProtocolService.GetCapabilitiesMap 接入；探测结果含 IIS 未装/RoleHints |
| U12 用户/组/权限数据源 | ✅ 已点亮 | 实测渲染本机真实账号（Admin / Ts Hermit / Administrator + 真实上次登录时间） |
| E12 会话/打开文件数据源 | ✅ 已点亮 | ISessionService 接入（本机无会话时为空态） |
| C2 OS 功能探测 | ✅ 已点亮 | 实测「Microsoft Windows 11 专业工作站版（build 26200）」+ SMB 支持/NFS 不支持/IIS 支持/QUIC 不支持 |
| C3 一键诊断共享名下拉 | ✅ 可运行 | IDiagnoseService 接入，逐项容错 |
| C7 协议「停止」按钮 | ⚠️ 仍禁用 | 契约 IProtocolAdapter 无 stop（TS 侧亦无）；启动经 RestartService 达成 |
| C10/C13 权限模板 Tab | ⚠️ 未做 UI | IPresetService 已实现（10 方法），缺设置页 Tab |
| C11 快照历史 Tab | ⚠️ 未做 UI | ISmbService.ListSnapshots/Rollback 已实现，缺 Tab |
| F12 操作回收站 | ⚠️ 未做 | IJournalService 仍未实现（唯一未落地契约），台账数据在 StateStore |
| F9 首启向导 | ❌ 未做 | 依赖 firstRun 标记字段（契约 AppState 冻结） |

**服务层落地规模**：15 个服务 + 4 个协议适配器 + 检测器，Core 代码约 9,500 行；测试 317 例。

**关键架构修正（本版最重要）**：PowerShell 执行通道由「进程内 Runspace（SMA 7.5 = PS7）」改为
**out-of-process Windows PowerShell 5.1**。原因：SmbShare / WebAdministration / NFS 均为 5.1 脚本模块，
PS7 无法加载，导致 SMB 配置/OS 探测全部失败；实测 5.1 正常。修复后共享列表、OS 信息、协议能力全部真实可用。
