# PowerShell 常驻进程池设计方案

## 摘要

当前 `runPowerShell`/`runPowerShellVoid`（[electron/lib/powershell.ts](file:///e:/workspace/win-share-panel/electron/lib/powershell.ts)）每次调用都通过 `execFile` 拉起一个全新的 `powershell.exe` 进程。每个进程需承担 .NET CLR 初始化 + PowerShell 引擎启动 + 模块加载开销（约 300–500ms）。虽然已用 `Promise.allSettled` 把多个命令改为并行，但每次刷新页面仍要重新支付 N 次进程启动开销（仪表盘刷新 ≈ 5 个进程 ≈ 1.5–2.5s）。

本方案引入一个**常驻 PowerShell 进程池**：维护 N 个长期存活的 PowerShell worker，通过 stdin/stdout 管道协议复用进程，把"每次刷新"的命令执行从 ~300ms/条 降到 ~30–80ms/条。`runPowerShell`/`runPowerShellVoid` 签名与行为完全不变，100+ 调用点和全部既有测试零改动。

---

## 现状分析

### 当前实现（[electron/lib/powershell.ts:30-72](file:///e:/workspace/win-share-panel/electron/lib/powershell.ts)）
- `runPowerShell<T>`：`execFile('powershell.exe', ['-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand', base64])`，命令拼接 `| ConvertTo-Json -Depth 5 -Compress`，解析 stdout JSON。
- `runPowerShellVoid`：同上但不拼接 ConvertTo-Json，丢弃 stdout。
- 共享逻辑：`encodeCommand`（UTF-16LE→Base64，规避中文 GBK 代码页问题）、`UTF8_PREFIX`（强制 UTF-8 输出 + 抑制进度流）、`parseJson`、`isRetryable`、`formatPsError`、`stripClixml`、重试循环、超时。

### 调用面（Phase 1 探索结果）
- 13 个服务文件、100+ 处调用：`share.ts`、`user.ts`、`smb.ts`、`session.ts`、`system.ts`、`nfs.ts`、`ftp.ts`、`webdav.ts`、`protocol/detect.ts`、4 个 `*Adapter.ts`。
- 所有调用都经 `runPowerShell`/`runPowerShellVoid` 两个入口，无直接 `execFile`/`spawn` 散落。

### 测试现状
- 既有测试（`*.test.ts`）用 `vi.mock('../../lib/powershell', ...)` + `vi.hoisted` **整体替换** powershell 模块，从不触达真实实现。
- vitest 配置（[vitest.config.ts](file:///e:/workspace/win-share-panel/vitest.config.ts)）：`environment: 'node'`，`include: ['electron/**/*.test.ts']`。

### Electron 生命周期（[electron/main.ts:141-156](file:///e:/workspace/win-share-panel/electron/main.ts)）
- `app.whenReady()` → `registerIpc()` + 建窗口。
- `app.on('before-quit')` → 销毁托盘（已有 `// 未来：添加进程池清理逻辑` 注释占位）。

### 关键约束（决定设计）
1. **签名/行为不变**：`runPowerShell`/`runPowerShellVoid` 及全部导出辅助函数（`psQuote`/`validateName`/`psBool` 等）保持原样，调用点零改动。
2. **错误语义等价**：终止性错误→抛 `formatPsError`；非终止性错误→按当前行为吞掉（当前依赖退出码：终止性错误→非零退出→抛；非终止性→零退出→返回输出）。进程池必须镜像此语义。
3. **超时与重试**：每命令独立超时；可重试错误（超时/ETIMEDOUT/ECONNRESET）自动重试。
4. **并行性**：保留 `Promise.allSettled` 并行（检测 3 并发、共享列表 4 并发、仪表盘 5 并发），池需支持并发派发。
5. **中文编码**：保留 UTF-16LE Base64 编码，规避 GBK 代码页破坏中文。

---

## 设计决策

### 架构：常驻 worker + stdin/stdout 标记协议 + 池化派发

每个 worker 是一个长期存活的 `powershell.exe`，启动时加载一段"服务端脚本"（经 `-EncodedCommand` 传入），该脚本循环从 stdin 读取 Base64 编码的命令、执行、向 stdout 写入带标记的结果。池管理 N 个 worker + 命令队列，把命令派发给空闲 worker。

### 为什么是"服务端脚本"而非裸 REPL
- 裸 `powershell.exe` 交互模式按行解析，多语句/含换行命令会被拆散；且无法可靠区分一条命令的输出边界。
- 服务端脚本用 `[Console]::In.ReadLine()` 按行读取 **Base64 载荷**（无换行），用 `[ScriptBlock]::Create($body)` 解析执行（与 `-EncodedCommand` 同解析器，语义一致），再用固定标记分隔输出。彻底规避换行/边界问题。

### Worker 服务端脚本（关键逻辑）
```powershell
[Console]::OutputEncoding=[System.Text.Encoding]::UTF8
$OutputEncoding=[System.Text.Encoding]::UTF8
$ProgressPreference='SilentlyContinue'
# 注意：不设 $ErrorActionPreference='Stop' —— 保持默认 'Continue'，
# 镜像当前"非终止性错误被吞、终止性错误触发抛错"的退出码语义。
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($null -eq $line) { break }            # stdin 关闭 → 退出
  if ($line -eq '___PS_QUIT___') { break }  # 优雅关闭
  if ($line -eq '') { continue }
  try {
    $script = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($line))
    $mode = $script.Substring(0,4)          # 'JSON' | 'VOID'
    $body = $script.Substring(5)
    $sb = [ScriptBlock]::Create($body)
    if ($mode -eq 'JSON') {
      $result = & $sb
      if ($null -ne $result) { $result | ConvertTo-Json -Depth 5 -Compress }
    } else {
      $null = & $sb
    }
    [Console]::Out.WriteLine('___PS_OK_<token>___'); [Console]::Out.Flush()
  } catch {
    [Console]::Out.WriteLine('___PS_ERR_<token>___')
    [Console]::Out.WriteLine($_.Exception.Message)
    [Console]::Out.WriteLine('___PS_ERR_END_<token>___'); [Console]::Out.Flush()
  }
}
```
- 载荷 = `encodeCommand(mode + '\u0000' + command)`（`\u0000` 分隔 mode 与 body，不会出现在命令中）。
- JSON 模式：服务端执行 `& $sb` 后对结果 `ConvertTo-Json`（等价当前 `<command> | ConvertTo-Json`）。
- VOID 模式：`$null = & $sb` 丢弃输出。
- 成功：stdout = `<json 输出>\n___PS_OK_<token>___`；失败：`___PS_ERR_<token>___\n<msg>\n___PS_ERR_END_<token>___`。
- **不设全局 ErrorActionPreference**：try/catch 仅捕获终止性错误（等价当前非零退出码）；非终止性错误被吞（等价当前零退出码）。行为与现状一致。

### 标记 token
- 用进程级随机 token（如 `7F3A2E9B`）拼入标记，避免命令输出偶发包含标记串。所有 worker 共享同一 token（启动时生成一次）。

### 池参数
- **池大小**：默认 3（匹配典型并发：检测 3 / 列表 4 / 仪表盘 5，3 个 worker 使多数命令无需排队且内存可控 ~90–150MB）。env `WINSHARE_PS_POOL_SIZE` 可覆盖。
- **懒启动 + 预热**：worker 首次有命令时才 spawn；`app.whenReady()` 后后台预热 1 个（首屏即有 1 个热 worker，其余并行 spawn ~300ms）。
- **无空闲回收**：控制面板长驻运行，worker 常驻不超时回收。
- **安全阀**：env `WINSHARE_PSPOOL=0` 禁用池，回退原 `execFile` 路径（保留 `runPowerShellLegacy` 供回退，便于灰度排障）。

### 每命令超时 → worker 中毒重启
- 派发命令时 `setTimeout(timeout)`。超时未收到标记：
  1. reject 该命令 promise（`formatPsError` 超时文案）。
  2. `child.kill()` 该 worker（卡死命令无法干净中断）。
  3. 标记 worker dead，从池移除，按需补 spawn 新 worker。
- 超时罕见（默认 15s），中毒重启成本可接受。

### Worker 崩溃处理
- `child.on('exit')` 在 worker.busy 时触发 → reject 在飞命令（标记为可重试，由 runPowerShell 重试循环换新 worker 重试）→ 移除并补 spawn。
- `child.on('exit')` 在 idle 时触发 → 标记 dead，按需补 spawn（保持池容量）。

### 派发算法
- 命令入队 → `dispatch()`：若有空闲 worker 立即派发；否则等待。worker 完成命令（收到 OK/ERR 标记）后置 idle，触发 `dispatch()` 派发下一条。
- stdout 增量缓冲：积累到出现 `___PS_OK_<token>___` 或 `___PS_ERR_<token>___`，按模式解析，清缓冲（保留标记后残余字节以防边界）。

### 可测性：注入 WorkerFactory
```typescript
type WorkerHandle = {
  stdin: Writable
  stdout: Readable
  stderr: Readable
  on(event: 'exit', cb: (code: number | null) => void): void
  kill(signal?: string): void
}
type WorkerFactory = (serverEncoded: string) => WorkerHandle
```
- 生产 factory：`child_process.spawn('powershell.exe', [...,'-EncodedCommand', serverEncoded], { stdio:['pipe','pipe','pipe'], windowsHide:true })`。
- 测试 factory：返回由测试控制的伪造流对（向 fake stdout 写标记响应、可模拟 exit/超时）。池的队列/派发/超时/崩溃/标记解析逻辑完全可单测，无需真实 PowerShell。

---

## 改动清单

### 1. 新增 `electron/lib/powershellPool.ts`
- `SERVER_SCRIPT` 常量（上文服务端脚本）。
- `encodeCommand` 复用 powershell.ts 的同名逻辑（导出后复用，避免重复）。
- `WorkerFactory` / `WorkerHandle` 类型；默认 `defaultWorkerFactory`（真实 spawn）。
- `PowerShellPool` 类：`workers`、`queue`、`token`、`size`、`factory`；方法 `execute(command, mode, opts): Promise<string>`（返回原始 JSON 串或抛错）、`prewarm()`、`shutdown()`、`dispatch()`、`spawnWorker()`、`onStdoutData()`、`onExit()`。
- 模块级单例：`getPool()`（懒创建）、`shutdownPool()`、`prewarmPool()`、`isPoolEnabled()`（读 env）。
- **模块导入无副作用**：单例懒创建，首次 `execute` 才 spawn，避免破坏测试与启动。

### 2. 修改 `electron/lib/powershell.ts`
- 保留并导出 `encodeCommand`、`UTF8_PREFIX`（供 pool 复用/兼容）。
- `runPowerShell<T>` / `runPowerShellVoid`：重试循环不变；循环内把执行体从 `execFileAsync(...)` 改为：
  - `isPoolEnabled()` → `pool.execute(command, 'JSON'|'VOID', opts)` → 拿到 JSON 串 → `parseJson<T>`。
  - 否则 → 现有 `execFileAsync` 路径（抽为 `execOnce` 内部函数保留）。
- 全部导出辅助函数（`psQuote`/`psEscapeSingle`/`validateName`/`validatePath`/`psBool`/`psNumber`/`psEnum`）不变。
- `formatPsError`/`stripClixml`/`isRetryable`/`parseJson`/`sleep` 不变，pool 抛出的错误经同一 `formatPsError` 处理（pool 把 PS_ERR 消息构造成与 execFile stderr 等价的错误对象）。

### 3. 修改 `electron/main.ts`
- `import { prewarmPool, shutdownPool } from './lib/powershellPool'`。
- `app.whenReady().then(...)` 内 `createTray()` 后追加 `prewarmPool()`（后台预热，不 await，不阻塞首屏）。
- `app.on('before-quit')` 内 `tray?.destroy()` 后追加 `shutdownPool()`；并在 `app.on('will-quit')` 兜底再调一次（幂等）。

### 4. 新增 `electron/lib/powershellPool.test.ts`
注入伪造 WorkerFactory，覆盖：
- 单命令 JSON 模式执行并解析返回。
- 多命令 FIFO 排队执行。
- 并发命令跨多 worker 并行派发（断言启动顺序）。
- 标记解析：多行 JSON、空输出（VOID）。
- ERR 路径：抛 `formatPsError` 等价错误。
- 超时：reject + kill worker + 补 spawn。
- Worker 崩溃（busy 时 exit）：reject 在飞命令 + 补 spawn。
- Worker 崩溃（idle 时 exit）：标记 dead，下次命令补 spawn。
- 重试：可重试错误换新 worker 重试成功。
- `shutdown()`：kill 全部 worker，队列清空。

### 既有测试：零改动
- `*.test.ts` 整体 mock `../../lib/powershell`，不触达 pool；`pnpm test` 应全绿（验证不回归）。

---

## 假设与决策
- **不设全局 `$ErrorActionPreference='Stop'`**：为严格镜像当前"非终止性错误被吞、终止性错误抛错"的退出码语义，避免行为漂移引发新弹错。（决策）
- **保留 env 安全阀 `WINSHARE_PSPOOL=0`**：用户偏好"完整替换以保一致性"，池为默认路径；但首版保留回退阀便于灰度排障，回退代码不污染默认行为。（决策）
- **池大小默认 3**：内存（~90–150MB）与并发（峰值 ~5）的平衡点；可 env 覆盖。（决策）
- **懒启动 + 预热 1**：首屏已有并行 spawn 优势，预热 1 个让首命令命中热 worker；常驻不回收。（决策）
- **payload 用 `\u0000` 分隔 mode/body**：null 字节不会出现在合法 PowerShell 命令中，安全。（假设）
- **服务端脚本经 `-EncodedCommand` 启动**：与现有编码方式一致，规避代码页问题；启动后转 stdin 协议。（决策）

---

## 验证步骤
1. `pnpm typecheck` 通过（含新增 pool 文件）。
2. `pnpm test` 通过：既有测试全绿（零回归）+ 新增 `powershellPool.test.ts` 全绿。
3. `pnpm dev` 手动验证：
   - 首次加载：`[perf]` 日志显示检测/列表/仪表盘耗时。
   - **刷新页面 3 次**：对比刷新耗时应显著下降（worker 已热，~30–80ms/条 vs ~300ms/条）。
   - 触发一个错误命令（如对不存在共享 `Remove-SmbShare`）：确认错误仍以 `formatPsError` 文案正确抛出。
   - 触发超时场景（可选）：确认 worker 中毒重启后后续命令正常。
   - 关闭窗口到托盘再打开：worker 常驻，刷新仍快。
   - 退出应用：任务管理器确认无残留 `powershell.exe` 子进程。
4. （可选）设 `WINSHARE_PSPOOL=0` 回退验证：行为与改动前一致。
