import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp } from 'antd'
import dayjs from 'dayjs'
import type { JournalEntry } from '../types'

// 真实走 src/stores/appStore（journal/loadJournal/undoJournal/clearJournal），
// 仅把主进程 IPC 边界 window.winshare 打桩——照抄 Sessions.test.tsx 的做法。
// vi.hoisted 早于 import 求值，确保 api.ts 里 `const api = window.winshare` 捕获到 stub。
const stub = vi.hoisted(() => {
  const s = {
    state: {
      journalList: vi.fn(),
      journalUndo: vi.fn(),
      journalClear: vi.fn(),
    },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import JournalDrawer, { archiveReason, relTime } from './JournalDrawer'
import { useAppStore } from '../stores/appStore'

// 时间语义是"多久以前"，所以夹具必须相对当下构造（多留 20~30s 余量，
// 避免用例跑得慢时跨过分钟/天边界导致文案变化）
const UNDOABLE: JournalEntry = {
  id: 'j1',
  ts: Date.now() - 5 * 60_000 - 20_000,
  action: 'delete',
  protocol: 'smb',
  name: 'Reports',
  detail: '路径 E:\\Reports，3 条授权已存档',
  undoable: true,
}
const ARCHIVE: JournalEntry = {
  id: 'j2',
  ts: Date.now() - 2 * 24 * 3600_000 - 30_000,
  action: 'create',
  protocol: 'ftp',
  name: 'Videos',
  detail: '路径 E:\\ftp\\site',
  undoable: false,
}

function renderDrawer() {
  return render(
    <AntdApp>
      <JournalDrawer open onClose={vi.fn()} />
    </AntdApp>,
  )
}

// 点击 Popconfirm 触发按钮后，按去空格文本命中确认按钮（不依赖 antd 内部结构变化）
async function confirmPopaction(triggerText: string | RegExp, okText: string): Promise<void> {
  const trigger = screen.getByRole('button', { name: triggerText })
  fireEvent.click(trigger)
  await waitFor(() =>
    expect(document.querySelector('.ant-popover, .ant-popconfirm')).not.toBeNull(),
  )
  const okBtn = Array.from(
    document.querySelectorAll('.ant-popover button, .ant-popconfirm button'),
  ).find((b) => (b.textContent || '').replace(/\s/g, '') === okText)
  expect(okBtn).toBeTruthy()
  fireEvent.click(okBtn!)
}

// 表格行内的「撤销」触发按钮计数（限定 tbody，避开 Popconfirm 浮层的"确认撤销"按钮）
function undoButtonsInTable(): number {
  return Array.from(document.querySelectorAll('.ant-table-tbody button')).filter((b) =>
    (b.textContent || '').replace(/\s/g, '').includes('撤销'),
  ).length
}

// 当前渲染出的数据行（避开表头/测量行）
function bodyRows(): string[] {
  return Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]')).map(
    (r) => r.textContent || '',
  )
}

/** antd Select 选项在 portal 里且 jsdom 无稳定 role=option → 按选项文本点选 */
async function pickSelectOption(combobox: HTMLElement, label: string) {
  fireEvent.mouseDown(combobox)
  const target = await waitFor(() => {
    const el = Array.from(document.querySelectorAll('.ant-select-item-option-content')).find((n) =>
      (n.textContent || '').includes(label),
    )
    if (!el) throw new Error(`下拉项未出现：${label}`)
    return el as HTMLElement
  })
  fireEvent.click(target)
}

beforeEach(() => {
  useAppStore.setState({ journal: [] })
  stub.state.journalList.mockReset().mockResolvedValue([UNDOABLE, ARCHIVE])
  stub.state.journalUndo.mockReset().mockResolvedValue('已撤销')
  stub.state.journalClear.mockReset().mockResolvedValue(0)
})

afterEach(async () => {
  cleanup()
  // 冲刷卸载竞态产生的微任务，避免假阳性 unhandled rejections
  await new Promise((r) => setTimeout(r, 0))
})

describe('relTime（相对时间：回收站的时间语义）', () => {
  const now = Date.parse('2026-10-09T12:00:00')
  it('刚刚 / 分钟 / 小时 / 天', () => {
    expect(relTime(now - 5_000, now)).toBe('刚刚')
    expect(relTime(now - 5 * 60_000, now)).toBe('5 分钟前')
    expect(relTime(now - 3 * 3600_000, now)).toBe('3 小时前')
    expect(relTime(now - 2 * 24 * 3600_000, now)).toBe('2 天前')
  })
  it('超过一周改用绝对时间（"37 天前"要心算，不如直接给日期）', () => {
    expect(relTime(now - 37 * 24 * 3600_000, now)).toBe('2026-09-02 12:00')
  })
  it('未来时间/时钟回拨：不硬凹相对值', () => {
    expect(relTime(now + 60_000, now)).toBe(dayjs(now + 60_000).format('YYYY-MM-DD HH:mm'))
  })
})

describe('archiveReason（不可撤销的真实理由，按记录区分）', () => {
  it('创建：说明可手工删除，而不是一句通用话术', () => {
    expect(archiveReason(ARCHIVE)).toContain('手工删除')
  })
  it('非 SMB 删除：点明该协议没有可还原快照', () => {
    const ftpDelete: JournalEntry = { ...ARCHIVE, action: 'delete', protocol: 'webdav' }
    expect(archiveReason(ftpDelete)).toContain('WEBDAV')
    expect(archiveReason(ftpDelete)).toContain('仅留档')
  })
  it('其余（SMB 但无快照）：如实说未留快照', () => {
    const smbNoSnapshot: JournalEntry = { ...UNDOABLE, undoable: false }
    expect(archiveReason(smbNoSnapshot)).toContain('未留下可还原的快照')
  })
})

describe('JournalDrawer（操作回收站抽屉）', () => {
  it('两类行渲染：新→旧列出，可撤销行给按钮、仅留档行只说明', async () => {
    renderDrawer()

    // 时间按"多久以前"呈现（更贴近"我刚才删的那个"），精确时间戳留在 title 里供取证
    expect(await screen.findByText('5 分钟前')).toBeInTheDocument()
    expect(screen.getByText('2 天前')).toBeInTheDocument()
    expect(screen.getByTitle(dayjs(UNDOABLE.ts).format('YYYY-MM-DD HH:mm:ss'))).toBeInTheDocument()

    // 类型中文标签、协议 Tag、共享名、detail 均显示
    expect(screen.getByText('删除')).toBeInTheDocument()
    expect(screen.getByText('创建')).toBeInTheDocument()
    expect(screen.getByText('SMB')).toBeInTheDocument()
    expect(screen.getByText('FTP')).toBeInTheDocument()
    expect(screen.getByText('Reports')).toBeInTheDocument()
    expect(screen.getByText('Videos')).toBeInTheDocument()
    expect(screen.getByText(UNDOABLE.detail as string)).toBeInTheDocument()
    expect(screen.getByText(ARCHIVE.detail as string)).toBeInTheDocument()

    // 新→旧：可撤销的 j1（时间更晚）排在仅留档的 j2 之前
    const rows = bodyRows()
    expect(rows[0]).toContain('Reports')
    expect(rows[1]).toContain('Videos')

    // 仅 1 个可点「撤销」按钮（j1）；j2 不给坏按钮，改显示「仅留档」标记
    expect(undoButtonsInTable()).toBe(1)
    expect(screen.getByText('仅留档')).toBeInTheDocument()
  })

  it('摘要给出总量/可撤销量与最近一次时间；撤销按钮带记录级 aria-label', async () => {
    renderDrawer()
    await screen.findByText('Reports')

    // 摘要行文本跨 <b> 分片，用整页文本断言（getByText(/共/) 会因分片多命中）
    expect(document.body.textContent).toMatch(/共\s*2\s*条记录/)
    expect(document.body.textContent).toMatch(/1\s*条可一键撤销/)
    expect(document.body.textContent).toMatch(/最近一次\s*5 分钟前/)

    // 屏幕阅读器/无鼠标场景下，光一个"撤销"无法知道撤销的是哪条
    expect(screen.getByRole('button', { name: '撤销 Reports 的删除' })).toBeInTheDocument()
  })

  it('撤销前的二次确认指明恢复对象（不是泛化的"将尝试恢复"）', async () => {
    renderDrawer()
    await screen.findByText('Reports')
    fireEvent.click(screen.getByRole('button', { name: /撤\s*销/ }))
    await waitFor(() =>
      expect(document.querySelector('.ant-popover, .ant-popconfirm')).not.toBeNull(),
    )
    const pop = document.querySelector('.ant-popover, .ant-popconfirm') as HTMLElement
    expect(pop.textContent).toContain('按留档快照恢复「Reports」')
    expect(pop.textContent).toContain('3 条授权已存档')
  })

  it('搜索：按共享名/详情过滤，无结果时给"清除筛选"而不是空白', async () => {
    renderDrawer()
    await screen.findByText('Reports')

    fireEvent.change(screen.getByLabelText('搜索操作记录'), { target: { value: 'Videos' } })
    await waitFor(() => expect(bodyRows()).toHaveLength(1))
    expect(bodyRows()[0]).toContain('Videos')

    fireEvent.change(screen.getByLabelText('搜索操作记录'), { target: { value: '不存在的共享' } })
    expect(await screen.findByText('没有匹配的记录')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '清除筛选' }))
    await waitFor(() => expect(bodyRows()).toHaveLength(2))
  })

  it('按类型筛选 + 只看可撤销，两者与搜索可叠加', async () => {
    renderDrawer()
    await screen.findByText('Reports')

    // 只看可撤销 → 只剩 j1
    fireEvent.click(screen.getByRole('switch', { name: '只看可撤销' }))
    await waitFor(() => expect(bodyRows()).toHaveLength(1))
    expect(bodyRows()[0]).toContain('Reports')

    // 叠加类型筛选：j1 是删除，选"创建"后应无匹配
    await pickSelectOption(screen.getByLabelText('按操作类型筛选'), '创建')
    expect(await screen.findByText('没有匹配的记录')).toBeInTheDocument()

    // 关掉"只看可撤销"仍受类型筛选约束 → 只剩 j2（创建）
    fireEvent.click(screen.getByRole('switch', { name: '只看可撤销' }))
    await waitFor(() => expect(bodyRows()).toHaveLength(1))
    expect(bodyRows()[0]).toContain('Videos')
  })

  it('空态区分两种处境：从未有过操作 vs 筛选后无结果', async () => {
    stub.state.journalList.mockResolvedValue([])
    renderDrawer()
    // 空列表给引导，而不是只写一句"暂无"
    expect(await screen.findByText('还没有可撤销的操作')).toBeInTheDocument()
    expect(
      screen.getByText(/删除或禁用共享、修改共享权限后，会在这里留下可撤销的记录/),
    ).toBeInTheDocument()
  })

  it('撤销成功：显示 undoJournal 返回文案并刷新列表', async () => {
    stub.state.journalUndo.mockResolvedValueOnce('已恢复共享"Reports"（3 条授权）')
    renderDrawer()
    await screen.findByText('Reports')

    const before = stub.state.journalList.mock.calls.length
    await confirmPopaction(/撤\s*销/, '确认撤销')

    // 走 store.undoJournal → 主进程 journalUndo(id)
    await waitFor(() => expect(stub.state.journalUndo).toHaveBeenCalledWith('j1'))
    // 成功文案来自 undoJournal 返回值
    expect(await screen.findByText(/已恢复共享"Reports"/)).toBeInTheDocument()
    // undoJournal 内部会再拉一次列表 → 刷新
    await waitFor(() => expect(stub.state.journalList.mock.calls.length).toBeGreaterThan(before))
  })

  it('撤销失败：透传原始错误文案，不吞错不假成功、不刷新', async () => {
    stub.state.journalUndo.mockRejectedValueOnce(
      new Error('恢复失败：共享"Reports"已存在，无需撤销'),
    )
    renderDrawer()
    await screen.findByText('Reports')

    const before = stub.state.journalList.mock.calls.length
    await confirmPopaction(/撤\s*销/, '确认撤销')

    await waitFor(() => expect(stub.state.journalUndo).toHaveBeenCalledWith('j1'))
    // 原始错误文案如实显示
    expect(await screen.findByText(/恢复失败：共享"Reports"已存在/)).toBeInTheDocument()
    // 失败不触发刷新（store.undoJournal 抛错早于 loadJournal）
    expect(stub.state.journalList.mock.calls.length).toBe(before)
    // 该行仍在，撤销按钮仍可点（未假成功移除）
    expect(undoButtonsInTable()).toBe(1)
  })

  it('清空记录：确认框写明条数与影响范围，确认后调用 clearJournal 且列表变空', async () => {
    renderDrawer()
    await screen.findByText('Reports')

    fireEvent.click(screen.getByRole('button', { name: /清空记录/ }))
    await waitFor(() =>
      expect(document.querySelector('.ant-popover, .ant-popconfirm')).not.toBeNull(),
    )
    const pop = document.querySelector('.ant-popover, .ant-popconfirm') as HTMLElement
    expect(pop.textContent).toContain('确认清空全部 2 条操作记录')
    expect(pop.textContent).toContain('已恢复的共享不受影响')

    const okBtn = Array.from(
      document.querySelectorAll('.ant-popover button, .ant-popconfirm button'),
    ).find((b) => (b.textContent || '').replace(/\s/g, '') === '确认清空')
    fireEvent.click(okBtn!)

    await waitFor(() => expect(stub.state.journalClear).toHaveBeenCalledTimes(1))
    expect(await screen.findByText('还没有可撤销的操作')).toBeInTheDocument()
    expect(screen.queryByText('Reports')).not.toBeInTheDocument()
  })
})

