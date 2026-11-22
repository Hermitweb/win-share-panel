import { describe, expect, it } from 'vitest'
import { repairJournalText } from './journalTextRepair'

// 真实乱码样本取自用户环境 appstate.json（2026-10 期间的 200 条台账）
describe('repairJournalText', () => {
  it('整句样本恢复中文主干（含全角标点，允许少量损失）', () => {
    const out = repairJournalText(
      '鍏变韩宸插垹闄わ紙NFS 鍏变韩鍙参粨涓嶆敮鎸佷竴閿箰鍙€锛岃鎸夌暀妗х顫宸ラ€祤»',
    )
    expect(out).toContain('共享已删除')
    expect(out).toContain('NFS 共享')
    expect(out).toContain('不支持一')
    expect(out).toContain('按留')
  })

  it('短词完全恢复', () => {
    expect(repairJournalText('娴嬭瘯')).toBe('测试')
    expect(repairJournalText('鍏变韩宸插垹闄わ紙')).toBe('共享已删除（')
  })

  it('幂等：修复后不再变化', () => {
    const once = repairJournalText('鍏变韩宸插垹闄わ紙NFS')
    expect(repairJournalText(once)).toBe(once)
  })

  it('纯 ASCII 不变', () => {
    expect(repairJournalText('C:\\share')).toBe('C:\\share')
    expect(repairJournalText('testwebdav')).toBe('testwebdav')
  })

  it('正常中文不变', () => {
    const good = '共享已删除（NFS 共享参数不支持一键撤销，请按留档手工重建）'
    expect(repairJournalText(good)).toBe(good)
  })

  it('null/空', () => {
    expect(repairJournalText(null)).toBe('')
    expect(repairJournalText('')).toBe('')
  })
})
