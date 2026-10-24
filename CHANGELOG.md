# Changelog

本文件记录 WinShare Panel 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]（v1.4.2 开发中）

### 🐛 修复 / Fixed：一键注册 PATH 的三处真实缺陷（v1.4.1 引入，v1.4.2 修正）

本轮对全仓做了一次带门禁的体检，发现 v1.4.1（commit `a2497be`）引入的"一键注册/撤销 PATH"
功能带着三个真实缺陷——其中两个当时只是**静默失效**，一个是**必然抛错**。
其中前两个在 **WPF 移植版（.NET）里当时已是正确写法**（移植时已修正，只是没回修回 TS 原版），
第三个两侧同病；本轮把 TS 侧补齐，并让两侧口径完全一致。

- **撤销 PATH 必然抛 `SyntaxError`（`electron/services/pathRegistration.ts` 段匹配正则）**：
  过滤用的正则尾部写成 `(?:"|\\)`，少了一个引号 —— 模板字面量求值后变成 `(?:"|\)`，
  `\)` 被当成转义字面量括号，**分组永不闭合**，`new RegExp()` 直接抛
  `Invalid regular expression: ... Unterminated group`。
  后果：`unregisterAppFromUserPath()` **每次调用都失败**（打包版点"撤销 PATH 注册"必报错）。
  修正：两侧统一为 `^"?<转义后的目录>"?$`（段可能带引号，故两侧各允许一个可选引号）。
  注：该路径当时**渲染层没有任何调用点**，属潜伏缺陷而非线上事故；通道一旦接上 UI 即爆。
- **注册表路径的反斜杠被 JS 吞掉（`readServicePendingMarker` 恒为 false）**：
  命令串里写的是 `HKLM:Software\WinSharePanel`——JS 字符串字面量中 `\W` 是**无效转义，
  反斜杠被直接丢弃**，实际发给 PowerShell 的是 `HKLM:SoftwareWinSharePanel`，
  那是**另一个不存在的键**，`-ErrorAction SilentlyContinue` 静默吞掉 → 服务标记永远读不到。
  修正：写成 `HKLM:Software\\WinSharePanel`。
  （C# 侧 `\W` 是**编译错误**，所以 .NET 版天然免疫——这也是它没带这个 bug 的原因。）
  ESLint 的 `no-useless-escape` 早已指出过这一行：那条告警不是噪音，指向真实的语义错误。
- **目录路径未做正则元字符转义（TS 与 .NET 同病）**：只处理了反斜杠，路径里的 `( ) . [ ]` 等
  仍被当成正则语法。装到 `C:\Program Files (x86)\WinShare Panel` 时，`(x86)` 变成捕获组，
  真实段匹配不上，反而匹配到形似但不同的 `C:\Program Files x86\WinShare Panel` →
  **撤销 PATH 会移除错段、留下真段**。修正：两侧统一加 `escapeRegExpLiteral()`
  （TS 正则字符集 / C# `Regex.Replace` 同规则）。

### ✨ 新增 / Added：系统注册项的 UI 入口（补上 v1.4.0 留下的空洞）

v1.4.0 把 `system:registerPath` / `system:unregisterPath` / `system:pathRegistered` /
`system:servicePending` 四个 IPC 通道与 preload 封装都做完了，但**渲染层一个调用点都没有**——
通道建好却没有任何界面能用，用户无法自助把应用目录加进 PATH。

- **`src/components/AppSettingsCards.tsx` 新增 `SystemRegistrationCard`**（挂在「应用设置」页）：
  展示当前是否已注册到用户级 PATH，一键注册/撤销（撤销带二次确认），并说明
  "写的是 HKCU\Environment\Path、无需 UAC、写入后广播环境变量变更通知"。
- **诚实降级**：开发模式下入口**禁用**并写明原因（写进 PATH 的是源码目录，没有意义）；
  广播失败按 warning 提示"需重开终端/登录"，不谎报成功；主进程**不回传完整 PATH**（只回长度）。
- **`AppInfo` 增加 `isPackaged`**（`electron/types.ts` + `system:appInfo`）：渲染层据此判断开发模式。
- **`src/types.ts` 补上 `PathRegistrationResult` 与四个通道的类型声明**。
- 新增 6 例 `AppSettings.test.tsx` 用例（注册/撤销/广播失败/调用失败/开发模式禁用/服务标记说明）。

### 🔧 修复 / Fixed：质量门禁回归（CI 此前是红的）

`docs/audit/00-backlog.md` §3.5 记载的"终态六门禁逐条 exit 0"已不成立——本轮体检时
lint / format:check / test 三门同时为红。逐项修复后恢复全绿：

- **lint 红**（唯一 1 条）：`pathRegistration.ts` 的 `\W` 无用转义（见上）。
- **format:check 红**（11 个文件）：`prettier --write` 一次修正。
- **test 红**（3 恒红 + 1 负载抖动）：
  - `pathRegistration.test.ts`「PATH 广播失败降级」用例的 mock **打错了目标**：
    `mockImplementationOnce` 命中的是第 1 次 void 调用——即 `setUserPath` 写入，而非广播
    （调用序：读 PATH → 写入 → 广播）。`setUserPath` 没有 try/catch，错误直接冒泡，用例恒红。
    修正：改为按调用序两段式（第 1 次写入成功、第 2 次广播抛错）。
  - `nativePaths.test.ts` 断言过严：要求"所有绝对路径候选都在 Sysnative 视图"，
    但候选③ `%ProgramFiles%\PowerShell\7\pwsh.exe` **按设计**就在该视图之外
    （Program Files 不经 Sysnative 镜像，且 32 位进程下取的是原生 64 位 PF，本就同位宽）。
    修正：断言改为"System32/Sysnative 视图内的两个候选走 Sysnative + PF 候选单列"。
  - `powershellPool.stress.test.ts` 场景 2 的**负载敏感断言抖动**（本次专项根治，见下）。
- **补 `.NET` 侧 CI 门禁**（此前 CI 只跑 TS，"CI 绿灯"不代表 WPF 侧也绿）：新增独立 job，
  跑 `dotnet build` + `dotnet test`。

### 🔧 修复 / Fixed：压力测试的负载敏感断言（专项根治，非放宽断言）

`electron/lib/powershellPool.stress.test.ts` 场景 2（100 并发含 10 条卡死命令）在全量跑 +
覆盖率插桩的负载下会间歇红（`fulfilled` 期望 90 实得 81~87）。**根因已定位**（四组对照实验）：

| 配置 | 快命令误拒数 |
| --- | --- |
| 基线：统一 25ms 超时 / 6ms 响应 / 事件循环卡顿 40ms | **18** |
| 宽裕阈值：统一 1000ms 超时 / 6ms 响应 / 卡顿 40ms | 0 |
| 零延迟响应：统一 25ms 超时 / 0ms 响应 / 卡顿 40ms | 0 |
| 无卡顿 | 0 |

即：正常命令的桩响应只要 6ms，统一 25ms 超时只留 19ms 余量；事件循环一卡顿，
**Node 的 timer 按"时长分桶"管理、每桶 expiry 取桶内最早者，跨桶处理顺序可能先跑 25ms 桶
再跑 6ms 桶** → 正常命令被误判超时、worker 被多 kill。生产侧 deadline 是 10s 级、PS 实际响应
百毫秒级，余量 100 倍以上，**这是测试桩的时间参数不贴近生产造成的假红，不是池实现的缺陷**。

修正（不改任何断言、不放宽语义）：场景 2 按"这条命令会不会卡死"给不同超时值——
卡死的 10 条仍用 25ms（就该被超时 kill，验证中毒重启），正常的 90 条用 2000ms（贴近真实调用方）。
实测 40ms 与 120ms 卡顿下均为 **0 误拒、0 多余 kill**。

### 🔧 修复 / Fixed：.NET 构建在装有 VS 的 Windows 上直接失败（MSB4126）

装了 Visual Studio 的 Windows 机器普遍带有环境变量 `Platform=x64`，而 `.slnx` 默认只提供
`Any CPU` 一套配置 → `dotnet build net/WinShare.slnx` 直接以
**MSB4126「指定的解决方案配置"Debug|x64"无效」**失败。修正：`net/WinShare.slnx` 显式登记
`Any CPU` 与 `x64` 两个平台；另加 `global.json` 固定 SDK 大版本（10.0.x），
并补 `pnpm build:dotnet` / `pnpm test:dotnet` 两个入口。

