# 多协议文件共享扩展方案（NFS + FTP + WebDAV）

## Context

WinShare Panel 当前仅支持 SMB 协议管理。用户要求集成 NFS、FTP、WebDAV 协议，使其成为通用文件共享管理工具。目标部署环境：Windows Server（三协议完整可用）与 Windows 11 客户端（NFS 仅客户端检测、FTP/WebDAV 需装 IIS）均要支持，通过能力检测自适应降级。实施范围：三协议全做。UI 采用统一共享页面 + 协议 Tab 方案。

## 架构设计

### 协议适配器抽象层（核心）

新建 `electron/services/protocol/` 目录：

- `ProtocolAdapter.ts` — 定义 `ProtocolAdapter` 接口 + `ProtocolCapabilities` 能力位（supportsCreate/Update/Delete/Toggle/Permissions/Sessions/OpenFiles/ServerConfig/Restart + permissionModel 枚举）。可选方法用 TS `?` 标记，不支持的能力抛 `Errors.unsupported`
- `registry.ts` — adapter 注册表，`getAdapter(proto)` 路由，`adapterList/Create/Update/Delete/Toggle/permissions/sessions` 统一入口函数
- `detect.ts` — `detectProtocols()` 检测各协议安装状态（Get-WindowsFeature Server 版 / Get-WindowsOptionalFeature 客户端版）+ 服务状态 + `installProtocol(proto)` 引导安装
- `adapters/smbAdapter.ts` — 包装现有 `share.ts`/`smb.ts`/`session.ts`/`user.ts` 导出，**原 service 文件不动**
- `adapters/nfsAdapter.ts` — NFS 共享 CRUD（Get/New/Set/Remove-NfsShare）+ 权限（Grant/Revoke-NfsSharePermission）+ 会话（Get-NfsClient）
- `adapters/ftpAdapter.ts` — IIS FTP 站点（New-WebFtpSite / Set-WebConfigurationProperty）+ 授权规则
- `adapters/webdavAdapter.ts` — IIS WebDAV（webdav/authoring 配置 + authoringRules）

**关键复用**（不修改）：`electron/lib/powershell.ts`（runPowerShell/runPowerShellVoid/psQuote/validateName/validatePath）、`lib/audit.ts`、`lib/errors.ts`。

### 数据模型扩展（`electron/types.ts`）

采用判别联合 + 协议专有字段。`Share` 类型加 `protocol: 'smb'|'nfs'|'ftp'|'webdav'` 判别字段，各协议子类型携带专有字段（SmbShare: encrypted/type/hidden；NfsShare: authentication/permission/anonymousUid/Gid/enableUnmappedAccess/allowRootAccess；FtpShare: port/sslPolicy/authMode/siteName；WebdavShare: port/anonymousEnabled/authoringEnabled/siteName）。为兼容现有 SMB 代码，`encrypted?`/`type?`/`hidden?` 保留在 ShareBase 可选。新增 `ProtocolSession`（通用会话）、`ProtocolFeatureState`、`ProtocolDetectionResult`、`CreateShareInput`、`UpdateShareInput` 类型。**保留旧类型别名**（CreateShareOpts 等）确保兼容。

### IPC 通道（`electron/ipc/index.ts`）

**不动现有** `share:*`/`smb:*`/`session:*`/`user:*`/`system:*`/`preset:*` 通道（SMB 零回归）。新增：
- `adapter:list/create/update/delete/toggle/permissions/setPermissions/sessions/closeSession/capabilities` — 统一协议路由，带 protocol 参数
- `nfs:getConfig/setConfig/serviceStatus/restart`、`ftp:*`、`webdav:*` — 协议专有服务器/站点配置
- `protocol:detect/install` — 能力探测 + 引导安装

所有新通道用 `wrap()` 审计包装。`electron/preload.ts` 追加 `adapter`/`nfs`/`ftp`/`webdav`/`protocol` 命名空间。`src/types.ts` re-export + 扩展 `WinShareApi`。

