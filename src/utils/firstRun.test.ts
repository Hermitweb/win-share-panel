import { describe, it, expect } from 'vitest'
import type { Share } from '../types'
import {
  DEFAULT_PERM_TIER,
  FIRST_RUN_DONE_KEY,
  FIRST_RUN_DONE_VALUE,
  GUIDE_STEPS,
  PERM_TIERS,
  buildCreateInput,
  isMarkedComplete,
  markFirstRunComplete,
  readFirstRunFlag,
  shouldShowGuide,
  tierToAccess,
  type FlagStorage,
} from './firstRun'

// ===== T4 首启判定纯函数 =====
// 三不打扰：有共享 / 已标记完成 / 专家模式。判定入参全部显式传入，
// 不读 storage/window/props，才能把"老用户绝不打扰"写成表驱动测试。

const share = (over: Partial<Share> = {}): Share =>
  ({
    name: 'docs',
    path: 'D:\\Share\\docs',
    description: '',
    protocol: 'smb',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: 'Enabled',
    cached: false,
    ...over,
  }) as Share

/** 最小 fake 存储（node 环境无 localStorage；注入式签名就是为它留的门） */
function fakeStorage(
  seed: Record<string, string> = {},
): FlagStorage & { map: Map<string, string> } {
  const map = new Map(Object.entries(seed))
  return {
    map,
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => {
      map.set(k, v)
    },
  }
}

describe('shouldShowGuide：首启判定纯函数', () => {
  it('空共享 + 新手模式 + 未标记 → 弹（唯一弹出组合）', () => {
    expect(shouldShowGuide([], false, null)).toBe(true)
  })
  it('有共享 → 不弹（无论标记与模式）', () => {
    for (const advanced of [false, true]) {
      for (const flag of [null, FIRST_RUN_DONE_VALUE]) {
        expect(shouldShowGuide([share()], advanced, flag)).toBe(false)
      }
    }
  })
  it('已标记完成 → 不弹（老用户绝不打扰）', () => {
    for (const flag of [FIRST_RUN_DONE_VALUE, 'true', true]) {
      expect(shouldShowGuide([], false, flag)).toBe(false)
    }
  })
  it('专家模式 → 不弹：能切专家的不是新手，向导对他是噪音', () => {
    expect(shouldShowGuide([], true, null)).toBe(false)
  })
  it('shares=null/undefined（列表尚未加载完）→ 不弹，加载中不闪向导', () => {
    expect(shouldShowGuide(null, false, null)).toBe(false)
    expect(shouldShowGuide(undefined, false, null)).toBe(false)
  })
  it('纯函数：不修改入参数组', () => {
    const shares = [share({ name: 'a' })]
    expect(() => shouldShowGuide(shares, false, null)).not.toThrow()
    expect(shares).toHaveLength(1)
  })
})

describe('isMarkedComplete：原始标记值 → 已完成判定', () => {
  it('1 / true / "true" 均算已标记', () => {
    for (const v of [FIRST_RUN_DONE_VALUE, true, 'true']) expect(isMarkedComplete(v)).toBe(true)
  })
  it('null / undefined / 空串 / "0" / false / 其他字符串均视为未标记', () => {
    for (const v of [null, undefined, '', '0', false, 'false', 'yes']) {
      expect(isMarkedComplete(v)).toBe(false)
    }
  })
})

