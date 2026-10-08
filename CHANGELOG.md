# Changelog

本文件记录 WinShare Panel 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [未发布] - 2026-06

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
- 仍开放（专项级，见 docs/audit/00-backlog.md §3.5）：R-8 数据获取模式重构（复启 set-state-in-effect）、R-5 全量组件抽象（三 PermPanel/面板表单复用）、jsdom 组件测试基建

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
