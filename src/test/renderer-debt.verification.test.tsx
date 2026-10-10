/**
 * t4 独立验证探针 —— 渲染层债清零（批 2）：14 处 set-state-in-effect 迁移后的行为保持。
 *
 * 目的与边界（诚实声明）：
 * - 本文件**不复用实现者的断言**：IPC 桩、夹具、断言口径与用例名全部自行编写，只沿用仓库既有的
 *   "import 前注入 window.winshare" 手法（规格 §3.3 第 7 条）。
 * - 证据等级只到 **jsdom + 门禁级**：真实 SMB / IIS / 防火墙 / icacls / 开机自启 / 跨小时趋势
 *   **未覆盖**，本文件不构成端到端验证。
 * - 逐条覆盖 t4 契约要求：打开→单次加载（静置后不重发）、切换目标→重载（含慢旧/快新的陈旧数据证伪）、
 *   关闭再打开→回到初始态、加载/保存失败→原因可见且不假成功、协议不支持→降级分支、
 *   编辑态/草稿不丢（含 Settings 磁盘水位草稿 dirty 语义）。
 * - 另有若干证伪型用例（卸载后写 state、F1 的 NTFS 只读视图可见性），其结论写在用例名与断言注释里。
 * - round 2 反转：F1 修复后，NTFS 用例的角色由「固定缺陷」转为「固定修复」（断言方向已反转）；
 *   同时按 captain 的判据更正，加载态不再用「空态文案不得出现」判定（实测该判据无效），
 *   改用可观测判据：`.ant-spin-spinning` 是否存在 + 其 aria-busy（见 busyFlag()）。
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import type { ReactNode } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type {
  AppState,
  AppStatePatch,
  JournalEntry,
  NfsServerConfig,
  NtfsAcl,
  ProtocolDetectionResult,
  ServiceStatus,
  Share,
  SharePermission,
} from '../types'

// 本文件全是 antd + jsdom 的重渲染用例；跑全量时并发负载会拉长首帧，统一放宽上限
vi.setConfig({ testTimeout: 30_000 })

// ============================================================================
// IPC 桩：api.ts 在模块加载时读 window.winshare，必须先注入再 import 组件
// ============================================================================
const stub = vi.hoisted(() => {
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
      export: r(''),
      import: r({ imported: 0, skipped: 0, errors: [] }),
    },
    system: {
      currentUser: r({ name: 'admin' }),
      isAdmin: r(true),
      dashboard: r({}),
      auditLog: r(''),
      health: r({ ok: true, detail: '' }),
      osInfo: r({}),
      selectFolder: r(null),
      openLogFolder: r(''),
      autoStart: r(true),
      setAutoStart: r(true),
      relaunch: r(null),
      pathForFile: vi.fn((): string => ''),
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
      serviceStatus: r({ name: 'NfsService', status: 'Running', startType: 'Automatic' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
    },
    ftp: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'ftpsvc', status: 'Running', startType: 'Automatic' }),
      restart: r(null),
      start: r(null),
      stop: r(null),
    },
    webdav: {
      getConfig: r({}),
      setConfig: r(null),
      restoreDefault: r({}),
      defaultConfig: r({}),
      serviceStatus: r({ name: 'W3SVC', status: 'Running', startType: 'Automatic' }),
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
    security: { report: r({ checked: 0, issues: [], at: 0 }) },
    firewall: { list: r([]), ensure: r([]), remove: r(null), preset: r([]) },
    diagnose: { run: r([]), applyFix: r('') },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import PermissionDrawer from '../components/PermissionDrawer'
import JournalDrawer from '../components/JournalDrawer'
import NfsSettingsPanel from '../components/NfsSettingsPanel'
import AppSettings from '../pages/AppSettings'
import { useAppStore } from '../stores/appStore'
import { useUiStore } from '../stores/uiStore'

// ============================================================================
// 夹具（自行构造，不引用实现者的测试文件）
// ============================================================================
const smbShare = (name: string, path = `D:\\${name}`): Share => ({
  name,
  path,
  description: `${name} 描述`,
  protocol: 'smb',
  type: 'Disk',
  hidden: false,
  encrypted: false,
  concurrentUsers: 0,
  status: 'Enabled',
  cached: false,
})

const nfsShare = (name: string): Share => ({
  ...smbShare(name, `/export/${name}`),
  protocol: 'nfs',
})

const perm = (
  account: string,
  access: SharePermission['access'],
  deny = false,
): SharePermission => ({
  shareName: 'data',
  account,
  accountType: 'User',
  access,
  deny,
})

const journalEntry = (id: string, name: string): JournalEntry => ({
  id,
  ts: Date.parse('2026-10-09T10:00:00Z'),
  action: 'delete',
  protocol: 'smb',
  name,
  detail: `${name} 详情`,
  undoable: true,
})

const nfsConfig = (): NfsServerConfig =>
  ({
    gracefulUnmount: true,
    logActivity: false,
    enableUnmappedAccess: false,
    enableAuthenticationRenegotiation: true,
    tcpConnectionTimeout: 300,
    udpConnectionTimeout: 30,
    restartConnectionTimeout: 45,
    maxConcurrentConnectionsPerUser: 0,
    directoryCacheExpiry: 30,
    anonymousUid: 0,
    anonymousGid: 0,
    gatewayCharacterSet: 'ANSI',
    protocolVersion: 'NFSv3',
  }) as NfsServerConfig

const serviceStatus = (
  name: string,
  status: ServiceStatus['status'] = 'Running',
): ServiceStatus => ({ name, status, startType: 'Automatic' })

const osInfo = () => ({
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
})

/** protocolCaps：只关心 installed 三态，其余字段给足类型所需的最小值 */
const capsWithInstalled = (
  installed: Partial<Record<'smb' | 'nfs' | 'ftp' | 'webdav', boolean>>,
) => {
  const mk = (protocol: 'smb' | 'nfs' | 'ftp' | 'webdav') => ({
    protocol,
    installed: installed[protocol] ?? false,
    installType: 'server-feature' as const,
    serviceName: `${protocol}-svc`,
    serviceStatus: 'Running' as const,
    installCommand: `install ${protocol}`,
    installHint: `${protocol} 安装提示`,
  })
  return {
    smb: mk('smb'),
    nfs: mk('nfs'),
    ftp: mk('ftp'),
    webdav: mk('webdav'),
  } as ProtocolDetectionResult
}

