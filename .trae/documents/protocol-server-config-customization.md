# NFS / FTP / WebDAV 高度可定制服务器配置

## Context

用户要求 NFS、FTP、WebDAV 的服务配置参照 SMB 那样可以高度自定义化配置。当前 SMB 有 26 个配置项 + 快照回滚；NFS 仅 6 项（4 开关 + 2 只读）；FTP 和 WebDAV **仅有服务控制**（重启/启动/停止），无任何服务器级配置。本方案将三个协议的配置能力扩展到与 SMB 同等的可定制深度。

## 设计概览

| 协议 | 当前后状 | 目标字段数 | 后端 PowerShell 来源 | 快照回滚 |
|------|---------|-----------|---------------------|---------|
| SMB | 26 项（已完成） | 26 | `Get/Set-SmbServerConfiguration` | 是 |
| NFS | 6 项 | 12 | `Get/Set-NfsServerConfiguration` | 否 |
| FTP | 0（仅服务控制） | 22 | IIS `ftpServer/*` 配置节 (`MACHINE/WEBROOT/APPHOST`) | 否 |
| WebDAV | 0（仅服务控制） | 14 | IIS `system.webServer/*` 配置节 | 否 |

> FTP/WebDAV 不做快照回滚：其配置由 10+ 独立 `Set-WebConfigurationProperty` 组成，部分节因 `overrideModeDefault=Deny` 锁定会失败，无法保证原子性回滚。

## 实现步骤

### Step 1: 类型定义 (`electron/types.ts`)

扩展 `NfsServerConfig`，新增 `FtpServerConfig`、`WebdavServerConfig`：

**NfsServerConfig 扩展**（6→12 项）：新增 `tcpConnectionTimeout`、`udpConnectionTimeout`、`restartConnectionTimeout`、`maxConcurrentConnectionsPerUser`、`directoryCacheExpiry`（数值，best-effort 读取，旧版 Windows 可能无此字段）+ `anonymousUid`、`anonymousGid`（只读）。

**FtpServerConfig 新建**（22 项）：SSL 策略(5) + 认证(3) + 防火墙端口(2) + 消息(5) + 目录浏览(1) + 用户隔离(1) + 超时(3) + 文件处理(2) + 日志(2)。参考 IIS `ftpServer/*` 配置节。

**WebdavServerConfig 新建**（14 项）：authoring(2) + 请求筛选(3) + 认证(3) + 请求限制(2) + 只读信息(4)。参考 IIS `system.webServer/*` 配置节。

### Step 2: 后端实现（可并行 2a/2b/2c）

**2a. `electron/services/nfs.ts`**：
- `defaultConfig()` 增加新字段默认值
- `getConfig()` 增加 `undefined` 守卫读取（参照 `smb.ts:77-83` 的 `raw.Field !== undefined ? ... : default` 模式）
- `setConfig()` 字段映射表扩展，数值字段直接拼参数

**2b. `electron/services/ftp.ts`**：在现有 4 个服务控制函数后追加：
- `defaultConfig()` — IIS FTP 7.5+ 默认值
- `getConfig()` — 单次 PowerShell 批量读取所有节（`MACHINE/WEBROOT/APPHOST`），每个 `Get-WebConfigurationProperty` 包 `try/catch`，整体失败返回 `defaultConfig()`
- `ensureFtpSectionsUnlocked()` — `appcmd unlock config` 预解锁 `ftpServer/security/authentication/*`、`ftpServer/security/ssl` 等 11 个节（参照 `webdavAdapter.ts:82-93` 的 `ensureWebdavSectionsUnlocked` 模式）
- `setConfig()` — 入口先调 `ensureFtpSectionsUnlocked()`，每个字段独立 `Set-WebConfigurationProperty` 包 PowerShell `try/catch`（项目记忆约束：`-ErrorAction SilentlyContinue` 无法抑制终止性错误），拼成单条命令 `runPowerShellVoid({ retries: 0 })`
- `restoreDefault()` — 调 `setConfig(defaultConfig())`

