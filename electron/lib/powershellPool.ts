import { spawn } from 'child_process'
import type { Writable, Readable } from 'stream'

// === PowerShell 常驻进程池 ===
//
// 背景：runPowerShell/runPowerShellVoid 原本每次调用都 execFile 拉起新 powershell.exe，
// 每条命令承担 ~300-500ms 的 CLR+引擎+模块启动开销。本池维护 N 个长期存活的 worker，
// 通过 stdin/stdout 标记协议复用进程，把命令执行降到 ~30-80ms/条。
//
// 协议：worker 启动时加载"服务端脚本"（经 -EncodedCommand 传入），脚本循环：
//   1. 从 stdin 读取一行 Base64 载荷（无换行，避免 REPL 行解析问题）
//   2. 解码为 UTF-16LE 字符串，前 4 字符为 mode('JSON'/'VOID')，第 5 字节为 \u0000 分隔，其余为命令体
//   3. [ScriptBlock]::Create(body) 解析执行（与 -EncodedCommand 同解析器，语义一致）
//   4. JSON 模式：对结果 ConvertTo-Json；VOID 模式：丢弃输出
//   5. stdout 写入带 token 的标记：成功 ___PS_OK_<token>___；失败 ___PS_ERR_<token>___\n<msg>\n___PS_ERR_END_<token>___
//
// 池派发：命令入队 → 找空闲 worker 派发；无空闲且未达容量则 spawn 新 worker；全忙则等待。
// 超时：每命令独立计时，超时则 reject + kill 中毒 worker + 补 spawn。
// 崩溃：worker.exit 时若在飞命令则 reject（可重试），移除并补 spawn。
//
// 错误语义镜像原 execFile：不设全局 $ErrorActionPreference（保持默认 'Continue'），
// try/catch 仅捕获终止性错误（等价原非零退出码→抛），非终止性错误被吞（等价原零退出码→返回输出）。

export type Mode = 'JSON' | 'VOID'

export interface PoolExecuteOptions {
  timeout: number
}

// Worker 句柄抽象：生产用 child_process.spawn，测试用伪造流。on 返回 void 以兼容 EventEmitter/ChildProcess。
export interface WorkerHandle {
  readonly stdin: Writable
  readonly stdout: Readable
  readonly stderr: Readable
  on(event: string, listener: (...args: any[]) => void): void
  kill(signal?: string): boolean
}

export type WorkerFactory = (serverEncodedCommand: string) => WorkerHandle

interface PendingCommand {
  command: string
  mode: Mode
  timeout: number
  resolve: (jsonStr: string) => void
  reject: (err: Error) => void
  timeoutTimer: NodeJS.Timeout | null
}

interface Worker {
  handle: WorkerHandle
  alive: boolean
  busy: boolean
  current: PendingCommand | null
  buffer: string
}

// UTF-16LE → Base64（与 powershell.ts 的 encodeCommand 一致，规避中文 GBK 代码页问题）
function encodeCommand(command: string): string {
  return Buffer.from(command, 'utf16le').toString('base64')
}

// 载荷 = encodeCommand(mode + '\u0000' + command)；\u0000 不会出现在合法命令中
function buildPayload(mode: Mode, command: string): string {
  return encodeCommand(mode + '\u0000' + command)
}

function randomToken(): string {
  return Math.random().toString(16).slice(2, 10).toUpperCase().padEnd(8, '0')
}

// worker 服务端脚本：设置 UTF-8 输出 + 抑制进度流，循环读 stdin 执行并写标记
function buildServerScript(
  okMarker: string,
  errMarker: string,
  errEndMarker: string,
  quitMarker: string
): string {
  return [
    "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8",
    "$OutputEncoding=[System.Text.Encoding]::UTF8",
    "$ProgressPreference='SilentlyContinue'",
    'while ($true) {',
    '  $line = [Console]::In.ReadLine()',
    '  if ($null -eq $line) { break }',
    `  if ($line -eq '${quitMarker}') { break }`,
    "  if ($line -eq '') { continue }",
    '  try {',
    '    $script = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($line))',
    '    $mode = $script.Substring(0,4)',
    '    $body = $script.Substring(5)',
    '    $sb = [ScriptBlock]::Create($body)',
    "    if ($mode -eq 'JSON') {",
    '      $result = & $sb',
    '      if ($null -ne $result) { $result | ConvertTo-Json -Depth 5 -Compress }',
    '    } else {',
    '      $null = & $sb',
    '    }',
    `    [Console]::Out.WriteLine('${okMarker}'); [Console]::Out.Flush()`,
    '  } catch {',
    `    [Console]::Out.WriteLine('${errMarker}')`,
    "    [Console]::Out.WriteLine($_.Exception.Message)",
    `    [Console]::Out.WriteLine('${errEndMarker}'); [Console]::Out.Flush()`,
    '  }',
    '}'
  ].join('\n')
}

