import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor, within } from '@testing-library/react'
import { App as AntdApp } from 'antd'
import type { UpdateCheckResult } from '../types'

// 真实走 src/api.ts（它在模块加载时读 window.winshare），只把 IPC 边界打桩——
// 照抄 JournalDrawer.test.tsx 的做法：vi.hoisted 早于 import 求值。
const stub = vi.hoisted(() => {
  const s = {
    update: { check: vi.fn() },
    system: { openExternal: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import UpdateChecker from './UpdateChecker'

const RELEASES = 'https://github.com/Hermitweb/win-share-panel/releases/latest'

const HAS_UPDATE: UpdateCheckResult = {
  current: '1.1.0',
  latest: '1.2.0',
  hasUpdate: true,
  releaseUrl: 'https://github.com/Hermitweb/win-share-panel/releases/tag/v1.2.0',
  publishedAt: '2026-10-01T00:00:00Z',
  unavailable: false,
  reason: null,
}
const UP_TO_DATE: UpdateCheckResult = {
  ...HAS_UPDATE,
  latest: '1.1.0',
  hasUpdate: false,
  releaseUrl: RELEASES,
  publishedAt: null,
}
const UNREACHABLE: UpdateCheckResult = {
  current: '1.1.0',
  latest: '',
  hasUpdate: false,
  releaseUrl: RELEASES,
  publishedAt: null,
  unavailable: true,
  reason:
    '无法连接到更新源（Hermitweb/win-share-panel）：fetch failed。若处于受限网络，可配置代理后重试，或直接打开下载页手动查看。',
}

function setup() {
  render(
    <AntdApp>
      <UpdateChecker />
    </AntdApp>,
  )
  return within(screen.getByTestId('update-checker'))
}

const click = (name: RegExp) => fireEvent.click(screen.getByRole('button', { name }))

describe('UpdateChecker（检查更新卡片）', () => {
  beforeEach(() => {
    stub.update.check.mockReset()
    stub.system.openExternal.mockReset()
  })
  afterEach(cleanup)

  it('尚未检查时不给任何版本结论（不预先声称"已是最新"）', () => {
    const card = setup()
    expect(card.getByText(/尚未检查/)).toBeInTheDocument()
    expect(card.queryByText('已是最新')).not.toBeInTheDocument()
    expect(card.queryByText('有新版本')).not.toBeInTheDocument()
  })

  it('有新版本：显示当前/最新与「有新版本」，并明说不会自动安装', async () => {
    stub.update.check.mockResolvedValue(HAS_UPDATE)
    const card = setup()
    click(/检查更新/)
    await waitFor(() => expect(card.getByText('1.2.0')).toBeInTheDocument())
    expect(card.getByText('1.1.0')).toBeInTheDocument()
    expect(card.getByText('有新版本')).toBeInTheDocument()
    expect(card.getByText(/不会自动下载或安装/)).toBeInTheDocument()
  })

  it('已是最新：显示「已是最新」', async () => {
    stub.update.check.mockResolvedValue(UP_TO_DATE)
    const card = setup()
    click(/检查更新/)
    await waitFor(() => expect(card.getByText('已是最新')).toBeInTheDocument())
  })

  it('关键回归钉：检查失败必须显示原因，且绝不显示「已是最新」/「有新版本」', async () => {
    stub.update.check.mockResolvedValue(UNREACHABLE)
    const card = setup()
    click(/检查更新/)
    await waitFor(() => expect(card.getByText('检查更新失败')).toBeInTheDocument())
    expect(card.getByText(/无法连接到更新源/)).toBeInTheDocument()
    // 把失败伪装成"绿"是这张卡片最不能犯的错，因此单独钉住
    expect(card.queryByText('已是最新')).not.toBeInTheDocument()
    expect(card.queryByText('有新版本')).not.toBeInTheDocument()
  })

  it('打开下载页：走 system:openExternal 且失败不静默', async () => {
    stub.system.openExternal.mockRejectedValue(new Error('仅支持 http/https 链接'))
    const card = setup()
    click(/打开下载页/)
    await waitFor(() => expect(stub.system.openExternal).toHaveBeenCalledTimes(1))
    expect(String(stub.system.openExternal.mock.calls[0][0])).toContain(
      'github.com/Hermitweb/win-share-panel',
    )
    await waitFor(() => expect(card.getByText(/仅支持 http\/https 链接/)).toBeInTheDocument())
  })

  it('通道级异常：报错但不给假结论', async () => {
    stub.update.check.mockRejectedValue(new Error('界面与后台版本不一致：缺少「update」通道。'))
    const card = setup()
    click(/检查更新/)
    await waitFor(() => expect(card.getByText(/界面与后台版本不一致/)).toBeInTheDocument())
    expect(card.queryByText('已是最新')).not.toBeInTheDocument()
  })
})
