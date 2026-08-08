import { runPowerShell, runPowerShellVoid, psQuote, psBool, psNumber } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { ServiceStatus, FtpServerConfig } from '../types'

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

// 检查服务是否存在（避免对不存在的服务执行 Restart-Service 抛错）
async function serviceExists(name: string): Promise<boolean> {
  try {
    const raw = await runPowerShell<any>(
      `Get-Service ${name} -ErrorAction SilentlyContinue | Select-Object Name`,
      { retries: 0 }
    )
    return !!(raw && raw.Name)
  } catch {
    return false
  }
}

export async function restartService(): Promise<void> {
  // 先检查服务是否已安装，避免 ftpsvc 不存在时 Restart-Service 抛出无意义错误
  const exists = await serviceExists('ftpsvc')
  if (!exists) {
    throw Errors.commandFailed(
      'ftpsvc 服务未安装。FTP 依赖 IIS 角色，请在「共享管理」页 FTP Tab 按引导安装 FTP 角色服务后重试'
    )
  }
  // 使用 try/catch 包装 Restart-Service，捕获真实失败原因（依赖服务未启动、权限不足等）
  try {
    await runPowerShellVoid(
      `try { Restart-Service -Name ftpsvc -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match 'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name ftpsvc -ErrorAction SilentlyContinue; if ((Get-Service ftpsvc).Status -ne 'Running') { throw } } else { throw } }`,
      { retries: 0 }
    )
  } catch (e) {
    const msg = (e as Error).message || ''
    if (/access is denied|拒绝访问|administrator|权限/i.test(msg)) {
      throw Errors.commandFailed('重启 ftpsvc 服务需要管理员权限，请以管理员身份运行 WinShare Panel')
    }
    if (/Cannot stop|无法停止|dependent|依赖/i.test(msg)) {
      throw Errors.commandFailed('ftpsvc 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机')
    }
    throw Errors.commandFailed(`重启 ftpsvc 服务失败：${msg.slice(0, 200)}`)
  }
}

