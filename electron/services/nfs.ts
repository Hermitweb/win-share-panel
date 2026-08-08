import { runPowerShell, runPowerShellVoid, psBool, psNumber } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { ServiceStatus, NfsServerConfig } from '../types'

// 默认配置：协议未装或查询失败时返回，让 Settings 表单可渲染（由 ProtocolCapabilityBanner 引导安装）
export function defaultConfig(): NfsServerConfig {
  return {
    gracefulUnmount: false,
    logActivity: false,
    enableUnmappedAccess: false,
    enableAuthenticationRenegotiation: false,
    gatewayCharacterSet: 'ANSI',
    protocolVersion: '4.1',
    // 连接与超时
    tcpConnectionTimeout: 240,
    udpConnectionTimeout: 240,
    restartConnectionTimeout: 60,
    maxConcurrentConnectionsPerUser: 0,
    directoryCacheExpiry: 60,
    // 身份映射（只读）
    anonymousUid: -2,
    anonymousGid: -2
  }
}

export async function getConfig(): Promise<NfsServerConfig> {
  try {
    // retries:0 避免未装 NFS 时无谓重试造成切 Tab 延迟
    const raw = await runPowerShell<any>('Get-NfsServerConfiguration', { retries: 0 })
    return {
      gracefulUnmount: !!raw.GracefulUnmount,
      logActivity: !!raw.LogActivity,
      enableUnmappedAccess: !!raw.EnableUnmappedAccess,
      enableAuthenticationRenegotiation: !!raw.EnableAuthenticationRenegotiation,
      gatewayCharacterSet: raw.GatewayCharacterSet || 'ANSI',
      protocolVersion: raw.ProtocolVersion || '4.1',
      // 连接与超时（best-effort：旧版 Windows 可能无此字段，降级为默认值）
      tcpConnectionTimeout: raw.TcpConnectionTimeout !== undefined ? Number(raw.TcpConnectionTimeout) : 240,
      udpConnectionTimeout: raw.UdpConnectionTimeout !== undefined ? Number(raw.UdpConnectionTimeout) : 240,
      restartConnectionTimeout: raw.RestartConnectionTimeout !== undefined ? Number(raw.RestartConnectionTimeout) : 60,
      maxConcurrentConnectionsPerUser: raw.MaxConcurrentConnectionsPerUser !== undefined ? Number(raw.MaxConcurrentConnectionsPerUser) : 0,
      directoryCacheExpiry: raw.DirectoryCacheExpiry !== undefined ? Number(raw.DirectoryCacheExpiry) : 60,
      // 身份映射（只读）
      anonymousUid: raw.AnonymousUid !== undefined ? Number(raw.AnonymousUid) : -2,
      anonymousGid: raw.AnonymousGid !== undefined ? Number(raw.AnonymousGid) : -2
    }
  } catch {
    // NFS 未装：返回默认配置，避免 Settings Tab 弹错
    return defaultConfig()
  }
}

export async function setConfig(config: Partial<NfsServerConfig>): Promise<void> {
  const parts = ['Set-NfsServerConfiguration']
  const map: Record<string, string> = {
    gracefulUnmount: 'GracefulUnmount',
    logActivity: 'LogActivity',
    enableUnmappedAccess: 'EnableUnmappedAccess',
    enableAuthenticationRenegotiation: 'EnableAuthenticationRenegotiation'
  }
  // 布尔字段：运行时类型校验，防止 IPC 传入非法值注入
  for (const key of Object.keys(map)) {
    const k = key as keyof NfsServerConfig
    const b = psBool(config[k])
    if (b) parts.push(`-${map[key]} ${b}`)
  }
  // 数值字段：运行时类型校验
  const numFields: Array<[keyof NfsServerConfig, string]> = [
    ['tcpConnectionTimeout', 'TcpConnectionTimeout'],
    ['udpConnectionTimeout', 'UdpConnectionTimeout'],
    ['restartConnectionTimeout', 'RestartConnectionTimeout'],
    ['maxConcurrentConnectionsPerUser', 'MaxConcurrentConnectionsPerUser'],
    ['directoryCacheExpiry', 'DirectoryCacheExpiry']
  ]
  for (const [k, psName] of numFields) {
    const n = psNumber(config[k])
    if (n) parts.push(`-${psName} ${n}`)
  }
  parts.push('-Confirm:$false')
  if (parts.length <= 2) throw Errors.invalidParam('未提供任何配置项')
  await runPowerShellVoid(parts.join(' '))
}

