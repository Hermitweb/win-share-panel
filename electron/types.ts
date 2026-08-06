// 共享类型定义（主进程与渲染进程共用）

export interface Share {
  name: string
  path: string
  description: string
  type: 'Disk' | 'IPC' | 'Printer' | 'Special'
  hidden: boolean
  concurrentUsers: number
  status: 'Enabled' | 'Disabled'
  cached: boolean
  encrypted: boolean
}

export interface LocalUser {
  name: string
  fullName: string
  enabled: boolean
  description: string
  groups: string[]
}

export interface LocalGroup {
  name: string
  description: string
  members: string[]
}

export interface SharePermission {
  shareName: string
  account: string
  accountType: 'User' | 'Group'
  access: 'Full' | 'Change' | 'Read' | 'NoAccess'
  deny: boolean
}

export interface SmbSession {
  clientId: string
  clientUserName: string
  clientComputerName: string
  sessionStartTime: string
  clientOpenFiles: number
  clientIdleTime: number
  bytesReceived: number
  bytesSent: number
}

export interface SmbOpenFile {
  fileId: string
  path: string
  clientUserName: string
  clientComputerName: string
  lockCount: number
  relativeOpenTime: number
}

export interface SmbServerConfig {
  enableSMB1Protocol: boolean
  enableSMB2Protocol: boolean
  enableSMB3Protocol: boolean
  enableGuestUserAccess: boolean
  enableInsecureGuestLogons: boolean
  auditSmb1Access: boolean
  requireSecuritySignature: boolean
  enableMultiChannel: boolean
  announceServer: boolean
  unauthenticatedUsersTimeLimit: number
}

export interface PermissionPreset {
  id: string
  name: string
  description: string
  builtIn: boolean
  entries: PresetEntry[]
}

export interface PresetEntry {
  account: string
  accountType: 'User' | 'Group'
  access: 'Full' | 'Change' | 'Read'
}

export interface UserInfo {
  username: string
  isAdmin: boolean
}

export interface DashboardStats {
  shareCount: number
  activeSessions: number
  openFiles: number
  serviceStatus: 'Running' | 'Stopped' | 'Unknown'
  topShares: { name: string; connections: number }[]
}

export interface ServiceStatus {
  name: string
  status: 'Running' | 'Stopped' | 'Unknown'
  startType: string
}

export interface NtfsAclEntry {
  account: string
  rights: string
  type: 'Allow' | 'Deny'
  inherited: boolean
}

export interface NtfsAcl {
  path: string
  entries: NtfsAclEntry[]
}

export interface CreateShareOpts {
  name: string
  path: string
  description?: string
  fullAccess?: string[]
  changeAccess?: string[]
  readAccess?: string[]
  encrypted?: boolean
}

export interface UpdateShareOpts {
  description?: string
}
