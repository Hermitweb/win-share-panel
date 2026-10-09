import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor, fireEvent, cleanup, within, act } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { AppState, AppStatePatch, DesiredRule, SecurityReport } from '../types'

// ============================================================================
// t6 设置页「偏好与安全」：使用模式 / 开机自启 / 告警规则 / 防火墙组内规则 /
// 账号安全体检 + 「一键诊断」入口。
// appState 用模块内 persisted 变量模拟主进程落盘，所以"重开仍在"是真的卸载重挂验证，
// 而不是断言 mock 被调过；写失败用例断言"原因可见 + 值回滚"，不吞错。
// ============================================================================

// 本文件都是 antd + jsdom 的重渲染用例，跑全量时 node/dom 两个 project 并行，
// 个别用例（两次挂载 + 切 Tab）会超过默认 5s 上限——那是负载不是功能失败，统一放宽。
vi.setConfig({ testTimeout: 30_000 })

const OS_INFO = {
  caption: 'Microsoft Windows Server 2022 Standard',
  buildNumber: 20348,
  skuId: 143,
  skuName: 'Server',
  isServer: true,
  isHomeEdition: false,
  hostname: 'SRV01',
  features: {
    smbShareModule: true,
    nfsServerCmdlets: true,
    iisAvailable: true,
    smbQuicConfig: true,
  },
}

/** 默认 SMB 配置（getConfig 返回值，够表单 setFieldsValue 用） */
const SMB_CONFIG = {
  enableSMB1Protocol: false,
  enableSMB2Protocol: true,
  enableSMB3Protocol: true,
  enableGuestUserAccess: false,
  enableInsecureGuestLogons: false,
  auditSmb1Access: false,
  requireSecuritySignature: true,
  enableMultiChannel: true,
  announceServer: true,
  unauthenticatedUsersTimeLimit: 300,
}

const apiStub = vi.hoisted(() => {
  // 参数放成 any[]（而非 unknown[]）：用例里要按真实签名写 mockImplementation，
  // unknown[] 会因函数参数逆变拒绝更窄的参数类型（strictFunctionTypes）。
  const r = (v: unknown) => vi.fn(async (..._args: any[]): Promise<any> => v)
  const s = {
    share: {
      list: r([]),
      get: r(null),
      create: r(null),
      update: r(null),
      delete: r(null),
      toggle: r(null),
      permissions: r([]),
      exportConfig: r(''),
      importConfig: r({ imported: 0, skipped: 0, errors: [] }),
      connections: r({ concurrentUsers: 0, clientConnections: [] }),
      openFiles: r([]),
      closeOpenFiles: r({ closed: 0, failed: 0 }),
    },
    user: {
      list: r([]),
      get: r(null),
      groups: r([]),
      sharePermissions: r([]),
      sharePermissionsForUser: r([]),
      setSharePermissions: r(null),
      ntfsPermissions: r({ path: '', entries: [] }),
      create: r(null),
      update: r(null),
      delete: r(null),
      setPassword: r(null),
      enable: r(null),
      disable: r(null),
      rename: r(null),
    },
    group: {
      create: r(null),
      delete: r(null),
      update: r(null),
      rename: r(null),
      addMember: r(null),
      removeMember: r(null),
    },
    session: { list: r([]), files: r([]), close: r(null), closeFile: r(null) },
    smb: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'LanmanServer', status: 'Running', startType: 'Automatic' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
      listSnapshots: r([]),
      rollback: r(null),
    },
    preset: {
      list: r([]),
      get: r(null),
      save: r(null),
      update: r(null),
      delete: r(null),
      duplicate: r(null),
      apply: r(null),
      export: r('{}'),
      import: r({ imported: 0, skipped: 0, errors: [] }),
    },
    system: {
      currentUser: r({ username: 'admin', isAdmin: true, computerName: 'SRV01' }),
      isAdmin: r(true),
      dashboard: r(null),
      auditLog: r(''),
      health: r({ ok: true, detail: 'ok' }),
      osInfo: r(null),
      selectFolder: r(null),
      openLogFolder: r(''),
      autoStart: r(null),
      setAutoStart: r(false),
      pathForFile: vi.fn(() => ''),
    },
    log: { write: r(null), tail: r('') },
    window: {
      minimize: r(null),
      toggleMaximize: r(false),
      close: r(null),
      isMaximized: r(false),
      onMaximizeChange: vi.fn(),
      showBalloon: r(null),
    },
    adapter: {
      list: r([]),
      create: r(null),
      update: r(null),
      delete: r(null),
      toggle: r(null),
      permissions: r([]),
      setPermissions: r(null),
      sessions: r([]),
      closeSession: r(null),
      capabilities: r({}),
    },
    nfs: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'NfsSvc', status: 'Stopped', startType: 'Manual' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
    },
    ftp: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'W3SVC', status: 'Stopped', startType: 'Manual' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
    },
    webdav: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'W3SVC', status: 'Stopped', startType: 'Manual' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
    },
    protocol: { detect: r({}), install: r(null) },
    state: {
      get: r(null),
      patch: r(null),
      journalList: r([]),
      journalUndo: r(''),
      journalClear: r(0),
    },
    disk: { usages: r([]), suggestRoot: r('D:') },
    security: { report: r(null) },
    firewall: { list: r([]), ensure: r([]), remove: r(null), preset: r([]) },
    diagnose: { run: r([]), applyFix: r('') },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import Settings, { type DiagnoseLoader, type SettingsProps } from './Settings'
