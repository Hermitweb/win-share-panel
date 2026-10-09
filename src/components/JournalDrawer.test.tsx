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

import JournalDrawer from './JournalDrawer'
import { useAppStore } from '../stores/appStore'

// 一条可撤销（SMB 删除）+ 一条仅留档（FTP 创建）
const UNDOABLE: JournalEntry = {
  id: 'j1',
  ts: 1730000000000,
  action: 'delete',
  protocol: 'smb',
  name: 'Reports',
  detail: '路径 E:\\Reports，3 条授权已存档',
  undoable: true,
}
const ARCHIVE: JournalEntry = {
  id: 'j2',
  ts: 1729990000000,
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

describe('JournalDrawer（操作回收站抽屉）', () => {
  it('两类行渲染：新→旧列出，可撤销行给按钮、仅留档行只说明', async () => {
    renderDrawer()

    // 两条记录均出现，且时间(dayjs)格式化正确
    await screen.findByText(dayjs(UNDOABLE.ts).format('YYYY-MM-DD HH:mm:ss'))
    expect(screen.getByText(dayjs(ARCHIVE.ts).format('YYYY-MM-DD HH:mm:ss'))).toBeInTheDocument()

    // 操作类型中文标签、协议 Tag、共享名、detail 均显示
    expect(screen.getByText('删除')).toBeInTheDocument()
    expect(screen.getByText('创建')).toBeInTheDocument()
    expect(screen.getByText('SMB')).toBeInTheDocument()
    expect(screen.getByText('FTP')).toBeInTheDocument()
    expect(screen.getByText('Reports')).toBeInTheDocument()
    expect(screen.getByText('Videos')).toBeInTheDocument()
    expect(screen.getByText(UNDOABLE.detail as string)).toBeInTheDocument()
    expect(screen.getByText(ARCHIVE.detail as string)).toBeInTheDocument()

    // 新→旧：可撤销的 j1（时间更晚）排在仅留档的 j2 之前（用 data-row-key 精确取数据行，避开表头/测量行）
    const bodyRows = Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]')).map(
      (r) => r.textContent,
    )
    expect(bodyRows[0]).toContain('Reports')
    expect(bodyRows[1]).toContain('Videos')

    // 仅 1 个可点「撤销」按钮（j1）；j2 不给坏按钮，改显示仅留档说明
    expect(undoButtonsInTable()).toBe(1)
    expect(screen.getByText('仅留档、不支持一键撤销')).toBeInTheDocument()
  })

  it('空态使用 Empty：无记录时显示占位而非报错', async () => {
    stub.state.journalList.mockResolvedValue([])
    renderDrawer()
    expect(await screen.findByText('暂无操作记录')).toBeInTheDocument()
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

  it('清空记录：危险操作带 Popconfirm，确认后调用 clearJournal 且列表变空', async () => {
    renderDrawer()
    await screen.findByText('Reports')

    await confirmPopaction(/清空记录/, '确认清空')

    await waitFor(() => expect(stub.state.journalClear).toHaveBeenCalledTimes(1))
    expect(await screen.findByText('暂无操作记录')).toBeInTheDocument()
    expect(screen.queryByText('Reports')).not.toBeInTheDocument()
  })
})