const baseState = (): AppState => ({
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
})

const cloneState = (s: AppState): AppState => JSON.parse(JSON.stringify(s)) as AppState

/** 延迟可控的 promise：用于制造"慢旧目标 / 快新目标"的竞态 */
function deferred<T>() {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

/** 主进程 appstate.json 的模块内镜像：patch 深合并 alertRules */
let persisted: AppState

let queryClient: QueryClient

const wrap = (node: ReactNode) => (
  <ConfigProvider locale={zhCN}>
    <AntdApp>
      <QueryClientProvider client={queryClient}>{node}</QueryClientProvider>
    </AntdApp>
  </ConfigProvider>
)

const renderProbe = (node: ReactNode) => render(wrap(node))

const settle = (ms = 200) => new Promise((r) => setTimeout(r, ms))

const noop = () => undefined

/** antd Spin 的加载态判据（与规格 §3.1 点 4 ③ 同口径） */
const spinning = () => document.querySelector('.ant-spin-spinning')

/**
 * Spin 容器的 aria-busy（round 2 判据更正后的可观测判据之一）：
 * 从 `.ant-spin-spinning` 自身或其最近的 [aria-busy] 祖先上取，取不到返回 null。
 */
const busyFlag = (): string | null => {
  const spin = spinning()
  if (!spin) return null
  return (
    spin.getAttribute('aria-busy') ?? spin.closest('[aria-busy]')?.getAttribute('aria-busy') ?? null
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  persisted = baseState()
  queryClient = new QueryClient({
    defaultOptions: { queries: { retry: 0, refetchOnWindowFocus: false } },
  })
  useAppStore.setState({ state: baseState(), hydrated: true, journal: [] })
  useUiStore.setState({
    refreshTick: 0,
    activeProtocol: 'all',
    protocolCaps: capsWithInstalled({}),
  })

  // 全部默认实现显式重设：避免上一用例的 mockImplementation(Once) 泄漏到下一个用例
  for (const m of [
    stub.share.permissions,
    stub.share.connections,
    stub.share.openFiles,
    stub.user.list,
    stub.user.groups,
    stub.user.setSharePermissions,
    stub.user.ntfsPermissions,
    stub.adapter.permissions,
    stub.adapter.setPermissions,
    stub.state.get,
    stub.state.patch,
    stub.state.journalList,
    stub.system.osInfo,
    stub.system.auditLog,
    stub.system.autoStart,
    stub.smb.getConfig,
    stub.smb.serviceStatus,
    stub.log.tail,
    stub.firewall.list,
    stub.security.report,
    stub.nfs.getConfig,
    stub.nfs.serviceStatus,
    stub.protocol.detect,
  ]) {
    m.mockReset()
  }

  stub.share.permissions.mockImplementation(async () => [])
  stub.share.connections.mockImplementation(async () => ({
    concurrentUsers: 0,
    clientConnections: [],
  }))
  stub.share.openFiles.mockImplementation(async () => [])
  stub.user.list.mockImplementation(async () => [])
  stub.user.groups.mockImplementation(async () => [])
  stub.user.setSharePermissions.mockImplementation(async () => undefined)
  stub.user.ntfsPermissions.mockImplementation(async (path: string) => ({ path, entries: [] }))
  stub.adapter.permissions.mockImplementation(async () => [])
  stub.adapter.setPermissions.mockImplementation(async () => undefined)
  stub.state.get.mockImplementation(async () => cloneState(persisted))
  stub.state.patch.mockImplementation(async (p: AppStatePatch) => {
    persisted = {
      ...persisted,
      ...p,
      alertRules: { ...persisted.alertRules, ...(p.alertRules ?? {}) },
    }
    return cloneState(persisted)
  })
  stub.state.journalList.mockImplementation(async () => [])
  stub.system.osInfo.mockImplementation(async () => osInfo())
  stub.system.auditLog.mockImplementation(async () => '')
  stub.system.autoStart.mockImplementation(async () => true)
  stub.smb.getConfig.mockImplementation(async () => ({}))
  stub.smb.serviceStatus.mockImplementation(async () => serviceStatus('LanmanServer'))
  stub.log.tail.mockImplementation(async () => '')
  stub.firewall.list.mockImplementation(async () => [])
  stub.security.report.mockImplementation(async () => ({ checked: 0, issues: [], at: 0 }))
  stub.nfs.getConfig.mockImplementation(async () => nfsConfig())
  stub.nfs.serviceStatus.mockImplementation(async () => serviceStatus('NfsService'))
  stub.protocol.detect.mockImplementation(async () => capsWithInstalled({ nfs: true }))
})

afterEach(async () => {
  cleanup()
  // 冲刷卸载竞态（react-query 取消 / 迟到响应），避免跨用例假阳性
  await settle(0)
})

// ============================================================================
// A. 抽屉/弹窗：打开→单次加载、切换目标→重载、关闭再打开、失败可见、降级
// ============================================================================
describe('t4 探针 · PermissionDrawer（点 5：B+A 迁移）', () => {
  it('打开即取数：permissions/user.list/user.groups 各恰好 1 次，静置后不重发，且加载中不闪空态', async () => {
    const pending = deferred<SharePermission[]>()
    stub.share.permissions.mockImplementation(() => pending.promise)

    renderProbe(<PermissionDrawer open share={smbShare('data')} onClose={noop} />)

    expect(stub.share.permissions).toHaveBeenCalledTimes(1)
    expect(stub.share.permissions).toHaveBeenCalledWith('data')
    expect(stub.user.list).toHaveBeenCalledTimes(1)
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
    // 渲染期调整 loading（§1.6 收紧 1）：首次可观测 DOM 就已是加载态。
    // 【判据更正（round 2）】不再使用「加载中不得出现空态文案」——round 1 实测 antd Table 的
    // locale.emptyText 在 Spin 期间仍留在 DOM（`暂无权限条目` 一直存在），该判据无效（captain 已认定
    // 属规格缺陷）。改用可观测判据：Spin 容器 `.ant-spin-spinning` 是否存在 + 其 aria-busy。
    expect(spinning()).not.toBeNull()
    expect(busyFlag()).toBe('true')
    // 记录事实（不作为判据）：空态文案在 Spin 期间确实仍在 DOM 中
    expect(screen.getByText('暂无权限条目')).toBeInTheDocument()

    await act(async () => {
      pending.resolve([perm('alice', 'Read')])
    })
    await screen.findByText('alice')
    await waitFor(() => expect(spinning()).toBeNull())

    await settle()
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)
    expect(stub.user.list).toHaveBeenCalledTimes(1)
    expect(stub.user.groups).toHaveBeenCalledTimes(1)
  })

  it('切换共享（旧目标慢、新目标快）：新目标数据最终胜出，迟到的旧响应不覆盖', async () => {
    const slowOld = deferred<SharePermission[]>()
    const fastNew = deferred<SharePermission[]>()
    stub.share.permissions
      .mockImplementationOnce(() => slowOld.promise)
      .mockImplementationOnce(() => fastNew.promise)

    const { rerender } = renderProbe(
      <PermissionDrawer open share={smbShare('data')} onClose={noop} />,
    )
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)

    rerender(wrap(<PermissionDrawer open share={smbShare('docs')} onClose={noop} />))
    expect(stub.share.permissions).toHaveBeenCalledTimes(2)
    expect(stub.share.permissions).toHaveBeenLastCalledWith('docs')

    await act(async () => {
      fastNew.resolve([perm('new-owner', 'Full')])
    })
    await screen.findByText('new-owner')

    // 旧目标（data）的响应晚到：必须被 dead 守卫丢弃
    await act(async () => {
      slowOld.resolve([perm('stale-owner', 'Read')])
    })
    await settle(0)
    expect(screen.queryByText('stale-owner')).not.toBeInTheDocument()
    expect(screen.getByText('new-owner')).toBeInTheDocument()
  })

  it('关闭再打开：权限行重读 1 次（合计 2），NTFS 页回到「未加载」初始态', async () => {
    stub.share.permissions.mockImplementation(async () => [perm('carol', 'Change')])

    const { rerender } = renderProbe(
      <PermissionDrawer open share={smbShare('data')} onClose={noop} />,
    )
    await screen.findByText('carol')
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)

    // 与真实父组件一致：关闭时把 share 置空
    rerender(wrap(<PermissionDrawer open={false} share={null} onClose={noop} />))
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)

    rerender(wrap(<PermissionDrawer open share={smbShare('data')} onClose={noop} />))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    await screen.findByText('carol')

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(screen.getByText('点击「加载」查看 NTFS ACL（只读）')).toBeInTheDocument()
  })

  it('加载失败：原因可见、空态保留、不出现假成功', async () => {
    stub.share.permissions.mockImplementation(async () => {
      throw new Error('SMB 权限通道不可用')
    })

    renderProbe(<PermissionDrawer open share={smbShare('data')} onClose={noop} />)

    await screen.findByText(/SMB 权限通道不可用/)
    expect(screen.getByText('暂无权限条目')).toBeInTheDocument()
    expect(screen.queryByText('权限已保存')).not.toBeInTheDocument()
    await waitFor(() => expect(spinning()).toBeNull())
  })

  it('编辑态不丢：本地加行后保存失败 → 原因可见、不重读、编辑行仍在', async () => {
    stub.share.permissions.mockImplementation(async () => [])
    stub.user.setSharePermissions.mockImplementation(async () => {
      throw new Error('共享权限写入被拒绝')
    })

    renderProbe(<PermissionDrawer open share={smbShare('data')} onClose={noop} />)
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.change(screen.getByPlaceholderText('账号名'), { target: { value: 'bob' } })
    fireEvent.click(screen.getByRole('button', { name: /添\s*加/ }))
    expect(screen.getByText('bob')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    fireEvent.click(await screen.findByRole('button', { name: /确\s*定/ }))
    await screen.findByText(/共享权限写入被拒绝/)
    // 保存成功才会重读：这里必须仍是 1 次
    await settle()
    expect(stub.share.permissions).toHaveBeenCalledTimes(1)
    expect(screen.getByText('bob')).toBeInTheDocument()
    expect(screen.queryByText('权限已保存')).not.toBeInTheDocument()
  })

  it('协议降级：非 SMB 共享走 NfsPermPanel（不碰 share.permissions），adapter 各 1 次且文案保留', async () => {
    stub.adapter.permissions.mockImplementation(async () => [
      {
        shareName: 'nfsshare',
        account: '10.0.0.7',
        accountType: 'Group',
        access: 'Read',
        deny: false,
      },
    ])

    renderProbe(<PermissionDrawer open share={nfsShare('nfsshare')} onClose={noop} />)

    await screen.findByText('10.0.0.7')
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)
    expect(stub.adapter.permissions).toHaveBeenCalledWith('nfs', 'nfsshare')
    // SMB 遗留通道与 SMB 保存通道都不得被触碰
    expect(stub.share.permissions).not.toHaveBeenCalled()
    expect(stub.user.setSharePermissions).not.toHaveBeenCalled()

    await settle()
    expect(stub.adapter.permissions).toHaveBeenCalledTimes(1)

    // 协议特有文案逐字保留（含保存二次确认标题）
    expect(screen.getByText('添加客户端规则')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    await screen.findByText('确认覆盖当前 NFS 客户端权限？')
  })

  it('保存成功：setPermissions 参数逐项正确、重读 1 次、成功文案可见（WebDAV 三档映射）', async () => {
    stub.adapter.permissions.mockImplementation(async () => [
      { shareName: 'wdpane', account: 'alice', accountType: 'User', access: 'Change', deny: false },
    ])
    stub.adapter.setPermissions.mockImplementation(async () => undefined)

    renderProbe(
      <PermissionDrawer
        open
        share={{ ...smbShare('wdpane', '/srv/wd'), protocol: 'webdav' }}
        onClose={noop}
      />,
    )

    await screen.findByText('alice')
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    fireEvent.click(await screen.findByRole('button', { name: /确\s*定/ }))

    await waitFor(() => expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1))
    // 权限模型映射不变：Change→rw（再回读 Change）、deny:false；accountType 原样保留
    expect(stub.adapter.setPermissions).toHaveBeenCalledWith('webdav', 'wdpane', [
      { shareName: 'wdpane', account: 'alice', accountType: 'User', access: 'Change', deny: false },
    ])
    await screen.findByText('作者规则已保存')
    // 保存成功 = 保存 1 次 + 重读 1 次
    await waitFor(() => expect(stub.adapter.permissions).toHaveBeenCalledTimes(2))
    await settle()
    expect(stub.adapter.setPermissions).toHaveBeenCalledTimes(1)
    // WebDAV 无「授权/拒绝」列
    expect(screen.queryByText('授权')).not.toBeInTheDocument()
  })

  it('证伪·卸载期间迟到响应不报错（dead 守卫）', async () => {
    const errors: string[] = []
    const spy = vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
      errors.push(args.map(String).join(' '))
    })
    try {
      const pending = deferred<SharePermission[]>()
      stub.share.permissions.mockImplementation(() => pending.promise)

      const { unmount } = renderProbe(
        <PermissionDrawer open share={smbShare('data')} onClose={noop} />,
      )
      unmount()
      await act(async () => {
        pending.resolve([perm('late', 'Read')])
      })
      await settle(0)

      // React 19 已移除"卸载后 setState"的控制台警告，故此断言只证明"没有异常/告警"；
      // dead 守卫的真正证据是上一条竞态用例（迟到响应不覆盖）。
      expect(errors.filter((e) => /unmount|setState|not wrapped in act/i.test(e))).toEqual([])
    } finally {
      spy.mockRestore()
    }
  })

  /**
   * F1 回归（round 2 反转）——NTFS 只读视图在「重新加载」后必须保持已加载。
   *
   * 证据件角色的正当演进：
   * - round 1：本用例名为「证伪·「重新加载」会清空已加载的 NTFS 只读视图」，**固定的是修复前的缺陷行为**
   *   （旧断言：ACL 被清空 + 出现「点击「加载」查看 NTFS ACL（只读）」）。它当时按事实断言，是 F1 的取证。
   * - round 2：F1 已由 t8 修复（PermissionDrawer 把 NTFS 归零的 reset 键与带 nonce 的 loading 键拆开）。
   *   该用例随即转红——原始失败输出正是修复生效的机器证明：
   *   `expected document not to contain element, found <td class="ant-table-cell ant-table-cell-ellipsis"
   *    title="BUILTIN\Administrators">BUILTIN\Administrators</td> instead`（src/test/renderer-debt.verification.test.tsx:662）。
   *   因此断言方向反转为**固定修复后行为**：ACL 内容仍在、不出现「未加载」提示、ntfsPermissions 调用次数不变。
   * - 反转由验证者本人完成（实现者不得编辑验证者的取证件，captain 已驳回代改方案）。
   */
  it('固定修复·「重新加载」不清空已加载的 NTFS 只读视图（F1 回归）', async () => {
    stub.share.permissions.mockImplementation(async () => [])
    const acl: NtfsAcl = {
      path: 'D:\\data',
      entries: [
        { account: 'BUILTIN\\Administrators', rights: 'Full', type: 'Allow', inherited: false },
      ],
    }
    stub.user.ntfsPermissions.mockImplementation(async () => acl)

    renderProbe(<PermissionDrawer open share={smbShare('data')} onClose={noop} />)
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }))
    await screen.findByText('BUILTIN\\Administrators')
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)

    // 回到共享权限页签，点「重新加载」：只重读共享权限，NTFS 视图必须保持已加载（HEAD 语义）
    fireEvent.click(screen.getByRole('tab', { name: '共享权限' }))
    fireEvent.click(screen.getByRole('button', { name: /重新加载/ }))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(screen.getByText('BUILTIN\\Administrators')).toBeInTheDocument()
    expect(screen.queryByText('点击「加载」查看 NTFS ACL（只读）')).not.toBeInTheDocument()
    // 重读不触发 NTFS 重取（事件路径仍是唯一入口）
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)
  })

  it('固定修复·保存成功后重读同样不清空 NTFS 只读视图（F1 第二条路径）', async () => {
    stub.share.permissions.mockImplementation(async () => [])
    stub.user.setSharePermissions.mockImplementation(async () => undefined)
    const acl: NtfsAcl = {
      path: 'D:\\data',
      entries: [{ account: 'D:\\admins', rights: 'Modify', type: 'Allow', inherited: true }],
    }
    stub.user.ntfsPermissions.mockImplementation(async () => acl)

    renderProbe(<PermissionDrawer open share={smbShare('data')} onClose={noop} />)
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }))
    await screen.findByText('D:\\admins')

    fireEvent.click(screen.getByRole('tab', { name: '共享权限' }))
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    fireEvent.click(await screen.findByRole('button', { name: /确\s*定/ }))

    await screen.findByText('权限已保存')
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    expect(screen.getByText('D:\\admins')).toBeInTheDocument()
    expect(screen.queryByText('点击「加载」查看 NTFS ACL（只读）')).not.toBeInTheDocument()
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)
  })

  it('语义保留·切换共享 → NTFS 视图回到「未加载」初始态且文案逐字未变', async () => {
    stub.share.permissions.mockImplementation(async () => [])
    stub.user.ntfsPermissions.mockImplementation(async () => ({
      path: 'D:\\data',
      entries: [{ account: 'svc-backup', rights: 'Read', type: 'Allow', inherited: false }],
    }))

    const { rerender } = renderProbe(
      <PermissionDrawer open share={smbShare('data')} onClose={noop} />,
    )
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(spinning()).toBeNull())

    fireEvent.click(screen.getByRole('tab', { name: 'NTFS 权限' }))
    fireEvent.click(await screen.findByRole('button', { name: /加\s*载/ }))
    await screen.findByText('svc-backup')
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)

    // 换共享：NTFS 只读视图归零（与 HEAD 的 [open, share] effect 语义一致）
    rerender(wrap(<PermissionDrawer open share={smbShare('docs')} onClose={noop} />))
    await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledTimes(2))
    await waitFor(() => expect(spinning()).toBeNull())

    expect(screen.queryByText('svc-backup')).not.toBeInTheDocument()
    // 文案逐字未变（不是改写成别的提示）
    expect(screen.getByText('点击「加载」查看 NTFS ACL（只读）')).toBeInTheDocument()
    // 换共享不自动重读 NTFS（仍是「点了加载才读」的事件路径）
    expect(stub.user.ntfsPermissions).toHaveBeenCalledTimes(1)
  })
})

