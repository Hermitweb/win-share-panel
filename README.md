<div align="center">

# WinShare Panel

**Windows 多协议文件共享可视化控制面板（SMB / NFS / FTP / WebDAV）**
**A modern visual control panel for Windows multi-protocol file sharing**

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows-10%2F11-lightgrey)
![Electron](https://img.shields.io/badge/Electron-31-47848F)
![React](https://img.shields.io/badge/React-19-61DAFB)
![Tests](https://img.shields.io/badge/tests-191%20passed-brightgreen)

</div>

---

## 📖 项目介绍 / Introduction

### 中文

**WinShare Panel** 是一个原生 Windows 桌面应用，通过可视化控制面板统一管理本机的**四种**文件共享协议——SMB、NFS、FTP、WebDAV，替代系统自带的 `fsmgmt.msc`、`fsmgmt` 零散的 `net share` / `New-SmbShare` / IIS 管理器等多套工具，提供现代化、集成化、可视化的管理体验。

**核心价值**：
- **多协议统一**：一套界面管 SMB / NFS / FTP / WebDAV，协议差异由适配器层屏蔽
- **可视化**：图形化管理共享、权限、会话，告别命令行
- **集成化**：共享管理 + 权限管理 + 会话监控 + 协议配置 + 用户/组管理，一站式
- **高性能**：常驻 PowerShell 进程池 + 并行执行，首屏与刷新从秒级降至百毫秒级
- **安全加固**：命令注入运行时校验、IPC 边界校验、contextIsolation 隔离、事务回滚
- **实时性**：会话与连接实时监控，异常即时告警

**目标用户**：系统管理员、小型企业/团队文件服务器管理者、需要频繁管理 Windows 共享的高级用户。

### English

**WinShare Panel** is a native Windows desktop app that manages local file sharing across **four** protocols—SMB, NFS, FTP, and WebDAV—through a unified visual control panel, replacing the legacy `fsmgmt.msc`, scattered `net share` / `New-SmbShare` / IIS Manager tools with a modern, integrated, visual experience.

**Core Values**: Multi-protocol unified · Visual · Integrated · High-performance (persistent PowerShell pool + parallel) · Security-hardened (injection validation, IPC guards, contextIsolation, transaction rollback) · Real-time.

**Target Users**: System administrators, small-team file-server maintainers, power users managing Windows shares.

---

## ✨ 功能特性 / Features

| 模块 / Module | 说明 / Description |
|---|---|
| 📊 仪表板 / Dashboard | 共享总数、活动会话、打开文件、协议分布、Top 5 热门共享、系统状态、PNG/CSV 导出 |
| 📁 共享管理 / Shares | 多协议共享 CRUD、新建向导（高度可自定义）、拖拽创建、批量操作、权限预设模板、配置导入导出（JSON） |
| 👥 用户权限 / Users | 本地用户/组 CRUD、共享权限（Full/Change/Read/Deny）、NTFS 权限查看、权限矩阵视图 |
| 🔄 会话监控 / Sessions | 多协议实时会话、打开文件、强制断开、可配置刷新间隔（1s/5s/10s/30s） |
| ⚙️ 协议配置 / Settings | SMB / NFS / FTP / WebDAV 服务器配置、服务启停、配置快照与回滚、协议能力探测与引导安装 |

**工程特性 / Engineering Highlights**：
- **多协议适配器架构**：`ProtocolAdapter` 统一接口，SMB / NFS / FTP / WebDAV 各自实现，`registry` 按协议路由
- **常驻 PowerShell 进程池**：N 个长期存活 worker 复用，命令执行从 ~300ms/条降至 ~30–80ms/条
- **并行 + 去重优化**：`Promise.allSettled` 并行协议探测/共享列表/仪表盘，in-flight promise 缓存去重并发请求
- **命令注入防护**：`psBool` / `psNumber` / `psEnum` 运行时类型校验 + `-EncodedCommand` 编码，杜绝拼接注入
- **IPC 边界校验**：协议名 / 共享名 / 路径在 IPC 入口运行时校验
- **事务补偿**：`setPermissions` 备份→应用→失败回滚，`createShare` 失败清理孤儿共享/站点
- **Electron 安全**：`contextIsolation: true` + `nodeIntegration: false` + `sandbox` + 单实例锁
- **审计日志**：轮转上限 5MB×10
- **崩溃捕获**：electron crashReporter + 本地错误日志轮转
- **窗口风格化**：无边框 + 自定义标题栏 + Win11 Acrylic/Mica 磨砂材质
- **全面测试**：191 个单元测试（10 个文件），覆盖注入防护、事务回滚、进程池排队/超时/崩溃、100 并发压测

> 📸 截图占位 / Screenshots placeholder（待补充 / TBD）

---

## 🛠 技术栈 / Tech Stack

| 层级 / Layer | 技术 / Tech | 版本 / Version | 选型理由 / Rationale |
|---|---|---|---|
| 桌面框架 | Electron | ^31 | Node.js 生态原生集成，打包成熟 |
| 构建工具 | electron-vite | ^5 | Electron 专用 Vite 集成，HMR 快 |
| 前端框架 | React | ^19 | 生态丰富，组件化，类型友好 |
| 语言 | TypeScript | ^7 | 全栈类型安全 |
| UI 组件库 | Ant Design | ^6 | 企业级组件齐全（按需加载 + Tree Shaking） |
| 样式 | TailwindCSS | ^3 | 原子化 CSS，快速布局 |
| 状态管理 | Zustand | ^5 | 轻量，无样板代码 |
| 路由 | React Router | ^7 | SPA 路由标准方案 |
| 图表 | ECharts | ^6 | 仪表板可视化（按需引入 + Tree Shaking） |
| 测试 | Vitest | ^4 | 单元测试（注入防护 / 事务回滚 / 进程池） |
| 打包 | electron-builder | ^25 | Windows NSIS 安装包 + 便携版 |

---

## 🚀 快速开始 / Quick Start

### 环境要求 / Prerequisites

- **Node.js** `^20.19.0` 或 `>=22.12.0`（electron-vite 引擎要求；pnpm 11.20+ 需 Node 22）
- **pnpm** `>=11`（推荐 / recommended）
- **Windows 10 / 11**（依赖 SMB/NFS PowerShell cmdlet 与 IIS FTP/WebDAV）
- **管理员权限**（运行时需 UAC 提权 / runtime UAC elevation required）

### 安装与运行 / Install & Run

```bash
# 1. 安装依赖 / Install dependencies
#    首次安装会下载 Electron 二进制，已通过 .npmrc 配置 npmmirror 镜像加速
pnpm install

# 2. 开发模式（HMR）/ Development mode
pnpm dev

# 3. 构建产物（main + preload + renderer）/ Build output
pnpm build

# 4. 类型检查 / Type check
pnpm typecheck

# 5. 单元测试 / Unit tests
pnpm test
```

> 若 Electron 二进制下载失败 / If Electron binary download fails：
> 设置环境变量 `ELECTRON_SKIP_BINARY_DOWNLOAD=1` 可跳过（仅构建不需要，运行 `pnpm dev` 必须下载）。

---

## 📂 目录结构 / Project Structure

```
win-share-panel/
├── docs/                       # 文档 / Documentation
│   ├── DEVELOPMENT.md          #   开发文档（架构/IPC/数据模型/安全/性能）/ Development doc
│   └── UI_DESIGN.md            #   UI 设计规范 / UI design spec
├── .github/workflows/          # CI/CD
│   └── release.yml             #   tag 触发自动构建并发布 Release
├── electron/                   # 主进程 / Main process
│   ├── main.ts                 #   入口（窗口/UAC/单实例锁/IPC/托盘/进程池生命周期）
│   ├── preload.ts              #   contextBridge 安全 API（多协议路由）
│   ├── ipc/                    #   IPC 路由 + 边界校验
│   ├── services/               #   业务服务（share/user/group/session/smb/nfs/ftp/webdav/preset/system）
│   │   └── protocol/           #   多协议适配器层 / Protocol adapter layer
│   │       ├── ProtocolAdapter.ts   # 统一接口
│   │       ├── registry.ts          # 协议路由
│   │       ├── detect.ts            # 协议能力探测
│   │       └── adapters/            # smb/nfs/ftp/webdav 适配器
│   ├── lib/                    #   powershell 执行器 + 进程池 / audit / errors
│   │   ├── powershell.ts       #     runPowerShell（池化 + 回退 execFile）
│   │   └── powershellPool.ts   #     常驻进程池 / Persistent PowerShell pool
│   └── types.ts
├── src/                        # 渲染进程 / Renderer (React)
│   ├── pages/                  #   Dashboard/Shares/Users/Sessions/Settings
│   ├── components/             #   Layout/TitleBar/各协议设置面板/详情抽屉/创建弹窗
│   ├── stores/                 #   Zustand（uiStore 等）
│   ├── hooks/                  #   useTickEffect 等
│   ├── utils/                  #   工具函数
│   ├── api.ts                  #   preload API 封装
│   └── types.ts
├── resources/                  # 静态资源 / Assets（logo.png / icon.ico）
├── vitest.config.ts            # 单元测试配置
├── electron-builder.yml        # 打包配置 / Packaging
├── electron.vite.config.ts     # electron-vite 配置
└── package.json
```

### 📚 完整文档 / Full Documentation

- **[docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md)** — 项目概述、技术架构、多协议适配器、PowerShell 进程池、IPC 通道、数据模型、安全设计、性能优化、测试
- **[docs/UI_DESIGN.md](./docs/UI_DESIGN.md)** — 浅蓝调磨砂玻璃风格、色彩/造型/光影、组件规范、Electron 窗口风格化
- **[CHANGELOG.md](./CHANGELOG.md)** — 版本变更记录

---

## 📦 打包与发布 / Packaging & Release

### 安装包与便携版 / Installer & Portable

```bash
# 生成 Windows NSIS 安装包 + 便携版 exe
pnpm build:win
```

打包配置见 [electron-builder.yml](./electron-builder.yml)：

- **NSIS 安装包**：含 UAC 提权（`requireAdministrator`）、开始菜单/桌面快捷方式
- **便携版 / Portable**：免安装单 exe，支持 U 盘运行（满足 USB 携带场景）
- **图标**：`resources/icon.ico`（Windows）/ `resources/logo.png`（UI/托盘）
- **资源打包**：`resources/` 通过 `extraResources` 包含进最终产物

### 自动发布 / Automated Release

推送 `v*` 标签即触发 [`.github/workflows/release.yml`](./.github/workflows/release.yml)：

```bash
git tag v1.0.0 && git push origin v1.0.0   # 触发 CI 构建 + 自动发布 GitHub Release
```

CI 在 `windows-latest` 上执行：`pnpm install` → `pnpm typecheck` → `pnpm build:win` → 上传 `release/*.exe` + `latest.yml` 到 Release。

### 自动更新 / Auto Update

按 [DEVELOPMENT.md](./docs/DEVELOPMENT.md) 规划，将集成 `electron-updater` 实现自动更新（v1.1 规划中）。

---

## ⚠️ 注意事项 / Notes

### 中文

1. **UAC 提权**：应用启动需管理员权限以调用 SMB/NFS PowerShell cmdlet 与 IIS FTP/WebDAV 配置，非管理员会触发 UAC 提示
2. **网络代理**：国内推送 GitHub 若被重置，可临时注入代理：
   ```bash
   git -c http.proxy=http://127.0.0.1:7890 -c https.proxy=http://127.0.0.1:7890 push
   ```
3. **依赖镜像**：`.npmrc` 已配置 `npmmirror.com` 加速 Electron / electron-builder 二进制下载
4. **PowerShell 执行策略**：通过 `-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand` 调用，不依赖系统 Profile；常驻进程池经 stdin/stdout 标记协议复用 worker
5. **危险操作**：所有写操作（创建/删除/断开/重启服务/修改配置）均二次确认；系统特殊共享（ADMIN$/IPC$/C$）与内置用户/组受保护不可删

### English

1. **UAC**: Admin required for SMB/NFS cmdlets and IIS FTP/WebDAV config; non-admin triggers UAC prompt
2. **Proxy**: If GitHub push reset, inject proxy via `git -c http.proxy=... push`
3. **Mirror**: `.npmrc` uses npmmirror.com for Electron binary acceleration
4. **PowerShell**: Invoked with `-NoProfile -EncodedCommand`, independent of system profile; a persistent pool reuses workers via a stdin/stdout marker protocol
5. **Destructive ops**: All writes require secondary confirmation; system shares (ADMIN$/IPC$/C$) and built-in users/groups are protected

---

## 📄 License

[MIT](./LICENSE) © WinShare Panel

---

<div align="center">

**📚 详细文档见 [`docs/`](./docs) · 详见 / See [`DEVELOPMENT.md`](./docs/DEVELOPMENT.md) · [`CHANGELOG.md`](./CHANGELOG.md)**

</div>
