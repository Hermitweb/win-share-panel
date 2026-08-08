import { describe, it, expect, vi, beforeEach } from 'vitest'

// Mock runPowerShell / runPowerShellVoid
vi.mock('../../../lib/powershell', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../lib/powershell')>()
  return {
    ...actual,
    runPowerShell: vi.fn(),
    runPowerShellVoid: vi.fn()
  }
})
vi.mock('../../nfs', () => ({
  getServiceStatus: vi.fn(),
  restartService: vi.fn(),
  getConfig: vi.fn(),
  setConfig: vi.fn(),
  defaultConfig: vi.fn(),
  restoreDefault: vi.fn()
}))

import { nfsAdapter } from './nfsAdapter'
import { runPowerShell, runPowerShellVoid } from '../../../lib/powershell'

const mockedRunPowerShell = vi.mocked(runPowerShell)
const mockedRunPowerShellVoid = vi.mocked(runPowerShellVoid)

describe('nfsAdapter', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  describe('createShare - 孤儿共享清理', () => {
    it('New-NfsShare 成功但 Get-NfsShare 失败 → 清理孤儿共享', async () => {
      // New-NfsShare 成功
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Get-NfsShare 失败
      mockedRunPowerShell.mockRejectedValueOnce(new Error('获取共享失败'))
      // 清理 Remove-NfsShare
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // 验证清理 Get-NfsShare
      mockedRunPowerShell.mockResolvedValueOnce(null as any)

      await expect(nfsAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'nfs'
      } as any)).rejects.toThrow()

      // 验证清理命令被调用
      expect(mockedRunPowerShellVoid).toHaveBeenCalledWith(
        expect.stringContaining('Remove-NfsShare'),
        { retries: 0 }
      )
    })

    it('无效共享名 → 抛出参数错误', async () => {
      await expect(nfsAdapter.createShare({
        name: 'test;rm',
        path: 'C:\\share',
        protocol: 'nfs'
      } as any)).rejects.toThrow('共享名非法')
    })

    it('无效路径 → 抛出参数错误', async () => {
      await expect(nfsAdapter.createShare({
        name: 'test',
        path: 'relative/path',
        protocol: 'nfs'
      } as any)).rejects.toThrow('路径非法')
    })
  })

  describe('createShare - 命令注入防护', () => {
    it('非法认证模式被过滤，回退到 sys', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'nfs',
        authentication: ['sys', 'EVIL_MODE; rm -rf']
      } as any)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('-Authentication sys')
      expect(cmd).not.toContain('EVIL_MODE')
      expect(cmd).not.toContain('rm -rf')
    })

    it('非法 nfsPermission 被过滤，回退到 rw', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'nfs',
        nfsPermission: 'EVIL; rm' as any
      } as any)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('-Permission rw')
      expect(cmd).not.toContain('EVIL')
    })

    it('非数字 anonymousUid 被跳过', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test',
        path: 'C:\\share',
        protocol: 'nfs',
        anonymousUid: 'INJECTION' as any
      } as any)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).not.toContain('AnonymousUid')
      expect(cmd).not.toContain('INJECTION')
    })
  })

  describe('setPermissions - 事务回滚', () => {
    it('部分授予失败 → 回滚到备份权限', async () => {
      // getPermissions (备份) → 返回 2 条已有权限
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'existing1', Permission: 'rw', Type: 'Allow' },
        { ClientName: 'existing2', Permission: 'ro', Type: 'Allow' }
      ] as any)
      // Get-NfsSharePermission (清空已有) → 返回已有权限
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'existing1', Permission: 'rw', Type: 'Allow' }
      ] as any)
      // Revoke existing1
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Grant user1 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Grant baduser (失败)
      mockedRunPowerShellVoid.mockRejectedValueOnce(new Error('账号不存在'))
      // getPermissions (回滚前状态)
      mockedRunPowerShell.mockResolvedValueOnce([{ ClientName: 'user1', Permission: 'rw', Type: 'Allow' }] as any)
      // Get-NfsSharePermission (回滚清空)
      mockedRunPowerShell.mockResolvedValueOnce([{ ClientName: 'user1', Permission: 'rw', Type: 'Allow' }] as any)
      // Revoke user1
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Restore backup: Grant existing1
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Restore backup: Grant existing2
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // getPermissions (回滚后状态)
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'existing1', Permission: 'rw', Type: 'Allow' },
        { ClientName: 'existing2', Permission: 'ro', Type: 'Allow' }
      ] as any)

      await expect(nfsAdapter.setPermissions('test', [
        { shareName: 'test', account: 'user1', accountType: 'User', access: 'Full', deny: false },
        { shareName: 'test', account: 'baduser', accountType: 'User', access: 'Read', deny: false }
      ])).rejects.toThrow('部分权限授予失败')

      // 验证回滚时有 Grant 备份权限的调用
      const grantCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Grant-NfsSharePermission')
      )
      // 2 次初始授予（1 成功 + 1 失败）+ 2 次回滚恢复 = 4 次
      expect(grantCalls.length).toBeGreaterThanOrEqual(3)
    })

    it('空权限数组 → 清空所有但不授予新的（合法操作）', async () => {
      // getPermissions (备份) → 返回 1 条
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'old', Permission: 'rw', Type: 'Allow' }
      ] as any)
      // Get-NfsSharePermission (清空) → 返回 1 条
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'old', Permission: 'rw', Type: 'Allow' }
      ] as any)
      // Revoke old
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // 无授予调用（空数组）

      await nfsAdapter.setPermissions('test', [])

      // 没有 Grant 调用
      const grantCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Grant-NfsSharePermission')
      )
      expect(grantCalls).toHaveLength(0)
      // 有 Revoke 调用
      const revokeCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Revoke-NfsSharePermission')
      )
      expect(revokeCalls).toHaveLength(1)
    })

    it('全部 NoAccess 权限 → 清空所有但不授予新的', async () => {
      // getPermissions (备份)
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'old', Permission: 'rw', Type: 'Allow' }
      ] as any)
      // Get-NfsSharePermission (清空)
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'old', Permission: 'rw', Type: 'Allow' }
      ] as any)
      // Revoke old
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await nfsAdapter.setPermissions('test', [
        { shareName: 'test', account: 'user1', accountType: 'User', access: 'NoAccess' as any, deny: false }
      ])

      // NoAccess 被跳过，无 Grant 调用
      const grantCalls = mockedRunPowerShellVoid.mock.calls.filter(
        ([cmd]) => cmd.includes('Grant-NfsSharePermission')
      )
      expect(grantCalls).toHaveLength(0)
    })

    it('全部成功授予 → 无回滚', async () => {
      // getPermissions (备份) → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Get-NfsSharePermission (清空) → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Grant user1 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      // Grant user2 (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await nfsAdapter.setPermissions('test', [
        { shareName: 'test', account: 'user1', accountType: 'User', access: 'Full', deny: false },
        { shareName: 'test', account: 'user2', accountType: 'User', access: 'Read', deny: false }
      ])

      // 无 Revoke 调用（回滚时的 Revoke）
      const allCalls = mockedRunPowerShellVoid.mock.calls
      // 只有 2 次 Grant，无额外 Revoke
      expect(allCalls.filter(([cmd]) => cmd.includes('Revoke'))).toHaveLength(0)
    })
  })

  describe('deleteShare', () => {
    it('无效共享名 → 抛出参数错误', async () => {
      await expect(nfsAdapter.deleteShare('test;rm')).rejects.toThrow('共享名非法')
    })

    it('删除成功', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      await nfsAdapter.deleteShare('test')
      expect(mockedRunPowerShellVoid).toHaveBeenCalledWith(
        expect.stringContaining('Remove-NfsShare -Name')
      )
    })
  })

  describe('updateShare - 边界用例', () => {
    it('无效共享名 → 抛出参数错误', async () => {
      await expect(nfsAdapter.updateShare('test;rm', {} as any)).rejects.toThrow('共享名非法')
    })

    it('非法 nfsPermission 被过滤，不拼入命令', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.updateShare('test', { nfsPermission: 'EVIL; rm' as any })

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      // psEnum 过滤整个非法值，命令中不残留任何注入片段
      expect(cmd).not.toContain('EVIL')
      expect(cmd).not.toContain('; rm')
      // 非法值被 psEnum 过滤后不附加 -Permission
      expect(cmd).not.toContain('-Permission')
    })

    it('非法 allowRootAccess（字符串）被 psBool 过滤', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.updateShare('test', { allowRootAccess: 'INJECTION' as any })

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).not.toContain('INJECTION')
      expect(cmd).not.toContain('-AllowRootAccess')
    })

    it('合法字段 → 拼入命令并返回映射 Share', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({
        Name: 'test', Path: 'C:\\share', Online: true, Permission: 'ro',
        Authentication: 'sys', AnonymousUid: -1, AnonymousGid: -1,
        EnableUnmappedAccess: false, AllowRootAccess: true
      } as any)

      const result = await nfsAdapter.updateShare('test', {
        nfsPermission: 'ro',
        allowRootAccess: true,
        enableUnmappedAccess: false
      } as any)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).toContain('-Permission ro')
      expect(cmd).toContain('-AllowRootAccess $true')
      // psBool(false) = '$false'，是 truthy 字符串，会被拼入
      expect(cmd).toContain('-EnableUnmappedAccess $false')
      expect(result.name).toBe('test')
      expect(result.nfsPermission).toBe('ro')
      expect(result.allowRootAccess).toBe(true)
    })

    it('Set-NfsShare 失败 → 抛出错误，不调用 Get-NfsShare', async () => {
      mockedRunPowerShellVoid.mockRejectedValueOnce(new Error('共享不存在'))

      await expect(nfsAdapter.updateShare('test', { nfsPermission: 'rw' } as any)).rejects.toThrow('共享不存在')
      // Get-NfsShare 不应被调用
      expect(mockedRunPowerShell).not.toHaveBeenCalled()
    })
  })

  describe('getPermissions - 权限映射', () => {
    it('rw → Change, Type=Allow → deny=false', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'client1', Permission: 'rw', Type: 'Allow' }
      ] as any)

      const result = await nfsAdapter.getPermissions('test')
      expect(result).toHaveLength(1)
      expect(result[0].access).toBe('Change')
      expect(result[0].deny).toBe(false)
      expect(result[0].accountType).toBe('Group')
    })

    it('ro → Read, Type=Deny → deny=true', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([
        { ClientName: 'client2', Permission: 'ro', Type: 'Deny' }
      ] as any)

      const result = await nfsAdapter.getPermissions('test')
      expect(result).toHaveLength(1)
      expect(result[0].access).toBe('Read')
      expect(result[0].deny).toBe(true)
    })

    it('空权限列表 → 返回空数组', async () => {
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      const result = await nfsAdapter.getPermissions('test')
      expect(result).toEqual([])
    })

    it('无效共享名 → 抛出参数错误', async () => {
      await expect(nfsAdapter.getPermissions('test;rm')).rejects.toThrow('共享名非法')
    })
  })

  describe('setPermissions - deny 标志映射', () => {
    it('deny=true → Grant 命令包含 -Type Deny', async () => {
      // backup getPermissions → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Get-NfsSharePermission (清空) → 空
      mockedRunPowerShell.mockResolvedValueOnce([] as any)
      // Grant (成功)
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)

      await nfsAdapter.setPermissions('test', [
        { shareName: 'test', account: 'badclient', accountType: 'Group', access: 'Read', deny: true }
      ])

      const grantCmd = mockedRunPowerShellVoid.mock.calls.find(
        ([cmd]) => cmd.includes('Grant-NfsSharePermission')
      )![0]
      expect(grantCmd).toContain('-Type Deny')
    })
  })

  describe('createShare - psBool 布尔字段注入防护', () => {
    it('allowRootAccess=true → 命令包含 -AllowRootAccess $true', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test', path: 'C:\\share', protocol: 'nfs', allowRootAccess: true
      } as any)

      expect(mockedRunPowerShellVoid.mock.calls[0][0]).toContain('-AllowRootAccess $true')
    })

    it('enableUnmappedAccess=false → 命令包含 -EnableUnmappedAccess $false', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test', path: 'C:\\share', protocol: 'nfs', enableUnmappedAccess: false
      } as any)

      // psBool(false)='$false'，是 truthy 字符串，if(uma) 为真，会被拼入
      expect(mockedRunPowerShellVoid.mock.calls[0][0]).toContain('-EnableUnmappedAccess $false')
    })

    it('allowRootAccess=非布尔字符串 → 被 psBool 过滤，不拼入命令', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test', path: 'C:\\share', protocol: 'nfs', allowRootAccess: 'INJECTION' as any
      } as any)

      const cmd = mockedRunPowerShellVoid.mock.calls[0][0]
      expect(cmd).not.toContain('INJECTION')
      expect(cmd).not.toContain('-AllowRootAccess')
    })

    it('anonymousGid=有效数字 → 拼入命令', async () => {
      mockedRunPowerShellVoid.mockResolvedValueOnce(undefined)
      mockedRunPowerShell.mockResolvedValueOnce({ Name: 'test', Path: 'C:\\share', Online: true } as any)

      await nfsAdapter.createShare({
        name: 'test', path: 'C:\\share', protocol: 'nfs', anonymousGid: 1000
      } as any)

      expect(mockedRunPowerShellVoid.mock.calls[0][0]).toContain('-AnonymousGid 1000')
    })
  })
})
