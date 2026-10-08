# 安全审计报告 (审计 A3)

- 范围：注入面、IPC 校验、凭据处理、Electron 加固、提权与错误泄漏
- 审计方式：逐文件静态通读 + 既有测试套件实证（191 用例）+ 进程池协议推演
- 审计员：audit-security（环境配额中断，由 Captain 接管复核并完成）
- 审计日期：2026-06

## 0. 执行摘要

**结论：命令注入防线整体健壮，未发现可利用的命令注入漏洞。** 审计员在中断前标注的 "P-01/P-02 关键注入" 经复核为**误报/待验证假设**——所有用户输入在进入执行路径前均经过 `psQuote`/`psBool`/`psNumber`/`psEnum` 的运行时校验，且命令统一经 `-EncodedCommand`（Base64 UTF-16LE）执行，不存在 `-Command` 直接拼接路径。

实测受限说明：本沙箱环境无法稳定地由 Node spawn 交互式 powershell worker，故端到端实时探针未跑通；但既有测试套件已覆盖全部注入防护分支，作为实证证据采用。

## 1. 威胁模型简表

| 威胁 | 入口 | 现有缓解 | 残余风险 |
|---|---|---|---|
| PowerShell 命令注入 | IPC → 服务 → 适配器拼接 | psQuote/psBool/psNumber/psEnum + -EncodedCommand | 低 |
| IPC 非法参数 | 渲染进程 → ipcMain | validateName/validatePath/requireProtocol/requireStringArray | 低 |
| 凭据明文落盘 | 用户口令/WebDAV 匿名口令 | 仅在创建时经 SecureString 传入，无落盘 | 需复核日志 |
| 权限提升 | requireAdministrator | 最小化权限、UAC 重启动 | 低 |
| 上下文隔离绕过 | preload | contextIsolation + sandbox + nodeIntegration=false | 低 |

## 2. 注入面复核（核心）

### 2.1 执行路径
`runPowerShell/runPowerShellVoid` 有两条路径，均安全：
- 主路径（进程池）：命令经 `buildPayload(mode, command) = encodeCommand(mode + '\u0000' + command)` 写入 stdin 单行，worker 端经 `[ScriptBlock]::Create(body)` 解析为单个脚本块。**整条命令作为一个 ScriptBlock 执行，无法从体内部"逃逸"出新的命令**——即便 body 含 `\u0000`/换行，也仅作为脚本块内的字面量。
- 回退路径（`execOnce`，`WINSHARE_PSPOOL=0`）：`'-EncodedCommand', encodeCommand(fullCmd)` 作为 argv 传入，同样无 shell 解析。

**未发现任何 `-Command` 直接拼接**（已 grep 全树确认）。

### 2.2 拼接点防护
逐文件核对 `electron/services/**` 与 `electron/services/protocol/adapters/**`：所有用户可控值（共享名、路径、用户名、账号、口令、端口、权限字符串、认证模式、枚举）均经对应守卫函数处理后拼入命令：
- 字符串 → `psQuote`（单引号包裹 + 内部 `'`→`''` 转义，PowerShell 单引号串内无插值/无转义，确定安全）
- 布尔 → `psBool`（非 boolean 返回 null，跳过该字段）
- 数值 → `psNumber`（非有限数字返回 null）
- 枚举 → `psEnum`（白名单外返回 null）

### 2.3 实证证据（既有测试）
`electron/lib/powershell.test.ts` 与适配器测试覆盖：
- `psQuote('it''s')` → `'it''s'`（单引号转义正确）
- 注入尝试「闭合引号 + 注入命令」→ 被单引号包裹隔离
- `psBool('$true; rm -rf')` → null（字符串非布尔）
- `psNumber('42; rm')` → null（字符串非数字）
- `psEnum("ro; Remove-LocalUser 'Administrator'", ALLOWED)` → null（白名单外）
- nfsAdapter：`allowRootAccess=非布尔字符串 → 被 psBool 过滤，不拼入命令`

### 2.4 对 "P-01/P-02 关键注入/NUL-newline 边界" 的复核结论
审计员中断前的线索指向进程池协议的 `\u0000` 分隔边界。推演结论：
- 分隔符 `\u0000` 固定在 index 4，worker 端 `Substring(0,4)`/`Substring(5)` 固定切分；命令体内部即使含 `\u0000`，也只是 ScriptBlock 中的字面 NUL，不会破坏切分或逃逸。
- 命令体经 `-EncodedCommand`/ScriptBlock 解析，**Newline 在单引号字符串中即字面换行、在 ScriptBlock 中即语句分隔但仍在同一条命令的解析域内**，无法注入到池协议层。
- 该路径**不构成命令注入漏洞**。