### 🧹 技术债清零 / Debt：工作区残留与构建告警

- **`.gitignore` 补齐**并清理约 **110MB** 未跟踪残留（`.repro/ProbeDisk` 73MB、
  外部会话产出的 `dsh-boot-animation-*` 38MB、截图目录等）。
  其中 `.repro/` 曾累积 70MB+ 的 .NET 探针构建产物，一次 `git add -A` 就会把它们带进仓库。
- **`DiagnoseModal` 静态+动态重复导入告警消除**：`SharesWithOps.tsx` 原为静态 import，
  而 `pages/Settings.tsx` 用 glob 动态 import → Vite 报
  "dynamically imported ... but also statically imported"（动态导入被静默失效，
  模块被拖进首屏主 chunk）。改为 `React.lazy` + 常驻 `Suspense` 边界后真正分包，
  且**保持"常驻挂载 + open 受控"语义**（关闭动画与 open 转换时的状态重置仍由组件内部负责）。

### 🔧 修复 / Fixed：DOM 组负载敏感的间歇红（并发超订）

提交后终验时 `test:coverage` 出现 4 例失败、而同一棵树的普通 `test` 与随后的覆盖率重跑均全绿
（755/755）—— 属**随负载漂移的间歇红**，与 backlog 里此前登记的"负载敏感断言抖动"同类，
但根因这次更明确：**并发超订**。

本机 16 逻辑核 / 8 物理核，`vitest` 默认按 CPU 数并发，27 个 DOM 文件（antd + jsdom 重型渲染）
全并行 → 每个 worker 只分到约半个物理核 → 异步等待预算（RTL `asyncUtilTimeout` 10s /
`testTimeout` 30s）偶发击穿。

修正（**不改任何用例、断言与等待时长**）：`vitest.config.ts` 给 DOM 组加 `maxWorkers: 4`
（vitest 4 要求各 project 的 `maxWorkers` 不同时必须给各自的 `sequence.groupOrder`，
故 node 组补 `groupOrder: 1`、dom 组 `groupOrder: 2`），node 组（纯逻辑、轻量）保持全并行。
CI（windows-latest 4 核）上默认并发本就是 3~4，本设置对其无影响，只修多核开发机的超订。

**稳定性与耗时实测**：连跑 3 遍 `test:coverage` 全部 755/755 全绿，
墙钟 **175s / 133s / 135s** —— 反而比加限流前（192~271s）**更快**（减少了并发争抢开销）。

另把 node 组挂着的 `retry: 1` **摘掉**：该 retry 原是给压测用例吸收负载敏感抖动的，
而那个抖动已在其自身文件里根治（按命令是否卡死分别给超时值）；留着它会让
"首次失败、重试通过"的用例被记为**通过**，反而可能掩盖新引入的间歇性回归。
摘掉后连跑 2 遍 `test:coverage` 仍全绿（墙钟 122s / 122s）。
**合计：修复后连续 5 遍 `test:coverage` 全绿。**

另修一处**桩不完整**导致的隐性开销：`src/pages/Settings.test.tsx`（约 30 处渲染「应用设置」页）
与 `src/test/renderer-debt.verification.test.tsx` 的 `api.system` 桩没有 `pathRegistered` /
`registerPath` / `unregisterPath` / `servicePending` / `appInfo`，
新卡片落地后每次渲染都会产生 5 个查询失败（queryFn 抛 TypeError）→ 错误态 + 额外重渲染。
现已补齐。

### 📝 文档 / Docs：WPF 侧 `DOCS.md` 从 T3a 老快照更新到实况

`net/WinShare.App/DOCS.md` 停在 T3a 交付时的快照，多处**已被推翻的"事实"**：

| 文档原文 | 实况 |
| --- | --- |
| 路由表：`/shares` `/users` `/sessions` `/settings` `/app-settings` 五条都挂 `PlaceholderPage` | T3b/c/d 已全部交付，六条路由都由各自模块的 `Register` 直接注册真实页面 |
| §4「当前 `AppServices.SystemService` 槽位为 null」「趋势采样器**未启动**」 | 组合根 `Composition/ServicesComposition.cs` 已装配 **15 个 Core 契约服务**并注入 UI 静态槽，仪表板馈送与趋势采样器都是活的 |

已按实况重写 §0（目录结构）、§3（导航注册 API）、§4（契约服务接入），
并修正 `App.xaml.cs`（「仪表板（本任务模块）+ 5 占位（TODO T3b/c/d）」等过期注释与日志文案）
与 `Views/PlaceholderPage.cs`（明确标注为历史脚手架、当前无路由引用）。

### 📝 文档 / Docs：README 截图占位改为可执行规格

原为模糊的「📸 截图占位 / Screenshots placeholder（待补充 / TBD）」。
现有素材逐张看过——本地 `ui-shot-*.png` 全是**空态仪表板**、首屏加载态、
UI 重设计**之前**的旧版、以及一张 Chromium 的下载弹窗，没有一张能代表产品，硬贴反而失实。
故改为可执行规格：整图 ≈1250×860、浅色主题、**必须含真实数据**（共享数/连接数非空、
共享管理列表非空），素材到位后放 `docs/screenshots/`。

### 🔧 修复 / Fixed：另一台机器安装后 PowerShell 失效导致应用不可用

- **PowerShell 运行时探测 + 失败诊断（`electron/services/psRuntime.ts` 新增）**：
  背景——一台 Win10/11 x64 机器上安装后应用"直接不可用"，根因是 `nativePowerShellExe()` 解析出的
  `powershell.exe` 路径**不存在或无法启动**（系统被精简工具删除 / 杀软或 AppLocker 拦截 spawn
  / 组策略禁用），而 `powershell.ts` 与 `powershellPool.ts` 对此**零感知**——所有共享/会话/用户
  功能全瘫，且无任何日志/提示告诉用户"是 PowerShell 本身出了问题"。
  本模块在应用启动时**尽早**做一次存在性探测（一次 `stat`，不 spawn PowerShell，廉价），
  把结果缓存并写进应用日志（`app.log` 里 `[psRuntime]` 前缀，便于事后追溯"这台机器装完就能
  看到 powershell.exe 不存在"）；并暴露 `system:psRuntime` / `system:psDiagnose` 两个 IPC 通道，
  渲染层「系统环境」卡（`OsCompatCard`）读取后展示"PowerShell 可用/缺失"，缺失时点「诊断」
  给出可执行修复建议（`sfc /scannow` / 杀软白名单 / 以管理员运行 / 系统恢复）。
  设计原则与 `nativePaths.ts` 一致：不改 PowerShell 解析策略（`nativePaths.ts` 是唯一真相源），
  只做**存在性 + 诊断建议**两条增量，避免两处逻辑漂移。`electron/services/psRuntime.test.ts`
  用注入的 `exists/stat` 固化"存在/缺失/stat 抛错/裸名/force 重探"5 个分支与 4 类诊断建议。

### ✨ 新增 / Added：安装器注册项完善 + 系统级一键注册（v1.4.0）

