import { useEffect, useRef, useState } from 'react'
import { Table, Button, Space, Tag, App, Spin, Empty } from 'antd'
import { ExportOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { Share, LocalUser, LocalGroup, SharePermission, Protocol } from '../types'

export type Access = 'Full' | 'Change' | 'Read' | 'Deny' | '-'

const ACCESS_TAG_COLOR: Record<Access, string> = {
  Full: 'blue',
  Change: 'purple',
  Read: 'default',
  Deny: 'red',
  '-': '',
}

const ACCESS_LABEL: Record<Access, string> = {
  Full: '完全',
  Change: '更改',
  Read: '只读',
  Deny: '拒绝',
  '-': '-',
}

// 同一账号在多个权限条目中时，按安全语义取最严格：Deny > Full > Change > Read
const PRIORITY: Record<Exclude<Access, '-'>, number> = { Deny: 0, Full: 1, Change: 2, Read: 3 }
function pickAccess(perms: SharePermission[]): Access {
  if (!perms.length) return '-'
  const sorted = perms
    .map(
      (p) =>
        (p.deny || p.access === 'NoAccess' ? 'Deny' : (p.access as Access)) as Exclude<Access, '-'>,
    )
    .sort((a, b) => PRIORITY[a] - PRIORITY[b])
  return sorted[0]
}

// —— 以下三个纯函数供 HTML 审计报告导出使用，单独导出以便脱离 DOM 单测（t8）——

// HTML 实体转义：共享名/账号名/路径等一切来自系统的字符串必须经此函数后再拼进模板。
// & 必须最先替换，否则后续实体（&lt; 等）会被二次转义。
export function escapeHtml(value: unknown): string {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

// Everyone / Authenticated Users / Guests 等“代表一批人”的宽权限账号；
// Windows 侧可能返回带机器/域前缀的形式（如 NT AUTHORITY\Authenticated Users），取末段比对。
const WIDE_ACCOUNT_NAMES = new Set(['everyone', 'authenticated users', 'guests'])
export function isWideAccount(name: string): boolean {
  const bare = name.includes('\\') ? name.slice(name.lastIndexOf('\\') + 1) : name
  return WIDE_ACCOUNT_NAMES.has(bare.trim().toLowerCase())
}

const PROTOCOL_LABEL: Record<Protocol, string> = {
  smb: 'SMB',
  nfs: 'NFS',
  ftp: 'FTP',
  webdav: 'WebDAV',
}

const ACCESS_HTML_CLASS: Record<Access, string> = {
  Full: 'access-full',
  Change: 'access-change',
  Read: 'access-read',
  Deny: 'access-deny',
  '-': 'access-none',
}

export interface HtmlReportInput {
  shares: Pick<Share, 'name' | 'path' | 'protocol'>[]
  accounts: { name: string; type: 'User' | 'Group' }[]
  matrix: Record<string, Record<string, Access>>
  generatedAt: Date
}

// 报告页内联样式：浅色底、打印友好；不引用任何外部字体/样式，避免出现外链。
const REPORT_CSS = `
:root{color-scheme:light}
body{margin:32px auto;max-width:1080px;padding:0 16px;font-family:"Segoe UI","Microsoft YaHei UI","Microsoft YaHei",system-ui,sans-serif;font-size:13px;line-height:1.6;color:#22303d;background:#f6f8fb;print-color-adjust:exact;-webkit-print-color-adjust:exact}
h1{margin:0 0 2px;font-size:22px}
h2{margin:26px 0 8px;font-size:16px;border-left:4px solid #3c65c3;padding-left:8px}
.meta{margin:2px 0;color:#5c6c7c}
table{width:100%;border-collapse:collapse;background:#fff;font-size:13px}
th,td{border:1px solid #c8d3df;padding:5px 8px;text-align:left;vertical-align:top}
th{background:#eef3fa;white-space:nowrap;font-weight:600}
td.num{text-align:right;white-space:nowrap}
.access-full{background:#e7f0ff}
.access-change{background:#f1ebfd}
.access-read{background:#f3f5f7}
.access-deny{background:#ffe6e4;color:#a4141a;font-weight:600}
.wide{background:#fff3cd;font-weight:700}
.warnbox{background:#fff8e6;border:1px solid #dfc26a;border-radius:6px;padding:10px 14px}
.okbox{background:#eef7ee;border:1px solid #9cc7a0;border-radius:6px;padding:10px 14px}
footer{margin-top:30px;border-top:1px solid #c8d3df;padding:12px 0 24px;color:#5c6c7c;font-size:12px}
@media print{body{margin:0;max-width:none;background:#fff;font-size:11px}h2{margin:14px 0 6px}tr{page-break-inside:avoid}}
`

// 生成一页自包含 HTML 审计报告：无外链资源、无脚本、样式全部内联。
// 一切来自系统的字符串（共享名/路径/账号名）都经 escapeHtml，防止共享名/账号名注入标记。
export function buildHtmlReport(input: HtmlReportInput): string {
  const { shares, accounts, matrix, generatedAt } = input
  const esc = escapeHtml
  const accessOf = (shareName: string, account: string): Access =>
    matrix[shareName]?.[account] || '-'
  const RANK: Exclude<Access, '-'>[] = ['Full', 'Change', 'Read', 'Deny']
  const shareRows = shares.map((s) => {
    const granted = accounts
      .map((a) => accessOf(s.name, a.name))
      .filter((v): v is Exclude<Access, '-'> => v !== '-')
    const highest = RANK.find((k) => granted.includes(k))
    return { s, granted, highest }
  })
  const totalEntries = shareRows.reduce((n, r) => n + r.granted.length, 0)
  const wideFindings = accounts
    .filter((a) => isWideAccount(a.name))
    .map((a) => ({ a, rows: shareRows.filter((r) => accessOf(r.s.name, a.name) !== '-') }))
    .filter((f) => f.rows.length > 0)

  const header = `
<header>
<h1>谁能访问什么 · 共享权限审计报告</h1>
<p class="meta">生成时间：${esc(generatedAt.toLocaleString())}（本地） · ${esc(generatedAt.toISOString())}（ISO）</p>
<p class="meta">概览：共享 ${shares.length} 个 · 账号 ${accounts.length} 个 · 共享权限条目 ${totalEntries} 条</p>
</header>`

  const wideSection = wideFindings.length
    ? `
<h2>宽权限账号提示</h2>
<div class="warnbox">
<ul>${wideFindings
        .map(
          (f) =>
            `<li class="wide">${esc(f.a.name)}（${esc(f.a.type === 'Group' ? '组' : '用户')}）：${f.rows
              .map((r) => esc(r.s.name))
              .join('、')}</li>`,
        )
        .join('')}</ul>
<p>风险说明：Everyone / Authenticated Users 等宽权限账号分别代表“所有访客（含匿名）”与“所有已登录用户”，一旦被授予「完全 / 更改」，任意满足条件的网络用户都可写入甚至删除共享数据，请重点复核。</p>
</div>`
    : `
<h2>宽权限账号提示</h2>
<div class="okbox">
<p>未发现 Everyone / Authenticated Users / Guests 等宽权限账号获得共享授权。风险说明：此类宽权限账号若被授予「完全 / 更改」，任意网络用户都可能读写共享数据，请避免。</p>
</div>`

  const overviewTable = `
<h2>共享概览</h2>
<table>
<thead><tr><th>名称</th><th>路径</th><th>协议</th><th>授权数</th><th>最高权限</th></tr></thead>
<tbody>${shareRows
    .map(
      ({ s, granted, highest }) =>
        `<tr><td>${esc(s.name)}</td><td>${esc(s.path)}</td><td>${esc(
          PROTOCOL_LABEL[s.protocol] ?? s.protocol,
        )}</td><td class="num">${granted.length}</td><td class="${
          highest ? ACCESS_HTML_CLASS[highest] : ''
        }">${highest ? ACCESS_LABEL[highest] : '无授权'}</td></tr>`,
    )
    .join('')}</tbody>
