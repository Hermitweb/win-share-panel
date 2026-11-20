# Windows 平台安装与运行配置指南（WPF 版 2.2.0+）

> 适用：WinShare Panel WPF 版（`net/WinShare.App`）。Electron 版已冻结不再维护。

## 一、安装阶段

### 1.1 安装包与安装位置

安装包为 Inno Setup 产出：`WinShare.Panel-{版本}-Setup-{架构}.exe`（x64 / x86 / arm64 三架构独立包，**self-contained**——.NET 10 运行时已捆绑，**无需预装任何框架**）。

| 权限环境 | 默认安装位置 | 注册表写入域 |
|---|---|---|
| 管理员（默认，UAC 通过） | `C:\Program Files\WinShare Panel`（arm64 → Program Files (arm64)，x86 → Program Files (x86)） | 机器级（HKLM） |
| 普通用户（向导中选择「仅为我安装」或命令行 `/CURRENTUSER`） | `%LOCALAPPDATA%\Programs\WinShare Panel` | 用户级（HKCU） |

- 向导中**可自由更改安装目录**（含自定义盘符）。
- 同架构重装/升级共用同一卸载项（稳定 AppId）；跨架构覆盖安装由 Windows 语义自然处理。
- **静默安装**（批量部署）：
  ```
  WinShare.Panel-2.2.0-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR="D:\WinShare" /TASKS="desktopicon,autostart,addpath"
  ```
  `/TASKS` 可选 `desktopicon`（桌面快捷方式）、`autostart`（开机自启）、`addpath`（加入 PATH）；`*` 表示全部。

### 1.2 安装任务项

| 任务项 | 落点 | 说明 |
|---|---|---|
| 创建桌面快捷方式 | 桌面 | 默认勾选 |
| 开机自动启动 | `HKLM\...\CurrentVersion\Run\WinSharePanel = "安装目录\WinShare.App.exe" --silent` | 全部用户登录时启动；**卸载时自动删除**；与应用内「开机自启」开关（HKCU 同键名）不冲突——单实例守卫会去重 |
| 加入系统 PATH | 管理员→机器级 PATH；普通用户→用户级 PATH | 追加不覆盖；卸载时自动移除；命令行可直接启动 `WinShare.App.exe` |

**服务注册语义**：本应用是 GUI 控制面板，**不注册 Windows 服务**；SMB / NFS / FTP / WebDAV 共享服务由 Windows 自身承载，面板负责配置与启停。「服务注册」落地为上表的 Run 键（开机拉起面板便于管理，非后台服务）。

### 1.3 卸载

