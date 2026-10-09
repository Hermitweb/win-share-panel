// 各协议"配置预设"（不同需求场景一键应用）
// values 仅包含对应协议 setConfig 白名单支持写入的字段（只读字段刻意不出现；
// 写入链路：IPC → 服务层 psBool/psNumber/psEnum 运行时守卫 → PowerShell）。
// 与"权限模板"(preset.ts) 是两回事：这里作用于服务器级配置。

export interface ConfigPreset {
  id: string
  name: string
  desc: string
  /** 降级安全性时必填的风险提示，应用前二次确认展示 */
  risk?: string
  values: Record<string, unknown>
}

// ===== SMB 服务器配置预设 =====
export const SMB_CONFIG_PRESETS: ConfigPreset[] = [
  {
    id: 'smb-sec-hardened',
    name: '安全加固（内网服务器）',
    desc: '强制签名、严格名称检查、SMB1 审计、禁访客、不广播存在',
    values: {
      enableSMB1Protocol: false,
      enableSMB2Protocol: true,
      enableSMB3Protocol: true,
      enableGuestUserAccess: false,
      enableInsecureGuestLogons: false,
      auditSmb1Access: true,
      requireSecuritySignature: true,
      enableStrictNameChecking: true,
      announceServer: false,
      unauthenticatedUsersTimeLimit: 30,
      enableLeasing: true,
      enableOplocks: true,
    },
  },
  {
    id: 'smb-perf',
    name: '性能优先（大文件传输）',
    desc: '多通道/租约/机会锁/目录缓存全开，压缩取速度优化，超时放宽',
    values: {
      enableMultiChannel: true,
      enableLeasing: true,
      enableOplocks: true,
      enableOplockDirectoryCache: true,
      enableSMBDirectoryCache: true,
      enableChannelChange: true,
      requestCompression: 'OptimizeForSpeed',
      maxWorkItems: 2048,
      sessionTimeoutSeconds: 300,
      maxSessionPerConnection: 1024,
    },
  },
  {
    id: 'smb-compat',
    name: '兼容优先（老旧客户端/NAS）',
    desc: '放开签名与严格名称检查、允许访客共享，兼容老设备接入',
    risk: '关闭强制签名并允许访客会降低安全性，仅建议在可信内网/排障时段使用',
    values: {
      requireSecuritySignature: false,
      enableStrictNameChecking: false,
      enableGuestUserAccess: true,
      enableInsecureGuestLogons: true,
      requestCompression: 'Off',
    },
  },
  {
    id: 'smb-home',
    name: '家庭局域网便捷',
    desc: '访客可发现、免签名，SMB2/3 保持开启，适合家用 NAS 场景',
    risk: '访客访问对内开放，请确认设备处于可信局域网',
    values: {
      enableSMB1Protocol: false,
      enableSMB2Protocol: true,
      enableSMB3Protocol: true,
      enableGuestUserAccess: true,
      requireSecuritySignature: false,
      announceServer: true,
      enableLeasing: true,
    },
  },
]

// ===== NFS 服务器配置预设 =====
export const NFS_CONFIG_PRESETS: ConfigPreset[] = [
  {
    id: 'nfs-sec',
    name: '安全优先（Kerberos 环境）',
    desc: '关闭未映射访问、开启协商与活动日志、优雅卸载',
    values: {
      enableUnmappedAccess: false,
      enableAuthenticationRenegotiation: true,
      logActivity: true,
      gracefulUnmount: true,
      restartConnectionTimeout: 60,
    },
  },
  {
    id: 'nfs-lan',
    name: '内网便捷（AUTH_SYS）',
    desc: '允许未映射访问、降低日志开销、加大目录缓存',
    risk: '未映射访问以 UID/GID 信任客户端，仅限可信内网',
    values: {
      enableUnmappedAccess: true,
      logActivity: false,
      gracefulUnmount: true,
      directoryCacheExpiry: 120,
    },
  },
]

// ===== FTP 服务器配置预设 =====
export const FTP_CONFIG_PRESETS: ConfigPreset[] = [
  {
    id: 'ftp-secure',
    name: '对外加密分发',
    desc: 'FTPS 强制、禁匿名、用户目录隔离、被动端口段（需防火墙放行）',
    risk: '被动模式端口段 50000-51000 需在防火墙/路由器放行',
    values: {
      sslControlChannelPolicy: 'SslRequire',
      sslDataChannelPolicy: 'SslRequire',
      anonymousEnabled: false,
      basicEnabled: true,
      userIsolationMode: 'StartInUsersDirectory',
      unauthenticatedTimeout: 15,
      firewallLowDataChannelPort: 50000,
      firewallHighDataChannelPort: 51000,
      logFilePeriod: 'Daily',
    },
  },
  {
    id: 'ftp-lan',
    name: '内网便捷传输',
    desc: 'TLS 可选、基本认证、无隔离、保留断点续传碎片',
    risk: 'TLS 可选意味着允许明文连接，仅限可信内网',
    values: {
      sslControlChannelPolicy: 'SslAllow',
      sslDataChannelPolicy: 'SslAllow',
      anonymousEnabled: false,
      basicEnabled: true,
      userIsolationMode: 'None',
      keepPartialUploads: true,
      allowReplaceOnRename: true,
    },
  },
  {
    id: 'ftp-anon-ro',
    name: '匿名下载站',
    desc: '匿名登录 + 基本认证关闭；写入需另行在授权规则限制',
    risk: '任何人可匿名登录，请确保站点目录仅放置可公开内容',
    values: {
      sslControlChannelPolicy: 'SslAllow',
      sslDataChannelPolicy: 'SslAllow',
      anonymousEnabled: true,
      anonymousUserName: 'IUSR',
      basicEnabled: false,
      userIsolationMode: 'None',
      keepPartialUploads: false,
    },
  },
]

// ===== WebDAV 服务器配置预设 =====
export const WEBDAV_CONFIG_PRESETS: ConfigPreset[] = [
  {
    id: 'wd-read-only',
    name: '安全只读发布',
    desc: '关闭 authoring，仅 Windows 认证，集成校验开启',
    values: {
      authoringEnabled: false,
      anonymousEnabled: false,
      basicEnabled: false,
      windowsEnabled: true,
      verifyIntegration: true,
      allowDoubleEscaping: false,
    },
  },
  {
    id: 'wd-collab',
    name: '内网协作写入',
    desc: '开启 authoring 与大请求体（约 286MB），Windows 认证',
    values: {
      authoringEnabled: true,
      anonymousEnabled: false,
      windowsEnabled: true,
      basicEnabled: false,
      maxAllowedContentLength: 300000000,
      authoringMaxRequestBodySize: 300000000,
      verifyIntegration: true,
    },
  },
  {
    id: 'wd-drop',
    name: '匿名投递区（高危）',
    desc: '任何人可上传：匿名 + authoring 开启',
    risk: '匿名上传可被滥用（恶意文件/占满磁盘），务必配合独立目录与配额，建议仅短时开启',
    values: {
      authoringEnabled: true,
      anonymousEnabled: true,
      windowsEnabled: false,
      basicEnabled: false,
      allowDoubleEscaping: false,
    },
  },
]
