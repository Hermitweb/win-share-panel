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