- **NSIS 安装器钩子（`resources/installer-hooks.nsh` 新增，`electron-builder.yml` 经
  `nsis.include` 注入）**，安装时写入 4 条注册项：
  1. **ARP 程序列表增强**（`HKLM\...\Uninstall\WinSharePanel`）：electron-builder 自带基础条目，
     本钩子补 `NoModify=1` / `NoRepair=1`（控制面板"程序和功能"里禁用"修改/修复"两个对单文件
     应用无意义的按钮）
  2. **登录快速启动**（`HKCU\...\Run\WinSharePanel = <dir>\WinShare Panel.exe --silent`）：
     登录时静默拉起（主进程单实例锁让第二个实例激活第一个，`main.ts` 的 `second-instance`
     处理器已就绪）；卸载时由 `un.onUnInst` 删除
  3. **Windows 服务预留标记**（`HKLM\Software\WinSharePanel\ServicePending=1`）：v1.4.0 暂不
     创建服务本体（与 GUI 应用形态冲突：服务跑在会话 0，无法显示窗口；真正做服务需 NSSM/
     计划任务包装器，留待后续版本），只留个标记供 UI 查询（`system:servicePending` IPC 通道），
     为"一键注册为系统服务"预留 UI 入口
     （v1.4.2 已在「应用设置」页接上该查询入口；标记本体仍只是提示，不触发任何动作）
  4. **系统 PATH 不默认写**（跨权限 + 卸载残留风险）：改由主进程
     `electron/services/pathRegistration.ts` 的"一键注册/撤销"入口写入**用户级**
     `HKCU\Environment\Path`（不需要 UAC 提权，与单用户工具形态匹配），写后广播
     `WM_SETTINGCHANGE` 让本会话终端立刻拿到新 PATH（PS 缺失时静默降级为"需重开终端"并
     在结果里注明）；卸载时由 `un.showPathHint` 弹消息框提示用户手动确认清理（NSIS 卸载器
     无法精确判断 PATH 里哪一段是本应用加的，避免误删用户自己配置的路径）
  配套 IPC 通道（`electron/ipc/index.ts` + `electron/preload.ts` + `src/types.ts`）：
  `system:registerPath` / `system:unregisterPath` / `system:pathRegistered` /
  `system:setLoginShortcut` / `system:getLoginShortcut` / `system:servicePending`；
  单测 `electron/services/pathRegistration.test.ts` 用 `vi.mock('../lib/powershell')` 固化
  幂等写入/撤销/广播失败降级/服务标记读取 4 类路径。

### ✨ 新增 / Added：PowerShell 多版本兼容（5.1 + 7.x 自动识别）

- **`electron/lib/nativePaths.ts` 重写为多版本兼容解析层**：
  旧实现只解析 `System32\WindowsPowerShell\v1.0\powershell.exe`（5.1）一个路径，
  该路径缺失时直接返回不存在的路径 → `spawn ENOENT` 0x80070003，应用全瘫。
  新实现按「5.1 优先，缺失兜底 7.x」的优先级枚举候选：
  ① 原生位宽 `powershell.exe`（5.1，32 位进程经 Sysnative 取原生 System32）
  ② `System32\WindowsPowerShell\v1.0\pwsh.exe`（PS7 默认注册位置）
  ③ `%ProgramFiles%\PowerShell\7\pwsh.exe`（winget / 手动安装常见位置；
  32 位进程下用 `ProgramW6432` 取原生 64 位 Program Files，规避 WOW64 重定向）
  ④ 裸名 `powershell.exe` / `pwsh.exe`（PATH 兜底，跨平台 / 自定义安装位置）。
  只有 5.1 缺失（精简系统）时才落 7.x——5.1 可用时不冒险切引擎
  （本项目全部 cmdlet 按 5.1 调试验证过，IIS COM 组件仅 5.1 注册）。
  `listPowerShellCandidates()` 返回全部候选（带 exists 标记）供日志/诊断，
  `nativePowerShellExe()` 契约不变（永非空，ENOENT 由 spawn 阶段暴露，
  `psRuntime.diagnosePsRuntime()` 负责翻译）。
  新增 `realPathFound` 语义（PsRuntimeStatus）：裸名 PATH 兜底不算"找到了
  PowerShell"——精简系统 5.1 与 7.x 全缺时 `resolvedPath` 回退裸名、`exists`
  乐观为 true，但 `realPathFound=false`，诊断据此判定"双引擎全缺"而非误报"已找到"。
- **`electron/lib/psRuntimeProbe.ts` 新增**：启动时非阻塞探测 `$PSVersionTable.PSVersion`
  （不阻塞首屏），把引擎大版本（5/7）与完整版本号写 `app.log`；7.x 时额外写一行
  「IIS COM 仅在 5.1 注册，FTP/WebDAV 管理可能不可用」提示。
- **`powershell.ts` / `powershellPool.ts` 自动继承多版本解析**：两者都经
  `nativePowerShellExe()` 取可执行文件，无需改动调用方；`formatPsError` 新增
  ENOENT/0x80070003 的可读归一化（指向诊断卡）。
- **`diagnose.ts` 的 `Get-WmiObject` 改 `Get-CimInstance`**：跨引擎兼容
  （7.x 的 WMI 兼容垫片在部分精简系统会失败，CMI 更稳）。
- **`docs/COMPATIBILITY.md` 新增 §5「PowerShell 版本矩阵」**：引擎识别优先级表、
  引擎 × 功能支持矩阵（5.1 / 7.x 各能力对照）、机器形态 × 行为、版本探测日志说明、
  多版本手工回归矩阵。

### 📄 文档 / Docs

- `docs/DEVELOPMENT.md` 补「安装器注册项与一键注册」一节，说明 4 条注册项的键名/清理时机/
  与 v1.4.0 服务预留的关系；`README.md` 的「快速开始」补一段"若 PowerShell 失效怎么排查"
  （指向 OsCompatCard 的诊断入口 + `sfc /scannow`）

## [1.3.0] - 2026-10-10

### 🖥 三架构发布（x86 / x64 / arm64）与架构对齐

- **安装包按架构区分并以标识命名**：`WinShare.Panel-<版本>-Setup-{x86,x64,arm64}.exe`（+ 对应的 `-portable-<架构>.exe`）。32 位对外写 **x86**（不再沿用 Electron 内部 id `ia32`）。命名由 `scripts/build-win.mjs` 注入 `WINSHARE_ARCH_LABEL` 生成，裸跑 electron-builder 会因该变量未定义而明确报错，不会产出错命名
- **逐架构单独打包，不再产出"胖"安装包**：原先一次调用给 `arch: [x64, ia32]`，NSIS/portable 会产出**同时内嵌两套 Electron** 的 196 MB `Setup.exe` / `-portable.exe`（实测与 101 MB / 96 MB 的单架构包并存）。现在每个架构单独调用 electron-builder 并各自输出到 `release/arch-<label>/`，再由脚本收敛产物 —— 用户只下载自己那套
- **PE 头架构硬校验**：每个架构打包后读取解包主程序 PE `Machine`（`0x014c`=x86 / `0x8664`=x64 / `0xaa64`=arm64），与标识不一致即构建失败。**宁可构建失败，也不发出名字写 arm64、里面装 x64 的包**
- **arm64 新增支持**：NSIS `Setup-arm64.exe` 与 `portable-arm64.exe` 原生 arm64；**arm64 不提供 MSI** —— electron-builder 25 内置的 wix 4.0.0.5512.2 不支持 arm64，会把请求静默降级成 x64 MSI（其源码注释即写明"results in an x64 MSI installer that installs an arm64 version"），那等于给用户一个架构标识错误的安装包
- **体积优化（实测取舍）**：
  - **`app.asar` 去掉整棵生产依赖树**：`externalizeDepsPlugin()` 让 electron-builder 把 `package.json` 的 `dependencies` 全树塞进 asar —— 实测 **147.9 MiB**，而应用源码只有 6.5 MiB；实测 `out/main/index.js` 与 `out/preload/index.js` 只 `require` electron 与 node 内置模块（第三方全部由 Vite 打进 `out/`）。按 electron-vite 约定把 10 个依赖迁到 `devDependencies` 后：**asar 147.9 → 6.51 MiB，解包总量 380 → 198.6 MiB**（同步更新 `pnpm-lock.yaml`，`pnpm install --frozen-lockfile` 通过）
  - **语言包收敛** `electronLanguages: [zh-CN, en-US]`：55 个 / 38.3 MB → 2 个 / 0.89 MB
  - **合计**：`Setup-x64` 96.5 → **69.2 MiB**、`portable-x64` 96.3 → **69.0 MiB**、`x64.msi` 106 → **79.0 MiB**（x86 同幅：Setup 91.7 → 64.3 MiB）；不再产出 187.7 MiB 的"胖"安装包
  - **否决 `compression: maximum`**：实测只省 **10 KB（0.014%）** 却让单次打包多花约 45 秒
  - 顺手排除 `out/**/*.log`：`out/` 里历史上被写入过 7 个诊断日志（`diagnose-test*.log`），不该随包发布
