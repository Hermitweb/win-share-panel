import { useEffect, useState } from 'react'
import { App } from 'antd'
import { api, call } from '../api'
import type { Protocol, SharePermission } from '../types'

/**
 * R-5：三协议权限面板（NfsPermPanel / FtpPermPanel / WebdavPermPanel）共用的
 * 「按 share 加载权限 → 本地编辑行 → 保存覆盖 → 重读」逻辑。
 *
 * 行为契约（与抽象前逐字一致）：
 * - IPC 参数：取数 `adapter.permissions(protocol, shareName)`；保存
 *   `adapter.setPermissions(protocol, shareName, rows.map(r => toPerm(r, shareName)))`。
 * - `rows` 是唯一真源：服务端结果直接落 rows，本地编辑直接改 rows（无服务端/草稿双态）。
 * - 目标切换/重读不清空 rows：旧数据在新数据到达前仍可见，只是盖上 Spin。
 * - 保存失败不重读、不做乐观变更、不假成功。
 */

/** 三协议面板共用的行基形状（Nfs 用 clientName 作 key，Ftp/Webdav 用 account） */
export interface ProtocolPermissionRowBase {
  account: string
  accountType: 'User' | 'Group'
}

export interface UseProtocolPermissionsOptions<TRow> {
  /** 权限通道（决定 IPC 通道；不参与行结构） */
  protocol: Extract<Protocol, 'nfs' | 'ftp' | 'webdav'>
  /** 目标共享名；变化即重载（等价原 `[share.name]`） */
  shareName: string
  /** adapter 返回 → 面板行。必须是模块级函数（引用稳定，避免重复 IPC） */
  toRow: (p: SharePermission) => TRow
  /** 面板行 → adapter 入参。必须是模块级函数；签名为 (row, shareName) */
  toPerm: (row: TRow, shareName: string) => SharePermission
  /** 行主键：'clientName' | 'account' */
  rowKey: keyof TRow & string
}

export interface UseProtocolPermissionsResult<TRow> {
  /** 当前行（服务端返回即落此处；本地编辑直接改此处） */
  rows: TRow[]
  /** 取数中（挂载即 true） */
  loading: boolean
  /** 保存中 */
  saving: boolean
  /** 事件入口：重新加载 / 保存成功后重读。禁止在 effect 中调用（内部 bump 渲染期重读序号） */
  reload: () => void
  /** 事件入口：保存覆盖。成功 → true，并内部 message.success(successText) + reload()；失败 → false，并 message.error(原因) */
  save: (successText: string) => Promise<boolean>
  /** 本地新增一行（校验与提示文案留在面板） */
  addRow: (row: TRow) => void
  /** 本地删除一行 */
  removeRow: (key: string) => void
  /** 本地改一行（浅合并） */
  updateRow: (key: string, patch: Partial<TRow>) => void
  /** 行是否已存在（面板用它给出"该账号/客户端已存在"提示） */
  hasRow: (key: string) => boolean
}

export function useProtocolPermissions<TRow>(
  opts: UseProtocolPermissionsOptions<TRow>,
): UseProtocolPermissionsResult<TRow> {
  const { message } = App.useApp()
  const { protocol, shareName, toRow, toPerm, rowKey } = opts

  const [rows, setRows] = useState<TRow[]>([])
  // 三面板仅在 PermissionDrawer 打开且协议匹配时挂载，挂载即加载
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [nonce, setNonce] = useState(0)

  const loadKey = `${protocol}|${shareName}|${nonce}`
  const [prevKey, setPrevKey] = useState(loadKey)
  // 渲染期调整 state（React 官方 "adjusting state during render"）：目标/重读序号变化时
  // 同一次提交内进入加载态，替代原 effect 内的同步 setLoading(true)。rows 有意不清空。
  if (prevKey !== loadKey) {
    setPrevKey(loadKey)
    setLoading(true)
  }

  useEffect(() => {
    let dead = false
    // 载入函数定义在 effect 回调内部：组件体定义的同构函数会在调用点被
    // set-state-in-effect 判为同步 setState（即使其 setState 在 await 之后）
    const load = async () => {
      try {
        const list = await call(() => api.adapter.permissions(protocol, shareName))
        if (!dead) setRows(list.map(toRow))
      } catch (e) {
        if (!dead) message.error((e as Error).message)
      } finally {
        if (!dead) setLoading(false)
      }
    }
    void load()
    return () => {
      dead = true
    }
    // 有意仅依赖 loadKey（协议|目标|重读序号）：toRow/message 为模块级/上下文稳定引用，
    // 纳入依赖会因每轮渲染新建引用而重复触发取数
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadKey])

  const reload = () => setNonce((n) => n + 1)

  const save = async (successText: string): Promise<boolean> => {
    setSaving(true)
    try {
      const perms = rows.map((r) => toPerm(r, shareName))
      await call(() => api.adapter.setPermissions(protocol, shareName, perms))
      message.success(successText)
      reload()
      return true
    } catch (e) {
      message.error((e as Error).message)
      return false
    } finally {
      setSaving(false)
    }
  }

  const addRow = (row: TRow) => setRows((prev) => [...prev, row])

  const removeRow = (key: string) =>
    setRows((prev) => prev.filter((r) => String(r[rowKey]) !== key))

  const updateRow = (key: string, patch: Partial<TRow>) =>
    setRows((prev) => prev.map((r) => (String(r[rowKey]) === key ? { ...r, ...patch } : r)))

  const hasRow = (key: string) => rows.some((r) => String(r[rowKey]) === key)

  return { rows, loading, saving, reload, save, addRow, removeRow, updateRow, hasRow }
}
