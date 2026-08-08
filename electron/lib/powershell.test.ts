import { describe, it, expect } from 'vitest'
import { psQuote, psEscapeSingle, validateName, validatePath, psBool, psNumber, psEnum } from './powershell'

describe('psQuote', () => {
  it('正常字符串用单引号包裹', () => {
    expect(psQuote('test')).toBe("'test'")
  })

  it('空字符串包裹为空引号', () => {
    expect(psQuote('')).toBe("''")
  })

  it('含单引号的字符串转义为双单引号', () => {
    expect(psQuote("it's")).toBe("'it''s'")
  })

  it('含多个单引号的字符串全部转义', () => {
    expect(psQuote("a'b'c")).toBe("'a''b''c'")
  })

  it('注入尝试：闭合引号 + 注入命令', () => {
    const malicious = "'; Remove-LocalUser -Name 'Administrator"
    const result = psQuote(malicious)
    // 转义后内部单引号变为 ''，无法闭合外层引号
    expect(result).toBe("'''; Remove-LocalUser -Name ''Administrator'")
    expect(result).not.toContain("Administrator' -")
  })

  it('注入尝试：$ 变量插值在单引号中不生效', () => {
    const malicious = '$(Remove-Item C:\\ -Recurse)'
    const result = psQuote(malicious)
    // PS 单引号字符串不转义反斜杠，$ 不会被插值
    expect(result).toBe("'$(Remove-Item C:\\ -Recurse)'")
  })

  it('注入尝试：反引号转义在单引号中不生效', () => {
    const malicious = 'test`n&whoami'
    const result = psQuote(malicious)
    expect(result).toBe("'test`n&whoami'")
  })

  it('中文字符串正常包裹', () => {
    expect(psQuote('测试共享')).toBe("'测试共享'")
  })

  it('数字参数自动转字符串', () => {
    expect(psQuote(42 as unknown as string)).toBe("'42'")
  })
})

describe('psEscapeSingle', () => {
  it('正常字符串不变', () => {
    expect(psEscapeSingle('test')).toBe('test')
  })

  it('含单引号转义为双单引号', () => {
    expect(psEscapeSingle("it's")).toBe("it''s")
  })

  it('用于 IIS 路径构造时不会被注入', () => {
    const malicious = "test'; Remove-Website -Name '"
    const result = psEscapeSingle(malicious)
    expect(result).toBe("test''; Remove-Website -Name ''")
  })
})

describe('validateName', () => {
  it('合法英文名', () => {
    expect(validateName('test')).toBe(true)
    expect(validateName('my_share')).toBe(true)
    expect(validateName('share.1')).toBe(true)
    expect(validateName('share-1')).toBe(true)
  })

  it('合法中文名', () => {
    expect(validateName('测试共享')).toBe(true)
    expect(validateName('共享1')).toBe(true)
  })

  it('含空格合法', () => {
    expect(validateName('my share')).toBe(true)
  })

  it('空字符串非法', () => {
    expect(validateName('')).toBe(false)
  })

  it('null/undefined 非法', () => {
    expect(validateName(null as unknown as string)).toBe(false)
    expect(validateName(undefined as unknown as string)).toBe(false)
  })

  it('超长名称非法（>80字符）', () => {
    expect(validateName('a'.repeat(81))).toBe(false)
    expect(validateName('a'.repeat(80))).toBe(true)
  })

  it('含 $ 非法（防 PowerShell 变量插值）', () => {
    expect(validateName('test$')).toBe(false)
    expect(validateName('te$st')).toBe(false)
  })

  it('含分号非法（防命令分隔）', () => {
    expect(validateName('test;rm')).toBe(false)
  })

  it('含管道符非法', () => {
    expect(validateName('test|cmd')).toBe(false)
  })

  it('含反斜杠非法', () => {
    expect(validateName('test\\share')).toBe(false)
  })

  it('含特殊字符非法', () => {
    expect(validateName('test<>')).toBe(false)
    expect(validateName('test"')).toBe(false)
    expect(validateName("test'")).toBe(false)
  })
})