describe('tierToAccess：三档人话 → fullAccess/changeAccess/readAccess', () => {
  it('档位键与说明一致：完全交给→fullAccess，一起用→changeAccess，只看看→readAccess', () => {
    expect(tierToAccess('full')).toEqual({ fullAccess: ['Users'] })
    expect(tierToAccess('change')).toEqual({ changeAccess: ['Users'] })
    expect(tierToAccess('read')).toEqual({ readAccess: ['Users'] })
  })
  it('三档互斥：任何一档只命中一个权限键', () => {
    for (const opt of PERM_TIERS) {
      expect(Object.keys(tierToAccess(opt.tier))).toHaveLength(1)
    }
  })
  it('返回数组拷贝：调用方改动不污染 DEFAULT_READ_ACCESS', () => {
    const out = tierToAccess('read')
    out.readAccess!.push('x')
    expect(tierToAccess('read').readAccess).toEqual(['Users'])
  })
  it('未知档位回退只读档（首启默认就安全）', () => {
    expect(tierToAccess('nope' as never)).toEqual({ readAccess: ['Users'] })
    expect(DEFAULT_PERM_TIER).toBe('read')
  })
  it('说明文案无 SMB1/枚举/租约/NTFS 术语', () => {
    const all = PERM_TIERS.map((p) => `${p.label}${p.desc}`).join('')
    expect(all).not.toMatch(/SMB1|NTFS|租约|枚举/)
  })
})

describe('buildCreateInput：向导唯一创建参数出口', () => {
  it('SMB 默认档：只带权限键，不残留其他档', () => {
    expect(buildCreateInput('docs', 'D:\\Share\\docs', 'change')).toEqual({
      protocol: 'smb',
      name: 'docs',
      path: 'D:\\Share\\docs',
      changeAccess: ['Users'],
    })
  })
  it('trim 路径与共享名', () => {
    const out = buildCreateInput('  docs ', '  D:\\Share\\docs ', 'read')
    expect(out.name).toBe('docs')
    expect(out.path).toBe('D:\\Share\\docs')
  })
  it('advanced 缺省/默认值 → 不提交任何高级参数（折叠未开=什么都没改）', () => {
    const out = buildCreateInput('a', 'b', 'read')
    expect(out.folderEnumerationMode).toBeUndefined()
    expect(out.cachingMode).toBeUndefined()
    expect(out.concurrentUserLimit).toBeUndefined()
    const out2 = buildCreateInput('a', 'b', 'read', { cachingMode: 'None', concurrentUserLimit: 0 })
    expect(out2.cachingMode).toBeUndefined()
    expect(out2.concurrentUserLimit).toBeUndefined()
  })
  it('advanced 显式非默认值透传', () => {
    const out = buildCreateInput('a', 'b', 'full', {
      cachingMode: 'Manual',
      concurrentUserLimit: 10,
    })
    expect(out).toMatchObject({
      fullAccess: ['Users'],
      cachingMode: 'Manual',
      concurrentUserLimit: 10,
    })
  })
})

describe('向导步骤契约', () => {
  it('共三步：选文件夹 → 给谁用 → 完成', () => {
    expect(GUIDE_STEPS.map((s) => s.title)).toEqual(['选文件夹', '给谁用', '完成'])
  })
})

describe('一次性完成标记读写（注入式存储，node 环境可用）', () => {
  it('markFirstRunComplete 写 FIRST_RUN_DONE_KEY=1；readFirstRunFlag 读回', () => {
    const st = fakeStorage()
    expect(markFirstRunComplete(st)).toBe(true)
    expect(st.map.get(FIRST_RUN_DONE_KEY)).toBe(FIRST_RUN_DONE_VALUE)
    expect(readFirstRunFlag(st)).toBe(FIRST_RUN_DONE_VALUE)
    expect(shouldShowGuide([], false, readFirstRunFlag(st))).toBe(false)
  })
  it('缺存储（node 环境无 localStorage）→ 读 null / 写 false，不抛错', () => {
    expect(readFirstRunFlag(null)).toBe(null)
    expect(markFirstRunComplete(null)).toBe(false)
  })
  it('存储抛错（隐私模式/配额）→ 静默降级，不炸向导', () => {
    const boom: FlagStorage = {
      getItem: () => {
        throw new Error('blocked')
      },
      setItem: () => {
        throw new Error('QuotaExceeded')
      },
    }
    expect(readFirstRunFlag(boom)).toBe(null)
    expect(markFirstRunComplete(boom)).toBe(false)
  })
})