// ============================================================================
// B. 回收站抽屉：stale-while-revalidate + 失败不假成功
// ============================================================================
describe('t4 探针 · JournalDrawer（点 4：B 类迁移）', () => {
  it('store 为空时打开：出现加载态、恰好 1 次 journalList，静置后不重发', async () => {
    stub.state.journalList.mockImplementation(async () => [journalEntry('j1', '演示共享')])

    renderProbe(<JournalDrawer open onClose={noop} />)

    expect(spinning()).not.toBeNull()
    expect(stub.state.journalList).toHaveBeenCalledTimes(1)

    await screen.findByText('演示共享')
    await waitFor(() => expect(spinning()).toBeNull())
    await settle()
    expect(stub.state.journalList).toHaveBeenCalledTimes(1)
  })

  it('store 已有数据时打开：不闪加载态，旧数据首帧即可读（stale-while-revalidate）', async () => {
    useAppStore.setState({ journal: [journalEntry('old', '既有记录')] })
    const pending = deferred<JournalEntry[]>()
    stub.state.journalList.mockImplementation(() => pending.promise)

    renderProbe(<JournalDrawer open onClose={noop} />)

    expect(spinning()).toBeNull()
    expect(screen.getByText('既有记录')).toBeInTheDocument()
    expect(stub.state.journalList).toHaveBeenCalledTimes(1)

    await act(async () => {
      pending.resolve([journalEntry('new', '刷新后记录')])
    })
    await screen.findByText('刷新后记录')
  })

  it('加载失败：原因可见、不假成功；列表为空是既有 appStore 语义（本批次未改动 appStore）', async () => {
    stub.state.journalList.mockImplementation(async () => {
      throw new Error('操作日志通道不可用')
    })

    renderProbe(<JournalDrawer open onClose={noop} />)

    await screen.findByText(/操作日志通道不可用/)
    expect(screen.queryByText('撤销成功')).not.toBeInTheDocument()
    await waitFor(() => expect(spinning()).toBeNull())
    // appStore.loadJournal 的 catch 会置空列表（HEAD 即如此），与规格 §3.1 点 4 ④「保持原样」不符
    expect(screen.getByText('还没有可撤销的操作')).toBeInTheDocument()
  })
})

