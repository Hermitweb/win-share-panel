import type { ProtocolAdapter } from '../ProtocolAdapter'
import type {
  Share,
  SharePermission,
  ServiceStatus,
  ProtocolSession,
  SmbOpenFile,
  SmbServerConfig,
  CreateShareInput,
  UpdateShareInput
} from '../../../types'
import * as share from '../../share'
import * as session from '../../session'
import * as smb from '../../smb'
import * as user from '../../user'

export const smbAdapter: ProtocolAdapter = {
  protocol: 'smb',
  capabilities: {
    supportsCreate: true,
    supportsUpdate: true,
    supportsDelete: true,
    supportsToggle: true,
    supportsPermissions: true,
    supportsSessions: true,
    supportsOpenFiles: true,
    supportsServerConfig: true,
    supportsRestart: true,
    permissionModel: 'smb-acl'
  },

  async listShares(): Promise<Share[]> {
    return share.listShares()
  },

  async createShare(input: CreateShareInput): Promise<Share> {
    return share.createShare({
      name: input.name,
      path: input.path,
      description: input.description,
      fullAccess: input.fullAccess,
      changeAccess: input.changeAccess,
      readAccess: input.readAccess,
      encrypted: input.encrypted
    })
  },

  async deleteShare(name: string): Promise<void> {
    return share.deleteShare(name)
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    return share.updateShare(name, { description: input.description })
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    return share.toggleShare(name, enabled)
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    return share.getSharePermissions(name)
  },

  async setPermissions(name: string, perms: SharePermission[]): Promise<void> {
    return user.setSharePermissions(name, perms)
  },

  async listSessions(): Promise<ProtocolSession[]> {
    const sessions = await session.listSessions()
    return sessions.map((s) => ({
      protocol: 'smb' as const,
      sessionId: s.clientId,
      clientUserName: s.clientUserName,
      clientComputerName: s.clientComputerName,
      sessionStartTime: s.sessionStartTime,
      clientOpenFiles: s.clientOpenFiles,
      clientIdleTime: s.clientIdleTime,
      bytesReceived: s.bytesReceived,
      bytesSent: s.bytesSent
    }))
  },

  async closeSession(sessionId: string): Promise<void> {
    // SMB 的 sessionId 格式为 clientUserName@computerName，closeSession 接收 clientUserName
    // 从 sessionId 反推 clientUserName（取 @ 前部分）
    const clientUserName = sessionId.includes('@') ? sessionId.split('@')[0] : sessionId
    return session.closeSession(clientUserName)
  },

  async listOpenFiles(): Promise<SmbOpenFile[]> {
    return session.listOpenFiles()
  },

  async closeFile(fileId: string): Promise<void> {
    return session.closeFile(fileId)
  },

  async getServiceStatus(): Promise<ServiceStatus> {
    return smb.getServiceStatus()
  },

  async restartService(): Promise<void> {
    return smb.restartService()
  },

  async getConfig(): Promise<SmbServerConfig> {
    return smb.getConfig()
  },

  async setConfig(config: Partial<SmbServerConfig>): Promise<void> {
    return smb.setConfig(config)
  }
}
