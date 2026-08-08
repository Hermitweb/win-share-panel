import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

// vi.hoisted 确保 mock 变量在 vi.mock 工厂函数（被提升到文件顶部）执行时可用
const { mockedRunPowerShell, mockedRunPowerShellVoid } = vi.hoisted(() => ({
  mockedRunPowerShell: vi.fn(),
  mockedRunPowerShellVoid: vi.fn()
}))

vi.mock('../../lib/powershell', () => ({
  runPowerShell: mockedRunPowerShell,
  runPowerShellVoid: mockedRunPowerShellVoid
}))

import { detectProtocols, __resetInflightForTesting } from './detect'
import type { ProtocolDetectionResult } from '../../types'

// 构造一个最小合法的检测结果
function makeDetectionResult(): ProtocolDetectionResult {
  return {
    smb: { protocol: 'smb', installed: true, installType: 'builtin', serviceName: 'LanmanServer', serviceStatus: 'Running', installCommand: '', installHint: '' },
    nfs: { protocol: 'nfs', installed: false, installType: 'client-only', serviceName: 'NfsService', serviceStatus: 'Stopped', installCommand: 'cmd', installHint: 'hint' },
    ftp: { protocol: 'ftp', installed: false, installType: 'iis-role', serviceName: 'ftpsvc', serviceStatus: 'Stopped', installCommand: 'cmd', installHint: 'hint' },
    webdav: { protocol: 'webdav', installed: false, installType: 'iis-role', serviceName: 'W3SVC', serviceStatus: 'Stopped', installCommand: 'cmd', installHint: 'hint' }
  }
}

// 设置 runPowerShell mock，使 doDetectProtocols 正常返回
// doDetectProtocols 调用 4 次 runPowerShell（通过 3 组 Promise.all 并行）：
//   1. isWindowsServer → 命令含 'Win32_OperatingSystem'
//   2. detectFtpState → 命令含 'FtpRoleInstalled'（唯一标识）
//   3. detectModules 内的 runPowerShell → 命令含 'Get-WebConfigurationProperty'
//   4. detectServices → 命令含 'ForEach-Object'
// 注意：不能用 'ftpsvc' + 'W3SVC' 匹配 detectFtpState，因为 detectServices 命令也含这两个词
function setupSuccessfulMock() {
  mockedRunPowerShell.mockImplementation((cmd: string) => {
    if (cmd.includes('Win32_OperatingSystem')) return Promise.resolve('Windows 10 Pro')
    if (cmd.includes('FtpRoleInstalled')) return Promise.resolve({ Installed: false, IisInstalled: false, FtpRoleInstalled: false })
    if (cmd.includes('Get-WebConfigurationProperty')) return Promise.resolve({ Nfs: false, Webdav: false })
    if (cmd.includes('ForEach-Object')) return Promise.resolve([{ Name: 'LanmanServer', Status: 4, StartType: 2 }])
    return Promise.resolve(null)
  })
}

