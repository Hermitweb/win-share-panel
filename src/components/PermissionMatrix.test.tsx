import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp } from 'antd'
import type { LocalUser, LocalGroup, Protocol, Share, SharePermission } from '../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    share: { list: vi.fn(), permissions: vi.fn() },
    user: { list: vi.fn(), groups: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import PermissionMatrix, {
  escapeHtml,
  isWideAccount,
  buildHtmlReport,
  type Access,
  type HtmlReportInput,
} from './PermissionMatrix'

// globals:false 下 RTL auto-cleanup 不注册，手动逐用例卸载
afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  for (const key of ['createObjectURL', 'revokeObjectURL'] as const) {
    const url = URL as unknown as Record<string, unknown>
    delete url[key]
  }
})

// —— 共享 fixture：含注入串共享名/路径、宽权限账号 Everyone、各档权限标签齐全 ——

const INJECT_SHARE = '<script>alert("xss")</script>'
const INJECT_PATH = 'C:\\Shares\\<img>&"quoted"'

function mkShare(name: string, path: string, protocol: Protocol = 'smb'): Share {
  return {
    name,
    path,
    description: '',
    protocol,
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: 'Enabled',
    cached: false,
  }
}

const ACCOUNTS: { name: string; type: 'User' | 'Group' }[] = [
  { name: 'alice', type: 'User' },
  { name: 'bob', type: 'User' },
  { name: 'Everyone', type: 'Group' },
]

const SHARES: Share[] = [
  mkShare('Docs', 'D:\\Shares\\Docs'),
  mkShare('Media', '\\\\fileserver\\media', 'nfs'),
  mkShare(INJECT_SHARE, INJECT_PATH),
]

const MATRIX: Record<string, Record<string, Access>> = {
  Docs: { alice: 'Read', bob: 'Change', Everyone: 'Full' },
  Media: { alice: '-', bob: 'Deny', Everyone: 'Read' },
  [INJECT_SHARE]: { alice: 'Full', bob: '-', Everyone: '-' },
}

const REPORT_FIXTURE: HtmlReportInput = {
  shares: SHARES,
  accounts: ACCOUNTS,
  matrix: MATRIX,
  generatedAt: new Date('2026-06-01T08:00:00Z'),
}

// —— escapeHtml 纯函数：验收要求覆盖 < > & " 与 <script> 注入串 ——

describe('escapeHtml（HTML 实体转义）', () => {
  it('转义 < > & " 与单引号', () => {
    expect(escapeHtml('<a href="x">&\'</a>')).toBe(
      '&lt;a href=&quot;x&quot;&gt;&amp;&#39;&lt;/a&gt;',
    )
  })

  it('<script> 注入串转义后不再保留标签形态', () => {
    const out = escapeHtml('<script>alert(1)</script>')
    expect(out).toBe('&lt;script&gt;alert(1)&lt;/script&gt;')
    expect(out).not.toMatch(/<script/i)
  })

  it('& 最先替换：已转义文本不会被二次破坏', () => {
    expect(escapeHtml('&amp;')).toBe('&amp;amp;')
  })

  it('空值/非字符串安全兜底为空串', () => {
    expect(escapeHtml('')).toBe('')
    expect(escapeHtml(null)).toBe('')
    expect(escapeHtml(undefined)).toBe('')
  })
})

describe('isWideAccount（宽权限账号识别）', () => {
  it('识别 Everyone / Authenticated Users / Guests（含大小写与机器前缀）', () => {
    expect(isWideAccount('Everyone')).toBe(true)
    expect(isWideAccount(' everyone ')).toBe(true)
    expect(isWideAccount('NT AUTHORITY\\Authenticated Users')).toBe(true)
    expect(isWideAccount('BUILTIN\\Guests')).toBe(true)
  })

  it('普通账号不误判', () => {
    expect(isWideAccount('alice')).toBe(false)
    expect(isWideAccount('Domain Admins')).toBe(false)
    expect(isWideAccount('Authenticated')).toBe(false)
  })
})

// —— buildHtmlReport 纯函数：内容结构 + 安全断言 ——

