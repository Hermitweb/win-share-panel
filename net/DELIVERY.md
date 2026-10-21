# WinShare Panel · WPF 重构交付说明（UI 层收口版）

> 范围决定：交付止于 UI 层（用户决策「到 UI 即停」）。服务层（T2 全量移植）与 T4 集成验收
> 未做，T4 已移入任务看板 backlog，重启服务层时恢复。

## 1. 交付物

| 层 | 位置 | 内容 |
|---|---|---|
| 契约 | `WinShare.Core/Contracts/` | 20 接口 + 21 DTO（字段 camelCase 对齐 electron/types.ts，逐接口标注 TS 来源） |
| PS 托管 | `WinShare.Core/Ps/` | 进程内 Runspace 池（对齐 powershellPool.ts 全语义）+ PsArgs 注入闸门 + PsRuntimeService |
| lib 层 | `WinShare.Core/Lib/` | Logger/Audit/StateStore/UrlGuard/PsRunner（对齐 electron/lib/*） |
| 杂域服务 | `WinShare.Core/Services/Misc/` | Transfer/Update/PathRegistration（PS 命令串逐字复制自 TS） |
| WPF 壳 | `WinShare.App/`（Shell/State/Events/Theming/I18n/Navigation/Dashboard） | 单实例、托盘、Win11 亚克力、主题双轨、事件总线、24h 采样器、仪表板（自绘趋势/Top5 + PNG/CSV 导出） |
| UI 模块 | `WinShare.App/{Shares,Users,Sessions,Settings}/` + `Views/{Shares,Users,Sessions,Settings}/` | 共享管理 / 用户权限+会话监控 / 协议配置+应用设置，全量对照 src/pages 与 AppSettingsCards.tsx |
| 测试 | `WinShare.Core.Tests`（177）+ `WinShare.App.Tests`（302） | 逐条 [Source: <ts-file>#<case>] 标注，xUnit 全绿 |

**验证基线**（收口日实测）：`dotnet build WinShare.slnx` 0 错；全量 xUnit 479/479；应用启动冒烟 6s 存活。

## 2. 导航接线（已做）

`App.xaml.cs RegisterPages()`：四模块 `SharesModule/UsersModule/SessionsModule/SettingsModule`
的 `Register(reg)` 已接入，占位页按同路由覆盖语义替换。

## 3. 未接入服务的降级口径（如实，不臆造）

- 各模块数据面走「契约空馈送」：服务缺席时界面显示占位/降级说明，不崩。
- 服务层接线位（未来重启 T2 时）：
  - Shares → `Shares/SharesServices` 静态槽（Share/Preset/User/Disk/Protocol）
  - Users/Sessions → `Users/UsersModule.UserService/ShareService`、`Sessions/SessionsModule.SessionService/Adapters`
  - Settings → `Settings/SettingsServiceSlots`（7 槽：Protocol/Update/Firewall/Security/Diagnose/PathRegistration/System）
  - 组合根在 `AppServices`，注入后即全活。

## 4. 已知缺口与替代理由（均有 UI 侧诚实降级）

- 审计日志源 = journal 台账（ISystemService 无 GetAuditLog 契约面）
- 自启 = 登录快捷方式 + appstate.autoStart（TS api.system.autoStart 无契约对应物）
- 协议「停止」按钮 = 禁用 + 契约缺口 tooltip（IProtocolAdapter 无 stop 方法）
- 权限模板/快照历史 Tab 未做（IPresetService/ISmbService 不在 T3d 依赖清单）
- 图表 = 纯 WPF 自绘（LiveCharts2 族包在本机 NuGet 代理不可得，实证 404/NU1101；
  可换渲染器接口 + 接入点在 `DOCS.md` 第 5 节）
- SMB QUIC「本机不支持禁用」未接 OS 探测（QuicHint 文案已落）

## 5. 编译面陷阱备忘（本机 .NET 9.0.19/10.0.13 实测）

- csproj 编译面 hack：SMA 7.5.0 ref 面把 RunspaceBase 标 internal → 走 runtime 程序集
  （`ExcludeAssets="compile"` + 显式 Reference，见 `WinShare.Core.csproj` 注释）。
- 本机 BCL 接口表行为与公开文档出入（string 无 IFormattable、Int32 有）——与 SMA
  混编无关（纯 net9/net10 工程同现，T1P 归因实验①-④）；7 条探针已锁回归网
  （`PsNumberProbeTests.cs`，含快照断言，变红先复判 runtime 版本）。
- `RuntimeHelpers.SetFieldValue` 在 .NET Core 面不存在 → 用 `FieldInfo.SetValue`。
- 测试宿主 %TEMP% 不可写 → 测试 scratch 一律落 `Tests/scratch/`。
- 构建前必 `Remove-Item Env:Platform`（DSH 宿主导出 x64 触发 MSB4126）。

## 6. git 状态

- 本地 main 领先 origin/main 4 个提交（checkpoint / T1P probe / 导航接线 / 终版交付面），**未推远端**。
- 仓库残留（非本任务产物）：`.t2d-logdump.txt`、`dsh-boot-animation*`。