**2c. `electron/services/webdav.ts`**：同 FTP 模式，关键差异：
- filter 路径必须用完整路径（`system.webServer/webdav/authoring` 等，项目记忆强约束）
- 复用 adapter 中的 `ensureWebdavSectionsUnlocked()`（提取到 `webdav.ts` 并 export，adapter 改为 import）

### Step 3: IPC handlers (`electron/ipc/index.ts`)

FTP 块（147-150 行）和 WebDAV 块（152-156 行）各追加 4 个 handler：`getConfig`/`setConfig`/`restoreDefault`/`defaultConfig`，参照 SMB 块（83-93 行）模式。

### Step 4: Preload (`electron/preload.ts`)

- `nfs` 对象：`unknown` 类型收窄为 `NfsServerConfig` / `Partial<NfsServerConfig>`
- `ftp` 对象：追加 `getConfig`/`setConfig`/`restoreDefault`/`defaultConfig` 方法
- `webdav` 对象：同上
- import 列表追加 `NfsServerConfig, FtpServerConfig, WebdavServerConfig`

### Step 5: 前端类型 (`src/types.ts`)

`WinShareApi` 接口同步更新：`nfs` 收窄类型，`ftp`/`webdav` 追加 4 个配置方法签名。import/export 列表追加 `FtpServerConfig, WebdavServerConfig`。

### Step 6: 前端组件（可并行 6a/6b/6c）

**6a. `src/components/NfsSettingsPanel.tsx`**：扩展表单，将"高级"Collapse 拆为"连接与超时"（4 个 InputNumber，可写）+ "身份映射"（4 个 disabled Input，只读）。

**6b. `src/components/FtpSettingsPanel.tsx`**（新建）：克隆 `NfsSettingsPanel.tsx` 结构（installed 三态：null 检测中 / false 引导安装 / true 渲染表单），分组：
- SSL / 安全（Select × 2 + Input × 1 + Switch × 3）
- 认证（Switch × 2 + Input × 1）
- 被动端口范围（InputNumber × 2）
- 高级 Collapse：消息(TextArea × 4 + Switch × 1) + 目录与隔离(Switch × 1 + Select × 1) + 超时(InputNumber × 3) + 文件处理(Switch × 2) + 日志(Input × 1 + Select × 1)
- 底部：保存/恢复默认/重启/启停/刷新 + 服务状态 Descriptions

**6c. `src/components/WebdavSettingsPanel.tsx`**（新建）：同结构，分组：
- WebDAV authoring（Switch × 1 + InputNumber × 1）
- 请求筛选（InputNumber × 1 + Switch × 2）
- 认证（Switch × 3）
- 高级 Collapse：请求限制(InputNumber × 2) + 只读信息(Descriptions)
- 底部按钮同 NFS/FTP

### Step 7: Settings.tsx 集成

`src/pages/Settings.tsx` 第 617-623 行：`IisServicePanel protocol="ftp"` → `<FtpSettingsPanel />`，`IisServicePanel protocol="webdav"` → `<WebdavSettingsPanel />`。更新 import。

### Step 8: 验证

1. `pnpm typecheck` 通过
2. 重启 dev server（主进程改动不热重载）
3. 手动测试：各协议设置页三态切换（未安装→引导、已安装→表单）、配置写入后回读一致、恢复默认、重启服务

## 关键约束（遵循项目记忆）

- IIS 配置节锁定：`Set-WebConfigurationProperty` 必须包在 PowerShell `try/catch` 内（非 `-ErrorAction SilentlyContinue`）
- WebDAV filter 路径必须用完整路径 `system.webServer/webdav/authoring`
- WebDAV/FTP 配置节需 `appcmd unlock config` 预解锁
- PowerShell 经 `runPowerShell`/`runPowerShellVoid`（`-EncodedCommand` Base64 UTF-16LE）
- 查询设 `retries: 0`，读取失败返回默认配置
- 字符串字段经 `psQuote` 转义防注入
