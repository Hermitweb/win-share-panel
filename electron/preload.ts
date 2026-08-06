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
  DashboardStats,
  SmbSnapshotMeta,
  Protocol,
  ProtocolSession,
  ProtocolDetectionResult,
  ProtocolCapabilities,
  CreateShareInput,
  UpdateShareInput
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
    importConfig: (json: string): Promise<{ imported: number; skipped: number; errors: string[] }> =>
      ipcRenderer.invoke('share:import', json)
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
    restart: (): Promise<void> => ipcRenderer.invoke('smb:restart'),
    listSnapshots: (): Promise<SmbSnapshotMeta[]> => ipcRenderer.invoke('smb:listSnapshots'),
    rollback: (id: string): Promise<void> => ipcRenderer.invoke('smb:rollback', id)
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
    },
    showBalloon: (title: string, body: string): Promise<void> => ipcRenderer.invoke('window:balloon', title, body)
  },
  // === 多协议扩展：统一协议路由 ===
  adapter: {
    list: (protocol?: Protocol): Promise<Share[]> => ipcRenderer.invoke('adapter:list', protocol),
    create: (input: CreateShareInput): Promise<Share> => ipcRenderer.invoke('adapter:create', input),
    update: (name: string, input: UpdateShareInput): Promise<Share> => ipcRenderer.invoke('adapter:update', name, input),
    delete: (protocol: Protocol, name: string): Promise<void> => ipcRenderer.invoke('adapter:delete', protocol, name),
    toggle: (protocol: Protocol, name: string, enabled: boolean): Promise<void> =>
      ipcRenderer.invoke('adapter:toggle', protocol, name, enabled),
    permissions: (protocol: Protocol, name: string): Promise<SharePermission[]> =>
      ipcRenderer.invoke('adapter:permissions', protocol, name),
    setPermissions: (protocol: Protocol, name: string, perms: SharePermission[]): Promise<void> =>
      ipcRenderer.invoke('adapter:setPermissions', protocol, name, perms),
    sessions: (protocol: Protocol): Promise<ProtocolSession[]> => ipcRenderer.invoke('adapter:sessions', protocol),
    closeSession: (protocol: Protocol, sessionId: string): Promise<void> =>
      ipcRenderer.invoke('adapter:closeSession', protocol, sessionId),
    capabilities: (): Promise<Record<Protocol, ProtocolCapabilities | null>> =>
      ipcRenderer.invoke('adapter:capabilities')
  },
  // === NFS 服务器配置/服务控制 ===
  nfs: {
    getConfig: (): Promise<unknown> => ipcRenderer.invoke('nfs:getConfig'),
    setConfig: (config: unknown): Promise<void> => ipcRenderer.invoke('nfs:setConfig', config),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('nfs:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('nfs:restart')
  },
  // === FTP 服务控制（站点级配置经 adapter 路由） ===
  ftp: {
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('ftp:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('ftp:restart')
  },
  // === WebDAV 服务控制（站点级配置经 adapter 路由） ===
  webdav: {
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('webdav:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('webdav:restart')
  },
  // === 协议能力探测 + 引导安装 ===
  protocol: {
    detect: (): Promise<ProtocolDetectionResult> => ipcRenderer.invoke('protocol:detect'),
    install: (protocol: Protocol): Promise<void> => ipcRenderer.invoke('protocol:install', protocol)
  }
}

contextBridge.exposeInMainWorld('winshare', api)

export type WinShareApi = typeof api
