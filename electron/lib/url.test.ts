import { describe, it, expect } from 'vitest'
import { isSafeExternalUrl } from './url'

describe('isSafeExternalUrl（openExternal 的协议白名单）', () => {
  it('放行 http/https（大小写不敏感）', () => {
    expect(isSafeExternalUrl('https://github.com/Hermitweb/win-share-panel/releases/latest')).toBe(
      true,
    )
    expect(isSafeExternalUrl('http://127.0.0.1:8080/x')).toBe(true)
    expect(isSafeExternalUrl('HTTPS://EXAMPLE.COM')).toBe(true)
  })

  it('拒绝非 http/https 协议（这是防 shell 被诱导打开的关键）', () => {
    expect(isSafeExternalUrl('file:///C:/Windows/System32/calc.exe')).toBe(false)
    expect(isSafeExternalUrl('javascript:alert(1)')).toBe(false)
    expect(isSafeExternalUrl('ms-msdt:/id PCWDiagnostic')).toBe(false)
    expect(isSafeExternalUrl('\\\\attacker\\share\\evil.exe')).toBe(false)
    expect(isSafeExternalUrl('C:\\Windows\\System32\\cmd.exe')).toBe(false)
  })

  it('拒绝非字符串与空串', () => {
    expect(isSafeExternalUrl(undefined)).toBe(false)
    expect(isSafeExternalUrl(null)).toBe(false)
    expect(isSafeExternalUrl(42)).toBe(false)
    expect(isSafeExternalUrl({ toString: () => 'https://x' })).toBe(false)
    expect(isSafeExternalUrl('')).toBe(false)
  })

  it('拒绝前导空白（不 trim：空白会改变 shell 解析语义）', () => {
    expect(isSafeExternalUrl(' https://example.com')).toBe(false)
    expect(isSafeExternalUrl('\thttps://example.com')).toBe(false)
  })

  it('拒绝超长 URL（限制长度，避免把巨串交给 shell）', () => {
    expect(isSafeExternalUrl('https://example.com/' + 'a'.repeat(2000))).toBe(false)
    expect(isSafeExternalUrl('https://example.com/' + 'a'.repeat(100))).toBe(true)
  })
})