</table>`

  const detailTable = `
<h2>账号 × 共享权限明细</h2>
<table>
<thead><tr><th>账号 / 共享</th>${accounts
    .map((a) => {
      const wide = isWideAccount(a.name)
      return `<th${wide ? ' class="wide"' : ''}>${esc(a.name)}${wide ? '（宽权限）' : ''}</th>`
    })
    .join('')}</tr></thead>
<tbody>${shares
    .map(
      (s) =>
        `<tr><td>${esc(s.name)}</td>${accounts
          .map((a) => {
            const v = accessOf(s.name, a.name)
            const wideCell = isWideAccount(a.name) && v !== '-' ? ' wide' : ''
            return `<td class="${ACCESS_HTML_CLASS[v]}${wideCell}">${ACCESS_LABEL[v]}</td>`
          })
          .join('')}</tr>`,
    )
    .join('')}</tbody>
</table>
<p class="meta">图例：完全 = 读取/写入/删除/改权限；更改 = 读取/写入；只读 = 仅读取；拒绝 = 显式拒绝；- = 无共享权限条目。</p>`

  const footer = `
<footer>
<p><strong>边界说明：</strong>本报告仅共享权限（Share）层面统计，未合并计算 NTFS 有效权限，不代表任何账号最终实际可访问的范围；如需有效权限请结合 NTFS ACL 复核。</p>
<p>由 WinShare Panel 导出 · 自包含单页 HTML（内联样式，无脚本、无外部资源），可直接打印存档。</p>
</footer>`

  return [
    '<!DOCTYPE html>',
    '<html lang="zh-CN">',
    '<head>',
    '<meta charset="utf-8" />',
    '<title>谁能访问什么 · 共享权限审计报告</title>',
    `<style>${REPORT_CSS}</style>`,
    '</head>',
    '<body>',
    header,
    wideSection,
    overviewTable,
    detailTable,
    footer,
    '</body>',
    '</html>',
  ].join('\n')
}

// 并发上限的 Promise 池
async function mapPool<T, R>(
  items: T[],
  limit: number,
  fn: (item: T) => Promise<R>,
  shouldCancel: () => boolean,
): Promise<R[]> {
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
        call(api.user.groups).catch(() => [] as LocalGroup[]),
      ])
      // 仅展示普通共享，过滤 IPC/Special
      const normalShares = (shareList || []).filter((s) => s.type !== 'Special' && s.type !== 'IPC')
      const acctList = [
        ...(users || []).map((u) => ({ name: u.name, type: 'User' as const })),
        ...(groups || []).map((g) => ({ name: g.name, type: 'Group' as const })),
      ]
      setShares(normalShares)
      setAccounts(acctList)

      // 并发拉取每个共享的权限（上限 4）
      const perms = await mapPool(
        normalShares,
        4,
        (s) => call(() => api.share.permissions(s.name)).catch(() => [] as SharePermission[]),
        () => cancelRef.current,
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
    // 有意仅挂载时加载一次：load 引用每轮渲染变化，加入依赖会无限循环；迟到响应由 cancelRef 机制作废
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
    const csv = [header, ...rows]
      .map((r) => r.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(','))
      .join('\n')
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
          access: matrix[s.name]?.[a.name] || '-',
        })),
      })),
    }
    download(
      JSON.stringify(data, null, 2),
      `permission-matrix-${Date.now()}.json`,
      'application/json',
    )
    message.success('JSON 已导出')
  }

  const exportHtml = () => {
    const html = buildHtmlReport({ shares, accounts, matrix, generatedAt: new Date() })
    download(html, `permission-report-${Date.now()}.html`, 'text/html;charset=utf-8')
    message.success('HTML 报告已导出')
  }

  const columns = [
    { title: '共享', dataIndex: 'name', width: 160, fixed: 'left' as const },
    ...accounts.map((a) => ({
      title: a.name,
      dataIndex: `acct_${a.name}`,
      width: 90,
      render: (v: Access) =>
        v && v !== '-' ? (
          <Tag color={ACCESS_TAG_COLOR[v]}>{ACCESS_LABEL[v]}</Tag>
        ) : (
          <span className="text-fog">-</span>
        ),
    })),
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
          <Button
            icon={<ExportOutlined />}
            onClick={exportCsv}
            disabled={loading || !shares.length}
          >
            导出 CSV
          </Button>
          <Button
            icon={<ExportOutlined />}
            onClick={exportJson}
            disabled={loading || !shares.length}
          >
            导出 JSON
          </Button>
          <Button
            icon={<ExportOutlined />}
            onClick={exportHtml}
            disabled={loading || !shares.length}
          >
            导出报告(HTML)
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
