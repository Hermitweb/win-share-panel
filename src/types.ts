import type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission,
  LocalUser,
  LocalGroup,
  GroupMember,
  NtfsAcl,
  NtfsAclEntry,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  NfsServerConfig,
  FtpServerConfig,
  WebdavServerConfig,
  ServiceStatus,
  PermissionPreset,
  PresetEntry,
  UserInfo,
  DashboardStats,
  SmbSnapshot,
  SmbSnapshotMeta,
  Protocol,
  ProtocolSession,
  ProtocolDetectionResult,
  ProtocolFeatureState,
  ProtocolCapabilities,
  CreateShareInput,
  UpdateShareInput
} from '../electron/types'

export type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission,
  LocalUser,
  LocalGroup,
  GroupMember,
  NtfsAcl,
  NtfsAclEntry,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  NfsServerConfig,
  FtpServerConfig,
  WebdavServerConfig,
  ServiceStatus,
  PermissionPreset,
  PresetEntry,
  UserInfo,
  DashboardStats,
  SmbSnapshot,
  SmbSnapshotMeta,
  Protocol,
  ProtocolSession,
  ProtocolDetectionResult,
  ProtocolFeatureState,
  ProtocolCapabilities,
  CreateShareInput,
  UpdateShareInput
}

// 共享连接信息
export interface ShareConnections {
  concurrentUsers: number
  clientConnections: { clientUserName: string; clientComputerName: string; openFiles: number }[]
}

export interface ShareOpenFile {
  fileId: number
  path: string
  clientUserName: string
  clientComputerName: string
  lockCount: number
}

// 用户创建/更新参数
export interface CreateUserOpts {
  name: string
  password: string
  fullName?: string
  description?: string
  enabled?: boolean
  passwordChangeable?: boolean
  passwordExpires?: boolean
}

export interface UpdateUserOpts {
  fullName?: string
  description?: string
  enabled?: boolean
  passwordChangeable?: boolean
  passwordExpires?: boolean
}

// 组创建参数
export interface CreateGroupOpts {
  name: string
  description?: string
}

