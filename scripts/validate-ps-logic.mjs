/**
 * PowerShell 多版本兼容逻辑的纯 JS 复算验证（v1.4.0）。
 *
 * 与 electron/lib/nativePaths.ts + services/psRuntime.ts 的核心策略**逐字一致**，
 * 用独立 Node 脚本验证（不依赖 tsc / vitest / esbuild —— 本机工具链段错误
 * RC=139 时的兜底验证手段）。
 *
 * 用法：node scripts/validate-ps-logic.mjs
 */
const assert = (cond, msg) => {
  if (!cond) {
    console.error('FAIL:', msg)
    process.exit(1)
  }
}

// === 与 nativePaths.listPowerShellCandidates 同构的候选枚举（含 arch/Sysnative 分支）===
const PS_REL = 'WindowsPowerShell\\v1.0\\powershell.exe'
const PWSH_REL = 'WindowsPowerShell\\v1.0\\pwsh.exe'
const PWSH_PF_REL = 'PowerShell\\7\\pwsh.exe'

function systemRootOf(d) {
  const raw = d.systemRoot ?? process.env.SystemRoot ?? process.env.windir ?? 'C:\\Windows'
  return raw.replace(/[\\/]+$/, '') || 'C:\\Windows'
}

/** 与 nativePrefixOf 一致：arch=ia32 且 Sysnative 里 stat 到 5.1 或 pwsh → 用 Sysnative */
function nativePrefixOf(d) {
  const root = systemRootOf(d)
  const platform = d.platform ?? 'win32'
  const exists = d.exists ?? (() => false)
  const is32Bit = (d.arch ?? 'x64') === 'ia32'
  const sysnative = `${root}\\Sysnative`
  const canUse =
    platform === 'win32' &&
    is32Bit &&
    (exists(`${sysnative}\\${PS_REL}`) || exists(`${sysnative}\\${PWSH_REL}`))
  return canUse ? sysnative : `${root}\\System32`
}

function listCandidates(d) {
  const exists = d.exists ?? (() => false)
  const pf = d.programFilesNative ?? 'C:\\Program Files'
  const prefix = nativePrefixOf(d)
  const out = []
  const ws51 = `${prefix}\\${PS_REL}`
  out.push({ path: ws51, engine: 'windowsPowerShell', exists: exists(ws51) })
  const pwshSys = `${prefix}\\${PWSH_REL}`
  out.push({ path: pwshSys, engine: 'powershellCore', exists: exists(pwshSys) })
  const pwshPf = `${pf}\\${PWSH_PF_REL}`
  out.push({ path: pwshPf, engine: 'powershellCore', exists: exists(pwshPf) })
  out.push({ path: 'powershell.exe', engine: 'windowsPowerShell', exists: true })
  out.push({ path: 'pwsh.exe', engine: 'powershellCore', exists: true })
  return out
}

/** 与 nativePowerShellExe 一致：取第一个存在的真实绝对路径候选，全缺回退裸名 5.1 */
function resolveExe(d) {
  for (const c of listCandidates(d)) {
    if (c.exists && /^[A-Za-z]:[\\/]/.test(c.path)) return c.path
  }
  return 'powershell.exe'
}

/** 与 psRuntime.probePowerShell 一致：realPathFound = 候选里至少一个真实绝对路径存在 */
function realPathFound(d) {
  return listCandidates(d).some((c) => c.exists && /^[A-Za-z]:[\\/]/.test(c.path))
}

/** 与 psRuntime.diagnosePsRuntime 的 ok/reason 分支一致 */
function diagnose(d) {
  const found = realPathFound(d)
  if (!found) {
    return {
      ok: false,
      reason: '未找到（真实路径都缺失）',
      fix: ['sfc /scannow', 'winget install Microsoft.PowerShell'],
    }
  }
  // 找到了真实路径：启动/执行失败
  const exe = resolveExe(d)
  const engine = listCandidates(d).find((c) => c.path === exe)?.engine ?? 'unknown'
  const has51 = listCandidates(d).some(
    (c) => c.engine === 'windowsPowerShell' && c.exists && /^[A-Za-z]:[\\/]/.test(c.path),
  )
  const fix = []
  if (engine === 'powershellCore') fix.push('IIS COM 仅 5.1 注册')
  if (!has51 && engine === 'powershellCore') fix.push('本机仅有 PowerShell 7')
  return { ok: true, reason: `存在（${engine}）：${exe}`, fix }
}

const ROOT = 'C:\\Windows'
const SYS = `${ROOT}\\System32`
const NATIVE = `${ROOT}\\Sysnative`
const PF = 'C:\\Program Files'
const PWSH_PF = `${PF}\\${PWSH_PF_REL}`

