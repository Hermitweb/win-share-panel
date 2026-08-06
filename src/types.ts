import type {
  Share,
  CreateShareOpts,
  UpdateShareOpts,
  SharePermission,
  LocalUser,
  LocalGroup,
  NtfsAcl,
  NtfsAclEntry,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  NfsServerConfig,
  ServiceStatus,
  PermissionPreset,
  UserInfo,
  DashboardStats,
  SmbSnapshot,
  SmbSnapshotMeta,
  Protocol,
  ProtocolSession,
  ProtocolDetectionResult,
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
  NtfsAcl,
  NtfsAclEntry,
  SmbSession,
  SmbOpenFile,
  SmbServerConfig,
  NfsServerConfig,
  ServiceStatus,
  PermissionPreset,
  UserInfo,
  DashboardStats,
  SmbSnapshot,
  SmbSnapshotMeta,
  Protocol,
  ProtocolSession,
  ProtocolDetectionResult,
  ProtocolCapabilities,
  CreateShareInput,
  UpdateShareInput
}

export interface WinShareApi {
  share: {
    list: () => Promise<Share[]>
    create: (opts: CreateShareOpts) => Promise<Share>
    update: (name: string, opts: UpdateShareOpts) => Promise<Share>
    delete: (name: string) => Promise<void>
    toggle: (name: string, enabled: boolean) => Promise<void>
    permissions: (name: string) => Promise<SharePermission[]>
    exportConfig: () => Promise<string>
    importConfig: (json: string) => Promise<{ imported: number; skipped: number; errors: string[] }>
  }
  user: {
    list: () => Promise<LocalUser[]>
    groups: () => Promise<LocalGroup[]>
    sharePermissions: (name: string) => Promise<SharePermission[]>
    setSharePermissions: (name: string, perms: SharePermission[]) => Promise<void>
    ntfsPermissions: (path: string) => Promise<NtfsAcl>
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
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
    listSnapshots: () => Promise<SmbSnapshotMeta[]>
    rollback: (id: string) => Promise<void>
  }
  preset: {
    list: () => Promise<PermissionPreset[]>
    save: (preset: PermissionPreset) => Promise<void>
    delete: (id: string) => Promise<void>
    apply: (shareName: string, presetId: string, mode: 'overwrite' | 'merge') => Promise<void>
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
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
  }
  ftp: {
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
  }
  webdav: {
    serviceStatus: () => Promise<ServiceStatus>
    restart: () => Promise<void>
  }
  protocol: {
    detect: () => Promise<ProtocolDetectionResult>
    install: (protocol: Protocol) => Promise<void>
  }
}
