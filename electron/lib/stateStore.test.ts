import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync, rmSync } from 'fs'
import { tmpdir } from 'os'
import { join } from 'path'

// 批1 t1：stateStore 单测。
// stateStore 直接用 fs 读写 %APPDATA%/WinSharePanel/state/appstate.json；
// 本用例把 os.platform mock 成 win32（钉住 APPDATA 分支，保证跨主机 CI 行为一致）、
// APPDATA stub 到每个用例独立的临时目录——真实落盘，但不碰用户数据。
// 重点：脏/旧文件安全升级、损坏回落默认不抛错、alertRules 浅合并、上限截断保留最新。
const mockedPlatform = vi.hoisted(() => vi.fn(() => 'win32'))
vi.mock('os', async (importOriginal) => {
  const actual = await importOriginal<typeof import('os')>()
  return { ...actual, platform: mockedPlatform }
})

import {
  loadState,
  saveState,
  addJournal,
  removeJournal,
  pushHistory,
  __resetStateCache,
} from './stateStore'

let dir = ''
const statePath = () => join(dir, 'WinSharePanel', 'state', 'appstate.json')

function seedState(raw: string): void {
  mkdirSync(join(dir, 'WinSharePanel', 'state'), { recursive: true })
  writeFileSync(statePath(), raw, 'utf8')
}
function readPersisted(): Record<string, unknown> {
  return JSON.parse(readFileSync(statePath(), 'utf8')) as Record<string, unknown>
}

const DEFAULT_STATE = {
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: {
    idleAlertMinutes: null,
    smb1Alert: true,
    weakPasswordAlert: true,
    diskLowGb: 20,
  },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'wsstate-'))
  vi.stubEnv('APPDATA', dir)
  __resetStateCache()
})
afterEach(() => {
  vi.unstubAllEnvs()
  rmSync(dir, { recursive: true, force: true })
})

describe('loadState 旧/脏文件安全升级', () => {
  it('旧版文件缺新字段 → 逐字段补默认值', () => {
    seedState(JSON.stringify({ theme: 'dark' }))
    const s = loadState()
    expect(s.theme).toBe('dark')
    expect(s.advancedMode).toBe(true)
    expect(s.pinned).toEqual([])
    expect(s.alertRules).toEqual(DEFAULT_STATE.alertRules)
    expect(s.autoStart).toBe(false)
    expect(s.dashboardHistory).toEqual([])
    expect(s.journal).toEqual([])
  })

  it('多余未知字段不穿透（收敛回已知 schema）', () => {
    seedState(JSON.stringify({ theme: 'light', futureField: { x: 1 } }))
    const s = loadState() as unknown as Record<string, unknown>
    expect(s.futureField).toBeUndefined()
  })

  it('脏值逐键拒收：类型不符一律回默认', () => {
    seedState(
      JSON.stringify({
        theme: 'neon',
        advancedMode: 'yes',
        pinned: ['ok', 42, null, 'ok2'],
        alertRules: {
          idleAlertMinutes: -5,
          smb1Alert: 'true',
          weakPasswordAlert: null,
          diskLowGb: '20',
        },
        autoStart: 1,
        dashboardHistory: [{ ts: 'no' }, { ts: 1, sessions: 2 }],
        journal: [{ id: 'a' }, { noId: true }, 'junk', null],
      }),
    )
    const s = loadState()
    expect(s.theme).toBe('light')
    expect(s.advancedMode).toBe(true)
    expect(s.pinned).toEqual(['ok', 'ok2'])
    expect(s.alertRules.idleAlertMinutes).toBeNull()
    expect(s.alertRules.smb1Alert).toBe(true)
    expect(s.alertRules.weakPasswordAlert).toBe(true)
    expect(s.alertRules.diskLowGb).toBe(20)
    expect(s.autoStart).toBe(false)
    expect(s.dashboardHistory).toEqual([{ ts: 1, sessions: 2 }])
    expect(s.journal).toEqual([{ id: 'a' }])
  })

  it('合法数值键四舍五入收敛（小数分钟/GB 不炸）', () => {
    seedState(JSON.stringify({ alertRules: { idleAlertMinutes: 12.7, diskLowGb: 33.2 } }))
    const s = loadState()
    expect(s.alertRules.idleAlertMinutes).toBe(13)
    expect(s.alertRules.diskLowGb).toBe(33)
  })
})

describe('loadState 文件损坏回落默认（不抛错）', () => {
  it('非 JSON 垃圾 → 默认值，不抛异常', () => {
    seedState('{{{not json}}}')
    expect(() => loadState()).not.toThrow()
    expect(loadState().theme).toBe('light')
  })

  it('顶层数组/字符串/数字/null 一律回落默认', () => {
    for (const bad of ['[]', '"str"', '42', 'null']) {
      __resetStateCache()
      seedState(bad)
      expect(loadState()).toEqual(DEFAULT_STATE)
    }
  })

  it('损坏后 saveState 可修复文件（写回合法 JSON）', () => {
    seedState('garbage-garbage')
    loadState()
    saveState({ theme: 'dark' })
    expect(readPersisted().theme).toBe('dark')
  })

  it('进程内缓存：命中后不再读文件（改文件不影响已缓存态）', () => {
    const s1 = loadState()
    seedState('{{break}}')
    expect(loadState()).toBe(s1)
  })
})

