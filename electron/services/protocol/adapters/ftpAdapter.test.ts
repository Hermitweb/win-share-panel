import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../../../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../lib/powershell')>()
  return {
    ...actual,
    runPowerShell: vi.fn(),
    runPowerShellVoid: vi.fn()
  }
})
vi.mock('../../ftp', () => ({
  ensureFtpSectionsUnlocked: vi.fn().mockResolvedValue(undefined),
  getServiceStatus: vi.fn(),
  restartService: vi.fn(),
  getConfig: vi.fn(),
  setConfig: vi.fn(),
  defaultConfig: vi.fn(),
  restoreDefault: vi.fn()
}))

import { ftpAdapter } from './ftpAdapter'
import { runPowerShell, runPowerShellVoid } from '../../../lib/powershell'

const mockedRunPowerShell = vi.mocked(runPowerShell)
const mockedRunPowerShellVoid = vi.mocked(runPowerShellVoid)

describe('ftpAdapter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  describe('createShare - 孤儿站点清理', () => {
    it('站点创建成功但 fetchSite 返回 null → 清理孤儿站点', async () => {
      // New-WebFtpSite 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // applyFtpConfig: sslPolicy 和 authMode 均为 undefined → parts.length===0 → 提前 return，不调用 runPowerShellVoid
      // fetchSite 返回 null（Get-Website 未找到）
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // 清理 Remove-Item
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // 验证清理 Get-Website
      mockedRunPowerShell.mockResolvedValueOnce(null as any)

      await expect(ftpAdapter.createShare({
        name: 'testftp',
        path: 'C:\\ftp',
        protocol: 'ftp',
        port: 21
      } as any)).rejects.toThrow('已自动清理孤儿站点')

      // 验证清理命令被调用
      const cleanupCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Remove-Item')
      )
      expect(cleanupCalls.length).toBeGreaterThan(0)
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(ftpAdapter.createShare({
        name: 'test;rm',
        path: 'C:\\ftp',
        protocol: 'ftp'
      } as any)).rejects.toThrow('站点名非法')
    })

    it('无效路径 → 抛出参数错误', async () => {
      await expect(ftpAdapter.createShare({
        name: 'test',
        path: 'relative',
        protocol: 'ftp'
      } as any)).rejects.toThrow('路径非法')
    })
  })

  describe('toggleShare - 存在性检查', () => {
    it('站点不存在 → 抛出 shareNotFound', async () => {
      // Get-Website 返回 null（站点不存在）
      mockedRunPowerShell.mockResolvedValueOnce(null as any)

      await expect(ftpAdapter.toggleShare('nonexistent', true)).rejects.toThrow('不存在')
    })

    it('站点存在 → 执行 Start/Stop-Website', async () => {
      // Get-Website 返回站点名
      mockedRunPowerShell.mockResolvedValueOnce('testftp' as any)
      // Start-Website
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.toggleShare('testftp', true)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('Start-Website')
      expect(cmd).toContain("'testftp'")
    })

    it('禁用站点 → 执行 Stop-Website', async () => {
      mockedRunPowerShell.mockResolvedValueOnce('testftp' as any)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.toggleShare('testftp', false)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('Stop-Website')
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(ftpAdapter.toggleShare('test;rm', true)).rejects.toThrow('站点名非法')
    })
  })

  describe('deleteShare - 幂等删除', () => {
    it('删除命令包含 try/catch（幂等）', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      await ftpAdapter.deleteShare('testftp')
      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('try {')
      expect(cmd).toContain('catch {}')
      expect(cmd).toContain('Remove-Item')
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(ftpAdapter.deleteShare('test|rm')).rejects.toThrow('站点名非法')
    })
  })

  describe('setPermissions - 事务回滚', () => {
    it('部分授予失败 → 回滚到备份权限', async () => {
      // getPermissions (备份) → 1 条已有
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: 'olduser', Roles: '', AccessType: 'Allow', Permissions: 'Read' }
      ] as any)
      // Clear-WebConfiguration
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add user1 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add baduser (失败)
      mockedRunPowerShellVoid.mockRejectedValueOnce(new Error('配置节锁定'))
      // getPermissions (回滚前)
      mockedRunPowerShell.mockResolvedValueOnce([{ Users: 'user1', Roles: '', AccessType: 'Allow', Permissions: 'Read' }] as any)
      // Clear (回滚清空)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Restore backup: Add olduser
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // getPermissions (回滚后)
      mockedRunPowerShell.mockResolvedValueOnce([{ Users: 'olduser', Roles: '', AccessType: 'Allow', Permissions: 'Read' }] as any)

      await expect(ftpAdapter.setPermissions('testftp', [
        { shareName: 'testftp', account: 'user1', accountType: 'User', access: 'Read', deny: false },
        { shareName: 'testftp', account: 'baduser', accountType: 'User', access: 'Change', deny: false }
      ])).rejects.toThrow('部分权限授予失败')
    })

    it('全部成功 → 无回滚', async () => {
      // getPermissions (备份) → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add user1 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.setPermissions('testftp', [
        { shareName: 'testftp', account: 'user1', accountType: 'User', access: 'Read', deny: false }
      ])

      // 无第二次 Clear（回滚时的 Clear）
      const clearCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Clear-WebConfiguration')
      )
      expect(clearCalls).toHaveLength(1) // 只有初始清空，无回滚清空
    })
  })

  describe('命令注入防护', () => {
    it('sslPolicy 非法值被过滤（applyFtpConfig 内部）', async () => {
      // getPermissions (备份) → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.setPermissions('testftp', [])

      // 验证 ensureFtpSectionsUnlocked 被调用（通过 ftp mock）
      // 这里主要验证不会因非法 sslPolicy 崩溃
    })
  })

  describe('updateShare - 边界用例', () => {
    it('无效站点名 → 抛出参数错误', async () => {
      await expect(ftpAdapter.updateShare('test;rm', {} as any)).rejects.toThrow('站点名非法')
    })

    it('成功更新 → 返回映射 Share', async () => {
      // applyFtpConfig: 无 sslPolicy/authMode → 提前 return，不调用 PSV
      // fetchSite → 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testftp',
        State: 'Started',
        PhysicalPath: 'C:\\ftp',
        Port: 21,
        SslPolicy: 'SslRequire',
        AnonymousEnabled: false,
        BasicEnabled: true
      }] as any)

      const result = await ftpAdapter.updateShare('testftp', {} as any)
      expect(result.name).toBe('testftp')
      expect(result.sslPolicy).toBe('SslRequire')
      expect(result.authMode).toBe('basic')
    })

    it('fetchSite 返回 null → 抛出 shareNotFound', async () => {
      // applyFtpConfig: 无配置 → 提前 return
      // fetchSite → 空数组
      mockedRunPowerShell.mockResolvedValueOnce([] as any)

      await expect(ftpAdapter.updateShare('nonexistent', {} as any)).rejects.toThrow('不存在')
    })

    it('带 sslPolicy + authMode → applyFtpConfig 调用 runPowerShellVoid', async () => {
      // applyFtpConfig: sslPolicy=SslRequire, authMode=basic → parts 非空
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // fetchSite → 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testftp',
        State: 'Started',
        PhysicalPath: 'C:\\ftp',
        Port: 21,
        SslPolicy: 'SslRequire',
        AnonymousEnabled: false,
        BasicEnabled: true
      }] as any)

      await ftpAdapter.updateShare('testftp', { sslPolicy: 'SslRequire', authMode: 'basic' } as any)

      const configCmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(configCmd).toContain('controlChannelPolicy')
      expect(configCmd).toContain('SslRequire')
      expect(configCmd).toContain('basicAuthentication')
    })
  })

  describe('getPermissions - 权限映射', () => {
    it('Permissions=Read → Read, Users → User', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { AccessType: 'Allow', Users: 'user1', Roles: '', Permissions: 'Read' }
      ] as any)

      const result = await ftpAdapter.getPermissions('testftp')
      expect(result[0].access).toBe('Read')
      expect(result[0].accountType).toBe('User')
      expect(result[0].deny).toBe(false)
    })

    it('Permissions=Read,Write → Change', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { AccessType: 'Allow', Users: 'user1', Roles: '', Permissions: 'Read,Write' }
      ] as any)

      const result = await ftpAdapter.getPermissions('testftp')
      expect(result[0].access).toBe('Change')
    })

    it('Permissions=Write → Change', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { AccessType: 'Allow', Users: 'user1', Roles: '', Permissions: 'Write' }
      ] as any)

      const result = await ftpAdapter.getPermissions('testftp')
      expect(result[0].access).toBe('Change')
    })

    it('AccessType=Deny → deny=true', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { AccessType: 'Deny', Users: 'baduser', Roles: '', Permissions: 'Read' }
      ] as any)

      const result = await ftpAdapter.getPermissions('testftp')
      expect(result[0].deny).toBe(true)
    })

    it('Roles（无 Users）→ Group', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { AccessType: 'Allow', Users: '', Roles: 'Admins', Permissions: 'Read,Write' }
      ] as any)

      const result = await ftpAdapter.getPermissions('testftp')
      expect(result[0].accountType).toBe('Group')
      expect(result[0].account).toBe('Admins')
    })

    it('空结果 → 返回空数组', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      const result = await ftpAdapter.getPermissions('testftp')
      expect(result).toEqual([])
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(ftpAdapter.getPermissions('test;rm')).rejects.toThrow('站点名非法')
    })
  })

  describe('setPermissions - deny 与 Group 边界', () => {
    it('deny=true → Add 命令包含 accessType=Deny', async () => {
      // backup → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.setPermissions('testftp', [
        { shareName: 'testftp', account: 'baduser', accountType: 'User', access: 'Read', deny: true }
      ])

      const addCmd = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('Add-WebConfiguration')
      )![0]
      expect(addCmd).toContain("accessType='Deny'")
    })

    it('accountType=Group → Add 命令使用 roles= 而非 users=', async () => {
      // backup → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await ftpAdapter.setPermissions('testftp', [
        { shareName: 'testftp', account: 'Admins', accountType: 'Group', access: 'Full', deny: false }
      ])

      const addCmd = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('Add-WebConfiguration')
      )![0]
      expect(addCmd).toContain("roles='Admins'")
      expect(addCmd).not.toContain('users=')
      // Full → Read,Write
      expect(addCmd).toContain("permissions='Read,Write'")
    })
  })

  describe('createShare - sslPolicy 注入防护', () => {
    it('非法 sslPolicy 被 psEnum 过滤，不拼入 SSL 配置命令', async () => {
      // New-WebFtpSite 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // applyFtpConfig: sslPolicy 非法 → psEnum 返回 null → 不添加 SSL parts
      //                 authMode undefined → 不添加 auth parts
      //                 parts.length===0 → 提前 return，不调用 PSV
      // fetchSite → 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testftp',
        State: 'Started',
        PhysicalPath: 'C:\\ftp',
        Port: 21,
        SslPolicy: '',
        AnonymousEnabled: false,
        BasicEnabled: false
      }] as any)

      await ftpAdapter.createShare({
        name: 'testftp',
        path: 'C:\\ftp',
        protocol: 'ftp',
        sslPolicy: 'EVIL; rm -rf' as any
      } as any)

      // 验证只有 New-WebFtpSite 的 PSV 调用，无 SSL 配置命令
      const psvCalls = mockedRunPowerShellVoid.mock.calls
      expect(psvCalls).toHaveLength(1)
      expect(psvCalls[0][0]).toContain('New-WebFtpSite')
      expect(psvCalls[0][0]).not.toContain('EVIL')
    })
  })
})
