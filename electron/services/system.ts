import { log } from '../lib/logger'
import { userInfo, hostname } from 'os'
import { app } from 'electron'
import { runPowerShell, runPowerShellVoid, psQuote } from '../lib/powershell'
import { readAuditLog } from '../lib/audit'
import { Errors } from '../lib/errors'
import { getServiceStatus } from './smb'
import { adapterList, adapterSessions } from './protocol/registry'
import type { UserInfo, DashboardStats, Protocol, Share, OsInfo } from '../types'

// 协议白名单（与 types.ts 声明一致），IPC 边界运行时校验
const PROTOCOLS = new Set<Protocol>(['smb', 'nfs', 'ftp', 'webdav'])
export function isProtocol(v: unknown): v is Protocol {
  return typeof v === 'string' && PROTOCOLS.has(v as Protocol)
}

export async function getCurrentUser(): Promise<UserInfo> {
  const info = userInfo()
  // A2：附计算机名供 UI 拼 UNC 路径（\\computer\share）
  return { username: info.username, isAdmin: await isAdmin(), computerName: hostname() }
}

export async function isAdmin(): Promise<boolean> {
  try {
    const r = await runPowerShell<boolean>(
      '(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)',
      { retries: 0 },
    )
    return !!r
  } catch {
    return false
  }
}

export async function relaunchAsAdmin(): Promise<void> {
  // 以管理员权限重新启动当前可执行文件（UAC 提权），成功后退出当前非提权实例。
  // 开发模式下提权无意义（electron-vite 子进程），直接抛错引导用户用管理员终端启动。
  if (!app.isPackaged) {
    throw Errors.commandFailed('开发模式不支持自动提权，请以管理员身份运行开发环境')
  }
  const exePath = process.execPath
  try {
    // Start-Process -Verb RunAs 触发 UAC 提示；新进程启动成功后当前实例退出
    await runPowerShellVoid(`Start-Process -FilePath ${psQuote(exePath)} -Verb RunAs`, {
      retries: 0,
      timeout: 30000,
    })
    // 给新实例一点启动时间后退出当前实例
    setTimeout(() => app.quit(), 500)
  } catch (e) {
    throw Errors.commandFailed(`提权失败：${(e as Error).message.slice(0, 200)}`)
  }
}

export async function getDashboardStats(): Promise<DashboardStats> {
  // M-8：按次时间戳计时，规避并发调用（挂载+F5）下 console.time 标签冲突
  const t0 = Date.now()
  // 所有独立查询并行执行，避免 5+ 个 PowerShell 进程串行启动造成 3-5s 延迟
  const [allSharesResult, smbSessionsResult, smbFilesResult, nfsSessionsResult, svcResult] =
    await Promise.allSettled([
      adapterList().catch(() => [] as Share[]),
      runPowerShell<any[]>('Get-SmbSession').catch(() => []),
      runPowerShell<any[]>('Get-SmbOpenFile').catch(() => []),
      adapterSessions('nfs').catch(() => []),
      getServiceStatus(),
    ])

  const allShares = allSharesResult.status === 'fulfilled' ? allSharesResult.value : []

  const byProtocol: Record<Protocol, { shares: number; sessions: number }> = {
    smb: { shares: 0, sessions: 0 },
    nfs: { shares: 0, sessions: 0 },
    ftp: { shares: 0, sessions: 0 },
    webdav: { shares: 0, sessions: 0 },
  }
  for (const s of allShares) {
    if (byProtocol[s.protocol]) byProtocol[s.protocol].shares++
  }

  // SMB 会话与打开文件
  let activeSessions = 0
  let openFiles = 0
  if (smbSessionsResult.status === 'fulfilled') {
    const smbSessions = Array.isArray(smbSessionsResult.value) ? smbSessionsResult.value.length : 0
    activeSessions += smbSessions
    byProtocol.smb.sessions = smbSessions
  }
  if (smbFilesResult.status === 'fulfilled') {
    openFiles = Array.isArray(smbFilesResult.value) ? smbFilesResult.value.length : 0
  }

  // NFS 会话
  if (nfsSessionsResult.status === 'fulfilled') {
    const nfsSessions = nfsSessionsResult.value
    byProtocol.nfs.sessions = nfsSessions.length
    activeSessions += nfsSessions.length
  }

  // SMB 服务状态
  const svc =
    svcResult.status === 'fulfilled'
      ? svcResult.value
      : { status: 'Unknown' as const, name: '', startType: '' as const }
  const serviceStatus: DashboardStats['serviceStatus'] = svc.status

  const topShares = allShares
    .filter((s) => s.type !== 'Special' && s.type !== 'IPC')
    .map((s) => ({ name: s.name, connections: s.concurrentUsers || 0, protocol: s.protocol }))
    .sort((a, b) => b.connections - a.connections)
    .slice(0, 8)

  log.info('perf', `getDashboardStats 耗时 ${Date.now() - t0}ms`)
  return {
    shareCount: allShares.filter((s) => s.type !== 'Special' && s.type !== 'IPC').length,
    activeSessions,
    openFiles,
    serviceStatus,
    topShares,
    byProtocol,
  }
}

