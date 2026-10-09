import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import type { Share, SharePermission } from '../../types'

// api.ts 在模块加载时读取 window.winshare —— 必须在 import 组件前注入 stub
const stub = vi.hoisted(() => {
  const s = {
    adapter: { permissions: vi.fn(), setPermissions: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import NfsPermPanel from './NfsPermPanel'

// ============================================================================
// t2 · NfsPermPanel（R-5 权限半边 + effect-setState 迁移）
// 断言口径（docs/audit/05-renderer-debt.md §3.3）：
//   ① "只加载一次" = 次数断言 + 静置 200ms 后仍为 1；② 切换目标断次数 + 数据内容，
//   竞态用例让旧响应晚于新响应返回；③ 失败路径同时断言"不该发生的没发生"；
//   ④ 断言可见文本，不断言内部 state。
// ============================================================================

function mkShare(name: string): Share {
  return {
    name,
    path: `C:\\Shares\\${name}`,
    description: '',
    protocol: 'nfs',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: 'Enabled',
    cached: false,
  }
}

function permFor(
  shareName: string,
  account: string,
  access: SharePermission['access'],
): SharePermission {
  return { shareName, account, accountType: 'Group', access, deny: false }
}

const settle = (ms = 200) => new Promise((r) => setTimeout(r, ms))

const bodyText = () => document.body.textContent || ''
async function expectNotice(re: RegExp) {
  await waitFor(() => expect(bodyText()).toMatch(re))
}

// 与 main.tsx 一致：中文 locale（Popconfirm 的默认确认文案是「确定」而非「OK」）
function Panel({ share }: { share: Share }) {
  return (
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <NfsPermPanel share={share} />
      </AntdApp>
    </ConfigProvider>
  )
}

function renderPanel(share: Share) {
  return render(<Panel share={share} />)
}

/** 表格数据行文本（避开表头/测量行） */
function tableRowTexts(): string[] {
  return Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]')).map(
    (r) => r.textContent || '',
  )
}

/** 点触发按钮并确认 Popconfirm（不依赖 antd 内部结构，按去空格文本命中） */
async function confirmPop(triggerName: string | RegExp, okText: string): Promise<void> {
  fireEvent.click(screen.getByRole('button', { name: triggerName }))
  await waitFor(() =>
    expect(document.querySelector('.ant-popover, .ant-popconfirm')).not.toBeNull(),
  )
  const okBtn = Array.from(
    document.querySelectorAll('.ant-popover button, .ant-popconfirm button'),
  ).find((b) => (b.textContent || '').replace(/\s/g, '') === okText)
  expect(okBtn).toBeTruthy()
  fireEvent.click(okBtn!)
}

/** 打开第 index 个下拉并返回其可见选项文本 */
async function openSelectOptions(index: number): Promise<string[]> {
  const combos = screen.getAllByRole('combobox')
  fireEvent.mouseDown(combos[index])
  return waitFor(() => {
    const texts = Array.from(document.querySelectorAll('.ant-select-item-option-content')).map(
      (n) => n.textContent || '',
    )
    if (texts.length === 0) throw new Error('下拉选项未出现')
    return texts
  })
}

// antd 对"两个汉字"的按钮会自动插入空格（可访问名为"保 存"），统一用去空格正则命中
const SAVE_BTN = /^保\s*存$/

beforeEach(() => {
  stub.adapter.permissions.mockReset().mockResolvedValue([])
  stub.adapter.setPermissions.mockReset().mockResolvedValue(undefined)
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
})

describe('NfsPermPanel · 取数节奏（打开即取数、恰好一次）', () => {
  it('挂载→permissions("nfs", share.name) 恰好 1 次；重复渲染不重发；静置 200ms 仍为 1', async () => {
    const share = mkShare('Docs')
    const { rerender } = renderPanel(share)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.permissions).toHaveBeenCalledWith('nfs', 'Docs')

    // 重复渲染（同 share）不得重发
    rerender(<Panel share={share} />)
    await settle()
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)
  })

  it('share.name 变化→再取 1 次，且行内容替换为新目标的数据', async () => {
    stub.adapter.permissions
      .mockResolvedValueOnce([permFor('Docs', '10.0.0.1', 'Read')])
      .mockResolvedValue([permFor('Media', '10.0.0.9', 'Change')])
    const { rerender } = renderPanel(mkShare('Docs'))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('10.0.0.1'))

    rerender(<Panel share={mkShare('Media')} />)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    expect(stub.adapter.permissions).toHaveBeenLastCalledWith('nfs', 'Media')
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('10.0.0.9'))
    expect(tableRowTexts().join('|')).not.toContain('10.0.0.1')
  })

  it('竞态：旧目标响应晚于新目标返回时，新目标数据最终胜出（迟到响应被丢弃）', async () => {
    let resolveFirst: (v: SharePermission[]) => void = () => {}
    stub.adapter.permissions
      .mockImplementationOnce(
        () =>
          new Promise<SharePermission[]>((r) => {
            resolveFirst = r
          }),
      )
      .mockImplementationOnce(async () => [permFor('Media', 'late-loser', 'Read')])

    const { rerender } = renderPanel(mkShare('Docs'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    rerender(<Panel share={mkShare('Media')} />)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('late-loser'))

    // 旧目标的响应此刻才到达：必须被丢弃，不得覆盖新目标数据
    resolveFirst([permFor('Docs', 'stale-old', 'Read')])
    await settle(50)
    expect(tableRowTexts().join('|')).not.toContain('stale-old')
    expect(tableRowTexts().join('|')).toContain('late-loser')
  })
})

