import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mkdtempSync } from 'fs'
import { join } from 'path'
import { tmpdir } from 'os'

// 快照写入隔离到临时目录（smb.ts 在 dataDir() 运行时才读取 APPDATA，顶层赋值即可生效）
process.env.APPDATA = mkdtempSync(join(tmpdir(), 'ws-smb-test-'))

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
}))

import { setConfig } from './smb'

// 模拟旧系统（Server 2019 类）：无 EnableSMBQUIC / SilentAU / SessionTimeoutSeconds 属性
const OLD_SYS_PROPS = {
  EnableSMB1Protocol: false,
  EnableSMB2Protocol: true,
  RequireSecuritySignature: true,
  MaxMpxCount: 16,
}

describe('smb.setConfig 版本适配过滤（OS matrix）', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockedRunPowerShellVoid.mockResolvedValue(undefined)
  })

  it('本机不存在的配置属性被跳过，存在的正常下发', async () => {
    mockedRunPowerShell.mockResolvedValue(OLD_SYS_PROPS)
    await setConfig({
      requireSecuritySignature: false,
      enableSMBQUIC: true,
      silentAU: true,
      sessionTimeoutSeconds: 300,
      maxMpxCount: 32,
    })
    const cmd = String(mockedRunPowerShellVoid.mock.calls.at(-1)?.[0])
    expect(cmd).toContain('-RequireSecuritySignature $false')
    expect(cmd).toContain('-MaxMpxCount 32')
    expect(cmd).not.toContain('-EnableSMBQUIC')
    expect(cmd).not.toContain('-SilentAU')
    expect(cmd).not.toContain('-SessionTimeoutSeconds')
  })

  it('新系统（属性齐全）全量下发', async () => {
    mockedRunPowerShell.mockResolvedValue({
      ...OLD_SYS_PROPS,
      EnableSMBQUIC: false,
      SilentAU: false,
      SessionTimeoutSeconds: 60,
    })
    await setConfig({ enableSMBQUIC: true, silentAU: true, sessionTimeoutSeconds: 300 })
    const cmd = String(mockedRunPowerShellVoid.mock.calls.at(-1)?.[0])
    expect(cmd).toContain('-EnableSMBQUIC $true')
    expect(cmd).toContain('-SilentAU $true')
    expect(cmd).toContain('-SessionTimeoutSeconds 300')
  })

  it('属性探测失败回退全量下发（错误由命令本身如实上抛）', async () => {
    // 快照调用成功、探测调用抛错 → supported=null → 不过滤
    mockedRunPowerShell
      .mockResolvedValueOnce(OLD_SYS_PROPS)
      .mockRejectedValueOnce(new Error('boom'))
    await setConfig({ enableSMBQUIC: true })
    const cmd = String(mockedRunPowerShellVoid.mock.calls.at(-1)?.[0])
    expect(cmd).toContain('-EnableSMBQUIC $true')
  })

  it('所改字段全部不受支持 → 明确拒绝而非静默空命令', async () => {
    mockedRunPowerShell.mockResolvedValue(OLD_SYS_PROPS)
    await expect(setConfig({ enableSMBQUIC: true, silentAU: true })).rejects.toThrow(
      '未提供任何配置项',
    )
    expect(mockedRunPowerShellVoid).not.toHaveBeenCalled()
  })
})