describe('buildHtmlReport（自包含 HTML 审计报告）', () => {
  const html = buildHtmlReport(REPORT_FIXTURE)

  it('单页自包含：DOCTYPE/内联 style，无 script、无外链资源', () => {
    expect(html.toLowerCase().startsWith('<!doctype html>')).toBe(true)
    expect(html).toContain('<style>')
    expect(html).not.toMatch(/<script/i)
    expect(html).not.toMatch(/<link\b/i)
    expect(html).not.toMatch(/<iframe\b/i)
    expect(html).not.toContain('http://')
    expect(html).not.toContain('https://')
    // CSS 内也不允许外部 url()
    expect(html).not.toMatch(/url\(\s*['"]?\//i)
    // 打印友好
    expect(html).toContain('@media print')
  })

  it('含标题、生成时间（本地+ISO）、共享数/账号数概览', () => {
    expect(html).toContain('谁能访问什么 · 共享权限审计报告')
    expect(html).toContain('生成时间')
    expect(html).toContain('（本地）')
    expect(html).toContain('2026-06-01T08:00:00.000Z')
    expect(html).toContain('共享 3 个 · 账号 3 个')
  })

  it('概览表每共享一行：名称/路径/协议/授权数/最高权限', () => {
    expect(html).toContain(
      '<th>名称</th><th>路径</th><th>协议</th><th>授权数</th><th>最高权限</th>',
    )
    expect(html).toContain('<td>Docs</td>')
    expect(html).toContain('D:\\Shares\\Docs')
    expect(html).toContain('<td>NFS</td>')
    // Docs 三个账号均有授权（含 Deny 条目），最高为完全
    expect(html).toMatch(
      /Docs<\/td><td>D:\\Shares\\Docs<\/td><td>SMB<\/td><td class="num">3<\/td><td class="access-full">完全<\/td>/,
    )
  })

  it('明细矩阵行列齐全且为中文权限标签', () => {
    expect(html).toContain('<th>alice</th>')
    expect(html).toContain('<th>bob</th>')
    // 宽权限账号列头带高亮与标注
    expect(html).toContain('<th class="wide">Everyone（宽权限）</th>')
    // 每行行首为共享名，三个共享齐全
    for (const name of ['Docs', 'Media']) {
      expect(html).toContain(`<td>${name}</td>`)
    }
    // 中文标签全覆盖
    for (const label of ['完全', '更改', '只读', '拒绝']) expect(html).toContain(label)
    expect(html).toContain('<td class="access-deny">拒绝</td>')
    expect(html).toContain('<td class="access-read">只读</td>')
    expect(html).toContain('<td class="access-change">更改</td>')
  })

  it('宽权限账号高亮并附一句风险说明', () => {
    expect(html).toContain('class="wide"')
    expect(html).toContain('宽权限账号提示')
    expect(html).toContain('风险说明')
    expect(html).toContain('Everyone')
  })

  it('注入串共享名/路径被转义为文本，不产生标记', () => {
    expect(html).toContain('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;')
    expect(html).toContain('C:\\Shares\\&lt;img&gt;&amp;&quot;quoted&quot;')
    expect(html).not.toContain(INJECT_SHARE)
    expect(html).not.toContain('<img>')
  })

  it('页脚带“仅共享权限，未含 NTFS 有效权限”诚实边界声明', () => {
    expect(html).toContain('仅共享权限')
    expect(html).toContain('NTFS')
  })

  it('空数据不崩溃且概览为 0', () => {
    const empty = buildHtmlReport({
      shares: [],
      accounts: [],
      matrix: {},
      generatedAt: REPORT_FIXTURE.generatedAt,
    })
    expect(empty).toContain('共享 0 个 · 账号 0 个')
    expect(empty).not.toMatch(/<script/i)
  })
})

// —— 组件集成：按钮并列 + 点击复用同一 download 助手 ——

function permFor(shareName: string): SharePermission[] {
  const row = MATRIX[shareName] || {}
  const out: SharePermission[] = []
  for (const a of ACCOUNTS) {
    const v = row[a.name]
    if (!v || v === '-') continue
    out.push({
      shareName,
      account: a.name,
      accountType: a.type,
      access: v === 'Deny' ? 'NoAccess' : v,
      deny: v === 'Deny',
    })
  }
  return out
}

beforeEach(() => {
  stub.share.list.mockResolvedValue(SHARES)
  stub.user.list.mockResolvedValue([{ name: 'alice' }, { name: 'bob' }] as unknown as LocalUser[])
  stub.user.groups.mockResolvedValue([{ name: 'Everyone' }] as unknown as LocalGroup[])
  stub.share.permissions.mockImplementation((name: string) => Promise.resolve(permFor(name)))
})

describe('PermissionMatrix HTML 导出（组件）', () => {
  it('导出报告(HTML) 与 CSV/JSON 并列，数据加载完成后启用', async () => {
    const { container } = render(
      <AntdApp>
        <PermissionMatrix />
      </AntdApp>,
    )
    const htmlBtn = screen.getByRole('button', { name: /导出报告\s*\(HTML\)/ })
    expect(screen.getByRole('button', { name: /导出\s*CSV/ })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /导出\s*JSON/ })).toBeInTheDocument()
    // 加载完成与表格提交在同一次 React 渲染：冷启动 jsdom 下 antd Table 首挂载可超默认 1s
    await waitFor(() => expect(htmlBtn).toBeEnabled(), { timeout: 4000 })
    // 固定列+滚动模式下 rc-table 会复制表头节点，用 getAllByText 断存在性
    expect(screen.getAllByText('alice').length).toBeGreaterThanOrEqual(1)
    expect(screen.getAllByText('Everyone').length).toBeGreaterThanOrEqual(1)
    // 表格内注入串按文本渲染，不产生可执行标记
    expect(container.querySelector('script')).toBeNull()
  })

  it('点击导出：经同一 download 助手产出转义后的自包含 HTML 文件', async () => {
    // 捕获 download 助手创建的 Blob 内容（不触碰真实 URL.createObjectURL/导航）
    const blobs: { parts: unknown[]; type?: string }[] = []
    class FakeBlob {
      constructor(parts: unknown[], options?: { type?: string }) {
        blobs.push({ parts, type: options?.type })
      }
    }
    vi.stubGlobal('Blob', FakeBlob)
    const createObjectURL = vi.fn(() => 'blob:report-mock')
    const revokeObjectURL = vi.fn()
    Object.defineProperty(URL, 'createObjectURL', { value: createObjectURL, configurable: true })
    Object.defineProperty(URL, 'revokeObjectURL', { value: revokeObjectURL, configurable: true })
    // jsdom 对锚点点击下载会打“navigation not implemented”噪音，静音之
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const anchors: HTMLAnchorElement[] = []
    const origCreate = document.createElement.bind(document)
    vi.spyOn(document, 'createElement').mockImplementation(((
      tag: string,
      opts?: ElementCreationOptions,
    ) => {
      const el = origCreate(tag as keyof HTMLElementTagNameMap, opts) as HTMLElement
      if (tag === 'a') anchors.push(el as HTMLAnchorElement)
      return el
    }) as unknown as typeof document.createElement)

    render(
      <AntdApp>
        <PermissionMatrix />
      </AntdApp>,
    )
    const htmlBtn = await screen.findByRole('button', { name: /导出报告\s*\(HTML\)/ })
    await waitFor(() => expect(htmlBtn).toBeEnabled(), { timeout: 4000 })
    fireEvent.click(htmlBtn)

    await waitFor(() => expect(blobs).toHaveLength(1))
    const html = String(blobs[0].parts[0])
    expect(blobs[0].type).toContain('text/html')
    expect(html.toLowerCase().startsWith('<!doctype html>')).toBe(true)
    expect(html).toContain('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;')
    expect(html).not.toMatch(/<script/i)
    expect(html).not.toContain('https://')
    expect(html).toContain('class="wide"')
    // 同一 download 助手：blob → objectURL → a[download] → revoke
    const anchor = anchors[anchors.length - 1]
    expect(anchor.download).toMatch(/^permission-report-\d+\.html$/)
    expect(createObjectURL).toHaveBeenCalledTimes(1)
    expect(revokeObjectURL).toHaveBeenCalledTimes(1)
  })
})