- `release/SHA256SUMS.txt`：本版本全部安装包本体的 SHA-256；`release.yml` 上传 `*.exe` + `*.msi` + 校验和（不再上传 `latest.yml` —— 本应用未集成 electron-updater，「检查更新」直接查 GitHub Releases API）

### 🔧 修复 / Fixed（32 位包的功能性缺陷）

- **CI 发布流水线的两处坑（v1.3.0 首次发布实测，均已修）**：
  - `EP_PUBLISH: never` 拦不住 electron-builder 的自动发布 —— **v1.2.0 的 Release 运行**正是因此报
    `⨯ GitHub Personal Access Token is not set` 失败（这就是 v1.2.0 有 tag 却没有 Release 的原因）。
    现在 `scripts/build-win.mjs` 在 CLI 上显式传 `--publish never`（双保险）
  - 仓库 `.npmrc` 的 `electron_mirror`（npmmirror）会被 pnpm 导出为 `npm_config_electron_mirror`，
    而 `@electron/get` 的取值优先级是 `npm_config_electron_mirror > npm_package_config_electron_mirror >
    ELECTRON_MIRROR > 默认值`（源码 `mirrorVar()`）—— workflow 里只设 `ELECTRON_MIRROR` **挡不住镜像**。
    v1.3.0 首次 Release 运行便从 npmmirror 拉 arm64 Electron，重试后解包目录里没有 `electron.exe`，
    arm64 打包失败（x86/x64 两步已成功，说明问题只出在那份下载）。workflow 现同时设 `npm_config_*`
    系列环境变量指向官方源
  - 打包脚本对**每个架构**增加一次重试（仅针对下载类瞬时故障；两次都失败仍照原样中止并抛出原始错误，不掩盖真实失败）
- **发布事实（如实留档）**：v1.3.0 的 9 个资产是**在本机构建并逐项校验后上传**的
  （PE 架构、FileVersion=1.3.0、运行时启动验证、SHA-256 全部核对）；CI 首次运行倒在 arm64 下载处，
  上述修复已进 `main`，后续 tag 由 CI 自动发布
- **32 位包改取原生 64 位 PowerShell（`electron/lib/nativePaths.ts` 新增）**：32 位进程在 64 位 Windows 上，`System32` 被 WOW64 重定向到 `SysWOW64`，裸名 `powershell.exe` 拉起的是 32 位 PowerShell。本机实测同一段探针在两种位宽下的差异：`Get-LocalUser` 在 32 位下**命令不存在**（缺 `Microsoft.PowerShell.LocalAccounts`）→ 用户权限页与账号安全体检直接不可用；IIS `Get-Website`（WebAdministration）**COM 类未注册 0x80040154** → FTP/WebDAV 面板与 IIS 能力探测失败；`ProgramFilesDir` 落在 WOW6432Node 视图。修复方式是按「存在即优先」探测 `Sysnative → System32`（`Sysnative` 只有 32 位进程可见，指向真正的原生 System32；本机实测 64 位进程 `Test-Path`=False、32 位进程=True），三种情形各自得到与操作系统同位宽的 PowerShell，且不依赖 `PROCESSOR_ARCHITEW6432` 一类环境推断。主进程启动时把「进程位宽 → 实际使用的 PowerShell」写进应用日志，用户可在「应用设置 → 日志」自行核对；`electron/lib/nativePaths.test.ts` 用注入的 `exists` 固化三条分支（9 例）
- 应用设置页的审计日志表改用稳定 `key`（原 `rowKey={(_, i) => String(i)}` 触发 antd v6「rowKey 函数取下标已废弃」告警）；测试桩补齐 HealthBar 所需通道，消除 `AppSettings.test.tsx` 的未处理 Promise 拒绝（此前 701 例全绿但 vitest 报 `Errors 1 error`）

### ✨ 新增 / Added（应用设置独立成页）

- **「应用设置」从「服务配置」的页签提为侧栏独立一项（`/app-settings`）**：原先它混在服务与协议配置的页签里，应用级开关（使用模式/主题、开机自启、告警规则、防火墙组内规则、账号安全体检）与模块 5 的 SMB/NFS/FTP/WebDAV 服务参数互不相干，入口难找。现拆成 `pages/AppSettings.tsx` + `components/AppSettingsCards.tsx`（五张卡片纯位移，查询 key / 乐观更新 / F5 tick 行为逐字保留），`pages/Settings.tsx` 只留服务与协议内容（净减 800 余行）
- **新增「关于」卡**：版本号与运行时（Electron/Chromium/Node/platform/arch）来自主进程新通道 `system:appInfo`（`app.getVersion()` + `process.versions`），**不在前端写死**；项目地址 / 问题反馈 / 下载页可一键复制或经 `system:openExternal`（主进程 http/https 白名单校验）在浏览器打开。仓库地址收敛到 `src/utils/appMeta.ts` 单一落点（`UpdateChecker` 里原先另写一份的常量随之收口；与主进程 `services/update.ts` 的双份常量关系已在文件头注明）
- **日志集成到本页**：应用日志（`app.log` 尾部 300 行，可刷新/复制/导出/打开日志目录）与审计日志（JSONL 解析为「时间/操作/对象/原因/结果」表格，末 200 条）从**错挂在 SMB 页签下**的位置搬来，与「关于 / 版本 / 项目地址」同处一处；两个面板各自持有 `useQuery`，不再搭上服务配置页那条「一次并发拉 6 项」的大查询
- **导航补齐**：命令面板（Ctrl+K）新增「前往：应用设置」；侧栏入口的回归用例落在 `src/pages/AppSettings.test.tsx`（原先的偏好/运维/体检用例整体迁到该文件与 `Settings.test.tsx` 的 `renderAppSettings()`）
- 主进程新增通道 `system:appInfo`（`electron/ipc/index.ts` + `preload.ts` + `electron/types.ts`/`src/types.ts` 的 `AppInfo` 类型），属新增通道：旧进程缺它时按既有「界面与后台版本不一致」提示处理

## [1.2.0] - 2026-10-10

### 🧹 技术债清零 / Debt（渲染层批 2：renderer-debt）

