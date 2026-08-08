import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { EventEmitter, PassThrough, Writable } from 'stream'
import { PowerShellPool, type WorkerHandle, type WorkerFactory } from './powershellPool'

// 固定 token，使标记可预测
const TOKEN = 'TEST'
const OK = `___PS_OK_${TOKEN}___`
const ERR = `___PS_ERR_${TOKEN}___`
const ERR_END = `___PS_ERR_END_${TOKEN}___`

// 伪造 worker 句柄：捕获 stdin 写入，stdout 由测试通过 respond() 推送，EventEmitter 模拟 exit/error
class FakeWorkerHandle extends EventEmitter implements WorkerHandle {
  written: string[] = []
  stdout = new PassThrough()
  stderr = new PassThrough()
  killed = false
  private stdinStream: Writable
  constructor() {
    super()
    const self = this
    this.stdinStream = new Writable({
      write(chunk, _enc, cb) {
        self.written.push(chunk.toString())
        cb()
      }
    })
  }
  get stdin(): Writable {
    return this.stdinStream
  }
  // 测试推送 stdout 响应
  respond(text: string): void {
    this.stdout.push(text)
  }
  // 模拟进程退出
  simulateExit(code: number | null): void {
    this.emit('exit', code, null)
  }
  kill(): boolean {
    this.killed = true
    // kill 会触发真实进程 exit；模拟之（onExit 因 alive=false 早退）
    this.emit('exit', 1, null)
    return true
  }
}

// 解码载荷：Base64(UTF16LE) → 原始字符串，拆分 mode\u0000command
function decodePayload(payload: string): { mode: string; command: string } {
  const decoded = Buffer.from(payload, 'base64').toString('utf16le')
  const sep = decoded.indexOf('\u0000')
  return { mode: decoded.slice(0, sep), command: decoded.slice(sep + 1) }
}

function makePool(size: number, handles: FakeWorkerHandle[]): PowerShellPool {
  const factory: WorkerFactory = () => {
    const h = new FakeWorkerHandle()
    handles.push(h)
    return h
  }
  return new PowerShellPool({ size, factory, token: TOKEN })
}