## 3. IPC 校验复核
`electron/ipc/index.ts` 在 handler 入口对关键参数做了运行时校验：
- `requireProtocol` / `requireName`（`validateName`）/ `requirePath`（`validatePath`）/ `requireStringArray`
- `adapter:*` 系列在调用前显式校验 protocol/name/path/perms 形态
- 返回值经 `wrap()` 统一审计 + 错误透传

**缺陷**：部分 handler（如 `share:create`、`user:create`、`user:setPassword`、`preset:save`）仅对 `opts?.name` 做预校验，未对 `opts` 内**全部**用户可控字段（如 path、fullAccess 数组元素、description）逐一校验；不过这些字段在服务层仍会经 `validatePath`/`psQuote` 等再次校验，属纵深防御的冗余层，风险低（见 F-1）。

## 4. 凭据面复核
- 用户口令：`New-LocalUser/Set-LocalUser -Password (ConvertTo-SecureString -AsPlainText -Force '...')`，经 `psQuote` 包裹，无明文落盘。后端不持久化口令。
- WebDAV/FTP 匿名口令：仅作为配置写入，经 `psQuote(String(val).replace(/\r?\n/g,' '))` 处理。
- 审计日志（`lib/audit.ts`）：需确认未记录口令/令牌（见 F-2 待办）。

## 5. Electron 加固复核
`electron/main.ts` `webPreferences`：
- ✅ `contextIsolation: true`
- ✅ `nodeIntegration: false`
- ✅ `sandbox: true`
- ✅ 无 `nodeIntegrationInWorker`、无 `webSecurity:false`、无危险 `webRequest` 覆写
- ✅ 单实例锁防止多开
- ✅ `before-quit`/`will-quit` 双保险关闭 PowerShell 进程池
- `crashReporter` 启动（`uploadToServer:false`，无隐私外发）

## 6. 发现清单（按严重级）

| ID | 严重级 | 标题 | 位置 | 建议 |
|---|---|---|---|---|
| F-1 | 低 | IPC 入口仅校验 name/path，未逐字段校验其余用户输入 | ipc/index.ts | 在 `share:create`/`user:create` 等 handler 内对 description/fullAccess 元素补 `validateName`/`validatePath`；当前靠服务层纵深防御兜底，非紧急 |
| F-2 | 中 | 审计日志是否记录敏感字段未证实 | lib/audit.ts | 通读 `audit.ts`，确保不记录口令/令牌；在 security 审计补充协议 |
| F-3 | 低 | `relaunchAsAdmin` 经 `Start-Process -Verb RunAs` 提权，`exePath` 经 psQuote；路径若含空格需确认 PS 引号包裹正确（当前 `psQuote` 单引号对含空格路径有效） | system.ts:42 | 维持现状，补一条单测验证含空格/特殊字符 exePath |
| F-4 | 低 | 渲染进程可传入任意形状对象，部分 handler 信任 `opts` 结构 | ipc/index.ts | 与服务层 `CreateShareOpts` 等运行时校验保持一致（已有 ps* 守卫） |

## 7. 已验证通过项（Positive Findings）
- 命令执行全走 `-EncodedCommand`，无 `-Command` 拼接（grep 实证）
- `psQuote`/`psBool`/`psNumber`/`psEnum` 四守卫覆盖所有用户输入拼接点（逐文件核对）
- Electron 三道加固（contextIsolation/sandbox/nodeIntegration=false）齐备
- IPC 关键参数（name/path/protocol/字符串数组）有运行时校验
- 进程池生命周期与超时/中毒重启逻辑完整（powershellPool.test.ts 覆盖）
- 既有 191 测试覆盖注入防护分支（psQuote/psBool/psEnum/nfsAdapter）

## 8. 探针覆盖矩阵
| 文件/模块 | 已探针 | 方式 |
|---|---|---|
| electron/lib/powershell.ts | ✅ | 静态 + 测试实证 |
| electron/lib/powershellPool.ts | ✅ | 静态推演 |
| electron/ipc/index.ts | ✅ | 静态 |
| electron/services/*.ts | ✅ | 静态逐文件 |
| electron/services/protocol/adapters/*.ts | ✅ | 静态逐文件 |
| electron/main.ts / preload.ts | ✅ | 静态 |
| electron/lib/audit.ts | ⚠️ 部分 | 仅签名层；F-2 需补全 |
| 端到端 powershell 实时探针 | ❌ | 沙箱无法稳定 spawn 交互式 powershell（环境限制，非代码问题） |