- **`react-hooks/set-state-in-effect` 全量清零（14 处 → 0）并复启为 `error`**：11 处 modal/drawer 层（`DiagnoseModal` / `FirstRunGuide` / `GroupManageModal` / `JournalDrawer` / `PermissionDrawer` / `PermissionMatrix` / `PresetEditor` / `ShareDetailDrawer` / `UserCreateModal` / `UserDetailDrawer` / `pages/Settings` 派生态）统一改为「渲染期调整 state」（新增 `useResetOnOpen` / `useResetOnKeyChange`）+「effect 内定义 loader」的 async 安全形态；3 处 PermPanel（Nfs/Ftp/Webdav）随 R-5 抽象收敛进 `useProtocolPermissions`。`eslint.config.mjs` 中该规则由 `'off'` 改回 `'error'`，并纠正旧留档的「剩余 10 处」——那只统计了 B1 的 26→10 收敛，漏计批 1 新增代码引入的 4 处（`DiagnoseModal:158` / `FirstRunGuide:52` / `JournalDrawer:127` / `pages/Settings:1132`），起点实测为 14 处。复启后 `pnpm lint`（`eslint . --max-warnings=0`）全库零违例、该规则 suppressions 为 0；任何回退（effect 内重新同步 setState、用 `setTimeout`/`queueMicrotask` 藏 setState、把 state 搬进 ref 静音、加本规则行内 disable、或把规则改回 `'off'`）都会被 lint 与 CI 门禁挡住
- **R-5 全量组件抽象（两半都做，不只 PermPanel 半边）**：三 PermPanel 与三 SettingsPanel 的逐字重复实现收敛为两套共享抽象——`useProtocolPermissions` + `ProtocolPermissionShell`、`useProtocolSettings` + `ProtocolSettingsShell`。六个面板内 `useEffect|useQuery|invalidateQueries|queryKey|setLoading|setRows|setSaving` 命中 0（抽象前为 3 份同构实现）、9 个 IPC 调用点收敛为 1 份、Shell 拥有的 12 条文案/标签由 3 份变 1 份，六面板合计净减 69 行；对外 props 形状（`{ share }`）与 DOM/文案契约逐字不变，新增 59 例面板用例（三 PermPanel 24 例 + 三 SettingsPanel 35 例）
- **行为零变化的独立验证（三轮）与两轮评审**：**round 1 红**——抓到 F1 行为回归（「重新加载 / 保存后重读」清空已加载的 NTFS 只读视图）与 F2 门禁不绿；**round 2 红**——F1 已闭环、F2/F3 未闭环（覆盖率门禁超时漂移，根因由「移出探针文件后仍红、超时漂移到本批次未改动的 `Sessions.test.tsx`」的对照实验确证）；**round 3 绿**（`test:coverage` 连续 2 次 exit 0、超时告警 0 次、失败点不再漂移）；F1 由 t8 修复、F2/F3 由 t9+t11 消除。随后新一轮验证又抓到 **F4**（新建 PermPanel 测试的 RTL `asyncUtilTimeout` 默认 1s 过紧，负载敏感间歇红），由 t13 修复后复验绿。评审 t5（R-5 抽象与协议权限面板）与 t6（modal/drawer 迁移）均 **verdict = pass**、无 findings，各留 6/5 条观察（见 `docs/audit/05-renderer-debt.md` §7）
- **如实留档：施工规格自身有 6 处写错 + 1 处自相矛盾**（详见 `docs/audit/05-renderer-debt.md` §7.3）：(1) §1.3#6 被规则行为探针 P-3 推翻；(2) §1.3#2 字面方案会新造违例且须改动 §1.5 禁改的 effect；(3) §1.3#11 会让「刷新连接」多发一次 `openFiles` IPC（违反 §4.1）；(4) §1.3#5 的 reset 键设计导致 NTFS 视图清空——这是**实现引入的真实回归**，按 §1.4/§4.1 意图修复而非登记为「允许的收紧」；(5) §1.6/§3.3 的判据「加载中不得出现空态文案」被否证（antd Table 的 `locale.emptyText` 在 Spin 期间仍在 DOM），已改为可观测判据；(6) 另三处「规格描述与既有行为不符」。另有 §3.4 把 `PermissionDrawer` 列为「预期零改动」与 §1.3#5 指派迁移该文件的自相矛盾——按 §1.3#5 记（该文件确实迁移），属**规格内部冲突**，不是任何实现者的违规
- **测试基建两处放宽（如实定性：消除基建抖动，不是修好了慢测试、也不是性能改进）**：`vitest.config.ts` 全局 `testTimeout` 5s→30s（管单个用例总时长）与 `src/test/setup.ts` 的 RTL `asyncUtilTimeout` 1s→10s（管 `waitFor` 轮询窗口）。两者是**两个不同的超时**，后者不被前者覆盖——这正是 F4 未被 t11 覆盖的原因。没有加速任何用例、没有删改任何断言或等待、没有动被测路径；代价是真失败的 `waitFor` 现在最多 ~10s 才报错（t13 自报 10411ms、verifier 独立复现 10009/10011ms），**只推迟暴露时点、未掩盖失败**。覆盖率棘轮阈值（30/25/19/32）与 `include`/`exclude` 逐字未动
- **终态门禁（本次集成实测）**：`pnpm typecheck` / `lint` / `format:check` / `test`（47 文件 650 用例）/ `test:coverage`（47/650，All files 42.02% stmts / 40.81% branch / 30.7% funcs / 44.09% lines，均高于未改动的棘轮阈值）/ `build` 逐条 **exit 0**

### 🌙 暗色模式与运维三项 / Dark & Ops

- **暗色模式双轨接线（把「只到一半」接完）**：原先只落到 `<html data-theme>` 与状态持久化，两轨都没接。① antd 轨——`ConfigProvider` 原先直接写在 `main.tsx`、位于 `<App/>` **之上**，读不到下层 appStore，`algorithm` 无从切换；抽成 `ThemeProvider` 后真正跟随 `appStore.state.theme`，暗色取 `darkAlgorithm`；② 自有 CSS 轨——`index.css` 新增 `[data-theme='dark']` 段（14 个令牌：body 渐变、glass-card、面板/标题栏/代码块/图表底色、滚动条、阴影）；③ tailwind 轨——`darkMode` 由默认 `'media'`（跟随系统）改 `'class'`（跟随应用主题）。全站 **24 处**硬编码浅色逐处替换（含 `bg-mist` / `border-black` / `rgba(0,0,0,…)` 等首批扫描漏项）；其中 2 处走 canvas（echarts `getDataURL`）不能用 `var()`，改为按 `data-theme` 取具体色值。机器证据：`applyThemeToDocument` 钉住「data-theme 与 .dark class 必须一起落」（上一批的教训是「只落一半等于没接线」）；`ThemeProvider` 用 `theme.useToken()` 读出实际 token——暗色容器色 ≠ 浅色注入值，即等价于 algorithm 真的换了；构建产物实测含 `[data-theme='dark']` 段与 8 个 `.dark:` 选择器
- **应用内检查更新（只检查 + 提示，不做自动下载安装）**：`update:check` 查询 GitHub Releases 最新版本 + 语义化版本比较（含预发布语义）+ `UpdateChecker` 卡片。**仍不集成 `electron-updater`**——它需要 electron-builder `publish` 配置、代码签名与线上 `latest.yml` 三件套，不是一次接线能补齐的。失败路径按「受限网络是常态」设计：网络不可达 / 404 未发版 / 5xx / 响应非 JSON / 缺 `tag_name` / 超时六类各自给出人话原因与可执行动作，并有专门的回归钉——**「检查失败」绝不显示「已是最新」**（把失败伪装成绿是这张卡片最不能犯的错）。新增 `system:openExternal`，只放行 http/https（白名单抽成 `isSafeExternalUrl` 纯函数，拒绝 `file://` / `javascript:` / `ms-msdt:` / UNC / 前导空白 / 超长 URL）
- **设置整体导入导出**：`state:exportAll/importAll` 搬的是**应用偏好与运维规则**（主题 / 新手模式 / 置顶 / 告警规则 / 自启意图），与 `share:`（共享配置）、`preset:`（权限模板）分工并列。**刻意不导出** `dashboardHistory` 与 `journal`——前者是「这台机器这段历史」的趋势曲线、后者是含改前快照的撤销台账，跨机器搬家会给出不存在的含义（有专门用例钉住「连字段名都不出现」）。导入不信任文件：512KB 上限 → 格式/版本校验 → 逐字段类型收敛 → 交给 `stateStore.saveState` 再走一次 normalize；语义是「合法字段应用、非法字段忽略」，且没有任何可识别项时明确拒绝而非静默成功。导入后**强制 `hydrate()`**——真源在主进程，不重新水合就会出现「导入了但界面没变化」的经典假象
- 新增 46 条测试（该批总计 696），覆盖：主题接线（两轨一致性 / algorithm 切换集成 / 算法映射）、更新检查服务层 11 例（六类失败 + 语义化比较）、URL 白名单 5 例、导入导出 17 例（护栏 + 逐字段收敛 + 往返幂等 + UI 五态）

### ✨ 新增 / Added

