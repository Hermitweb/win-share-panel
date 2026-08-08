import { runPowerShell, runPowerShellVoid, psBool, psNumber } from '../lib/powershell'
import { Errors } from '../lib/errors'
import type { ServiceStatus, WebdavServerConfig } from '../types'

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

// 检查服务是否存在
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
  const exists = await serviceExists('W3SVC')
  if (!exists) {
    throw Errors.commandFailed(
      'W3SVC 服务未安装。WebDAV 依赖 IIS 角色，请在「共享管理」页 WebDAV Tab 按引导安装 IIS 角色后重试'
    )
  }
  try {
    await runPowerShellVoid(
      `try { Restart-Service -Name W3SVC -Force -ErrorAction Stop } catch { if ($_.Exception.Message -match 'Cannot stop|无法停止|dependent|依赖') { Start-Service -Name W3SVC -ErrorAction SilentlyContinue; if ((Get-Service W3SVC).Status -ne 'Running') { throw } } else { throw } }`,
      { retries: 0 }
    )
  } catch (e) {
    const msg = (e as Error).message || ''
    if (/access is denied|拒绝访问|administrator|权限/i.test(msg)) {
      throw Errors.commandFailed('重启 W3SVC 服务需要管理员权限，请以管理员身份运行 WinShare Panel')
    }
    if (/Cannot stop|无法停止|dependent|依赖/i.test(msg)) {
      throw Errors.commandFailed('W3SVC 服务存在依赖服务无法重启，请手动停止依赖服务后重试，或重启计算机')
    }
    throw Errors.commandFailed(`重启 W3SVC 服务失败：${msg.slice(0, 200)}`)
  }
}

