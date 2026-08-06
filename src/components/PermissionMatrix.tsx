import { useEffect, useRef, useState } from 'react'
import { Table, Button, Space, Tag, App, Spin, Empty } from 'antd'
import { ExportOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { Share, LocalUser, LocalGroup, SharePermission } from '../types'

type Access = 'Full' | 'Change' | 'Read' | 'Deny' | '-'

const ACCESS_TAG_COLOR: Record<Access, string> = {
  Full: 'blue',
  Change: 'purple',
  Read: 'default',
  Deny: 'red',
  '-': ''
}

const ACCESS_LABEL: Record<Access, string> = {
  Full: '完全',
  Change: '更改',
  Read: '只读',
  Deny: '拒绝',
  '-': '-'
}

// 同一账号在多个权限条目中时，按安全语义取最严格：Deny > Full > Change > Read
const PRIORITY: Record<Exclude<Access, '-'>, number> = { Deny: 0, Full: 1, Change: 2, Read: 3 }
function pickAccess(perms: SharePermission[]): Access {
  if (!perms.length) return '-'
  const sorted = perms
    .map((p) => (p.deny || p.access === 'NoAccess' ? 'Deny' : (p.access as Access)) as Exclude<Access, '-'>)
    .sort((a, b) => PRIORITY[a] - PRIORITY[b])
  return sorted[0]
}

// 并发上限的 Promise 池
async function mapPool<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>, shouldCancel: () => boolean): Promise<R[]> {
  const results: R[] = new Array(items.length)
  let cursor = 0
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (cursor < items.length) {
      if (shouldCancel()) return
      const i = cursor++
      results[i] = await fn(items[i])
    }
  })
  await Promise.all(workers)
  return results
}

export default function PermissionMatrix() {
  const { message } = App.useApp()
  const [shares, setShares] = useState<Share[]>([])
  const [accounts, setAccounts] = useState<{ name: string; type: 'User' | 'Group' }[]>([])
  const [matrix, setMatrix] = useState<Record<string, Record<string, Access>>>({})
  const [loading, setLoading] = useState(false)
  const cancelRef = useRef(false)

  const load = async () => {
    setLoading(true)
    cancelRef.current = false
    try {
      const [shareList, users, groups] = await Promise.all([
        call(api.share.list).catch(() => [] as Share[]),
        call(api.user.list).catch(() => [] as LocalUser[]),
        call(api.user.groups).catch(() => [] as LocalGroup[])
      ])
      // 仅展示普通共享，过滤 IPC/Special
      const normalShares = (shareList || []).filter((s) => s.type !== 'Special' && s.type !== 'IPC')
      const acctList = [
        ...(users || []).map((u) => ({ name: u.name, type: 'User' as const })),
        ...(groups || []).map((g) => ({ name: g.name, type: 'Group' as const }))
      ]
      setShares(normalShares)
      setAccounts(acctList)

      // 并发拉取每个共享的权限（上限 4）
      const perms = await mapPool(
        normalShares,
        4,
        (s) => call(() => api.share.permissions(s.name)).catch(() => [] as SharePermission[]),
        () => cancelRef.current
      )
      if (cancelRef.current) return

      const map: Record<string, Record<string, Access>> = {}
      normalShares.forEach((s, i) => {
        const list = perms[i] || []
        const byAccount: Record<string, Access> = {}
        // 按 account 分组
        const grouped: Record<string, SharePermission[]> = {}
        list.forEach((p) => {
          if (!grouped[p.account]) grouped[p.account] = []
          grouped[p.account].push(p)
        })
        acctList.forEach((a) => {
          byAccount[a.name] = grouped[a.name] ? pickAccess(grouped[a.name]) : '-'
        })
        map[s.name] = byAccount
      })
      setMatrix(map)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
    return () => {
      cancelRef.current = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const cancel = () => {
    cancelRef.current = true
    setLoading(false)
    message.info('已取消加载')
  }

  const download = (content: string, filename: string, type: string) => {
    const blob = new Blob([content], { type })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = filename
    a.click()
    URL.revokeObjectURL(url)
  }

  const exportCsv = () => {
    const header = ['共享', ...accounts.map((a) => a.name)]
    const rows = shares.map((s) => {
      const row = matrix[s.name] || {}
      return [s.name, ...accounts.map((a) => ACCESS_LABEL[row[a.name] || '-'])]
    })
    const csv = [header, ...rows].map((r) => r.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(',')).join('\n')
    download('\uFEFF' + csv, `permission-matrix-${Date.now()}.csv`, 'text/csv;charset=utf-8')
    message.success('CSV 已导出')
  }

  const exportJson = () => {
    const data = {
      exportedAt: new Date().toISOString(),
      shares: shares.map((s) => ({
        name: s.name,
        path: s.path,
        permissions: accounts.map((a) => ({
          account: a.name,
          accountType: a.type,
          access: matrix[s.name]?.[a.name] || '-'
        }))
      }))
    }
    download(JSON.stringify(data, null, 2), `permission-matrix-${Date.now()}.json`, 'application/json')
    message.success('JSON 已导出')
  }

  const columns = [
    { title: '共享', dataIndex: 'name', width: 160, fixed: 'left' as const },
    ...accounts.map((a) => ({
      title: a.name,
      dataIndex: `acct_${a.name}`,
      width: 90,
      render: (v: Access) => (v && v !== '-' ? <Tag color={ACCESS_TAG_COLOR[v]}>{ACCESS_LABEL[v]}</Tag> : <span className="text-fog">-</span>)
    }))
  ]

  const dataSource = shares.map((s) => {
    const row: Record<string, unknown> = { name: s.name, key: s.name }
    const m = matrix[s.name] || {}
    accounts.forEach((a) => {
      row[`acct_${a.name}`] = m[a.name] || '-'
    })
    return row
  })

  return (
    <div className="glass-card p-3">
      <div className="mb-3 flex items-center justify-between">
        <span className="text-sm text-fog">
          矩阵单元格按 Deny 优先显示（安全语义）。{shares.length} 共享 × {accounts.length} 账号。
        </span>
        <Space>
          {loading ? (
            <Button icon={<StopOutlined />} onClick={cancel}>
              取消
            </Button>
          ) : (
            <Button icon={<ReloadOutlined />} onClick={load}>
              重新加载
            </Button>
          )}
          <Button icon={<ExportOutlined />} onClick={exportCsv} disabled={loading || !shares.length}>
            导出 CSV
          </Button>
          <Button icon={<ExportOutlined />} onClick={exportJson} disabled={loading || !shares.length}>
            导出 JSON
          </Button>
        </Space>
      </div>
      <Spin spinning={loading}>
        {shares.length === 0 && !loading ? (
          <Empty description="暂无共享" />
        ) : (
          <Table
            dataSource={dataSource}
            columns={columns}
            rowKey="name"
            pagination={false}
            size="small"
            scroll={{ x: 'max-content', y: 480 }}
            bordered
          />
        )}
      </Spin>
    </div>
  )
}