- **一键诊断向导（★★）**：`diagnose:run` 串查「服务在跑 → 445 在听 → 共享存在 → 本地路径存在 → NTFS 可读 → 防火墙放行 → 共享权限非空 → SMB1/访客策略」，逐项红绿灯 + 可执行修复按钮（启服务 / 加 445 入站规则 / 授予 Everyone 读取）。单项探测失败记 warn 而非整体崩；修复的路径一律由服务端按共享名重新解析并复用 `validatePath`，**绝不信渲染层传入的路径**
- **操作回收站（★ 删除可撤销）**：`appstate.json` 记最近 200 条写操作；SMB 删除/禁用/权限变更带改前快照，可一键回放（删除=按快照重建含权限与高级选项，禁用=走既有 disabled.json 通道，权限=回写改前授权）；撤销成功即消费记录；`create` 与非 SMB 删除**只留档不假承诺可撤销**
- **磁盘水位（★）**：共享所在盘剩余空间按 `alertRules.diskLowGb` 分级黄/红标；UNC 路径不显示水位（未知就是未知，不显示 0%）
- **删除前连接影响提示（★）**：确认框显示"当前 N 个连接正在使用 X、Y——删除会强制断开，正在写入的文件可能丢失"，并注明该操作能否撤销；原"不可恢复"措辞在有了撤销底座后已不属实，一并纠正
- **首启三问向导（★）**：选文件夹 → 给谁用（三档人话权限）→ 完成；高级参数折叠；路径默认取"非系统盘剩余空间最大者"，共享名由末级目录带出（用户改过则不覆盖），默认授权 `Users` 读取。首启判定为纯函数（空共享 + 未标记 + 新手模式才弹），老用户升级不受打扰
- **常用共享置顶 + 「只看有人连接」筛选（★）**
- **新手/专家双模式**：新手模式收起 SMB1/枚举/租约/缓存/并发上限等概念；专家模式与迁移前完全一致
- **开机自启 / 告警规则可配 / 账号安全体检（★）**：自启读写系统登录项（平台不支持则整卡隐藏）；告警规则四项（空闲提醒、SMB1、弱口令、磁盘阈值）逐键浅合并落盘；体检报告空口令 / 永不过期 / 超 180 天未改
- **防火墙自动配置（★）**：`WinShare Panel` 分组内规则的幂等 ensure / 列举 / 删除，预设覆盖 SMB(445)、FTP(21 + 被动段)、WebDAV(80)、SMB over QUIC(445/UDP)；**只增删本应用组内规则，绝不触碰系统或第三方规则**
- **连接数 24h 趋势 + 需要关注聚合**：主进程每 5 分钟采样（服务未起跳过，不写假点），折线用真实时间戳，停机稀疏段留白不斜连
- **「谁能访问什么」HTML 报告导出（★）**：与既有 CSV/JSON 并列，一页自包含可打印报告（概览 + 权限矩阵 + 宽权限高亮），所有系统来源字符串做 HTML 实体转义，页脚注明"仅共享权限、未含 NTFS 有效权限"的诚实边界
- 新增 245 条测试（总计 502），覆盖：主进程 6 个新模块（stateStore/disk/firewall/journal/security/diagnose，覆盖率 97–100%）、回收站与诊断组件、首启向导与判定、共享页四项体验、设置页五块运维控制、HTML 报告转义

### 🐛 修复 / Fixed

- **`firewall.ts` 规则名白名单不放行 `/`**（qa-main 报出、captain 复现）：与自身预设名 `WinShare SMB (445/TCP)` 互斥，导致 `ensureRules(presetRules(kind))` 恒抛 `INVALID_PARAM`——预设建规则、按名删规则、诊断的"防火墙"一键修复在运行时全部不可用。现放行 `/`（这些名字经 `psQuote` 进单引号上下文，无插值风险），并补"每个预设 kind 都能通过本模块自校验"的回归钉
- `ensureRules` 校验与写入同循环交错会**部分提交**（合法项先建、非法项后抛）：改为先全量校验再写入
- `ensureRules` 的 protocol 校验是死分支（上一行三元已归一）：改为显式拒绝非法入参
- `getDiskUsages` 非数值 `Free`/`Used` 会产出 `freeGB=NaN`：加有限数收敛

### ⚠️ 未完成 / 已知边界

- **暗色模式已双轨接线**（antd `darkAlgorithm` + `index.css` `[data-theme='dark']` + tailwind `darkMode:'class'`），但**未做真机 GUI 视觉走查**：对比度、glass-card 透明度、渐变观感只有人眼能判，jsdom 不能替代。另有两处如实记录的取舍：冷启动首帧按默认浅色渲染一次（`appStore.hydrate` 异步读主进程 appstate.json）故有**一次浅色闪烁**，未消除；`Dashboard` 的 echarts canvas 底色与 `index.css` 的 `--chart-bg` 同值但**双真源**，改动需同步
- 英文界面（i18n 抽取）、预约式任务仍未做。i18n 规模已实测：**非注释中文字面量 1167 条 / 39 个文件**（`Settings.tsx` 单项 206 条），且 27 个测试文件含 17723 个中文字符的断言面；实测它与暗色同时触碰 9 个组件 + `main.tsx`，同工作区并行必丢改动，故应单独成批（默认语言必须保持中文，否则既有断言面会一起崩）
- **R-8 作为「数据获取模式重构」的整体条目仍开放**：本批次完成的是它的阻塞条件（14 处违例清零 → `set-state-in-effect` 复启为 error），但**没有**把全部挂载取数改写成 defer/React Query（本批次明确不改数据获取范式）；jsdom 组件测试基建亦未引入
- **既有的负载敏感压力用例未根治**：`electron/lib/powershellPool.stress.test.ts`「场景2：100 并发含 10 条卡死命令」在本批次多轮运行中独立观测到间歇失败（`expected [Array(82)] to have a length of 90` + retry 后仍 76）——它是**断言失败、不是超时**，本批次对该文件**零改动**，因此既非本批次引入、也不受本批次的 `testTimeout` / `asyncUtilTimeout` 放宽影响；建议另开专项，且 `82 vs 90` 可能不只是环境抖动、也可能是进程池并发上限的真实边界信号
- **证据边界**：本批次的独立验证只到 **jsdom + 门禁级**（IPC 全为 mock），未做真机 GUI 走查，未覆盖真实 SMB/IIS/防火墙/icacls/开机自启/跨小时趋势，**不构成端到端验证**。运维三项另有两处具体缺口：**检查更新的成功路径在本机无法验证**（实测 `api.github.com` 不可达；失败路径反而有六类真实验证）；**导入导出的真实文件下载与文件选择经打桩**（jsdom 不实现对象 URL 与下载导航）

## [1.1.0] - 2026-10-09

### 🖥 系统版本适配（OS matrix）

- **运行时功能探测 `system:osInfo`**：单次 PowerShell 往返获取 OS caption/build/SKU + 功能矩阵（SMB 模块 / NFS 服务端 cmdlet / IIS 可承载性（Server 恒真、Home SKU 恒假）/ SMB QUIC 属性），主进程缓存；新增 `docs/COMPATIBILITY.md` 完整矩阵与手工回归清单
- **SMB 服务器配置"按机过滤"下发**：保存前探测本机 `Get-SmbServerConfiguration` 实际属性集，旧系统（Server 2016/2019、Win10）不存在的 `EnableSMBQUIC`/`SilentAU`/`SessionTimeoutSeconds` 等自动跳过并日志留痕，不再报"找不到参数"；全部被跳过时明确拒绝而非空命令
- **UI 适配呈现**：设置页新增"系统环境"卡（OS + 功能矩阵 + 家庭版 IIS 不可用提示）；QUIC 开关不支持时禁用+tooltip；新建共享弹窗按探测结果禁用不适协议选项（客户端系统禁 NFS 创建标注"客户端仅"、无 IIS 系统禁 FTP/WebDAV 标注）
- 新增适配测试 8 个（smb 过滤 4 + osInfo 探测/映射/缓存 4），总用例 256

### 🔁 架构演进 / Architecture（B1）

- **数据层迁移 react-query（@tanstack/react-query 5）**：5 个页面（仪表板/共享管理/用户权限/会话监控/服务配置）+ 3 个协议配置面板统一 `useQuery` 拉取；轮询/并发去重/缓存失效内建，替代手写 `load + inflight 守卫 + 定时器` 链；`load()` 名称保留、语义=invalidate（变更回调零改动）；`refetchOnWindowFocus:false`/`retry:0` 适配 IPC 语义；F5 与托盘刷新经 store tick → invalidate 桥接
- **命令面板**：状态重置改"渲染期调整 state"官方模式；短查询门控替代同步清空
- 行为验证：新增 Sessions 页 3 个 jsdom 数据流测试（首挂载/刷新/断开失效），并借此发现修复卸载竞态（refetchQueries 结果 undefined 崩溃）
- 遗留：10 处 modal/drawer 层 effect-setState 未迁移（清单见 eslint.config.mjs 留档），与 R-5 组件抽象同属后续工作包，规则复启以 UI 回归为前置条件（**批 2 已全部清零**：实测起点为 14 处、非 10 处，见上方 [Unreleased]「技术债清零」段）

