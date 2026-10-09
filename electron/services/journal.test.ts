import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mkdtempSync, rmSync } from 'fs'
import { tmpdir } from 'os'
import { join } from 'path'

// 批1 t1：journal（操作回收站/撤销）单测。
// stateStore 走真实实现（APPDATA stub 到临时目录、os.platform mock 成 win32），
// 从而能端到端钉住"撤销成功后记录被移除 / 撤销失败记录必须还在"——撤销不假成功。
// share/user 写通道全部 mock，保证任何拒绝路径都是"零副作用"。
const mockedPlatform = vi.hoisted(() => vi.fn(() => 'win32'))
vi.mock('os', async (importOriginal) => {
  const actual = await importOriginal<typeof import('os')>()
  return { ...actual, platform: mockedPlatform }
})
vi.mock('./share', () => ({
  listShares: vi.fn(),
  restoreShare: vi.fn(),
  toggleShare: vi.fn(),
}))
vi.mock('./user', () => ({
  setSharePermissions: vi.fn(),
}))

import * as share from './share'
import * as user from './user'
import { listJournal, clearJournal, undoJournal } from './journal'
import { addJournal, __resetStateCache } from '../lib/stateStore'
import { AppError } from '../lib/errors'
import type { SharePermission, ShareSnapshot } from '../types'

const mockedShare = vi.mocked(share)
const mockedUser = vi.mocked(user)

const PERM: SharePermission = {
  shareName: 'docs',
  account: 'Everyone',
  accountType: 'Group',
  access: 'Read',
  deny: false,
}

const SNAPSHOT: ShareSnapshot = {
  name: 'docs',
  path: 'E:\\docs',
  description: '',
  permissions: [PERM],
  encrypted: false,
}

function seedDeleteEntry(): string {
  return addJournal({
    action: 'delete',
    protocol: 'smb',
    name: 'docs',
    undoable: true,
    snapshot: SNAPSHOT,
  }).id
}

let dir = ''
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'wsjournal-'))
  vi.stubEnv('APPDATA', dir)
  __resetStateCache()
  vi.clearAllMocks()
  // 缺省写通道：读列表为空、写均成功
  mockedShare.listShares.mockResolvedValue([])
  mockedShare.restoreShare.mockResolvedValue(undefined)
  mockedShare.toggleShare.mockResolvedValue(undefined)
  mockedUser.setSharePermissions.mockResolvedValue(undefined)
})
afterEach(() => {
  vi.unstubAllEnvs()
  rmSync(dir, { recursive: true, force: true })
})

describe('undoJournal 拒绝路径：抛错且零副作用', () => {
  it('不存在 id → 抛"不存在或已被清理"，不发起任何读写通道调用', async () => {
    await expect(undoJournal('ghost')).rejects.toThrow(/操作记录不存在/)
    expect(mockedShare.listShares).not.toHaveBeenCalled()
    expect(mockedShare.restoreShare).not.toHaveBeenCalled()
    expect(mockedShare.toggleShare).not.toHaveBeenCalled()
    expect(mockedUser.setSharePermissions).not.toHaveBeenCalled()
  })

  it('undoable=false（仅留档）→ 抛不支持撤销，记录原样保留', async () => {
    const e = addJournal({ action: 'create', protocol: 'smb', name: 'x', undoable: false })
    await expect(undoJournal(e.id)).rejects.toThrow(/不支持撤销/)
    expect(listJournal().map((j) => j.id)).toEqual([e.id])
    expect(mockedShare.restoreShare).not.toHaveBeenCalled()
  })

  it('非 SMB 协议 → 提示按留档手工重建，零系统调用', async () => {
    const e = addJournal({
      action: 'delete',
      protocol: 'ftp',
      name: 'site',
      undoable: true,
      snapshot: SNAPSHOT,
    })
    await expect(undoJournal(e.id)).rejects.toThrow(/FTP 暂不支持一键撤销/)
    expect(mockedShare.listShares).not.toHaveBeenCalled()
    expect(listJournal()).toHaveLength(1)
  })

  it('delete 但快照缺失/形状不对 → 抛"缺少删除前快照"，记录保留', async () => {
    const e1 = addJournal({ action: 'delete', protocol: 'smb', name: 'a', undoable: true })
    const e2 = addJournal({
      action: 'delete',
      protocol: 'smb',
      name: 'b',
      undoable: true,
      snapshot: [PERM], // 数组形状不是 ShareSnapshot（撤销需要的是重建参数）
    })
    await expect(undoJournal(e1.id)).rejects.toThrow(/缺少删除前快照/)
    await expect(undoJournal(e2.id)).rejects.toThrow(/缺少删除前快照/)
    expect(mockedShare.restoreShare).not.toHaveBeenCalled()
    expect(listJournal()).toHaveLength(2)
  })

  it('permset 但快照不是权限数组 → 抛"缺少权限快照"，零写入', async () => {
    const e = addJournal({
      action: 'permset',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
      snapshot: SNAPSHOT,
    })
    await expect(undoJournal(e.id)).rejects.toThrow(/缺少权限快照/)
    expect(mockedUser.setSharePermissions).not.toHaveBeenCalled()
  })
})