### UI 架构（统一页面 + 协议 Tab）

- `src/pages/Shares.tsx` — 顶部 antd v6 `Tabs items`（全部/SMB/NFS/FTP/WebDAV）。All 视图加 protocol 列（Tag 按协议着色：smb=blue/nfs=purple/ftp=green/webdav=orange），具体协议 Tab 显示专有列。新建 Modal 加 protocol Select，`shouldUpdate` 动态切换表单分支。批量操作 `selectedShares` 改为 `${protocol}:${name}` 复合 key。数据源切换为 `api.adapter.list(activeProtocol)`
- `src/components/PermissionDrawer.tsx` — 根据 `share.protocol` 渲染对应 Panel。新建 `src/components/PermissionPanel/{Smb,Nfs,Ftp,Webdav}PermPanel.tsx`。NTFS Tab 仅 SMB 显示
- `src/components/ProtocolCapabilityBanner.tsx`（新建）— 启动调 `api.protocol.detect()`，未装协议 Tab 灰显 + 「安装」按钮
- `src/pages/Sessions.tsx` — 协议 Tab。SMB/NFS 用 adapter.sessions，FTP/WebDAV 显示 Empty + 「打开 IIS 日志」按钮
- `src/pages/Settings.tsx` — 4 协议子 Tab（SMB 保留现有 4 子 Tab，NFS/FTP/WebDAV 各 1-2 子 Tab）
- `src/stores/uiStore.ts` — 新增 `activeProtocol`/`protocolCaps` + setter
- `src/components/HealthBar.tsx` — 调 protocol.detect 兼顾各协议服务状态
- `src/pages/Dashboard.tsx` + `src/components/CommandPalette.tsx` — 阶段 3 改为 `api.adapter.list()` 聚合跨协议

## 三阶段实施计划

### 阶段 1：抽象层 + SMB 包装 + NFS（~17 人日）

**后端**：新建 `protocol/{ProtocolAdapter,registry,detect}.ts` + `adapters/{smbAdapter,nfsAdapter}.ts` + `services/nfs.ts`。修改 `types.ts`（判别联合 + 新类型）、`ipc/index.ts`（adapter:*/nfs:*/protocol:*）、`preload.ts`、`src/types.ts`

**前端**：修改 `Shares.tsx`（协议 Tab + protocol 列 + 新建 Modal 协议分支）、`uiStore.ts`（activeProtocol）、`PermissionDrawer.tsx`（NFS 分支 + 新建 NfsPermPanel）、`ProtocolCapabilityBanner.tsx`（新建）、`HealthBar.tsx`、`Settings.tsx`（NFS 子 Tab）、`Sessions.tsx`（NFS 会话 Tab）

**验收**：SMB 现有功能零回归；NFS 共享 CRUD + 权限 + 会话可用（Server 环境）；客户端版 NFS Tab 显示降级提示

### 阶段 2：FTP（~13 人日）

**后端**：新建 `adapters/ftpAdapter.ts` + `services/ftp.ts`。修改 `ipc/index.ts`（ftp:*）、`preload.ts`、`detect.ts`（IIS 角色检测）

**前端**：修改 `Shares.tsx`（FTP Tab + 专有列 port/sslPolicy/authMode + 新建 Modal FTP 分支）、`PermissionDrawer.tsx`（FTP 分支 + 新建 FtpPermPanel IIS 授权规则）、`Settings.tsx`（FTP 子 Tab：站点配置 + SSL + 服务状态）、`Sessions.tsx`（FTP Empty + IIS 日志按钮）

**特殊处理**：FTP 共享名=IIS site name（校验冲突）；端口冲突检测；toggle 映射 `Start/Stop-Website`（非 disabled.json）；SSL 配置 `Set-WebConfigurationProperty ftpServer/security/ssl`

**验收**：装 IIS+FTP 角色机器上完整管理 FTP 站点；授权规则可编辑；SSL 策略可切换

### 阶段 3：WebDAV + 统一视图 + 打磨（~13 人日）

