<div align="center">

# WinShare Panel

**基于 Windows 文件共享（SMB）的现代化可视化控制面板**
**A modern visual control panel for Windows file sharing (SMB)**

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows-10%2F11-lightgrey)
![Electron](https://img.shields.io/badge/Electron-31-47848F)
![React](https://img.shields.io/badge/React-19-61DAFB)

</div>

---

## 📖 项目介绍 / Introduction

### 中文

**WinShare Panel** 是一个原生 Windows 桌面应用，通过可视化控制面板管理本机的文件共享（SMB）功能，替代系统自带的 `fsmgmt.msc` 和零散的 `net share` 命令，提供现代化、集成化、可视化的管理体验。

**核心价值**：
- **可视化**：图形化管理共享、权限、会话，告别命令行
- **集成化**：共享管理 + 权限管理 + 会话监控 + SMB 配置，一站式
- **实时性**：会话与连接实时监控，异常即时告警
- **安全性**：Windows 集成认证，操作全程审计

**目标用户**：系统管理员、小型企业/团队文件服务器管理者、需要频繁管理 Windows 共享的高级用户。

### English

**WinShare Panel** is a native Windows desktop app that manages local file sharing (SMB) through a visual control panel, replacing the legacy `fsmgmt.msc` and scattered `net share` commands with a modern, integrated, visual experience.

**Core Values**: Visual · Integrated · Real-time · Secure (Windows-integrated auth with full audit trail).

**Target Users**: System administrators, small-team file-server maintainers, power users managing Windows shares.

---

## ✨ 功能特性 / Features

| 模块 / Module | 说明 / Description |
|---|---|
| 📊 仪表板 / Dashboard | 共享总数、活动会话、打开文件、访问趋势图、Top 5 热门共享、系统状态、快捷操作 |
| 📁 共享管理 / Shares | 共享 CRUD、新建向导、拖拽创建、批量操作、权限预设模板、配置导入导出（JSON） |
| 👥 用户权限 / Users | 本地用户/组、共享权限（Full/Change/Read/Deny）、NTFS 权限查看、权限矩阵视图 |
| 🔄 会话监控 / Sessions | 实时会话、打开文件、强制断开、可配置刷新间隔（1s/5s/10s/30s） |
| ⚙️ SMB 配置 / Settings | SMB1/2/3 协议、来宾访问、加密、审核日志、LanmanServer 服务控制 |

**工程特性 / Engineering Highlights**：
- PowerShell 执行器：超时 15s + 自动重试 2 次 + `ConvertTo-Json` 结构化解析
- 审计日志：轮转上限 5MB×10
- 崩溃捕获：electron crashReporter + 本地错误日志轮转
- 窗口风格化：无边框 + 自定义标题栏 + Win11 Acrylic/Mica 磨砂材质
- 权限预设模板：只读团队 / 读写协作 / 管理员全权 三套内置模板
- UAC 提权：`requestedExecutionLevel: requireAdministrator`

> 📸 截图占位 / Screenshots placeholder（待补充 / TBD）

---

## 🛠 技术栈 / Tech Stack

| 层级 / Layer | 技术 / Tech | 版本 / Version | 选型理由 / Rationale |
|---|---|---|---|
| 桌面框架 | Electron | ^31 | Node.js 生态原生集成，打包成熟 |
| 构建工具 | electron-vite | ^5 | Electron 专用 Vite 集成，HMR 快 |
| 前端框架 | React | ^19 | 生态丰富，组件化，类型友好 |
| 语言 | TypeScript | ^5 | 全栈类型安全 |
| UI 组件库 | Ant Design | ^5 | 企业级组件齐全（按需加载 + Tree Shaking） |
| 样式 | TailwindCSS | ^3 | 原子化 CSS，快速布局 |
| 状态管理 | Zustand | ^4 | 轻量，无样板代码 |
| 路由 | React Router | ^6 | SPA 路由标准方案 |
| 图表 | ECharts | ^5 | 仪表板可视化（按需引入 + Tree Shaking） |
| 打包 | electron-builder | ^24 | Windows NSIS 安装包 + 便携版 |

---

## 🚀 快速开始 / Quick Start

### 环境要求 / Prerequisites

- **Node.js** `^20.19.0` 或 `>=22.12.0`（electron-vite 引擎要求）
- **pnpm** `>=10`（推荐 / recommended）
- **Windows 10 / 11**（依赖 SMB PowerShell cmdlet）
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
```

> 若 Electron 二进制下载失败 / If Electron binary download fails：
> 设置环境变量 `ELECTRON_SKIP_BINARY_DOWNLOAD=1` 可跳过（仅构建不需要，运行 `pnpm dev` 必须下载）。

---

## 📂 目录结构 / Project Structure

```
win-share-panel/
├── docs/                       # 文档 / Documentation
│   ├── DEVELOPMENT.md          #   开发文档（10 章）/ Development doc
│   └── UI_DESIGN.md            #   UI 设计规范 / UI design spec
├── electron/                   # 主进程 / Main process
│   ├── main.ts                 #   入口（窗口/UAC/IPC/托盘）
│   ├── preload.ts              #   contextBridge 安全 API
│   ├── ipc/                    #   IPC 路由
│   ├── services/               #   业务服务（share/user/session/smb/preset/system）
│   ├── lib/                    #   powershell 执行器 / audit / errors
│   └── types.ts
├── src/                        # 渲染进程 / Renderer (React)
│   ├── pages/                  #   Dashboard/Shares/Users/Sessions/Settings
│   ├── components/             #   Layout/TitleBar/CommandPalette
│   ├── api.ts                  #   preload API 封装
│   └── types.ts
├── resources/                  # 静态资源 / Assets
│   ├── logo.jpg / logo.png     #   UI/托盘图标
│   └── icon.ico                #   Windows 打包图标
├── electron-builder.yml        # 打包配置 / Packaging
├── electron.vite.config.ts     # electron-vite 配置
└── package.json
```

### 📚 完整文档 / Full Documentation

- **[docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md)** — 项目概述、技术架构、功能模块、系统设计、IPC 通道、数据模型、6 阶段开发计划
- **[docs/UI_DESIGN.md](./docs/UI_DESIGN.md)** — 浅蓝调磨砂玻璃风格、色彩/造型/光影、组件规范、Electron 窗口风格化

---

## 📦 打包 / Packaging

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

### 自动更新 / Auto Update

按 [DEVELOPMENT.md](./docs/DEVELOPMENT.md) 规划，将集成 `electron-updater` 实现自动更新（v1.1 规划中）。

---

## ⚠️ 注意事项 / Notes

### 中文

1. **UAC 提权**：应用启动需管理员权限以调用 SMB PowerShell cmdlet（`New-SmbShare` / `Close-SmbSession` 等），非管理员会触发 UAC 提示
2. **网络代理**：国内推送 GitHub 若被重置，可临时注入代理：
   ```bash
   git -c http.proxy=http://127.0.0.1:7890 -c https.proxy=http://127.0.0.1:7890 push
   ```
3. **依赖镜像**：`.npmrc` 已配置 `npmmirror.com` 加速 Electron / electron-builder 二进制下载
4. **PowerShell 执行策略**：调用 `powershell.exe -NoProfile -Command`，不依赖系统 Profile
5. **危险操作**：所有写操作（创建/删除/断开/重启服务）均二次确认

### English

1. **UAC**: Admin required for SMB cmdlets; non-admin triggers UAC prompt
2. **Proxy**: If GitHub push reset, inject proxy via `git -c http.proxy=... push`
3. **Mirror**: `.npmrc` uses npmmirror.com for Electron binary acceleration
4. **PowerShell**: Invoked with `-NoProfile`, independent of system profile
5. **Destructive ops**: All writes require secondary confirmation

---

## 📄 License

[MIT](./LICENSE) © WinShare Panel

---

<div align="center">

**📚 详细文档见 [`docs/`](./docs) · 详见 / See [`DEVELOPMENT.md`](./docs/DEVELOPMENT.md)**

</div>
