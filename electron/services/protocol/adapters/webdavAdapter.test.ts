import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('../../../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../lib/powershell')>()
  return {
    ...actual,
    runPowerShell: vi.fn(),
    runPowerShellVoid: vi.fn()
  }
})
vi.mock('../../webdav', () => ({
  ensureWebdavSectionsUnlocked: vi.fn().mockResolvedValue(undefined),
  getServiceStatus: vi.fn(),
  restartService: vi.fn(),
  getConfig: vi.fn(),
  setConfig: vi.fn(),
  defaultConfig: vi.fn(),
  restoreDefault: vi.fn()
}))

import { webdavAdapter } from './webdavAdapter'
import { runPowerShell, runPowerShellVoid } from '../../../lib/powershell'

const mockedRunPowerShell = vi.mocked(runPowerShell)
const mockedRunPowerShellVoid = vi.mocked(runPowerShellVoid)

describe('webdavAdapter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  describe('createShare - 孤儿站点清理', () => {
    it('站点创建成功但 fetchSite 返回 null → 清理孤儿站点', async () => {
      // New-Website 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // enableAuthoring: Set + Get 返回 true
      mockedRunPowerShell.mockResolvedValueOnce(true as any)
      // anonymousEnabled 未指定，跳过
      // fetchSite 返回 null
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // 清理 Remove-Website
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // 验证清理 Get-Website
      mockedRunPowerShell.mockResolvedValueOnce(null as any)

      await expect(webdavAdapter.createShare({
        name: 'testwebdav',
        path: 'C:\\webdav',
        protocol: 'webdav',
        port: 80
      } as any)).rejects.toThrow('已自动清理孤儿站点')

      const cleanupCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Remove-Website')
      )
      expect(cleanupCalls.length).toBeGreaterThan(0)
    })

    it('enableAuthoring 失败 → 清理孤儿站点', async () => {
      // New-Website 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // enableAuthoring 返回 false
      mockedRunPowerShell.mockResolvedValueOnce(false as any)
      // 清理 Remove-Website
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await expect(webdavAdapter.createShare({
        name: 'testwebdav',
        path: 'C:\\webdav',
        protocol: 'webdav'
      } as any)).rejects.toThrow('WebDAV authoring 启用失败')
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(webdavAdapter.createShare({
        name: 'test;rm',
        path: 'C:\\webdav',
        protocol: 'webdav'
      } as any)).rejects.toThrow('站点名非法')
    })
  })

  describe('createShare - anonymousEnabled 边界', () => {
    it('anonymousEnabled=true → 配置匿名访问', async () => {
      // New-Website 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // enableAuthoring 返回 true
      mockedRunPowerShell.mockResolvedValueOnce(true as any)
      // Set anonymousAuthentication
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // fetchSite 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testwebdav',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: true
      }] as any)

      const result = await webdavAdapter.createShare({
        name: 'testwebdav',
        path: 'C:\\webdav',
        protocol: 'webdav',
        anonymousEnabled: true
      } as any)

      // 验证 Set-WebConfigurationProperty 被调用
      const setCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Set-WebConfigurationProperty') && cmd.includes('anonymousAuthentication')
      )
      expect(setCalls.length).toBe(1)
      expect(setCalls[0][0]).toContain('$true')
    })

    it('anonymousEnabled=false → 仍执行 Set（禁用匿名访问）', async () => {
      // New-Website 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // enableAuthoring 返回 true
      mockedRunPowerShell.mockResolvedValueOnce(true as any)
      // Set anonymousAuthentication (psBool(false) = '$false', if(anon) 是 truthy)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // fetchSite 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testwebdav',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: false
      }] as any)

      await webdavAdapter.createShare({
        name: 'testwebdav',
        path: 'C:\\webdav',
        protocol: 'webdav',
        anonymousEnabled: false
      } as any)

      // 验证 Set-WebConfigurationProperty 被调用，且值为 $false
      const setCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('anonymousAuthentication')
      )
      expect(setCalls.length).toBe(1)
      expect(setCalls[0][0]).toContain('$false')
    })

    it('anonymousEnabled=undefined → 跳过匿名访问配置', async () => {
      // New-Website 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // enableAuthoring 返回 true
      mockedRunPowerShell.mockResolvedValueOnce(true as any)
      // fetchSite 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'testwebdav',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: false
      }] as any)

      await webdavAdapter.createShare({
        name: 'testwebdav',
        path: 'C:\\webdav',
        protocol: 'webdav'
      } as any)

      // 无 anonymousAuthentication 的 Set 调用
      const setCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('anonymousAuthentication')
      )
      expect(setCalls).toHaveLength(0)
    })
  })

  describe('toggleShare - 存在性检查', () => {
    it('站点不存在 → 抛出 shareNotFound', async () => {
      mockedRunPowerShell.mockResolvedValueOnce(null as any)
      await expect(webdavAdapter.toggleShare('nonexistent', true)).rejects.toThrow('不存在')
    })

    it('站点存在 → 执行 Start-Website', async () => {
      mockedRunPowerShell.mockResolvedValueOnce('test' as any)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      await webdavAdapter.toggleShare('test', true)
      expect(mockedRunPowerShellVoid.mock.calls[0][0]).toContain('Start-Website')
    })

    it('站点存在 → 执行 Stop-Website', async () => {
      mockedRunPowerShell.mockResolvedValueOnce('test' as any)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      await webdavAdapter.toggleShare('test', false)
      expect(mockedRunPowerShellVoid.mock.calls[0][0]).toContain('Stop-Website')
    })
  })

  describe('deleteShare - 幂等删除', () => {
    it('删除命令包含 try/catch（幂等）', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      await webdavAdapter.deleteShare('test')
      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('try {')
      expect(cmd).toContain('Remove-Website')
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(webdavAdapter.deleteShare('test;rm')).rejects.toThrow('站点名非法')
    })
  })

  describe('toggleShare - 无效名验证', () => {
    it('无效站点名 → 抛出参数错误', async () => {
      await expect(webdavAdapter.toggleShare('test;rm', true)).rejects.toThrow('站点名非法')
    })
  })

  describe('setPermissions - 事务回滚', () => {
    it('部分授予失败 → 回滚到备份权限', async () => {
      // getPermissions (备份) → 1 条已有
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: 'olduser', Roles: '', Path: '*', Access: 'Read' }
      ] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add user1 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add baduser (失败)
      mockedRunPowerShellVoid.mockRejectedValueOnce(new Error('配置节锁定'))
      // getPermissions (回滚前)
      mockedRunPowerShell.mockResolvedValueOnce([{ Users: 'user1', Roles: '', Path: '*', Access: 'Read' }] as any)
      // Clear (回滚清空)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Restore backup
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // getPermissions (回滚后)
      mockedRunPowerShell.mockResolvedValueOnce([{ Users: 'olduser', Roles: '', Path: '*', Access: 'Read' }] as any)

      await expect(webdavAdapter.setPermissions('test', [
        { shareName: 'test', account: 'user1', accountType: 'User', access: 'Read', deny: false },
        { shareName: 'test', account: 'baduser', accountType: 'User', access: 'Change', deny: false }
      ])).rejects.toThrow('部分权限授予失败')
    })

    it('全部成功 → 无回滚', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([] as any) // 备份
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined) // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined) // Add user1

      await webdavAdapter.setPermissions('test', [
        { shareName: 'test', account: 'user1', accountType: 'User', access: 'Read', deny: false }
      ])

      const clearCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Clear-WebConfiguration')
      )
      expect(clearCalls).toHaveLength(1)
    })
  })

  describe('updateShare - 边界用例', () => {
    it('无效站点名 → 抛出参数错误', async () => {
      await expect(webdavAdapter.updateShare('test;rm', {} as any)).rejects.toThrow('站点名非法')
    })

    it('anonymousEnabled=true → Set-WebConfigurationProperty 调用 $true', async () => {
      // Set anonymousAuthentication
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // fetchSite → 返回站点
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'test',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: true
      }] as any)

      await webdavAdapter.updateShare('test', { anonymousEnabled: true } as any)

      const setCall = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('anonymousAuthentication')
      )
      expect(setCall).toBeDefined()
      expect(setCall![0]).toContain('$true')
    })

    it('anonymousEnabled=false → Set-WebConfigurationProperty 调用 $false', async () => {
      // psBool(false) = '$false'（truthy 字符串），if(anon) 为真 → 执行 Set
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // fetchSite
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'test',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: false
      }] as any)

      await webdavAdapter.updateShare('test', { anonymousEnabled: false } as any)

      const setCall = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('anonymousAuthentication')
      )
      expect(setCall).toBeDefined()
      expect(setCall![0]).toContain('$false')
    })

    it('anonymousEnabled=undefined → 跳过匿名配置，直接 fetchSite', async () => {
      // 无 PSV 调用，直接 fetchSite
      mockedRunPowerShell.mockResolvedValueOnce([{
        Name: 'test',
        State: 'Started',
        PhysicalPath: 'C:\\webdav',
        Port: 80,
        AuthoringEnabled: true,
        AnonymousEnabled: false
      }] as any)

      await webdavAdapter.updateShare('test', {} as any)

      // 无 anonymousAuthentication 的 Set 调用
      const setCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('anonymousAuthentication')
      )
      expect(setCalls).toHaveLength(0)
    })

    it('fetchSite 返回 null → 抛出 shareNotFound', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([] as any)

      await expect(webdavAdapter.updateShare('nonexistent', {} as any)).rejects.toThrow('不存在')
    })
  })

  describe('getPermissions - 权限映射', () => {
    it('Access=Read → Read', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: 'user1', Roles: '', Path: '*', Access: 'Read' }
      ] as any)

      const result = await webdavAdapter.getPermissions('test')
      expect(result[0].access).toBe('Read')
      expect(result[0].accountType).toBe('User')
    })

    it('Access=Write → Change', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: 'user1', Roles: '', Path: '*', Access: 'Write' }
      ] as any)

      const result = await webdavAdapter.getPermissions('test')
      expect(result[0].access).toBe('Change')
    })

    it('Access=Source → Full', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: 'user1', Roles: '', Path: '*', Access: 'Source' }
      ] as any)

      const result = await webdavAdapter.getPermissions('test')
      expect(result[0].access).toBe('Full')
    })

    it('Roles（无 Users）→ Group', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { Users: '', Roles: 'Admins', Path: '*', Access: 'Read,Write,Source' }
      ] as any)

      const result = await webdavAdapter.getPermissions('test')
      expect(result[0].accountType).toBe('Group')
      expect(result[0].account).toBe('Admins')
      expect(result[0].access).toBe('Full')
    })

    it('空结果 → 返回空数组', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      const result = await webdavAdapter.getPermissions('test')
      expect(result).toEqual([])
    })

    it('无效站点名 → 抛出参数错误', async () => {
      await expect(webdavAdapter.getPermissions('test;rm')).rejects.toThrow('站点名非法')
    })
  })

  describe('setPermissions - Group 与空数组边界', () => {
    it('accountType=Group → Add 命令使用 roles= 而非 users=', async () => {
      // backup → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Add (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await webdavAdapter.setPermissions('test', [
        { shareName: 'test', account: 'Admins', accountType: 'Group', access: 'Full', deny: false }
      ])

      const addCmd = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('Add-WebConfiguration')
      )![0]
      expect(addCmd).toContain("roles='Admins'")
      expect(addCmd).not.toContain('users=')
      // Full → Read,Write,Source
      expect(addCmd).toContain("access='Read,Write,Source'")
    })

    it('空权限数组 → 仅清空，无 Add 调用', async () => {
      // backup → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Clear
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await webdavAdapter.setPermissions('test', [])

      const addCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Add-WebConfiguration')
      )
      expect(addCalls).toHaveLength(0)
      const clearCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Clear-WebConfiguration')
      )
      expect(clearCalls).toHaveLength(1)
    })
  })
})
