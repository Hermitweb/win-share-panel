import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp } from 'antd'
import StaleProcessNotice, { inspectBridge } from './StaleProcessNotice'

afterEach(cleanup)

/** 完整的新版桥（五个批1 通道 + relaunch 都在） */
const freshBridge = () => ({
  state: {},
  disk: {},
  security: {},
  firewall: {},
  diagnose: {},
  system: { relaunch: vi.fn(async () => undefined), autoStart: vi.fn() },
})

function renderNotice(bridge: Record<string, unknown> | undefined) {
  return render(
    <AntdApp>
      <StaleProcessNotice bridge={bridge} />
    </AntdApp>,
  )
}

describe('inspectBridge（纯函数判定）', () => {
  it('通道齐全 → 不算落后', () => {
    expect(inspectBridge(freshBridge())).toEqual({ missing: [], canRelaunch: true })
  })

  it('缺通道 → 逐个列出；桥为 undefined（无注入）时五组全缺', () => {
    const partial = freshBridge() as Record<string, unknown>
    delete partial.diagnose
    delete partial.state
    expect(inspectBridge(partial).missing).toEqual(['state', 'diagnose'])
    expect(inspectBridge(undefined).missing).toEqual([
      'state',
      'disk',
      'security',
      'firewall',
      'diagnose',
    ])
  })

  it('旧的旧进程：缺通道且连 relaunch 都没有 → canRelaunch=false', () => {
    // 这正是本功能要面对的处境：需要重启的进程，自身没有重启通道
    const old = { share: {}, user: {}, system: { autoStart: vi.fn() } }
    expect(inspectBridge(old)).toEqual({
      missing: ['state', 'disk', 'security', 'firewall', 'diagnose'],
      canRelaunch: false,
    })
  })
})

describe('StaleProcessNotice 呈现', () => {
  it('通道齐全时不打扰用户', async () => {
    renderNotice(freshBridge())
    await new Promise((r) => setTimeout(r, 20))
    expect(document.body.textContent).not.toMatch(/升级前的版本/)
  })

  it('缺通道且有 relaunch → 给出「立即重启」按钮，点击调用 relaunch', async () => {
    const bridge = freshBridge()
    delete (bridge as Record<string, unknown>).diagnose
    renderNotice(bridge)
    await screen.findByText(/后台进程仍是升级前的版本/)
    expect(screen.getByText(/缺少通道：diagnose/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: '立即重启' }))
    await waitFor(() => expect(bridge.system.relaunch).toHaveBeenCalledTimes(1))
  })

  it('缺通道但连 relaunch 都没有 → 不给死按钮，退回托盘退出指引', async () => {
    renderNotice({ share: {}, system: { autoStart: vi.fn() } })
    await screen.findByText(/后台进程仍是升级前的版本/)
    expect(screen.queryByRole('button', { name: '立即重启' })).not.toBeInTheDocument()
    expect(screen.getByText(/从右下角托盘图标选择「退出」后重新启动/)).toBeInTheDocument()
  })
})
