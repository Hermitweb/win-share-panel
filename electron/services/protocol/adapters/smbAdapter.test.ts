import { describe, it, expect, vi, beforeEach } from 'vitest'

// Mock 依赖模块
vi.mock('../../share', () => ({
  createShare: vi.fn(),
  deleteShare: vi.fn(),
  updateShare: vi.fn(),
  toggleShare: vi.fn(),
  listShares: vi.fn(),
  getSharePermissions: vi.fn()
}))
vi.mock('../../session', () => ({
  listSessions: vi.fn(),
  listOpenFiles: vi.fn(),
  closeSession: vi.fn(),
  closeFile: vi.fn()
}))
vi.mock('../../smb', () => ({
  getServiceStatus: vi.fn(),
  restartService: vi.fn(),
  getConfig: vi.fn(),
  setConfig: vi.fn(),
  defaultConfig: vi.fn(),
  restoreDefault: vi.fn()
}))
vi.mock('../../user', () => ({
  setSharePermissions: vi.fn()
}))

import { smbAdapter } from './smbAdapter'
import * as share from '../../share'
import * as session from '../../session'
import * as user from '../../user'

const mockedShare = vi.mocked(share)
const mockedSession = vi.mocked(session)
const mockedUser = vi.mocked(user)

describe('smbAdapter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  describe('closeSession - sessionId 解析', () => {
    it('标准格式 user@COMPUTER → 提取 user', async () => {
      await smbAdapter.closeSession('admin@WORKSTATION')
      expect(mockedSession.closeSession).toHaveBeenCalledWith('admin')
    })

    it('无 @ 的 sessionId → 原样传递', async () => {
      await smbAdapter.closeSession('admin')
      expect(mockedSession.closeSession).toHaveBeenCalledWith('admin')
    })

    it('UPN 格式 user@domain.com@COMPUTER → 提取 user@domain.com', async () => {
      // 最后一个 @ 是分隔符，前面的是 UPN 用户名
      await smbAdapter.closeSession('user@domain.com@COMPUTER')
      expect(mockedSession.closeSession).toHaveBeenCalledWith('user@domain.com')
    })

    it('空字符串 sessionId → 原样传递', async () => {
      await smbAdapter.closeSession('')
      expect(mockedSession.closeSession).toHaveBeenCalledWith('')
    })

    it('多个 @ 的复杂 sessionId → 取最后一个 @ 前', async () => {
      await smbAdapter.closeSession('a@b@c@d')
      expect(mockedSession.closeSession).toHaveBeenCalledWith('a@b@c')
    })

    it('closeSession 失败时抛出错误', async () => {
      mockedSession.closeSession.mockRejectedValueOnce(new Error('会话不存在'))
      await expect(smbAdapter.closeSession('admin@PC')).rejects.toThrow('会话不存在')
    })
  })

  describe('createShare - 错误传播', () => {
    it('服务层创建成功 → 返回 Share 对象', async () => {
      const mockShare = { name: 'test', path: 'C:\\share', protocol: 'smb' }
      mockedShare.createShare.mockResolvedValueOnce(mockShare as any)
      const result = await smbAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'smb'
      } as any)
      expect(result).toEqual(mockShare)
    })

    it('服务层抛错 → 适配器层重新抛出', async () => {
      mockedShare.createShare.mockRejectedValueOnce(new Error('共享已存在'))
      await expect(smbAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'smb'
      } as any)).rejects.toThrow('共享已存在')
    })
  })

  describe('setPermissions - 错误传播', () => {
    it('user.setSharePermissions 抛错 → 适配器层重新抛出', async () => {
      mockedUser.setSharePermissions.mockRejectedValueOnce(new Error('权限设置失败'))
      await expect(smbAdapter.setPermissions('test', [
        { shareName: 'test', account: 'admin', accountType: 'User', access: 'Full', deny: false }
      ])).rejects.toThrow('权限设置失败')
    })

    it('空权限数组 → 正常调用服务层', async () => {
      mockedUser.setSharePermissions.mockResolvedValueOnce(undefined)
      await smbAdapter.setPermissions('test', [])
      expect(mockedUser.setSharePermissions).toHaveBeenCalledWith('test', [])
    })
  })

  describe('deleteShare - 错误传播', () => {
    it('服务层删除成功', async () => {
      mockedShare.deleteShare.mockResolvedValueOnce(undefined)
      await smbAdapter.deleteShare('test')
      expect(mockedShare.deleteShare).toHaveBeenCalledWith('test')
    })

    it('服务层抛错 → 重新抛出', async () => {
      mockedShare.deleteShare.mockRejectedValueOnce(new Error('受保护的共享'))
      await expect(smbAdapter.deleteShare('ADMIN$')).rejects.toThrow('受保护的共享')
    })
  })

  describe('listSessions - 映射', () => {
    it('正确映射 SmbSession 到 ProtocolSession', async () => {
      mockedSession.listSessions.mockResolvedValueOnce([
        {
          clientId: 'admin@PC',
          clientUserName: 'admin',
          clientComputerName: 'PC',
          sessionStartTime: '2024-01-01',
          clientOpenFiles: 3,
          clientIdleTime: 60,
          bytesReceived: 1024,
          bytesSent: 2048
        }
      ] as any)
      const result = await smbAdapter.listSessions()
      expect(result).toHaveLength(1)
      expect(result[0]).toEqual({
        protocol: 'smb',
        sessionId: 'admin@PC',
        clientUserName: 'admin',
        clientComputerName: 'PC',
        sessionStartTime: '2024-01-01',
        clientOpenFiles: 3,
        clientIdleTime: 60,
        bytesReceived: 1024,
        bytesSent: 2048
      })
    })

    it('空会话列表 → 返回空数组', async () => {
      mockedSession.listSessions.mockResolvedValueOnce([])
      const result = await smbAdapter.listSessions()
      expect(result).toEqual([])
    })
  })

  describe('toggleShare - 错误传播', () => {
    it('服务层成功 → 无异常', async () => {
      mockedShare.toggleShare.mockResolvedValueOnce(undefined)
      await smbAdapter.toggleShare('test', true)
      expect(mockedShare.toggleShare).toHaveBeenCalledWith('test', true)
    })

    it('服务层抛错 → 适配器层重新抛出', async () => {
      mockedShare.toggleShare.mockRejectedValueOnce(new Error('共享不存在'))
      await expect(smbAdapter.toggleShare('test', false)).rejects.toThrow('共享不存在')
    })
  })

  describe('updateShare - 错误传播', () => {
    it('服务层成功 → 返回 Share', async () => {
      const mockShare = { name: 'test', path: 'C:\\share', protocol: 'smb' }
      mockedShare.updateShare.mockResolvedValueOnce(mockShare as any)
      const result = await smbAdapter.updateShare('test', { description: 'new desc' })
      expect(mockedShare.updateShare).toHaveBeenCalledWith('test', { description: 'new desc' })
      expect(result).toEqual(mockShare)
    })

    it('服务层抛错 → 适配器层重新抛出', async () => {
      mockedShare.updateShare.mockRejectedValueOnce(new Error('共享不存在'))
      await expect(smbAdapter.updateShare('test', { description: 'x' })).rejects.toThrow('共享不存在')
    })
  })

  describe('closeFile - 错误传播', () => {
    it('服务层成功 → 无异常', async () => {
      mockedSession.closeFile.mockResolvedValueOnce(undefined)
      await smbAdapter.closeFile('123')
      expect(mockedSession.closeFile).toHaveBeenCalledWith('123')
    })

    it('服务层抛错 → 适配器层重新抛出', async () => {
      mockedSession.closeFile.mockRejectedValueOnce(new Error('文件已关闭'))
      await expect(smbAdapter.closeFile('123')).rejects.toThrow('文件已关闭')
    })
  })

  describe('listOpenFiles - 映射', () => {
    it('空列表 → 返回空数组', async () => {
      mockedSession.listOpenFiles.mockResolvedValueOnce([])
      const result = await smbAdapter.listOpenFiles()
      expect(result).toEqual([])
    })

    it('非空列表 → 原样透传', async () => {
      const mockFiles = [
        { fileId: '1', path: 'C:\\share\\file1.txt', userName: 'admin' }
      ] as any
      mockedSession.listOpenFiles.mockResolvedValueOnce(mockFiles)
      const result = await smbAdapter.listOpenFiles()
      expect(result).toEqual(mockFiles)
    })
  })
})
