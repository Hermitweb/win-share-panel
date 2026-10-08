import { describe, it, expect, vi } from 'vitest'

// R-1 导入护栏行为测试：share.importConfig / preset.importPresets
// mock PowerShell 层（护栏路径在触达执行层前应全部抛出，mock 仅为安全隔离）
vi.mock('../lib/powershell', () => ({
  runPowerShell: vi.fn(),
  runPowerShellVoid: vi.fn(),
  psQuote: (v: string) => `'${v}'`,
  psEscapeSingle: (v: string) => v.replace(/'/g, "''"),
  psBool: (v: unknown) => (typeof v === 'boolean' ? `$${v}` : null),
  psNumber: (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? String(v) : null),
  psEnum: (v: unknown, allowed: ReadonlySet<string>) =>
    typeof v === 'string' && allowed.has(v) ? v : null,
  validateName: (v: string) => !!v && /^[A-Za-z0-9._\- ]{1,80}$/.test(v),
  validatePath: (v: string) => /^[A-Za-z]:[\\/]/.test(v),
}))

import { importConfig } from './share'
import { importPresets } from './preset'

const OVER_SIZE = 'a'.repeat(2 * 1024 * 1024 + 1) // >2MB 字符

describe('share.importConfig 护栏', () => {
  it('非法 JSON → 友好错误而非裸 SyntaxError', async () => {
    await expect(importConfig('{ not json')).rejects.toThrow('JSON 解析失败')
  })

  it('超过 2MB → 先行拒绝', async () => {
    await expect(importConfig(JSON.stringify({ shares: [OVER_SIZE] }))).rejects.toThrow('2MB 上限')
  })

  it('缺少 shares 字段 → 格式错误', async () => {
    await expect(importConfig(JSON.stringify({ foo: 1 }))).rejects.toThrow('导入文件格式错误')
  })

  it('条目超过 500 → 拒绝分批提示', async () => {
    const big = {
      shares: Array.from({ length: 501 }, () => ({ name: 's', path: 'C:\\s' })),
    }
    await expect(importConfig(JSON.stringify(big))).rejects.toThrow('500 上限')
  })
})

describe('preset.importPresets 护栏', () => {
  it('非法 JSON → 友好错误', async () => {
    await expect(importPresets('[')).rejects.toThrow('JSON 解析失败')
  })

  it('超过 2MB → 先行拒绝', async () => {
    await expect(importPresets(JSON.stringify({ presets: [OVER_SIZE] }))).rejects.toThrow(
      '2MB 上限',
    )
  })

  it('缺少 presets 字段 → 格式错误', async () => {
    await expect(importPresets(JSON.stringify({ x: [] }))).rejects.toThrow('缺少 presets 字段')
  })

  it('条目超过 500 → 拒绝分批提示', async () => {
    const big = {
      presets: Array.from({ length: 501 }, (_, i) => ({ id: `p${i}`, name: `n${i}`, entries: [] })),
    }
    await expect(importPresets(JSON.stringify(big))).rejects.toThrow('500 上限')
  })
})