import { useAppStore } from '../stores/appStore'
import { useUiStore } from '../stores/uiStore'

function baseState(): AppState {
  return {
    theme: 'light',
    advancedMode: true,
    pinned: [],
    alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
    autoStart: false,
    dashboardHistory: [],
    journal: [],
  }
}

/** 模拟主进程 appstate.json：补丁逐键带上，alertRules 浅合并保留其余项 */
function mergeState(prev: AppState, p: AppStatePatch): AppState {
  return { ...prev, ...p, alertRules: { ...prev.alertRules, ...(p.alertRules ?? {}) } }
}

const clone = (s: AppState): AppState => JSON.parse(JSON.stringify(s)) as AppState

let persisted: AppState

/** api.firewall.list() 的返回：主进程只查 DisplayGroup=WinShare Panel，故这里全是组内规则 */
const GROUP_RULES = [
  { name: 'WinShare SMB (445/TCP)', enabled: true, ports: '445' },
  { name: 'WinShare FTP Passive (50000-51000/TCP)', enabled: false, ports: '50000-51000' },
]

const reportWith = (issues: SecurityReport['issues'], checked = 3): SecurityReport => ({
  checked,
  issues,
  at: Date.parse('2026-10-09T10:00:00Z'),
})

beforeEach(() => {
  persisted = baseState()
  useAppStore.setState({ state: baseState(), hydrated: false, journal: [] })
  useUiStore.setState({ refreshTick: 0 })

  apiStub.system.osInfo.mockImplementation(async () => OS_INFO)
  apiStub.smb.getConfig.mockImplementation(async () => ({ ...SMB_CONFIG }))
  apiStub.state.get.mockImplementation(async () => clone(persisted))
  apiStub.state.patch.mockImplementation(async (p: AppStatePatch) => {
    persisted = mergeState(persisted, p)
    return clone(persisted)
  })
  apiStub.system.autoStart.mockImplementation(async () => true)
  apiStub.system.setAutoStart.mockImplementation(async (v: boolean) => v)
  apiStub.firewall.list.mockImplementation(async () => GROUP_RULES)
  apiStub.firewall.preset.mockImplementation(
    async (kind: string, opts?: { passiveFrom?: number; passiveTo?: number }) => {
      if (kind === 'ftpPassive') {
        const from = opts?.passiveFrom
        const to = opts?.passiveTo
        if (typeof from !== 'number' || typeof to !== 'number' || from > to) {
          throw new Error('被动端口范围非法')
        }
        return [{ name: `WinShare FTP Passive (${from}-${to}/TCP)`, ports: `${from}-${to}` }]
      }
      if (kind === 'smb') return [{ name: 'WinShare SMB (445/TCP)', ports: '445' }]
      return [{ name: `WinShare ${kind} (445/TCP)`, ports: '445' }]
    },
  )
  apiStub.firewall.ensure.mockImplementation(async (rules: DesiredRule[]) =>
    rules.filter((x) => !GROUP_RULES.some((g) => g.name === x.name)).map((x) => x.name),
  )
  apiStub.firewall.remove.mockImplementation(async () => undefined)
  apiStub.security.report.mockImplementation(async () =>
    reportWith([
      { user: 'guest', level: 'fail', issue: '账号设置了"密码永不需要"，可空口令登录' },
      { user: 'backup', level: 'warn', issue: '密码设置为永不过期（共享账号建议定期轮换）' },
    ]),
  )
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0)) // 冲刷卸载竞态（react-query 取消），避免假阳性
  vi.clearAllMocks()
})