// ============================================================================
// C. 设置页磁盘水位草稿：外部值跟随 / dirty / 保存失败退回
// ============================================================================
describe('t4 探针 · Settings 磁盘水位草稿（点 14：C 类派生态迁移）', () => {
  const openOpsTab = async () => {
    // 该卡片随「应用设置」提为侧栏独立页（原先挂在设置页的一个页签下），故直接渲染该页
    renderProbe(<AppSettings />)
    await screen.findByText('磁盘低水位阈值（GB）')
  }

  const diskCard = () => {
    const card = screen.getByText('告警规则').closest('.ant-card')
    if (!card) throw new Error('未找到「告警规则」卡片')
    return card as HTMLElement
  }

  const diskInput = () =>
    within(diskCard()).getByRole('spinbutton', { name: '磁盘低水位阈值GB' }) as HTMLInputElement

  const setExternal = (next: number) => {
    act(() => {
      const s = useAppStore.getState().state
      useAppStore.setState({ state: { ...s, alertRules: { ...s.alertRules, diskLowGb: next } } })
    })
  }

  it('外部值变化→草稿跟随且不产生提交；用户改动→出现保存按钮并只提交该项', async () => {
    await openOpsTab()
    expect(diskInput().value).toBe('20')
    expect(within(diskCard()).queryByRole('button', { name: /保\s*存/ })).not.toBeInTheDocument()

    setExternal(45)
    await waitFor(() => expect(diskInput().value).toBe('45'))
    expect(stub.state.patch).not.toHaveBeenCalled()

    fireEvent.change(diskInput(), { target: { value: '30' } })
    const save = await within(diskCard()).findByRole('button', { name: /保\s*存/ })
    fireEvent.click(save)

    await waitFor(() => expect(stub.state.patch).toHaveBeenCalledTimes(1))
    expect(stub.state.patch).toHaveBeenCalledWith({ alertRules: { diskLowGb: 30 } })
    await screen.findByText(/磁盘阈值已保存/)
    // 提交后回到非 dirty（草稿跟随服务端新值 30）
    await waitFor(() =>
      expect(within(diskCard()).queryByRole('button', { name: /保\s*存/ })).not.toBeInTheDocument(),
    )
  })

  it('保存失败：原因可见、草稿退回当前服务端值、不残留脏值', async () => {
    stub.state.patch.mockImplementation(async () => {
      throw new Error('diskLowGb 超出允许范围')
    })

    await openOpsTab()
    fireEvent.change(diskInput(), { target: { value: '99999' } })
    fireEvent.click(await within(diskCard()).findByRole('button', { name: /保\s*存/ }))

    await screen.findByText(/保存失败：diskLowGb 超出允许范围/)
    await waitFor(() => expect(diskInput().value).toBe('20'))
    expect(within(diskCard()).queryByRole('button', { name: /保\s*存/ })).not.toBeInTheDocument()
  })
})

