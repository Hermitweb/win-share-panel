# WinShare Panel 开发文档

> 基于 Windows 文件共享功能（SMB）的全新控制面板
> 版本：v1.0  |  更新日期：2026-08-06

---

## 一、项目概述

### 1.1 项目目标
打造一个原生 Windows 桌面应用，通过可视化控制面板管理本机的文件共享（SMB）功能，替代系统自带的 `fsmgmt.msc` 和零散的 `net share` 命令，提供现代化、集成化、可视化的管理体验。

### 1.2 核心价值
- **可视化**：图形化管理共享、权限、会话，告别命令行
- **集成化**：共享管理 + 权限管理 + 会话监控 + SMB 配置，一站式
- **实时性**：会话与连接实时监控，异常即时告警
- **安全性**：Windows 集成认证，操作全程审计

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
│  │  React 18 + TS + Vite │◄─►│  Node.js + Express    │  │
│  │  Ant Design + Tailwind│IPC│  ├─ 共享服务            │  │
│  │                       │   │  ├─ 用户/权限服务       │  │
│  │  Pages:               │   │  ├─ 会话监控服务        │  │
│  │  · 仪表板              │   │  ├─ SMB 配置服务        │  │
│  │  · 共享管理            │   │  └─ PowerShell 执行器   │  │
│  │  · 用户权限            │   │                       │  │
│  │  · 会话监控            │   │  Windows 集成认证       │  │
│  │  · 系统设置            │   │  UAC 管理员提权        │  │
│  └───────────────────────┘   └──────────┬────────────┘  │
└──────────────────────────────────────────┼──────────────┘
                                           │ child_process
                           ┌───────────────▼───────────────┐
                           │      Windows 系统层            │
                           │  · PowerShell (Get-Smb*)       │
                           │  · net.exe (share/session)     │
                           │  · WMI / CIM                   │
                           │  · icacls (NTFS 权限)          │
                           │  · Windows API (原生模块)      │
                           └───────────────────────────────┘
```

### 2.2 技术栈选型

| 层级 | 技术 | 版本 | 选型理由 |
|------|------|------|----------|
| 桌面框架 | Electron | ^28.x | Node.js 生态原生集成，跨平台，打包成熟 |
| 构建工具 | electron-vite | ^2.x | Electron 专用 Vite 集成，HMR 快 |
| 前端框架 | React | ^18.x | 生态丰富，组件化，类型友好 |
| 语言 | TypeScript | ^5.x | 全栈类型安全 |
| UI 组件库 | Ant Design | ^5.x | 企业级管理后台首选，组件齐全（按需加载 + Tree Shaking） |
| 样式方案 | TailwindCSS | ^3.x | 原子化 CSS，快速布局 |
| 状态管理 | Zustand | ^4.x | 轻量，无样板代码 |
| 路由 | React Router | ^6.x | SPA 路由标准方案 |
| 图表 | ECharts | ^5.x | 仪表板数据可视化（按需引入 + Tree Shaking） |
| 进程调用 | execa | ^8.x | 比 child_process 更友好的 API |
| 打包工具 | electron-builder | ^24.x | Windows 安装包（NSIS）|
| 测试 | Vitest + Playwright | latest | 单元 + E2E 测试 |

---

## 三、功能模块设计

### 3.1 模块总览

```
WinShare Panel
├── 1. 仪表板 (Dashboard)           ← 总览、统计、快捷操作
├── 2. 共享文件夹管理 (Shares)       ← 核心：增删改查共享
├── 3. 用户与权限管理 (Users)        ← 用户/组、共享权限、NTFS 权限
├── 4. 会话与连接监控 (Sessions)     ← 实时会话、打开文件、强制断开
└── 5. SMB 服务配置 (Settings)      ← SMB 服务器参数、日志、审计
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

#### 模块 5：SMB 服务配置
- **服务器配置**
  - SMB 协议版本启用（SMB1/SMB2/SMB3）
  - 未身份验证的来宾访问
  - 加密设置
  - 审核日志级别
  - 保持连接超时、空闲超时等参数
- **日志查看**：SMB 相关 Windows 事件日志
- **审计设置**：记录所有面板操作到本地日志文件
- **服务控制**：重启 Server 服务（lanmanserver）
- **危险操作二次确认**

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

所有 IPC 通道命名遵循 `domain:action` 规范，统一通过 `preload.ts` 暴露：

