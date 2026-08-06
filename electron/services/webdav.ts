import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import type { ServiceStatus } from '../types'

// WebDAV 服务控制（IIS W3SVC，即 World Wide Web Publishing Service）
// WebDAV 站点级配置（authoring/authoringRules）由 webdavAdapter 经 WebAdministration 模块处理

export async function getServiceStatus(): Promise<ServiceStatus> {
  try {
    const raw = await runPowerShell<any>(
      'Get-Service W3SVC -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType'
    )
    if (!raw || !raw.Name) {
      return { name: 'W3SVC', status: 'Unknown', startType: 'Unknown' }
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
    return { name: 'W3SVC', status: 'Unknown', startType: 'Unknown' }
  }
}

export async function restartService(): Promise<void> {
  await runPowerShellVoid('Restart-Service -Name W3SVC -Force -ErrorAction SilentlyContinue')
}
