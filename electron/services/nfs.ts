import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { ServiceStatus, NfsServerConfig } from '../types'

// 默认配置：协议未装或查询失败时返回，让 Settings 表单可渲染（由 ProtocolCapabilityBanner 引导安装）
function defaultConfig(): NfsServerConfig {
  return {
    gracefulUnmount: false,
    logActivity: false,
    enableUnmappedAccess: false,
    enableAuthenticationRenegotiation: false,
    gatewayCharacterSet: '',
    protocolVersion: ''
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
      gatewayCharacterSet: raw.GatewayCharacterSet || '',
      protocolVersion: raw.ProtocolVersion || ''
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
  for (const key of Object.keys(map)) {
    const k = key as keyof NfsServerConfig
    if (config[k] !== undefined) {
      parts.push(`-${map[key]} $${config[k]}`)
    }
  }
  parts.push('-Confirm:$false')
  if (parts.length <= 2) throw Errors.invalidParam('未提供任何配置项')
  await runPowerShellVoid(parts.join(' '))
}

export async function getServiceStatus(): Promise<ServiceStatus> {
  try {
    const raw = await runPowerShell<any>(
      'Get-Service NfsService -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType'
    )
    if (!raw || !raw.Name) {
      return { name: 'NfsService', status: 'Unknown', startType: 'Unknown' }
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
    return { name: 'NfsService', status: 'Unknown', startType: 'Unknown' }
  }
}

export async function restartService(): Promise<void> {
  await runPowerShellVoid('Restart-Service -Name NfsService -Force -ErrorAction SilentlyContinue')
}
