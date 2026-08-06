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
} from '../electron/types'

export type {
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
    importConfig: (json: string) => Promise<void>
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
  }
}