// ============================================================================
// D. R-5 三 SettingsPanel 半边：能力门控、单次读取、降级分支
// ============================================================================
describe('t4 探针 · NfsSettingsPanel（R-5 SettingsPanel 半边）', () => {
  it('已安装：getConfig/serviceStatus 各恰好 1 次，「刷新」触发失效重取', async () => {
    useUiStore.setState({ protocolCaps: capsWithInstalled({ nfs: true }) })

    renderProbe(<NfsSettingsPanel />)

    await waitFor(() => expect(stub.nfs.getConfig).toHaveBeenCalledTimes(1))
    expect(stub.nfs.serviceStatus).toHaveBeenCalledTimes(1)
    expect(await screen.findByText('服务状态')).toBeInTheDocument()
    expect(screen.getByText('NfsService')).toBeInTheDocument()
    expect(screen.queryByText('NFS 协议未安装')).not.toBeInTheDocument()

    await settle()
    expect(stub.nfs.getConfig).toHaveBeenCalledTimes(1)

    fireEvent.click(screen.getByRole('button', { name: /刷\s*新/ }))
    await waitFor(() => expect(stub.nfs.getConfig).toHaveBeenCalledTimes(2))
  })

  it('未安装：渲染降级引导且不调用 getConfig（enabled 门控生效）', async () => {
    useUiStore.setState({ protocolCaps: capsWithInstalled({ nfs: false }) })

    renderProbe(<NfsSettingsPanel />)

    expect(await screen.findByText('NFS 协议未安装')).toBeInTheDocument()
    await settle()
    expect(stub.nfs.getConfig).not.toHaveBeenCalled()
    expect(stub.nfs.serviceStatus).not.toHaveBeenCalled()
  })
})
