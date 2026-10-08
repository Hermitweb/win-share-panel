import { format } from 'util'
import {
  appendFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
} from 'fs'
import { homedir, platform } from 'os'
import { join } from 'path'

// === 统一应用日志系统 ===
// 目标：排障证据可持久化、可复制（打包后 console 不可见、toast 一闪而过的痛点）。
// - 文件：%APPDATA%/WinSharePanel/logs/app.log（2MB 轮转 ×3）
// - 签名：log.info(scope, ...args)——与 console 习惯一致（util.format 渲染，对象/Error 完整保留）
// - console 镜像：按级别映射 console.*，dev 终端与 dev.log 可见性不变
// - 渲染层错误经 IPC app:logWrite 汇入同一文件（scope=renderer）
// 注意：本文件是日志系统底座，镜像输出必须直接使用 console，不得引入 log.*（避免自递归）。

export type LogLevel = 'debug' | 'info' | 'warn' | 'error'

const LEVELS: LogLevel[] = ['debug', 'info', 'warn', 'error']
const MAX_SIZE = 2 * 1024 * 1024
const MAX_FILES = 3
const MAX_LINE_LEN = 8000

let cachedDir = ''
// 文件落盘开关：vitest 运行默认关闭（防止单测 mock 日志污染真实日志），
// logger 自身用例通过 __setFileLoggingForTesting(true) 显式开启并配合目录覆盖
let fileEnabled = !('VITEST' in process.env)

export function logDir(): string {
  if (cachedDir) return cachedDir
  const base =
    platform() === 'win32' ? process.env.APPDATA || homedir() : join(homedir(), '.winshare-panel')
  cachedDir = join(base, 'WinSharePanel', 'logs')
  return cachedDir
}

// 测试专用：覆盖日志目录（与 __resetPoolForTesting 同惯例），null 恢复默认推导
export function __setLogDirForTesting(dir: string | null): void {
  cachedDir = dir ?? ''
}

// 测试专用：开/关文件落盘（仅应在 *.test.ts 中调用）
export function __setFileLoggingForTesting(enabled: boolean): void {
  fileEnabled = enabled
}

function logFile(): string {
  return join(logDir(), 'app.log')
}

/** 纯函数：格式化单行日志（供单测） */
export function formatLine(level: LogLevel, ts: string, scope: string, text: string): string {
  const clean = text.replace(/\s+$/, '').slice(0, MAX_LINE_LEN)
  return `[${ts}] [${level.toUpperCase()}] [${scope}] ${clean}`
}

/** 校验并规整外部来源的 level，非法回退 info（与 F-1 边界守卫同风格） */
export function normalizeLevel(v: unknown): LogLevel {
  return typeof v === 'string' && (LEVELS as string[]).includes(v) ? (v as LogLevel) : 'info'
}

function rotate(file: string): void {
  // 触顶删除最旧归档，然后 .2→.3、.1→.2、app.log→.1
  try {
    if (existsSync(`${file}.${MAX_FILES}`)) rmSync(`${file}.${MAX_FILES}`)
    for (let i = MAX_FILES - 1; i >= 1; i--) {
      if (existsSync(`${file}.${i}`)) renameSync(`${file}.${i}`, `${file}.${i + 1}`)
    }
    if (existsSync(file)) renameSync(file, `${file}.1`)
  } catch {
    /* 轮转失败不阻塞写入 */
  }
}

/** 写一条日志：console 镜像 + 文件全量；任何 IO 失败静默降级为仅 console */
export function writeLog(level: LogLevel, scope: string, text: string): void {
  const line = formatLine(level, new Date().toISOString(), scope, text)
  switch (level) {
    case 'error':
      console.error(line)
      break
    case 'warn':
      console.warn(line)
      break
    default:
      console.log(line)
  }
  try {
    if (!fileEnabled) return
    const dir = logDir()
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
    const file = logFile()
    if (existsSync(file) && statSync(file).size > MAX_SIZE) rotate(file)
    appendFileSync(file, line + '\n', 'utf8')
  } catch {
    // 日志失败绝不影响主流程
  }
}

/** 读取尾部 N 行（UI"应用日志"页与排障复制用）；文件不存在返回空串 */
export function readLogTail(lines = 300): string {
  try {
    const file = logFile()
    if (!existsSync(file)) return ''
    let content = readFileSync(file, 'utf8')
    // 大文件仅截尾部 1MB 再取行，避免全量进内存
    const cap = 1024 * 1024
    if (content.length > cap) {
      content = content.slice(-cap)
      content = content.slice(content.indexOf('\n') + 1)
    }
    const arr = content.split(/\r?\n/).filter((l) => l.length > 0)
    return arr.slice(Math.max(0, arr.length - Math.max(1, lines))).join('\n')
  } catch {
    return ''
  }
}

function variadic(level: LogLevel) {
  return (scope: string, ...args: unknown[]): void => {
    writeLog(
      level,
      String(scope),
      args.map((a) => (a instanceof Error ? (a.stack ?? a.message) : format(a))).join(' '),
    )
  }
}

// 便捷 API：log.info('smb', 'msg', {obj}) —— 与 console 习惯一致
export const log = {
  debug: variadic('debug'),
  info: variadic('info'),
  warn: variadic('warn'),
  error: variadic('error'),
}