// 启动服务（若已停止）
export async function startService(): Promise<void> {
  const exists = await serviceExists('ftpsvc')
  if (!exists) {
    throw Errors.commandFailed('ftpsvc 服务未安装，请先安装 FTP 角色服务')
  }
  try {
    await runPowerShellVoid('Start-Service -Name ftpsvc -ErrorAction Stop', { retries: 0 })
  } catch (e) {
    throw Errors.commandFailed(`启动 ftpsvc 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}

// 停止服务
export async function stopService(): Promise<void> {
  const exists = await serviceExists('ftpsvc')
  if (!exists) {
    throw Errors.commandFailed('ftpsvc 服务未安装，请先安装 FTP 角色服务')
  }
  try {
    await runPowerShellVoid('Stop-Service -Name ftpsvc -Force -ErrorAction Stop', { retries: 0 })
  } catch (e) {
    throw Errors.commandFailed(`停止 ftpsvc 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}

// === FTP 服务器级配置（IIS ftpServer/* 配置节，MACHINE/WEBROOT/APPHOST 级别）===

// 配置字段表：驱动 getConfig/setConfig 命令构建
interface FtpConfigField {
  tsField: keyof FtpServerConfig
  filter: string
  psName: string
  type: 'boolean' | 'string' | 'number' | 'enum'
}

const FTP_FIELDS: FtpConfigField[] = [
  { tsField: 'sslControlChannelPolicy', filter: 'ftpServer/security/ssl', psName: 'controlChannelPolicy', type: 'enum' },
  { tsField: 'sslDataChannelPolicy', filter: 'ftpServer/security/ssl', psName: 'dataChannelPolicy', type: 'enum' },
  { tsField: 'sslServerCertHash', filter: 'ftpServer/security/ssl', psName: 'serverCertHash', type: 'string' },
  { tsField: 'sslClientCertRequired', filter: 'ftpServer/security/ssl', psName: 'clientCertRequired', type: 'boolean' },
  { tsField: 'ssl128', filter: 'ftpServer/security/ssl', psName: 'ssl128', type: 'boolean' },
  { tsField: 'anonymousEnabled', filter: 'ftpServer/security/authentication/anonymousAuthentication', psName: 'enabled', type: 'boolean' },
  { tsField: 'anonymousUserName', filter: 'ftpServer/security/authentication/anonymousAuthentication', psName: 'userName', type: 'string' },
  { tsField: 'basicEnabled', filter: 'ftpServer/security/authentication/basicAuthentication', psName: 'enabled', type: 'boolean' },
  { tsField: 'firewallLowDataChannelPort', filter: 'ftpServer/firewallSupport', psName: 'lowDataChannelPort', type: 'number' },
  { tsField: 'firewallHighDataChannelPort', filter: 'ftpServer/firewallSupport', psName: 'highDataChannelPort', type: 'number' },
  { tsField: 'greetingMessage', filter: 'ftpServer/messages', psName: 'greetingMessage', type: 'string' },
  { tsField: 'bannerMessage', filter: 'ftpServer/messages', psName: 'bannerMessage', type: 'string' },
  { tsField: 'exitMessage', filter: 'ftpServer/messages', psName: 'exitMessage', type: 'string' },
  { tsField: 'maxClientsMessage', filter: 'ftpServer/messages', psName: 'maxClientsMessage', type: 'string' },
  { tsField: 'suppressDefaultMessages', filter: 'ftpServer/messages', psName: 'suppressDefault', type: 'boolean' },
  { tsField: 'showVirtualDirs', filter: 'ftpServer/directoryBrowse', psName: 'showVirtualDirs', type: 'boolean' },
  { tsField: 'userIsolationMode', filter: 'ftpServer/userIsolation', psName: 'mode', type: 'enum' },
  { tsField: 'unauthenticatedTimeout', filter: 'ftpServer/connections', psName: 'unauthenticatedTimeout', type: 'number' },
  { tsField: 'controlConnectionTimeout', filter: 'ftpServer/connections', psName: 'controlConnectionTimeout', type: 'number' },
  { tsField: 'dataChannelConnectionTimeout', filter: 'ftpServer/connections', psName: 'dataChannelConnectionTimeout', type: 'number' },
  { tsField: 'keepPartialUploads', filter: 'ftpServer/fileHandling', psName: 'keepPartialUploads', type: 'boolean' },
  { tsField: 'allowReplaceOnRename', filter: 'ftpServer/fileHandling', psName: 'allowReplaceOnRename', type: 'boolean' },
  { tsField: 'logFileDirectory', filter: 'ftpServer/logFile', psName: 'directory', type: 'string' },
  { tsField: 'logFilePeriod', filter: 'ftpServer/logFile', psName: 'period', type: 'enum' }
]

// IIS FTP 7.5+ 默认配置
export function defaultConfig(): FtpServerConfig {
  return {
    sslControlChannelPolicy: 'SslAllow',
    sslDataChannelPolicy: 'SslAllow',
    sslServerCertHash: '',
    sslClientCertRequired: false,
    ssl128: false,
    anonymousEnabled: false,
    anonymousUserName: 'IUSR',
    basicEnabled: false,
    firewallLowDataChannelPort: 0,
    firewallHighDataChannelPort: 0,
    greetingMessage: '',
    bannerMessage: '',
    exitMessage: '',
    maxClientsMessage: '',
    suppressDefaultMessages: false,
    showVirtualDirs: false,
    userIsolationMode: 'None',
    unauthenticatedTimeout: 30,
    controlConnectionTimeout: 300,
    dataChannelConnectionTimeout: 30,
    keepPartialUploads: false,
    allowReplaceOnRename: false,
    logFileDirectory: '%SystemDrive%\\inetpub\\logs\\LogFiles',
    logFilePeriod: 'Daily'
  }
}

// 预解锁 FTP 配置节（overrideModeDefault=Deny 的节需 appcmd unlock 后才能在服务器级 Set）
// 同时解锁 authorization 供 ftpAdapter 站点级 setPermissions 复用
// best-effort：失败不阻断后续写入（部分节可能已解锁）
export async function ensureFtpSectionsUnlocked(): Promise<void> {
  const appcmd = '$env:windir\\system32\\inetsrv\\appcmd.exe'
  const sections = [
    "'ftpServer/security/authentication/anonymousAuthentication'",
    "'ftpServer/security/authentication/basicAuthentication'",
    "'ftpServer/security/authentication/customAuthentication'",
    "'ftpServer/security/authorization'",
    "'ftpServer/security/ssl'",
    "'ftpServer/userIsolation'",
    "'ftpServer/messages'",
    "'ftpServer/directoryBrowse'",
    "'ftpServer/connections'",
    "'ftpServer/fileHandling'",
    "'ftpServer/firewallSupport'",
    "'ftpServer/logFile'"
  ]
  const cmd = sections
    .map((s) => `try { & ${appcmd} unlock config /section:${s} 2>&1 | Out-Null } catch {}`)
    .join('; ')
  try {
    await runPowerShellVoid(cmd, { retries: 0, timeout: 30000 })
  } catch {
    // best-effort：解锁失败不阻断
  }
}

// 读取 FTP 服务器级配置（单次 PowerShell 批量读取所有节）
// best-effort：整体失败返回默认配置（如 ftpsvc 未安装或 IIS 未配置）
export async function getConfig(): Promise<FtpServerConfig> {
  try {
    const readParts = FTP_FIELDS.map(
      (f) =>
        `try { $r | Add-Member -MemberType NoteProperty -Name ${f.tsField} -Value (Get-WebConfigurationProperty -Filter '${f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name ${f.psName} -ErrorAction Stop) } catch {}`
    )
    const cmd = `Import-Module WebAdministration; $r = [PSCustomObject]@{}; ${readParts.join('; ')}; $r`
    const raw = await runPowerShell<any>(cmd, { retries: 0 })
    const def = defaultConfig()
    const result: Record<string, unknown> = {}
    for (const f of FTP_FIELDS) {
      const v = raw[f.tsField]
      if (v === undefined || v === null) {
        result[f.tsField] = def[f.tsField]
      } else if (f.type === 'boolean') {
        result[f.tsField] = !!v
      } else if (f.type === 'number') {
        result[f.tsField] = Number(v) || def[f.tsField]
      } else {
        result[f.tsField] = String(v)
      }
    }
    return result as unknown as FtpServerConfig
  } catch {
    return defaultConfig()
  }
}

// 写入 FTP 服务器级配置（per-field try/catch，部分节锁定不阻断其他字段写入）
export async function setConfig(config: Partial<FtpServerConfig>): Promise<void> {
  // 预解锁配置节
  await ensureFtpSectionsUnlocked()
  const writeParts: string[] = []
  for (const f of FTP_FIELDS) {
    const val = config[f.tsField]
    if (val === undefined) continue
    let psVal: string | null = null
    if (f.type === 'boolean') {
      psVal = psBool(val)
    } else if (f.type === 'number') {
      psVal = psNumber(val)
    } else {
      // 字符串/枚举：清理换行，单引号转义
      psVal = psQuote(String(val).replace(/\r?\n/g, ' '))
    }
    if (psVal === null) continue // 非法类型跳过，杜绝注入
    writeParts.push(
      `try { Set-WebConfigurationProperty -Filter '${f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name ${f.psName} -Value ${psVal} -ErrorAction Stop } catch {}`
    )
  }
  if (writeParts.length === 0) throw Errors.invalidParam('未提供任何配置项')
  await runPowerShellVoid(writeParts.join('; '), { retries: 0, timeout: 30000 })
}

// 恢复默认配置
export async function restoreDefault(): Promise<FtpServerConfig> {
  const def = defaultConfig()
  await setConfig(def)
  return def
}