describe('PowerShellPool', () => {
  let handles: FakeWorkerHandle[]
  let pool: PowerShellPool

  beforeEach(() => {
    handles = []
    pool = makePool(3, handles)
  })

  afterEach(() => {
    pool.shutdown()
  })

  it('单命令 JSON 模式：返回标记前的原始 stdout', async () => {
    const p = pool.execute('Get-SmbShare', 'JSON', { timeout: 5000 })
    // execute 的 dispatch 同步完成 → worker 已派发
    expect(handles).toHaveLength(1)
    const h = handles[0]
    // 验证载荷编码正确（mode + \u0000 + command）
    const decoded = decodePayload(h.written[0].replace(/\n$/, ''))
    expect(decoded.mode).toBe('JSON')
    expect(decoded.command).toBe('Get-SmbShare')

    h.respond('{"name":"share1"}\n' + OK)
    const result = await p
    expect(result.trim()).toBe('{"name":"share1"}')
  })

  it('多行 JSON 输出：标记解析正确', async () => {
    const p = pool.execute('Get-SmbSession', 'JSON', { timeout: 5000 })
    const h = handles[0]
    const multiLineJson = '[\n  {"Client":"a"},\n  {"Client":"b"}\n]'
    h.respond(multiLineJson + '\n' + OK)
    const result = await p
    expect(JSON.parse(result)).toEqual([{ Client: 'a' }, { Client: 'b' }])
  })

  it('VOID 模式：空输出，返回空串', async () => {
    const p = pool.execute('Remove-SmbShare -Name x', 'VOID', { timeout: 5000 })
    const h = handles[0]
    const decoded = decodePayload(h.written[0].replace(/\n$/, ''))
    expect(decoded.mode).toBe('VOID')
    // VOID 服务端不输出 JSON，仅写 OK 标记行
    h.respond(OK + '\n')
    const result = await p
    expect(result).toBe('')
  })

  it('多命令 FIFO 排队：size=1 时串行执行', async () => {
    pool.shutdown()
    pool = makePool(1, handles)

    const p1 = pool.execute('c1', 'JSON', { timeout: 5000 })
    const p2 = pool.execute('c2', 'JSON', { timeout: 5000 })
    const p3 = pool.execute('c3', 'JSON', { timeout: 5000 })

    // size=1 → 仅 1 个 worker，c2/c3 排队
    expect(handles).toHaveLength(1)
    const h = handles[0]

    h.respond('"r1"\n' + OK)
    expect((await p1).trim()).toBe('"r1"')

    // c1 完成后 dispatch 派发 c2 到同一 worker
    h.respond('"r2"\n' + OK)
    expect((await p2).trim()).toBe('"r2"')

    h.respond('"r3"\n' + OK)
    expect((await p3).trim()).toBe('"r3"')
  })

  it('并发命令跨多 worker 并行派发：2 命令均派发后才响应', async () => {
    const p1 = pool.execute('c1', 'JSON', { timeout: 5000 })
    const p2 = pool.execute('c2', 'JSON', { timeout: 5000 })

    // 关键断言：2 个 worker 均已派发（并行），在任一响应前
    expect(handles).toHaveLength(2)
    expect(handles[0].written).toHaveLength(1)
    expect(handles[1].written).toHaveLength(1)

    // 分别响应
    handles[0].respond('"a"\n' + OK)
    handles[1].respond('"b"\n' + OK)

    const [r1, r2] = await Promise.all([p1, p2])
    expect(r1.trim()).toBe('"a"')
    expect(r2.trim()).toBe('"b"')
  })

  it('ERR 路径：reject 带 stderr 的错误对象', async () => {
    const p = pool.execute('Throw-Error', 'JSON', { timeout: 5000 })
    const h = handles[0]
    h.respond(ERR + '\nAccess is denied\n' + ERR_END)
    await expect(p).rejects.toMatchObject({ message: 'Command failed', stderr: 'Access is denied' })
  })

  it('ERR 多行消息：提取 ERR 与 ERR_END 之间的文本', async () => {
    const p = pool.execute('Bad', 'JSON', { timeout: 5000 })
    const h = handles[0]
    h.respond(ERR + '\nLine1\nLine2\n' + ERR_END)
    await expect(p).rejects.toMatchObject({ stderr: 'Line1\nLine2' })
  })

  it('ERR 标记到达但 ERR_END 未到：继续等待，不提前 reject', async () => {
    const p = pool.execute('Bad', 'JSON', { timeout: 5000 })
    const h = handles[0]
    h.respond(ERR + '\npartial msg')
    // 仅 ERR 无 ERR_END → 仍在等待，promise 未决
    let settled = false
    await Promise.race([
      p.then(
        () => (settled = true),
        () => (settled = true)
      ),
      new Promise((r) => setTimeout(r, 30))
    ])
    expect(settled).toBe(false)
    // 补齐 ERR_END → reject，消息为 ERR 与 ERR_END 之间的全部文本
    h.respond('\nmore\n' + ERR_END)
    await expect(p).rejects.toMatchObject({ stderr: 'partial msg\nmore' })
  })

  it('超时：reject 超时错误 + kill 中毒 worker + 后续命令补 spawn 成功', async () => {
    const p = pool.execute('Slow', 'JSON', { timeout: 50 })
    const h = handles[0]
    // 不响应，等超时
    await expect(p).rejects.toThrow('Command timed out')
    expect(h.killed).toBe(true)

    // 后续命令应补 spawn 新 worker 并成功
    const p2 = pool.execute('After', 'JSON', { timeout: 5000 })
    expect(handles).toHaveLength(2)
    handles[1].respond('"ok"\n' + OK)
    expect((await p2).trim()).toBe('"ok"')
  })

  it('Worker 崩溃（busy 时 exit）：reject 在飞命令（可重试）+ 后续补 spawn', async () => {
    const p = pool.execute('Crash', 'JSON', { timeout: 5000 })
    const h = handles[0]
    // 模拟进程意外退出
    h.simulateExit(1)
    const err = await p.catch((e: Error) => e)
    expect(err.message).toBe('PowerShell 进程意外退出，请重试')
    // 错误消息含"重试"→ isRetryable 匹配 → runPowerShell 会换新 worker 重试
    expect(/重试|timed out|ECONNRESET/i.test(err.message)).toBe(true)

    // 后续命令补 spawn 新 worker
    const p2 = pool.execute('After', 'JSON', { timeout: 5000 })
    expect(handles).toHaveLength(2)
    handles[1].respond('"ok"\n' + OK)
    expect((await p2).trim()).toBe('"ok"')
  })

  it('Worker 崩溃（idle 时 exit）：标记 dead，下次命令补 spawn', async () => {
    // 先执行并完成一条命令，使 worker 进入 idle
    const p1 = pool.execute('First', 'JSON', { timeout: 5000 })
    handles[0].respond('"r1"\n' + OK)
    await p1
    expect(handles).toHaveLength(1)

    // 模拟 idle worker 崩溃（无在飞命令）
    handles[0].simulateExit(1)
    // 不应 reject 任何命令（无在飞）

    // 下次命令补 spawn 新 worker
    const p2 = pool.execute('Second', 'JSON', { timeout: 5000 })
    expect(handles).toHaveLength(2)
    handles[1].respond('"r2"\n' + OK)
    expect((await p2).trim()).toBe('"r2"')
  })

  it('崩溃后重试（模拟 runPowerShell 重试）：新 worker 执行成功', async () => {
    // 第一次：崩溃
    const p1 = pool.execute('Cmd', 'JSON', { timeout: 5000 })
    handles[0].simulateExit(1)
    await expect(p1).rejects.toThrow('PowerShell 进程意外退出')
    // 第二次（模拟 runPowerShell 换新 worker 重试）：成功
    const p2 = pool.execute('Cmd', 'JSON', { timeout: 5000 })
    expect(handles).toHaveLength(2)
    handles[1].respond('"recovered"\n' + OK)
    expect((await p2).trim()).toBe('"recovered"')
  })

  it('shutdown：拒绝在飞与排队命令 + kill 全部 worker', async () => {
    // 用 size=2：c1/c2 派发到 h0/h1（在飞），c3 排队
    pool.shutdown()
    pool = makePool(2, handles)

    const p1 = pool.execute('c1', 'JSON', { timeout: 5000 })
    const p2 = pool.execute('c2', 'JSON', { timeout: 5000 })
    const p3 = pool.execute('c3', 'JSON', { timeout: 5000 })
    expect(handles).toHaveLength(2)

    pool.shutdown()

    // 在飞（c1/c2）与排队（c3）均被拒绝
    await expect(p1).rejects.toThrow('PowerShell 进程池已关闭')
    await expect(p2).rejects.toThrow('PowerShell 进程池已关闭')
    await expect(p3).rejects.toThrow('PowerShell 进程池已关闭')
    expect(handles[0].killed).toBe(true)
    expect(handles[1].killed).toBe(true)

    // shutdown 后 execute 立即拒绝
    await expect(pool.execute('after', 'JSON', { timeout: 5000 })).rejects.toThrow('PowerShell 进程池已关闭')
  })

  it('残余字节处理：OK 标记后的杂散字节不污染下一条命令（assign 清空 buffer）', async () => {
    // 模拟一次响应中 OK 后紧跟杂散字节（边界场景）
    const p1 = pool.execute('c1', 'JSON', { timeout: 5000 })
    const h = handles[0]
    h.respond('"r1"\n' + OK + '\n"stray"')
    expect((await p1).trim()).toBe('"r1"')
    // stray 残留在 buffer；下一条命令 assign 时 buffer 被清空，stray 不影响解析
    const p2 = pool.execute('c2', 'JSON', { timeout: 5000 })
    h.respond('"r2"\n' + OK)
    expect((await p2).trim()).toBe('"r2"')
  })
})