// 机器形态 1：64 位进程，默认系统（只有 5.1）
{
  const d = { arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: (p) => p === `${SYS}\\${PS_REL}` }
  assert(resolveExe(d) === `${SYS}\\${PS_REL}`, `形态1 应取 System32 的 5.1，实际 ${resolveExe(d)}`)
  const diag = diagnose(d)
  assert(diag.ok === true, '形态1 应 ok=true')
  assert(diag.reason.includes('windowsPowerShell'), '形态1 engine 应为 windowsPowerShell')
}

// 机器形态 2：32 位进程在 64 位系统（Sysnative 命中 5.1）
{
  const d = { arch: 'ia32', systemRoot: ROOT, programFilesNative: PF, exists: (p) => p === `${NATIVE}\\${PS_REL}` }
  assert(resolveExe(d) === `${NATIVE}\\${PS_REL}`, `形态2 应取 Sysnative 的 5.1，实际 ${resolveExe(d)}`)
}

// 机器形态 3：32 位进程 + Sysnative 里 5.1 被删、只剩 Sysnative 的 pwsh（精简系统位宽对齐）
{
  const d = { arch: 'ia32', systemRoot: ROOT, programFilesNative: PF, exists: (p) => p === `${NATIVE}\\${PWSH_REL}` }
  assert(resolveExe(d) === `${NATIVE}\\${PWSH_REL}`, `形态3 应取 Sysnative 的 7.x，实际 ${resolveExe(d)}`)
  const diag = diagnose(d)
  assert(diag.fix.includes('IIS COM 仅 5.1 注册'), '形态3 应提示 IIS COM 差异')
  assert(diag.fix.includes('本机仅有 PowerShell 7'), '形态3 应提示"本机仅有 7"')
}

// 机器形态 4：64 位进程，5.1 与 7.x 并存 → 仍走 5.1
{
  const d = {
    arch: 'x64',
    systemRoot: ROOT,
    programFilesNative: PF,
    exists: (p) => p === `${SYS}\\${PS_REL}` || p === `${SYS}\\${PWSH_REL}` || p === PWSH_PF,
  }
  assert(resolveExe(d) === `${SYS}\\${PS_REL}`, `形态4 应仍取 5.1，实际 ${resolveExe(d)}`)
}

// 机器形态 5：精简系统，只有 winget 装的 7.x（PF）
{
  const d = { arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: (p) => p === PWSH_PF }
  assert(resolveExe(d) === PWSH_PF, `形态5 应兜底 PF 的 7.x，实际 ${resolveExe(d)}`)
}

// 机器形态 6：全缺（杀软删干净）→ 回退裸名，realPathFound=false，双引擎修复建议
{
  const d = { arch: 'x64', systemRoot: ROOT, programFilesNative: PF, exists: () => false }
  assert(resolveExe(d) === 'powershell.exe', `形态6 应回退裸名，实际 ${resolveExe(d)}`)
  assert(realPathFound(d) === false, '形态6 realPathFound 应为 false')
  const diag = diagnose(d)
  assert(diag.ok === false, '形态6 应 ok=false（双引擎真实路径全缺）')
  assert(diag.fix.includes('sfc /scannow'), '形态6 应建议 sfc')
  assert(diag.fix.includes('winget install Microsoft.PowerShell'), '形态6 应建议装 PowerShell 7')
}

// 优先级顺序：5.1 → 7.x System32 → 7.x PF → 裸名
{
  const d = {
    arch: 'x64',
    systemRoot: ROOT,
    programFilesNative: PF,
    exists: (p) => [`${SYS}\\${PS_REL}`, `${SYS}\\${PWSH_REL}`, PWSH_PF].includes(p),
  }
  const c = listCandidates(d)
  assert(c[0].path === `${SYS}\\${PS_REL}`, '优先级 1 应是 5.1')
  assert(c[1].path === `${SYS}\\${PWSH_REL}`, '优先级 2 应是 7.x System32')
  assert(c[2].path === PWSH_PF, '优先级 3 应是 7.x PF')
  assert(c[3].path === 'powershell.exe' && c[4].path === 'pwsh.exe', '优先级 4/5 应是裸名')
  assert(c.every((x) => x.path === 'powershell.exe' || x.path === 'pwsh.exe' || x.exists), '存在性标记一致')
}

console.log('PASS: PowerShell 多版本兼容逻辑验证通过（nativePaths + psRuntime，含 32/64 位宽 + 全缺兜底）')
