import { execFile } from 'child_process'
import { promisify } from 'util'

const execFileAsync = promisify(execFile)

const DEFAULT_TIMEOUT = 15000
const DEFAULT_RETRIES = 2

// 强制 PowerShell 以 UTF-8 输出，避免 Node 按 UTF-8 解码 GBK 字节导致中文乱码
// （中文 Windows 默认控制台代码页 936/GBK，ConvertTo-Json 输出的中文按 GBK 编码）
const UTF8_PREFIX =
  '[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; '

export interface PsOptions {
  timeout?: number
  retries?: number
}

// 执行 PowerShell 命令并返回 JSON 解析结果（Get-* 用），带超时与自动重试
export async function runPowerShell<T>(command: string, opts: PsOptions = {}): Promise<T> {
  const timeout = opts.timeout ?? DEFAULT_TIMEOUT
  const retries = opts.retries ?? DEFAULT_RETRIES
  let lastError: Error | null = null

  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      const { stdout } = await execFileAsync(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', `${UTF8_PREFIX}${command} | ConvertTo-Json -Depth 5 -Compress`],
        { timeout, maxBuffer: 10 * 1024 * 1024, windowsHide: true }
      )
      return parseJson<T>(stdout)
    } catch (err) {
      lastError = err as Error
      if (!isRetryable((err as Error).message) || attempt === retries) break
      await sleep(300 * (attempt + 1))
    }
  }
  throw formatPsError(lastError)
}

// 执行不需要返回值的命令（Set-/New-/Remove- 用）
export async function runPowerShellVoid(command: string, opts: PsOptions = {}): Promise<void> {
  const timeout = opts.timeout ?? DEFAULT_TIMEOUT
  const retries = opts.retries ?? DEFAULT_RETRIES
  let lastError: Error | null = null
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      await execFileAsync(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', `${UTF8_PREFIX}${command}`],
        { timeout, maxBuffer: 10 * 1024 * 1024, windowsHide: true }
      )
      return
    } catch (err) {
      lastError = err as Error
      if (!isRetryable((err as Error).message) || attempt === retries) break
      await sleep(300 * (attempt + 1))
    }
  }
  throw formatPsError(lastError)
}

function parseJson<T>(stdout: string): T {
  const trimmed = (stdout || '').trim()
  if (!trimmed) return [] as unknown as T
  let result: unknown
  try {
    result = JSON.parse(trimmed)
  } catch {
    throw new Error(`PowerShell 输出解析失败：${trimmed.slice(0, 200)}`)
  }
  if (result === null) return [] as unknown as T
  return result as T
}

function isRetryable(msg: string): boolean {
  return /timed out|ETIMEDOUT|ECONNRESET|temporarily|重试/i.test(msg)
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms))
}

function formatPsError(err: Error | null): Error {
  if (!err) return new Error('未知错误')
  const msg = err.message
  if (/timed out|ETIMEDOUT/i.test(msg)) return new Error('命令执行超时，请检查系统响应或稍后重试')
  if (/access is denied|拒绝访问/i.test(msg)) return new Error('权限不足，请以管理员身份运行')
  return new Error(`命令执行失败：${msg.slice(0, 300)}`)
}

// PS 单引号转义
export function psQuote(value: string): string {
  return `'${String(value).replace(/'/g, "''")}'`
}

// 共享名/账号名白名单校验
export function validateName(name: string, maxLen = 80): boolean {
  if (!name || name.length > maxLen) return false
  return /^[A-Za-z0-9._\-\u4e00-\u9fa5 $]{1,80}$/.test(name)
}

// Windows 路径校验
export function validatePath(p: string): boolean {
  return /^[A-Za-z]:[\\/]([^\u0000-\u001f<>:"|?*][^<>:"|?*]*)*$/.test(p)
}