**后端**：新建 `adapters/webdavAdapter.ts` + `services/webdav.ts`。修改 `ipc/index.ts`（webdav:*）、`preload.ts`、`system.ts`（getDashboardStats 聚合 adapter.list）

**前端**：修改 `Shares.tsx`（WebDAV Tab + 专有列）、`PermissionDrawer.tsx`（WebDAV 分支 + 新建 WebdavPermPanel authoringRules）、`Dashboard.tsx`（跨协议统计 + 协议着色）、`CommandPalette.tsx`（跨协议搜索）、`Settings.tsx`（WebDAV 子 Tab）、`Sessions.tsx`（WebDAV Empty）。全局打磨：协议图标、Tab 动画、降级提示统一文案

**验收**：WebDAV 站点 CRUD；authoringRules 配置；Dashboard 跨协议统计；CommandPalette 跨 4 协议搜索；未装协议优雅降级

## 关键文件清单

**新建**（15 个）：
- 后端：`electron/services/protocol/{ProtocolAdapter,registry,detect}.ts`、`adapters/{smbAdapter,nfsAdapter,ftpAdapter,webdavAdapter}.ts`、`services/{nfs,ftp,webdav}.ts`
- 前端：`src/components/ProtocolCapabilityBanner.tsx`、`src/components/PermissionPanel/{Smb,Nfs,Ftp,Webdav}PermPanel.tsx`

**修改**（13 个）：
- 后端：`electron/types.ts`、`ipc/index.ts`、`preload.ts`、`services/system.ts`
- 前端：`src/types.ts`、`stores/uiStore.ts`、`pages/{Shares,Sessions,Settings,Dashboard}.tsx`、`components/{PermissionDrawer,HealthBar,CommandPalette}.tsx`

## 风险与降级

| 风险 | 降级策略 |
|------|----------|
| Win11 客户端 NFS 无 Server 角色 | detectProtocols 检测 OS 版本，客户端版 NFS Tab 显示「仅客户端，无法创建共享，需 Windows Server」 |
| IIS 未装 WebAdministration 模块 | detectProtocols 检测模块可用性，未装显示安装引导 |
| FTP/WebDAV 无原生会话 API | Sessions 页显示 Empty + 「打开 IIS 日志目录」按钮 |
| NFS 认证模型与 SMB 完全不同（Kerberos/AUTH_SYS） | permissionModel 字段分支，NfsPermPanel 独立 UI（client 通配符 + ro/rw） |
| 跨协议批量操作部分失败 | 沿用现有 `Promise.allSettled` 模式，统计成功失败数 |
| 共享名跨协议冲突 | selectedShares 用 `${protocol}:${name}` 复合 key |
| Install-WindowsFeature 需管理员 + 重启 | installProtocol 前检查 `system.isAdmin()`，失败提示，安装后提示重启 |
| 现有 SMB 代码访问 `share.encrypted` 等字段 | 类型扩展时这些字段保留可选在 ShareBase，SMB 路径无需改 |

## 验证方法

**静态验证**（每阶段）：
- `pnpm typecheck:node && pnpm typecheck:web` 双层通过
- `pnpm build` 产物正常生成

**功能验证**：
- 阶段 1：SMB 回归（F5/Ctrl+N/Del/Space/拖拽/批量/权限/快照/导入导出全通过）+ NFS 共享 CRUD + 权限 + 会话（需 Server 或启用 NFS）
- 阶段 2：FTP 站点 CRUD + 授权规则 + SSL（需装 IIS+FTP 角色）
- 阶段 3：WebDAV 站点 CRUD + authoringRules + Dashboard 跨协议统计 + CommandPalette 跨协议搜索

**降级验证**：
- 未装协议的 Tab 显示引导安装 Banner（不崩溃）
- 客户端版 NFS Tab 显示降级提示
- FTP/WebDAV Sessions Tab 显示 Empty + 日志按钮

**运行验证**：`pnpm dev` 启动，electron 窗口打开，逐协议 Tab 操作验证。