// 默认工厂：spawn 真实 powershell.exe，加载服务端脚本
export function defaultWorkerFactory(serverEncoded: string): WorkerHandle {
  const child = spawn(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', serverEncoded],
    { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true }
  )
  return {
    // stdio:'pipe' 下三者非 null
    stdin: child.stdin as Writable,
    stdout: child.stdout as Readable,
    stderr: child.stderr as Readable,
    on(event, listener) {
      child.on(event as string, listener as (...args: any[]) => void)
    },
    kill(signal) {
      return child.kill(signal as NodeJS.Signals | undefined)
    }
  }
}

export interface PowerShellPoolOptions {
  size?: number
  factory?: WorkerFactory
  token?: string
}

export class PowerShellPool {
  private workers: Worker[] = []
  private queue: PendingCommand[] = []
  private readonly token: string
  private readonly size: number
  private readonly factory: WorkerFactory
  private readonly okMarker: string
  private readonly errMarker: string
  private readonly errEndMarker: string
  private readonly quitMarker: string
  private shuttingDown = false

  constructor(opts: PowerShellPoolOptions = {}) {
    this.size = Math.max(1, opts.size ?? 3)
    this.factory = opts.factory ?? defaultWorkerFactory
    this.token = opts.token ?? randomToken()
    this.okMarker = `___PS_OK_${this.token}___`
    this.errMarker = `___PS_ERR_${this.token}___`
    this.errEndMarker = `___PS_ERR_END_${this.token}___`
    this.quitMarker = `___PS_QUIT_${this.token}___`
  }

