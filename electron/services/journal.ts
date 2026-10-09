import { Errors } from '../lib/errors'
import { log } from '../lib/logger'
import { loadState, removeJournal, saveState } from '../lib/stateStore'
import * as share from './share'
import * as user from './user'
import type { JournalEntry, SharePermission, ShareSnapshot } from '../types'

// ===== 操作回收站：最近 N 条操作 + 一键撤销 =====
// 撤销只对"有快照且系统层面可还原"的操作开放：
//   delete(smb)      → 按快照重建共享（含权限/高级选项）
//   toggle-off(smb)  → 走既有 disabled.json 恢复通道
//   permset(smb)     → 回写改前权限快照
// create 与非 SMB 删除仅留档不撤销（还原会丢失协议专有配置，宁可不给按钮）。
// 撤销成功即消费该记录，避免同一快照被重复回放。

function isShareSnapshot(s: JournalEntry['snapshot']): s is ShareSnapshot {
  return !!s && !Array.isArray(s) && typeof (s as ShareSnapshot).path === 'string'
}

export function listJournal(): JournalEntry[] {
  // 新→旧，UI 直接渲染
  return [...loadState().journal].reverse()
}

export function clearJournal(): number {
  const n = loadState().journal.length
  saveState({ journal: [] })
  return n
}

/** 撤销一条操作；返回人类可读的结果说明 */
export async function undoJournal(id: string): Promise<string> {
  const entry = loadState().journal.find((j) => j.id === id)
  if (!entry) throw Errors.invalidParam('操作记录不存在或已被清理')
  if (!entry.undoable) throw Errors.invalidParam('该操作不支持撤销（仅留档）')
  if (entry.protocol !== 'smb') {
    throw Errors.invalidParam(`${entry.protocol.toUpperCase()} 暂不支持一键撤销，请按留档手工重建`)
  }

  let message: string
  switch (entry.action) {
    case 'delete': {
      if (!isShareSnapshot(entry.snapshot)) {
        throw Errors.invalidParam('缺少删除前快照，无法撤销')
      }
      // 共享若已被重新创建（人工或后续操作），恢复会撞名——如实报错而不是静默跳过
      const exists = (await share.listShares()).some((s) => s.name === entry.name)
      if (exists) throw Errors.commandFailed(`共享"${entry.name}"已存在，无需撤销`)
      await share.restoreShare(entry.snapshot)
      message = `已恢复共享"${entry.name}"（路径 ${entry.snapshot.path}，${entry.snapshot.permissions.length} 条授权）`
      break
    }
    case 'toggle-off': {
      // 优先走 disabled.json 通道（与用户在列表里点"启用"完全同路径）
      try {
        await share.toggleShare(entry.name, true)
        message = `已重新启用共享"${entry.name}"`
      } catch (e) {
        const first = (e as Error).message
        if (!isShareSnapshot(entry.snapshot)) {
          throw Errors.commandFailed(`恢复失败：${first}`)
        }
        // disabled.json 记录已被消费（例如用户手工启用过）→ 用 journal 快照兜底重建
        const exists = (await share.listShares()).some((s) => s.name === entry.name)
        if (exists) throw Errors.commandFailed(`共享"${entry.name}"已存在，无需撤销`)
        await share.restoreShare(entry.snapshot)
        message = `已按快照恢复共享"${entry.name}"（禁用记录已被消费，改用快照重建）`
      }
      break
    }
    case 'permset': {
      if (!Array.isArray(entry.snapshot)) {
        throw Errors.invalidParam('缺少权限快照，无法撤销')
      }
      const perms = entry.snapshot as SharePermission[]
      await user.setSharePermissions(entry.name, perms)
      message = `已回滚"${entry.name}"的共享权限到改前状态（${perms.length} 条）`
      break
    }
    case 'create':
    default:
      throw Errors.invalidParam('该操作不支持撤销（仅留档）')
  }

  removeJournal(id)
  log.info('journal', '[undoJournal] 撤销成功:', entry.action, entry.protocol, entry.name)
  return message
}
