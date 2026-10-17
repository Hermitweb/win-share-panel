# WinShare Panel 系统兼容性矩阵（OS Compatibility Matrix）

> 版本：v1.1.0 ｜ 更新：2026-10-09
> 适配策略：**运行时功能探测（feature-driven），不按版本号硬编码**。
> 探测实现：`electron/services/system.ts → getOsInfo()`（单次 PowerShell 往返，进程内缓存）；
> 应用内入口：设置页"系统环境"卡。

## 1. OS × 功能支持矩阵

图例：✅ 完整支持 ｜ 🟡 受限/降级 ｜ ❌ 不可用 ｜ ⚙️ 需安装角色/可选功能（应用内一键安装）

| 能力 | Win 10 21H2/22H2<br>Home | Win 10/11 Pro·Ent<br>/Education | Win 11 23H2+ | Server 2016 | Server 2019 | Server 2022/2025 |
|---|---|---|---|---|---|---|
| **SMB 共享管理**（增删改查/权限/会话/打开文件） | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| SMB 加密（EncryptData） | ✅¹ | ✅¹ | ✅¹ | ✅¹ | ✅¹ | ✅¹ |
| **SMB QUIC** 配置项 | ❌² | 🟡 Win11 23H2+ ✅ | ✅ | ❌² | ❌²（需补丁³） | ✅ |
| **NFS 服务端**（创建 NFS 共享/会话/权限） | ❌⁴ | 🟡 客户端only⁴ | 🟡 客户端only⁴ | ⚙️✅ | ⚙️✅ | ⚙️✅ |
| NFS 客户端（挂载） | ❌⁴ | ⚙️✅ | ⚙️✅ | ✅ | ✅ | ✅ |
| **FTP（IIS FTP）** | ❌⁵ | ⚙️✅ | ⚙️✅ | ⚙️✅ | ⚙️✅ | ⚙️✅ |
| **WebDAV（IIS WebDAV）** | ❌⁵ | ⚙️✅ | ⚙️✅ | ⚙️✅ | ⚙️✅ | ⚙️✅ |
| 本地用户/组管理 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Win11 亚克力材质 | ❌（自动跳过） | 🟡 视版本 | ✅ | ❌ | ❌ | ❌ |

注释：
1. 需 SMB 3.x（Win8+/Server2012+），全部目标系统满足。
2. `Set-SmbServerConfiguration -EnableSMBQUIC` 参数不存在——**应用已按机过滤**：保存时探测本机
   `Get-SmbServerConfiguration` 实际属性集，不支持项自动跳过并在日志记录（不报错）。
3. Server 2019 安装 KB5025230 后支持 QUIC；探测以属性存在为准，装补丁后自动解禁。
4. Win10/11 客户端系统无 NFS 服务端角色：`Get-NfsShare` 等 cmdlet 不存在 → 探测标记
   `nfsServerCmdlets=false`；`detectProtocols` 报 `installType: 'client-only'`。
5. **Windows 家庭版不含 IIS 组件**（可选功能列表中不存在）→ `iisAvailable=false`。

## 2. 版本差异在应用中的吸收方式（已实现）

| 差异点 | 吸收机制 | 代码位置 |
|---|---|---|
| 旧系统缺 SMB 服务器配置参数（QUIC/SilentAU/SessionTimeoutSeconds 等） | 保存前属性探测过滤，缺失跳过 + 日志留痕；全被跳过时明确拒绝 | `smb.ts buildSetCmd(config, supported)` |
| 客户端系统无 NFS 服务端 | `installType: 'client-only'` → 新建共享弹窗禁用 NFS 选项并注记 | `detect.ts` + `Shares.tsx` 协议 Select |
| 家庭版无 IIS（FTP/WebDAV 全链路不可用） | `osInfo.features.iisAvailable=false` → 协议选项禁用 + 环境卡红色标注 + 面板保持安装引导降级 | `system.ts getOsInfo` + `OsCompatCard` |
| Server vs 客户端安装命令不同 | Install-WindowsFeature vs Enable-WindowsOptionalFeature 双命令集，caption 判型 | `detect.ts PROTOCOL_INFO` |
| Get-SmbServerConfiguration 字段缺失（旧版本） | getConfig 对每个可选字段 `!== undefined` 判断回填默认值 | `smb.ts getConfig` |
| cmdlet 非终止性错误差异（版本相关报错文案） | EAP=Stop 统一转终止性错误 + formatPsError 归一化（权限/重启/无效功能分类） | `powershell.ts` |
| Win11 材质 API 存在性 | isWin11(build≥22000) + 可选链调用，低版本静默跳过 | `main.ts` |
| SMB QUIC UI 开关 | 探测不支持时禁用 + tooltip 说明"保存时自动跳过" | `Settings.tsx` |

