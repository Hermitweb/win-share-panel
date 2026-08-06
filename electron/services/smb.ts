import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { SmbServerConfig, ServiceStatus } from '../types'

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
    unauthenticatedUsersTimeLimit: raw.UnauthenticatedUsersTimeLimit || 0
  }
}

export async function setConfig(config: Partial<SmbServerConfig>): Promise<void> {
  const parts = ['Set-SmbServerConfiguration']
  const map: Record<string, string> = {
    enableSMB1Protocol: 'EnableSMB1Protocol',
    enableSMB2Protocol: 'EnableSMB2Protocol',
    enableSMB3Protocol: 'EnableSMB3Protocol',
    enableGuestUserAccess: 'EnableGuestUserAccess',
    enableInsecureGuestLogons: 'EnableInsecureGuestLogons',
    auditSmb1Access: 'AuditSmb1Access',
    requireSecuritySignature: 'RequireSecuritySignature',
    enableMultiChannel: 'EnableMultiChannel',
    announceServer: 'AnnounceServer'
  }
  for (const key of Object.keys(map)) {
    const k = key as keyof SmbServerConfig
    if (config[k] !== undefined) {
      parts.push(`-${map[key]} $${config[k]}`)
    }
  }
  parts.push('-Force')
  if (parts.length <= 2) throw Errors.invalidParam('未提供任何配置项')
  await runPowerShellVoid(parts.join(' '))
}

export async function getServiceStatus(): Promise<ServiceStatus> {
  const raw = await runPowerShell<any>('Get-Service LanmanServer')
  return {
    name: raw.Name || 'LanmanServer',
    status: raw.Status === 'Running' ? 'Running' : raw.Status === 'Stopped' ? 'Stopped' : 'Unknown',
    startType: raw.StartType || ''
  }
}

export async function restartService(): Promise<void> {
  await runPowerShellVoid('Restart-Service -Name LanmanServer -Force')
}