function renderSettings(props: SettingsProps = {}) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: 0, refetchOnWindowFocus: false } },
  })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>
          <Settings {...props} />
        </QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

/** 打开「偏好与安全」Tab（antd Tabs 懒挂载：没点开就不会发防火墙/体检请求） */
async function openOpsTab() {
  fireEvent.click(await screen.findByRole('tab', { name: /偏好与安全/ }))
  await screen.findByText('使用模式与外观')
}

/** 按卡片标题定位容器（标题文案唯一，避免同页多个开关互相误命中） */
function cardByTitle(title: string): HTMLElement {
  const head = Array.from(document.querySelectorAll('.ant-card-head-title')).find((h) =>
    (h.textContent || '').includes(title),
  )
  const card = head?.closest('.ant-card')
  if (!card) throw new Error(`未找到卡片：${title}`)
  return card as HTMLElement
}

const bodyText = () => document.body.textContent || ''

/** antd message 是异步浮层：按正则等它出现（用于断言"已保存/成功/失败原因"可见） */
async function expectNotice(re: RegExp) {
  await waitFor(() => expect(bodyText()).toMatch(re))
}

/**
 * antd Segmented 在 jsdom 下把选中态渲染成 <input checked> 属性，但 input.checked
 * 这个 DOM property 不会同步（jest-dom 的 toBeChecked 看的是 property），
 * 所以按 antd 自己的选中态类断言"当前选中哪一项"，这才是用户实际看到的态。
 */
function segmentSelected(name: string): boolean {
  const input = screen.getByRole('radio', { name }) as HTMLElement
  return Boolean(
    input.closest('.ant-segmented-item')?.classList.contains('ant-segmented-item-selected'),
  )
}

/**
 * antd Select 的选项渲染在 portal 里，jsdom 下不暴露稳定的 role="option"，
 * 统一按 .ant-select-item-option-content 文本点选（与 DiagnoseModal.test.tsx 同一手法）。
 */
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

describe('设置页 · 使用模式与外观', () => {
  it('切到新手模式写入 advancedMode 并持久化；卸载重挂后仍是新手（重开仍在）', async () => {
    renderSettings()
    await openOpsTab()
    expect(
      screen.getByText(/专家模式会展开高级参数（未认证超时 \/ 会话超时 \/ 每连接最大会话/),
    ).toBeInTheDocument()

    fireEvent.click(screen.getByRole('radio', { name: '新手模式' }))
    await waitFor(() => expect(apiStub.state.patch).toHaveBeenCalledWith({ advancedMode: false }))
    expect(persisted.advancedMode).toBe(false)
    await expectNotice(/已切换到新手模式/)

    cleanup()
    apiStub.state.get.mockClear()
    renderSettings()
    await openOpsTab()
    // 重挂后要等 hydrate 真正读完主进程偏好；跑全量时并行负载会让首帧慢于默认 1s 轮询窗口
    await waitFor(() => expect(apiStub.state.get).toHaveBeenCalled(), { timeout: 5000 })
    await waitFor(() => expect(segmentSelected('新手模式')).toBe(true), { timeout: 5000 })
    expect(segmentSelected('专家模式')).toBe(false)
  })

  it('新手模式真的收起 SMB 高级参数（文案不是空话）；专家模式保持展开', async () => {
    persisted.advancedMode = false
    const view = renderSettings()
    // 两处同名文本（h1 与 Tab 标题）→ 用标题级查询，避免 findByText 多命中报错
    await screen.findByRole('heading', { name: '服务器配置' })
    // hydrate 是异步的（读主进程 appState）：等新手态真正生效再断言收起
    await waitFor(() => expect(screen.getByText(/当前为新手模式：高级参数/)).toBeInTheDocument())
    expect(screen.queryByText('高级参数（吞吐/超时/压缩）')).not.toBeInTheDocument()
    view.unmount()

    persisted.advancedMode = true
    renderSettings()
    expect(await screen.findByText('高级参数（吞吐/超时/压缩）')).toBeInTheDocument()
  })

  it('模式写入被主进程拒绝：原因可见，开关回滚到原值', async () => {
    apiStub.state.patch.mockImplementation(async () => {
      throw new Error('advancedMode 必须为布尔值')
    })
    renderSettings()
    await openOpsTab()
    await waitFor(() => expect(segmentSelected('专家模式')).toBe(true))
    fireEvent.click(screen.getByRole('radio', { name: '新手模式' }))
    await expectNotice(/advancedMode 必须为布尔值/)
    await waitFor(() => expect(segmentSelected('专家模式')).toBe(true))
    expect(segmentSelected('新手模式')).toBe(false)
  })

  it('主题切换写入 appState.theme=dark', async () => {
    renderSettings()
    await openOpsTab()
    fireEvent.click(screen.getByRole('radio', { name: '深色' }))
    await waitFor(() => expect(apiStub.state.patch).toHaveBeenCalledWith({ theme: 'dark' }))
    expect(persisted.theme).toBe('dark')
  })
})

