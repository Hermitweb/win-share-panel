import { runPowerShell, runPowerShellVoid, psBool, psNumber, psEnum, psQuote } from '../lib/powershell'
import { Errors } from '../lib/errors'
import { readFileSync, writeFileSync, existsSync, mkdirSync, readdirSync, unlinkSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'
import type { SmbServerConfig, ServiceStatus, SmbSnapshot, SmbSnapshotMeta } from '../types'

const MAX_SNAPSHOTS = 20

function dataDir(): string {
  return join(process.env.APPDATA || homedir(), 'WinSharePanel')
}
function snapshotDir(): string {
  const dir = join(dataDir(), 'smb-snapshots')
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
  return dir
}

// 防止 rollback→setConfig 递归快照
let skipNextSnapshot = false

// setConfig 串行化锁：防止并发 setConfig 互相覆盖 skipNextSnapshot 标志
// （虽 JS 单线程，但 async 交错仍可能导致 rollback→setConfig 与用户连点保存竞态）
let setConfigChain: Promise<void> = Promise.resolve()

// ISO 时间戳作文件名需替换 : 为 -（Windows 不允许 :）
function tsToFilename(ts: string): string {
  return ts.replace(/[:.]/g, '-')
}

// SMB 服务器默认配置：基于 Windows Server 2022 安全推荐默认值
export function defaultConfig(): SmbServerConfig {
  return {
    enableSMB1Protocol: false, // 出于安全考虑默认关闭 SMB1
    enableSMB2Protocol: true,
    enableSMB3Protocol: true,
    enableGuestUserAccess: false,
    enableInsecureGuestLogons: false,
    auditSmb1Access: true,
    requireSecuritySignature: true, // 默认要求签名
    enableMultiChannel: true,
    announceServer: false,
    unauthenticatedUsersTimeLimit: 0,
    // 扩展默认值
    enableOplocks: true,
    enableOplockDirectoryCache: true,
    enableStrictNameChecking: true,
    enableLeasing: true,
    enableSMBQUIC: false,
    enableChannelChange: true,
    enableSMBDirectoryCache: true,
    sessionTimeoutSeconds: 60,
    maxSessionPerConnection: 1024,
    maxMpxCount: 1000,
    maxWorkItems: 1000,
    maxThreadsPerQueue: 100,
    multipleSessionsPerConnection: false,
    requestCompression: 'Off',
    silentAU: false
  }
}

export async function getConfig(): Promise<SmbServerConfig> {
  const raw = await runPowerShell<any>('Get-SmbServerConfiguration')
  return {
    enableSMB1Protocol: !!raw.EnableSMB1Protocol,
    enableSMB2Protocol: !!raw.EnableSMB2Protocol,
    enableSMB3Protocol: !!raw.EnableSMB3Protocol,
    enableGuestUserAccess: !!raw.EnableGuestUserAccess,
    enableInsecureGuestLogons: !!raw.EnableInsecureGuestLogons,
    auditSmb1Access: !!raw.AuditSmb1Access,
    requireSecuritySignature: !!raw.RequireSecuritySignature,
    enableMultiChannel: !!raw.EnableMultiChannel,
    announceServer: !!raw.AnnounceServer,
    unauthenticatedUsersTimeLimit: raw.UnauthenticatedUsersTimeLimit || 0,
    // 扩展字段（某些旧版 Windows 可能无这些字段，降级为默认值）
    enableOplocks: raw.EnableOplocks !== undefined ? !!raw.EnableOplocks : true,
    enableOplockDirectoryCache: raw.EnableOplockDirectoryCache !== undefined ? !!raw.EnableOplockDirectoryCache : true,
    enableStrictNameChecking: raw.EnableStrictNameChecking !== undefined ? !!raw.EnableStrictNameChecking : true,
    enableLeasing: raw.EnableLeasing !== undefined ? !!raw.EnableLeasing : true,
    enableSMBQUIC: raw.EnableSMBQUIC !== undefined ? !!raw.EnableSMBQUIC : false,
    enableChannelChange: raw.EnableChannelChange !== undefined ? !!raw.EnableChannelChange : true,
    enableSMBDirectoryCache: raw.EnableSMBDirectoryCache !== undefined ? !!raw.EnableSMBDirectoryCache : true,
    sessionTimeoutSeconds: raw.SessionTimeoutSeconds || 60,
    maxSessionPerConnection: raw.MaxSessionPerConnection || 1024,
    maxMpxCount: raw.MaxMpxCount || 1000,
    maxWorkItems: raw.MaxWorkItems || 1000,
    maxThreadsPerQueue: raw.MaxThreadsPerQueue || 100,
    multipleSessionsPerConnection: !!raw.MultipleSessionsPerConnection,
    requestCompression: raw.RequestCompression || 'Off',
    silentAU: !!raw.SilentAU
  }
}

// 字段名映射：TS 字段 -> PowerShell 参数名
const FIELD_MAP: Record<string, string> = {
  enableSMB1Protocol: 'EnableSMB1Protocol',
  enableSMB2Protocol: 'EnableSMB2Protocol',
  enableSMB3Protocol: 'EnableSMB3Protocol',
  enableGuestUserAccess: 'EnableGuestUserAccess',
  enableInsecureGuestLogons: 'EnableInsecureGuestLogons',
  auditSmb1Access: 'AuditSmb1Access',
  requireSecuritySignature: 'RequireSecuritySignature',
  enableMultiChannel: 'EnableMultiChannel',
  announceServer: 'AnnounceServer',
  enableOplocks: 'EnableOplocks',
  enableOplockDirectoryCache: 'EnableOplockDirectoryCache',
  enableStrictNameChecking: 'EnableStrictNameChecking',
  enableLeasing: 'EnableLeasing',
  enableSMBQUIC: 'EnableSMBQUIC',
  enableChannelChange: 'EnableChannelChange',
  enableSMBDirectoryCache: 'EnableSMBDirectoryCache',
  multipleSessionsPerConnection: 'MultipleSessionsPerConnection',
  silentAU: 'SilentAU'
}

// RequestCompression 合法枚举值（Set-SmbServerConfiguration -RequestCompression）
const COMPRESSION_OPTIONS = new Set(['Off', 'OptimizeForSpeed', 'OptimizeForSize'])

function buildSetCmd(config: Partial<SmbServerConfig>): string {
  const parts = ['Set-SmbServerConfiguration']
  // 布尔/数值字段映射（运行时类型校验，防止渲染进程传入非法类型注入命令）
  for (const key of Object.keys(FIELD_MAP)) {
    const k = key as keyof SmbServerConfig
    if (config[k] === undefined) continue
    const v = config[k]
    const bv = psBool(v)
    if (bv) {
      parts.push(`-${FIELD_MAP[key]} ${bv}`)
      continue
    }
    const nv = psNumber(v)
    if (nv) parts.push(`-${FIELD_MAP[key]} ${nv}`)
  }
  // 数值字段
  if (config.unauthenticatedUsersTimeLimit !== undefined) {
    const nv = psNumber(config.unauthenticatedUsersTimeLimit)
    if (nv) parts.push(`-UnauthenticatedUsersTimeLimit ${nv}`)
  }
  if (config.sessionTimeoutSeconds !== undefined) {
    const nv = psNumber(config.sessionTimeoutSeconds)
    if (nv) parts.push(`-SessionTimeoutSeconds ${nv}`)
  }
  if (config.maxSessionPerConnection !== undefined) {
    const nv = psNumber(config.maxSessionPerConnection)
    if (nv) parts.push(`-MaxSessionPerConnection ${nv}`)
  }
  if (config.maxMpxCount !== undefined) {
    const nv = psNumber(config.maxMpxCount)
    if (nv) parts.push(`-MaxMpxCount ${nv}`)
  }
  if (config.maxWorkItems !== undefined) {
    const nv = psNumber(config.maxWorkItems)
    if (nv) parts.push(`-MaxWorkItems ${nv}`)
  }
  if (config.maxThreadsPerQueue !== undefined) {
    const nv = psNumber(config.maxThreadsPerQueue)
    if (nv) parts.push(`-MaxThreadsPerQueue ${nv}`)
  }
  // 枚举字段（白名单校验 + 引号包裹）
  if (config.requestCompression !== undefined) {
    const ev = psEnum(config.requestCompression, COMPRESSION_OPTIONS)
    if (ev) parts.push(`-RequestCompression ${psQuote(ev)}`)
  }
  parts.push('-Force')
  return parts.join(' ')
}

export async function setConfig(config: Partial<SmbServerConfig>): Promise<void> {
  // 串行化：将整次 setConfig（含快照+写入）排入链式队列，避免并发竞态
  const run = async (): Promise<void> => {
    // 写前快照当前完整配置（rollback 触发的 setConfig 跳过）
    if (!skipNextSnapshot) {
      try {
        const current = await getConfig()
        const ts = new Date().toISOString()
        const id = tsToFilename(ts)
        const snap: SmbSnapshot = { id, ts, config: current }
        writeFileSync(join(snapshotDir(), `${id}.json`), JSON.stringify(snap, null, 2), 'utf8')
        cleanupSnapshots()
      } catch {
        // 快照失败不阻塞写入
      }
    }
    skipNextSnapshot = false

    const cmd = buildSetCmd(config)
    if (cmd.split(' ').length <= 2) throw Errors.invalidParam('未提供任何配置项')
    await runPowerShellVoid(cmd)
  }
  // 排入串行队列：前一次 setConfig 完成（含 await）后才执行本次
  const next = setConfigChain.then(run, run)
  // 保持链不中断：即使本次失败，下一次仍可执行
  setConfigChain = next.catch(() => undefined)
  return next
}

// 恢复默认配置
export async function restoreDefault(): Promise<SmbServerConfig> {
  const def = defaultConfig()
  await setConfig(def)
  return def
}

export async function listSnapshots(): Promise<SmbSnapshotMeta[]> {
  try {
    const files = readdirSync(snapshotDir()).filter((f) => f.endsWith('.json'))
    return files
      .map((f) => ({ id: f.replace(/\.json$/, ''), ts: f.replace(/\.json$/, '') }))
      .sort((a, b) => b.ts.localeCompare(a.ts))
  } catch {
    return []
  }
}

export async function rollbackSnapshot(id: string): Promise<void> {
  // 安全校验：id 必须为 ISO 时间戳转换的文件名格式（仅字母数字与连字符），
  // 防止 ../ 路径穿越读取上级目录任意 .json 并执行其中 config
  if (!/^[A-Za-z0-9_-]+$/.test(id)) throw Errors.invalidParam('快照 ID 格式非法')
  const file = join(snapshotDir(), `${id}.json`)
  if (!existsSync(file)) throw Errors.invalidParam('快照不存在')
  let snap: SmbSnapshot
  try {
    snap = JSON.parse(readFileSync(file, 'utf8'))
  } catch {
    throw Errors.invalidParam('快照文件损坏，无法解析')
  }
  // 二次校验：确保 snap 是合法快照对象
  if (!snap || typeof snap !== 'object' || !snap.ts || !snap.config) {
    throw Errors.invalidParam('快照内容不合法')
  }
  // 防止 setConfig 再次存快照；finally 重置以保证后续正常写入仍会快照
  skipNextSnapshot = true
  try {
    await setConfig(snap.config)
  } finally {
    skipNextSnapshot = false
  }
}

function cleanupSnapshots(): void {
  try {
    const files = readdirSync(snapshotDir())
      .filter((f) => f.endsWith('.json'))
      .sort((a, b) => b.localeCompare(a)) // ts desc
    if (files.length > MAX_SNAPSHOTS) {
      files.slice(MAX_SNAPSHOTS).forEach((f) => {
        try {
          unlinkSync(join(snapshotDir(), f))
        } catch {
          // 忽略删除失败
        }
      })
    }
  } catch {
    // 忽略
  }
}

export async function getServiceStatus(): Promise<ServiceStatus> {
  // Select-Object 精简输出，避免序列化整个依赖服务树（原命令输出超 100KB）
  const raw = await runPowerShell<any>(
    'Get-Service LanmanServer | Select-Object Name, Status, StartType'
  )
  // ConvertTo-Json 将 ServiceControllerStatus 枚举序列化为数值：
  //   1=Stopped, 2=StartPending, 3=StopPending, 4=Running, 5=ContinuePending, 6=PausePending, 7=Paused
  // ServiceStartMode 枚举：0=Boot, 1=System, 2=Automatic, 3=Manual, 4=Disabled
  const statusNum = typeof raw.Status === 'number' ? raw.Status : Number(raw.Status)
  const statusStr = typeof raw.Status === 'string' ? raw.Status : ''
  let status: 'Running' | 'Stopped' | 'Unknown'
  if (statusStr === 'Running' || statusNum === 4) status = 'Running'
  else if (statusStr === 'Stopped' || statusNum === 1) status = 'Stopped'
  else if (statusStr === 'Paused' || statusNum === 7) status = 'Stopped' // Paused 视为非运行
  else status = 'Unknown'

  const startNum = typeof raw.StartType === 'number' ? raw.StartType : Number(raw.StartType)
  let startType = typeof raw.StartType === 'string' ? raw.StartType : ''
  if (!startType) {
    if (startNum === 2) startType = 'Automatic'
    else if (startNum === 3) startType = 'Manual'
    else if (startNum === 4) startType = 'Disabled'
    else if (startNum === 0) startType = 'Boot'
    else if (startNum === 1) startType = 'System'
  }

  return {
    name: raw.Name || 'LanmanServer',
    status,
    startType
  }
}

export async function restartService(): Promise<void> {
  await runPowerShellVoid('Restart-Service -Name LanmanServer -Force')
}

export async function startService(): Promise<void> {
  await runPowerShellVoid('Start-Service -Name LanmanServer')
}

export async function stopService(): Promise<void> {
  await runPowerShellVoid('Stop-Service -Name LanmanServer -Force')
}
