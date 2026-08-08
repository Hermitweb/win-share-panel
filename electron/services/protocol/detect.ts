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
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName ClientForNFS-Infrastructure -All -NoRestart',
    hint: 'Windows Server 安装 NFS 服务角色；Win10/11 客户端仅支持 NFS 客户端，无法创建共享',
    serviceName: 'NfsService'
  },
  ftp: {
    serverCmd: 'Install-WindowsFeature Web-Ftp-Server -IncludeAllSubFeature -IncludeManagementTools',
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -All -NoRestart',
    hint: '需安装 IIS 角色与 FTP 角色服务，ftpsvc 服务依赖 IIS',
    serviceName: 'ftpsvc'
  },
  webdav: {
    serverCmd: 'Install-WindowsFeature Web-WebDAV-Redirector -IncludeAllSubFeature -IncludeManagementTools',
    clientCmd: 'Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebDAV -All -NoRestart',
    hint: '需安装 IIS 角色与 WebDAV 发布角色服务',
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
// 注意：WebDAV 不能仅用 Get-Command Add-WebConfiguration 判断——那是 WebAdministration
// 模块的 cmdlet，只要装了 IIS 管理工具就存在，并不代表 WebDAV 发布角色服务已安装。
// system.webServer/webdav/authoring 配置节仅在安装 WebDAV 发布功能（Web-WebDAV / IIS-WebDAV）
// 后才注册，故用 Get-WebConfigurationProperty 探测该节是否注册（-ErrorAction Stop + try/catch
// 兼容终止性/非终止性错误）。
// NFS: Get-NfsShare 是 Server 专属 cmdlet（需 FS-NFS-Service 角色），Win10/11 客户端安装
// ClientForNFS-Infrastructure 后只有 NfsClnt 服务，没有 Get-NfsShare。故多重检测：
// cmdlet → NfsClnt 服务 → NfsService 服务，任一存在即视为已安装。
// FTP: New-WebFtpSite 是 WebAdministration 模块 cmdlet，只要装了 IIS 管理工具就存在，
// 不代表 FTP 角色服务已安装。FTP 角色安装后会注册 ftpsvc 服务，故用服务存在性检测。
// FTP 检测细化：区分 IIS 基础服务与 FTP 角色服务
async function detectFtpState(): Promise<{ installed: boolean; iisInstalled: boolean; ftpRoleInstalled: boolean }> {
  try {
    const raw = await runPowerShell<any>(
      `$ftp=$false; $iis=$false; $ftpRole=$false; ` +
      `try { if (Get-Service W3SVC -ErrorAction SilentlyContinue) { $iis=$true } } catch {}; ` +
      `try { if (Get-Service ftpsvc -ErrorAction SilentlyContinue) { $ftpRole=$true; $ftp=$true } } catch {}; ` +
      // 若 ftpsvc 不存在但 IIS 已安装，进一步用可选功能/服务器角色确认 FTP 发布功能状态
      `if (-not $ftpRole -and $iis) { ` +
      `  try { ` +
      `    if ((Get-WindowsOptionalFeature -Online -FeatureName IIS-FTPServer -ErrorAction SilentlyContinue).State -eq 'Enabled') { $ftpRole=$true } ` +
      `  } catch {}; ` +
      `  try { ` +
      `    if ((Get-WindowsFeature -Name Web-Ftp-Server -ErrorAction SilentlyContinue).InstallState -eq 'Installed') { $ftpRole=$true } ` +
      `  } catch {} ` +
      `}; ` +
      `[PSCustomObject]@{ Installed=$ftp; IisInstalled=$iis; FtpRoleInstalled=$ftpRole }`
    )
    return {
      installed: !!raw.Installed,
      iisInstalled: !!raw.IisInstalled,
      ftpRoleInstalled: !!raw.FtpRoleInstalled
    }
  } catch {
    return { installed: false, iisInstalled: false, ftpRoleInstalled: false }
  }
}

async function detectModules(): Promise<{ nfs: boolean; ftp: boolean; webdav: boolean; ftpState: ReturnType<typeof detectFtpState> extends Promise<infer U> ? U : never }> {
  try {
    const [ftpState, raw] = await Promise.all([
      detectFtpState(),
      runPowerShell<any>(
        // WebDAV: 必须用完整路径 system.webServer/webdav/authoring，短路径 webdav/authoring
        // 在 WebAdministration 模块中解析有缺陷（读取恒返回空、写入静默失败）
        `$wd=$false; try { $null = Get-WebConfigurationProperty -Filter 'system.webServer/webdav/authoring' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name enabled -ErrorAction Stop; $wd=$true } catch {}; ` +
        // NFS: 多重检测——Server cmdlet / 客户端服务 / 服务端服务
        `$nfs=$false; try { if (Get-Command Get-NfsShare -ErrorAction SilentlyContinue) { $nfs=$true } elseif (Get-Service NfsClnt -ErrorAction SilentlyContinue) { $nfs=$true } elseif (Get-Service NfsService -ErrorAction SilentlyContinue) { $nfs=$true } } catch {}; ` +
        `[PSCustomObject]@{ Nfs=$nfs; Webdav=$wd }`
      )
    ])
    return {
      nfs: !!raw.Nfs,
      ftp: ftpState.installed,
      webdav: !!raw.Webdav,
      ftpState
    }
  } catch {
    return { nfs: false, ftp: false, webdav: false, ftpState: { installed: false, iisInstalled: false, ftpRoleInstalled: false } }
  }
}

// 批量查询相关服务状态
async function detectServices(): Promise<Record<string, RawService>> {
  try {
    const raw = await runPowerShell<RawService | RawService[]>(
      // 逐个查询服务：Get-Service Name1,Name2 在某些服务不存在时会导致进程退出码非 0，
      // execFile 据此抛错使 detectServices 误入 catch 返回空对象。
      // 逐个 Get-Service -ErrorAction SilentlyContinue 避免此问题。
      // NfsClnt = 客户端 NFS 服务（Win10/11），NfsService = 服务端 NFS 服务（Windows Server）
      "@('LanmanServer','NfsService','NfsClnt','ftpsvc','W3SVC') | ForEach-Object { Get-Service -Name $_ -ErrorAction SilentlyContinue } | Where-Object { $_ } | Select-Object Name, Status, StartType"
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

// 并发去重：多个组件（HealthBar / Dashboard / ProtocolCapabilityBanner）同时挂载时，
// 各自触发 protocol:detect IPC → detectProtocols()。若不加去重，会同时启动 3 组
// PowerShell 进程（每组 3 个进程 = 9 个），造成 CPU 峰值 + 重复延迟。
// 用 in-flight promise 缓存：并发调用共享同一个 promise，完成后清空供下次刷新使用。
let inflightDetect: Promise<ProtocolDetectionResult> | null = null

export async function detectProtocols(): Promise<ProtocolDetectionResult> {
  if (inflightDetect) {
    console.log('[perf] detectProtocols 复用 in-flight promise（跳过重复检测）')
    return inflightDetect
  }
  console.time('[perf] detectProtocols')
  inflightDetect = doDetectProtocols().finally(() => {
    inflightDetect = null
    console.timeEnd('[perf] detectProtocols')
  })
  return inflightDetect
}

// 测试专用：重置 in-flight promise，防止上一个测试的未完成检测泄漏到下一个测试
export function __resetInflightForTesting(): void {
  inflightDetect = null
}

async function doDetectProtocols(): Promise<ProtocolDetectionResult> {
  const [isServer, modules, services] = await Promise.all([
    isWindowsServer(),
    detectModules(),
    detectServices()
  ])

  const build = (proto: Protocol): ProtocolFeatureState => {
    const info = PROTOCOL_INFO[proto]
    const svcKey = info.serviceName.toLowerCase()
    let svc = services[svcKey]
    // NFS 客户端服务名为 NfsClnt，服务端为 NfsService；客户端系统无 NfsService 时回退到 NfsClnt
    if (proto === 'nfs' && !svc) svc = services['nfsclnt']
    const serviceName = svc?.Name || info.serviceName
    const installed =
      proto === 'smb' ? true : proto === 'nfs' ? modules.nfs : proto === 'ftp' ? modules.ftp : modules.webdav

    let installType: ProtocolFeatureState['installType']
    if (proto === 'smb') installType = 'builtin'
    else if (isServer) installType = 'server-feature'
    else installType = proto === 'nfs' ? 'client-only' : 'iis-role'

    // FTP 提示细化：区分 IIS 未装 / FTP 角色未装 / 服务已停
    let installHint = info.hint
    if (proto === 'ftp' && !installed) {
      const { iisInstalled, ftpRoleInstalled } = modules.ftpState
      if (!iisInstalled && !ftpRoleInstalled) {
        installHint = isServer
          ? 'FTP 依赖 IIS 角色。当前 IIS 与 FTP 角色服务均未安装，请点击「安装」自动安装 IIS + FTP 角色服务。'
          : 'FTP 依赖 IIS 可选功能。当前 IIS 与 FTP 可选功能均未安装，请点击「安装」自动安装。'
      } else if (iisInstalled && !ftpRoleInstalled) {
        installHint = isServer
          ? 'IIS 基础角色已安装，但 FTP 角色服务未安装。请点击「安装」补装 FTP 发布服务。'
          : 'IIS 基础功能已安装，但 FTP 可选功能未启用。请点击「安装」补装 FTP 功能。'
      } else {
        installHint = 'FTP 角色服务已安装，但 ftpsvc 服务未运行。请点击「安装」或前往服务管理启动 ftpsvc。'
      }
    }

    return {
      protocol: proto,
      installed,
      installType,
      serviceName,
      serviceStatus: parseServiceStatus(svc),
      installCommand: isServer ? info.serverCmd : info.clientCmd,
      installHint
    }
  }

  return {
    smb: build('smb'),
    nfs: build('nfs'),
    ftp: build('ftp'),
    webdav: build('webdav')
  }
}

// 检查 Windows 可选功能是否已安装（客户端场景）
async function isClientFeatureInstalled(featureName: string): Promise<boolean> {
  try {
    const state = await runPowerShell<string>(
      `(Get-WindowsOptionalFeature -Online -FeatureName ${featureName} -ErrorAction SilentlyContinue).State`,
      { retries: 0 }
    )
    return String(state || '').trim().toLowerCase() === 'enabled'
  } catch {
    return false
  }
}

// 检查 Windows Server 角色是否已安装（服务器场景）
async function isServerFeatureInstalled(featureName: string): Promise<boolean> {
  try {
    const installed = await runPowerShell<string>(
      `(Get-WindowsFeature -Name ${featureName} -ErrorAction SilentlyContinue).InstallState`,
      { retries: 0 }
    )
    return String(installed || '').trim().toLowerCase() === 'installed'
  } catch {
    return false
  }
}

export async function installProtocol(protocol: Protocol): Promise<void> {
  const info = PROTOCOL_INFO[protocol]
  if (!info.serverCmd && !info.clientCmd) {
    throw Errors.invalidParam(`${protocol.toUpperCase()} 协议无需安装（系统内置）`)
  }
  const isServer = await isWindowsServer()
  const cmd = isServer ? info.serverCmd : info.clientCmd
  if (!cmd) {
    throw Errors.invalidParam(`${protocol.toUpperCase()} 协议在当前系统版本上不支持安装`)
  }

  // 提取 FeatureName 用于预检（命令中 -FeatureName 后的 token）
  const featureMatch = cmd.match(/-FeatureName\s+(\S+)/)
  const featureName = featureMatch ? featureMatch[1] : ''

  // 预检：是否已安装（best-effort，失败则跳过，不阻塞安装流程）
  // 注意：不预检"功能是否可用"——Get-WindowsOptionalFeature 在非管理员或某些环境下会
  // 静默失败返回空，导致把"检查失败"误判为"功能不可用"而中止安装。
  // 直接尝试安装命令本身是更可靠的事实来源。
  if (featureName) {
    try {
      const alreadyInstalled = isServer
        ? await isServerFeatureInstalled(featureName)
        : await isClientFeatureInstalled(featureName)
      if (alreadyInstalled) {
        // 已安装：无需重复安装，直接返回成功
        return
      }
    } catch {
      // 预检失败（可能缺少权限）：跳过，直接尝试安装
    }
  }

  // 安装 Windows 功能可能耗时较长，给 5 分钟超时
  try {
    await runPowerShellVoid(cmd, { timeout: 300000, retries: 0 })
  } catch (e) {
    const msg = (e as Error).message || ''
    // 友好化常见错误
    if (/elevation|administrator|权限不足|拒绝访问|Run as administrator|以管理员身份运行/i.test(msg)) {
      throw Errors.commandFailed(
        `${protocol.toUpperCase()} 安装需要管理员权限，请以管理员身份运行 WinShare Panel 后重试`
      )
    }
    if (/NoMatch|not found|找不到|无法识别|not a valid|无效/i.test(msg)) {
      throw Errors.commandFailed(
        `${protocol.toUpperCase()} 功能名在当前系统版本上无效，请确认系统支持该协议（${info.hint}）`
      )
    }
    if (/restart|reboot|重启|重启计算机/i.test(msg)) {
      throw Errors.commandFailed(
        `${protocol.toUpperCase()} 安装成功但需要重启计算机才能生效，请保存工作后重启系统`
      )
    }
    // 透传原始错误（已截断）
    throw Errors.commandFailed(`${protocol.toUpperCase()} 安装失败：${msg.slice(0, 200)}`)
  }
}