describe('NfsPermPanel · 保存（覆盖 + 重读 + 不假成功）', () => {
  it('保存→setPermissions 参数逐项正确（rw→Change、deny、accountType:"Group"）+ 保存后重读 1 次 + 成功文案', async () => {
    stub.adapter.permissions.mockResolvedValue([
      {
        shareName: 'Docs',
        account: '192.168.1.0/24',
        accountType: 'Group',
        access: 'Read',
        deny: true,
      },
    ])
    renderPanel(mkShare('Docs'))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('192.168.1.0/24'))
    expect(tableRowTexts().join('|')).toContain('只读 (ro)')
    expect(tableRowTexts().join('|')).toContain('拒绝')

    await confirmPop(SAVE_BTN, '确定')

    await waitFor(() => expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.setPermissions).toHaveBeenCalledWith('nfs', 'Docs', [
      {
        shareName: 'Docs',
        account: '192.168.1.0/24',
        accountType: 'Group',
        access: 'Read',
        deny: true,
      },
    ])
    // 保存成功 = 保存 1 次 + 重读 1 次（合计 2 次取数）
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    await expectNotice(/权限已保存/)
  })

  it('保存失败：原因可见、不重读、本地编辑不丢（不假成功）', async () => {
    stub.adapter.setPermissions.mockRejectedValue(new Error('NFS 导出写入失败'))
    renderPanel(mkShare('Docs'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    // 本地新增一行（编辑态）
    fireEvent.change(screen.getByPlaceholderText('客户端名称（如 * 或 192.168.1.0/24）'), {
      target: { value: '10.1.2.3' },
    })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('10.1.2.3'))

    await confirmPop(SAVE_BTN, '确定')

    await expectNotice(/NFS 导出写入失败/)
    expect(bodyText()).not.toMatch(/权限已保存/)
    // 不该发生的没发生：没有重读、本地编辑行仍在
    await settle()
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)
    expect(tableRowTexts().join('|')).toContain('10.1.2.3')
  })

  it('加载失败：原因可见，空态仍是「暂无客户端规则」', async () => {
    stub.adapter.permissions.mockRejectedValue(new Error('NFS 权限读取失败'))
    renderPanel(mkShare('Docs'))
    await expectNotice(/NFS 权限读取失败/)
    await waitFor(() => expect(screen.getByText('暂无客户端规则')).toBeInTheDocument())
  })
})

describe('NfsPermPanel · 协议特有部分（列、选项、文案、添加区校验）', () => {
  it('本地新增行：空值/重复给出提示且不入表，合法值入表并参与保存', async () => {
    renderPanel(mkShare('Docs'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))
    const input = screen.getByPlaceholderText('客户端名称（如 * 或 192.168.1.0/24）')

    // 空值 → 提示、不入表
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await expectNotice(/请输入客户端名称/)
    expect(tableRowTexts()).toHaveLength(0)

    fireEvent.change(input, { target: { value: '192.168.1.0/24' } })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('192.168.1.0/24'))

    // 重复 → 提示、不重复入表
    fireEvent.change(input, { target: { value: '192.168.1.0/24' } })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await expectNotice(/该客户端已存在/)
    expect(tableRowTexts()).toHaveLength(1)

    // 新增行参与保存：默认权限 rw + 允许 → Change / deny:false / accountType:'Group'
    await confirmPop(SAVE_BTN, '确定')
    await waitFor(() => expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.setPermissions).toHaveBeenCalledWith('nfs', 'Docs', [
      {
        shareName: 'Docs',
        account: '192.168.1.0/24',
        accountType: 'Group',
        access: 'Change',
        deny: false,
      },
    ])
  })

  it('协议特有列与文案逐字可见：客户端列、权限「只读 (ro)/读写 (rw)」、类型「允许/拒绝」、头部通配符说明、确认标题', async () => {
    renderPanel(mkShare('Docs'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    // 表头
    expect(screen.getByText('客户端')).toBeInTheDocument()
    expect(screen.getByText('权限')).toBeInTheDocument()
    expect(screen.getByText('类型')).toBeInTheDocument()
    // 头部说明（含通配符示例）
    expect(bodyText()).toMatch(/NFS 基于客户端授权/)
    expect(bodyText()).toMatch(/保存时将覆盖现有规则/)
    expect(screen.getByText('192.168.1.0/24')).toBeInTheDocument()
    // 添加区标题 + 提示句
    expect(screen.getByText('添加客户端规则')).toBeInTheDocument()
    expect(bodyText()).toMatch(/拒绝规则优先于允许规则；未匹配的客户端遵循共享默认权限。/)
    // 权限/类型选项文案（打开添加区下拉）
    expect(await openSelectOptions(0)).toEqual(expect.arrayContaining(['只读 (ro)', '读写 (rw)']))
    expect(await openSelectOptions(1)).toEqual(expect.arrayContaining(['允许', '拒绝']))
    // 保存确认标题
    fireEvent.click(screen.getByRole('button', { name: SAVE_BTN }))
    await expectNotice(/确认覆盖当前 NFS 客户端权限？/)
  })
})
