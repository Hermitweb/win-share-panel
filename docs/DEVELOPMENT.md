# WinShare Panel 开发文档

> Windows 多协议文件共享控制面板（SMB / NFS / FTP / WebDAV）
> 版本：v1.1.0  |  更新日期：2026-10-09

---

## 一、项目概述

### 1.1 项目目标
打造一个原生 Windows 桌面应用，通过可视化控制面板**统一管理本机的四种文件共享协议**——SMB、NFS、FTP、WebDAV，替代系统自带的 `fsmgmt.msc`、零散的 `net share` / `New-SmbShare` / IIS 管理器等多套工具，提供现代化、集成化、可视化的管理体验。协议差异由适配器层（`ProtocolAdapter`）屏蔽，上层 UI 与 IPC 统一路由。

### 1.2 核心价值
- **多协议统一**：一套界面管 SMB / NFS / FTP / WebDAV，协议差异由适配器层屏蔽
- **可视化**：图形化管理共享、权限、会话，告别命令行
- **集成化**：共享管理 + 权限管理 + 会话监控 + 协议配置 + 用户/组管理，一站式
- **高性能**：常驻 PowerShell 进程池 + 并行执行 + 请求去重，首屏与刷新从秒级降至百毫秒级
- **安全加固**：命令注入运行时校验、IPC 边界校验、contextIsolation 隔离、事务回滚
- **实时性**：会话与连接实时监控，异常即时告警

### 1.3 目标用户
- 系统管理员（运维场景）
- 小型企业/团队文件服务器管理者
- 需要频繁管理 Windows 共享的高级用户

---

## 二、技术架构

### 2.1 总体架构

```
┌─────────────────────────────────────────────────────────┐
│                   Electron 桌面应用                       │
│  ┌───────────────────────┐   ┌───────────────────────┐  │
│  │     渲染进程 (UI)      │   │     主进程 (Main)      │  │
│  │  React 19 + TS + Vite │   │  Node.js              │  │
│  │  Ant Design 6+ Tailwind│IPC│  ├─ 共享/用户/组服务    │  │
│  │                       │   │  ├─ 会话监控服务        │  │
│  │  Pages:               │   │  ├─ 协议适配器层        │  │
│  │  · 仪表板              │   │  │  (SMB/NFS/FTP/WebDAV)│  │
│  │  · 共享管理            │   │  ├─ 协议配置/探测服务   │  │
│  │  · 用户权限            │   │  └─ PowerShell 执行器   │  │
│  │  · 会话监控            │   │     └─ 常驻进程池 ◄     │  │
│  │  · 协议配置            │   │                       │  │
│  └───────────────────────┘   │  Windows 集成认证       │  │
│                              │  UAC 管理员提权         │  │
│                              │  单实例锁               │  │
│                              └──────────┬────────────┘  │
└─────────────────────────────────────────┼──────────────┘
                                          │ stdin/stdout 标记协议
                                          │ (常驻 worker 复用)
                           ┌──────────────▼───────────────┐
                           │      Windows 系统层            │
                           │  · SMB: Get/New/Set-Smb*       │
                           │  · NFS: Get/New-NfsShare + 注册表│
                           │  · FTP: IIS WebAdministration  │
                           │  · WebDAV: IIS webdav/authoring│
                           │  · net.exe / WMI / CIM         │
                           │  · icacls (NTFS 权限)          │
                           └───────────────────────────────┘
```

