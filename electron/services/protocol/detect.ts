import { runPowerShell, runPowerShellVoid } from '../../lib/powershell'
import { Errors } from '../../lib/errors'
import type { Protocol, ProtocolDetectionResult, ProtocolFeatureState } from '../../types'

interface RawService {
  Name: string
  Status: number | string
  StartType: number | string
}

// 各协议安装命令与提示
const PROTOCOL_INFO: Record<
  Protocol,
  { serverCmd: string; clientCmd: string; hint: string; serviceName: string }
> = {
  smb: {
    serverCmd: '',
    clientCmd: '',
    hint: 'SMB 内置于 Windows，LanmanServer 服务随系统启动',
    serviceName: 'LanmanServer'
  },
  nfs: {
    serverCmd: 'Install-WindowsFeature FS-NFS-Service -IncludeManagementTools',
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName ClientForNFSInfrastructure -All',
    hint: 'Windows Server 安装 NFS 服务角色；Win10/11 客户端仅支持 NFS 客户端，无法创建共享'
  ,
    serviceName: 'NfsService'
  },
  ftp: {
    serverCmd: 'Install-WindowsFeature Web-Ftp-Server -IncludeAllSubFeature -IncludeManagementTools',
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All',
    hint: '需安装 IIS 角色与 FTP 角色服务，ftpsvc 服务依赖 IIS'
  ,
    serviceName: 'ftpsvc'
  },
  webdav: {
    serverCmd: 'Install-WindowsFeature Web-WebDAV-Redirector -IncludeAllSubFeature -IncludeManagementTools',
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebDAV -All',
    hint: '需安装 IIS 角色与 WebDAV 发布角色服务'
  ,
    serviceName: 'W3SVC'
  }
}

// 检测是否为 Windows Server
async function isWindowsServer(): Promise<boolean> {
  try {
    const caption = await runPowerShell<string>(
      '(Get-WmiObject Win32_OperatingSystem).Caption',
      { retries: 0 }
    )
    return /Server/i.test(String(caption || ''))
  } catch {
    return false
  }
}

// 解析服务状态数值枚举
function parseServiceStatus(raw: RawService | undefined): 'Running' | 'Stopped' | 'Unknown' {
  if (!raw || !raw.Name) return 'Unknown'
  const statusNum = typeof raw.Status === 'number' ? raw.Status : Number(raw.Status)
  const statusStr = typeof raw.Status === 'string' ? raw.Status : ''
  if (statusStr === 'Running' || statusNum === 4) return 'Running'
  if (statusStr === 'Stopped' || statusNum === 1) return 'Stopped'
  return 'Unknown'
}

// 检测各协议模块是否可用
async function detectModules(): Promise<{ nfs: boolean; ftp: boolean; webdav: boolean }> {
  try {
    const raw = await runPowerShell<any>(
      `[PSCustomObject]@{ Nfs=[bool](Get-Command Get-NfsShare -ErrorAction SilentlyContinue); Ftp=[bool](Get-Command New-WebFtpSite -ErrorAction SilentlyContinue); Webdav=[bool](Get-Command Add-WebConfiguration -ErrorAction SilentlyContinue) }`
    )
    return {
      nfs: !!raw.Nfs,
      ftp: !!raw.Ftp,
      webdav: !!raw.Webdav
    }
  } catch {
    return { nfs: false, ftp: false, webdav: false }
  }
}

// 批量查询相关服务状态
async function detectServices(): Promise<Record<string, RawService>> {
  try {
    const raw = await runPowerShell<RawService | RawService[]>(
      'Get-Service LanmanServer,NfsService,ftpsvc,W3SVC -ErrorAction SilentlyContinue | Select-Object Name, Status, StartType'
    )
    const arr = Array.isArray(raw) ? raw : [raw]
    const map: Record<string, RawService> = {}
    for (const s of arr) {
      if (s && s.Name) map[s.Name.toLowerCase()] = s
    }
    return map
  } catch {
    return {}
  }
}

export async function detectProtocols(): Promise<ProtocolDetectionResult> {
  const [isServer, modules, services] = await Promise.all([
    isWindowsServer(),
    detectModules(),
    detectServices()
  ])

  const build = (proto: Protocol): ProtocolFeatureState => {
    const info = PROTOCOL_INFO[proto]
    const svcKey = info.serviceName.toLowerCase()
    const svc = services[svcKey]
    const installed =
      proto === 'smb' ? true : proto === 'nfs' ? modules.nfs : proto === 'ftp' ? modules.ftp : modules.webdav

    let installType: ProtocolFeatureState['installType']
    if (proto === 'smb') installType = 'builtin'
    else if (isServer) installType = 'server-feature'
    else installType = proto === 'nfs' ? 'client-only' : 'iis-role'

    return {
      protocol: proto,
      installed,
      installType,
      serviceName: info.serviceName,
      serviceStatus: parseServiceStatus(svc),
      installCommand: isServer ? info.serverCmd : info.clientCmd,
      installHint: info.hint
    }
  }

  return {
    smb: build('smb'),
    nfs: build('nfs'),
    ftp: build('ftp'),
    webdav: build('webdav')
  }
}

export async function installProtocol(protocol: Protocol): Promise<void> {
  const info = PROTOCOL_INFO[protocol]
  if (!info.serverCmd && !info.clientCmd) {
    throw Errors.invalidParam(`${protocol} 协议无需安装`)
  }
  const isServer = await isWindowsServer()
  const cmd = isServer ? info.serverCmd : info.clientCmd
  if (!cmd) {
    throw Errors.invalidParam(`${protocol} 协议在当前系统版本上不支持安装`)
  }
  // 安装 Windows 功能可能耗时较长，给 5 分钟超时
  await runPowerShellVoid(cmd, { timeout: 300000, retries: 0 })
}
