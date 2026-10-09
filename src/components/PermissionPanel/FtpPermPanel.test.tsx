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

import FtpPermPanel from './FtpPermPanel'

// ============================================================================
// t2 · FtpPermPanel（R-5 权限半边 + effect-setState 迁移）
// 断言口径同 NfsPermPanel.test.tsx（docs/audit/05-renderer-debt.md §3.3）
// ============================================================================

function mkShare(name: string): Share {
  return {
    name,
    path: `C:\\ftp\\${name}`,
    description: '',
    protocol: 'ftp',
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
  accountType: 'User' | 'Group' = 'User',
): SharePermission {
  return { shareName, account, accountType, access, deny: false }
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
        <FtpPermPanel share={share} />
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

const SAVE_BTN = /^保\s*存$/

beforeEach(() => {
  stub.adapter.permissions.mockReset().mockResolvedValue([])
  stub.adapter.setPermissions.mockReset().mockResolvedValue(undefined)
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
})

describe('FtpPermPanel · 取数节奏（打开即取数、恰好一次）', () => {
  it('挂载→permissions("ftp", share.name) 恰好 1 次；重复渲染不重发；静置 200ms 仍为 1', async () => {
    const share = mkShare('site')
    const { rerender } = renderPanel(share)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.permissions).toHaveBeenCalledWith('ftp', 'site')

    rerender(<Panel share={share} />)
    await settle()
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)
  })

  it('share.name 变化→再取 1 次，且行内容替换为新目标的数据', async () => {
    stub.adapter.permissions
      .mockResolvedValueOnce([permFor('site', 'alice', 'Read')])
      .mockResolvedValue([permFor('backup', 'bob', 'Change')])
    const { rerender } = renderPanel(mkShare('site'))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('alice'))

    rerender(<Panel share={mkShare('backup')} />)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    expect(stub.adapter.permissions).toHaveBeenLastCalledWith('ftp', 'backup')
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('bob'))
    expect(tableRowTexts().join('|')).not.toContain('alice')
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
      .mockImplementationOnce(async () => [permFor('backup', 'late-loser', 'Read')])

    const { rerender } = renderPanel(mkShare('site'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    rerender(<Panel share={mkShare('backup')} />)
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('late-loser'))

    resolveFirst([permFor('site', 'stale-old', 'Read')])
    await settle(50)
    expect(tableRowTexts().join('|')).not.toContain('stale-old')
    expect(tableRowTexts().join('|')).toContain('late-loser')
  })
})

describe('FtpPermPanel · 保存（覆盖 + 重读 + 不假成功）', () => {
  it('保存→setPermissions 参数逐项正确（rw→Change、deny、accountType 保留）+ 保存后重读 1 次 + 成功文案', async () => {
    stub.adapter.permissions.mockResolvedValue([
      {
        shareName: 'site',
        account: 'Administrators',
        accountType: 'Group',
        access: 'Full',
        deny: true,
      },
    ])
    renderPanel(mkShare('site'))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('Administrators'))
    // 行内 Tag 与选项文案：Full→rw、deny→拒绝
    expect(tableRowTexts().join('|')).toContain('组')
    expect(tableRowTexts().join('|')).toContain('读写 (Read, Write)')
    expect(tableRowTexts().join('|')).toContain('拒绝')

    await confirmPop(SAVE_BTN, '确定')

    await waitFor(() => expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.setPermissions).toHaveBeenCalledWith('ftp', 'site', [
      {
        shareName: 'site',
        account: 'Administrators',
        accountType: 'Group',
        access: 'Change',
        deny: true,
      },
    ])
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    await expectNotice(/授权规则已保存/)
  })

  it('保存失败：原因可见、不重读、本地编辑不丢（不假成功）', async () => {
    stub.adapter.setPermissions.mockRejectedValue(new Error('IIS 配置节被锁定'))
    renderPanel(mkShare('site'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    fireEvent.change(screen.getByPlaceholderText('账号名（如 * 或 Administrators）'), {
      target: { value: 'alice' },
    })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('alice'))

    await confirmPop(SAVE_BTN, '确定')

    await expectNotice(/IIS 配置节被锁定/)
    expect(bodyText()).not.toMatch(/授权规则已保存/)
    await settle()
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)
    expect(tableRowTexts().join('|')).toContain('alice')
  })

  it('加载失败：原因可见，空态仍是「暂无授权规则」', async () => {
    stub.adapter.permissions.mockRejectedValue(new Error('FTP 授权读取失败'))
    renderPanel(mkShare('site'))
    await expectNotice(/FTP 授权读取失败/)
    await waitFor(() => expect(screen.getByText('暂无授权规则')).toBeInTheDocument())
  })
})

describe('FtpPermPanel · 协议特有部分（列、选项、文案、添加区校验）', () => {
  it('本地新增行：空值/重复给出提示且不入表，合法值入表并参与保存（默认只读 + 允许 + 用户）', async () => {
    renderPanel(mkShare('site'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))
    const input = screen.getByPlaceholderText('账号名（如 * 或 Administrators）')

    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await expectNotice(/请输入账号名/)
    expect(tableRowTexts()).toHaveLength(0)

    fireEvent.change(input, { target: { value: 'alice' } })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await waitFor(() => expect(tableRowTexts().join('|')).toContain('alice'))

    fireEvent.change(input, { target: { value: 'alice' } })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))
    await expectNotice(/该账号已存在/)
    expect(tableRowTexts()).toHaveLength(1)

    await confirmPop(SAVE_BTN, '确定')
    await waitFor(() => expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1))
    expect(stub.adapter.setPermissions).toHaveBeenCalledWith('ftp', 'site', [
      {
        shareName: 'site',
        account: 'alice',
        accountType: 'User',
        access: 'Read',
        deny: false,
      },
    ])
  })

  it('协议特有列与文案逐字可见：账号/类型/权限/授权列、用户组 Tag、选项文案、头部与确认标题', async () => {
    renderPanel(mkShare('site'))
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(1))

    for (const title of ['账号', '类型', '权限', '授权']) {
      expect(screen.getByText(title)).toBeInTheDocument()
    }
    expect(bodyText()).toMatch(/FTP 授权规则基于用户\/组授予 Read 或 Read\+Write，可设允许\/拒绝。/)
    expect(screen.getByText('添加授权规则')).toBeInTheDocument()
    expect(bodyText()).toMatch(/组授权使用 roles，用户授权使用 users；拒绝规则优先于允许规则。/)

    // 添加区三个下拉：类型（用户/组）、权限、授权
    expect(await openSelectOptions(0)).toEqual(expect.arrayContaining(['用户', '组']))
    expect(await openSelectOptions(1)).toEqual(
      expect.arrayContaining(['只读 (Read)', '读写 (Read, Write)']),
    )
    expect(await openSelectOptions(2)).toEqual(expect.arrayContaining(['允许', '拒绝']))

    fireEvent.click(screen.getByRole('button', { name: SAVE_BTN }))
    await expectNotice(/确认覆盖当前 FTP 授权规则？/)
  })
})
