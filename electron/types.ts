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
  // 扩展账号属性
  passwordRequired: boolean
  passwordChangeable: boolean
  passwordExpires: boolean
  userMayChangePassword: boolean
  passwordLastSet: string
  lastLogon: string
  sid: string
  principalSource: 'Local' | 'ActiveDirectory' | 'MicrosoftAccount' | string
}

export interface LocalGroup {
  name: string
  description: string
  members: GroupMember[]
}

export interface GroupMember {
  name: string
  objectClass: 'User' | 'Group'
  principalSource: 'Local' | 'ActiveDirectory' | string
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
  // 扩展配置项
  enableOplocks: boolean
  enableOplockDirectoryCache: boolean
  enableStrictNameChecking: boolean
  enableLeasing: boolean
  enableSMBQUIC: boolean
  enableChannelChange: boolean
  enableSMBDirectoryCache: boolean
  sessionTimeoutSeconds: number
  maxSessionPerConnection: number
  maxMpxCount: number
  maxWorkItems: number
  maxThreadsPerQueue: number
  multipleSessionsPerConnection: boolean
  requestCompression: 'Off' | 'Allow' | 'Require'
  silentAU: boolean
}

// NFS 服务器配置（Get-NfsServerConfiguration 返回字段子集）
export interface NfsServerConfig {
  gracefulUnmount: boolean
  logActivity: boolean
  enableUnmappedAccess: boolean
  enableAuthenticationRenegotiation: boolean
  gatewayCharacterSet: string // 只读
  protocolVersion: string // 只读
  // 连接与超时（best-effort 读取，旧版 Windows 可能无此字段）
  tcpConnectionTimeout: number // 秒，默认 240
  udpConnectionTimeout: number // 秒，默认 240
  restartConnectionTimeout: number // 秒，默认 60
  maxConcurrentConnectionsPerUser: number // 默认 0（无限制）
  directoryCacheExpiry: number // 秒，默认 60（仅 Server 2019+）
  // 身份映射（只读展示）
  anonymousUid: number // 默认 -2
  anonymousGid: number // 默认 -2
}

// FTP 服务器配置（IIS ftpServer/* 配置节，服务器级 MACHINE/WEBROOT/APPHOST）
export interface FtpServerConfig {
  // SSL / 安全
  sslControlChannelPolicy: 'SslAllow' | 'SslRequire' | 'SslRequireCredentials'
  sslDataChannelPolicy: 'SslAllow' | 'SslRequire' | 'SslRequireCredentials'
  sslServerCertHash: string // SHA-1 thumbprint，空字符串表示未配置
  sslClientCertRequired: boolean
  ssl128: boolean // 强制 128 位 SSL
  // 认证
  anonymousEnabled: boolean
  anonymousUserName: string // 默认 IUSR
  basicEnabled: boolean
  // 防火墙（被动数据通道端口范围）
  firewallLowDataChannelPort: number // 0 表示未配置
  firewallHighDataChannelPort: number // 0 表示未配置
  // 消息
  greetingMessage: string
  bannerMessage: string
  exitMessage: string
  maxClientsMessage: string
  suppressDefaultMessages: boolean
  // 目录浏览
  showVirtualDirs: boolean
  // 用户隔离
  userIsolationMode: 'None' | 'StartInUsersDirectory' | 'IsolateUsers' | 'IsolateUsersWithoutAD' | 'ActiveDirectory'
  // 连接超时（秒）
  unauthenticatedTimeout: number
  controlConnectionTimeout: number // 默认 300
  dataChannelConnectionTimeout: number // 默认 30
  // 文件处理
  keepPartialUploads: boolean
  allowReplaceOnRename: boolean
  // 日志
  logFileDirectory: string
  logFilePeriod: 'Hourly' | 'Daily' | 'Weekly' | 'Monthly' | 'MaxSize' | 'Never'
}

// WebDAV 服务器配置（IIS system.webServer/* 配置节，服务器级 MACHINE/WEBROOT/APPHOST）
export interface WebdavServerConfig {
  // WebDAV authoring（全局默认）
  authoringEnabled: boolean
  authoringMaxRequestBodySize: number // 字节数，0 表示不限制
  // 请求筛选
  maxAllowedContentLength: number // 字节数，默认 30000000
  allowDoubleEscaping: boolean
  verifyIntegration: boolean
  // 认证（服务器级默认）
  anonymousEnabled: boolean
  basicEnabled: boolean
  windowsEnabled: boolean
  // 请求限制
  maxUrlLength: number // 默认 260
  maxQueryStringLength: number // 默认 2048
  // 只读信息
  globalAuthoringRulesCount: number
  enableStaticCompression: boolean
  enableDynamicCompression: boolean
  requireSSL: boolean
}

export interface PermissionPreset {
  id: string
  name: string
  description: string
  builtIn: boolean
  entries: PresetEntry[]
  category?: string
  createdAt?: string
  updatedAt?: string
}

export interface PresetEntry {
  account: string
  accountType: 'User' | 'Group'
  access: 'Full' | 'Change' | 'Read'
  deny?: boolean
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
  noAccess?: string[]
  // 扩展：SMB 共享高级选项
  concurrentUserLimit?: number
  cached?: boolean
  encryptData?: boolean
  securityDescriptor?: string
  // 访问枚举与分支缓存
  folderEnumerationMode?: 'AccessBased' | 'Unrestricted'
  cachingMode?: 'None' | 'Manual' | 'Documents' | 'Programs' | 'BranchCache'
  shareShadowCopy?: boolean
}

export interface UpdateShareOpts {
  description?: string
  // 扩展可改字段
  concurrentUserLimit?: number
  cached?: boolean
  folderEnumerationMode?: 'AccessBased' | 'Unrestricted'
  cachingMode?: 'None' | 'Manual' | 'Documents' | 'Programs' | 'BranchCache'
  encryptData?: boolean
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
  noAccess?: string[]
  encryptData?: boolean
  concurrentUserLimit?: number
  cachingMode?: 'None' | 'Manual' | 'Documents' | 'Programs' | 'BranchCache'
  folderEnumerationMode?: 'AccessBased' | 'Unrestricted'
  shareShadowCopy?: boolean
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
