import { describe, it, expect, beforeAll, vi } from 'vitest'
import { mkdtempSync, existsSync, readFileSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'
import {
  __setFileLoggingForTesting,
  __setLogDirForTesting,
  formatLine,
  log,
  logDir,
  normalizeLevel,
  readLogTail,
  writeLog,
} from './logger'

describe('logger 纯函数', () => {
  it('formatLine 含级别/时间戳/scope/文本，尾部空白剥离', () => {
    const line = formatLine('warn', '2026-06-01T00:00:00.000Z', 'smb', '  消息  ')
    expect(line).toBe('[2026-06-01T00:00:00.000Z] [WARN] [smb]   消息')
  })

  it('formatLine 超长截断至 8000', () => {
    const line = formatLine('info', 'T', 's', 'x'.repeat(10000))
    expect(line.length).toBeLessThanOrEqual(8000 + '[T] [INFO] [s] '.length)
  })

  it('normalizeLevel 白名单与回退', () => {
    expect(normalizeLevel('error')).toBe('error')
    expect(normalizeLevel('debug')).toBe('debug')
    expect(normalizeLevel('rm -rf')).toBe('info')
    expect(normalizeLevel(42)).toBe('info')
    expect(normalizeLevel(undefined)).toBe('info')
  })
})

describe('logger 文件写入', () => {
  beforeAll(() => {
    __setLogDirForTesting(mkdtempSync(join(tmpdir(), 'wslog-test-')))
    // vitest 环境默认关闭文件落盘（防污染真实日志），本用例显式开启并指向临时目录
    __setFileLoggingForTesting(true)
    // 静默 console 镜像（轮转用例会写 2MB+ 日志，避免刷屏测试输出）
    vi.spyOn(console, 'log').mockImplementation(() => {})
  })

  it('writeLog 落盘并可 tail 读回', () => {
    writeLog('info', 'test', 'hello-一条中文日志')
    const content = readFileSync(join(logDir(), 'app.log'), 'utf8')
    expect(content).toContain('[INFO] [test] hello-一条中文日志')
    expect(content).toMatch(/^\[\d{4}-\d{2}-\d{2}T/is)
  })

  it('readLogTail 行数限制', () => {
    for (let i = 0; i < 10; i++) writeLog('debug', 'tail', `line-${i}`)
    const tail = readLogTail(3)
    expect(tail.split('\n')).toHaveLength(3)
    expect(tail).toContain('line-9')
    expect(tail).not.toContain('line-5\n')
  })

  it('log.info 多参数与对象渲染（util.format）', () => {
    log.info('fmt', 'name:', 'a', { n: 1 })
    const content = readFileSync(join(logDir(), 'app.log'), 'utf8')
    expect(content).toContain('name: a { n: 1 }')
  })

  it('log.error 接收 Error 输出 stack', () => {
    const err = new Error('boom-error-case')
    log.error('fmt', err)
    const content = readFileSync(join(logDir(), 'app.log'), 'utf8')
    expect(content).toContain('boom-error-case')
  })

  it('超过 2MB 触发轮转（app.log.1 生成且最新内容在主文件）', () => {
    const big = 'R'.repeat(8000)
    for (let i = 0; i < 280; i++) writeLog('info', 'rotate', big) // ~2.24MB
    expect(existsSync(join(logDir(), 'app.log.1'))).toBe(true)
    writeLog('info', 'rotate', 'after-rotate-marker')
    expect(readLogTail(5)).toContain('after-rotate-marker')
  })
})
