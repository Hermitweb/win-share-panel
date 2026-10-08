import { describe, it, expect, vi, beforeEach } from 'vitest'

// B-1/B-2 回归测试（2026-10-08 日志排障发现的"假成功"缺陷）：
// 1) New-SmbShare 错误被吞（历史）→ 现已在进程池/server 前缀层改 EAP=Stop；
//    此处验证服务层：写命令抛错 → 触发孤儿清理并 rethrow；
// 2) 写"成功"但回读为空（假成功窗口）→ createShare 必须报错而非返回垃圾对象。
const { mockedRunPowerShell, mockedRunPowerShellVoid } = vi.hoisted(() => ({
  mockedRunPowerShell: vi.fn(),
  mockedRunPowerShellVoid: vi.fn(),
}))

vi.mock('../lib/powershell', () => ({
  runPowerShell: mockedRunPowerShell,
  runPowerShellVoid: mockedRunPowerShellVoid,
  psQuote: (v: string) => `'${v}'`,
  psBool: (v: unknown) => (typeof v === 'boolean' ? `$${v}` : null),
  psNumber: (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? String(v) : null),
  psEnum: (v: unknown, allowed: ReadonlySet<string>) =>
    typeof v === 'string' && allowed.has(v) ? v : null,
  validateName: (v: string) => !!v && /^[A-Za-z0-9._\u4e00-\u9fa5 -]{1,80}$/.test(v),
  validatePath: (v: string) => /^[A-Za-z]:[\\/]/.test(v),
}))

import { createShare } from './share'

const baseOpts = {
  name: '测试',
  path: 'E:\\测试',
  fullAccess: ['Administrator'],
}

describe('createShare(smb) 假成功防线（B-2）', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    // 缺省实现必须是 Promise（源码链上有 .catch）：void 成功、单值查询返回 null
    mockedRunPowerShellVoid.mockResolvedValue(undefined)
    mockedRunPowerShell.mockResolvedValue(null)
  })

  it('写命令抛错 → 清理孤儿并 rethrow（不再吞错）', async () => {
    mockedRunPowerShellVoid.mockRejectedValueOnce(new Error('命令执行失败：Access is denied'))
    await expect(createShare(baseOpts as never)).rejects.toThrow('Access is denied')
    // 第一个 reject 是 New-SmbShare；其后 createShare 的 catch 会发起清理 Remove
    expect(mockedRunPowerShellVoid).toHaveBeenCalledTimes(2)
  })

  it('写命令"成功"但回读为空 → 报回读失败，不得返回垃圾对象', async () => {
    // runPowerShellVoid 成功；Get-SmbShare 回读空（parseJson 对空输出返回 [] 的旧世界）
    mockedRunPowerShell.mockResolvedValueOnce([]) // 空数组 → raw.Name undefined
    await expect(createShare(baseOpts as never)).rejects.toThrow('未能回读到该共享')
  })

  it('回读 null 同样拒绝', async () => {
    mockedRunPowerShell.mockResolvedValueOnce(null)
    await expect(createShare(baseOpts as never)).rejects.toThrow('未能回读到该共享')
  })

  it('回读正常 → 返回映射后的共享', async () => {
    mockedRunPowerShell.mockResolvedValueOnce({
      Name: '测试',
      Path: 'E:\\测试',
      Description: '',
      ShareType: 0,
      Hidden: false,
      ConcurrentUsers: 0,
      Cached: false,
      Encrypted: false,
      Special: false,
    })
    const share = await createShare(baseOpts as never)
    expect(share.name).toBe('测试')
    expect(share.protocol).toBe('smb')
  })
})
