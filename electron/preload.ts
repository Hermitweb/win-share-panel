import { contextBridge, ipcRenderer } from 'electron'
import type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission,
  LocalUser,
  LocalGroup,
  NtfsAcl,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  ServiceStatus,
  PermissionPreset,
  UserInfo,
  DashboardStats
} from './types'

const api = {
  share: {
    list: (): Promise<Share[]> => ipcRenderer.invoke('share:list'),
    create: (opts: CreateShareOpts): Promise<Share> => ipcRenderer.invoke('share:create', opts),
    update: (name: string, opts: UpdateShareOpts): Promise<Share> => ipcRenderer.invoke('share:update', name, opts),
    delete: (name: string): Promise<void> => ipcRenderer.invoke('share:delete', name),
    toggle: (name: string, enabled: boolean): Promise<void> => ipcRenderer.invoke('share:toggle', name, enabled),
    permissions: (name: string): Promise<SharePermission[]> => ipcRenderer.invoke('share:permissions', name),
    exportConfig: (): Promise<string> => ipcRenderer.invoke('share:export'),
    importConfig: (json: string): Promise<void> => ipcRenderer.invoke('share:import', json)
  },
  user: {
    list: (): Promise<LocalUser[]> => ipcRenderer.invoke('user:list'),
    groups: (): Promise<LocalGroup[]> => ipcRenderer.invoke('user:groups'),
    sharePermissions: (name: string): Promise<SharePermission[]> => ipcRenderer.invoke('user:sharePermissions', name),
    setSharePermissions: (name: string, perms: SharePermission[]): Promise<void> =>
      ipcRenderer.invoke('user:setSharePermissions', name, perms),
    ntfsPermissions: (path: string): Promise<NtfsAcl> => ipcRenderer.invoke('user:ntfsPermissions', path)
  },
  session: {
    list: (): Promise<SmbSession[]> => ipcRenderer.invoke('session:list'),
    files: (): Promise<SmbOpenFile[]> => ipcRenderer.invoke('session:files'),
    close: (clientUserName: string): Promise<void> => ipcRenderer.invoke('session:close', clientUserName),
    closeFile: (fileId: string): Promise<void> => ipcRenderer.invoke('session:closeFile', fileId)
  },
  smb: {
    getConfig: (): Promise<SmbServerConfig> => ipcRenderer.invoke('smb:getConfig'),
    setConfig: (config: Partial<SmbServerConfig>): Promise<void> => ipcRenderer.invoke('smb:setConfig', config),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('smb:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('smb:restart')
  },
  preset: {
    list: (): Promise<PermissionPreset[]> => ipcRenderer.invoke('preset:list'),
    save: (preset: PermissionPreset): Promise<void> => ipcRenderer.invoke('preset:save', preset),
    delete: (id: string): Promise<void> => ipcRenderer.invoke('preset:delete', id),
    apply: (shareName: string, presetId: string, mode: 'overwrite' | 'merge'): Promise<void> =>
      ipcRenderer.invoke('preset:apply', shareName, presetId, mode)
  },
  system: {
    currentUser: (): Promise<UserInfo> => ipcRenderer.invoke('system:currentUser'),
    isAdmin: (): Promise<boolean> => ipcRenderer.invoke('system:isAdmin'),
    dashboard: (): Promise<DashboardStats> => ipcRenderer.invoke('system:dashboard'),
    auditLog: (): Promise<string> => ipcRenderer.invoke('system:auditLog'),
    health: (): Promise<{ ok: boolean; detail: string }> => ipcRenderer.invoke('system:health')
  },
  window: {
    minimize: (): Promise<void> => ipcRenderer.invoke('window:minimize'),
    toggleMaximize: (): Promise<boolean> => ipcRenderer.invoke('window:toggleMaximize'),
    close: (): Promise<void> => ipcRenderer.invoke('window:close'),
    isMaximized: (): Promise<boolean> => ipcRenderer.invoke('window:isMaximized'),
    onMaximizeChange: (cb: (maximized: boolean) => void): void => {
      ipcRenderer.on('window:maximizeChange', (_e, maximized: boolean) => cb(maximized))
    }
  }
}

contextBridge.exposeInMainWorld('winshare', api)

export type WinShareApi = typeof api
