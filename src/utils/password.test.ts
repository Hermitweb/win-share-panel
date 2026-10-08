import { describe, it, expect } from 'vitest'
import { generatePassword, evaluateStrength, STRENGTH_LABEL, STRENGTH_COLOR } from './password'

describe('generatePassword', () => {
  it('默认生成 12 位', () => {
    expect(generatePassword()).toHaveLength(12)
  })

  it('尊重 length 参数', () => {
    expect(generatePassword(16)).toHaveLength(16)
    expect(generatePassword(40)).toHaveLength(40)
  })

  it('默认选项保证四种字符集各至少出现一次', () => {
    for (let i = 0; i < 10; i++) {
      const pwd = generatePassword(12)
      expect(pwd).toMatch(/[A-Z]/)
      expect(pwd).toMatch(/[a-z]/)
      expect(pwd).toMatch(/[0-9]/)
      expect(pwd).toMatch(/[^a-zA-Z0-9]/)
    }
  })

  it('仅启用 digits 时输出全为数字', () => {
    const pwd = generatePassword(12, {
      uppercase: false,
      lowercase: false,
      digits: true,
      special: false,
    })
    expect(pwd).toMatch(/^[0-9]+$/)
    expect(pwd).toHaveLength(12)
  })

  it('全部禁用时回退默认字符集（不会死循环/空串）', () => {
    const pwd = generatePassword(12, {
      uppercase: false,
      lowercase: false,
      digits: false,
      special: false,
    })
    expect(pwd).toHaveLength(12)
    expect(pwd).toMatch(/[A-Za-z0-9]/)
  })

  it('length 小于启用字符集数时仍返回请求长度', () => {
    expect(generatePassword(2)).toHaveLength(2)
  })

  it('相邻两次生成大概率不同（随机性 sanity）', () => {
    const a = generatePassword(16)
    const b = generatePassword(16)
    // 理论上有极小碰撞概率（>10^-28），不作为 flaky 来源
    expect(a === b).toBe(false)
  })
})

describe('evaluateStrength', () => {
  it('空串/短串为 weak', () => {
    expect(evaluateStrength('')).toBe('weak')
    expect(evaluateStrength('abc')).toBe('weak')
    expect(evaluateStrength('abcdef')).toBe('weak') // <6 边界：恰好 6 位但 variety=1
  })

  it('长度>=8 且 variety>=2 为 medium', () => {
    expect(evaluateStrength('abcdefg1')).toBe('medium') // lowercase+digits
    expect(evaluateStrength('Abcdefg1')).toBe('medium') // variety=3 但长度 8 <12
  })

  it('长度>=12 且 variety>=3 为 strong', () => {
    expect(evaluateStrength('Abcdefghij1!')).toBe('strong')
  })

  it('长而单一字符集仍为 weak（variety=1）', () => {
    expect(evaluateStrength('abcdefghijkl')).toBe('weak')
  })

  it('标签与颜色映射覆盖全部强度等级', () => {
    expect(Object.keys(STRENGTH_LABEL).sort()).toEqual(['medium', 'strong', 'weak'])
    expect(Object.keys(STRENGTH_COLOR).sort()).toEqual(['medium', 'strong', 'weak'])
  })
})
