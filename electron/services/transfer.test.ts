import { describe, it, expect, vi, beforeEach } from 'vitest'

// 用注入的假 stateStore：本测试只关心 transfer 的组装/校验职责，
// 落盘与 normalize 收敛属 stateStore 自身（已有独立用例，且需要临时目录真实落盘）。
const store = vi.hoisted(() => ({
  state: {} as Record<string, unknown>,
  saved: [] as Record<string, unknown>[],
}))
vi.mock('../lib/stateStore', () => ({
  loadState: () => store.state,
  saveState: (p: Record<string, unknown>) => {
    store.saved.push(p)
    return store.state
  },
}))

import { exportSettings, importSettings, TRANSFER_FORMAT, TRANSFER_VERSION } from './transfer'

const CURRENT = {
  theme: 'dark',
  advancedMode: false,
  pinned: ['smb:Reports', 'ftp:pub'],
  alertRules: { idleAlertMinutes: 30, smb1Alert: false, weakPasswordAlert: true, diskLowGb: 5 },
  autoStart: true,
  // 运行时数据：必须**不**出现在导出里
  dashboardHistory: [{ ts: 1, users: 2 }],
  journal: [{ id: 'j1', action: 'delete' }],
}

beforeEach(() => {
  store.state = structuredClone(CURRENT)
  store.saved = []
})

describe('exportSettings', () => {
  it('形状：format / version / exportedAt / settings 五项一一对应', () => {
    const b = JSON.parse(exportSettings()) as Record<string, unknown>
    expect(b.format).toBe(TRANSFER_FORMAT)
    expect(b.version).toBe(TRANSFER_VERSION)
    expect(typeof b.exportedAt).toBe('string')
    expect(b.settings).toEqual({
      theme: 'dark',
      advancedMode: false,
      pinned: ['smb:Reports', 'ftp:pub'],
      alertRules: { idleAlertMinutes: 30, smb1Alert: false, weakPasswordAlert: true, diskLowGb: 5 },
      autoStart: true,
    })
  })

  it('关键不变式：不导出 dashboardHistory / journal（运行时数据不可当偏好搬家）', () => {
    const text = exportSettings()
    expect(text).not.toContain('dashboardHistory')
    expect(text).not.toContain('journal')
    // 反向钉：连字段名都不该出现，更不该带值
    expect(text).not.toContain('"j1"')
  })

  it('导出的是快照，不是引用：外部改动 store 不会污染已导出文本', () => {
    const text = exportSettings()
    store.state.pinned = ['changed']
    expect(JSON.parse(text).settings.pinned).toEqual(['smb:Reports', 'ftp:pub'])
  })
})

describe('importSettings：护栏', () => {
  const bundle = (settings: unknown, version = TRANSFER_VERSION, format = TRANSFER_FORMAT) =>
    JSON.stringify({ format, version, exportedAt: 'x', settings })

  it('非字符串 / 超大 / 非 JSON', () => {
    expect(() => importSettings(42)).toThrow(/必须是文本/)
    expect(() => importSettings('x'.repeat(512 * 1024 + 1))).toThrow(/过大/)
    expect(() => importSettings('{not json')).toThrow(/不是合法的 JSON/)
  })

  it('格式不符（含别的应用的 JSON）', () => {
    expect(() => importSettings(JSON.stringify({ hello: 'world' }))).toThrow(/格式不符/)
    expect(() => importSettings(bundle({}, 1, 'other-app/settings'))).toThrow(/格式不符/)
  })

  it('版本高于本程序：明确拒绝并给出可执行建议，不尝试猜', () => {
    expect(() => importSettings(bundle({ theme: 'dark' }, TRANSFER_VERSION + 1))).toThrow(
      /高于本程序支持的版本/,
    )
  })

  it('缺版本号 / 缺 settings 段', () => {
    expect(() => importSettings(JSON.stringify({ format: TRANSFER_FORMAT, settings: {} }))).toThrow(
      /缺少版本号/,
    )
    expect(() => importSettings(bundle(undefined))).toThrow(/没有 settings 段/)
  })

  it('解析后没有任何可识别设置项：拒绝而不是静默"成功"', () => {
    expect(() => importSettings(bundle({ unknownField: 1 }))).toThrow(/没有任何可识别的设置项/)
    expect(() => importSettings(bundle({ theme: 'blue', advancedMode: 'yes' }))).toThrow(
      /没有任何可识别的设置项/,
    )
  })
})

describe('importSettings：逐字段收敛（合法应用、非法忽略）', () => {
  it('完整文件：apply 全部五项，并把 patch 交给 saveState', () => {
    const text = exportSettings()
    store.saved = []
    const r = importSettings(text)
    expect(r.applied).toEqual(['theme', 'advancedMode', 'pinned', 'alertRules', 'autoStart'])
    expect(store.saved).toHaveLength(1)
    expect(store.saved[0]).toEqual({
      theme: 'dark',
      advancedMode: false,
      pinned: ['smb:Reports', 'ftp:pub'],
      alertRules: { idleAlertMinutes: 30, smb1Alert: false, weakPasswordAlert: true, diskLowGb: 5 },
      autoStart: true,
    })
  })

  it('往返幂等：export → import 得到的 patch 与当前设置一致', () => {
    const r = importSettings(exportSettings())
    expect(r.applied).toHaveLength(5)
    expect(store.saved[0]).toMatchObject({
      theme: CURRENT.theme,
      advancedMode: CURRENT.advancedMode,
      autoStart: CURRENT.autoStart,
    })
  })

  it('混合脏值：只应用合法字段，不因个别脏值整体拒绝', () => {
    const r = importSettings(
      JSON.stringify({
        format: TRANSFER_FORMAT,
        version: 1,
        settings: { theme: 'blue', advancedMode: true, autoStart: 'yes', pinned: ['ok', 7, null] },
      }),
    )
    expect(r.applied).toEqual(['advancedMode', 'pinned'])
    expect(store.saved[0]).toEqual({ advancedMode: true, pinned: ['ok'] })
  })

  it('字符串自动启 —— stateStore.normalize 会收敛，transfer 只做形状层放行', () => {
    const r = importSettings(
      JSON.stringify({ format: TRANSFER_FORMAT, version: 1, settings: { autoStart: true } }),
    )
    expect(r.applied).toEqual(['autoStart'])
    expect(store.saved[0]).toEqual({ autoStart: true })
  })
})