describe('设置页 · 开机自启', () => {
  it('进入页面读取真实状态：系统已开启则开关显示已开启', async () => {
    renderSettings()
    await openOpsTab()
    await waitFor(() => expect(apiStub.system.autoStart).toHaveBeenCalledTimes(1))
    const card = cardByTitle('开机自启')
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
    expect(within(card).getByText('已开启')).toBeInTheDocument()
  })

  it('关闭写入成功：以主进程读回的真实值显示，并把意愿持久化', async () => {
    apiStub.system.setAutoStart.mockImplementation(async () => false)
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('开机自启')
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
    fireEvent.click(within(card).getByRole('switch', { name: '开机自启开关' }))
    await waitFor(() => expect(apiStub.system.setAutoStart).toHaveBeenCalledWith(false))
    await waitFor(() => expect(apiStub.state.patch).toHaveBeenCalledWith({ autoStart: false }))
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).not.toBeChecked(),
    )
    await expectNotice(/已关闭开机自启/)
  })

  it('写入失败：显示原因并回滚到读取到的真实状态，不谎称成功', async () => {
    apiStub.system.setAutoStart.mockImplementation(async () => {
      throw new Error('设置开机自启失败：拒绝访问')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('开机自启')
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
    fireEvent.click(within(card).getByRole('switch', { name: '开机自启开关' }))
    await expectNotice(/开机自启设置失败：设置开机自启失败：拒绝访问/)
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
    expect(bodyText()).not.toMatch(/已开启开机自启|已关闭开机自启/)
    expect(apiStub.state.patch).not.toHaveBeenCalledWith(
      expect.objectContaining({ autoStart: false }),
    )
  })

  it('系统读回值与请求不一致（被策略驳回）：如实提示当前状态', async () => {
    // 当前真实值=开启；请求关闭但主进程读回仍是开启 → 不能报"已关闭"
    apiStub.system.setAutoStart.mockImplementation(async () => true)
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('开机自启')
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
    fireEvent.click(within(card).getByRole('switch', { name: '开机自启开关' }))
    await expectNotice(/系统未接受该设置，当前为已开启/)
    expect(bodyText()).not.toMatch(/已关闭开机自启/)
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: '开机自启开关' })).toBeChecked(),
    )
  })

  it('平台不支持（返回 null）：整卡隐藏，不给改不动的开关', async () => {
    apiStub.system.autoStart.mockImplementation(async () => null)
    renderSettings()
    await openOpsTab()
    await waitFor(() => expect(apiStub.system.autoStart).toHaveBeenCalled())
    await screen.findByText('告警规则') // 其余卡片正常渲染
    expect(screen.queryByText('开机自启')).not.toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: '开机自启开关' })).not.toBeInTheDocument()
  })

  it('读取失败：卡片内可见原因，不静默显示成"已关闭"', async () => {
    apiStub.system.autoStart.mockImplementation(async () => {
      throw new Error('IPC 通道未注册')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('开机自启')
    await waitFor(() =>
      expect(within(card).getByText(/读取开机自启状态失败：IPC 通道未注册/)).toBeInTheDocument(),
    )
    expect(within(card).queryByRole('switch', { name: '开机自启开关' })).not.toBeInTheDocument()
  })
})