export async function startService(): Promise<void> {
  const exists = await serviceExists('W3SVC')
  if (!exists) {
    throw Errors.commandFailed('W3SVC 服务未安装，请先安装 IIS 角色')
  }
  try {
    await runPowerShellVoid('Start-Service -Name W3SVC -ErrorAction Stop', { retries: 0 })
  } catch (e) {
    throw Errors.commandFailed(`启动 W3SVC 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}

export async function stopService(): Promise<void> {
  const exists = await serviceExists('W3SVC')
  if (!exists) {
    throw Errors.commandFailed('W3SVC 服务未安装，请先安装 IIS 角色')
  }
  try {
    await runPowerShellVoid('Stop-Service -Name W3SVC -Force -ErrorAction Stop', { retries: 0 })
  } catch (e) {
    throw Errors.commandFailed(`停止 W3SVC 服务失败：${(e as Error).message.slice(0, 200)}`)
  }
}

// === WebDAV 服务器级配置（IIS system.webServer/* 配置节，MACHINE/WEBROOT/APPHOST 级别）===

// WebDAV 配置节完整路径（项目记忆强约束：必须用完整路径，短路径解析存在缺陷）
const AUTHORING_FILTER = 'system.webServer/webdav/authoring'
const AUTHORING_RULES_FILTER = 'system.webServer/webdav/authoring/authoringRules'
const ANON_AUTH_FILTER = 'system.webServer/security/authentication/anonymousAuthentication'
const BASIC_AUTH_FILTER = 'system.webServer/security/authentication/basicAuthentication'
const WINDOWS_AUTH_FILTER = 'system.webServer/security/authentication/windowsAuthentication'
const REQUEST_FILTERING_FILTER = 'system.webServer/security/requestFiltering'
const URL_COMPRESSION_FILTER = 'system.webServer/urlCompression'
const ACCESS_FILTER = 'system.webServer/security/access'

// 可写字段表：驱动 getConfig/setConfig 命令构建（WebDAV 配置无字符串/枚举字段）
interface WebdavConfigField {
  tsField: keyof WebdavServerConfig
  filter: string
  psName: string
  type: 'boolean' | 'number'
}

const WEBDAV_FIELDS: WebdavConfigField[] = [
  { tsField: 'authoringEnabled', filter: AUTHORING_FILTER, psName: 'enabled', type: 'boolean' },
  { tsField: 'authoringMaxRequestBodySize', filter: AUTHORING_FILTER, psName: 'maxRequestBodySize', type: 'number' },
  { tsField: 'maxAllowedContentLength', filter: REQUEST_FILTERING_FILTER, psName: 'maxAllowedContentLength', type: 'number' },
  { tsField: 'allowDoubleEscaping', filter: REQUEST_FILTERING_FILTER, psName: 'allowDoubleEscaping', type: 'boolean' },
  { tsField: 'verifyIntegration', filter: REQUEST_FILTERING_FILTER, psName: 'verifyIntegration', type: 'boolean' },
  { tsField: 'anonymousEnabled', filter: ANON_AUTH_FILTER, psName: 'enabled', type: 'boolean' },
  { tsField: 'basicEnabled', filter: BASIC_AUTH_FILTER, psName: 'enabled', type: 'boolean' },
  { tsField: 'windowsEnabled', filter: WINDOWS_AUTH_FILTER, psName: 'enabled', type: 'boolean' },
  { tsField: 'maxUrlLength', filter: `${REQUEST_FILTERING_FILTER}/requestLimits`, psName: 'maxUrl', type: 'number' },
  { tsField: 'maxQueryStringLength', filter: `${REQUEST_FILTERING_FILTER}/requestLimits`, psName: 'maxQueryString', type: 'number' }
]

// IIS WebDAV 默认配置
export function defaultConfig(): WebdavServerConfig {
  return {
    authoringEnabled: false,
    authoringMaxRequestBodySize: 0,
    maxAllowedContentLength: 30000000,
    allowDoubleEscaping: false,
    verifyIntegration: true,
    anonymousEnabled: false,
    basicEnabled: false,
    windowsEnabled: true,
    maxUrlLength: 260,
    maxQueryStringLength: 2048,
    // 只读信息
    globalAuthoringRulesCount: 0,
    enableStaticCompression: true,
    enableDynamicCompression: false,
    requireSSL: false
  }
}

// 预解锁 WebDAV 配置节（overrideModeDefault=Deny 的节需 appcmd unlock 后才能在服务器级 Set）
// 同时解锁 authoring/authoringRules 供 webdavAdapter 复用（站点级写入也需服务器级先解锁）
// 幂等：已解锁的节为 no-op。best-effort：失败不阻断后续写入（由 Set 的 try/catch 兜底）
export async function ensureWebdavSectionsUnlocked(): Promise<void> {
  const appcmd = '$env:windir\\system32\\inetsrv\\appcmd.exe'
  const sections = [
    `'${AUTHORING_FILTER}'`,
    `'${AUTHORING_RULES_FILTER}'`,
    `'${ANON_AUTH_FILTER}'`,
    `'${BASIC_AUTH_FILTER}'`,
    `'${WINDOWS_AUTH_FILTER}'`,
    `'${REQUEST_FILTERING_FILTER}'`,
    `'${ACCESS_FILTER}'`,
    `'${URL_COMPRESSION_FILTER}'`
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

// 读取 WebDAV 服务器级配置（单次 PowerShell 批量读取所有节 + 只读字段）
// best-effort：整体失败返回默认配置（如 IIS 未安装或 WebDAV 未配置）
export async function getConfig(): Promise<WebdavServerConfig> {
  try {
    const readParts = WEBDAV_FIELDS.map(
      (f) =>
        `try { $r | Add-Member -MemberType NoteProperty -Name ${f.tsField} -Value (Get-WebConfigurationProperty -Filter '${f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name ${f.psName} -ErrorAction Stop) } catch {}`
    )
    // 只读字段：globalAuthoringRulesCount（authoringRules 计数）、压缩、SSL
    const readOnlyParts = [
      `try { $r | Add-Member -MemberType NoteProperty -Name globalAuthoringRulesCount -Value @(Get-WebConfiguration -Filter '${AUTHORING_RULES_FILTER}/*' -PSPath 'MACHINE/WEBROOT/APPHOST' -ErrorAction Stop).Count } catch { $r | Add-Member -MemberType NoteProperty -Name globalAuthoringRulesCount -Value 0 }`,
      `try { $r | Add-Member -MemberType NoteProperty -Name enableStaticCompression -Value (Get-WebConfigurationProperty -Filter '${URL_COMPRESSION_FILTER}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name doStaticCompression -ErrorAction Stop) } catch {}`,
      `try { $r | Add-Member -MemberType NoteProperty -Name enableDynamicCompression -Value (Get-WebConfigurationProperty -Filter '${URL_COMPRESSION_FILTER}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name doDynamicCompression -ErrorAction Stop) } catch {}`,
      `try { $f = (Get-WebConfigurationProperty -Filter '${ACCESS_FILTER}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name sslFlags -ErrorAction Stop); $r | Add-Member -MemberType NoteProperty -Name requireSSL -Value ($f -and $f -ne 'None' -and $f -ne '') } catch {}`
    ]
    const cmd = `Import-Module WebAdministration; $r = [PSCustomObject]@{}; ${readParts.join('; ')}; ${readOnlyParts.join('; ')}; $r`
    const raw = await runPowerShell<any>(cmd, { retries: 0 })
    const def = defaultConfig()
    const result: Record<string, unknown> = {}
    for (const f of WEBDAV_FIELDS) {
      const v = raw[f.tsField]
      if (v === undefined || v === null) {
        result[f.tsField] = def[f.tsField]
      } else if (f.type === 'boolean') {
        result[f.tsField] = !!v
      } else {
        result[f.tsField] = Number(v) || def[f.tsField]
      }
    }
    // 只读字段
    result.globalAuthoringRulesCount =
      raw.globalAuthoringRulesCount !== undefined ? Number(raw.globalAuthoringRulesCount) || 0 : 0
    result.enableStaticCompression =
      raw.enableStaticCompression !== undefined ? !!raw.enableStaticCompression : def.enableStaticCompression
    result.enableDynamicCompression =
      raw.enableDynamicCompression !== undefined ? !!raw.enableDynamicCompression : def.enableDynamicCompression
    result.requireSSL = raw.requireSSL !== undefined ? !!raw.requireSSL : def.requireSSL
    return result as unknown as WebdavServerConfig
  } catch {
    return defaultConfig()
  }
}

// 写入 WebDAV 服务器级配置（per-field try/catch，部分节锁定不阻断其他字段写入）
// 只读字段（globalAuthoringRulesCount/enableStaticCompression/enableDynamicCompression/requireSSL）不可写，跳过
export async function setConfig(config: Partial<WebdavServerConfig>): Promise<void> {
  // 预解锁配置节
  await ensureWebdavSectionsUnlocked()
  const writeParts: string[] = []
  for (const f of WEBDAV_FIELDS) {
    const val = config[f.tsField]
    if (val === undefined) continue
    const psVal = f.type === 'boolean' ? psBool(val) : psNumber(val)
    if (psVal === null) continue // 非法类型跳过，杜绝注入
    writeParts.push(
      `try { Set-WebConfigurationProperty -Filter '${f.filter}' -PSPath 'MACHINE/WEBROOT/APPHOST' -Name ${f.psName} -Value ${psVal} -ErrorAction Stop } catch {}`
    )
  }
  if (writeParts.length === 0) throw Errors.invalidParam('未提供任何配置项')
  await runPowerShellVoid(writeParts.join('; '), { retries: 0, timeout: 30000 })
}

// 恢复默认配置
export async function restoreDefault(): Promise<WebdavServerConfig> {
  const def = defaultConfig()
  await setConfig(def)
  return def
}
