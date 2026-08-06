import { ipcMain } from 'electron'
import { audit } from '../lib/audit'
import * as share from '../services/share'
import * as user from '../services/user'
import * as session from '../services/session'
import * as smb from '../services/smb'
import * as preset from '../services/preset'
import * as system from '../services/system'

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

export function registerIpc(): void {
  // === share ===
  ipcMain.handle('share:list', () => wrap(share.listShares, 'list', 'shares'))
  ipcMain.handle('share:create', (_e, opts) => wrap(() => share.createShare(opts), 'create', opts?.name || ''))
  ipcMain.handle('share:update', (_e, name: string, opts) => wrap(() => share.updateShare(name, opts), 'update', name))
  ipcMain.handle('share:delete', (_e, name: string) => wrap(() => share.deleteShare(name), 'delete', name))
  ipcMain.handle('share:toggle', (_e, name: string, enabled: boolean) => wrap(() => share.toggleShare(name, enabled), 'toggle', name))
  ipcMain.handle('share:permissions', (_e, name: string) => wrap(() => share.getSharePermissions(name), 'getPermissions', name))
  ipcMain.handle('share:export', () => wrap(share.exportConfig, 'export', 'config'))
  ipcMain.handle('share:import', (_e, json: string) => wrap(() => share.importConfig(json), 'import', 'config'))

  // === user ===
  ipcMain.handle('user:list', () => wrap(user.listUsers, 'list', 'users'))
  ipcMain.handle('user:groups', () => wrap(user.listGroups, 'list', 'groups'))
  ipcMain.handle('user:sharePermissions', (_e, name: string) => wrap(() => user.getSharePermissions(name), 'getPermissions', name))
  ipcMain.handle('user:setSharePermissions', (_e, name: string, perms) => wrap(() => user.setSharePermissions(name, perms), 'setPermissions', name))
  ipcMain.handle('user:ntfsPermissions', (_e, path: string) => wrap(() => user.getNtfsPermissions(path), 'getNtfs', path))

  // === session ===
  ipcMain.handle('session:list', () => wrap(session.listSessions, 'list', 'sessions'))
  ipcMain.handle('session:files', () => wrap(session.listOpenFiles, 'list', 'openFiles'))
  ipcMain.handle('session:close', (_e, clientUserName: string) => wrap(() => session.closeSession(clientUserName), 'closeSession', clientUserName))
  ipcMain.handle('session:closeFile', (_e, fileId: string) => wrap(() => session.closeFile(fileId), 'closeFile', fileId))

  // === smb ===
  ipcMain.handle('smb:getConfig', () => wrap(smb.getConfig, 'getConfig', 'smb'))
  ipcMain.handle('smb:setConfig', (_e, config) => wrap(() => smb.setConfig(config), 'setConfig', 'smb'))
  ipcMain.handle('smb:serviceStatus', () => wrap(smb.getServiceStatus, 'serviceStatus', 'LanmanServer'))
  ipcMain.handle('smb:restart', () => wrap(smb.restartService, 'restart', 'LanmanServer'))

  // === preset ===
  ipcMain.handle('preset:list', () => preset.listPresets())
  ipcMain.handle('preset:save', (_e, p) => preset.savePreset(p))
  ipcMain.handle('preset:delete', (_e, id: string) => preset.deletePreset(id))
  ipcMain.handle('preset:apply', (_e, name: string, id: string, mode: 'overwrite' | 'merge') =>
    wrap(() => preset.applyPreset(name, id, mode), 'applyPreset', name)
  )

  // === system ===
  ipcMain.handle('system:currentUser', () => system.getCurrentUser())
  ipcMain.handle('system:isAdmin', () => system.isAdmin())
  ipcMain.handle('system:dashboard', () => system.getDashboardStats())
  ipcMain.handle('system:auditLog', () => system.getAuditLog())
  ipcMain.handle('system:health', () => system.healthCheck())
}
