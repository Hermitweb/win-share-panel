import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'

// api.ts 在模块加载时读取 window.winshare —— 必须先注入 stub 再 import 组件
const stub = vi.hoisted(() => {
  const s = {
    adapter: { list: vi.fn() },
    diagnose: { run: vi.fn(), applyFix: vi.fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import DiagnoseModal from './DiagnoseModal'
import type { DiagnoseItem } from '../types'

// 本机并发跑多套组件测试时 jsdom 会明显变慢：放宽异步等待预算
const OPT = { timeout: 30_000 }
const until = (fn: () => void | Promise<void>) => vi.waitFor(fn, { timeout: 5_000 })

// globals:false 下 RTL auto-cleanup 不注册，手动逐用例卸载，避免 Modal/浮层跨用例累积
afterEach(async () => {
  cleanup()
  // 冲刷卸载后仍在飞行的 promise，避免污染下一个用例
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
  delete (window.navigator as unknown as { clipboard?: unknown }).clipboard
})

const SHARES = [
  { name: 'Docs', path: 'D:\\Docs', description: '部门文档', protocol: 'smb' },
  { name: 'Photos', path: 'E:\\Photos', description: '', protocol: 'smb' },
]

/** 三类状态 + 一条带 fix 的异常 + 一条无 fix 的异常 + 一条全局体检的"跳过"项 */
const ITEMS: DiagnoseItem[] = [
  {
    key: 'smb-service',
    label: 'SMB 服务端（LanmanServer）运行',
    status: 'pass',
    detail: '状态：Running（Automatic）',
  },
  { key: 'port-445', label: 'SMB 端口 445 已监听', status: 'warn', detail: '探测失败：命令超时' },
  {
    key: 'firewall-smb',
    label: '防火墙放行 SMB 入站',
    status: 'fail',
    detail: '未见启用的共享放行规则',
    fix: 'firewall:smb',
  },
  {
    key: 'share-perms',
    label: '共享权限已授予访问者',
    status: 'fail',
    detail: '共享权限为空——任何人都连不上',
  },
  { key: 'share-exists', label: '目标共享存在', status: 'pass', detail: '未指定共享，跳过' },
]
// 2 pass / 1 warn / 2 fail

const STATUS_WORD: Record<DiagnoseItem['status'], string> = {
  pass: '正常',
  warn: '需关注',
  fail: '异常',
}

beforeEach(() => {
  stub.adapter.list.mockResolvedValue(SHARES)
  stub.diagnose.run.mockResolvedValue(ITEMS)
  stub.diagnose.applyFix.mockResolvedValue('已添加防火墙规则：SMB 445/TCP 入站放行')
})

function renderModal(props?: { open?: boolean; initialShareName?: string }) {
  render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <DiagnoseModal
          open={props?.open ?? true}
          onClose={vi.fn()}
          initialShareName={props?.initialShareName}
        />
      </AntdApp>
    </ConfigProvider>,
  )
}

/** 点「开始诊断」并等指定项落地（默认等首项） */
async function startDiagnose(waitForLabel = 'SMB 服务端（LanmanServer）运行') {
  fireEvent.click(await screen.findByRole('button', { name: /开始诊断/ }))
  await screen.findByText(waitForLabel)
}

/** 取某一项的行容器（组件写入 data-key/data-status/data-color，避免依赖 antd 内部类名） */
function rowOf(item: Pick<DiagnoseItem, 'key' | 'status'>): HTMLElement {
  const row = document.querySelector(`li[data-key="${item.key}"][data-status="${item.status}"]`)
  if (!row) throw new Error(`未找到 ${item.key}（status=${item.status}）的行`)
  return row as HTMLElement
}

/** 点触发按钮并取当前最新的 Popconfirm 浮层 */
async function openConfirm(triggerName: RegExp): Promise<HTMLElement> {
  fireEvent.click(screen.getByRole('button', { name: triggerName }))
  await until(() =>
    expect(document.querySelectorAll('.ant-popover, .ant-popconfirm').length).toBeGreaterThan(0),
  )
  const all = document.querySelectorAll('.ant-popover, .ant-popconfirm')
  return all[all.length - 1] as HTMLElement
}

/** 从下拉里点选第一个共享（选项文本在 .ant-select-item-option-content，冒泡到可点的 option） */
async function pickFirstShare(expectLabel: string) {
  fireEvent.mouseDown(screen.getByRole('combobox'))
  await until(() =>
    expect(document.querySelector('.ant-select-item-option-content')).not.toBeNull(),
  )
  fireEvent.click(document.querySelector('.ant-select-item-option-content')!)
  await until(() =>
    expect((document.querySelector('.ant-select') as HTMLElement).textContent).toContain(
      expectLabel,
    ),
  )
}

describe('DiagnoseModal（一键诊断向导）', () => {
  it(
    'props 只需 { open, onClose, initialShareName? }：open=false 时不出面板、不预拉数据',
    OPT,
    () => {
      renderModal({ open: false })
      expect(screen.queryByRole('button', { name: /开始诊断/ })).not.toBeInTheDocument()
      expect(stub.adapter.list).not.toHaveBeenCalled()
    },
  )

  it('共享名下拉来自真实共享列表（api.adapter.list("smb")），且允许留空', OPT, async () => {
    renderModal()
    await until(() => expect(stub.adapter.list).toHaveBeenCalledWith('smb'))

    // 未选时有"留空＝全局体检"占位，即留空可达
    expect(document.querySelector('.ant-select-placeholder')?.textContent).toContain('全局体检')

    fireEvent.mouseDown(screen.getByRole('combobox'))
    await until(() => {
      const labels = Array.from(document.querySelectorAll('.ant-select-item-option-content')).map(
        (el) => el.textContent,
      )
      expect(labels).toEqual(expect.arrayContaining(['Docs — 部门文档', 'Photos']))
    })
  })

  it('选定共享后诊断：run 收到该共享名，且换目标会清空旧结论', OPT, async () => {
    renderModal()
    await until(() => expect(stub.adapter.list).toHaveBeenCalledWith('smb'))

    // 先做一次全局体检拿到结果
    await startDiagnose()
    expect(screen.getByRole('button', { name: /重新诊断/ })).toBeInTheDocument()

    // 换目标共享：上一个范围的逐项结论不得留下来套在新目标上
    await pickFirstShare('Docs')
    expect(document.querySelector('li[data-key="smb-service"]')).toBeNull()
    expect(screen.getByRole('button', { name: /开始诊断/ })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /开始诊断/ }))
    await until(() => expect(stub.diagnose.run).toHaveBeenCalledWith('Docs'))
  })

  it('留空＝全局体检：run 收到 undefined，主进程的"跳过"项原样可见', OPT, async () => {
    renderModal()
    await startDiagnose()
    expect(stub.diagnose.run).toHaveBeenCalledWith(undefined)
    const skipRow = rowOf({ key: 'share-exists', status: 'pass' })
    expect(within(skipRow).getByText('未指定共享，跳过')).toBeInTheDocument()
  })

  it('initialShareName 带入目标共享（从共享行点进来的路径）', OPT, async () => {
    renderModal({ initialShareName: 'Photos' })
    await startDiagnose()
    expect(stub.diagnose.run).toHaveBeenCalledWith('Photos')
  })

  it('逐项红绿灯：pass 绿 / warn 黄 / fail 红，label 与 detail 分显', OPT, async () => {
    renderModal()
    await startDiagnose()

    expect(rowOf(ITEMS[0]).getAttribute('data-color')).toBe('green')
    expect(rowOf(ITEMS[1]).getAttribute('data-color')).toBe('gold')
    expect(rowOf(ITEMS[2]).getAttribute('data-color')).toBe('red')

    ITEMS.forEach((i) => {
      const row = rowOf(i)
      // label 与 detail 分属两个元素：详情（路径/状态）可能很长，不挤压标题
      expect(within(row).getByText(i.label)).toBeInTheDocument()
      expect(within(row).getByText(i.detail)).toBeInTheDocument()
      expect(within(row).getByText(STATUS_WORD[i.status])).toBeInTheDocument()
      // 红绿灯色名同时落到 antd Tag 的预设色类上
      expect(row.querySelector(`.ant-tag-${row.getAttribute('data-color')}`)).not.toBeNull()
    })
  })

  it('仅带 fix 的异常行出现「一键修复」；无 fix 的 fail 给人话下一步建议', OPT, async () => {
    renderModal()
    await startDiagnose()

    expect(screen.getAllByRole('button', { name: /一键修复/ })).toHaveLength(1)
    expect(within(rowOf(ITEMS[2])).getByRole('button', { name: /一键修复/ })).toBeInTheDocument()
    // 同为异常的 share-perms 没有 fix → 只给建议，不给按钮
    expect(
      within(rowOf(ITEMS[3])).queryByRole('button', { name: /一键修复/ }),
    ).not.toBeInTheDocument()

    const advice = within(rowOf(ITEMS[3])).getByText(/下一步：/)
    expect(advice.textContent).toContain('权限')
    expect(advice.textContent).toContain('读取')
    // pass / warn 行不出建议块
    expect(within(rowOf(ITEMS[1])).queryByText(/下一步：/)).not.toBeInTheDocument()
    expect(within(rowOf(ITEMS[0])).queryByText(/下一步：/)).not.toBeInTheDocument()
  })

  it('修复必须二次确认：取消不动手，确认后 applyFix(fix,{shareName}) 并自动复检', OPT, async () => {
    renderModal()
    await startDiagnose()

    // 确认框写清要动什么，点下去前用户就知道改的是哪一处
    const pop1 = await openConfirm(/一键修复/)
    expect(pop1.textContent).toContain('添加防火墙规则')
    fireEvent.click(within(pop1).getByRole('button', { name: /取\s*消/ }))
    await new Promise((r) => setTimeout(r, 0))
    expect(stub.diagnose.applyFix).not.toHaveBeenCalled()
    expect(stub.diagnose.run).toHaveBeenCalledTimes(1)

    const pop2 = await openConfirm(/一键修复/)
    fireEvent.click(within(pop2).getByRole('button', { name: /确认修复/ }))

    await until(() =>
      expect(stub.diagnose.applyFix).toHaveBeenCalledWith('firewall:smb', { shareName: undefined }),
    )
    // 复检：诊断再跑一次，红绿灯由主进程重新判定
    await until(() => expect(stub.diagnose.run).toHaveBeenCalledTimes(2))
    expect(screen.getByRole('button', { name: /重新诊断/ })).toBeInTheDocument()
    // 提示的是主进程 applyFix 的返回文案
    expect(await screen.findByText('已添加防火墙规则：SMB 445/TCP 入站放行')).toBeInTheDocument()
  })

  it('修复失败不吞错：显示主进程错误文案，不假装复检通过', OPT, async () => {
    stub.diagnose.applyFix.mockRejectedValue(new Error('需要管理员权限'))
    renderModal()
    await startDiagnose()

    const pop = await openConfirm(/一键修复/)
    fireEvent.click(within(pop).getByRole('button', { name: /确认修复/ }))

    expect(await screen.findByText('需要管理员权限')).toBeInTheDocument()
    expect(stub.diagnose.run).toHaveBeenCalledTimes(1)
  })

  it('run 抛错时整体错误可见（不白屏）并可重试；恢复后错误条消失', OPT, async () => {
    stub.diagnose.run.mockRejectedValue(new Error('PowerShell 通道不可用'))
    renderModal()
    fireEvent.click(await screen.findByRole('button', { name: /开始诊断/ }))

    expect(await screen.findByText('诊断没能完成')).toBeInTheDocument()
    expect(screen.getByText('PowerShell 通道不可用')).toBeInTheDocument()
    // 面板骨架仍在：下拉、重试入口都还在（不是白屏）
    expect(screen.getByRole('combobox')).toBeInTheDocument()
    const retry = screen.getByRole('button', { name: /重\s*试/ })

    stub.diagnose.run.mockResolvedValue(ITEMS)
    fireEvent.click(retry)
    await until(() => expect(stub.diagnose.run).toHaveBeenCalledTimes(2))
    expect(await screen.findByText('SMB 服务端（LanmanServer）运行')).toBeInTheDocument()
    expect(screen.queryByText('诊断没能完成')).not.toBeInTheDocument()
  })

  it('底部汇总「N 项通过 / M 项异常」', OPT, async () => {
    renderModal()
    await startDiagnose()
    expect(screen.getByText(/2 项通过 \/ 2 项异常/)).toBeInTheDocument()
    expect(screen.getByText(/1 项需关注/)).toBeInTheDocument()
  })

  it('复制诊断结果：剪贴板内容含每项 label+status+detail、汇总与建议', OPT, async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(window.navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    })
    renderModal()
    await startDiagnose()

    fireEvent.click(screen.getByRole('button', { name: /复制诊断结果/ }))
    await until(() => expect(writeText).toHaveBeenCalledTimes(1))

    const text: string = writeText.mock.calls[0][0]
    ITEMS.forEach((i) => {
      expect(text).toContain(`[${STATUS_WORD[i.status]}] ${i.label} —— ${i.detail}`)
    })
    expect(text).toContain('2 项通过 / 2 项异常')
    expect(text).toContain('范围：全局体检（未指定共享）')
    expect(text).toContain('下一步怎么办')
  })

  it('留空全局体检时 acl 类修复不可用（确认按钮禁用，避免不知改哪个文件夹）', OPT, async () => {
    stub.diagnose.run.mockResolvedValue([
      {
        key: 'ntfs-read',
        label: '共享路径 NTFS 已授予读取权限',
        status: 'fail',
        detail: '该路径 NTFS 未授予任何可读权限',
        fix: 'acl:read-everyone',
      },
    ])
    renderModal()
    await startDiagnose('共享路径 NTFS 已授予读取权限')

    const pop = await openConfirm(/一键修复/)
    expect(pop.textContent).toContain('给共享文件夹的 NTFS 权限追加 Everyone 读取')
    const okBtn = within(pop).getByRole('button', { name: /确认修复/ }) as HTMLButtonElement
    expect(okBtn.disabled).toBe(true)
    expect(stub.diagnose.applyFix).not.toHaveBeenCalled()
  })
})