- 移除程序文件、快捷方式、自启 Run 值、PATH 中的安装目录。
- **保留**用户数据 `%APPDATA%\WinSharePanel\`（appstate.json / 审计日志 / 操作回收站 / 首启标记）——升级与重装不丢配置。彻底清除请手动删除该目录。

## 二、运行阶段

### 2.1 管理员权限（按需提权）

- 应用以 `asInvoker` 启动（不自动弹 UAC）。非管理员时**顶栏出现黄色提示条**：
  - 「以管理员身份重启」按钮（顶栏提示条与「应用设置 → 关于」两处入口）→ UAC 确认后旧实例退出、提权实例接管；
  - UAC 取消静默收敛，不打扰。
- 提权失败的常见原因与对策见下表。

### 2.2 端口 / 服务占用

- **创建共享前预检**：新建 FTP / WebDAV 站点时，向导对本机端口做 TcpListener 探测——被占用时**创建前**就地提示「端口 X 已被本机其它程序占用：请更换端口…」（不再等到 New-WebBinding 失败）。
- 运行中的服务启停 / 重启走设置页协议页签（带确认框）；错误经友好化映射（权限不足 / 服务未装 / IIS 锁定等均有下一步建议）。

### 2.3 任务栏与托盘驻留

| 设置（应用设置 → 偏好与运维 → 窗口行为） | 行为 |
|---|---|
| 在任务栏显示图标 = 开（默认） | 任务栏常驻图标 |
| 在任务栏显示图标 = 关 | 最小化到托盘后任务栏图标隐藏（纯托盘驻留） |
| 关闭窗口时最小化到系统托盘 = 开（默认） | 点 ✕ → 托盘驻留，共享服务保持；首次关闭有气泡说明 |
| 关闭窗口时最小化到系统托盘 = 关 | 点 ✕ → 直接退出 |

托盘右键菜单：显示主窗口 / 打开日志文件夹 / 退出。两项设置持久化到 `%APPDATA%\WinSharePanel\state\appstate.json`（`showInTaskbar` / `closeToTray`），重启保持。

### 2.4 异常防护与防闪退

| 防护层 | 行为 |
|---|---|
| UI 线程异常 | DispatcherUnhandledException → 记入应用日志 + 托盘气泡「程序发生未处理异常（已记录日志）」，**应用不退出** |
| 线程池/Task 异常 | UnobservedTaskException → 记录 + SetObserved，不崩 |
| 终极兜底 | AppDomain.UnhandledException → 落盘日志（闪退后可从 `%APPDATA%\WinSharePanel\logs\app.log` 定位） |
| 服务降级 | 任一契约服务装配失败 → 对应模块占位降级（页面可开、操作给「服务未就绪：请重启应用」人话提示），不崩 |
| 单实例 | 二实例激活消息后自退；提权重启链路有 6s 互斥量重试窗防竞态互踢 |

## 三、兼容矩阵

| 环境 | 兼容性 | 说明 |
|---|---|---|
| Windows 11（23H2+） | 全功能 | SMB QUIC 可用 |
| Windows 11（21H2/22H2） | 除 SMB QUIC 外全功能 | QUIC 探测为不支持，保存时自动跳过 |
| Windows 10 1809+（build 17763+） | 除 SMB QUIC 外全功能 | 安装器 MinVersion 强制（低于 1809 拒绝安装）；NFS 视 SKU |
| Windows 10 家庭版 | SMB 可用；NFS/FTP/WebDAV 引导受限 | 无 IIS（FTP/WebDAV 装不上，页面给引导）；无 NFS 服务端 Cmdlets |
| Windows Server 2019+/2022 | 全功能 + QUIC（2022+） | NFS 服务端 Cmdlets 内置 |
| Windows 10 1809 以下（8.1/7） | **不支持** | 安装器直接拦截 |
| x64 / x86 / arm64 | 三架构独立安装包 | x86 包经 Sysnative 用 64 位 PowerShell 避免能力缺口 |
| 杀毒软件（火绒/360 等） | 可能拦截未签名构建 | 症状：日志/状态零写入、PS 子进程被拦、共享操作报权限错误。将 exe 加入信任区或使用签名版即可 |
| 普通用户环境（per-user 安装） | 面板可用；共享管理类操作需提权 | 顶栏提示条 + 「以管理员身份重启」闭环 |

## 四、常见问题排障

| 症状 | 原因 | 处理 |
|---|---|---|
| 安装时报「另一程序正在使用此文件」 | 旧版本 WinShare 正在运行 | 退出应用（托盘右键 → 退出）后重试 |
| 开机不自启 | v2.2.0 前版本自启键 exe 名写错（已修复） | 升级到 2.2.0+，或在应用内重新开关一次「开机自启」 |
| 任务栏图标不见了 | 「窗口行为 → 在任务栏显示图标」被关闭 | 从托盘打开应用，到该设置改回 |
| 关闭窗口后找不到应用 | 关闭即托盘驻留（默认） | 托盘图标双击恢复；或改「关闭按钮行为」 |
| 创建 FTP/WebDAV 提示端口占用 | 端口被其它进程/站点监听 | 换端口，或停掉占用方（`netstat -ano \| findstr <端口>` 定位） |
| 操作报「需要管理员权限」 | 未提权运行 | 顶栏提示条 / 应用设置 →「以管理员身份重启」 |
| 安装到 Program Files 后设置丢失 | 用了不同的权限模式重装 | 管理员与 per-user 是两个独立安装；数据都在各自 `%APPDATA%` 不受影响 |
