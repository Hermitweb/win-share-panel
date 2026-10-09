import { log, logDir, normalizeLevel, readLogTail, writeLog } from '../lib/logger'
import { app, dialog, ipcMain, BrowserWindow, shell } from 'electron'
import { audit } from '../lib/audit'
import { Errors } from '../lib/errors'
import { validateShareName, validatePath } from '../lib/powershell'
import * as share from '../services/share'
import * as user from '../services/user'
import * as session from '../services/session'
import * as smb from '../services/smb'
import * as preset from '../services/preset'
import * as system from '../services/system'
import { isProtocol } from '../services/system'
import * as nfs from '../services/nfs'
import * as ftp from '../services/ftp'
import * as webdav from '../services/webdav'
import {
  adapterList,
  adapterCreate,
  adapterUpdate,
  adapterDelete,
  adapterToggle,
  adapterGetPermissions,
  adapterSetPermissions,
  adapterSessions,
  adapterCloseSession,
  getCapabilitiesMap,
} from '../services/protocol/registry'
import { detectProtocols, installProtocol } from '../services/protocol/detect'
// ===== 批1 UX 底座：应用状态 / 磁盘水位 / 安全体检 / 防火墙 / 一键诊断 =====
import { loadState, saveState } from '../lib/stateStore'
import { listJournal, undoJournal, clearJournal } from '../services/journal'
import { getDiskUsages, suggestRoot } from '../services/disk'
import { securityReport } from '../services/security'
import {
  FW_GROUP,
  listManagedRules,
  ensureRules,
  removeRule,
  presetRules,
} from '../services/firewall'
import { runDiagnose, applyFix } from '../services/diagnose'
import { checkForUpdate } from '../services/update'
import { isSafeExternalUrl } from '../lib/url'
import { exportSettings, importSettings } from '../services/transfer'
import type {
  Protocol,
  CreateShareInput,
  UpdateShareInput,
  SharePermission,
  AppStatePatch,
  AlertRules,
  DesiredRule,
  DiagnoseFixKind,
} from '../types'

// 防火墙预设白名单（渲染层只能选其一，端口范围另经 opts 校验）
const PRESET_KINDS = new Set(['smb', 'ftp', 'ftpPassive', 'webdav', 'quic'])

// 统一包装：审计 + 错误透传
function wrap<T>(fn: () => Promise<T>, action: string, target: string): Promise<T> {
  return fn()
    .then((r) => {
      audit('system', action, target, 'success')
      return r
    })
    .catch((e) => {
      audit('system', action, target, 'failure', (e as Error).message)
      throw e
    })
}

// === IPC 输入校验（防御纵深）：TS 类型仅编译期，渲染进程可传任意形状 ===
// 协议校验：非法协议直接拒绝，避免 getAdapter 抛错前浪费一次 PowerShell 调用
function requireProtocol(v: unknown): Protocol {
  if (!isProtocol(v)) throw Errors.invalidParam(`非法协议：${String(v)}`)
  return v
}
// 共享名校验：空/超长/非法字符直接拒绝。B-4：与读取侧一致放行系统共享尾 $（IPC$ 等），
// adapter:create 的 $ 名最终仍被服务层严格 validateName 拒绝（纵深不变）。
function requireName(v: unknown): string {
  if (typeof v !== 'string' || !validateShareName(v)) throw Errors.invalidParam('共享名非法')
  return v
}
// 路径校验
function requirePath(v: unknown): string {
  if (typeof v !== 'string' || !validatePath(v)) throw Errors.invalidParam('路径非法')
  return v
}
// 字符串数组校验（用于 SMB 访问控制 fullAccess/changeAccess/readAccess/noAccess）
function requireStringArray(v: unknown): string[] | undefined {
  if (v === undefined || v === null) return undefined
  if (!Array.isArray(v)) throw Errors.invalidParam('参数必须为数组')
  for (const item of v) {
    if (typeof item !== 'string' || !item.length)
      throw Errors.invalidParam('数组元素必须为非空字符串')
    // F-1（审计纵深）：限制元素长度与控制字符；DOMAIN\user、空格等合法账号形态保持放行
    if (item.length > 200 || CONTROL_CHARS.test(item))
      throw Errors.invalidParam('数组元素过长或含控制字符')
  }
  return v as string[]
}