// 恢复默认配置
export async function restoreDefault(): Promise<NfsServerConfig> {
  const def = defaultConfig()
  await setConfig(def)
  return def
}

// 检测 NFS 服务名：Windows Server 为 NfsService，Win10/11 客户端为 NfsClnt
// 一次查询两个候选名，返回存在的那个（优先 NfsService）
async function getNfsServiceName(): Promise<string | null> {
  try {
    const raw = await runPowerShell<string>(
      "@('NfsService','NfsClnt') | ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } | Where-Object { $_ } | Select-Object -First 1 -ExpandProperty Name",
      { retries: 0 }
    )
    return raw ? String(raw).trim() : null
  } catch {
    return null
  }
}

export async function getServiceStatus(): Promise<ServiceStatus> {
  const name = await getNfsServiceName()
  if (!name) {
    return { name: 'NFS', status: 'Unknown', startType: 'Unknown' }
  }
  try {
    const raw = await runPowerShell<any>(
      `Get-Service ${name} -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType`
    )
    if (!raw || !raw.Name) {
      return { name, status: 'Unknown', startType: 'Unknown' }
    }
    const statusNum = typeof raw.Status === 'number' ? raw.Status : Number(raw.Status)
    const statusStr = typeof raw.Status === 'string' ? raw.Status : ''
    let status: 'Running' | 'Stopped' | 'Unknown'
    if (statusStr === 'Running' || statusNum === 4) status = 'Running'
    else if (statusStr === 'Stopped' || statusNum === 1) status = 'Stopped'
    else status = 'Unknown'

    const startNum = typeof raw.StartType === 'number' ? raw.StartType : Number(raw.StartType)
    let startType = typeof raw.StartType === 'string' ? raw.StartType : ''
    if (!startType) {
      if (startNum === 2) startType = 'Automatic'
      else if (startNum === 3) startType = 'Manual'
      else if (startNum === 4) startType = 'Disabled'
      else startType = 'Unknown'
    }
    return { name: raw.Name, status, startType }
  } catch {
    return { name, status: 'Unknown', startType: 'Unknown' }
  }
}

export async function restartService(): Promise<void> {
  const name = await getNfsServiceName()
  if (!name) {
    throw Errors.commandFailed(
      'NFS 服务未安装。请在「共享管理」页 NFS Tab 按引导安装 NFS 角色后重试'
    )
  }
  try {
    await runPowerShellVoid(`Restart-Service -Name ${name} -Force -ErrorAction Stop`, {
      retries: 0
    })
  } catch (e) {
    const msg = (e as Error).message || ''
    if (/access is denied|拒绝访问|administrator|权限/i.test(msg)) {
      throw Errors.commandFailed('重启 NFS 服务需要管理员权限，请以管理员身份运行 WinShare Panel')
    }
    throw Errors.commandFailed(`重启 NFS 服务失败：${msg.slice(0, 200)}`)
  }
}

export async function startService(): Promise<void> {
  const name = await getNfsServiceName()
  if (!name) {
    throw Errors.commandFailed('NFS 服务未安装，请先安装 NFS 角色')
  }
  try {
    await runPowerShellVoid(`Start-Service -Name ${name} -ErrorAction Stop`, { retries: 0 })
  } catch (e) {
    throw Errors.commandFailed(`启动 NFS 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}

export async function stopService(): Promise<void> {
  const name = await getNfsServiceName()
  if (!name) {
    throw Errors.commandFailed('NFS 服务未安装，请先安装 NFS 角色')
  }
  try {
    await runPowerShellVoid(`Stop-Service -Name ${name} -Force -ErrorAction Stop`, {
      retries: 0
    })
  } catch (e) {
    throw Errors.commandFailed(`停止 NFS 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}