  // 提交命令，返回标记前的原始 stdout（JSON 串；VOID 模式为空串）
  execute(command: string, mode: Mode, opts: PoolExecuteOptions): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      if (this.shuttingDown) {
        reject(new Error('PowerShell 进程池已关闭'))
        return
      }
      this.queue.push({ command, mode, timeout: opts.timeout, resolve, reject, timeoutTimer: null })
      this.dispatch()
    })
  }

  // 后台预热 1 个 worker（不阻塞，失败静默——首次 execute 时会按需 spawn）
  prewarm(): void {
    if (this.shuttingDown) return
    if (this.workers.some((w) => w.alive)) return
    try {
      this.spawnWorker()
    } catch {
      // 预热失败忽略，execute 时再尝试
    }
  }

  shutdown(): void {
    this.shuttingDown = true
    // 拒绝所有排队命令（避免悬挂 promise）
    const q = this.queue.splice(0)
    for (const p of q) {
      if (p.timeoutTimer) clearTimeout(p.timeoutTimer)
      p.reject(new Error('PowerShell 进程池已关闭'))
    }
    // 优雅退出 + 强杀兜底；同时拒绝在飞命令（避免悬挂 promise）
    for (const w of this.workers) {
      w.alive = false
      if (w.current) {
        const cmd = w.current
        if (cmd.timeoutTimer) clearTimeout(cmd.timeoutTimer)
        cmd.timeoutTimer = null
        w.current = null
        cmd.reject(new Error('PowerShell 进程池已关闭'))
      }
      try {
        w.handle.stdin.write(this.quitMarker + '\n')
      } catch {
        // stdin 已关闭，忽略
      }
      try {
        w.handle.kill()
      } catch {
        // 忽略
      }
    }
    this.workers = []
  }

  private spawnWorker(): Worker {
    const script = buildServerScript(this.okMarker, this.errMarker, this.errEndMarker, this.quitMarker)
    const handle = this.factory(encodeCommand(script))
    if (typeof handle.stdout.setEncoding === 'function') {
      handle.stdout.setEncoding('utf8')
    }
    const worker: Worker = { handle, alive: true, busy: false, current: null, buffer: '' }
    this.workers.push(worker)

    handle.stdout.on('data', (chunk: string | Buffer) => this.onStdoutData(worker, chunk))
    handle.stderr.on('data', (chunk: string | Buffer) => {
      const text = typeof chunk === 'string' ? chunk : chunk.toString('utf8')
      const t = text.trim()
      if (t) console.debug('[psPool] worker stderr:', t.slice(0, 200))
    })
    // worker 退出时写 stdin 可能 EPIPE，吞掉由 exit 处理
    handle.stdin.on('error', () => {
      /* 忽略 */
    })
    handle.on('exit', (code: number | null) => this.onExit(worker, code))
    handle.on('error', () => this.onExit(worker, null))

    return worker
  }

  private dispatch(): void {
    if (this.shuttingDown) return
    while (this.queue.length > 0) {
      let worker = this.workers.find((w) => w.alive && !w.busy)
      if (!worker) {
        const aliveCount = this.workers.filter((w) => w.alive).length
        if (aliveCount < this.size) {
          try {
            worker = this.spawnWorker()
          } catch {
            // spawn 失败：暂停派发，等下次 execute/回调重试
            return
          }
        } else {
          // 全忙：等待某 worker 完成后触发 dispatch
          return
        }
      }
      const cmd = this.queue.shift() as PendingCommand
      this.assign(worker, cmd)
    }
  }

  private assign(worker: Worker, cmd: PendingCommand): void {
    worker.busy = true
    worker.current = cmd
    worker.buffer = ''
    const payload = buildPayload(cmd.mode, cmd.command)
    try {
      worker.handle.stdin.write(payload + '\n')
    } catch {
      // 同步写失败罕见（EPIPE 异步触发）；交由 exit 处理
    }
    cmd.timeoutTimer = setTimeout(() => this.onTimeout(worker, cmd), cmd.timeout)
  }

  private onStdoutData(worker: Worker, chunk: string | Buffer): void {
    if (!worker.alive) return
    worker.buffer += typeof chunk === 'string' ? chunk : chunk.toString('utf8')
    this.processBuffer(worker)
  }

  private processBuffer(worker: Worker): void {
    if (!worker.current) return // 无在飞命令（如预热期杂散输出），忽略

    // 错误路径：需 errMarker + errEndMarker 齐全
    const errIdx = worker.buffer.indexOf(this.errMarker)
    if (errIdx >= 0) {
      const afterErr = worker.buffer.slice(errIdx + this.errMarker.length)
      const endIdx = afterErr.indexOf(this.errEndMarker)
      if (endIdx < 0) return // 等待 ERR_END
      const errMsg = afterErr
        .slice(0, endIdx)
        .replace(/^\r?\n/, '')
        .replace(/\r?\n$/, '')
        .trim()
      worker.buffer = afterErr.slice(endIdx + this.errEndMarker.length)
      const cmd = worker.current
      this.finishCommand(worker, cmd)
      // 构造与 execFile stderr 等价的错误对象，交由 formatPsError 处理
      const e = new Error('Command failed')
      ;(e as Error & { stderr?: string }).stderr = errMsg
      cmd.reject(e)
      this.dispatch()
      return
    }

    // 成功路径
    const okIdx = worker.buffer.indexOf(this.okMarker)
    if (okIdx >= 0) {
      const jsonStr = worker.buffer.slice(0, okIdx)
      worker.buffer = worker.buffer.slice(okIdx + this.okMarker.length)
      const cmd = worker.current
      this.finishCommand(worker, cmd)
      cmd.resolve(jsonStr)
      this.dispatch()
      return
    }
    // 既无 OK 也无 ERR：继续等待更多数据
  }

  private finishCommand(worker: Worker, cmd: PendingCommand): void {
    if (cmd.timeoutTimer) {
      clearTimeout(cmd.timeoutTimer)
      cmd.timeoutTimer = null
    }
    worker.current = null
    worker.busy = false
  }

  private onTimeout(worker: Worker, cmd: PendingCommand): void {
    if (worker.current !== cmd) return // 已完成（防御）
    cmd.timeoutTimer = null
    worker.current = null // 防止 exit/迟到标记双重处理
    worker.busy = false
    worker.alive = false // 标记中毒，exit 触发时跳过
    try {
      worker.handle.kill()
    } catch {
      // 忽略
    }
    this.workers = this.workers.filter((w) => w !== worker)
    // 超时错误经 formatPsError 转为"命令执行超时"（含 "timed out" → 可重试）
    cmd.reject(new Error('Command timed out'))
    this.dispatch()
  }

  private onExit(worker: Worker, _code: number | null): void {
    if (!worker.alive) return // 已处理（超时 kill / shutdown）
    worker.alive = false
    if (worker.current) {
      const cmd = worker.current
      if (cmd.timeoutTimer) {
        clearTimeout(cmd.timeoutTimer)
        cmd.timeoutTimer = null
      }
      worker.current = null
      worker.busy = false
      // 崩溃错误含"重试"→ isRetryable 匹配 → runPowerShell 换新 worker 重试
      cmd.reject(new Error('PowerShell 进程意外退出，请重试'))
    }
    this.workers = this.workers.filter((w) => w !== worker)
    this.dispatch()
  }
}

// === 模块级单例（懒创建，导入无副作用）===

let poolSingleton: PowerShellPool | null = null

export function isPoolEnabled(): boolean {
  return process.env.WINSHARE_PSPOOL !== '0'
}

export function getPool(): PowerShellPool {
  if (!poolSingleton) {
    const size = Number(process.env.WINSHARE_PS_POOL_SIZE) || 3
    poolSingleton = new PowerShellPool({ size })
  }
  return poolSingleton
}

export function prewarmPool(): void {
  if (isPoolEnabled()) getPool().prewarm()
}

export function shutdownPool(): void {
  poolSingleton?.shutdown()
  poolSingleton = null
}

// 测试专用：重置单例
export function __resetPoolForTesting(): void {
  poolSingleton?.shutdown()
  poolSingleton = null
}