```typescript
// preload.ts 暴露的 API（contextBridge）
interface WinShareAPI {
  share: {
    list: () => Promise<Share[]>
    create: (opts: CreateShareOpts) => Promise<Share>
    update: (name: string, opts: UpdateShareOpts) => Promise<Share>
    delete: (name: string) => Promise<void>
    toggle: (name: string, enabled: boolean) => Promise<void>
  }
  user: {
    listUsers: () => Promise<LocalUser[]>
    listGroups: () => Promise<LocalGroup[]>
    getSharePermissions: (shareName: string) => Promise<SharePermission[]>
    setSharePermissions: (shareName: string, perms: SharePermission[]) => Promise<void>
    getNtfsPermissions: (path: string) => Promise<NtfsAcl>
  }
  session: {
    listSessions: () => Promise<SmbSession[]>
    listOpenFiles: () => Promise<SmbOpenFile[]>
    closeSession: (clientId: string) => Promise<void>
    closeFile: (fileId: string) => Promise<void>
  }
  smb: {
    getConfig: () => Promise<SmbServerConfig>
    setConfig: (config: Partial<SmbServerConfig>) => Promise<void>
    getServiceStatus: () => Promise<ServiceStatus>
    restartService: () => Promise<void>
  }
  system: {
    getCurrentUser: () => Promise<UserInfo>
    isAdmin: () => Promise<boolean>
    relaunchAsAdmin: () => Promise<void>
    getDashboardStats: () => Promise<DashboardStats>
  }
  window: {
    minimize: () => Promise<void>
    toggleMaximize: () => Promise<boolean>   // 返回当前是否最大化
    close: () => Promise<void>
    isMaximized: () => Promise<boolean>
  }
  preset: {
    list: () => Promise<PermissionPreset[]>
    save: (preset: PermissionPreset) => Promise<void>           // 新增/更新
    delete: (id: string) => Promise<void>                       // 仅自定义可删
    apply: (shareName: string, presetId: string, mode: 'overwrite' | 'merge') => Promise<void>
  }
}
```

### 4.3 Windows 系统调用层

通过 `PowerShell 执行器` 封装所有 Windows 操作，统一处理：
- 命令执行（`execa` 调用 `powershell.exe -NoProfile -Command`）
- 参数转义（防注入）
- 错误处理与解析
- 管理员权限检测

#### Windows 命令映射表

| 功能 | PowerShell 命令 | 备注 |
|------|----------------|------|
| 列出共享 | `Get-SmbShare` | 含特殊共享 |
| 创建共享 | `New-SmbShare -Name X -Path Y -Description Z -FullAccess A` | 权限参数化 |
| 修改共享 | `Set-SmbShare -Name X -Description Z` | |
| 删除共享 | `Remove-SmbShare -Name X` | |
| 共享权限 | `Get-SmbShareAccess / Grant-SmbShareAccess / Revoke-SmbShareAccess` | |
| 活动会话 | `Get-SmbSession` | |
| 打开文件 | `Get-SmbOpenFile` | 需启用 openfiles |
| 断开会话 | `Close-SmbSession -ClientUserName X` | |
| 关闭文件 | `Close-SmbOpenFile -FileId X` | |
| 本地用户 | `Get-LocalUser` | |
| 本地组 | `Get-LocalGroup / Get-LocalGroupMember` | |
| NTFS 权限 | `Get-Acl` / `icacls` | icacls 输出更友好 |
| SMB 服务器配置 | `Get-SmbServerConfiguration / Set-SmbServerConfiguration` | |
| 服务状态 | `Get-Service LanmanServer` | |
| 重启服务 | `Restart-Service LanmanServer` | 危险操作 |

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

---

## 五、项目结构