## 3. 手工回归矩阵（发版前建议执行）

> 六门禁自动化覆盖逻辑层；OS 矩阵行为需在真实系统验证。每台机器 15 分钟。

**通用冒烟（所有系统）**
- [ ] SMB：创建共享（浏览选路/拖拽）→ 列表出现 → 权限抽屉读取/授予 Everyone 只读 → 从另一机 `\\host\share` 访问
- [ ] 会话监控：连接数实时刷新、断开生效
- [ ] 服务配置：改 SMB 参数保存 → 快照历史出现改前快照 → 回滚成功
- [ ] 设置页"系统环境"卡：版本/SKU/功能矩阵显示正确
- [ ] 无管理员启动：所有写操作报"权限不足，请以管理员身份运行"（非静默）

**Win10/11 Pro/Ent**
- [ ] NFS 面板：显示客户端引导，协议下拉 NFS 创建项为禁用
- [ ] FTP/WebDAV：一键安装 → 重启服务 → 建站点 → 匿名/基本认证访问

**Win10/11 Home**
- [ ] 环境卡：IIS=红色不可用，Home SKU 判定
- [ ] FTP/WebDAV 面板降级文案；SMB 全部功能不受影响

**Server 2016/2019**
- [ ] SMB 配置页：QUIC 开关禁用；含 QUIC 的预设应用时日志出现"版本适配…已跳过"
- [ ] NFS：一键安装 FS-NFS-Service 后共享创建/权限/会话（Get-NfsSession）

**Server 2022/2025**
- [ ] QUIC 开关可启用（SMB over QUIC 端口默认 445）
- [ ] NFS 角色完整流程

## 4. 已知限制

- Windows 7/8/8.1、Server 2012 R2：**不支持**（Get-Smb*/NFS/IIS 命令面差异过大 + Electron 31 已不支持 Win7/8），安装器可装但应用可能无法启动，请勿用于生产
- Server Core：SMB/NFS cmdlet 可用，IIS GUI 概念缺失 → FTP/WebDAV 管理未验证；用户/组管理与 SMB 功能应正常
- 家庭中文版/单语言版按 Home 处理（SKU 101/102/103/104/105/109/110 表）
- 多 NIC + 防火墙策略对 QUIC 的影响属网络环境范畴，不在应用职责内

## 5. PowerShell 版本矩阵（v1.4.0 多版本兼容）

WinShare Panel 依赖 PowerShell 执行全部 SMB/NFS/FTP/WebDAV cmdlet，
v1.4.0 起应用按「5.1 优先，缺失兜底 7.x」策略自动识别本机可用的 PowerShell
引擎（`electron/lib/nativePaths.ts` 的候选枚举 + `electron/services/psRuntime.ts`
的启动探测），无需用户手动选择。

### 5.1 引擎识别（按优先级）