describe('设置页 · 告警规则', () => {
  it('四项都可编辑：每次只提交被改的那一项，其余项由浅合并保留', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')

    fireEvent.click(within(card).getByRole('switch', { name: 'SMB1 接入告警开关' }))
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenLastCalledWith({ alertRules: { smb1Alert: false } }),
    )

    fireEvent.click(within(card).getByRole('switch', { name: '弱口令空口令告警开关' }))
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenLastCalledWith({
        alertRules: { weakPasswordAlert: false },
      }),
    )

    await pickSelectOption(within(card).getByRole('combobox', { name: '空闲会话提醒' }), '15 分钟')
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenLastCalledWith({
        alertRules: { idleAlertMinutes: 15 },
      }),
    )

    const disk = within(card).getByRole('spinbutton', { name: '磁盘低水位阈值GB' })
    fireEvent.change(disk, { target: { value: '30' } })
    fireEvent.blur(disk)
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenLastCalledWith({ alertRules: { diskLowGb: 30 } }),
    )
    await expectNotice(/磁盘阈值已保存/)

    // 浅合并结果：四项互不覆盖
    expect(persisted.alertRules).toEqual({
      idleAlertMinutes: 15,
      smb1Alert: false,
      weakPasswordAlert: false,
      diskLowGb: 30,
    })
    // 每次补丁只带一个键
    for (const call of apiStub.state.patch.mock.calls as [AppStatePatch][]) {
      expect(Object.keys(call[0].alertRules ?? {})).toHaveLength(1)
    }
  })

  it('空闲提醒选「关闭」提交 null（而非 0）', async () => {
    persisted.alertRules.idleAlertMinutes = 30
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')
    await pickSelectOption(within(card).getByRole('combobox', { name: '空闲会话提醒' }), '关闭')
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenLastCalledWith({
        alertRules: { idleAlertMinutes: null },
      }),
    )
    expect(persisted.alertRules.idleAlertMinutes).toBeNull()
  })

  it('阈值没改动就不提交（避免无意义写入）', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')
    const disk = within(card).getByRole('spinbutton', { name: '磁盘低水位阈值GB' })
    fireEvent.change(disk, { target: { value: '20' } })
    fireEvent.blur(disk)
    await new Promise((r) => setTimeout(r, 50))
    expect(apiStub.state.patch).not.toHaveBeenCalled()
  })

  it('保存失败：原因可见，开关回滚', async () => {
    apiStub.state.patch.mockImplementation(async () => {
      throw new Error('idleAlertMinutes 必须为正数或 null')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')
    fireEvent.click(within(card).getByRole('switch', { name: 'SMB1 接入告警开关' }))
    await expectNotice(/保存失败：idleAlertMinutes 必须为正数或 null/)
    await waitFor(() =>
      expect(within(card).getByRole('switch', { name: 'SMB1 接入告警开关' })).toBeChecked(),
    )
  })
})