export interface WinShareApi {
  share: {
    list: () => Promise<Share[]>
    get: (name: string) => Promise<Share>
    create: (opts: CreateShareOpts) => Promise<Share>
    update: (name: string, opts: UpdateShareOpts) => Promise<Share>
    delete: (name: string) => Promise<void>
    toggle: (name: string, enabled: boolean) => Promise<void>
    permissions: (name: string) => Promise<SharePermission[]>
    exportConfig: () => Promise<string>
    importConfig: (json: string) => Promise<{ imported: number; skipped: number; errors: string[] }>
    connections: (name: string) => Promise<ShareConnections>
    openFiles: (name: string) => Promise<ShareOpenFile[]>
    closeOpenFiles: (name: string) => Promise<{ closed: number; failed: number }>
  }
  user: {
    list: () => Promise<LocalUser[]>
    get: (name: string) => Promise<LocalUser>
    groups: () => Promise<LocalGroup[]>
    sharePermissions: (name: string) => Promise<SharePermission[]>
    sharePermissionsForUser: (name: string) => Promise<SharePermission[]>
    setSharePermissions: (name: string, perms: SharePermission[]) => Promise<void>
    ntfsPermissions: (path: string) => Promise<NtfsAcl>
    create: (opts: CreateUserOpts) => Promise<void>
    update: (name: string, opts: UpdateUserOpts) => Promise<void>
    delete: (name: string) => Promise<void>
    setPassword: (name: string, password: string) => Promise<void>
    enable: (name: string) => Promise<void>
    disable: (name: string) => Promise<void>
    rename: (oldName: string, newName: string) => Promise<void>
  }
  group: {
    create: (opts: CreateGroupOpts) => Promise<void>
    delete: (name: string) => Promise<void>
    update: (name: string, description: string) => Promise<void>
    rename: (oldName: string, newName: string) => Promise<void>
    addMember: (group: string, member: string) => Promise<void>
    removeMember: (group: string, member: string) => Promise<void>
  }
  session: {
    list: () => Promise<SmbSession[]>
    files: () => Promise<SmbOpenFile[]>
    close: (clientUserName: string) => Promise<void>
    closeFile: (fileId: string) => Promise<void>
  }
  smb: {
    getConfig: () => Promise<SmbServerConfig>
    setConfig: (config: Partial<SmbServerConfig>) => Promise<void>
    restoreDefault: () => Promise<SmbServerConfig>
    defaultConfig: () => Promise<SmbServerConfig>
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
    start: () => Promise<void>
    stop: () => Promise<void>
    listSnapshots: () => Promise<SmbSnapshotMeta[]>
    rollback: (id: string) => Promise<void>
  }
  preset: {
    list: () => Promise<PermissionPreset[]>
    get: (id: string) => Promise<PermissionPreset | null>
    save: (preset: PermissionPreset) => Promise<void>
    update: (id: string, updates: Partial<PermissionPreset>) => Promise<void>
    delete: (id: string) => Promise<void>
    duplicate: (id: string, name?: string) => Promise<PermissionPreset>
    apply: (shareName: string, presetId: string, mode: 'overwrite' | 'merge') => Promise<void>
    export: () => Promise<string>
    import: (json: string) => Promise<{ imported: number; skipped: number; errors: string[] }>
  }
  system: {
    currentUser: () => Promise<UserInfo>
    isAdmin: () => Promise<boolean>
    dashboard: () => Promise<DashboardStats>
    auditLog: () => Promise<string>
    health: () => Promise<{ ok: boolean; detail: string }>
  }
  window: {
    minimize: () => Promise<void>
    toggleMaximize: () => Promise<boolean>
    close: () => Promise<void>
    isMaximized: () => Promise<boolean>
    onMaximizeChange: (cb: (maximized: boolean) => void) => void
    showBalloon: (title: string, body: string) => Promise<void>
  }
  // 多协议扩展
  adapter: {
    list: (protocol?: Protocol) => Promise<Share[]>
    create: (input: CreateShareInput) => Promise<Share>
    update: (name: string, input: UpdateShareInput) => Promise<Share>
    delete: (protocol: Protocol, name: string) => Promise<void>
    toggle: (protocol: Protocol, name: string, enabled: boolean) => Promise<void>
    permissions: (protocol: Protocol, name: string) => Promise<SharePermission[]>
    setPermissions: (protocol: Protocol, name: string, perms: SharePermission[]) => Promise<void>
    sessions: (protocol: Protocol) => Promise<ProtocolSession[]>
    closeSession: (protocol: Protocol, sessionId: string) => Promise<void>
    capabilities: () => Promise<Record<Protocol, ProtocolCapabilities | null>>
  }
  nfs: {
    getConfig: () => Promise<NfsServerConfig>
    setConfig: (config: Partial<NfsServerConfig>) => Promise<void>
    restoreDefault: () => Promise<NfsServerConfig>
    defaultConfig: () => Promise<NfsServerConfig>
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
    start: () => Promise<void>
    stop: () => Promise<void>
  }
  ftp: {
    getConfig: () => Promise<FtpServerConfig>
    setConfig: (config: Partial<FtpServerConfig>) => Promise<void>
    restoreDefault: () => Promise<FtpServerConfig>
    defaultConfig: () => Promise<FtpServerConfig>
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
    start: () => Promise<void>
    stop: () => Promise<void>
  }
  webdav: {
    getConfig: () => Promise<WebdavServerConfig>
    setConfig: (config: Partial<WebdavServerConfig>) => Promise<void>
    restoreDefault: () => Promise<WebdavServerConfig>
    defaultConfig: () => Promise<WebdavServerConfig>
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
    start: () => Promise<void>
    stop: () => Promise<void>
  }
  protocol: {
    detect: () => Promise<ProtocolDetectionResult>
    install: (protocol: Protocol) => Promise<void>
  }
}
