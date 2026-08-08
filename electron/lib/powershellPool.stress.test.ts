import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { EventEmitter, PassThrough, Writable } from 'stream'
import { PowerShellPool, type WorkerHandle, type WorkerFactory } from './powershellPool'

const TOKEN = 'TEST'
const OK = `___PS_OK_${TOKEN}___`

// 全局并发计数（跨所有 worker）：跟踪峰值并发，验证不超过池大小
let activeCommands = 0
let peakConcurrency = 0
function noteStart(): void {
  activeCommands++
  if (activeCommands > peakConcurrency) peakConcurrency = activeCommands
}
function noteEnd(): void {
  activeCommands--
}

// 模块级可配置延迟函数：每条命令的响应延迟（ms）；Infinity = 卡死（等超时）
// 通过引用读取，测试中修改后对所有（含懒 spawn 的）worker 生效
let currentDelayFn: (command: string) => number = () => 8

// 压力测试用伪造 worker：收到载荷后按 currentDelayFn 调度响应
class StressWorkerHandle extends EventEmitter implements WorkerHandle {
  written: string[] = []
  stdout = new PassThrough()
  stderr = new PassThrough()
  killed = false
  pending = false
  private stdinStream: Writable
  constructor() {
    super()
    const self = this
    this.stdinStream = new Writable({
      write(chunk, _enc, cb) {
        const payload = chunk.toString().replace(/\n$/, '')
        self.written.push(payload)
        const decoded = Buffer.from(payload, 'base64').toString('utf16le')
        const command = decoded.slice(decoded.indexOf('\u0000') + 1)
        self.pending = true
        noteStart()
        const delay = currentDelayFn(command)
        if (Number.isFinite(delay)) {
          setTimeout(() => {
            if (self.killed) return // 超时被 kill，不再响应
            self.pending = false
            noteEnd()
            self.stdout.push(`"${command}-result"\n${OK}`)
          }, delay)
        }
        // delay === Infinity → 不响应，等待池超时 kill
        cb()
      }
    })
  }
  get stdin(): Writable {
    return this.stdinStream
  }
  kill(): boolean {
    // 超时 kill：若有在飞命令，释放并发计数
    if (this.pending) {
      this.pending = false
      noteEnd()
    }
    this.killed = true
    this.emit('exit', 1, null)
    return true
  }
}

function makeStressPool(size: number, handles: StressWorkerHandle[]): PowerShellPool {
  const factory: WorkerFactory = () => {
    const h = new StressWorkerHandle()
    handles.push(h)
    return h
  }
  return new PowerShellPool({ size, factory, token: TOKEN })
}