describe('设置页 · 防火墙规则（WinShare Panel 组）', () => {
  it('列表只展示组内规则，并写明不动系统规则', async () => {
    renderSettings()
    await openOpsTab()
    await waitFor(() => expect(apiStub.firewall.list).toHaveBeenCalledTimes(1))
    const card = cardByTitle('防火墙规则')
    expect(within(card).getByText('WinShare SMB (445/TCP)')).toBeInTheDocument()
    expect(within(card).getByText('WinShare FTP Passive (50000-51000/TCP)')).toBeInTheDocument()
    expect(within(card).getAllByRole('row')).toHaveLength(3) // 表头 + 2 条组内规则
    expect(
      within(card).getByText(/只管理本应用「WinShare Panel」显示组内创建的规则/),
    ).toBeInTheDocument()
    expect(within(card).getByText('启用')).toBeInTheDocument()
    expect(within(card).getByText('已禁用')).toBeInTheDocument()
  })

  it('预设添加：期望规则由主进程生成后幂等 ensure；组内已存在时如实说"无需重复添加"', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    fireEvent.click(within(card).getByRole('button', { name: /添加预设规则/ }))
    await waitFor(() => expect(apiStub.firewall.preset).toHaveBeenCalledWith('smb', undefined))
    await waitFor(() =>
      expect(apiStub.firewall.ensure).toHaveBeenCalledWith([
        { name: 'WinShare SMB (445/TCP)', ports: '445' },
      ]),
    )
    await expectNotice(/无需重复添加/)
    expect(bodyText()).not.toMatch(/已添加 \d+ 条组内规则/)

    // 换成组内没有的规则名 → 成功文案带上新建规则名
    apiStub.firewall.ensure.mockImplementation(async (rules: DesiredRule[]) =>
      rules.map((x) => x.name),
    )
    fireEvent.click(within(card).getByRole('button', { name: /添加预设规则/ }))
    await expectNotice(/已添加 1 条组内规则：WinShare SMB \(445\/TCP\)/)
  })

  it('ftpPassive 需端口范围：from>to 时禁止提交并说明，范围合法才带 opts 调用', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    await pickSelectOption(
      within(card).getByRole('combobox', { name: '防火墙预设' }),
      'FTP 被动端口范围（自定义）',
    )

    const from = within(card).getByRole('spinbutton', { name: '被动起始端口' })
    const to = within(card).getByRole('spinbutton', { name: '被动结束端口' })
    fireEvent.change(from, { target: { value: '51000' } })
    fireEvent.change(to, { target: { value: '50000' } })
    const addBtn = within(card).getByRole('button', { name: /添加预设规则/ })
    expect(addBtn).toBeDisabled()
    expect(within(card).getByText(/起始 ≤ 结束，填对后才能提交/)).toBeInTheDocument()
    fireEvent.click(addBtn)
    await new Promise((r) => setTimeout(r, 50))
    expect(apiStub.firewall.preset).not.toHaveBeenCalled()

    fireEvent.change(from, { target: { value: '50000' } })
    await waitFor(() =>
      expect(within(card).getByRole('button', { name: /添加预设规则/ })).toBeEnabled(),
    )
    fireEvent.click(within(card).getByRole('button', { name: /添加预设规则/ }))
    await waitFor(() =>
      expect(apiStub.firewall.preset).toHaveBeenCalledWith('ftpPassive', {
        passiveFrom: 50000,
        passiveTo: 50000,
      }),
    )
    await waitFor(() =>
      expect(apiStub.firewall.ensure).toHaveBeenCalledWith([
        { name: 'WinShare FTP Passive (50000-50000/TCP)', ports: '50000-50000' },
      ]),
    )
  })

  it('删除有二次确认：确认前不调用 remove；确认后调用并重读组内规则', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    await waitFor(() => expect(within(card).getAllByRole('row')).toHaveLength(3))
    const row = within(card).getAllByRole('row')[1]
    fireEvent.click(within(row).getByRole('button'))
    await waitFor(() => expect(document.querySelector('.ant-popover')).not.toBeNull())
    expect(screen.getByText(/删除组内规则「WinShare SMB \(445\/TCP\)」？/)).toBeInTheDocument()
    expect(apiStub.firewall.remove).not.toHaveBeenCalled()

    const pops = document.querySelectorAll('.ant-popover')
    const last = pops[pops.length - 1] as Element
    const ok = Array.from(last.querySelectorAll('button')).find(
      (b) => (b.textContent || '').replace(/\s/g, '') === '删除',
    )
    expect(ok).toBeTruthy()
    fireEvent.click(ok!)
    await waitFor(() =>
      expect(apiStub.firewall.remove).toHaveBeenCalledWith('WinShare SMB (445/TCP)'),
    )
    await expectNotice(/已删除组内规则/)
    await waitFor(() => expect(apiStub.firewall.list.mock.calls.length).toBeGreaterThan(1))
  })

  it('删除失败：原因可见', async () => {
    apiStub.firewall.remove.mockImplementation(async () => {
      throw new Error('规则正被占用')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    await waitFor(() => expect(within(card).getAllByRole('row')).toHaveLength(3))
    const row = within(card).getAllByRole('row')[1]
    fireEvent.click(within(row).getByRole('button'))
    const pops = await waitFor(() => document.querySelectorAll('.ant-popover'))
    const last = pops[pops.length - 1] as Element
    const ok = Array.from(last.querySelectorAll('button')).find(
      (b) => (b.textContent || '').replace(/\s/g, '') === '删除',
    )
    fireEvent.click(ok!)
    await expectNotice(/删除防火墙规则失败：规则正被占用/)
    expect(bodyText()).not.toMatch(/已删除组内规则/)
  })

  it('ensure 失败：显示主进程原因，不假装已添加', async () => {
    apiStub.firewall.ensure.mockImplementation(async () => {
      throw new Error('需要管理员权限')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    fireEvent.click(within(card).getByRole('button', { name: /添加预设规则/ }))
    await expectNotice(/添加防火墙规则失败：需要管理员权限/)
    expect(bodyText()).not.toMatch(/已添加 \d+ 条组内规则|无需重复添加/)
  })

  it('列表读取失败：卡片内可见原因 + 重试', async () => {
    apiStub.firewall.list.mockImplementation(async () => {
      throw new Error('Get-NetFirewallRule 失败')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('防火墙规则')
    await waitFor(() =>
      expect(
        within(card).getByText(/读取组内规则失败：Get-NetFirewallRule 失败/),
      ).toBeInTheDocument(),
    )
    expect(within(card).queryByRole('button', { name: /添加预设规则/ })).not.toBeInTheDocument()
  })
})

describe('设置页 · 账号安全体检', () => {
  it('显示 checked 数与问题清单：fail 红 / warn 橙，并附一句建议', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('账号安全体检')
    await waitFor(() => expect(within(card).getByText(/已检查 3 个启用账号/)).toBeInTheDocument())
    expect(within(card).getByText('guest')).toBeInTheDocument()
    expect(within(card).getByText(/可空口令登录/)).toBeInTheDocument()
    expect(within(card).getByText('backup')).toBeInTheDocument()
    expect(within(card).getByText(/密码设置为永不过期/)).toBeInTheDocument()
    // 配色：fail=红、warn=橙，且各自只标注自己那一行
    expect(card.querySelector('.sec-issue-fail .ant-tag')).toHaveClass('ant-tag-red')
    expect(card.querySelector('.sec-issue-warn .ant-tag')).toHaveClass('ant-tag-orange')
    expect(within(card).getByText(/建议：为共享账号设置口令并开启密码过期策略/)).toBeInTheDocument()
  })

  it('无问题时结论与数据一致：只说探测过的三项，不臆造弱口令结论', async () => {
    apiStub.security.report.mockImplementation(async () => reportWith([], 5))
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('账号安全体检')
    await waitFor(() =>
      expect(
        within(card).getByText(
          /未发现空口令 \/ 密码永不过期 \/ 超 180 天未修改的账号（共检查 5 个启用账号）/,
        ),
      ).toBeInTheDocument(),
    )
    expect(card.textContent).not.toMatch(/弱口令/)
    expect(within(card).queryByText(/严重|提醒/)).not.toBeInTheDocument()
  })

  it('体检失败：原因可见，重试后出结果', async () => {
    apiStub.security.report.mockImplementation(async () => {
      throw new Error('Get-LocalUser 被策略拒绝')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('账号安全体检')
    await waitFor(() =>
      expect(within(card).getByText(/体检失败：Get-LocalUser 被策略拒绝/)).toBeInTheDocument(),
    )
    apiStub.security.report.mockImplementation(async () => reportWith([], 2))
    fireEvent.click(within(card).getByRole('button', { name: /重\s*试/ }))
    await waitFor(() => expect(within(card).getByText(/共检查 2 个启用账号/)).toBeInTheDocument())
  })
})

describe('设置页 · 一键诊断入口', () => {
  it('DiagnoseModal 已就绪：按钮可用，点击后加载并打开它', async () => {
    const loader: DiagnoseLoader = vi.fn(async () => ({
      default: ({ open }: { open: boolean }) =>
        open ? <div data-testid="fake-diagnose">诊断向导替身</div> : null,
    }))
    renderSettings({ diagnoseLoader: loader })
    const btn = screen.getByRole('button', { name: /一键诊断/ })
    expect(btn).toBeEnabled()
    fireEvent.click(btn)
    expect(await screen.findByTestId('fake-diagnose')).toBeInTheDocument()
    expect(loader).toHaveBeenCalledTimes(1)
    expect(screen.queryByText(/诊断面板尚未就绪/)).not.toBeInTheDocument()
  })

  it('诊断组件尚未合入：按钮禁用并说明原因，点了也不会没反应', async () => {
    renderSettings({ diagnoseLoader: null })
    const btn = screen.getByRole('button', { name: /一键诊断/ })
    expect(btn).toBeDisabled()
    expect(
      screen.getByText(/诊断面板尚未就绪（src\/components\/DiagnoseModal\.tsx 未合入）/),
    ).toBeInTheDocument()
    fireEvent.click(btn)
    await new Promise((r) => setTimeout(r, 20))
    expect(document.querySelector('.ant-modal')).toBeNull()
  })

  it('模块加载失败：显示原因，不静默', async () => {
    const loader: DiagnoseLoader = vi.fn(async () => {
      throw new Error('Failed to fetch dynamically imported module')
    })
    renderSettings({ diagnoseLoader: loader })
    fireEvent.click(screen.getByRole('button', { name: /一键诊断/ }))
    await expectNotice(/诊断面板打开失败：Failed to fetch/)
    expect(document.querySelector('.ant-modal')).toBeNull()
  })

  it('默认装配自洽：探测到组件就可点且能打开，未探测到就禁用并说明', async () => {
    renderSettings()
    const btn = screen.getByRole('button', { name: /一键诊断/ })
    if (btn.hasAttribute('disabled')) {
      expect(screen.getByText(/诊断面板尚未就绪/)).toBeInTheDocument()
    } else {
      expect(screen.queryByText(/诊断面板尚未就绪/)).not.toBeInTheDocument()
      fireEvent.click(btn)
      await waitFor(
        () => expect(document.querySelector('.ant-modal, .ant-drawer')).not.toBeNull(),
        { timeout: 4000 },
      )
      expect(bodyText()).not.toMatch(/诊断面板打开失败/)
    }
  })
})

describe('设置页 · 读取节奏（无新增轮询风暴）', () => {
  it('打开 Tab 后各读请求只发一次，静置 800ms 不自动重取', async () => {
    renderSettings()
    await openOpsTab()
    await waitFor(() => expect(apiStub.security.report).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(apiStub.firewall.list).toHaveBeenCalledTimes(1))
    await new Promise((r) => setTimeout(r, 800))
    expect(apiStub.security.report).toHaveBeenCalledTimes(1)
    expect(apiStub.firewall.list).toHaveBeenCalledTimes(1)
    expect(apiStub.system.autoStart).toHaveBeenCalledTimes(1)
    expect(apiStub.state.get).toHaveBeenCalledTimes(1)
    expect(apiStub.firewall.preset).not.toHaveBeenCalled()
    expect(apiStub.firewall.ensure).not.toHaveBeenCalled()
  })
})

// 批2（docs/audit/05-renderer-debt.md §3.1 点 14）：磁盘水位草稿的 dirty 语义（C 类派生态迁移回归）
describe('设置页 · 磁盘水位草稿（C 类派生态：渲染期调整 state）', () => {
  /** 外部改 appState.alertRules.diskLowGb（模拟主进程落盘后 store 更新） */
  function externalDiskLowGb(next: number) {
    act(() => {
      const s = useAppStore.getState().state
      useAppStore.setState({ state: { ...s, alertRules: { ...s.alertRules, diskLowGb: next } } })
    })
  }

  const diskInput = (card: HTMLElement) =>
    within(card).getByRole('spinbutton', { name: '磁盘低水位阈值GB' }) as HTMLInputElement

  it('外部值变化→草稿跟随；用户改动→dirty 并只提交这一项', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')
    expect(diskInput(card).value).toBe('20')

    // ① 外部值变化：草稿跟随（不再多一次渲染），且不产生任何提交
    externalDiskLowGb(35)
    await waitFor(() => expect(diskInput(card).value).toBe('35'))
    expect(apiStub.state.patch).not.toHaveBeenCalled()

    // ② 用户改动 → dirty（出现「保存」按钮）→ 提交只带 diskLowGb 一个键
    fireEvent.change(diskInput(card), { target: { value: '30' } })
    const saveBtn = await within(card).findByRole('button', { name: /保\s*存/ })
    fireEvent.click(saveBtn)
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenCalledWith({ alertRules: { diskLowGb: 30 } }),
    )
    expect(apiStub.state.patch).toHaveBeenCalledTimes(1)
    await expectNotice(/磁盘阈值已保存/)
    // 提交后不再 dirty
    await waitFor(() =>
      expect(within(card).queryByRole('button', { name: /保\s*存/ })).not.toBeInTheDocument(),
    )
  })

  it('保存失败：原因可见、草稿退回当前服务端值（不残留脏值）', async () => {
    apiStub.state.patch.mockImplementation(async () => {
      throw new Error('diskLowGb 超出允许范围')
    })
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')

    fireEvent.change(diskInput(card), { target: { value: '99999' } })
    fireEvent.click(await within(card).findByRole('button', { name: /保\s*存/ }))

    await expectNotice(/保存失败：diskLowGb 超出允许范围/)
    // 草稿退回当前值（20）且不再 dirty
    await waitFor(() => expect(diskInput(card).value).toBe('20'))
    await waitFor(() =>
      expect(within(card).queryByRole('button', { name: /保\s*存/ })).not.toBeInTheDocument(),
    )
  })

  it('外部刷新同值不打断"已改但未提交"的草稿（dirty 保持）', async () => {
    renderSettings()
    await openOpsTab()
    const card = cardByTitle('告警规则')

    fireEvent.change(diskInput(card), { target: { value: '30' } })
    await within(card).findByRole('button', { name: /保\s*存/ })

    // 外部刷新：diskLowGb 仍是 20（值未变），草稿必须保持 30 且仍 dirty
    externalDiskLowGb(20)
    expect(diskInput(card).value).toBe('30')
    expect(within(card).getByRole('button', { name: /保\s*存/ })).toBeInTheDocument()

    // 此时提交的仍是用户改的值
    fireEvent.click(within(card).getByRole('button', { name: /保\s*存/ }))
    await waitFor(() =>
      expect(apiStub.state.patch).toHaveBeenCalledWith({ alertRules: { diskLowGb: 30 } }),
    )
  })
})