export async function getAuditLog(): Promise<string> {
  return readAuditLog()
}

export async function healthCheck(): Promise<{ ok: boolean; detail: string }> {
  try {
    await runPowerShellVoid('Get-Command Get-SmbShare | Out-Null', { retries: 0 })
    return { ok: true, detail: 'PowerShell SMB 模块可用' }
  } catch (e) {
    return { ok: false, detail: (e as Error).message }
  }
}

// ===== 版本适配（OS matrix）：单次 PowerShell 往返探测 OS + 功能可用性，进程内缓存 =====
// 设计原则：以"功能是否可用"驱动 UI/写入降级，不硬编码版本号判断——
// 同一版本不同 SKU（Home 无 IIS）、Server 各版本（2016 无 QUIC）差异全部经探测吸收。
const SKU_NAMES: Record<number, string> = {
  2: 'Home Basic',
  3: 'Home Premium',
  4: 'Enterprise',
  6: 'Business (Server)',
  7: 'Enterprise (Server)',
  8: 'Datacenter (Server)',
  9: 'Web Server (Server)',
  12: 'Storage Server',
  28: 'Standard (Server)',
  39: 'Datacenter (Server)',
  51: 'Essentials (Server)',
  61: 'Business (Server)',
  62: 'Enterprise (Server)',
  63: 'Datacenter (Server)',
  101: 'Home',
  102: 'Home (N)',
  103: 'Home 单语言版',
  104: 'Home (中国)',
  105: 'Home (N) 单语言版',
  109: 'Home',
  110: 'Home 单语言版',
  111: 'Education',
  121: 'Pro Education',
  122: 'Pro for Workstations',
  161: 'Pro Workstations (Server)?',
  175: 'SE 单语言版',
}
// 家庭系 SKU：无 IIS（FTP/WebDAV 不可用），无客户端 NFS
const HOME_SKUS = new Set([101, 102, 103, 104, 105, 109, 110])

let osInfoCache: OsInfo | null = null

export async function getOsInfo(opts: { refresh?: boolean } = {}): Promise<OsInfo> {
  if (osInfoCache && !opts.refresh) return osInfoCache
  const raw = await runPowerShell<any>(
    '$os = Get-CimInstance Win32_OperatingSystem; ' +
      '$build = if ($os.BuildNumber) { [int]$os.BuildNumber } else { [Environment]::OSVersion.Version.Build }; ' +
      '$quic = $false; try { $quic = ((Get-SmbServerConfiguration).PSObject.Properties.Name -contains "EnableSMBQUIC") } catch {}; ' +
      '$iisFeat = $false; try { $null = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -ErrorAction Stop; $iisFeat = $true } catch {}; ' +
      '[PSCustomObject]@{ Caption = $os.Caption; Build = $build; SKU = [int]$os.OperatingSystemSKU; ' +
      'SmbShare = [bool](Get-Command Get-SmbShare -ErrorAction SilentlyContinue); ' +
      'NfsServer = [bool](Get-Command Get-NfsShare -ErrorAction SilentlyContinue); ' +
      'IisModule = [bool](Get-Module -ListAvailable -Name WebAdministration); ' +
      'IisFeature = $iisFeat; Quic = [bool]$quic }',
    { retries: 0 },
  )
  const caption = String(raw?.Caption ?? '')
  const isServer = /server/i.test(caption)
  const skuId = Number(raw?.SKU ?? 0)
  const info: OsInfo = {
    caption: caption.trim() || 'Windows（未知版本）',
    buildNumber: Number(raw?.Build ?? 0),
    skuId,
    skuName: SKU_NAMES[skuId] ?? `SKU ${skuId}`,
    isServer,
    isHomeEdition: HOME_SKUS.has(skuId),
    hostname: hostname(),
    features: {
      smbShareModule: Boolean(raw?.SmbShare),
      nfsServerCmdlets: Boolean(raw?.NfsServer),
      // Server 恒可承载 IIS；客户端看 IIS 可选功能是否存在（Home 查询失败→false）
      iisAvailable: isServer || Boolean(raw?.IisModule) || Boolean(raw?.IisFeature),
      smbQuicConfig: Boolean(raw?.Quic),
    },
  }
  osInfoCache = info
  return info
}