// === F-1 防御纵深助手：形状守卫（类型混淆/超长/控制字符一律拒绝；空值按未提供放行）。
// 语义校验仍归服务层（validateName/validatePath/ps*），此处只做边界收敛，不改合法输入语义。
// eslint-disable-next-line no-control-regex -- \u0000-\u001f/\u007f 为显式排除控制字符，正是 F-1 边界守卫的语义本身
const CONTROL_CHARS = /[\u0000-\u001f\u007f]/
function requirePlainObject(v: unknown, what: string): Record<string, unknown> {
  if (typeof v !== 'object' || v === null || Array.isArray(v))
    throw Errors.invalidParam(`${what}必须为对象`)
  return v as Record<string, unknown>
}
function requireTextField(v: unknown, what: string, maxLen: number): void {
  if (v === undefined || v === null) return
  if (typeof v !== 'string' || v.length > maxLen || CONTROL_CHARS.test(v))
    throw Errors.invalidParam(`${what}不合法（类型/长度/控制字符）`)
}
function requireBooleanField(v: unknown, what: string): void {
  if (v === undefined || v === null) return
  if (typeof v !== 'boolean') throw Errors.invalidParam(`${what}必须为布尔值`)
}

// === 批1 UX 底座入参守卫 ===
// 应用状态补丁：只接受白名单字段，且逐字段类型收敛（渲染层可传任意形状）
function requireStatePatch(v: unknown): AppStatePatch {
  const o = requirePlainObject(v, '应用状态补丁')
  const out: AppStatePatch = {}
  if (o.theme !== undefined) {
    if (o.theme !== 'light' && o.theme !== 'dark') throw Errors.invalidParam('theme 非法')
    out.theme = o.theme
  }
  if (o.advancedMode !== undefined) {
    if (typeof o.advancedMode !== 'boolean') throw Errors.invalidParam('advancedMode 必须为布尔值')
    out.advancedMode = o.advancedMode
  }
  if (o.autoStart !== undefined) {
    if (typeof o.autoStart !== 'boolean') throw Errors.invalidParam('autoStart 必须为布尔值')
    out.autoStart = o.autoStart
  }
  if (o.pinned !== undefined) {
    const pinned = requireStringArray(o.pinned) ?? []
    if (pinned.length > 100) throw Errors.invalidParam('置顶数量过多')
    out.pinned = pinned
  }
  if (o.alertRules !== undefined) {
    const a = requirePlainObject(o.alertRules, '告警规则')
    // 逐键按需带上：未提供的键不写进补丁，stateStore 浅合并会保留原值
    const rules: Partial<AlertRules> = {}
    if (a.idleAlertMinutes !== undefined) {
      if (!(
        a.idleAlertMinutes === null ||
        (typeof a.idleAlertMinutes === 'number' && a.idleAlertMinutes > 0)
      )) {
        throw Errors.invalidParam('idleAlertMinutes 必须为正数或 null')
      }
      rules.idleAlertMinutes = a.idleAlertMinutes as number | null
    }
    if (a.smb1Alert !== undefined) {
      requireBooleanField(a.smb1Alert, 'smb1Alert')
      rules.smb1Alert = a.smb1Alert as boolean
    }
    if (a.weakPasswordAlert !== undefined) {
      requireBooleanField(a.weakPasswordAlert, 'weakPasswordAlert')
      rules.weakPasswordAlert = a.weakPasswordAlert as boolean
    }
    if (a.diskLowGb !== undefined) {
      if (!(typeof a.diskLowGb === 'number' && a.diskLowGb >= 0)) {
        throw Errors.invalidParam('diskLowGb 必须为非负数')
      }
      rules.diskLowGb = a.diskLowGb as number
    }
    out.alertRules = rules
  }
  if (Object.keys(out).length === 0) throw Errors.invalidParam('补丁无有效字段')
  return out
}

function requireDiagnoseFix(v: unknown): DiagnoseFixKind {
  const allowed: DiagnoseFixKind[] = ['service:lanman', 'firewall:smb', 'acl:read-everyone']
  if (!allowed.includes(v as DiagnoseFixKind)) throw Errors.invalidParam('修复项非法')
  return v as DiagnoseFixKind
}