describe('undoJournal 成功与假成功防线', () => {
  it('delete(smb)+快照 → restoreShare 按快照重建，消息含路径与授权数，记录被移除', async () => {
    const id = seedDeleteEntry()
    const msg = await undoJournal(id)
    expect(mockedShare.restoreShare).toHaveBeenCalledWith(SNAPSHOT)
    expect(msg).toContain('已恢复共享')
    expect(msg).toContain('E:\\docs')
    expect(msg).toContain('1 条授权')
    expect(listJournal()).toEqual([])
  })

  it('delete 但共享已被重建 → 报「已存在，无需撤销」，restoreShare 不被调用，记录保留（撤销不假成功）', async () => {
    const id = seedDeleteEntry()
    mockedShare.listShares.mockResolvedValue([{ name: 'docs' } as never])
    const err = (await undoJournal(id).catch((x) => x)) as AppError
    expect(err).toBeInstanceOf(AppError)
    expect(err.code).toBe('COMMAND_FAILED')
    expect(err.message).toContain('已存在，无需撤销')
    expect(mockedShare.restoreShare).not.toHaveBeenCalled()
    expect(listJournal().map((j) => j.id)).toEqual([id])
  })

  it('restoreShare 本身失败 → 直接 rethrow，记录保留可再试', async () => {
    const id = seedDeleteEntry()
    mockedShare.restoreShare.mockRejectedValue(new Error('Access is denied'))
    await expect(undoJournal(id)).rejects.toThrow('Access is denied')
    expect(listJournal()).toHaveLength(1)
  })

  it('permset → setSharePermissions 回写改前权限，记录移除', async () => {
    const perms: SharePermission[] = [PERM]
    const e = addJournal({
      action: 'permset',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
      snapshot: perms,
    })
    const msg = await undoJournal(e.id)
    expect(mockedUser.setSharePermissions).toHaveBeenCalledWith('docs', perms)
    expect(msg).toContain('已回滚')
    expect(listJournal()).toEqual([])
  })

  it('toggle-off → 走既有启用通道 toggleShare(name, true)，成功后记录移除', async () => {
    const e = addJournal({
      action: 'toggle-off',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
      snapshot: SNAPSHOT,
    })
    const msg = await undoJournal(e.id)
    expect(mockedShare.toggleShare).toHaveBeenCalledWith('docs', true)
    expect(msg).toContain('已重新启用')
    expect(listJournal()).toEqual([])
  })

  it('toggle-off 通道已被消费 → 按 journal 快照兜底重建（先查重）', async () => {
    const e = addJournal({
      action: 'toggle-off',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
      snapshot: SNAPSHOT,
    })
    mockedShare.toggleShare.mockRejectedValue(new Error('no disabled record'))
    const msg = await undoJournal(e.id)
    expect(mockedShare.listShares).toHaveBeenCalled()
    expect(mockedShare.restoreShare).toHaveBeenCalledWith(SNAPSHOT)
    expect(msg).toContain('改用快照重建')
    expect(listJournal()).toEqual([])
  })

  it('toggle-off 通道失败且兜底撞名 → 「已存在，无需撤销」，记录保留', async () => {
    const e = addJournal({
      action: 'toggle-off',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
      snapshot: SNAPSHOT,
    })
    mockedShare.toggleShare.mockRejectedValue(new Error('nope'))
    mockedShare.listShares.mockResolvedValue([{ name: 'docs' } as never])
    await expect(undoJournal(e.id)).rejects.toThrow(/已存在，无需撤销/)
    expect(mockedShare.restoreShare).not.toHaveBeenCalled()
    expect(listJournal()).toHaveLength(1)
  })

  it('toggle-off 通道失败且无快照可兜底 → 如实报恢复失败', async () => {
    const e = addJournal({
      action: 'toggle-off',
      protocol: 'smb',
      name: 'docs',
      undoable: true,
    })
    mockedShare.toggleShare.mockRejectedValue(new Error('disk gone'))
    await expect(undoJournal(e.id)).rejects.toThrow(/恢复失败：disk gone/)
    expect(listJournal()).toHaveLength(1)
  })
})

describe('listJournal / clearJournal', () => {
  it('新→旧排序（UI 直接渲染）', () => {
    const a = addJournal({ action: 'create', protocol: 'smb', name: 'a', undoable: false })
    const b = addJournal({ action: 'create', protocol: 'smb', name: 'b', undoable: false })
    expect(listJournal().map((j) => j.id)).toEqual([b.id, a.id])
  })

  it('clearJournal 返回条数并清空；再撤销已清 id 走"不存在"分支', async () => {
    addJournal({ action: 'delete', protocol: 'smb', name: 'a', undoable: true, snapshot: SNAPSHOT })
    addJournal({ action: 'permset', protocol: 'smb', name: 'b', undoable: true, snapshot: [PERM] })
    expect(clearJournal()).toBe(2)
    expect(listJournal()).toEqual([])
    await expect(undoJournal('anything')).rejects.toThrow(/操作记录不存在/)
  })
})