describe('detectProtocols - 并发去重与竞态条件', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    // 重置模块级 in-flight promise，防止上一个测试的未完成检测泄漏
    __resetInflightForTesting()
  })

  afterEach(() => {
    // 重置 mock 默认实现，防止上一个测试的 mockRejectedValue/mockImplementation 泄漏
    mockedRunPowerShell.mockReset()
    mockedRunPowerShellVoid.mockReset()
  })

  it('并发调用共享同一个 in-flight promise（仅执行一次检测）', async () => {
    setupSuccessfulMock()

    // 同时发起 3 个并发调用（模拟 HealthBar + Dashboard + Banner 同时挂载）
    const [r1, r2, r3] = await Promise.all([
      detectProtocols(),
      detectProtocols(),
      detectProtocols()
    ])

    // 三个调用返回相同结果
    expect(r1).toEqual(r2)
    expect(r2).toEqual(r3)
    expect(r1.smb.installed).toBe(true)

    // 关键断言：仅执行 1 次检测（4 次 runPowerShell 调用），而非 3 次（12 次）
    // doDetectProtocols 内部：isWindowsServer(1) + detectFtpState(1) + detectModules.runPowerShell(1) + detectServices(1) = 4
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)
  })

  it('检测完成后 inflightDetect 清空 → 下次调用触发新检测', async () => {
    setupSuccessfulMock()

    // 第一次调用
    await detectProtocols()
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)

    // 第二次调用（前一次已完成，inflightDetect 已被 finally 清空）
    await detectProtocols()
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(8) // 4 + 4
  })

  it('runPowerShell 全部 reject → doDetectProtocols 内部 catch 降级，仍返回有效结果', async () => {
    // doDetectProtocols 内部所有子函数（isWindowsServer/detectModules/detectServices）均有 try/catch，
    // runPowerShell reject 时降级为默认值，detectProtocols 不会抛错
    mockedRunPowerShell.mockRejectedValue(new Error('PowerShell 崩溃'))

    const result = await detectProtocols()

    // 降级结果：SMB 恒为已安装，其他协议未安装
    expect(result.smb.installed).toBe(true)
    expect(result.nfs.installed).toBe(false)
    expect(result.ftp.installed).toBe(false)
    expect(result.webdav.installed).toBe(false)

    // inflightDetect 已被 finally 清空 → 下次调用触发新检测
    setupSuccessfulMock()
    const result2 = await detectProtocols()
    expect(result2.smb.serviceStatus).toBe('Running') // 这次有服务数据
  })

  it('并发调用中一个 await 另一个仍在飞行 → 共享同一个检测', async () => {
    // 用 deferred 控制检测完成时机
    let resolveBarrier!: (v: any) => void
    const barrier = new Promise<any>((res) => { resolveBarrier = res })

    mockedRunPowerShell.mockImplementation((cmd: string) => {
      if (cmd.includes('Win32_OperatingSystem')) return Promise.resolve('Windows 10 Pro')
      if (cmd.includes('FtpRoleInstalled')) return Promise.resolve({ Installed: false, IisInstalled: false, FtpRoleInstalled: false })
      if (cmd.includes('Get-WebConfigurationProperty')) return Promise.resolve({ Nfs: false, Webdav: false })
      if (cmd.includes('ForEach-Object')) return barrier.then(() => [{ Name: 'LanmanServer', Status: 4, StartType: 2 }])
      return Promise.resolve(null)
    })

    // 发起第一次调用（不 await，仍在飞行中）
    const promise1 = detectProtocols()

    // 让微任务刷新，确保第一次调用已启动
    await new Promise((r) => setTimeout(r, 0))

    // 此时检测已在飞行中，runPowerShell 已被调用 4 次
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)

    // 第二次调用：inflightDetect 仍存在 → 应复用同一个 in-flight 检测
    const promise2 = detectProtocols()

    // 关键断言：第二次调用没有触发新的 runPowerShell 调用（仍为 4 次，非 8 次）
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)

    // 释放 barrier，让检测完成
    resolveBarrier([{ Name: 'LanmanServer', Status: 4, StartType: 2 }])

    const [r1, r2] = await Promise.all([promise1, promise2])

    // 两个调用返回相同结果（来自同一次检测）
    expect(r1).toEqual(r2)
    expect(r1.smb.installed).toBe(true)
    expect(r1.smb.serviceStatus).toBe('Running')

    // 最终仅 4 次 runPowerShell（1 次检测），非 8 次
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)
  })

  it('串行快速调用：第一次完成后第二次立即发起 → 两次独立检测', async () => {
    setupSuccessfulMock()

    // 串行调用（第二次在第一次完成后才发起）
    const r1 = await detectProtocols()
    const r2 = await detectProtocols()

    // 两次结果相同（但来自独立检测）
    expect(r1).toEqual(r2)
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(8)
  })

  it('doDetectProtocols 内部三组查询并行执行', async () => {
    const callTimes: number[] = []
    mockedRunPowerShell.mockImplementation((cmd: string) => {
      callTimes.push(Date.now())
      if (cmd.includes('Win32_OperatingSystem')) return Promise.resolve('Windows 10 Pro')
      if (cmd.includes('FtpRoleInstalled')) return Promise.resolve({ Installed: false, IisInstalled: false, FtpRoleInstalled: false })
      if (cmd.includes('Get-WebConfigurationProperty')) return Promise.resolve({ Nfs: false, Webdav: false })
      if (cmd.includes('ForEach-Object')) return Promise.resolve([{ Name: 'LanmanServer', Status: 4, StartType: 2 }])
      return Promise.resolve(null)
    })

    await detectProtocols()

    // 4 次 runPowerShell 调用应几乎同时启动（Promise.all 并行）
    expect(callTimes).toHaveLength(4)
    const maxDiff = Math.max(...callTimes) - Math.min(...callTimes)
    expect(maxDiff).toBeLessThan(10)
  })

  it('5 次并发调用 → 仅 1 次检测，结果一致', async () => {
    setupSuccessfulMock()

    const results = await Promise.all([
      detectProtocols(),
      detectProtocols(),
      detectProtocols(),
      detectProtocols(),
      detectProtocols()
    ])

    // 所有结果一致，SMB 恒为已安装
    for (const r of results) {
      expect(r.smb.installed).toBe(true)
      expect(r.smb.serviceStatus).toBe('Running')
      expect(r.nfs.installed).toBe(false)
    }

    // 仅 4 次 runPowerShell 调用（1 次检测），而非 20 次（5 次检测）
    expect(mockedRunPowerShell).toHaveBeenCalledTimes(4)
  })
})