function requireDesiredRules(v: unknown): DesiredRule[] {
  if (!Array.isArray(v) || v.length === 0 || v.length > 20) {
    throw Errors.invalidParam('防火墙规则列表必须为 1-20 条')
  }
  return v.map((raw) => {
    const o = requirePlainObject(raw, '防火墙规则')
    requireTextField(o.name, '规则名', 100)
    requireTextField(o.ports, '端口', 64)
    if (o.protocol !== undefined && o.protocol !== 'TCP' && o.protocol !== 'UDP') {
      throw Errors.invalidParam('协议非法')
    }
    return {
      name: o.name as string,
      ports: o.ports as string,
      protocol: o.protocol as 'TCP' | 'UDP' | undefined,
    }
  })
}

export function registerIpc(): void {
  // === share ===
  ipcMain.handle('share:list', () => wrap(share.listShares, 'list', 'shares'))
  ipcMain.handle('share:get', (_e, name: string) => wrap(() => share.getShare(name), 'get', name))
  ipcMain.handle('share:create', (_e, opts) => {
    const o = requirePlainObject(opts, '共享创建参数')
    requireTextField(o.description, '描述', 200)
    requireBooleanField(o.encrypted, 'encrypted')
    requireBooleanField(o.encryptData, 'encryptData')
    requireBooleanField(o.shareShadowCopy, 'shareShadowCopy')
    requireBooleanField(o.cached, 'cached')
    requireStringArray(o.fullAccess)
    requireStringArray(o.changeAccess)
    requireStringArray(o.readAccess)
    requireStringArray(o.noAccess)
    return wrap(() => share.createShare(opts), 'create', opts?.name || '')
  })
  ipcMain.handle('share:update', (_e, name: string, opts) => {
    const o = requirePlainObject(opts, '共享更新参数')
    requireTextField(o.description, '描述', 200)
    requireBooleanField(o.cached, 'cached')
    requireBooleanField(o.encryptData, 'encryptData')
    return wrap(() => share.updateShare(name, opts), 'update', name)
  })
  ipcMain.handle('share:delete', (_e, name: string) =>
    wrap(() => share.deleteShare(name), 'delete', name),
  )
  ipcMain.handle('share:toggle', (_e, name: string, enabled: boolean) =>
    wrap(() => share.toggleShare(name, enabled), 'toggle', name),
  )
  ipcMain.handle('share:permissions', (_e, name: string) =>
    wrap(() => share.getSharePermissions(name), 'getPermissions', name),
  )
  ipcMain.handle('share:export', () => wrap(share.exportConfig, 'export', 'config'))
  ipcMain.handle('share:import', (_e, json: string) =>
    wrap(() => share.importConfig(json), 'import', 'config'),
  )
  ipcMain.handle('share:connections', (_e, name: string) =>
    wrap(() => share.getShareConnections(name), 'connections', name),
  )
  ipcMain.handle('share:openFiles', (_e, name: string) =>
    wrap(() => share.getShareOpenFiles(name), 'openFiles', name),
  )
  ipcMain.handle('share:closeOpenFiles', (_e, name: string) =>
    wrap(() => share.closeShareOpenFiles(name), 'closeOpenFiles', name),
  )

  // === user ===
  ipcMain.handle('user:list', () => wrap(user.listUsers, 'list', 'users'))
  ipcMain.handle('user:get', (_e, name: string) => wrap(() => user.getUser(name), 'get', name))
  ipcMain.handle('user:groups', () => wrap(user.listGroups, 'list', 'groups'))
  ipcMain.handle('user:sharePermissions', (_e, name: string) =>
    wrap(() => user.getSharePermissions(name), 'getPermissions', name),
  )
  ipcMain.handle('user:sharePermissionsForUser', (_e, name: string) =>
    wrap(() => user.getUserSharePermissions(name), 'getUserPermissions', name),
  )
  ipcMain.handle('user:setSharePermissions', (_e, name: string, perms) =>
    wrap(() => user.setSharePermissions(name, perms), 'setPermissions', name),
  )
  ipcMain.handle('user:ntfsPermissions', (_e, path: string) =>
    wrap(() => user.getNtfsPermissions(path), 'getNtfs', path),
  )
  ipcMain.handle('user:create', (_e, opts) => {
    const o = requirePlainObject(opts, '用户创建参数')
    requireTextField(o.name, '用户名', 80)
    requireTextField(o.fullName, '全名', 200)
    requireTextField(o.description, '描述', 200)
    requireBooleanField(o.enabled, 'enabled')
    requireBooleanField(o.passwordChangeable, 'passwordChangeable')
    requireBooleanField(o.passwordExpires, 'passwordExpires')
    // 口令刻意不做控制字符守卫：合法口令可含任意字符；
    // 非空/≤127 由服务层校验，拼接由 psQuote 兜底安全
    return wrap(() => user.createUser(opts), 'create', opts?.name || '')
  })
  ipcMain.handle('user:update', (_e, name: string, opts) => {
    const o = requirePlainObject(opts, '用户更新参数')
    requireTextField(o.fullName, '全名', 200)
    requireTextField(o.description, '描述', 200)
    requireBooleanField(o.enabled, 'enabled')
    requireBooleanField(o.passwordChangeable, 'passwordChangeable')
    requireBooleanField(o.passwordExpires, 'passwordExpires')
    return wrap(() => user.updateUser(name, opts), 'update', name)
  })
  ipcMain.handle('user:delete', (_e, name: string) =>
    wrap(() => user.deleteUser(name), 'delete', name),
  )
  ipcMain.handle('user:setPassword', (_e, name: string, password: string) =>
    wrap(() => user.setUserPassword(name, password), 'setPassword', name),
  )
  ipcMain.handle('user:enable', (_e, name: string) =>
    wrap(() => user.enableUser(name), 'enable', name),
  )
  ipcMain.handle('user:disable', (_e, name: string) =>
    wrap(() => user.disableUser(name), 'disable', name),
  )
  ipcMain.handle('user:rename', (_e, oldName: string, newName: string) =>
    wrap(() => user.renameUser(oldName, newName), 'rename', oldName),
  )

  // === group ===
  ipcMain.handle('group:create', (_e, opts) => {
    const o = requirePlainObject(opts, '组创建参数')
    requireTextField(o.name, '组名', 80)
    requireTextField(o.description, '描述', 200)
    return wrap(() => user.createGroup(opts), 'createGroup', opts?.name || '')
  })
  ipcMain.handle('group:delete', (_e, name: string) =>
    wrap(() => user.deleteGroup(name), 'deleteGroup', name),
  )
  ipcMain.handle('group:update', (_e, name: string, desc: string) => {
    requireTextField(desc, '组描述', 200)
    return wrap(() => user.updateGroup(name, desc), 'updateGroup', name)
  })
  ipcMain.handle('group:rename', (_e, name: string, newName: string) =>
    wrap(() => user.renameGroup(name, newName), 'renameGroup', name),
  )
  ipcMain.handle('group:addMember', (_e, group: string, member: string) =>
    wrap(() => user.addGroupMember(group, member), 'addMember', group),
  )
  ipcMain.handle('group:removeMember', (_e, group: string, member: string) =>
    wrap(() => user.removeGroupMember(group, member), 'removeMember', group),
  )

  // === session ===
  ipcMain.handle('session:list', () => wrap(session.listSessions, 'list', 'sessions'))
  ipcMain.handle('session:files', () => wrap(session.listOpenFiles, 'list', 'openFiles'))
  ipcMain.handle('session:close', (_e, clientUserName: string) =>
    wrap(() => session.closeSession(clientUserName), 'closeSession', clientUserName),
  )
  ipcMain.handle('session:closeFile', (_e, fileId: string) =>
    wrap(() => session.closeFile(fileId), 'closeFile', fileId),
  )

  // === smb ===
  ipcMain.handle('smb:getConfig', () => wrap(smb.getConfig, 'getConfig', 'smb'))
  ipcMain.handle('smb:setConfig', (_e, config) => {
    requirePlainObject(config, 'SMB 服务器配置')
    return wrap(() => smb.setConfig(config), 'setConfig', 'smb')
  })
  ipcMain.handle('smb:restoreDefault', () => wrap(smb.restoreDefault, 'restoreDefault', 'smb'))
  ipcMain.handle('smb:defaultConfig', () => smb.defaultConfig())
  ipcMain.handle('smb:serviceStatus', () =>
    wrap(smb.getServiceStatus, 'serviceStatus', 'LanmanServer'),
  )
  ipcMain.handle('smb:restart', () => wrap(smb.restartService, 'restart', 'LanmanServer'))
  ipcMain.handle('smb:start', () => wrap(smb.startService, 'start', 'LanmanServer'))
  ipcMain.handle('smb:stop', () => wrap(smb.stopService, 'stop', 'LanmanServer'))
  ipcMain.handle('smb:listSnapshots', () => smb.listSnapshots())
  ipcMain.handle('smb:rollback', (_e, id: string) =>
    wrap(() => smb.rollbackSnapshot(id), 'rollback', id),
  )

  // === preset ===
  ipcMain.handle('preset:list', () => preset.listPresets())
  ipcMain.handle('preset:get', (_e, id: string) => preset.getPreset(id))
  ipcMain.handle('preset:save', (_e, p) => {
    requirePlainObject(p, '预设模板')
    return wrap(() => preset.savePreset(p), 'save', p?.id || 'preset')
  })
  ipcMain.handle('preset:update', (_e, id: string, updates) =>
    wrap(() => preset.updatePreset(id, updates), 'update', id),
  )
  ipcMain.handle('preset:delete', (_e, id: string) =>
    wrap(() => preset.deletePreset(id), 'delete', id),
  )
  ipcMain.handle('preset:duplicate', (_e, id: string, name?: string) =>
    wrap(() => preset.duplicatePreset(id, name), 'duplicate', id),
  )
  ipcMain.handle('preset:apply', (_e, name: string, id: string, mode: 'overwrite' | 'merge') =>
    wrap(() => preset.applyPreset(name, id, mode), 'applyPreset', name),
  )
  ipcMain.handle('preset:export', () => preset.exportPresets())
  ipcMain.handle('preset:import', (_e, json: string) =>
    wrap(() => preset.importPresets(json), 'import', 'presets'),
  )

  // === system ===
  ipcMain.handle('system:currentUser', () => system.getCurrentUser())
  ipcMain.handle('system:isAdmin', () => system.isAdmin())
  ipcMain.handle('system:dashboard', () => system.getDashboardStats())
  ipcMain.handle('system:auditLog', () => system.getAuditLog())
  ipcMain.handle('system:health', () => system.healthCheck())
  // 版本适配：OS + 功能探测（供设置页环境卡与 UI 降级）
  ipcMain.handle('system:osInfo', () => system.getOsInfo())
  // 路径选择修复：preload 早已暴露 selectFolder，但主进程从未注册 handler（调用必
  // "No handler registered"）——补原生文件夹选择对话框；取消返回 null，不回传任何敏感信息
  ipcMain.handle('system:selectFolder', async (e) => {
    const win = BrowserWindow.fromWebContents(e.sender)
    const options = {
      title: '选择共享文件夹',
      properties: ['openDirectory', 'createDirectory'] as ('openDirectory' | 'createDirectory')[],
    }
    const r = win ? await dialog.showOpenDialog(win, options) : await dialog.showOpenDialog(options)
    return r.canceled ? null : (r.filePaths[0] ?? null)
  })
  // 打开日志文件夹（命令面板"打开日志文件夹"命令使用）；成功返回 ''，失败返回错误串
  ipcMain.handle('system:openLogFolder', () => shell.openPath(logDir()))
  // 开机自启（服务器/NAS 常驻托管）：读写系统登录项设置，Win 下走 HKCU Run 键
  ipcMain.handle('system:autoStart', () => {
    try {
      return app.getLoginItemSettings().openAtLogin
    } catch {
      return null // 平台不支持时返回 null，UI 隐藏该项
    }
  })
  ipcMain.handle('system:setAutoStart', (_e, enabled: unknown) => {
    if (typeof enabled !== 'boolean') throw Errors.invalidParam('enabled 必须为布尔值')
    return wrap(
      async () => {
        try {
          app.setLoginItemSettings({ openAtLogin: enabled })
          return app.getLoginItemSettings().openAtLogin
        } catch (e) {
          throw Errors.commandFailed(`设置开机自启失败：${(e as Error).message}`)
        }
      },
      'setAutoStart',
      String(enabled),
    )
  })

  // === app: 日志系统（E6）===
  // 渲染层错误/事件转发主进程持久化；边界守卫：level 白名单、长度截断、简单限流
  let logWindowStart = Date.now()
  let logWindowCount = 0
  ipcMain.handle('app:logWrite', (_e, level: unknown, message: unknown) => {
    if (typeof message !== 'string' || !message.length) return null
    const now = Date.now()
    if (now - logWindowStart > 1000) {
      logWindowStart = now
      logWindowCount = 0
    }
    logWindowCount++
    // 每秒 30 条限流：失控循环不得刷爆日志
    if (logWindowCount > 30) return null
    writeLog(normalizeLevel(level), 'renderer', message.slice(0, 4000))
    return null
  })
  ipcMain.handle('app:logTail', (_e, lines: unknown) => {
    const n =
      typeof lines === 'number' && Number.isFinite(lines) ? Math.min(Math.max(1, lines), 2000) : 300
    return readLogTail(n)
  })

  // === adapter: 多协议统一路由（共享 CRUD + 权限 + 会话） ===
  ipcMain.handle('adapter:list', (_e, protocol?: Protocol) =>
    wrap(() => adapterList(protocol), 'list', `shares:${protocol || 'all'}`),
  )
  ipcMain.handle('adapter:create', (_e, input: CreateShareInput) => {
    requireProtocol(input?.protocol)
    requireName(input?.name)
    requirePath(input?.path)
    requireStringArray(input?.fullAccess)
    requireStringArray(input?.changeAccess)
    requireStringArray(input?.readAccess)
    requireStringArray(input?.noAccess)
    log.info(
      'index',
      `[createShare] IPC 入口校验通过, 协议: ${input.protocol}, 共享名: ${input.name}`,
    )
    return wrap(() => adapterCreate(input), 'create', `${input.protocol}:${input.name}`)
  })
  ipcMain.handle('adapter:update', (_e, name: string, input: UpdateShareInput) => {
    requireName(name)
    requireProtocol(input?.protocol)
    return wrap(() => adapterUpdate(name, input), 'update', `${input.protocol}:${name}`)
  })
  ipcMain.handle('adapter:delete', (_e, protocol: Protocol, name: string) => {
    requireProtocol(protocol)
    requireName(name)
    return wrap(() => adapterDelete(protocol, name), 'delete', `${protocol}:${name}`)
  })
  ipcMain.handle('adapter:toggle', (_e, protocol: Protocol, name: string, enabled: boolean) => {
    requireProtocol(protocol)
    requireName(name)
    if (typeof enabled !== 'boolean') throw Errors.invalidParam('enabled 必须为布尔值')
    return wrap(() => adapterToggle(protocol, name, enabled), 'toggle', `${protocol}:${name}`)
  })
  ipcMain.handle('adapter:permissions', (_e, protocol: Protocol, name: string) => {
    requireProtocol(protocol)
    requireName(name)
    return wrap(
      () => adapterGetPermissions(protocol, name),
      'getPermissions',
      `${protocol}:${name}`,
    )
  })
  ipcMain.handle(
    'adapter:setPermissions',
    (_e, protocol: Protocol, name: string, perms: SharePermission[]) => {
      requireProtocol(protocol)
      requireName(name)
      if (!Array.isArray(perms)) throw Errors.invalidParam('权限列表必须为数组')
      return wrap(
        () => adapterSetPermissions(protocol, name, perms),
        'setPermissions',
        `${protocol}:${name}`,
      )
    },
  )
  ipcMain.handle('adapter:sessions', (_e, protocol: Protocol) => {
    requireProtocol(protocol)
    return wrap(() => adapterSessions(protocol), 'list', `${protocol}:sessions`)
  })
  ipcMain.handle('adapter:closeSession', (_e, protocol: Protocol, sessionId: string) => {
    requireProtocol(protocol)
    if (typeof sessionId !== 'string' || !sessionId) throw Errors.invalidParam('sessionId 非法')
    return wrap(
      () => adapterCloseSession(protocol, sessionId),
      'closeSession',
      `${protocol}:${sessionId}`,
    )
  })
  ipcMain.handle('adapter:capabilities', () => getCapabilitiesMap())

  // === nfs: NFS 服务器配置/服务控制 ===
  ipcMain.handle('nfs:getConfig', () => wrap(nfs.getConfig, 'getConfig', 'nfs'))
  ipcMain.handle('nfs:setConfig', (_e, config) => {
    requirePlainObject(config, 'NFS 服务器配置')
    return wrap(() => nfs.setConfig(config), 'setConfig', 'nfs')
  })
  ipcMain.handle('nfs:restoreDefault', () => wrap(nfs.restoreDefault, 'restoreDefault', 'nfs'))
  ipcMain.handle('nfs:defaultConfig', () => nfs.defaultConfig())
  ipcMain.handle('nfs:serviceStatus', () =>
    wrap(nfs.getServiceStatus, 'serviceStatus', 'NfsService'),
  )
  ipcMain.handle('nfs:restart', () => wrap(nfs.restartService, 'restart', 'NfsService'))
  ipcMain.handle('nfs:start', () => wrap(nfs.startService, 'start', 'NfsService'))
  ipcMain.handle('nfs:stop', () => wrap(nfs.stopService, 'stop', 'NfsService'))

  // === ftp: FTP 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  ipcMain.handle('ftp:getConfig', () => wrap(ftp.getConfig, 'getConfig', 'ftp'))
  ipcMain.handle('ftp:setConfig', (_e, config) => {
    requirePlainObject(config, 'FTP 服务器配置')
    return wrap(() => ftp.setConfig(config), 'setConfig', 'ftp')
  })
  ipcMain.handle('ftp:restoreDefault', () => wrap(ftp.restoreDefault, 'restoreDefault', 'ftp'))
  ipcMain.handle('ftp:defaultConfig', () => ftp.defaultConfig())
  ipcMain.handle('ftp:serviceStatus', () => wrap(ftp.getServiceStatus, 'serviceStatus', 'ftpsvc'))
  ipcMain.handle('ftp:restart', () => wrap(ftp.restartService, 'restart', 'ftpsvc'))
  ipcMain.handle('ftp:start', () => wrap(ftp.startService, 'start', 'ftpsvc'))
  ipcMain.handle('ftp:stop', () => wrap(ftp.stopService, 'stop', 'ftpsvc'))

  // === webdav: WebDAV 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  ipcMain.handle('webdav:getConfig', () => wrap(webdav.getConfig, 'getConfig', 'webdav'))
  ipcMain.handle('webdav:setConfig', (_e, config) => {
    requirePlainObject(config, 'WebDAV 服务器配置')
    return wrap(() => webdav.setConfig(config), 'setConfig', 'webdav')
  })
  ipcMain.handle('webdav:restoreDefault', () =>
    wrap(webdav.restoreDefault, 'restoreDefault', 'webdav'),
  )
  ipcMain.handle('webdav:defaultConfig', () => webdav.defaultConfig())
  ipcMain.handle('webdav:serviceStatus', () =>
    wrap(webdav.getServiceStatus, 'serviceStatus', 'W3SVC'),
  )
  ipcMain.handle('webdav:restart', () => wrap(webdav.restartService, 'restart', 'W3SVC'))
  ipcMain.handle('webdav:start', () => wrap(webdav.startService, 'start', 'W3SVC'))
  ipcMain.handle('webdav:stop', () => wrap(webdav.stopService, 'stop', 'W3SVC'))

  // === protocol: 能力探测 + 引导安装 ===
  ipcMain.handle('protocol:detect', () => wrap(detectProtocols, 'detect', 'protocols'))
  ipcMain.handle('protocol:install', (_e, protocol: Protocol) =>
    wrap(() => installProtocol(protocol), 'install', protocol),
  )

  // === state: 应用级持久状态（UI 偏好/告警规则/置顶/操作回收站）===
  // 读操作不包 wrap（不写系统状态，无需审计噪音）；写操作走审计。
  ipcMain.handle('state:get', () => loadState())
  ipcMain.handle('state:patch', (_e, patch) =>
    wrap(() => Promise.resolve(saveState(requireStatePatch(patch))), 'statePatch', 'appState'),
  )
  ipcMain.handle('state:journalList', () => listJournal())
  ipcMain.handle('state:journalUndo', (_e, id: unknown) => {
    if (typeof id !== 'string' || !id || id.length > 64) throw Errors.invalidParam('记录 id 非法')
    return wrap(() => undoJournal(id), 'journalUndo', id)
  })
  ipcMain.handle('state:journalClear', () =>
    wrap(() => Promise.resolve(clearJournal()), 'journalClear', 'journal'),
  )

  // === disk: 磁盘水位 ===
  ipcMain.handle('disk:usages', () => wrap(getDiskUsages, 'diskUsages', 'drives'))
  // 新建共享的路径智能默认值（非系统盘剩余空间最大者）
  ipcMain.handle('disk:suggestRoot', () => suggestRoot())

  // === security: 账号安全体检（空口令/永不过期/长期未改）===
  ipcMain.handle('security:report', () => wrap(securityReport, 'securityReport', 'localUsers'))

  // === firewall: 本应用组内入站规则 ===
  ipcMain.handle('firewall:list', () => wrap(listManagedRules, 'fwList', FW_GROUP))
  // 逐条守卫后再交给 ensureRules（幂等创建；服务层同样有自己的白名单校验）
  ipcMain.handle('firewall:ensure', (_e, rules: unknown) => {
    const desired = requireDesiredRules(rules)
    return wrap(() => ensureRules(desired), 'fwEnsure', FW_GROUP)
  })
  ipcMain.handle('firewall:remove', (_e, name: string) => {
    requireTextField(name, '规则名', 100)
    return wrap(() => removeRule(name), 'fwRemove', `${FW_GROUP}:${name}`)
  })
  ipcMain.handle('firewall:preset', (_e, kind: unknown, opts: unknown) => {
    if (typeof kind !== 'string' || !PRESET_KINDS.has(kind)) throw Errors.invalidParam('预设名非法')
    const o = opts === undefined || opts === null ? {} : requirePlainObject(opts, '预设参数')
    return wrap(
      () => Promise.resolve(presetRules(kind, o as { passiveFrom?: number; passiveTo?: number })),
      'fwPreset',
      kind,
    )
  })

  // === diagnose: 一键诊断 + 自动修复 ===
  ipcMain.handle('diagnose:run', (_e, opts: unknown) => {
    const o = opts === undefined || opts === null ? {} : requirePlainObject(opts, '诊断参数')
    const rawName = o.shareName
    if (rawName !== undefined && rawName !== null && typeof rawName !== 'string') {
      throw Errors.invalidParam('shareName 必须为字符串')
    }
    const shareName: string | undefined =
      typeof rawName === 'string' && rawName ? rawName : undefined
    if (shareName && !validateShareName(shareName)) {
      throw Errors.invalidParam('共享名非法')
    }
    return wrap(() => runDiagnose({ shareName }), 'diagnose', shareName || 'all')
  })
  ipcMain.handle('diagnose:applyFix', (_e, fix: unknown, args: unknown) => {
    const kind = requireDiagnoseFix(fix)
    let shareName: string | undefined
    if (args !== undefined && args !== null) {
      const o = requirePlainObject(args, '修复参数')
      if (typeof o.shareName === 'string' && o.shareName) {
        if (!validateShareName(o.shareName)) throw Errors.invalidParam('共享名非法')
        shareName = o.shareName
      }
    }
    return wrap(() => applyFix(kind, { shareName }), 'diagnoseFix', `${kind}:${shareName || ''}`)
  })

  // === update: 应用内检查更新（只检查 + 提示，不自动下载安装） ===
  // 当前版本由主进程提供；service 层刻意不依赖 electron，便于 node 环境单测
  ipcMain.handle('update:check', () =>
    wrap(() => checkForUpdate(app.getVersion()), 'updateCheck', app.getVersion()),
  )

  // === system:openExternal: 用系统默认浏览器打开外部链接 ===
  // 只放行 http/https：防止渲染层诱导打开 file:// 等协议（本窗口 sandbox 且未设 setWindowOpenHandler）
  ipcMain.handle('system:openExternal', (_e, url: unknown) => {
    if (!isSafeExternalUrl(url)) {
      throw Errors.invalidParam('仅支持 http/https 链接')
    }
    return wrap(() => shell.openExternal(url).then(() => null), 'openExternal', url)
  })

  // === state:exportAll / state:importAll: 设置整体导入导出 ===
  // 与 share/preset 导出的分工见 services/transfer.ts 头注释（偏好 vs 业务数据）
  ipcMain.handle('state:exportAll', () =>
    wrap(() => Promise.resolve(exportSettings()), 'settingsExport', 'all'),
  )
  ipcMain.handle('state:importAll', (_e, json: unknown) =>
    wrap(() => Promise.resolve(importSettings(json)), 'settingsImport', 'all'),
  )
}
