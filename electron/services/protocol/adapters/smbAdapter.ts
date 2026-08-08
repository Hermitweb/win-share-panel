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
    console.log('[createShare:smb] 适配器接收输入:', {
      name: input.name,
      path: input.path,
      description: input.description,
      encrypted: input.encrypted,
      fullAccess: input.fullAccess,
      changeAccess: input.changeAccess,
      readAccess: input.readAccess,
      noAccess: input.noAccess,
      encryptData: input.encryptData,
      concurrentUserLimit: input.concurrentUserLimit,
      cachingMode: input.cachingMode,
      folderEnumerationMode: input.folderEnumerationMode,
      shareShadowCopy: input.shareShadowCopy
    })
    try {
      const result = await share.createShare({
        name: input.name,
        path: input.path,
        description: input.description,
        fullAccess: input.fullAccess,
        changeAccess: input.changeAccess,
        readAccess: input.readAccess,
        noAccess: input.noAccess,
        encrypted: input.encrypted,
        encryptData: input.encryptData,
        concurrentUserLimit: input.concurrentUserLimit,
        cachingMode: input.cachingMode,
        folderEnumerationMode: input.folderEnumerationMode,
        shareShadowCopy: input.shareShadowCopy
      })
      console.log('[createShare:smb] 共享创建完成:', input.name)
      return result
    } catch (e) {
      console.error('[createShare:smb] 创建失败:', input.name, (e as Error).message)
      throw e
    }
  },

  async deleteShare(name: string): Promise<void> {
    console.log('[deleteShare:smb] 删除共享:', name)
    try {
      await share.deleteShare(name)
      console.log('[deleteShare:smb] 删除成功:', name)
    } catch (e) {
      console.error('[deleteShare:smb] 删除失败:', name, (e as Error).message)
      throw e
    }
  },

  async updateShare(name: string, input: UpdateShareInput): Promise<Share> {
    console.log('[updateShare:smb] 更新共享:', name, { description: input.description })
    try {
      const result = await share.updateShare(name, { description: input.description })
      console.log('[updateShare:smb] 更新成功:', name)
      return result
    } catch (e) {
      console.error('[updateShare:smb] 更新失败:', name, (e as Error).message)
      throw e
    }
  },

  async toggleShare(name: string, enabled: boolean): Promise<void> {
    console.log('[toggleShare:smb] 切换共享状态:', name, '→', enabled ? '启用' : '禁用')
    try {
      await share.toggleShare(name, enabled)
      console.log('[toggleShare:smb] 切换成功:', name, '→', enabled ? '启用' : '禁用')
    } catch (e) {
      console.error('[toggleShare:smb] 切换失败:', name, (e as Error).message)
      throw e
    }
  },

  async getPermissions(name: string): Promise<SharePermission[]> {
    return share.getSharePermissions(name)
  },

  async setPermissions(name: string, perms: SharePermission[]): Promise<void> {
    console.log('[setPermissions:smb] 设置权限:', name, { 权限条数: perms.length, 权限: perms.map(p => `${p.account}=${p.access}${p.deny ? '(deny)' : ''}`) })
    try {
      await user.setSharePermissions(name, perms)
      console.log('[setPermissions:smb] 权限设置成功:', name)
    } catch (e) {
      console.error('[setPermissions:smb] 权限设置失败:', name, (e as Error).message)
      throw e
    }
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
    // 从 sessionId 反推 clientUserName（取最后一个 @ 前部分，兼容 UPN 用户名 user@domain.com）
    const idx = sessionId.lastIndexOf('@')
    const clientUserName = idx >= 0 ? sessionId.slice(0, idx) : sessionId
    console.log('[closeSession:smb] 关闭会话:', sessionId, '→ clientUserName:', clientUserName)
    try {
      await session.closeSession(clientUserName)
      console.log('[closeSession:smb] 会话关闭成功:', sessionId)
    } catch (e) {
      console.error('[closeSession:smb] 会话关闭失败:', sessionId, (e as Error).message)
      throw e
    }
  },

  async listOpenFiles(): Promise<SmbOpenFile[]> {
    return session.listOpenFiles()
  },

  async closeFile(fileId: string): Promise<void> {
    console.log('[closeFile:smb] 关闭文件:', fileId)
    try {
      await session.closeFile(fileId)
      console.log('[closeFile:smb] 文件关闭成功:', fileId)
    } catch (e) {
      console.error('[closeFile:smb] 文件关闭失败:', fileId, (e as Error).message)
      throw e
    }
  },

  async getServiceStatus(): Promise<ServiceStatus> {
    return smb.getServiceStatus()
  },

  async restartService(): Promise<void> {
    console.log('[restartService:smb] 重启 SMB 服务...')
    try {
      await smb.restartService()
      console.log('[restartService:smb] 服务重启成功')
    } catch (e) {
      console.error('[restartService:smb] 服务重启失败:', (e as Error).message)
      throw e
    }
  },

  async getConfig(): Promise<SmbServerConfig> {
    return smb.getConfig()
  },

  async setConfig(config: Partial<SmbServerConfig>): Promise<void> {
    console.log('[setConfig:smb] 设置服务器配置:', Object.keys(config))
    try {
      await smb.setConfig(config)
      console.log('[setConfig:smb] 配置设置成功')
    } catch (e) {
      console.error('[setConfig:smb] 配置设置失败:', (e as Error).message)
      throw e
    }
  },

  defaultConfig(): SmbServerConfig {
    return smb.defaultConfig()
  },

  async restoreDefault(): Promise<SmbServerConfig> {
    return smb.restoreDefault()
  }
}