describe('PowerShellPool 压力模拟 - 100 并发', () => {
  let handles: StressWorkerHandle[]
  let pool: PowerShellPool

  beforeEach(() => {
    handles = []
    activeCommands = 0
    peakConcurrency = 0
    currentDelayFn = () => 8
  })

  afterEach(() => {
    pool.shutdown()
  })

  it('场景1：100 并发全部成功，size=3 → 排队执行、worker 复用无 churn、并发不超限', async () => {
    pool = makeStressPool(3, handles)
    currentDelayFn = () => 8 // 每条命令 8ms
    const N = 100

    const commands = Array.from({ length: N }, (_, i) => `c${i}`)
    const t0 = Date.now()
    const results = await Promise.all(commands.map((cmd) => pool.execute(cmd, 'JSON', { timeout: 5000 })))
    const elapsed = Date.now() - t0

    // 全部成功且结果唯一
    expect(results).toHaveLength(N)
    const set = new Set(results.map((r) => r.trim()))
    expect(set.size).toBe(N)

    // 仅 spawn 3 个 worker（无 churn），证明排队复用
    expect(handles).toHaveLength(3)
    // 每个 worker 处理多条命令（约 N/3）
    const totalWrites = handles.reduce((s, h) => s + h.written.length, 0)
    expect(totalWrites).toBe(N)
    for (const h of handles) {
      expect(h.written.length).toBeGreaterThan(20) // 每个 worker 处理 30+ 条
    }

    // 峰值并发不超过池大小，且 100 命令足以打满
    expect(peakConcurrency).toBe(3)
    // 无并发计数泄漏
    expect(activeCommands).toBe(0)

    // 总耗时合理：≈ ceil(100/3)*8ms ≈ 272ms（留宽松上下界）
    console.log(`[stress1] 100 并发 size=3: 耗时 ${elapsed}ms, peak=${peakConcurrency}, workers=${handles.length}`)
    expect(elapsed).toBeGreaterThan(150) // 至少排队等待
    expect(elapsed).toBeLessThan(2000)
  }, 15000)

  it('场景2：100 并发含 10 条卡死命令（超时），size=3 → 超时 reject + 中毒重启 + 其余成功', async () => {
    pool = makeStressPool(3, handles)
    const N = 100
    // 索引 % 10 === 9 的命令卡死（c9,c19,...,c99 共 10 条），其余 6ms 响应
    currentDelayFn = (cmd) => {
      const m = cmd.match(/^c(\d+)$/)
      const idx = m ? Number(m[1]) : -1
      return idx % 10 === 9 ? Infinity : 6
    }

    const commands = Array.from({ length: N }, (_, i) => `c${i}`)
    const t0 = Date.now()
    const settled = await Promise.allSettled(
      commands.map((cmd) => pool.execute(cmd, 'JSON', { timeout: 25 }))
    )
    const elapsed = Date.now() - t0

    const fulfilled = settled.filter((s) => s.status === 'fulfilled') as PromiseFulfilledResult<string>[]
    const rejected = settled.filter((s) => s.status === 'rejected') as PromiseRejectedResult[]

    // 10 条超时 reject，90 条成功
    expect(fulfilled).toHaveLength(90)
    expect(rejected).toHaveLength(10)
    for (const r of rejected) {
      expect(r.reason.message).toBe('Command timed out')
    }
    // 成功的结果唯一
    const okSet = new Set(fulfilled.map((s) => s.value.trim()))
    expect(okSet.size).toBe(90)

    // 峰值并发不超过池大小（超时 kill 后补 spawn，不超额）
    expect(peakConcurrency).toBeLessThanOrEqual(3)
    expect(activeCommands).toBe(0)

    // worker churn：初始 3 + 10 次超时重启，spawn 总数 <= 3 + 10
    expect(handles.length).toBeGreaterThanOrEqual(3)
    expect(handles.length).toBeLessThanOrEqual(3 + 10)
    // 被超时 kill 的 worker 共 10 个（10 条慢命令各 kill 一个）
    const killedCount = handles.filter((h) => h.killed).length
    expect(killedCount).toBe(10)

    console.log(`[stress2] 100 并发(10 超时) size=3: 耗时 ${elapsed}ms, peak=${peakConcurrency}, workers=${handles.length}, killed=${killedCount}`)
  }, 15000)

  it('场景3：100 并发全部卡死（极端），size=3 timeout=20 → 全部超时，无丢失/悬挂，池可恢复', async () => {
    pool = makeStressPool(3, handles)
    const N = 100
    currentDelayFn = () => Infinity // 全部卡死

    const commands = Array.from({ length: N }, (_, i) => `c${i}`)
    const t0 = Date.now()
    const settled = await Promise.allSettled(
      commands.map((cmd) => pool.execute(cmd, 'JSON', { timeout: 20 }))
    )
    const elapsed = Date.now() - t0

    // 全部超时 reject，无 fulfilled，无悬挂
    expect(settled).toHaveLength(N)
    const rejected = settled.filter((s) => s.status === 'rejected') as PromiseRejectedResult[]
    expect(rejected).toHaveLength(N)
    for (const r of rejected) {
      expect(r.reason.message).toBe('Command timed out')
    }

    // 峰值并发不超过池大小，无计数泄漏
    expect(peakConcurrency).toBeLessThanOrEqual(3)
    expect(activeCommands).toBe(0)

    // 每条命令都 kill 了一个 worker
    expect(handles.filter((h) => h.killed).length).toBe(N)

    console.log(`[stress3] 100 并发(全超时) size=3: 耗时 ${elapsed}ms, peak=${peakConcurrency}, workers=${handles.length}`)
    expect(elapsed).toBeLessThan(5000)

    // 池仍可用：后续命令能正常执行（worker 已恢复）
    currentDelayFn = () => 5
    const after = await pool.execute('after-recovery', 'JSON', { timeout: 5000 })
    expect(after.trim()).toBe('"after-recovery-result"')
  }, 20000)
})
