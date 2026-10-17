import { describe, it, expect, beforeEach } from 'vitest'
import {
  probePowerShell,
  getPowershellStatus,
  diagnosePsRuntime,
  engineLabel,
  resetPsProbeCache,
  __setTestOverrides,
} from './psRuntime'

// 与 nativePaths.test.ts 同一手法：注入 exists/arch 模拟「只有 5.1 / 只有 7.x / 双引擎 / 全缺」
// 四种机器形态，不依赖测试机器的 %SystemRoot% / %ProgramFiles% / process.arch。
const ROOT = 'C:\\Windows'
const SYS = `${ROOT}\\System32`
const PF = 'C:\\Program Files'
const PS_51 = `${SYS}\\WindowsPowerShell\\v1.0\\powershell.exe`
const PWSH_7 = `${SYS}\\WindowsPowerShell\\v1.0\\pwsh.exe`
const PWSH_PF = `${PF}\\PowerShell\\7\\pwsh.exe`

const only51 = (p: string) => p === PS_51
const only7 = (p: string) => p === PWSH_7
const both = (p: string) => p === PS_51 || p === PWSH_7 || p === PWSH_PF
const none = () => false

// 统一 deps：64 位进程（arch='x64'，不命中 Sysnative）+ programFilesNative 注入
const deps = (exists: (p: string) => boolean) => ({
  platform: 'win32' as const,
  arch: 'x64' as const,
  systemRoot: ROOT,
  programFilesNative: PF,
  exists,
})

beforeEach(() => {
  resetPsProbeCache()
  __setTestOverrides(null)
})

describe('probePowerShell：多版本候选 + 引擎识别（不 spawn PowerShell）', () => {
  it('只有 5.1：resolvedPath = 5.1，engine = windowsPowerShell，realPathFound=true', () => {
    const s = probePowerShell(deps(only51))
    expect(s.exists).toBe(true)
    expect(s.realPathFound).toBe(true)
    expect(s.resolvedPath).toBe(PS_51)
    expect(s.engine).toBe('windowsPowerShell')
    // 5 个候选：3 个真实路径 + 2 个裸名
    expect(s.candidates?.length).toBe(5)
    const pwshSys = s.candidates?.find((c) => c.path === PWSH_7)
    expect(pwshSys?.exists).toBe(false)
  })

  it('只有 7.x（System32 视图）：resolvedPath = pwsh，engine = powershellCore', () => {
    const s = probePowerShell(deps(only7))
    expect(s.exists).toBe(true)
    expect(s.realPathFound).toBe(true)
    expect(s.resolvedPath).toBe(PWSH_7)
    expect(s.engine).toBe('powershellCore')
  })

  it('5.1 与 7.x 都在：优先 5.1（不冒险切引擎）', () => {
    const s = probePowerShell(deps(both))
    expect(s.resolvedPath).toBe(PS_51)
    expect(s.engine).toBe('windowsPowerShell')
    expect(s.realPathFound).toBe(true)
  })

  it('全都不存在（精简系统）：回退裸名 5.1，realPathFound=false，候选全标记不存在', () => {
    const s = probePowerShell(deps(none))
    expect(s.resolvedPath).toBe('powershell.exe')
    expect(s.exists).toBe(true) // 裸名不 stat，乐观
    expect(s.realPathFound).toBe(false) // 没有任何真实绝对路径存在
    const absolute = s.candidates?.filter((c) => /^[A-Za-z]:[\\/]/.test(c.path)) ?? []
    expect(absolute.length).toBe(3) // 5.1 / 7.x System32 / 7.x PF 三个真实路径
    for (const c of absolute) expect(c.exists).toBe(false)
  })

  it('engineLabel：5.1 → "Windows PowerShell 5.1"，7.x → "PowerShell 7+（pwsh）"', () => {
    const s51 = probePowerShell(deps(only51))
    expect(engineLabel(s51)).toBe('Windows PowerShell 5.1')
    resetPsProbeCache()
    const s7 = probePowerShell(deps(only7))
    expect(engineLabel(s7)).toBe('PowerShell 7+（pwsh）')
  })

  it('缓存命中：force=false 时不重新 stat', () => {
    const first = probePowerShell(deps(only51))
    const hit = getPowershellStatus()
    expect(hit.probedAt).toBe(first.probedAt)
  })
})

describe('diagnosePsRuntime：失败建议（多版本兼容）', () => {
  it('两个引擎的真实路径都缺：realPathFound=false，ok=false，建议 sfc + 安装 PowerShell 7', () => {
    const d = diagnosePsRuntime('ENOENT', deps(none))
    expect(d.ok).toBe(false) // 双引擎真实路径全缺 → ok=false
    expect(d.reason).toMatch(/5\.1 与 7\.x 的真实路径都缺失/)
    expect(d.suggestions.join('|')).toMatch(/sfc \/scannow/)
    expect(d.suggestions.join('|')).toMatch(/winget install Microsoft\.PowerShell/)
  })

  it('7.x 引擎 + IIS 相关 cmdlet 失败：提示 5.1 才有 IIS COM', () => {
    const d = diagnosePsRuntime('IIS 模块未注册 0x80040154', deps(only7))
    expect(d.ok).toBe(true)
    expect(d.reason).toMatch(/PowerShell 7\+/)
    expect(d.suggestions.join('|')).toMatch(/IIS|5\.1/)
  })

  it('5.1 引擎 + 0x80070003（杀软拦截）：建议白名单放行', () => {
    const d = diagnosePsRuntime('spawn ENOENT 0x80070003', deps(only51))
    expect(d.ok).toBe(true)
    expect(d.suggestions.join('|')).toMatch(/杀软|白名单|0x80070003/)
  })

  it('拒绝访问：建议以管理员运行', () => {
    const d = diagnosePsRuntime('access is denied', deps(only51))
    expect(d.suggestions.join('|')).toMatch(/管理员/)
  })

  it('CLIXML 执行失败：建议 sfc（区分 5.1 / 7.x 的 .NET 损坏）', () => {
    const d = diagnosePsRuntime('CLIXML <Objs>', deps(only51))
    expect(d.suggestions.join('|')).toMatch(/sfc|损坏/)
  })

  it('本机仅有 7.x（无 5.1）：额外提示"核心 SMB 可用，IIS/32 位 cmdlet 可能差异"', () => {
    const d = diagnosePsRuntime('命令执行失败', deps(only7))
    expect(d.suggestions.join('|')).toMatch(/本机仅有 PowerShell 7|核心 SMB/)
  })
})