// 批2（docs/audit/05-renderer-debt.md §3.1 点 4）：打开/关闭生命周期与 stale-while-revalidate。
const settle = () => new Promise((r) => setTimeout(r, 200))
const spinning = () => document.querySelector('.ant-spin-spinning') !== null

/** 与 renderDrawer 同层级，供 rerender 复用（增删包裹层会整棵重挂，破坏"只加载一次"断言） */
function wrapDrawer(open: boolean) {
  return (
    <AntdApp>
      <JournalDrawer open={open} onClose={vi.fn()} />
    </AntdApp>
  )
}

describe('JournalDrawer · 打开生命周期（set-state-in-effect 迁移回归）', () => {
  it('打开→恰好 1 次 api.state.journalList；重复渲染与静置 200ms 都不重发', async () => {
    const { rerender } = renderDrawer()
    await waitFor(() => expect(stub.state.journalList).toHaveBeenCalledTimes(1))

    rerender(wrapDrawer(true))
    await settle()
    expect(stub.state.journalList).toHaveBeenCalledTimes(1)
  })

  it('关闭再打开：再 1 次（合计 2）', async () => {
    const { rerender } = renderDrawer()
    await waitFor(() => expect(stub.state.journalList).toHaveBeenCalledTimes(1))

    rerender(wrapDrawer(false))
    await settle()
    rerender(wrapDrawer(true))

    await waitFor(() => expect(stub.state.journalList).toHaveBeenCalledTimes(2))
    expect(await screen.findByText('Reports')).toBeInTheDocument()
  })

  it('stale-while-revalidate：store 为空→打开即转圈；store 已有数据→不转圈、立即渲染旧数据', async () => {
    // ① store 为空 + 列表请求悬停：加载态必须出现（不是先白一下再出内容）
    let resolveList: (v: unknown[]) => void = () => {}
    stub.state.journalList.mockImplementation(
      () => new Promise((res) => (resolveList = res as (v: unknown[]) => void)),
    )
    const first = renderDrawer()
    await waitFor(() => expect(spinning()).toBe(true))
    resolveList([UNDOABLE, ARCHIVE])
    await waitFor(() => expect(screen.getByText('Reports')).toBeInTheDocument())
    first.unmount()

    // ② store 已有数据：打开那一刻就能读，不出现加载态
    useAppStore.setState({ journal: [UNDOABLE, ARCHIVE] })
    stub.state.journalList.mockResolvedValue([UNDOABLE, ARCHIVE])
    renderDrawer()
    expect(spinning()).toBe(false)
    expect(screen.getByText('Reports')).toBeInTheDocument()
  })

  it('列表拉取失败：原因可见、不假成功；列表按 store 既有失败语义清空（本次迁移未改动该行为）', async () => {
    // 先有数据（证明"失败"不是"本来就没有"），再让下一次拉取失败
    useAppStore.setState({ journal: [UNDOABLE, ARCHIVE] })
    stub.state.journalList.mockRejectedValue(new Error('留档读取失败：文件被占用'))

    const { rerender } = renderDrawer()
    await waitFor(() => expect(document.body.textContent).toContain('留档读取失败：文件被占用'))
    // 绝不假成功
    expect(document.body.textContent).not.toContain('操作记录已清空')

    // 关闭再打开，仍如实报错（错误不被吞掉）
    rerender(wrapDrawer(false))
    await settle()
    rerender(wrapDrawer(true))
    await waitFor(() => expect(stub.state.journalList).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(document.body.textContent).toContain('留档读取失败：文件被占用'))
  })
})