describe('容量上限：截断保留最新', () => {
  it('journal 超 200 → 保留最后 200 条（最新在末尾）', () => {
    const journal = Array.from({ length: 210 }, (_, i) => ({
      id: `j${i}`,
      ts: i,
      action: 'create' as const,
      protocol: 'smb' as const,
      name: `s${i}`,
      undoable: false,
    }))
    seedState(JSON.stringify({ journal }))
    const s = loadState()
    expect(s.journal).toHaveLength(200)
    expect(s.journal[0].id).toBe('j10')
    expect(s.journal[199].id).toBe('j209')
  })

  it('dashboardHistory 超 288 → 保留最后 288 个采样点', () => {
    const dashboardHistory = Array.from({ length: 300 }, (_, i) => ({
      ts: i,
      sessions: 0,
      openFiles: 0,
    }))
    seedState(JSON.stringify({ dashboardHistory }))
    const s = loadState()
    expect(s.dashboardHistory).toHaveLength(288)
    expect(s.dashboardHistory[0].ts).toBe(12)
    expect(s.dashboardHistory[287].ts).toBe(299)
  })

  it('pinned 超 50 → 保留最后 50 个', () => {
    const pinned = Array.from({ length: 60 }, (_, i) => `smb:s${i}`)
    seedState(JSON.stringify({ pinned }))
    const s = loadState()
    expect(s.pinned).toHaveLength(50)
    expect(s.pinned[49]).toBe('smb:s59')
  })

  it('addJournal 追加后总量仍受上限约束（新条目必在）', () => {
    const journal = Array.from({ length: 200 }, (_, i) => ({
      id: `old${i}`,
      ts: i,
      action: 'create' as const,
      protocol: 'smb' as const,
      name: `s${i}`,
      undoable: false,
    }))
    seedState(JSON.stringify({ journal }))
    const e = addJournal({ action: 'delete', protocol: 'smb', name: 'newest', undoable: true })
    const s = loadState()
    expect(s.journal).toHaveLength(200)
    expect(s.journal[199].id).toBe(e.id)
    expect(s.journal.some((j) => j.id === 'old0')).toBe(false)
  })
})

describe('saveState 浅合并与持久化', () => {
  it('alertRules 只改一键时其余键保持原值（不覆盖未传键）', () => {
    saveState({ alertRules: { diskLowGb: 50 } })
    expect(loadState().alertRules).toEqual({
      idleAlertMinutes: null,
      smb1Alert: true,
      weakPasswordAlert: true,
      diskLowGb: 50,
    })
    // 第二次只改 smb1Alert：50 是"上一次存的值"而非默认 20，浅合并必须保住
    saveState({ alertRules: { smb1Alert: false } })
    const ar = loadState().alertRules
    expect(ar.diskLowGb).toBe(50)
    expect(ar.smb1Alert).toBe(false)
  })

  it('patch 未含的顶层键保持（theme 修改不丢 pinned）', () => {
    saveState({ pinned: ['smb:a'], theme: 'dark' })
    saveState({ theme: 'light' })
    const s = loadState()
    expect(s.pinned).toEqual(['smb:a'])
    expect(s.theme).toBe('light')
  })

  it('原子写：文件可读、内存与磁盘一致、无 .tmp 残留', () => {
    saveState({ autoStart: true })
    expect(readPersisted().autoStart).toBe(true)
    expect(existsSync(`${statePath()}.tmp`)).toBe(false)
    __resetStateCache()
    expect(loadState().autoStart).toBe(true)
  })

  it('patch 携带脏值同样被逐键收敛（IPC 边界无类型保证）', () => {
    saveState({
      theme: 'garbage' as never,
      pinned: ['ok', 7 as never],
      autoStart: 'yes' as never,
    })
    const s = loadState()
    expect(s.theme).toBe('light')
    expect(s.pinned).toEqual(['ok'])
    expect(s.autoStart).toBe(false)
  })
})

describe('journal 读写闭环', () => {
  it('addJournal 生成 id/ts；removeJournal 只删指定 id', () => {
    const a = addJournal({ action: 'delete', protocol: 'smb', name: 'docs', undoable: true })
    const b = addJournal({ action: 'permset', protocol: 'smb', name: 'x', undoable: true })
    expect(a.id).toMatch(/^j\d+-\d+$/)
    expect(b.id).not.toBe(a.id)
    expect(loadState().journal.map((j) => j.id)).toEqual([a.id, b.id])
    removeJournal(a.id)
    expect(loadState().journal.map((j) => j.id)).toEqual([b.id])
    // 删不存在的 id 是安全的 no-op
    removeJournal('ghost')
    expect(loadState().journal).toHaveLength(1)
  })

  it('pushHistory 按序追加点位', () => {
    pushHistory({ ts: 1, sessions: 0, openFiles: 0 })
    pushHistory({ ts: 2, sessions: 3, openFiles: 4 })
    expect(loadState().dashboardHistory.map((h) => h.ts)).toEqual([1, 2])
  })
})