**多协议适配器层**（`electron/services/protocol/`）：四个协议各实现 `ProtocolAdapter` 接口，`registry.ts` 按协议路由；上层 IPC（`adapter:*`）与 UI 无需感知协议差异。详见 [4.7 多协议适配器架构](#47-多协议适配器架构)。

### 2.2 技术栈选型

| 层级 | 技术 | 版本 | 选型理由 |
|------|------|------|----------|
| 桌面框架 | Electron | ^31 | Node.js 生态原生集成，打包成熟 |
| 构建工具 | electron-vite | ^5 | Electron 专用 Vite 集成，HMR 快 |
| 前端框架 | React | ^19 | 生态丰富，组件化，类型友好 |
| 语言 | TypeScript | ^7 | 全栈类型安全 |
| UI 组件库 | Ant Design | ^6 | 企业级管理后台首选，组件齐全（按需加载 + Tree Shaking） |
| 样式方案 | TailwindCSS | ^3 | 原子化 CSS，快速布局 |
| 状态管理 | Zustand | ^5 | 轻量，无样板代码 |
| 路由 | React Router | ^7 | SPA 路由标准方案 |
| 图表 | ECharts | ^6 | 仪表板数据可视化（按需引入 + Tree Shaking） |
| 进程调用 | child_process + 常驻进程池 | 内置 | spawn 长期存活 PowerShell worker，stdin/stdout 复用 |
| 打包工具 | electron-builder | ^25 | Windows 安装包（NSIS）+ 便携版 |
| 测试 | Vitest | ^4 | 单元测试（注入防护 / 事务回滚 / 进程池排队/超时/压测） |

---

## 三、功能模块设计

### 3.1 模块总览

```
WinShare Panel
├── 1. 仪表板 (Dashboard)           ← 总览、统计、快捷操作
├── 2. 共享文件夹管理 (Shares)       ← 核心：增删改查共享
├── 3. 用户与权限管理 (Users)        ← 用户/组、共享权限、NTFS 权限
├── 4. 会话与连接监控 (Sessions)     ← 实时会话、打开文件、强制断开
├── 5. 服务配置 (Settings)          ← 服务与协议参数：SMB/NFS/FTP/WebDAV、权限模板、快照
└── 6. 应用设置 (AppSettings)       ← 应用自身：版本/项目地址/检查更新、偏好与运维、应用与审计日志
```

### 3.2 模块详细设计

#### 模块 1：仪表板
- 共享总数、活动会话数、打开文件数、今日访问量
- 共享访问趋势图（折线图，近 7 天）
- Top 5 热门共享（柱状图）
- 当前活动会话列表（实时刷新，可滚动）
- 系统状态：SMB 服务状态、磁盘空间、CPU/内存
- 快捷操作：新建共享、查看会话、刷新配置

#### 模块 2：共享文件夹管理
- **列表视图**：表格展示所有共享（名称、路径、描述、类型、连接数、状态）
- **新建共享**：表单向导
  - 本地路径选择（文件夹选择器）
  - 共享名、描述
  - 共享类型（普通共享 / 隐藏共享（$ 结尾）/ 特殊共享）
  - **权限模板**（可选）：选择预设模板（只读团队/读写协作/管理员全权）一键填充权限，预览矩阵后可微调
  - 手动设置初始权限（Full/Change/Read，分配给用户/组，与模板二选一或叠加）
  - 选项：缓存、加密、APM（如可用）
- **编辑共享**：修改描述、权限
- **删除共享**：二次确认，可选是否同时取消文件夹 NTFS 共享权限
- **启用/禁用共享**
- **打开本地路径**：在资源管理器中定位
- 筛选与搜索

#### 模块 3：用户与权限管理
- **本地用户列表**：用户名、全名、状态、所属组
- **本地组列表**：组名、描述、成员
- **共享权限管理**（每个共享）
  - 查看当前权限（用户/组 → Full/Change/Read/Deny）
  - 添加/移除用户或组
  - 修改权限级别
- **NTFS 权限查看**（只读，通过 icacls）
  - 显示文件夹的 ACL
- **权限矩阵视图**：共享 × 用户/组 的权限总览

#### 模块 4：会话与连接监控
- **活动会话列表**：客户端、用户名、会话时长、打开文件数、空闲时间、传输字节
- **打开的文件列表**：文件路径、访问用户、打开模式（读/写）、锁定计数
- **操作**
  - 强制断开会话（单个/批量）
  - 关闭打开的文件（单个/批量）
- **实时刷新**：可配置刷新间隔（1s/5s/10s/30s），支持暂停
- **历史记录**（可选，v2）：会话连接/断开日志

#### 模块 5：服务配置（Settings，侧栏「服务配置」）
- **服务器配置**
  - SMB 协议版本启用（SMB1/SMB2/SMB3）
  - 未身份验证的来宾访问
  - 加密设置
  - 审核日志级别
  - 保持连接超时、空闲超时等参数
- **协议面板**：NFS / FTP（含被动端口段）/ WebDAV 的服务参数与共享权限
- **权限模板**：内置模板查看/复制，自定义模板编辑/删除，支持导入导出
- **快照历史**：SMB 配置写入前的自动快照与回滚
- **日志查看**：SMB 相关 Windows 事件日志
- **服务控制**：重启 Server 服务（lanmanserver）
- **危险操作二次确认**

#### 模块 6：应用设置（AppSettings，侧栏「应用设置」）
> 与模块 5 的分工：模块 5 改的是**被管理的服务与协议**，本模块改的是**这个面板自己**。
> 原先它混在模块 5 的页签里，应用级开关（自启/告警/体检）与日志错挂在 SMB 页签下。
- **关于**：版本号与运行时（Electron/Chromium/Node，来自主进程 `system:appInfo`，非前端写死）、
  项目地址、问题反馈、下载页（复制 / 走白名单校验的 `system:openExternal` 在浏览器打开）
- **检查更新**：查 GitHub Releases，只提示不自动下载安装
- **备份与恢复**：应用偏好与运维规则的导出/导入（`state:exportAll/importAll`）
- **偏好与运维**：新手/专家模式与主题、开机自启、告警规则四项、防火墙组内规则、账号安全体检
- **日志**：应用日志（`app.log` 尾部，可复制/导出/打开日志目录）与审计日志（JSONL → 结构化表格）

---

## 四、系统设计

### 4.1 进程模型

采用 Electron 双进程 + IPC 通信：

- **主进程（Main）**：运行在 Node.js 环境，承载所有业务逻辑和 Windows 系统调用
  - 提供 IPC 接口（`ipcMain.handle`）
  - 可选内嵌本地 Express 服务（为未来远程管理预留）
  - 执行 PowerShell 命令（通过 execa）
  - 处理 UAC 提权、Windows 集成认证
- **渲染进程（Renderer）**：React UI，仅负责展示和交互
  - 通过 `preload.ts` 暴露的安全 API 调用主进程

### 4.2 IPC 通道设计

所有 IPC 通道命名遵循 `domain:action` 规范，统一通过 `preload.ts` 的 `contextBridge.exposeInMainWorld('winshare', api)` 暴露。IPC 入口对协议名 / 共享名 / 路径做运行时校验（见 [7.2 命令注入防护](#72-命令注入防护)）。API 命名空间概览（完整定义见 [electron/preload.ts](../electron/preload.ts)）：

```typescript
interface WinShareApi {
  // SMB 共享（兼容旧入口，内部路由到 adapter）
  share: { list, get, create, update, delete, toggle, permissions, exportConfig,
           importConfig, connections, openFiles, closeOpenFiles }
  // 用户与组
  user:  { list, get, groups, sharePermissions, sharePermissionsForUser, setSharePermissions,
           ntfsPermissions, create, update, delete, setPassword, enable, disable, rename }
  group: { create, delete, update, rename, addMember, removeMember }
  // 会话监控
  session: { list, files, close, closeFile }
  // === 多协议统一路由（核心）===
  adapter: {
    list(protocol?): Promise<Share[]>
    create(input: CreateShareInput): Promise<Share>
    update(name, input: UpdateShareInput): Promise<Share>
    delete(protocol, name): Promise<void>
    toggle(protocol, name, enabled): Promise<void>
    permissions(protocol, name): Promise<SharePermission[]>
    setPermissions(protocol, name, perms): Promise<void>      // 事务回滚
    sessions(protocol): Promise<ProtocolSession[]>
    closeSession(protocol, sessionId): Promise<void>
    capabilities(): Promise<Record<Protocol, ProtocolCapabilities | null>>
  }
  // 各协议服务器级配置 + 服务控制
  smb:    { getConfig, setConfig, restoreDefault, defaultConfig, serviceStatus,
            restart, start, stop, listSnapshots, rollback }
  nfs:    { getConfig, setConfig, restoreDefault, defaultConfig, serviceStatus, restart, start, stop }
  ftp:    { getConfig, setConfig, restoreDefault, defaultConfig, serviceStatus, restart, start, stop }
  webdav: { getConfig, setConfig, restoreDefault, defaultConfig, serviceStatus, restart, start, stop }
  // 协议探测 + 引导安装
  protocol: { detect(): Promise<ProtocolDetectionResult>; install(protocol): Promise<void> }
  // 权限预设模板
  preset: { list, get, save, update, delete, duplicate, apply, export, import }
  // 系统
  system: { currentUser, isAdmin, dashboard, auditLog, health }
  // 窗口
  window: { minimize, toggleMaximize, close, isMaximized, onMaximizeChange, showBalloon }
}
```

> **设计要点**：`adapter:*` 是多协议统一入口，第一个参数均为 `protocol: Protocol`（`'smb' | 'nfs' | 'ftp' | 'webdav'`），由 `registry` 路由到对应适配器。NFS/FTP/WebDAV 的**站点级**配置经 `adapter` 路由，**服务器级**配置经各自 `nfs:` / `ftp:` / `webdav:` 命名空间。

### 4.3 Windows 系统调用层

通过 `PowerShell 执行器`（[electron/lib/powershell.ts](../electron/lib/powershell.ts)）封装所有 Windows 操作，统一处理：
- 命令执行：经**常驻进程池**（[electron/lib/powershellPool.ts](../electron/lib/powershellPool.ts)）派发给长期存活的 `powershell.exe` worker；`WINSHARE_PSPOOL=0` 时回退单次 `execFile`
- 命令编码：UTF-16LE→Base64（`-EncodedCommand`），规避中文 GBK 代码页问题
- 参数转义 + 运行时类型校验（`psBool` / `psNumber` / `psEnum`，防注入）
- 错误处理与 JSON 解析（`ConvertTo-Json -Depth 5 -Compress`）
- 超时（默认 15s）+ 可重试错误自动重试；只读协议查询 `retries: 0`

#### Windows 命令映射表

| 协议 | 功能 | PowerShell 命令 | 备注 |
|------|------|----------------|------|
| SMB | 列出共享 | `Get-SmbShare` | 含特殊共享 |
| SMB | 创建共享 | `New-SmbShare -Name X -Path Y -FullAccess A` | 权限参数化 |
| SMB | 删除共享 | `Remove-SmbShare -Name X` | |
| SMB | 共享权限 | `Grant/Revoke-SmbShareAccess` | 事务回滚 |
| SMB | 会话/文件 | `Get-SmbSession / Get-SmbOpenFile / Close-*` | |
| SMB | 服务器配置 | `Get/Set-SmbServerConfiguration` | 快照/回滚 |
| NFS | 列出共享 | `Get-NfsShare` | 需 NFS 角色已安装 |
| NFS | 创建共享 | `New-NfsShare -Name X -Path Y -Authentication sys -Permission rw -AnonymousGid N` | psBool/psNumber 校验 |
| NFS | 服务器配置 | 注册表 `HKLM\SYSTEM\...NfsServer` + `Restart-Service NFS` | |
| FTP | 站点管理 | `New-WebFtpSite` + `Set-WebConfigurationProperty`（IIS WebAdministration） | 站点不存在用 `Get-Website \| Where` 过滤；`retries:0` |
| FTP | 认证模式 | `Set-WebConfigurationProperty ...ftpAuthentication` | try/catch 处理锁定节 |
| WebDAV | 站点管理 | `New-Website` + `Add-WebConfigurationProperty`（`system.webServer/webdav/authoring`） | 完整 filter 路径；`enableAuthoring` try/catch |
| WebDAV | 授权规则 | `Add/Clear-WebConfigurationProperty`（`webdav/authoringRules`） | 事务回滚 |
| 通用 | 本地用户 | `Get/New/Set/Remove-LocalUser` | 内置用户受保护 |
| 通用 | 本地组 | `Get/New-LocalGroup / Add/Remove-LocalGroupMember` | 内置组受保护 |
| 通用 | NTFS 权限 | `Get-Acl` / `icacls` | icacls 输出更友好 |
| 通用 | 服务控制 | `Get/Start/Stop/Restart-Service` | LanmanServer / NFS / FTP（MSFTPSVC）/ W3SVC |

### 4.4 认证与权限

- **Windows 集成认证**：
  - 启动时通过 `os.userInfo()` 和 `process.env.USERNAME` 获取当前 Windows 登录用户
  - 使用 `net session` 命令是否成功来检测管理员权限（或尝试访问需管理员权限的资源）
- **UAC 提权**：
  - 应用启动时检测是否为管理员，非管理员时通过 `electron.app.relaunch` + `--as-admin` 触发 UAC 提示
  - 使用 `electron-builder` 的 `requestedExecutionLevel: "requireAdministrator"` 打包配置
- **操作授权**：所有写操作（创建/删除/修改/断开）需二次确认弹窗
- **审计日志**：所有写操作记录到 `%APPDATA%/WinSharePanel/audit.log`

### 4.5 窗口风格化（Electron）

应用窗口外壳采用**无边框 + 自定义标题栏 + 系统磨砂材质**，与浅蓝调磨砂玻璃风格统一。详细视觉规范见 [UI_DESIGN.md 第九章](./UI_DESIGN.md)。

- **无边框窗口**：`frame: false`，自定义标题栏（拖拽区 + 窗口控制按钮），按钮悬浮浅蓝高亮、关闭键悬浮蜜桃粉
- **系统磨砂材质**：
  - Windows 11：`backgroundMaterial: 'acrylic'`（亚克力）或 `'mica'`（云母）
  - Windows 10：`transparent: true` + CSS `backdrop-filter` 模拟磨砂
  - 失焦回退色：`backgroundColor: '#F4FAFD'`（极浅蓝雾），保持品牌一致
- **版本检测**：主进程启动时检测 Windows 版本，自动选择材质策略
- **窗口控制 IPC**：`window:minimize` / `window:toggleMaximize` / `window:close` / `window:isMaximized`，由 `preload.ts` 暴露
- **窗口状态联动**：监听 `maximize` / `unmaximize` 事件，渲染进程同步切换圆角与阴影类名；最大化时去圆角
- **白屏防御**：`show: false` + `ready-to-show` 后再显示
- **图标统一**：窗口图标 `icon: 'resources/icon.ico'`，标题栏/系统托盘图标用 `resources/logo.png`（16×16），官方 logo 见 UI_DESIGN.md 6.5 节
- 主进程 BrowserWindow 配置示例与标题栏组件要点见 UI_DESIGN.md 9.5 / 9.6 节

### 4.6 数据模型

```typescript
// 共享
interface Share {
  name: string
  path: string
  description: string
  type: 'Disk' | 'IPC' | 'Printer' | 'Special'
  hidden: boolean          // 是否 $ 结尾
  concurrentUsers: number
  status: 'Enabled' | 'Disabled'
  cached: boolean
  encrypted: boolean
}

// 本地用户
interface LocalUser {
  name: string
  fullName: string
  enabled: boolean
  description: string
  groups: string[]
}

// 共享权限
interface SharePermission {
  shareName: string
  account: string          // 用户或组名
  accountType: 'User' | 'Group'
  access: 'Full' | 'Change' | 'Read' | 'NoAccess'
  deny: boolean
}

// SMB 会话
interface SmbSession {
  clientId: string
  clientUserName: string
  clientComputerName: string
  sessionStartTime: Date
  clientOpenFiles: number
  clientIdleTime: number   // 秒
  bytesReceived: number
  bytesSent: number
}

// 打开的文件
interface SmbOpenFile {
  fileId: string
  path: string
  clientUserName: string
  clientComputerName: string
  lockCount: number
  relativeOpenTime: number // 秒
}

// SMB 服务器配置
interface SmbServerConfig {
  enableSMB1Protocol: boolean
  enableSMB2Protocol: boolean
  enableSMB3Protocol: boolean
  enableGuestUserAccess: boolean
  enableInsecureGuestLogons: boolean
  auditSmb1Access: boolean
  requireSecuritySignature: boolean
  enableMultiChannel: boolean
  announceServer: boolean
  unauthenticatedUsersTimeLimit: number
  // ... 其他参数
}

// 权限预设模板
interface PermissionPreset {
  id: string
  name: string                 // 只读团队 / 读写协作 / 管理员全权
  description: string
  builtIn: boolean             // 内置不可删
  entries: PresetEntry[]
}

interface PresetEntry {
  account: string              // 支持占位符：{Everyone} {Administrators} {CurrentUser}
  accountType: 'User' | 'Group'
  access: 'Full' | 'Change' | 'Read'
}
```

多协议扩展类型（节选，完整定义见 [electron/types.ts](../electron/types.ts)）：

```typescript
type Protocol = 'smb' | 'nfs' | 'ftp' | 'webdav'

interface ProtocolCapabilities {
  installed: boolean                 // 协议是否已安装
  createShare: boolean
  deleteShare: boolean
  updateShare: boolean
  toggleShare: boolean
  permissions: boolean               // 是否支持权限管理
  sessions: boolean                  // 是否支持会话监控
  serverConfig: boolean              // 是否暴露服务器级配置
}

interface CreateShareInput {
  protocol: Protocol
  name: string
  path: string
  description?: string
  // 各协议特有字段（NFS authentication/permission/anonymousGid、FTP bindings/ssl、
  // WebDAV authoringRules 等）—— 由适配器各自解析，上层统一透传
  [key: string]: unknown
}

interface ProtocolSession {
  protocol: Protocol
  sessionId: string
  user: string
  computer: string
  // ...
}
```

### 4.7 多协议适配器架构

`electron/services/protocol/` 实现**策略模式**，将协议差异收敛到适配器层：

- **`ProtocolAdapter` 接口**（[ProtocolAdapter.ts](../electron/services/protocol/ProtocolAdapter.ts)）：必选 `listShares` / `createShare` / `deleteShare`；可选 `updateShare` / `toggleShare` / `getPermissions` / `setPermissions` / `listSessions` / `closeSession` / `getConfig` / `setConfig` / `restoreDefault` / `getServiceStatus` 等（用 `?` 标记，不支持的能力抛 `Errors.unsupported`）。
- **`registry.ts`**：注册四个适配器（smb / nfs / ftp / webdav），按 `protocol` 路由 `adapter:*` IPC 调用。
- **`detect.ts`**：探测各协议安装状态与能力（`ProtocolCapabilities`），只读查询 `retries: 0`，失败返回空结果而非抛错。
- **适配器实现**：
  - `smbAdapter.ts`：`Smb*` cmdlet，`createShare` 含 try/catch + 孤儿共享清理
  - `nfsAdapter.ts`：`New-NfsShare` + 注册表配置，`createShare` 失败清理孤儿共享
  - `ftpAdapter.ts`：IIS `New-WebFtpSite` + `Set-WebConfigurationProperty`，`createShare` 失败 `Remove-Website` 清理孤儿站点
  - `webdavAdapter.ts`：IIS `New-Website` + `webdav/authoring` 配置（完整 filter 路径），`createShare` 含 `enableAuthoring` 校验 + 失败清理孤儿站点

### 4.8 PowerShell 常驻进程池

> 详细设计见 [.trae/documents/powershell-process-pool-design.md](../.trae/documents/powershell-process-pool-design.md)

**背景**：原 `runPowerShell` 每次 `execFile` 拉起新 `powershell.exe`，每条命令承担 ~300–500ms 的 CLR+引擎+模块启动开销。常驻进程池将命令执行降至 ~30–80ms/条。

**实现**（[electron/lib/powershellPool.ts](../electron/lib/powershellPool.ts)）：
- **N 个长期存活 worker**（默认 3，`WINSHARE_PS_POOL_SIZE` 可调），每个加载一段"服务端脚本"（经 `-EncodedCommand` 启动）。
- **stdin/stdout 标记协议**：载荷 = `encodeCommand(mode + '\u0000' + command)`（Base64）；worker 读取→`[ScriptBlock]::Create` 执行→JSON 模式 `ConvertTo-Json`、VOID 模式丢弃输出→写带 token 标记 `___PS_OK_<token>___` / `___PS_ERR_<token>___\n<msg>\n___PS_ERR_END_<token>___`。
- **派发**：命令入队→找空闲 worker 派发；无空闲且未达容量则 spawn；全忙则排队。
- **超时**：每命令独立计时，超时 reject + kill 中毒 worker + 补 spawn。
- **崩溃**：worker.exit 时 reject 在飞命令（标记可重试，由 `runPowerShell` 换新 worker 重试）+ 移除补 spawn。
- **错误语义镜像 execFile**：不设全局 `$ErrorActionPreference`，try/catch 仅捕获终止性错误（等价非零退出码→抛），非终止性错误被吞（等价零退出码→返回输出）。
- **生命周期**：`app.whenReady()` 后 `prewarmPool()` 后台预热 1 个 worker（不阻塞首屏）；`before-quit` + `will-quit` 兜底 `shutdownPool()`，确保无 `powershell.exe` 残留。
- **安全阀**：`WINSHARE_PSPOOL=0` 禁用池，回退 `execOnce`（单次 `execFile` + `-EncodedCommand`）。
- **可测性**：`WorkerFactory` 注入，测试用伪造流控制响应，池的排队/派发/超时/崩溃逻辑完全可单测。

`runPowerShell` / `runPowerShellVoid` 签名与行为不变，100+ 调用点与全部既有测试零改动。

### 4.9 性能优化

| 优化点 | 改动 | 效果 |
|--------|------|------|
| 协议探测并行 | `detect.ts` 多协议探测改 `Promise.allSettled` 并行（3 并发） | 探测耗时 ≈ 最慢一个而非累加 |
| 协议探测去重 | in-flight Promise 缓存，并发 `protocol:detect` 共享同一请求 | 避免重复 PowerShell 调用 |
| 共享列表并行 | 四协议 `adapter:list` 改 `Promise.allSettled` 并行（4 并发） | 列表刷新 ≈ 最慢一个 |
| 仪表盘并行 | 仪表盘数据采集改 `Promise.allSettled` 并行（5 并发） | 首屏从 ~1.5–2.5s 降至百毫秒级 |
| PowerShell 池 | 常驻 worker 复用（见 4.8） | 单命令 ~300ms → ~30–80ms |

只读协议查询统一 `retries: 0`，避免协议未安装时的重试延迟；失败返回空数组而非抛错，保证主流程不被个别协议拖垮。

### 4.10 事务补偿与孤儿清理

**`setPermissions` 事务回滚**（四协议统一）：
1. **备份**：读取当前权限并记录（`[setPermissions:<protocol>] 已备份当前权限: N 条`）。
2. **应用**：清空旧权限 + 逐条授予新权限。
3. **失败回滚**：任一步失败 → 还原备份权限，记录四个时间点状态：backup / rollback-trigger / pre-rollback / post-rollback。
4. **成功**：`[setPermissions:<protocol>] 权限设置成功`。

**`createShare` 孤儿清理**（四协议统一）：
- 创建过程任一步失败 → 清理已创建的残留资源（SMB/NFS `Remove-*Share`，FTP/WebDAV `Remove-Website`），避免孤儿共享/站点泄漏。
- FTP/WebDAV 额外校验：`createShare` 含 `enableAuthoring`（WebDAV）/ 认证模式（FTP）helper，失败即 `Remove-Website` 清理。

---

## 五、项目结构

```
win-share-panel/
├── docs/
│   ├── DEVELOPMENT.md              # 本开发文档
│   └── UI_DESIGN.md                # UI 设计规范
├── .github/workflows/
│   └── release.yml                 # tag 触发自动构建并发布 Release
├── .trae/documents/                # 设计稿（进程池/共享自定义/协议配置/用户权限）
├── electron/                       # 主进程
│   ├── main.ts                     # 入口（窗口/UAC/单实例锁/IPC/托盘/进程池生命周期）
│   ├── preload.ts                  # contextBridge 安全 API（多协议路由）
│   ├── ipc/
│   │   └── index.ts                # IPC 路由注册 + 边界校验
│   ├── services/                   # 业务服务层
│   │   ├── share.ts / user.ts / group.ts / session.ts
│   │   ├── smb.ts / nfs.ts / ftp.ts / webdav.ts   # 各协议服务器级配置/服务控制
│   │   ├── preset.ts / system.ts
│   │   └── protocol/               # 多协议适配器层
│   │       ├── ProtocolAdapter.ts  #   统一接口
│   │       ├── registry.ts         #   协议路由
│   │       ├── detect.ts           #   协议能力探测
│   │       └── adapters/           #   smb/nfs/ftp/webdav 适配器 + 单元测试
│   ├── lib/
│   │   ├── powershell.ts           # PowerShell 执行器（池化 + 回退 execFile）
│   │   ├── powershellPool.ts       # 常驻进程池 / Persistent pool
│   │   ├── audit.ts                # 审计日志
│   │   └── errors.ts               # 统一错误定义
│   └── types.ts                    # 主进程类型（多协议）
├── src/                            # 渲染进程（React）
│   ├── main.tsx                    # React 入口
│   ├── App.tsx                     # 根组件 + 路由
│   ├── pages/                      # Dashboard/Shares/Users/Sessions/Settings
│   ├── components/                 # Layout/TitleBar/各协议设置面板/详情抽屉/创建弹窗
│   ├── stores/                     # Zustand（uiStore 等）
│   ├── hooks/                      # useTickEffect 等
│   ├── utils/                      # 工具函数
│   ├── api.ts                      # preload API 封装
│   └── types.ts
├── resources/                      # 静态资源：logo.png(标题栏·托盘)/icon.ico(打包图标)
├── vitest.config.ts                # 单元测试配置
├── package.json
├── electron.vite.config.ts
├── electron-builder.yml            # 打包配置
├── tsconfig.json
├── tsconfig.node.json
├── tailwind.config.js
└── .gitignore
```

---

## 六、开发计划

分 6 个阶段迭代开发，每个阶段可独立验证。

### 阶段 1：项目骨架与基础设施
- 初始化 electron-vite + React + TS 项目
- 集成 Ant Design（按需加载）、TailwindCSS、React Router、Zustand
- 配置 Design Tokens（CSS 变量 + Tailwind 扩展），统一浅蓝调磨砂玻璃风格
- **窗口风格化**：无边框窗口 + 自定义标题栏 + 系统磨砂材质（Win11 Acrylic / Win10 CSS 模拟）+ 窗口控制 IPC
- 搭建主进程/渲染进程/IPC 基础架构
- 实现 UAC 提权与 Windows 集成认证（拒绝时优雅降级提示，P8）
- 实现 PowerShell 执行器（**内置超时 15s + 自动重试 2 次，P1**）与输出解析器（**强制 ConvertTo-Json + 结构校验，P2**）
- 实现审计日志模块（轮转上限 5MB×10，P9）
- **崩溃捕获**：electron crashReporter + 本地错误日志轮转（P3）
- **交付**：可启动的应用骨架，风格化磨砂玻璃窗口，命令调用具备超时/重试/崩溃捕获

### 阶段 2：共享文件夹管理（核心）
- 实现共享列表（Get-SmbShare）
- 实现新建共享向导（New-SmbShare，含路径选择、权限初设）
- 实现编辑/删除/启停共享
- 共享列表筛选搜索
- **拖拽创建共享**：拖文件夹进窗口自动填路径（S2）
- **批量操作**：多选批量启停/删除共享
- **权限预设模板**：只读/读写/全权三套模板一键套用（S6）
- **共享配置导出/导入（JSON）**：换电脑迁移（S3）
- 快捷键：Del 删除、Ctrl+N 新建
- **交付**：完整共享 CRUD + 拖拽 + 批量 + 模板 + 导入导出

### 阶段 3：用户与权限管理
- 实现本地用户/组列表
- 实现共享权限查看与修改（Grant/Revoke-SmbShareAccess）
- 实现 NTFS 权限查看（icacls）
- 实现权限矩阵视图
- **权限矩阵导出**（CSV/JSON）：备份权限分配
- **交付**：可视化权限管理 + 矩阵导出

### 阶段 4：会话与连接监控
- 实现活动会话列表（Get-SmbSession），实时刷新
- 实现打开文件列表（Get-SmbOpenFile）
- 实现断开会话、关闭文件
- 刷新间隔配置与暂停
- **系统托盘常驻 + 开机自启**（S5）
- **会话异常托盘气泡告警**（新连接/可疑暴力破解）
- **交付**：实时监控面板 + 托盘常驻告警

### 阶段 5：SMB 配置与仪表板
- 实现 SMB 服务器配置读写（Get/Set-SmbServerConfiguration）
- 实现服务状态查看与重启
- 实现仪表板（统计卡片、趋势图、Top 共享、活动会话）
- 集成 ECharts（按需引入）
- **SMB 配置快照/回滚**：改配置前自动存带时间戳快照，一键回退（S7/P4）
- **健康检查**：SMB 服务异常时面板顶栏告警
- **仪表板报表导出**（PNG/CSV）
- **交付**：完整仪表板与配置 + 快照回滚 + 健康检查

### 阶段 6：测试、优化与打包
- 单元测试（Vitest）：服务层、解析器
- E2E 测试（Playwright）：关键流程
- 错误处理与边界优化（错误分级 + 友好提示）
- **命令面板 Ctrl+K**：全局快速跳转 + 搜索 + 执行命令（S1）
- **快捷键体系完善**：F5 刷新、Del/Ctrl+N/Esc/Space 等（S8）
- **全局搜索**：跨共享/用户/会话统一检索
- **轻量优化**：ECharts 按需、AntD Tree Shake（P6）
- **安全审计**：命令注入复测、IPC 通道白名单、contextIsolation 严格隔离（P5/P10）
- electron-builder 打包为 NSIS 安装包 + **便携版 portable**（S10）
- **electron-updater 自动更新**
- 安装包图标、签名（可选）
- **交付**：可分发 Windows 安装包 + 便携版，含命令面板与自动更新

---

## 七、安全设计

### 7.1 权限与认证
- 应用以管理员权限运行（UAC `requireAdministrator`）
- Windows 集成认证，无需独立账号系统
- 所有写操作二次确认
- **单实例锁**：`app.requestSingleInstanceLock()`，二次启动激活已有窗口而非新开进程
- **受保护资源**：系统特殊共享（ADMIN$/IPC$/C$）与内置用户/组禁止删除

### 7.2 命令注入防护
- **`-EncodedCommand` 编码**：所有命令经 UTF-16LE→Base64 编码传递，规避中文 GBK 代码页问题与 shell 解析注入
- **运行时类型校验**（[electron/lib/powershell.ts](../electron/lib/powershell.ts)）：
  - `psBool(v)`：布尔字段强制 `true`/`false`，拒绝任意字符串拼入
  - `psNumber(v)`：数字字段强制有限数，拒绝非数字注入
  - `psEnum(v, allowed)`：枚举字段白名单校验（如 NFS `Authentication`、`Permission`；FTP `sslPolicy`）
  - `psQuote` / `psEscapeSingle`：字符串单引号转义
  - `validateName` / `validatePath`：共享名/路径白名单校验
- **IPC 边界校验**：协议名 / 共享名 / 路径在 IPC 入口运行时校验，非法值直接拒绝
- **只读查询容错**：`adapterList` / `adapterGetPermissions` / `adapterSessions` / `nfs.getConfig` 失败返回空数组而非抛错，避免错误信息泄露

### 7.3 Electron 渲染进程隔离
- `contextIsolation: true`：渲染进程与 Node.js 上下文隔离
- `nodeIntegration: false`：渲染进程不直接访问 Node
- `sandbox: true`：预加载脚本沙箱化
- `preload.ts` 仅经 `contextBridge.exposeInMainWorld` 暴露白名单 API，无 `ipcRenderer.on` 任意通道

### 7.4 审计日志
- 记录所有写操作：时间、操作人、操作类型、目标、结果
- 日志存储：`%APPDATA%/WinSharePanel/audit.log`
- 日志轮转：单文件 5MB，保留 10 份

### 7.5 危险操作清单（需二次确认）
- 删除共享 / 强制断开会话 / 关闭打开的文件
- 修改各协议服务器配置 / 重启服务
- `setPermissions`（事务回滚兜底）

### 7.6 事务完整性
- `setPermissions`：备份→应用→失败回滚（见 [4.10 事务补偿](#410-事务补偿与孤儿清理)）
- `createShare`：失败清理孤儿共享/站点
- SMB 配置：变更前自动快照，支持一键回滚

---

## 八、风险评估

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| 不同 Windows 版本 SMB cmdlet 差异 | 部分 API 不可用 | 启动时检测版本，降级到 net.exe 命令；最低支持 Win10 1809 |
| UAC 提权失败 | 应用无法运行 | 提供详细错误提示，引导用户手动以管理员运行 |
| PowerShell 执行策略限制 | 命令执行失败 | 使用 `-NoProfile -ExecutionPolicy Bypass`，仅执行内联命令不加载脚本 |
| 大量会话/文件时性能问题 | UI 卡顿 | 列表分页、虚拟滚动；重操作放主进程异步执行 |
| Electron 安装包体积大（~80MB） | 分发不便 | 接受现状；如需轻量可评估迁移到 Tauri（v2 考虑） |
| 关闭文件可能导致用户数据丢失 | 数据完整性 | 强提示 + 二次确认 + 记录审计 |

---

## 九、环境要求

### 9.1 开发环境
- Windows 10 1809+ / Windows 11 / Windows Server 2019+
- Node.js 18 LTS+
- npm 9+ 或 pnpm 8+
- PowerShell 5.1+（系统自带）
- 管理员权限的开发终端

### 9.2 运行环境
- Windows 10 1809+ / Windows 11 / Windows Server 2019+
- SMB 服务（lanmanserver）已安装（默认）
- 管理员权限

---

## 十、极客体验与韧性设计

> 极客用户 & 效率专家视角的增强设计：提升爽感、降低维护成本、防止踩坑。

### 10.0 用户体验批1（Unreleased）：新手上路 / 日常运维 / 故障自助

按"谁在痛、痛多深"落地 ★ 项，主进程底座与渲染层入口的对应关系：

| 能力 | 主进程 | 渲染层入口 | 诚实边界 |
|---|---|---|---|
| 一键诊断 | `services/diagnose.ts` + `diagnose:run/applyFix` | 共享页工具栏 / 行内「诊断此共享」/ 服务配置页 / 命令面板 | 修复动作需管理员；路径一律服务端重解析后走 `validatePath` |
| 操作回收站 | `lib/stateStore.ts` journal + `services/journal.ts` | 共享页「操作回收站」抽屉 / 命令面板 | 仅 SMB 的删除/禁用/权限变更可撤销；create 与非 SMB 删除只留档 |
| 磁盘水位 | `services/disk.ts`（`Get-PSDrive`） | 共享页行内标 + 仪表板概览 | UNC 无盘符不显示水位（未知即未知） |
| 防火墙组内规则 | `services/firewall.ts`（`WinShare Panel` 组） | 应用设置页防火墙卡 + 诊断修复按钮 | 只增删本应用组内规则，不动系统/第三方规则 |
| 首启向导 | `disk:suggestRoot` + `appstate.json` | `components/FirstRunGuide`（`App.tsx` 的 FirstRunMount） | 空共享 + 未标记 + 新手模式才弹；「以后再说」只关本次会话 |
| 双模式 | `appState.advancedMode` | 应用设置页开关，全站读取 | 新手模式只收敛展示，不改变已保存的配置值 |
| 趋势/告警 | 主进程 5min 采样 + `appState.alertRules` | 仪表板趋势卡与「需要关注」 | 服务未起跳过采样（不写假点）；字段缺失的检查项如实省略 |
| 权限报告 | —（复用既有权限矩阵数据） | 权限矩阵「导出报告(HTML)」 | 仅共享权限，未含 NTFS 有效权限 |

约定：跨层类型集中在 `electron/types.ts` 且该文件保持**零 import**——渲染层 `tsconfig.json`
没有 node types，一旦让它反向 import 服务实现，就会把 `powershellPool` 一类 node 依赖
拖进渲染层类型图（本批修过一次这样的回归）。

### 10.1 懒人化体验
- **命令面板（Ctrl+K）**：全局快速跳转 + 搜索共享/用户/会话 + 直接执行命令
- **拖拽创建共享**：拖文件夹进窗口 → 自动填路径 → 弹权限预设 → 一键完成
- **权限预设模板**：「只读团队」(Everyone→Read) /「读写协作」(CurrentUser→Change, Everyone→Read) /「管理员全权」(Administrators→Full, CurrentUser→Full) 三套内置模板一键套用；支持自定义模板，存 `%APPDATA%/WinSharePanel/presets.json`；套用支持 overwrite/merge 两种模式（新建默认 overwrite，编辑默认 merge）
- **快捷键体系**：F5 刷新、Del 删除、Ctrl+N 新建、Esc 关弹窗、Space 切换启用、Ctrl+K 命令面板
- **批量操作**：共享多选批量启停/删除；会话批量断开；权限矩阵批量改
- **系统托盘常驻 + 开机自启**：后台监控，有连接即气泡提醒
- **全局搜索**：跨共享/用户/会话统一检索

### 10.2 数据主权与安全
- **共享配置导出/导入（JSON）**：换电脑一键迁移所有共享与权限
- **SMB 配置快照/回滚**：改配置前自动存带时间戳快照，一键回退
- **权限矩阵导出**（CSV/JSON）：备份权限分配
- **审计日志导出 + 轮转上限**：日志可导出，单文件 5MB 保留 10 份
- **便携版分发**：免安装 exe，U 盘即开即用，环境零依赖

### 10.3 异常自我修复
- **PowerShell 调用超时 + 自动重试**：统一封装，默认超时 15s，失败重试 2 次
- **错误分类与友好提示**：错误分级（网络/权限/参数/系统），人话提示而非 raw PS 错误
- **electron crashReporter + 错误日志轮转**：崩溃可复盘
- **健康检查**：SMB 服务状态轮询，异常时面板顶栏告警
- **UAC 拒绝优雅降级**：非管理员运行时明确提示而非闪退
- **白屏防御**：show:false + ready-to-show

### 10.4 轻量化与低依赖
- **移除 Express**：纯 IPC 通信，不引入 HTTP 服务
- **ECharts 按需引入**：仅引入仪表板用到的图表模块 + Tree Shaking
- **Ant Design 按需加载 + Tree Shaking**
- **Electron 语言包收敛**：`electronLanguages: [zh-CN, en-US]`，每架构省 37.4 MB（实测，见 11.6）
- **更新策略（如实）**：**未集成 electron-updater**（它需要 electron-builder `publish` 配置、代码签名与线上 `latest.yml` 三件套）；应用内「检查更新」查 GitHub Releases 最新版并提示，**不自动下载安装**，由用户手动取新包
- **部署定位**：本应用为 Windows 本地桌面应用（管理本机 SMB 必须在 Windows 跑 PowerShell），Vercel/Railway/树莓派均不适用；低成本方案聚焦便携版 + 免安装

### 10.5 预防性清单（防踩坑）
| 编号 | 建议 | 防的坑 |
|------|------|--------|
| P1 | PS 命令统一超时 + 重试封装 + 常驻进程池 | 命令卡死致面板无响应；进程启动开销 |
| P2 | 输出强制 ConvertTo-Json + 结构校验 | 输出漂移致解析崩溃 |
| P3 | crashReporter + 错误日志轮转 | 崩溃后无迹可寻 |
| P4 | 配置变更前自动快照 + setPermissions 事务回滚 | 误改致共享全断无法回退 |
| P5 | `-EncodedCommand` 编码 + psBool/psNumber/psEnum 运行时校验 + IPC 边界校验 | 命令注入 |
| P6 | 移除 Express / ECharts 按需 / AntD Tree Shake | 包膨胀启动慢 |
| P7 | Win10/Win11 双材质策略 + 降级 | Win10 风格断裂白屏 |
| P8 | UAC 拒绝优雅降级提示 | 非管理员闪退 |
| P9 | 审计日志导出 + 轮转上限 | 日志无限增长 |
| P10 | IPC 通道白名单 + contextIsolation | 渲染层 XSS 直通系统命令 |

---

## 十一、附录

> **UI 视觉设计规范**详见独立文档：[UI_DESIGN.md](./UI_DESIGN.md)
> 风格：浅蓝调 · 轻渐变扁平化 · 新拟态柔光磨砂玻璃风。前端开发以该文档的 Design Tokens 为单一事实来源。

### 11.1 关键依赖（package.json 节选）

```json
{
  "dependencies": {
    "electron": "^31.3.0",
    "react": "^19.2.8",
    "react-dom": "^19.2.8",
    "react-router-dom": "^7.18.2",
    "antd": "^6.5.3",
    "@ant-design/icons": "^6.3.2",
    "zustand": "^5.0.14",
    "echarts": "^6.1.0",
    "echarts-for-react": "^3.0.6",
    "dayjs": "^1.11.21"
  },
  "devDependencies": {
    "electron-vite": "^5.0.0",
    "electron-builder": "^25.0.0",
    "typescript": "^7.0.2",
    "vite": "^7.3.6",
    "@vitejs/plugin-react": "^5.2.0",
    "tailwindcss": "^3.4.13",
    "vitest": "^4.1.10",
    "@types/node": "^20.14.0",
    "@types/react": "^19.2.18",
    "@types/react-dom": "^19.2.4"
  }
}
```

### 11.2 PowerShell 执行器（进程池 + EncodedCommand）

所有 PowerShell 命令经 UTF-16LE→Base64（`-EncodedCommand`）编码传递，并由常驻进程池派发（`WINSHARE_PSPOOL=0` 时回退单次 `execFile`）：

```typescript
// electron/lib/powershell.ts（节选）
// 回退路径：单次 execFile 拉起 powershell.exe
async function execOnce(command: string, withJson: boolean, timeout: number): Promise<string> {
  const fullCmd = withJson
    ? `${UTF8_PREFIX}${command} | ConvertTo-Json -Depth 5 -Compress`
    : `${UTF8_PREFIX}${command}`
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encodeCommand(fullCmd)],
    { timeout, maxBuffer: 10 * 1024 * 1024, windowsHide: true }
  )
  return stdout
}

export async function runPowerShell<T>(command: string, opts: PsOptions = {}): Promise<T> {
  const timeout = opts.timeout ?? DEFAULT_TIMEOUT
  const retries = opts.retries ?? DEFAULT_RETRIES
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      const stdout = isPoolEnabled()
        ? await getPool().execute(command, 'JSON', { timeout })   // 常驻进程池
        : await execOnce(command, true, timeout)                  // 回退
      return parseJson<T>(stdout)
    } catch (err) {
      if (!isRetryable((err as Error).message) || attempt === retries) throw formatPsError(err as Error)
      await sleep(300 * (attempt + 1))
    }
  }
  throw formatPsError(lastError!)
}
```

运行时类型校验（防注入）：

```typescript
psBool(v)        // boolean → 'true'/'false'，非布尔抛错
psNumber(v)      // number → 字符串，非有限数抛错
psEnum(v, allowed) // 枚举白名单校验
psQuote(s)       // 字符串单引号包裹 + 内部单引号转义
```

### 11.3 单元测试

```bash
pnpm test   # vitest run --passWithNoTests
```

**191 个单元测试**（10 个文件），覆盖：

| 测试文件 | 覆盖范围 |
|----------|----------|
| `lib/powershell.test.ts` | `psBool`/`psNumber`/`psEnum`/`validateName`/`parseJson` 注入防护与解析 |
| `lib/powershellPool.test.ts` | 进程池单命令/排队/并发/标记解析/ERR 路径/超时 kill/崩溃恢复/shutdown |
| `lib/powershellPool.stress.test.ts` | 100 并发（全成功/含超时/全超时）排队与恢复压测 |
| `services/protocol/detect.test.ts` | 协议能力探测、只读 `retries:0`、失败容错 |
| `services/protocol/registry.test.ts` | 协议路由、不支持能力抛 unsupported |
| `services/protocol/adapters/*.test.ts` | 四适配器 createShare（注入防护/孤儿清理）、setPermissions（事务回滚/空数组边界）、closeSession（sessionId 解析） |
| `services/system.test.ts` | 仪表盘统计、健康检查 |

### 11.4 打包配置（electron-builder.yml 节选）

```yaml
appId: com.winshare.panel
productName: WinShare Panel
directories:
  buildResources: resources
asar: true
electronLanguages: [zh-CN, en-US]        # 语言包收敛：每架构省 37.4 MB（实测）
win:
  target:                                # 三架构；实际由 scripts/build-win.mjs 逐架构单独打包
    - { target: nsis,     arch: [x64, ia32, arm64] }
    - { target: portable, arch: [x64, ia32, arm64] }
    - { target: msi,      arch: [x64, ia32] }   # arm64 无 MSI（见 11.6）
  icon: resources/icon.ico                       # 应用图标
  requestedExecutionLevel: requireAdministrator   # 强制管理员权限
nsis:
  oneClick: false
  allowToChangeInstallationDirectory: true
  createDesktopShortcut: true
  installerIcon: resources/icon.ico
  uninstallerIcon: resources/icon.ico
  artifactName: WinShare.Panel-${version}-Setup-${env.WINSHARE_ARCH_LABEL}.${ext}
```

### 11.5 三架构打包与架构对齐（v1.3.0）

#### 为什么逐架构单独打包（`scripts/build-win.mjs`）

同一次 electron-builder 调用里给多个 `arch`，NSIS/portable 会产出**内嵌多架构的"胖"安装包**
（v1.2.0 的 `release/` 里实测同时存在 196 MB 的 `Setup.exe` / `-portable.exe` 与 101 MB / 96 MB
的单架构包）。逐架构单独调用的收益：① 用户只下载自己那套 Electron；② 架构标识可按架构分别命名
（Electron 内部 id 是 `ia32`，对外必须写 `x86`）；③ arm64 的 MSI 不会被静默降级。

打包脚本的三道保障：

1. **命名即承诺**：`WINSHARE_ARCH_LABEL`（x86/x64/arm64）注入 `artifactName`；
   直接裸跑 electron-builder 会因该变量未定义而明确报错，不会产出错命名。
2. **PE 头硬校验**：每个架构打包后读解包主程序的 PE `Machine`
   （`0x014c`=x86 / `0x8664`=x64 / `0xaa64`=arm64），不一致直接构建失败。
3. **校验和**：`release/SHA256SUMS.txt` 覆盖本版本全部安装包本体，供下载方核对。

#### 32 位包为什么必须取原生 64 位 PowerShell（`electron/lib/nativePaths.ts`）

32 位进程在 64 位 Windows 上，`C:\Windows\System32` 会被 WOW64 重定向到 `SysWOW64`，
裸名 `powershell.exe` 拉起的是 **32 位 PowerShell**。本机实测同脚本在两种位宽下的差异：

| 探针 | 32 位 PowerShell | 64 位 PowerShell | 受影响的界面 |
|---|---|---|---|
| `Get-LocalUser` | **命令不存在**（缺 `Microsoft.PowerShell.LocalAccounts`） | 正常 | 用户权限页、账号安全体检 |
| IIS `Get-Website`（WebAdministration） | **COM 类未注册 0x80040154** | 正常 | FTP / WebDAV 面板、IIS 能力探测 |
| `HKLM:\...\CurrentVersion` 的 `ProgramFilesDir` | `C:\Program Files (x86)`（WOW6432Node） | `C:\Program Files` | 依赖注册表视图的判断 |
| `Get-SmbShare` / `Get-SmbServerConfiguration` | 正常 | 正常 | （无差异） |

修复：`Sysnative` 是只有 32 位进程可见的虚拟别名，指向真正的原生 System32
（本机实测：64 位进程 `Test-Path` = False；32 位进程 = True）。因此按"存在即优先"探测
`Sysnative → System32`，三种情形各自得到与操作系统同位宽的 PowerShell：32 位进程在 64 位系统上
命中 Sysnative、64 位进程落回 System32、纯 32 位系统落回 System32（唯一可用版本）。

主进程启动时把「进程位宽 → 实际使用的 PowerShell」写进应用日志，用户无需调试器即可核对；
`electron/lib/nativePaths.test.ts` 用注入的 `exists` 固化三条分支。

> `inetsrv\appcmd.exe` 无需同样处理：实测 System32 与 SysWOW64 **都存在**该文件，
> 且拉起原生 PowerShell 后 PS 内部的 `system32` 本身就是原生视图。

### 11.6 体积优化（实测取舍）

| 措施 | 实测效果 | 结论 |
|---|---|---|
| 依赖全部放 `devDependencies` | `app.asar` **147.9 → 6.51 MiB**，解包总量 380 → 198.6 MiB | **采纳**（见下方"为什么"） |
| `electronLanguages: [zh-CN, en-US]` | 语言包 55 个 / 38.3 MB → 2 个 / 0.89 MB | **采纳** |
| `compression: maximum` | 73,589,372 B → 73,578,834 B（**省 10 KB / 0.014%**），单次打包多花约 45 s | **否决**（净收益为零） |
| 排除 `out/**/*.log` | 去掉误入包的 7 个诊断日志（`diagnose-test*.log`） | **采纳** |
| 逐架构单独打包 | 不再产出 187.7 MiB 的胖安装包；每个用户只下自己那套 | **采纳** |
| arm64 不提供 MSI | 避免"x64 MSI 装 arm64"的错标识包 | **采纳**（用 NSIS/portable） |

最终产物（v1.3.0 实测）：

| 产物 | v1.2.0 | v1.3.0 |
|---|---|---|
| `Setup-x64.exe` | 96.5 MiB | **69.2 MiB** |
| `portable-x64.exe` | 96.3 MiB | **69.0 MiB** |
| `x64.msi` | 106 MiB | **79.0 MiB** |
| `Setup-x86.exe` | 91.7 MiB | **64.3 MiB** |
| `Setup-arm64.exe` | — | **73.0 MiB**（新增） |
| `Setup.exe` / `portable.exe`（胖） | 187.7 / 187.5 MiB | 不再产出 |

#### 为什么依赖必须放 `devDependencies`

`electron.vite.config.ts` 用 `externalizeDepsPlugin()`（main / preload）。electron-builder 的依赖收集器
不看"实际 require 了什么"，而是把 `package.json` 的 **`dependencies` 整棵树**复制进 `app.asar`：
本项目这棵树有 147.9 MiB（antd/echarts/react 等），而应用自身只有 6.5 MiB。
实测打包产物里 `out/main/index.js` 与 `out/preload/index.js` 只 require
`electron` / `child_process` / `crypto` / `fs` / `os` / `path` / `util` ——
第三方全部被 Vite 打包进 `out/`，运行期不需要任何 `node_modules`。

因此规则是：**凡是会被 Vite 打进 `out/` 的依赖，一律放 `devDependencies`**。
把 `react`/`antd` 一类搬回 `dependencies` 会让每个安装包悄悄胖 20 MiB、安装后多占 180+ MiB，
`electron-builder.yml` 与本节都写明了这条约束以免被"顺手修好"。

### 11.7 CI/CD 发布

推送 `v*` 标签触发 [`.github/workflows/release.yml`](../.github/workflows/release.yml)：
`windows-latest` 上 `pnpm install` → `pnpm typecheck` → `pnpm build:win`（三架构）→
上传 `release/*.exe` + `release/*.msi` + `release/SHA256SUMS.txt` 到 GitHub Release。

> `latest.yml` 不再上传：本应用**没有集成 electron-updater**（应用内「检查更新」直接查
> GitHub Releases API，只提示、不自动下载安装），`latest.yml` 只对 electron-updater 有意义。

#### 两个踩过的坑（v1.3.0 首次发布实测，务必保留现有写法）

1. **`EP_PUBLISH: never` 拦不住 electron-builder 的自动发布**。v1.2.0 的 Release 运行因此报
   `⨯ GitHub Personal Access Token is not set` 失败 —— 这也解释了为什么 v1.1.0/v1.2.0 有 tag 却没有 Release。
   现在 `scripts/build-win.mjs` 在 CLI 上显式传 `--publish never`（`EP_PUBLISH` 保留作双保险）。
2. **镜像必须用 `npm_config_*` 形式设置**。仓库 `.npmrc` 的 `electron_mirror`
   （`https://npmmirror.com/mirrors/electron/`）会被 pnpm 导出为 `npm_config_electron_mirror`，
   而 `@electron/get` 的 `mirrorVar()` 取值优先级是：

   ```
   NPM_CONFIG_ELECTRON_MIRROR > npm_config_electron_mirror >
   npm_package_config_electron_mirror > ELECTRON_MIRROR > options > 默认值
   ```

   → workflow 里**只设 `ELECTRON_MIRROR` 挡不住 `.npmrc`**。v1.3.0 首次 Release 运行便从 npmmirror
   拉 arm64 Electron（境外慢），重试后解包目录里没有 `electron.exe`，arm64 打包失败
   （x86/x64 已成功，说明问题只出在那份下载）。现在 workflow 同时设
   `npm_config_electron_mirror` 与 `ELECTRON_MIRROR`（以及 `*_builder_binaries_*`）指向官方源。
   runner 在境外，直连官方源更快更稳；本地开发仍走 `.npmrc` 的镜像。

此外 `scripts/build-win.mjs` 对每个架构的打包调用带**一次重试**（针对下载类瞬时故障）；
两次都失败仍照原样中止并抛出原始错误，不掩盖真实失败。

---

**文档结束。**