### ✨ 新增 / Added

全面审计（docs/audit/）与工程化规范化落地。

### ⚙️ 工程化 / Engineering

- **E1 pnpm 工具链修复**：删除非法 `pnpm-workspace.yaml`（缺 `packages` 字段导致全部 pnpm 脚本不可用），`onlyBuiltDependencies` 迁移至 package.json `pnpm` 字段；新增 `packageManager: pnpm@9.12.0` 与 `engines` 声明
- **E2 静态检查与格式化门禁**：引入 ESLint（flat config：typescript-eslint + react + react-hooks 7）与 Prettier，新增 `lint` / `format` / `format:check` 脚本；全库 57 项违规归零；`typescript` 7.0.2→6.0.3（typescript-eslint@8 不支持 TS7 的必要取舍，typecheck/build 无回归）
- **E3 CI 质量门禁**：新增 `.github/workflows/ci.yml`（push/PR：frozen 安装 → typecheck → lint → format:check → test:coverage）；`release.yml` 对齐 packageManager 单一真相源与 `--frozen-lockfile`，根除工具链版本漂移
- **E4 测试基础设施**：`@vitest/coverage-v8` + 棘轮覆盖率阈值（30/25/19/32 = 实测水位取整）；渲染层首批单测（password/uiStore/api），总用例 191→212
- **E5 类型检查收紧与 P2 修复**：electron 测试纳入 typecheck 并修复暴露的 77 处类型问题（可选适配器方法调用等）；双 tsconfig 开启 `noUnusedLocals`/`noUnusedParameters`；导入 JSON 增加 2MB/500 条护栏并附单测（R-1）；NFS 只读字段语义注释留档（M-1 复核：UI 已是 disabled 只读分区，非缺陷）

### 🐛 修复 / Fixed

- `dev` 脚本 `chcp 65001 >nul 2>&1 &` 的位运算符改为 `&&`（E-10）
- 全库未使用导入/只写不读死状态清理（lint 归零过程，行为不变；R-2/E-7）
- `.gitignore` 补充 `.agent-teams/`（E-8）

### 🔧 P3 收尾 / Follow-ups（2026-06 第二批）

- **M-3** 进程池空闲期杂散 stdout 直接丢弃：防止 buffer 无限累积与伪造协议标记窗口，附回归测试
- **M-4** 池协议 token 改用 `crypto.randomBytes`（加密安全随机源）
- **M-2/M-6** SMB 快照链语义注释留档（restoreDefault 正常快照）；共享恢复失败信息场景化（"恢复共享X失败"）
- **R-4/R-5(部分)** 抽取 `useEnsureProtocolCaps` 统一协议探测逻辑（Dashboard / Banner / 三 SettingsPanel 五处 → 一处），保留各消费方失败降级语义（面板 onDetectFailure→未装渲染）
- **F-1/F-4** IPC 边界逐字段形状守卫（share/user/group:create、update、preset:save、四 setConfig）：对象/文本长度/控制字符/布尔类型畸形输入在边界拒绝；合法语义（DOMAIN\user、口令任意字符集）不受影响
- **R-6** `api.call` 透传 AppError 的 `code`/`category`（Electron 不支持该属性过桥时行为等价），附 2 用例
- **R-7** 死样式清理：tailwind 移除 primary-light/secondary/accent/backdropBlur.glass/shadow-hover，`:root` 移除 6 个零引用 CSS 变量（类名与 var() 引用逐项扫描取证）；build 复验通过
- **F-3** relaunchAsAdmin 含空格路径补 psQuote 单测
- **E6 统一日志系统**：新增 `electron/lib/logger.ts`——运行日志持久化到 `%APPDATA%\WinSharePanel\logs\app.log`（2MB×3 轮转、级别、console 镜像）；全库 12 文件 `console.*` 收编为 `log.*`；渲染层崩溃经 `ErrorBoundary` + `window.onerror`/`unhandledrejection` 转发落盘；设置页新增"应用日志"Tab（查看最近 300 行/复制/导出），托盘菜单新增"打开日志文件夹"；IPC 日志通道带 level 白名单、4000 字符截断、30 条/秒限流；vitest 环境默认关闭文件落盘防测试污染；logger 8 用例，全量 232 用例六门禁全绿
- **B-1/B-2 假成功缺陷修复**（日志上线当日由 app.log+audit.log 交叉定位）：cmdlet 非终止性错误（权限不足/名称冲突）被当作退出码 0 吞掉 → `New-SmbShare` 等写操作假成功、审计记 success、UI 刷新时才报"共享不存在"。修复：进程池 server 脚本与 execOnce 前缀统一 `$ErrorActionPreference='Stop'`（既有容错路径均有显式 -EA 或 try/catch，语义不变）；SMB/NFS createShare 回读判空守卫（4 回归用例）；同型修复 healthCheck 模块探测假可用、4 处存在性检查 `[]` truthy 误判（新增 `firstOrNull`）；全量 236 用例六门禁全绿
- **B-4 系统共享详情误报"共享名非法"**（用户提供复制出的错误文本定位）：查看 `IPC$`/`ADMIN$` 等系统共享的详情/权限被入口校验拒绝——`validateName` 为防 PS 双引号插值全局禁 `$`，但系统共享名必然含合法尾 `$`。新增 `validateShareName`（读取/操作路径放行恰一个**尾部** `$`，中间/多个 `$` 与 `$(...)` 注入形态仍拒绝；全部嵌入点核对处于 psQuote 单引号上下文，`$` 无插值语义）；创建/用户名/权限写入保持严格；补齐 `toggleShare` 对系统特殊共享的防护（其禁用分支会真删共享，此前仅被过严校验意外挡住）；新增 `validateShareName` 4 组单测（尾 $ 合法/注入形态拒绝）
- **协议专门适配（能力补全 + UI 暴露）**：NFS 共享详情新增"站点配置"Tab（ro/rw、root 抑制、未映射访问编辑，适配器早已支持、UI 首次暴露）；FTP 站点支持修改**本地物理路径**（Set-ItemProperty + validatePath 守卫，UI 暴露）；WebDAV 站点支持切换 **authoring 写入开关**（更新路径补齐，创建时已有）；SMB 高级属性新增**隐藏共享**开关（Set-SmbShare -Hidden，经 psBool 守卫）；抽屉脚注指引同步更新；+4 适配器用例（含非法路径拒绝注入探针）
- **各协议"配置预设"一键应用**：新增 `utils/configPresets.ts`（SMB×4：安全加固/性能优先/兼容老旧/家庭局域网；NFS×2；FTP×3：对外加密分发/内网便捷/匿名下载；WebDAV×3：只读发布/协作写入/匿名投递）+ `ConfigPresetBar` 组件（卡片式、变更字段预览、降安全性预设 risk 二次确认）；接入 SMB 服务器配置页与 FTP/NFS/WebDAV 三面板；SMB 应用前自动快照可回滚
- **修复顺带**：SMB"请求压缩"下拉选项与后端白名单不一致（Allow/Require → OptimizeForSpeed/OptimizeForSize，原选项被 psEnum 静默丢弃）
- **路径选择修复**：新建共享与 FTP 站点配置的路径字段新增"浏览"原生文件夹选择按钮（补注册缺失的 `system:selectFolder` IPC handler → `dialog.showOpenDialog`，此前 preload 暴露了 API 但主进程从未注册，调用必报错）；拖拽取路径从已被 Electron 32+ 移除的 `File.path` 迁移至官方 `webUtils.getPathForFile`（preload 暴露，异常回退空串），手动拖拽与浏览双通道均可用
- **命令面板完善**：新增"操作"命令组——新建共享 / 强制刷新 / 重新检测协议能力 / 检查服务健康 / **打开日志文件夹**（新 IPC `system:openLogFolder` → shell.openPath）；共享搜索结果提供两个直达动作：**打开详情抽屉**（store 意图 detailTick+detailShare 跨组件直达）与**复制本地路径**；会话结果跳转并自动选中；footer 快捷键提示（↑↓/Enter/Esc）
- **A1 域账号支持**：新增 `validateAccountName`（放行 `DOMAIN\user`、`NT AUTHORITY\SYSTEM` 形态的反斜杠与尾 `$`，控制字符/引号仍拒；账户名全部经 psQuote 单引号上下文嵌入无插值风险）；应用于权限条目校验、组成员添加/移除、新建共享访问列表逐条守卫、preset 应用（修复内置模板 apply 时账户校验过严的隐患）
- **A2 UNC/网络路径**：`UserInfo` 增 `computerName`（os.hostname）；共享详情"网络路径"行按协议生成 `\\host\share` / `ftp://host:port` / `http://host:port` 并可一键复制
- **A3 渲染层 console.error 转发**：main.tsx 拦截 console.error（50 条限额+截断）汇入 app.log，组件级错误亦可回溯
- **A4 审计日志 Tab 美化**：JSONL 解析为结构化行（时间/操作/对象/原因/成败 Tag）+ 复制按钮，末 200 条
- **A5 文档同步**：README 环境要求修正（pnpm 11 → 9.x 与 packageManager 同源）、质量门禁命令表、管理员终端提示、日志系统说明
- **新建共享常见预设**：协议感知场景 chips 一键填入表单（SMB：团队协作/公开只读/私有加密；NFS：只读发布/读写共享/Kerberos 安全共享；FTP：安全分发/匿名下载/内网共享；WebDAV：只读发布/匿名投递），填入后仍可逐项调整
- **合并说明（NFS 会话）**：采纳 origin/main v1.0.1 的语义修正——listSessions 改用 `Get-NfsSession`（MSFT_NfsSession 仅 SessionId/NetworkName/State/ClientId，相应字段留空），closeSession 改用 `Disconnect-NfsSession -SessionId`；统一为 logger 风格
- 仍开放（专项级，见 docs/audit/00-backlog.md §3.5）：R-8 数据获取模式重构（复启 set-state-in-effect）、R-5 全量组件抽象（三 PermPanel/面板表单复用）（**批 2 已闭环 R-5 全量与规则复启**；R-8 作为「数据获取模式重构」整体条目仍开放，见上方 [Unreleased]）