| 优先级 | 可执行文件 | 引擎 | 安装位置 |
|---|---|---|---|
| 1 | `powershell.exe`（原生位宽） | Windows PowerShell 5.1 | `%SystemRoot%\System32\WindowsPowerShell\v1.0\`（32 位进程经 Sysnative） |
| 2 | `pwsh.exe`（System32 视图） | PowerShell 7.x | 同上目录（PS7 安装时默认注册） |
| 3 | `pwsh.exe`（%ProgramFiles%） | PowerShell 7.x | `%ProgramFiles%\PowerShell\7\`（winget / 手动安装常见位置） |
| 4 | 裸名 `powershell.exe` / `pwsh.exe` | 交给 PATH | 跨平台开发机 / 用户自定义安装位置 |

**优先 5.1 的原因**：本项目全部 cmdlet 按 5.1 调试验证过（IIS
WebAdministration 的 COM 组件只在 5.1 注册、`Get-LocalUser` / SMB cmdlet 面
完整），7.x 下部分行为有差异，5.1 可用时不冒险切引擎。

### 5.2 引擎 × 功能支持

| 能力 | 5.1（powershell.exe） | 7.x（pwsh.exe） |
|---|---|---|
| SMB 共享 / 会话 / 配置（Get-Smb* / Set-Smb*） | ✅ 完整 | ✅ 完整 |
| 本地用户/组（Get-LocalUser / LocalAccounts 模块） | ✅ 完整 | ✅ 完整 |
| NFS（Get-NfsShare / 角色安装） | ✅ 完整 | ✅ 完整 |
| FTP / WebDAV（IIS WebAdministration 的 COM 组件） | ✅ 完整 | 🟡 **COM 组件仅在 5.1 注册**，7.x 下 FTP/WebDAV 站点管理可能不可用——可装回 5.1 后重启应用 |
| 中文 Windows 的代码页处理 | ✅ `-EncodedCommand` 始终 UTF-16LE，5.1 / 7.x 行为一致 | ✅ 一致 |
| `ConvertTo-Json -Depth` | ✅ 显式传 `-Depth 5`，5.1 / 7.x 一致 | ✅ 一致 |
| `.NET` API 面（`Add-Type` / `DllImport` / `SendMessageTimeout`） | ✅ .NET 4.x | 🟡 .NET 8，部分类型名/签名有差异（广播 WM_SETTINGCHANGE 用，7.x 下已验证） |
| `$ProgressPreference='SilentlyContinue'` | ✅ PS3.0+ 内置变量 | ✅ 一致 |

### 5.3 机器形态 × 行为

| 机器形态 | 应用行为 |
|---|---|
| 默认 Win10/11（只有 5.1） | 走 5.1，全部功能完整 |
| 装了 PowerShell 7（5.1 + 7.x 并存） | 仍走 5.1，全部功能完整 |
| 精简系统，只剩 7.x | 自动切到 7.x；SMB/NFS/本地用户完整，**IIS（FTP/WebDAV）可能不可用**（诊断卡会提示装回 5.1） |
| 精简系统，5.1 与 7.x 都缺 | 应用全瘫 → 诊断卡给「sfc /scannow 修复 5.1」或「winget install Microsoft.PowerShell 装 7.x」两条修复路径 |
| 纯 32 位系统 | 只有 32 位 5.1（`Get-LocalUser` 缺失，已知限制）；v1.3.0 的 Sysnative 对齐让 32 位进程取原生 64 位 5.1 规避此缺口 |

### 5.4 版本探测与日志

应用启动时（`main.ts` 的 `whenReady`）：
1. **同步 stat**（不 spawn）：解析出的可执行文件是否存在 → 写 `app.log`
   `[psRuntime]` 前缀一行
2. **异步版本探测**（`lib/psRuntimeProbe.ts`，不阻塞首屏）：spawn 一次
   PowerShell 读 `$PSVersionTable.PSVersion`，把「引擎 + 完整版本号」写
   `app.log`；7.x 时额外写一行提示「IIS COM 仅在 5.1 注册」

渲染层「服务配置 → 系统环境」卡（`OsCompatCard`）展示：
- PowerShell 可用/缺失（红色标签 + 诊断按钮）
- 当前选中的引擎（5.1 / 7.x）
- 本机全部 PowerShell 候选（按优先级列出，标注"使用中"）

## 6. 手工回归矩阵（PowerShell 多版本）

- [ ] 只有 5.1 的机器：全功能正常（基线）
- [ ] 5.1 + 7.x 并存的机器：应用走 5.1，`app.log` 的 `[psRuntime]` 行引擎=5.1
- [ ] 只有 7.x 的机器：SMB/NFS/本地用户正常；FTP/WebDAV 站点管理降级（诊断卡提示）
- [ ] 精简系统（两引擎都缺）：诊断卡显示缺失 + 「sfc /scannow」/「winget install」两条修复建议
- [ ] 32 位包在 64 位系统（Sysnative 命中）：候选全部走原生位宽，`[arch]` 日志行与 v1.3.0 一致