```
win-share-panel/
├── docs/
│   └── DEVELOPMENT.md              # 本开发文档
├── electron/                       # 主进程
│   ├── main.ts                     # 主进程入口（窗口、UAC、IPC 注册）
│   ├── preload.ts                  # 预加载脚本（contextBridge）
│   ├── ipc/
│   │   ├── index.ts                # IPC 路由注册
│   │   ├── share.ts                # 共享相关 IPC handler
│   │   ├── user.ts                 # 用户权限相关
│   │   ├── session.ts              # 会话监控相关
│   │   └── smb.ts                  # SMB 配置相关
│   ├── services/                   # 业务服务层
│   │   ├── shareService.ts
│   │   ├── userService.ts
│   │   ├── sessionService.ts
│   │   ├── smbConfigService.ts
│   │   └── authService.ts
│   ├── lib/
│   │   ├── powershell.ts           # PowerShell 执行器（核心）
│   │   ├── parser.ts               # 输出解析器（JSON 转 TS 对象）
│   │   ├── audit.ts                # 审计日志
│   │   └── errors.ts               # 统一错误定义
│   └── types/                      # 主进程类型
├── src/                            # 渲染进程（React）
│   ├── main.tsx                    # React 入口
│   ├── App.tsx                     # 根组件 + 路由
│   ├── pages/
│   │   ├── Dashboard.tsx
│   │   ├── Shares/
│   │   │   ├── ShareList.tsx
│   │   │   ├── ShareForm.tsx       # 新建/编辑
│   │   │   └── SharePermission.tsx
│   │   ├── Users/
│   │   │   ├── UserList.tsx
│   │   │   └── PermissionMatrix.tsx
│   │   ├── Sessions/
│   │   │   ├── SessionList.tsx
│   │   │   └── OpenFileList.tsx
│   │   └── Settings/
│   │       └── SmbConfig.tsx
│   ├── components/                 # 通用组件
│   │   ├── Layout/
│   │   ├── StatCard.tsx
│   │   ├── ConfirmDialog.tsx
│   │   └── ...
│   ├── api/                        # 调用 preload API 的封装
│   │   └── index.ts
│   ├── stores/                     # Zustand 状态
│   │   ├── shareStore.ts
│   │   └── sessionStore.ts
│   └── styles/
├── resources/                      # 静态资源：logo.jpg(源)/logo.png(256,标题栏·托盘)/icon.ico(打包图标)
├── scripts/                        # 构建脚本
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
- 应用以管理员权限运行（UAC requireAdministrator）
- Windows 集成认证，无需独立账号系统
- 所有写操作二次确认

### 7.2 命令注入防护
- 所有 PowerShell 命令参数化传递，禁止字符串拼接
- 使用 `execa` 的数组参数形式，避免 shell 解析
- 用户输入经过白名单校验（共享名、路径、账号名）

### 7.3 审计日志
- 记录所有写操作：时间、操作人、操作类型、目标、结果
- 日志存储：`%APPDATA%/WinSharePanel/audit.log`
- 日志轮转：单文件 5MB，保留 10 份

### 7.4 危险操作清单（需二次确认）
- 删除共享
- 强制断开会话
- 关闭打开的文件
- 修改 SMB 服务器配置
- 重启 Server 服务

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
- **自动更新**：electron-updater，发版后自动升级
- **部署定位**：本应用为 Windows 本地桌面应用（管理本机 SMB 必须在 Windows 跑 PowerShell），Vercel/Railway/树莓派均不适用；低成本方案聚焦便携版 + 自动更新 + 免安装

### 10.5 预防性清单（防踩坑）
| 编号 | 建议 | 防的坑 |
|------|------|--------|
| P1 | PS 命令统一超时 + 重试封装 | 命令卡死致面板无响应 |
| P2 | 输出强制 ConvertTo-Json + 结构校验 | 输出漂移致解析崩溃 |
| P3 | crashReporter + 错误日志轮转 | 崩溃后无迹可寻 |
| P4 | 配置变更前自动快照 | 误改致共享全断无法回退 |
| P5 | 参数白名单 + execa 数组传参 | 命令注入 |
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
    "electron": "^28.0.0",
    "react": "^18.2.0",
    "react-dom": "^18.2.0",
    "react-router-dom": "^6.20.0",
    "antd": "^5.12.0",
    "zustand": "^4.4.0",
    "echarts": "^5.4.0",
    "echarts-for-react": "^3.0.2",
    "execa": "^8.0.0",
    "dayjs": "^1.11.0"
  },
  "devDependencies": {
    "electron-vite": "^2.0.0",
    "electron-builder": "^24.9.0",
    "typescript": "^5.3.0",
    "vite": "^5.0.0",
    "@vitejs/plugin-react": "^4.2.0",
    "tailwindcss": "^3.4.0",
    "vitest": "^1.0.0",
    "@playwright/test": "^1.40.0"
  }
}
```

### 11.2 PowerShell 输出 JSON 化示例

为便于解析，所有 PowerShell 命令统一输出 JSON：

```powershell
# 主进程调用示例
powershell.exe -NoProfile -Command "Get-SmbShare | ConvertTo-Json -Depth 3"
```

```typescript
// powershell.ts 执行器封装
import { execa } from 'execa'

export async function runPowerShell<T>(command: string): Promise<T> {
  const { stdout } = await execa('powershell.exe', [
    '-NoProfile',
    '-NonInteractive',
    '-Command',
    `${command} | ConvertTo-Json -Depth 5`
  ])
  const result = JSON.parse(stdout || 'null')
  // Get-* 命令返回单个对象时 JSON 是对象，多个时是数组，统一成数组
  return Array.isArray(result) ? result : (result ? [result] : [])
}
```

### 11.3 打包配置（electron-builder.yml 节选）

```yaml
appId: com.winshare.panel
productName: WinShare Panel
directories:
  buildResources: resources
win:
  target: nsis
  icon: resources/icon.ico                       # 应用图标（官方 logo，已定稿）
  requestedExecutionLevel: requireAdministrator   # 强制管理员权限
nsis:
  oneClick: false
  allowToChangeInstallationDirectory: true
  createDesktopShortcut: true
  installerIcon: resources/icon.ico
  uninstallerIcon: resources/icon.ico
```

---

**文档结束。确认无误后即可进入阶段 1 开发。**
