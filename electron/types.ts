// 共享类型定义（主进程与渲染进程共用）

// 协议类型：SMB（Windows 原生）、NFS（Windows NFS 服务）、FTP（IIS FTP 站点）、WebDAV（IIS WebDAV 扩展）
export type Protocol = 'smb' | 'nfs' | 'ftp' | 'webdav'

// 共享基础接口：所有协议共有字段 + 各协议可选专有字段（兼容现有 SMB 代码）
// SMB 专有字段（type/hidden/encrypted）保留必填；其他协议字段可选
export interface Share {
  name: string
  path: string
  description: string
  protocol: Protocol
  // SMB 专有（SMB 数据必填，其他协议不填）
  type: 'Disk' | 'IPC' | 'Printer' | 'Special'
  hidden: boolean
  encrypted: boolean
  // 通用状态
  concurrentUsers: number
  status: 'Enabled' | 'Disabled'
  cached: boolean
  // NFS 专有（可选）
  networkName?: string
  authentication?: ('krb5' | 'krb5i' | 'krb5p' | 'sys')[]
  nfsPermission?: 'ro' | 'rw'
  anonymousUid?: number
  anonymousGid?: number
  enableUnmappedAccess?: boolean
  allowRootAccess?: boolean
  // FTP 专有（可选）
  port?: number
  siteName?: string
  sslPolicy?: 'SslAllow' | 'SslRequire' | 'SslRequireCredentials'
  authMode?: 'anonymous' | 'basic' | 'windows'
  // WebDAV 专有（可选）
  anonymousEnabled?: boolean
  authoringEnabled?: boolean
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

// NFS 服务器配置（Get-NfsServerConfiguration 返回字段子集）
export interface NfsServerConfig {
  gracefulUnmount: boolean
  logActivity: boolean
  enableUnmappedAccess: boolean
  enableAuthenticationRenegotiation: boolean
  gatewayCharacterSet: string
  protocolVersion: string
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
  topShares: { name: string; connections: number; protocol: Protocol }[]
  byProtocol: Record<Protocol, { shares: number; sessions: number }>
}

export interface ServiceStatus {
  name: string
  status: 'Running' | 'Stopped' | 'Unknown'
  startType: string
}

export interface SmbSnapshot {
  id: string
  ts: string
  config: SmbServerConfig
}

export interface SmbSnapshotMeta {
  id: string
  ts: string
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

// === 多协议扩展类型 ===

// 通用协议会话（覆盖 SMB/NFS；FTP/WebDAV 无原生会话，由 adapter 抛 unsupported）
export interface ProtocolSession {
  protocol: Protocol
  sessionId: string
  clientUserName: string
  clientComputerName: string
  sessionStartTime: string
  clientOpenFiles: number
  clientIdleTime: number
  bytesReceived: number
  bytesSent: number
}

// 协议功能检测结果
export interface ProtocolFeatureState {
  protocol: Protocol
  installed: boolean
  installType: 'server-feature' | 'client-only' | 'iis-role' | 'builtin'
  serviceName: string
  serviceStatus: 'Running' | 'Stopped' | 'Unknown'
  installCommand: string
  installHint: string
}

export type ProtocolDetectionResult = Record<Protocol, ProtocolFeatureState>

// 创建共享的统一输入（各协议字段可选，按 protocol 分支使用）
export interface CreateShareInput {
  protocol: Protocol
  name: string
  path: string
  description?: string
  // SMB
  encrypted?: boolean
  fullAccess?: string[]
  changeAccess?: string[]
  readAccess?: string[]
  // NFS
  authentication?: ('krb5' | 'krb5i' | 'krb5p' | 'sys')[]
  nfsPermission?: 'ro' | 'rw'
  allowRootAccess?: boolean
  enableUnmappedAccess?: boolean
  anonymousUid?: number
  anonymousGid?: number
  // FTP
  port?: number
  sslPolicy?: 'SslAllow' | 'SslRequire' | 'SslRequireCredentials'
  authMode?: 'anonymous' | 'basic' | 'windows'
  // WebDAV
  anonymousEnabled?: boolean
}

export interface UpdateShareInput {
  protocol: Protocol
  description?: string
  // NFS 可改字段
  nfsPermission?: 'ro' | 'rw'
  allowRootAccess?: boolean
  enableUnmappedAccess?: boolean
  // FTP 可改字段
  sslPolicy?: 'SslAllow' | 'SslRequire' | 'SslRequireCredentials'
  authMode?: 'anonymous' | 'basic' | 'windows'
  // WebDAV 可改字段
  anonymousEnabled?: boolean
}

// 协议能力位：adapter 声明支持哪些操作，UI 据此降级
export interface ProtocolCapabilities {
  supportsCreate: boolean
  supportsUpdate: boolean
  supportsDelete: boolean
  supportsToggle: boolean
  supportsPermissions: boolean
  supportsSessions: boolean
  supportsOpenFiles: boolean
  supportsServerConfig: boolean
  supportsRestart: boolean
  permissionModel: 'smb-acl' | 'nfs-krb' | 'iis-auth' | 'webdav-rules'
}
