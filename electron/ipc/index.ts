import { ipcMain } from 'electron'
import { audit } from '../lib/audit'
import { Errors } from '../lib/errors'
import { validateName, validatePath } from '../lib/powershell'
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
  getCapabilitiesMap
} from '../services/protocol/registry'
import { detectProtocols, installProtocol } from '../services/protocol/detect'
import type { Protocol, CreateShareInput, UpdateShareInput, SharePermission } from '../types'

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
// 共享名校验：空/超长/非法字符直接拒绝
function requireName(v: unknown): string {
  if (typeof v !== 'string' || !validateName(v)) throw Errors.invalidParam('共享名非法')
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
    if (typeof item !== 'string' || !item.length) throw Errors.invalidParam('数组元素必须为非空字符串')
  }
  return v as string[]
}

export function registerIpc(): void {
  // === share ===
  ipcMain.handle('share:list', () => wrap(share.listShares, 'list', 'shares'))
  ipcMain.handle('share:get', (_e, name: string) => wrap(() => share.getShare(name), 'get', name))
  ipcMain.handle('share:create', (_e, opts) => wrap(() => share.createShare(opts), 'create', opts?.name || ''))
  ipcMain.handle('share:update', (_e, name: string, opts) => wrap(() => share.updateShare(name, opts), 'update', name))
  ipcMain.handle('share:delete', (_e, name: string) => wrap(() => share.deleteShare(name), 'delete', name))
  ipcMain.handle('share:toggle', (_e, name: string, enabled: boolean) => wrap(() => share.toggleShare(name, enabled), 'toggle', name))
  ipcMain.handle('share:permissions', (_e, name: string) => wrap(() => share.getSharePermissions(name), 'getPermissions', name))
  ipcMain.handle('share:export', () => wrap(share.exportConfig, 'export', 'config'))
  ipcMain.handle('share:import', (_e, json: string) => wrap(() => share.importConfig(json), 'import', 'config'))
  ipcMain.handle('share:connections', (_e, name: string) => wrap(() => share.getShareConnections(name), 'connections', name))
  ipcMain.handle('share:openFiles', (_e, name: string) => wrap(() => share.getShareOpenFiles(name), 'openFiles', name))
  ipcMain.handle('share:closeOpenFiles', (_e, name: string) => wrap(() => share.closeShareOpenFiles(name), 'closeOpenFiles', name))

  // === user ===
  ipcMain.handle('user:list', () => wrap(user.listUsers, 'list', 'users'))
  ipcMain.handle('user:get', (_e, name: string) => wrap(() => user.getUser(name), 'get', name))
  ipcMain.handle('user:groups', () => wrap(user.listGroups, 'list', 'groups'))
  ipcMain.handle('user:sharePermissions', (_e, name: string) => wrap(() => user.getSharePermissions(name), 'getPermissions', name))
  ipcMain.handle('user:sharePermissionsForUser', (_e, name: string) => wrap(() => user.getUserSharePermissions(name), 'getUserPermissions', name))
  ipcMain.handle('user:setSharePermissions', (_e, name: string, perms) => wrap(() => user.setSharePermissions(name, perms), 'setPermissions', name))
  ipcMain.handle('user:ntfsPermissions', (_e, path: string) => wrap(() => user.getNtfsPermissions(path), 'getNtfs', path))
  ipcMain.handle('user:create', (_e, opts) => wrap(() => user.createUser(opts), 'create', opts?.name || ''))
  ipcMain.handle('user:update', (_e, name: string, opts) => wrap(() => user.updateUser(name, opts), 'update', name))
  ipcMain.handle('user:delete', (_e, name: string) => wrap(() => user.deleteUser(name), 'delete', name))
  ipcMain.handle('user:setPassword', (_e, name: string, password: string) => wrap(() => user.setUserPassword(name, password), 'setPassword', name))
  ipcMain.handle('user:enable', (_e, name: string) => wrap(() => user.enableUser(name), 'enable', name))
  ipcMain.handle('user:disable', (_e, name: string) => wrap(() => user.disableUser(name), 'disable', name))
  ipcMain.handle('user:rename', (_e, oldName: string, newName: string) => wrap(() => user.renameUser(oldName, newName), 'rename', oldName))

  // === group ===
  ipcMain.handle('group:create', (_e, opts) => wrap(() => user.createGroup(opts), 'createGroup', opts?.name || ''))
  ipcMain.handle('group:delete', (_e, name: string) => wrap(() => user.deleteGroup(name), 'deleteGroup', name))
  ipcMain.handle('group:update', (_e, name: string, desc: string) => wrap(() => user.updateGroup(name, desc), 'updateGroup', name))
  ipcMain.handle('group:rename', (_e, name: string, newName: string) => wrap(() => user.renameGroup(name, newName), 'renameGroup', name))
  ipcMain.handle('group:addMember', (_e, group: string, member: string) => wrap(() => user.addGroupMember(group, member), 'addMember', group))
  ipcMain.handle('group:removeMember', (_e, group: string, member: string) => wrap(() => user.removeGroupMember(group, member), 'removeMember', group))

  // === session ===
  ipcMain.handle('session:list', () => wrap(session.listSessions, 'list', 'sessions'))
  ipcMain.handle('session:files', () => wrap(session.listOpenFiles, 'list', 'openFiles'))
  ipcMain.handle('session:close', (_e, clientUserName: string) => wrap(() => session.closeSession(clientUserName), 'closeSession', clientUserName))
  ipcMain.handle('session:closeFile', (_e, fileId: string) => wrap(() => session.closeFile(fileId), 'closeFile', fileId))

  // === smb ===
  ipcMain.handle('smb:getConfig', () => wrap(smb.getConfig, 'getConfig', 'smb'))
  ipcMain.handle('smb:setConfig', (_e, config) => wrap(() => smb.setConfig(config), 'setConfig', 'smb'))
  ipcMain.handle('smb:restoreDefault', () => wrap(smb.restoreDefault, 'restoreDefault', 'smb'))
  ipcMain.handle('smb:defaultConfig', () => smb.defaultConfig())
  ipcMain.handle('smb:serviceStatus', () => wrap(smb.getServiceStatus, 'serviceStatus', 'LanmanServer'))
  ipcMain.handle('smb:restart', () => wrap(smb.restartService, 'restart', 'LanmanServer'))
  ipcMain.handle('smb:start', () => wrap(smb.startService, 'start', 'LanmanServer'))
  ipcMain.handle('smb:stop', () => wrap(smb.stopService, 'stop', 'LanmanServer'))
  ipcMain.handle('smb:listSnapshots', () => smb.listSnapshots())
  ipcMain.handle('smb:rollback', (_e, id: string) => wrap(() => smb.rollbackSnapshot(id), 'rollback', id))

  // === preset ===
  ipcMain.handle('preset:list', () => preset.listPresets())
  ipcMain.handle('preset:get', (_e, id: string) => preset.getPreset(id))
  ipcMain.handle('preset:save', (_e, p) => wrap(() => preset.savePreset(p), 'save', p?.id || 'preset'))
  ipcMain.handle('preset:update', (_e, id: string, updates) => wrap(() => preset.updatePreset(id, updates), 'update', id))
  ipcMain.handle('preset:delete', (_e, id: string) => wrap(() => preset.deletePreset(id), 'delete', id))
  ipcMain.handle('preset:duplicate', (_e, id: string, name?: string) => wrap(() => preset.duplicatePreset(id, name), 'duplicate', id))
  ipcMain.handle('preset:apply', (_e, name: string, id: string, mode: 'overwrite' | 'merge') =>
    wrap(() => preset.applyPreset(name, id, mode), 'applyPreset', name)
  )
  ipcMain.handle('preset:export', () => preset.exportPresets())
  ipcMain.handle('preset:import', (_e, json: string) => wrap(() => preset.importPresets(json), 'import', 'presets'))

  // === system ===
  ipcMain.handle('system:currentUser', () => system.getCurrentUser())
  ipcMain.handle('system:isAdmin', () => system.isAdmin())
  ipcMain.handle('system:dashboard', () => system.getDashboardStats())
  ipcMain.handle('system:auditLog', () => system.getAuditLog())
  ipcMain.handle('system:health', () => system.healthCheck())

  // === adapter: 多协议统一路由（共享 CRUD + 权限 + 会话） ===
  ipcMain.handle('adapter:list', (_e, protocol?: Protocol) =>
    wrap(() => adapterList(protocol), 'list', `shares:${protocol || 'all'}`))
  ipcMain.handle('adapter:create', (_e, input: CreateShareInput) => {
    requireProtocol(input?.protocol)
    requireName(input?.name)
    requirePath(input?.path)
    requireStringArray(input?.fullAccess)
    requireStringArray(input?.changeAccess)
    requireStringArray(input?.readAccess)
    requireStringArray(input?.noAccess)
    console.log(`[createShare] IPC 入口校验通过, 协议: ${input.protocol}, 共享名: ${input.name}`)
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
    return wrap(() => adapterGetPermissions(protocol, name), 'getPermissions', `${protocol}:${name}`)
  })
  ipcMain.handle('adapter:setPermissions', (_e, protocol: Protocol, name: string, perms: SharePermission[]) => {
    requireProtocol(protocol)
    requireName(name)
    if (!Array.isArray(perms)) throw Errors.invalidParam('权限列表必须为数组')
    return wrap(() => adapterSetPermissions(protocol, name, perms), 'setPermissions', `${protocol}:${name}`)
  })
  ipcMain.handle('adapter:sessions', (_e, protocol: Protocol) => {
    requireProtocol(protocol)
    return wrap(() => adapterSessions(protocol), 'list', `${protocol}:sessions`)
  })
  ipcMain.handle('adapter:closeSession', (_e, protocol: Protocol, sessionId: string) => {
    requireProtocol(protocol)
    if (typeof sessionId !== 'string' || !sessionId) throw Errors.invalidParam('sessionId 非法')
    return wrap(() => adapterCloseSession(protocol, sessionId), 'closeSession', `${protocol}:${sessionId}`)
  })
  ipcMain.handle('adapter:capabilities', () => getCapabilitiesMap())

  // === nfs: NFS 服务器配置/服务控制 ===
  ipcMain.handle('nfs:getConfig', () => wrap(nfs.getConfig, 'getConfig', 'nfs'))
  ipcMain.handle('nfs:setConfig', (_e, config) => wrap(() => nfs.setConfig(config), 'setConfig', 'nfs'))
  ipcMain.handle('nfs:restoreDefault', () => wrap(nfs.restoreDefault, 'restoreDefault', 'nfs'))
  ipcMain.handle('nfs:defaultConfig', () => nfs.defaultConfig())
  ipcMain.handle('nfs:serviceStatus', () => wrap(nfs.getServiceStatus, 'serviceStatus', 'NfsService'))
  ipcMain.handle('nfs:restart', () => wrap(nfs.restartService, 'restart', 'NfsService'))
  ipcMain.handle('nfs:start', () => wrap(nfs.startService, 'start', 'NfsService'))
  ipcMain.handle('nfs:stop', () => wrap(nfs.stopService, 'stop', 'NfsService'))

  // === ftp: FTP 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  ipcMain.handle('ftp:getConfig', () => wrap(ftp.getConfig, 'getConfig', 'ftp'))
  ipcMain.handle('ftp:setConfig', (_e, config) => wrap(() => ftp.setConfig(config), 'setConfig', 'ftp'))
  ipcMain.handle('ftp:restoreDefault', () => wrap(ftp.restoreDefault, 'restoreDefault', 'ftp'))
  ipcMain.handle('ftp:defaultConfig', () => ftp.defaultConfig())
  ipcMain.handle('ftp:serviceStatus', () => wrap(ftp.getServiceStatus, 'serviceStatus', 'ftpsvc'))
  ipcMain.handle('ftp:restart', () => wrap(ftp.restartService, 'restart', 'ftpsvc'))
  ipcMain.handle('ftp:start', () => wrap(ftp.startService, 'start', 'ftpsvc'))
  ipcMain.handle('ftp:stop', () => wrap(ftp.stopService, 'stop', 'ftpsvc'))

  // === webdav: WebDAV 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  ipcMain.handle('webdav:getConfig', () => wrap(webdav.getConfig, 'getConfig', 'webdav'))
  ipcMain.handle('webdav:setConfig', (_e, config) => wrap(() => webdav.setConfig(config), 'setConfig', 'webdav'))
  ipcMain.handle('webdav:restoreDefault', () => wrap(webdav.restoreDefault, 'restoreDefault', 'webdav'))
  ipcMain.handle('webdav:defaultConfig', () => webdav.defaultConfig())
  ipcMain.handle('webdav:serviceStatus', () => wrap(webdav.getServiceStatus, 'serviceStatus', 'W3SVC'))
  ipcMain.handle('webdav:restart', () => wrap(webdav.restartService, 'restart', 'W3SVC'))
  ipcMain.handle('webdav:start', () => wrap(webdav.startService, 'start', 'W3SVC'))
  ipcMain.handle('webdav:stop', () => wrap(webdav.stopService, 'stop', 'W3SVC'))

  // === protocol: 能力探测 + 引导安装 ===
  ipcMain.handle('protocol:detect', () => wrap(detectProtocols, 'detect', 'protocols'))
  ipcMain.handle('protocol:install', (_e, protocol: Protocol) =>
    wrap(() => installProtocol(protocol), 'install', protocol))
}
