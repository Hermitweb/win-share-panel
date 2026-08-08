import { contextBridge, ipcRenderer } from 'electron'
import type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission,
  LocalUser,
  LocalGroup,
  GroupMember,
  NtfsAcl,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  NfsServerConfig,
  FtpServerConfig,
  WebdavServerConfig,
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
    get: (name: string): Promise<Share> => ipcRenderer.invoke('share:get', name),
    create: (opts: CreateShareOpts): Promise<Share> => ipcRenderer.invoke('share:create', opts),
    update: (name: string, opts: UpdateShareOpts): Promise<Share> => ipcRenderer.invoke('share:update', name, opts),
    delete: (name: string): Promise<void> => ipcRenderer.invoke('share:delete', name),
    toggle: (name: string, enabled: boolean): Promise<void> => ipcRenderer.invoke('share:toggle', name, enabled),
    permissions: (name: string): Promise<SharePermission[]> => ipcRenderer.invoke('share:permissions', name),
    exportConfig: (): Promise<string> => ipcRenderer.invoke('share:export'),
    importConfig: (json: string): Promise<{ imported: number; skipped: number; errors: string[] }> =>
      ipcRenderer.invoke('share:import', json),
    connections: (name: string): Promise<{ concurrentUsers: number; clientConnections: { clientUserName: string; clientComputerName: string; openFiles: number }[] }> =>
      ipcRenderer.invoke('share:connections', name),
    openFiles: (name: string): Promise<{ fileId: number; path: string; clientUserName: string; clientComputerName: string; lockCount: number }[]> =>
      ipcRenderer.invoke('share:openFiles', name),
    closeOpenFiles: (name: string): Promise<{ closed: number; failed: number }> =>
      ipcRenderer.invoke('share:closeOpenFiles', name)
  },
  user: {
    list: (): Promise<LocalUser[]> => ipcRenderer.invoke('user:list'),
    get: (name: string): Promise<LocalUser> => ipcRenderer.invoke('user:get', name),
    groups: (): Promise<LocalGroup[]> => ipcRenderer.invoke('user:groups'),
    sharePermissions: (name: string): Promise<SharePermission[]> => ipcRenderer.invoke('user:sharePermissions', name),
    sharePermissionsForUser: (name: string): Promise<SharePermission[]> => ipcRenderer.invoke('user:sharePermissionsForUser', name),
    setSharePermissions: (name: string, perms: SharePermission[]): Promise<void> =>
      ipcRenderer.invoke('user:setSharePermissions', name, perms),
    ntfsPermissions: (path: string): Promise<NtfsAcl> => ipcRenderer.invoke('user:ntfsPermissions', path),
    create: (opts: {
      name: string
      password: string
      fullName?: string
      description?: string
      enabled?: boolean
      passwordChangeable?: boolean
      passwordExpires?: boolean
    }): Promise<void> => ipcRenderer.invoke('user:create', opts),
    update: (name: string, opts: {
      fullName?: string
      description?: string
      enabled?: boolean
      passwordChangeable?: boolean
      passwordExpires?: boolean
    }): Promise<void> => ipcRenderer.invoke('user:update', name, opts),
    delete: (name: string): Promise<void> => ipcRenderer.invoke('user:delete', name),
    setPassword: (name: string, password: string): Promise<void> =>
      ipcRenderer.invoke('user:setPassword', name, password),
    enable: (name: string): Promise<void> => ipcRenderer.invoke('user:enable', name),
    disable: (name: string): Promise<void> => ipcRenderer.invoke('user:disable', name),
    rename: (oldName: string, newName: string): Promise<void> => ipcRenderer.invoke('user:rename', oldName, newName)
  },
  group: {
    create: (opts: { name: string; description?: string }): Promise<void> =>
      ipcRenderer.invoke('group:create', opts),
    delete: (name: string): Promise<void> => ipcRenderer.invoke('group:delete', name),
    update: (name: string, description: string): Promise<void> =>
      ipcRenderer.invoke('group:update', name, description),
    rename: (oldName: string, newName: string): Promise<void> =>
      ipcRenderer.invoke('group:rename', oldName, newName),
    addMember: (group: string, member: string): Promise<void> =>
      ipcRenderer.invoke('group:addMember', group, member),
    removeMember: (group: string, member: string): Promise<void> =>
      ipcRenderer.invoke('group:removeMember', group, member)
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
    restoreDefault: (): Promise<SmbServerConfig> => ipcRenderer.invoke('smb:restoreDefault'),
    defaultConfig: (): Promise<SmbServerConfig> => ipcRenderer.invoke('smb:defaultConfig'),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('smb:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('smb:restart'),
    start: (): Promise<void> => ipcRenderer.invoke('smb:start'),
    stop: (): Promise<void> => ipcRenderer.invoke('smb:stop'),
    listSnapshots: (): Promise<SmbSnapshotMeta[]> => ipcRenderer.invoke('smb:listSnapshots'),
    rollback: (id: string): Promise<void> => ipcRenderer.invoke('smb:rollback', id)
  },
  preset: {
    list: (): Promise<PermissionPreset[]> => ipcRenderer.invoke('preset:list'),
    get: (id: string): Promise<PermissionPreset | null> => ipcRenderer.invoke('preset:get', id),
    save: (preset: PermissionPreset): Promise<void> => ipcRenderer.invoke('preset:save', preset),
    update: (id: string, updates: Partial<PermissionPreset>): Promise<void> =>
      ipcRenderer.invoke('preset:update', id, updates),
    delete: (id: string): Promise<void> => ipcRenderer.invoke('preset:delete', id),
    duplicate: (id: string, name?: string): Promise<PermissionPreset> =>
      ipcRenderer.invoke('preset:duplicate', id, name),
    apply: (shareName: string, presetId: string, mode: 'overwrite' | 'merge'): Promise<void> =>
      ipcRenderer.invoke('preset:apply', shareName, presetId, mode),
    export: (): Promise<string> => ipcRenderer.invoke('preset:export'),
    import: (json: string): Promise<{ imported: number; skipped: number; errors: string[] }> =>
      ipcRenderer.invoke('preset:import', json)
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
    getConfig: (): Promise<NfsServerConfig> => ipcRenderer.invoke('nfs:getConfig'),
    setConfig: (config: Partial<NfsServerConfig>): Promise<void> => ipcRenderer.invoke('nfs:setConfig', config),
    restoreDefault: (): Promise<NfsServerConfig> => ipcRenderer.invoke('nfs:restoreDefault'),
    defaultConfig: (): Promise<NfsServerConfig> => ipcRenderer.invoke('nfs:defaultConfig'),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('nfs:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('nfs:restart'),
    start: (): Promise<void> => ipcRenderer.invoke('nfs:start'),
    stop: (): Promise<void> => ipcRenderer.invoke('nfs:stop')
  },
  // === FTP 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  ftp: {
    getConfig: (): Promise<FtpServerConfig> => ipcRenderer.invoke('ftp:getConfig'),
    setConfig: (config: Partial<FtpServerConfig>): Promise<void> => ipcRenderer.invoke('ftp:setConfig', config),
    restoreDefault: (): Promise<FtpServerConfig> => ipcRenderer.invoke('ftp:restoreDefault'),
    defaultConfig: (): Promise<FtpServerConfig> => ipcRenderer.invoke('ftp:defaultConfig'),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('ftp:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('ftp:restart'),
    start: (): Promise<void> => ipcRenderer.invoke('ftp:start'),
    stop: (): Promise<void> => ipcRenderer.invoke('ftp:stop')
  },
  // === WebDAV 服务器级配置 + 服务控制（站点级配置经 adapter 路由） ===
  webdav: {
    getConfig: (): Promise<WebdavServerConfig> => ipcRenderer.invoke('webdav:getConfig'),
    setConfig: (config: Partial<WebdavServerConfig>): Promise<void> => ipcRenderer.invoke('webdav:setConfig', config),
    restoreDefault: (): Promise<WebdavServerConfig> => ipcRenderer.invoke('webdav:restoreDefault'),
    defaultConfig: (): Promise<WebdavServerConfig> => ipcRenderer.invoke('webdav:defaultConfig'),
    serviceStatus: (): Promise<ServiceStatus> => ipcRenderer.invoke('webdav:serviceStatus'),
    restart: (): Promise<void> => ipcRenderer.invoke('webdav:restart'),
    start: (): Promise<void> => ipcRenderer.invoke('webdav:start'),
    stop: (): Promise<void> => ipcRenderer.invoke('webdav:stop')
  },
  // === 协议能力探测 + 引导安装 ===
  protocol: {
    detect: (): Promise<ProtocolDetectionResult> => ipcRenderer.invoke('protocol:detect'),
    install: (protocol: Protocol): Promise<void> => ipcRenderer.invoke('protocol:install', protocol)
  }
}

contextBridge.exposeInMainWorld('winshare', api)

export type WinShareApi = typeof api
