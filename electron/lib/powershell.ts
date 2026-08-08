import { execFile } from 'child_process'
import { promisify } from 'util'
import { getPool, isPoolEnabled } from './powershellPool'

const execFileAsync = promisify(execFile)

const DEFAULT_TIMEOUT = 15000
const DEFAULT_RETRIES = 2

// 强制 PowerShell 以 UTF-8 输出，避免 Node 按 UTF-8 解码 GBK 字节导致中文乱码
// （中文 Windows 默认控制台代码页 936/GBK，ConvertTo-Json 输出的中文按 GBK 编码）
// $ProgressPreference=SilentlyContinue 抑制模块加载进度输出，避免 -NonInteractive 模式下
// 序列化为 CLIXML 污染 stderr 导致 formatPsError 误报
const UTF8_PREFIX =
  '[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference=\'SilentlyContinue\'; '

export interface PsOptions {
  timeout?: number
  retries?: number
}

// 将命令编码为 -EncodedCommand 所需的 Base64(UTF-16LE)。
// Windows PowerShell 5.1 的 -Command 参数按系统代码页（中文系统为 GBK）解析命令字符串，
// 导致内联的中文（如站点名"测试"）被破坏成乱码（"娴嬭瘯"），站点操作全部失败。
// -EncodedCommand 始终按 UTF-16LE 解码，彻底规避代码页问题。
function encodeCommand(command: string): string {
  return Buffer.from(command, 'utf16le').toString('base64')
}

// 回退路径（WINSHARE_PSPOOL=0 或池禁用时）：单次 execFile 拉起 powershell.exe
// withJson=true 拼接 ConvertTo-Json（runPowerShell 用），false 则不拼接（runPowerShellVoid 用）
async function execOnce(command: string, withJson: boolean, timeout: number): Promise<string> {
  const fullCmd = withJson
    ? `${UTF8_PREFIX}${command} | ConvertTo-Json -Depth 5 -Compress`
    : `${UTF8_PREFIX}${command}`
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encodeCommand(fullCmd)],
    { timeout, maxBuffer: 10 * 1024 * 1024, windowsHide: true }
  )
  return stdout
}

// 执行 PowerShell 命令并返回 JSON 解析结果（Get-* 用），带超时与自动重试
// 默认走常驻进程池（复用 worker，~30-80ms/条）；WINSHARE_PSPOOL=0 时回退 execOnce
export async function runPowerShell<T>(command: string, opts: PsOptions = {}): Promise<T> {
  const timeout = opts.timeout ?? DEFAULT_TIMEOUT
  const retries = opts.retries ?? DEFAULT_RETRIES
  let lastError: Error | null = null

  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      const stdout = isPoolEnabled()
        ? await getPool().execute(command, 'JSON', { timeout })
        : await execOnce(command, true, timeout)
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
      if (isPoolEnabled()) {
        await getPool().execute(command, 'VOID', { timeout })
      } else {
        await execOnce(command, false, timeout)
      }
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
  // execFile 错误对象含 stderr 字段（PowerShell 的错误输出）；
  // -EncodedCommand/-NonInteractive 模式下 stderr 可能是 CLIXML 序列化格式，需剥离提取可读文本
  const rawStderr = (err as Error & { stderr?: string }).stderr || ''
  const rawMsg = err.message || ''
  const combined = rawStderr || rawMsg
  if (/timed out|ETIMEDOUT/i.test(rawMsg)) return new Error('命令执行超时，请检查系统响应或稍后重试')
  const readable = stripClixml(combined)
  if (/access is denied|拒绝访问/i.test(readable)) return new Error('权限不足，请以管理员身份运行')
  return new Error(`命令执行失败：${readable.slice(0, 300)}`)
}

// 剥离 PowerShell 的 CLIXML 序列化格式，提取可读错误文本。
// CLIXML 形如：#< CLIXML\n<Objs...><S S="Error">Remove-Item : ...</S>...
// 优先提取 <S S="Error">...</S> 标签内的文本；若无 Error 标签（仅 progress 等），
// 剥离所有 XML 标签返回剩余文本，避免把原始 CLIXML 当错误信息展示。
function stripClixml(text: string): string {
  if (!text.includes('CLIXML')) return text
  // 优先提取 Error/Warning 流文本
  const matches = text.match(/<S S="(Error|Warning)"[^>]*>([\s\S]*?)<\/S>/g)
  if (matches && matches.length > 0) {
    return matches
      .map((m) => m.replace(/<S S="[^"]*"[^>]*>/, '').replace(/<\/S>/, ''))
      .map((s) => s.replace(/_x([0-9A-Fa-f]{4})_/g, (_, hex) => String.fromCharCode(parseInt(hex, 16))))
      .join(' ')
      .trim()
  }
  // 无 Error/Warning 标签：剥离所有 CLIXML XML 标签，提取剩余纯文本
  const stripped = text
    .replace(/^#<\s*CLIXML\s*/i, '')
    .replace(/<[^>]+>/g, ' ')
    .replace(/_x([0-9A-Fa-f]{4})_/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))
    .replace(/\s+/g, ' ')
    .trim()
  // 如果剥离后只剩进度信息或为空，返回通用提示
  if (!stripped || /准备.*模块|preparing.*module/i.test(stripped)) {
    return 'PowerShell 命令执行失败（无详细错误信息）'
  }
  return stripped
}

// PS 单引号转义（将值用单引号包裹，内部 ' 转义为 ''）
export function psQuote(value: string): string {
  return `'${String(value).replace(/'/g, "''")}'`
}

// PS 单引号字符串内部转义（不包裹外层引号）
// 用于将值嵌入更大的单引号字符串中，如 IIS 路径 'IIS:\Sites\{name}'
// 不能用 psQuote，因为 psQuote 会额外加一层引号导致 "IIS:\Sites\'name'" 无效路径
export function psEscapeSingle(value: string): string {
  return String(value).replace(/'/g, "''")
}

// 共享名/账号名白名单校验
// 注意：不允许 $ —— Windows 本地账号名不会在中间含 $（仅机器账号以 $ 结尾），
// 而 $ 在 PowerShell 双引号字符串中会触发变量插值，存在注入风险，故从白名单移除。
export function validateName(name: string, maxLen = 80): boolean {
  if (!name || name.length > maxLen) return false
  return /^[A-Za-z0-9._\-\u4e00-\u9fa5 ]{1,80}$/.test(name)
}

// Windows 路径校验
export function validatePath(p: string): boolean {
  return /^[A-Za-z]:[\\/]([^\u0000-\u001f<>:"|?*][^<>:"|?*]*)*$/.test(p)
}

// === 安全拼接辅助：IPC 边界无运行时类型保证，TS 类型仅编译期。
// 渲染进程可能传入与声明类型不符的值，以下函数在拼入 PowerShell 命令前做运行时校验，
// 非法值返回 null（调用方跳过该字段），杜绝布尔/数值/枚举字段被用作注入载体。===

// 布尔字段：仅接受真正的 boolean，输出 $true/$false
export function psBool(v: unknown): string | null {
  return typeof v === 'boolean' ? `$${v}` : null
}

// 数值字段：仅接受有限数字，输出其字符串形式
export function psNumber(v: unknown): string | null {
  return typeof v === 'number' && Number.isFinite(v) ? String(v) : null
}

// 枚举字段：仅接受白名单内的字符串，输出裸值（调用方需确保 cmdlet 接受裸枚举）
export function psEnum(v: unknown, allowed: ReadonlySet<string>): string | null {
  return typeof v === 'string' && allowed.has(v) ? v : null
}