describe('validatePath', () => {
  it('合法 Windows 绝对路径', () => {
    expect(validatePath('C:\\share')).toBe(true)
    expect(validatePath('D:\\data\\folder')).toBe(true)
    expect(validatePath('C:/share')).toBe(true)
  })

  it('相对路径非法', () => {
    expect(validatePath('relative/path')).toBe(false)
    expect(validatePath('./share')).toBe(false)
  })

  it('Unix 路径非法', () => {
    expect(validatePath('/home/user')).toBe(false)
  })

  it('含控制字符非法（首位）', () => {
    // 正则仅对路径段首字符排除控制字符，中间字符不排除——这是已知限制
    expect(validatePath('C:\\\x00share')).toBe(false)
  })

  it('含非法字符非法', () => {
    expect(validatePath('C:\\share<"test"')).toBe(false)
    expect(validatePath('C:\\share|pipe')).toBe(false)
    expect(validatePath('C:\\share?query')).toBe(false)
    expect(validatePath('C:\\share*star')).toBe(false)
  })

  it('空字符串非法', () => {
    expect(validatePath('')).toBe(false)
  })
})

describe('psBool', () => {
  it('true 返回 $true', () => {
    expect(psBool(true)).toBe('$true')
  })

  it('false 返回 $false', () => {
    expect(psBool(false)).toBe('$false')
  })

  it('字符串 "true" 返回 null（非布尔）', () => {
    expect(psBool('true')).toBeNull()
  })

  it('数字 1 返回 null（非布尔）', () => {
    expect(psBool(1)).toBeNull()
  })

  it('数字 0 返回 null（非布尔）', () => {
    expect(psBool(0)).toBeNull()
  })

  it('undefined 返回 null', () => {
    expect(psBool(undefined)).toBeNull()
  })

  it('null 返回 null', () => {
    expect(psBool(null)).toBeNull()
  })

  it('注入字符串 "$true; rm -rf" 返回 null', () => {
    expect(psBool('$true; rm -rf')).toBeNull()
  })
})

describe('psNumber', () => {
  it('正整数返回字符串', () => {
    expect(psNumber(42)).toBe('42')
  })

  it('零返回字符串', () => {
    expect(psNumber(0)).toBe('0')
  })

  it('负数返回字符串', () => {
    expect(psNumber(-1)).toBe('-1')
  })

  it('小数返回字符串', () => {
    expect(psNumber(3.14)).toBe('3.14')
  })

  it('NaN 返回 null', () => {
    expect(psNumber(NaN)).toBeNull()
  })

  it('Infinity 返回 null', () => {
    expect(psNumber(Infinity)).toBeNull()
  })

  it('字符串 "42" 返回 null（非数字类型）', () => {
    expect(psNumber('42')).toBeNull()
  })

  it('注入字符串 "42; rm" 返回 null', () => {
    expect(psNumber('42; rm')).toBeNull()
  })

  it('undefined 返回 null', () => {
    expect(psNumber(undefined)).toBeNull()
  })
})

describe('psEnum', () => {
  const ALLOWED = new Set(['ro', 'rw', 'krb5'])

  it('白名单内值原样返回', () => {
    expect(psEnum('ro', ALLOWED)).toBe('ro')
    expect(psEnum('rw', ALLOWED)).toBe('rw')
    expect(psEnum('krb5', ALLOWED)).toBe('krb5')
  })

  it('白名单外值返回 null', () => {
    expect(psEnum('rx', ALLOWED)).toBeNull()
    expect(psEnum('admin', ALLOWED)).toBeNull()
  })

  it('非字符串返回 null', () => {
    expect(psEnum(42, ALLOWED)).toBeNull()
    expect(psEnum(true, ALLOWED)).toBeNull()
  })

  it('注入字符串返回 null（不在白名单中）', () => {
    expect(psEnum("ro; Remove-LocalUser 'Administrator'", ALLOWED)).toBeNull()
  })

  it('undefined 返回 null', () => {
    expect(psEnum(undefined, ALLOWED)).toBeNull()
  })

  it('空字符串返回 null', () => {
    expect(psEnum('', ALLOWED)).toBeNull()
  })
})
