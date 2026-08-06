import { runPowerShell, runPowerShellVoid } from '../lib/powershell'
import type { ServiceStatus } from '../types'

// FTP 服务控制（IIS ftpsvc，即 Microsoft FTP Service）
// FTP 站点级配置（端口/SSL/授权）由 ftpAdapter 经 IIS WebAdministration 模块处理

export async function getServiceStatus(): Promise<ServiceStatus> {
  try {
    const raw = await runPowerShell<any>(
      'Get-Service ftpsvc -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType'
    )
    if (!raw || !raw.Name) {
      return { name: 'ftpsvc', status: 'Unknown', startType: 'Unknown' }
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
    return { name: 'ftpsvc', status: 'Unknown', startType: 'Unknown' }
  }
}

export async function restartService(): Promise<void> {
  await runPowerShellVoid('Restart-Service -Name ftpsvc -Force -ErrorAction SilentlyContinue')
}
