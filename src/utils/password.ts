// 密码生成 + 强度评估（客户端纯函数，无后端依赖）

export interface PasswordOptions {
  uppercase: boolean
  lowercase: boolean
  digits: boolean
  special: boolean
}

const POOLS: Record<keyof PasswordOptions, string> = {
  uppercase: 'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
  lowercase: 'abcdefghijklmnopqrstuvwxyz',
  digits: '0123456789',
  special: '!@#$%^&*()-_=+[]{}'
}

const DEFAULT_OPTIONS: PasswordOptions = {
  uppercase: true,
  lowercase: true,
  digits: true,
  special: true
}

// 使用 crypto.getRandomValues 生成密码学安全随机数
function secureRandomInt(max: number): number {
  const buf = new Uint32Array(1)
  crypto.getRandomValues(buf)
  return buf[0] % max
}

// 生成随机密码：至少包含每个启用字符集的一个字符，其余随机填充
export function generatePassword(length = 12, opts: Partial<PasswordOptions> = {}): string {
  const options = { ...DEFAULT_OPTIONS, ...opts }
  const enabledPools = (Object.keys(options) as (keyof PasswordOptions)[]).filter((k) => options[k])

  if (enabledPools.length === 0) {
    // 全部禁用时回退到默认
    return generatePassword(length, DEFAULT_OPTIONS)
  }

  const allChars = enabledPools.map((k) => POOLS[k]).join('')
  const chars: string[] = []

  // 保证每个启用字符集至少出现一次
  for (const pool of enabledPools) {
    chars.push(POOLS[pool][secureRandomInt(POOLS[pool].length)])
  }

  // 填充剩余长度
  for (let i = chars.length; i < length; i++) {
    chars.push(allChars[secureRandomInt(allChars.length)])
  }

  // Fisher-Yates 洗牌，避免前 N 位固定为各字符集首字符
  for (let i = chars.length - 1; i > 0; i--) {
    const j = secureRandomInt(i + 1)
    ;[chars[i], chars[j]] = [chars[j], chars[i]]
  }

  return chars.slice(0, length).join('')
}

// 评估密码强度：基于长度 + 字符种类多样性
export type PasswordStrength = 'weak' | 'medium' | 'strong'

export function evaluateStrength(pwd: string): PasswordStrength {
  if (!pwd || pwd.length < 6) return 'weak'

  let variety = 0
  if (/[a-z]/.test(pwd)) variety++
  if (/[A-Z]/.test(pwd)) variety++
  if (/[0-9]/.test(pwd)) variety++
  if (/[^a-zA-Z0-9]/.test(pwd)) variety++

  if (pwd.length >= 12 && variety >= 3) return 'strong'
  if (pwd.length >= 8 && variety >= 2) return 'medium'
  return 'weak'
}

export const STRENGTH_LABEL: Record<PasswordStrength, string> = {
  weak: '弱',
  medium: '中',
  strong: '强'
}

export const STRENGTH_COLOR: Record<PasswordStrength, string> = {
  weak: 'red',
  medium: 'orange',
  strong: 'green'
}