## [v1.0.1] - 2026-09

> 本节为合并 origin/main 时补记（远端发布时未更新 CHANGELOG）。

- 多架构打包支持：nsis / portable / msi × x64 / ia32（electron-builder arch 矩阵，产物名含 `${arch}`），引入 `resedit` 用于资源编辑
- CI 修复：`EP_PUBLISH=never` 禁用 electron-builder 自动发布，避免 Release 资产重复上传

## [v1.0.0] - 2026-08-08

首个正式发布版本：多协议共享管理 + 生产级安全加固 + 性能优化 + 全面测试。

### ✨ 新增 / Added

#### 多协议统一管理
- **四种协议适配器**：SMB / NFS / FTP / WebDAV 各自实现统一 `ProtocolAdapter` 接口，`registry` 按协议路由调用
- **协议能力探测**：`protocol:detect` 检测各协议安装状态与能力（`ProtocolCapabilities`），UI 顶部 banner 引导安装未就绪协议
- **多协议会话监控**：`adapter:sessions` / `adapter:closeSession` 统一会话列表与断开
- **协议级配置**：NFS / FTP / WebDAV 服务器配置读写、服务启停、恢复默认（`nfs:` / `ftp:` / `webdav:` IPC 命名空间）
- **新建共享高度可自定义**：创建向导支持各协议特有字段（NFS 认证/权限/匿名 GID、FTP 站点绑定/SSL、WebDAV 授权规则等）

#### PowerShell 常驻进程池
- **`electron/lib/powershellPool.ts`**：维护 N 个长期存活 PowerShell worker，通过 stdin/stdout 标记协议复用进程
- 命令执行开销从 ~300–500ms/条降至 ~30–80ms/条（CLR+引擎+模块启动开销仅付一次）
- 懒启动 + 首屏预热 1 个 worker；超时 kill 中毒 worker 并补 spawn；崩溃自动恢复
- env 安全阀：`WINSHARE_PSPOOL=0` 回退原 `execFile` 路径；`WINSHARE_PS_POOL_SIZE` 调整池大小

#### 性能优化
- **并行执行**：协议探测（3 并发）、共享列表（4 并发）、仪表盘数据（5 并发）改用 `Promise.allSettled` 并行
- **in-flight 去重**：并发协议探测请求共享同一 Promise 缓存，避免重复 PowerShell 调用
- 首屏与刷新从秒级（~1.5–2.5s）降至百毫秒级

#### 安全加固
- **命令注入运行时校验**：`psBool` / `psNumber` / `psEnum` 对布尔/数字/枚举字段做运行时类型验证，拒绝非法值拼入命令
- **`-EncodedCommand` 编码**：所有命令经 UTF-16LE→Base64 编码，规避中文 GBK 代码页问题与 shell 解析注入
- **IPC 边界校验**：协议名 / 共享名 / 路径在 IPC 入口运行时校验
- **Electron 安全**：`contextIsolation: true` + `nodeIntegration: false` + `sandbox` + 单实例锁（`requestSingleInstanceLock`）
- **只读协议查询容错**：`adapterList` / `adapterGetPermissions` / `adapterSessions` / `nfs.getConfig` 失败返回空数组而非抛错
- **受保护资源**：系统特殊共享（ADMIN$/IPC$/C$）与内置用户/组禁止删除

#### 事务补偿与孤儿清理
- **`setPermissions` 事务回滚**：备份当前权限 → 应用新权限 → 失败时回滚至备份；四个协议统一记录 backup/rollback-trigger/pre-rollback/post-rollback 四个时间点权限状态
- **`createShare` 孤儿清理**：创建失败时自动 `Remove-SmbShare` / `Remove-Website` 清理残留共享/站点，避免资源泄漏

#### 用户与组管理
- 本地用户 CRUD（创建/启用/禁用/重命名/改密/删除）
- 本地组 CRUD + 成员增删（`group:` IPC 命名空间）

#### 测试
- **191 个单元测试**（10 个文件）：覆盖命令注入防护、输入校验、事务回滚、错误传播、进程池排队/超时/崩溃恢复
- **进程池压测**：100 并发全成功、100 并发含 10 超时、100 并发全超时三个场景，验证排队/中毒重启/无悬挂

### 🔄 变更 / Changed

- **技术栈升级**：React 18→19、Ant Design 5→6、TypeScript 5→7、electron-vite 2→5、Vitest 1→4、electron-builder 24→25、Zustand 4→5、React Router 6→7、ECharts 5→6、Electron 28→31
- **PowerShell 执行器重构**：`runPowerShell` / `runPowerShellVoid` 路由到进程池，签名与行为不变，100+ 调用点零改动
- **统一日志格式**：四个协议适配器统一 `[<operation>:<protocol>]` 前缀与回滚节点日志
- **`electron/main.ts`**：集成进程池生命周期（`prewarmPool` 预热、`before-quit` + `will-quit` 兜底 `shutdownPool`）

### 🐛 修复 / Fixed

- **antd v6 `valueStyle` 弃用警告**：`Dashboard.tsx` 改用 `styles.content`（antd v6 API）
- **浏览器预览崩溃**：`TitleBar.tsx` 增加 `if (!api) return` 守卫，无 Electron preload 时不访问 `window.winshare`
- **生产环境 logo/favicon 不显示**：绝对路径改相对路径
- **release workflow Node 版本**：pnpm 11.20+ 要求 Node 22，CI `node-version` 升至 22
- **多开实例**：`requestSingleInstanceLock` 单实例锁，托盘已有实例时双击激活而非新开进程

### 🛠 工程化 / Engineering

- **CI/CD**：`.github/workflows/release.yml` tag 触发自动构建并发布 GitHub Release（NSIS + 便携版 + `latest.yml`）
- **`vitest.config.ts`**：单元测试配置（`environment: node`）
- **设计文档**：`.trae/documents/` 下新增进程池设计、共享自定义、协议配置自定义、用户权限增强等设计稿

---

### 📦 产物 / Artifacts

- `WinShare-Panel-Setup-1.0.0.exe` — NSIS 安装包（含 UAC 提权）
- `WinShare-Panel-1.0.0-portable.exe` — 便携版（免安装）
- `latest.yml` — 自动更新元数据
