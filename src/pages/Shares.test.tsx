import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor, fireEvent, cleanup } from '@testing-library/react'
import { App as AntdApp, ConfigProvider } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type {
  AppState,
  CreateShareInput,
  DiskUsage,
  ProtocolDetectionResult,
  Share,
} from '../types'

// 批1 运维体验（磁盘水位 / 置顶 / 连接中筛选 / 智能默认值 / 双模式）行为回归。
// 与 Sessions.test.tsx 同一套路：window.winshare 桩必须先于被测模块求值（vi.hoisted）。
const stub = vi.hoisted(() => {
  // 默认返回 Promise：任何未被用例显式接管的 IPC 调用都不会在组件 effect 里炸链式 .then
  const fn = () => vi.fn().mockResolvedValue(undefined)
  const s = {
    adapter: {
      list: fn(),
      create: fn(),
      update: fn(),
      delete: fn(),
      toggle: fn(),
      permissions: fn(),
      setPermissions: fn(),
      sessions: fn(),
      closeSession: fn(),
      capabilities: fn(),
    },
    preset: { list: fn() },
    user: {
      list: fn(),
      groups: fn(),
      sharePermissions: fn(),
      sharePermissionsForUser: fn(),
      ntfsPermissions: fn(),
      setSharePermissions: fn(),
    },
    share: {
      list: fn(),
      get: fn(),
      permissions: fn(),
      connections: fn(),
      openFiles: fn(),
      closeOpenFiles: fn(),
      exportConfig: fn(),
      importConfig: fn(),
    },
    system: {
      osInfo: fn(),
      selectFolder: fn(),
      pathForFile: fn(),
      currentUser: fn(),
      isAdmin: fn(),
    },
    protocol: { detect: fn(), install: fn() },
    disk: { usages: fn(), suggestRoot: fn() },
    state: { get: fn(), patch: fn(), journalList: fn(), journalUndo: fn(), journalClear: fn() },
    session: { list: fn(), files: fn() },
    log: { write: fn(), tail: fn() },
    window: { showBalloon: fn() },
  }
  ;(globalThis.window as unknown as { winshare: unknown }).winshare = s
  return s
})

import Shares from './Shares'
import type { SharesProps } from './Shares'
import { useUiStore } from '../stores/uiStore'
import { useAppStore } from '../stores/appStore'

const share = (name: string, path: string, over: Partial<Share> = {}): Share => ({
  name,
  path,
  description: '',
  protocol: 'smb',
  type: 'Disk',
  hidden: false,
  encrypted: false,
  concurrentUsers: 0,
  status: 'Enabled',
  cached: false,
  ...over,
})

const usage = (drive: string, freeGB: number, totalGB: number, freePct: number): DiskUsage => ({
  drive,
  freeGB,
  totalGB,
  freePct,
})

const appState = (over: Partial<AppState> = {}): AppState => ({
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: { idleAlertMinutes: null, smb1Alert: true, weakPasswordAlert: true, diskLowGb: 20 },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
  ...over,
})

const DETECT: ProtocolDetectionResult = {
  smb: {
    protocol: 'smb',
    installed: true,
    installType: 'builtin',
    serviceName: 'LanmanServer',
    serviceStatus: 'Running',
    installCommand: '',
    installHint: '',
  },
  nfs: {
    protocol: 'nfs',
    installed: true,
    installType: 'server-feature',
    serviceName: 'nfs-server',
    serviceStatus: 'Running',
    installCommand: '',
    installHint: '',
  },
  ftp: {
    protocol: 'ftp',
    installed: true,
    installType: 'iis-role',
    serviceName: 'FTPSVC',
    serviceStatus: 'Running',
    installCommand: '',
    installHint: '',
  },
  webdav: {
    protocol: 'webdav',
    installed: true,
    installType: 'iis-role',
    serviceName: 'W3SVC',
    serviceStatus: 'Running',
    installCommand: '',
    installHint: '',
  },
}

// C 盘告急（剩 2GB）、E 盘偏紧（8% → warn）、D 盘健康、F 盘默认不报警（100GB/45%）
const DISKS: DiskUsage[] = [
  usage('C:', 2, 200, 1),
  usage('E:', 15, 500, 8),
  usage('D:', 400, 1000, 40),
  usage('F:', 100, 1000, 45),
]

// \\fs01\public 无本地盘符；\\?\ 之类的 Z: 盘不在查盘结果里（未知用量）
const SHARES: Share[] = [
  share('urgent', 'C:\\share\\urgent'),
  share('lowish', 'E:\\share\\lowish', { concurrentUsers: 4 }),
  share('healthy', 'D:\\share\\healthy'),
  share('public', '\\\\fs01\\public', { concurrentUsers: 2 }),
  share('nofs', 'Z:\\ghost'),
  share('site', 'D:\\iis\\site', { protocol: 'ftp', type: 'Disk', port: 21 }),
]

// antd + jsdom 下单条用例首轮渲染就要秒级：给足超时，避免把环境开销误读成功能失败。
// 用 options 对象而不是裸数字：vitest 的 it 重载为 (name, fn?, timeout?) —— 数字必须放第三位，
// 而 (name, options, fn) 这种写法才允许排在用例体前面，语义等价（captain 修复签名不匹配）。
const UI_TIMEOUT = { timeout: 30_000 }

function renderPage(props: SharesProps = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: 0 } } })
  return render(
    <ConfigProvider locale={zhCN}>
      <AntdApp>
        <QueryClientProvider client={qc}>
          <Shares {...props} />
        </QueryClientProvider>
      </AntdApp>
    </ConfigProvider>,
  )
}

/** 行顺序（rowKey=protocol:name）——比读单元格文本稳定，不受水位标记影响 */
function rowKeys(): string[] {
  return Array.from(document.querySelectorAll('.ant-table-tbody tr[data-row-key]')).map(
    (tr) => (tr as HTMLElement).dataset.rowKey ?? '',
  )
}

function diskTags(level: 'warn' | 'danger'): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>(`.disk-${level}`))
}

beforeEach(() => {
  useUiStore.setState({
    activeProtocol: 'all',
    protocolCaps: DETECT,
    selectedShares: [],
    shareCreateOpen: false,
    refreshTick: 0,
    shareDeleteTick: 0,
    shareToggleTick: 0,
    detailTick: 0,
    detailShare: null,
  })
  useAppStore.setState({ hydrated: true, journal: [], state: appState() })

  stub.adapter.list.mockImplementation(async (proto?: string) =>
    proto ? SHARES.filter((s) => s.protocol === proto) : [...SHARES],
  )
  stub.adapter.capabilities.mockResolvedValue({ smb: null, nfs: null, ftp: null, webdav: null })
  stub.adapter.create.mockResolvedValue(SHARES[0])
  stub.adapter.update.mockResolvedValue(SHARES[0])
  stub.adapter.delete.mockResolvedValue(undefined)
  stub.adapter.toggle.mockResolvedValue(undefined)
  stub.preset.list.mockResolvedValue([])
  stub.user.list.mockResolvedValue([{ name: 'alice' }])
  stub.user.groups.mockResolvedValue([{ name: 'Users' }, { name: 'Administrators' }])
  stub.share.get.mockResolvedValue(SHARES[0])
  stub.system.osInfo.mockResolvedValue({
    caption: 'Microsoft Windows Server 2022 Standard',
    buildNumber: 20348,
    skuId: 8,
    skuName: 'Server',
    isServer: true,
    isHomeEdition: false,
    hostname: 'HOST',
    features: {
      smbShareModule: true,
      nfsServerCmdlets: true,
      iisAvailable: true,
      smbQuicConfig: true,
    },
  })
  stub.system.selectFolder.mockResolvedValue(null)
  stub.system.currentUser.mockResolvedValue({
    username: 'admin',
    isAdmin: true,
    computerName: 'PC-OPS',
  })
  stub.protocol.detect.mockResolvedValue(DETECT)
  stub.disk.usages.mockResolvedValue(DISKS)
  stub.disk.suggestRoot.mockResolvedValue('D:\\share\\suggested')
  stub.state.get.mockResolvedValue(appState())
  stub.state.patch.mockImplementation(async (p: Partial<AppState>) => ({
    ...useAppStore.getState().state,
    ...p,
  }))
})

afterEach(async () => {
  cleanup()
  await new Promise((r) => setTimeout(r, 0))
  vi.clearAllMocks()
})

describe('Shares · 磁盘水位', () => {
  it('warn 黄标 / danger 红标；ok、UNC、查不到用量的盘都不显示水位', UI_TIMEOUT, async () => {
    renderPage()
    await screen.findByText('urgent')

    expect(diskTags('danger')).toHaveLength(1)
    expect(diskTags('danger')[0].className).toContain('ant-tag-error')
    expect(diskTags('danger')[0].textContent).toContain('剩 2 GB')

    expect(diskTags('warn')).toHaveLength(1)
    expect(diskTags('warn')[0].className).toContain('ant-tag-warning')

    // healthy(D 盘充足) / public(UNC 无盘符) / nofs(Z 盘查无用量) / site(D 盘) 都不打标——
    // 未知就是未知，不能显示 0% 或空标记去误导运维
    expect(document.querySelectorAll('.disk-warn, .disk-danger')).toHaveLength(2)
  })

  it('阈值取 appState.alertRules.diskLowGb：提高阈值后同一块盘亮黄标', UI_TIMEOUT, async () => {
    stub.adapter.list.mockImplementation(async () => [share('big', 'F:\\data\\big')])
    renderPage()
    await screen.findByText('big')
    expect(document.querySelectorAll('.disk-warn, .disk-danger')).toHaveLength(0)

    useAppStore.setState({
      state: appState({ alertRules: { ...appState().alertRules, diskLowGb: 200 } }),
    })
    await waitFor(() => expect(diskTags('warn')).toHaveLength(1))
    expect(diskTags('warn')[0].textContent).toContain('剩 100 GB')
  })

  it('查盘失败（disk.usages 抛错）不影响列表，且不打任何水位标记', UI_TIMEOUT, async () => {
    stub.disk.usages.mockRejectedValue(new Error('Get-PSDrive 失败'))
    renderPage()
    await screen.findByText('urgent')
    expect(document.querySelectorAll('.disk-warn, .disk-danger')).toHaveLength(0)
  })
})

describe('Shares · 置顶', () => {
  it(
    '置顶行排到最前并带星标态，状态经 appState.pinned(protocol:name) 持久化',
    UI_TIMEOUT,
    async () => {
      renderPage()
      await screen.findByText('healthy')
      expect(rowKeys()[0]).toBe('smb:urgent')

      fireEvent.click(screen.getByRole('button', { name: '置顶 healthy' }))

      // 持久化走 appState.patch（主进程 appstate.json 才是真相源），key=protocol:name
      await waitFor(() =>
        expect(stub.state.patch).toHaveBeenCalledWith({ pinned: ['smb:healthy'] }),
      )
      expect(useAppStore.getState().state.pinned).toEqual(['smb:healthy'])

      // 排序：置顶在最前，其余保持原序
      await waitFor(() => expect(rowKeys()[0]).toBe('smb:healthy'))
      expect(rowKeys()).toEqual([
        'smb:healthy',
        'smb:urgent',
        'smb:lowish',
        'smb:public',
        'smb:nofs',
        'ftp:site',
      ])
      // 星标态可感知（按钮语义翻转为"取消置顶"）
      expect(screen.getByRole('button', { name: '取消置顶 healthy' })).toBeInTheDocument()
    },
  )

  it('重开应用（以已持久化的 pinned 重新挂载）后仍排在最前', UI_TIMEOUT, async () => {
    useAppStore.setState({ state: appState({ pinned: ['smb:nofs'] }) })
    renderPage()
    await screen.findByText('nofs')
    expect(rowKeys()[0]).toBe('smb:nofs')
    fireEvent.click(screen.getByRole('button', { name: '取消置顶 nofs' }))
    await waitFor(() => expect(stub.state.patch).toHaveBeenLastCalledWith({ pinned: [] }))
  })
})

describe('Shares · 只看有人连接', () => {
  it('筛选开关生效，且与关键字搜索、协议 Tab 叠加', UI_TIMEOUT, async () => {
    renderPage()
    await screen.findByText('lowish')
    expect(rowKeys()).toHaveLength(6)

    fireEvent.click(screen.getByRole('checkbox', { name: /只看有人连接/ }))
    await waitFor(() => expect(rowKeys()).toEqual(['smb:lowish', 'smb:public']))

    // 叠加关键字：连接中 + 名字含 low → 只剩一行
    fireEvent.change(screen.getByPlaceholderText('搜索名称 / 路径 / 描述'), {
      target: { value: 'low' },
    })
    await waitFor(() => expect(rowKeys()).toEqual(['smb:lowish']))

    // 叠加协议 Tab：主进程按 smb 筛选，连接中筛选继续保持生效
    fireEvent.click(screen.getByRole('tab', { name: 'SMB' }))
    await waitFor(() => expect(stub.adapter.list).toHaveBeenCalledWith('smb'))
    fireEvent.change(screen.getByPlaceholderText('搜索名称 / 路径 / 描述'), {
      target: { value: '' },
    })
    await waitFor(() => expect(rowKeys()).toEqual(['smb:lowish', 'smb:public']))
  })
})

describe('Shares · 新建共享智能默认值', () => {
  it(
    '路径取 suggestRoot，改名后自动带出共享名，手改过的名字不被覆盖，默认 Users 读取',
    UI_TIMEOUT,
    async () => {
      renderPage()
      fireEvent.click(screen.getByRole('button', { name: /新建共享/ }))

      // findBy* 的返回类型是 HTMLElement，取 value 需窄化为输入元素
      const pathInput = (await screen.findByPlaceholderText('如 D:\\Share')) as HTMLInputElement
      const nameInput = (await screen.findByPlaceholderText('如 SharedDocs')) as HTMLInputElement

      // 打开即有可用值：建议根来自 api.disk.suggestRoot()
      await waitFor(() => expect(pathInput.value).toBe('D:\\share\\suggested'))
      // 路径带出共享名（末级目录）
      await waitFor(() => expect(nameInput.value).toBe('suggested'))

      // 用户手改名字后，路径再变化也不得覆盖
      fireEvent.change(nameInput, { target: { value: 'MyDocs' } })
      fireEvent.change(pathInput, { target: { value: 'E:\\Projects\\Docs' } })
      await waitFor(() => expect(pathInput.value).toBe('E:\\Projects\\Docs'))
      expect(nameInput.value).toBe('MyDocs')

      // 默认授权档：Users 读取（折叠面板展开可见）
      fireEvent.click(screen.getByText('访问控制（可选，不选则使用默认权限）'))
      const chips = await screen.findAllByText(/Users/)
      expect(chips.length).toBeGreaterThan(0)

      fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))
      await waitFor(() => expect(stub.adapter.create).toHaveBeenCalledTimes(1))
      const input = stub.adapter.create.mock.calls[0][0] as CreateShareInput
      expect(input.name).toBe('MyDocs')
      expect(input.path).toBe('E:\\Projects\\Docs')
      expect(input.readAccess).toEqual(['Users'])
      expect(input.protocol).toBe('smb')
    },
  )

  it('suggestRoot 失败时不阻断新建（路径留空由用户填写）', UI_TIMEOUT, async () => {
    stub.disk.suggestRoot.mockRejectedValue(new Error('查盘失败'))
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: /新建共享/ }))
    const pathInput = (await screen.findByPlaceholderText('如 D:\\Share')) as HTMLInputElement
    await new Promise((r) => setTimeout(r, 20))
    expect(pathInput.value).toBe('')
  })
})

describe('Shares · 新手/专家双模式', () => {
  const ADVANCED_LABELS = [
    '启用 SMB 加密',
    '高级选项',
    '共享级数据加密',
    '卷影副本',
    '文件夹枚举模式',
    '脱机缓存模式',
    '并发用户上限（0=无限制）',
    'BranchCache',
    '基于访问（仅可见有权限的子项）',
    // SMB1/租约类文案随高级项一起收敛
    '私有加密',
    '仅管理员 + 传输加密 + 按访问枚举',
  ]

  it('advancedMode=false 隐藏高级项及其文案，提交时也不写入隐藏参数', UI_TIMEOUT, async () => {
    useAppStore.setState({ state: appState({ advancedMode: false }) })
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: /新建共享/ }))
    await screen.findByPlaceholderText('如 SharedDocs')

    for (const label of ADVANCED_LABELS) {
      expect(screen.queryByText(label)).toBeNull()
    }

    await waitFor(() =>
      expect((screen.getByPlaceholderText('如 D:\\Share') as HTMLInputElement).value).toBe(
        'D:\\share\\suggested',
      ),
    )
    fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))
    await waitFor(() => expect(stub.adapter.create).toHaveBeenCalledTimes(1))
    const input = stub.adapter.create.mock.calls[0][0] as CreateShareInput
    // 隐藏项静默入库 = 替用户做主，必须不提交
    expect(input.encrypted).toBeUndefined()
    expect(input.encryptData).toBeUndefined()
    expect(input.cachingMode).toBeUndefined()
    expect(input.folderEnumerationMode).toBeUndefined()
    expect(input.concurrentUserLimit).toBeUndefined()
    expect(input.shareShadowCopy).toBeUndefined()
    // 核心项仍在：默认授权 Users 读取
    expect(input.readAccess).toEqual(['Users'])
  })

  it('专家模式高级项与迁移前一致（SMB 加密 + 高级选项面板可展开取值）', UI_TIMEOUT, async () => {
    renderPage()
    fireEvent.click(screen.getByRole('button', { name: /新建共享/ }))
    await screen.findByText('启用 SMB 加密')
    expect(screen.getByText('私有加密')).toBeInTheDocument()

    fireEvent.click(screen.getByText('高级选项'))
    expect(await screen.findByText('脱机缓存模式')).toBeInTheDocument()
    expect(screen.getByText('文件夹枚举模式')).toBeInTheDocument()
    expect(screen.getByText('并发用户上限（0=无限制）')).toBeInTheDocument()
    expect(screen.getByText('共享级数据加密')).toBeInTheDocument()
    expect(screen.getByText('卷影副本')).toBeInTheDocument()

    // 高级项取值确实进入 payload（回归：不能被双模式改造吃掉）
    fireEvent.click(screen.getByRole('button', { name: /创\s*建/ }))
    await waitFor(() => expect(stub.adapter.create).toHaveBeenCalledTimes(1))
    const input = stub.adapter.create.mock.calls[0][0] as CreateShareInput
    expect(input.encrypted).toBe(false)
    expect(input.cachingMode).toBe('Manual')
    expect(input.folderEnumerationMode).toBe('Unrestricted')
    expect(input.concurrentUserLimit).toBe(0)
    expect(input.encryptData).toBe(false)
    expect(input.shareShadowCopy).toBe(false)
  })

  it('编辑表单同步收敛：新手模式不出现 root 访问等高级项', UI_TIMEOUT, async () => {
    stub.adapter.list.mockImplementation(async () => [
      share('nfsro', 'E:\\nfs', {
        protocol: 'nfs',
        nfsPermission: 'ro',
        authentication: ['sys'],
        allowRootAccess: true,
      }),
    ])
    useAppStore.setState({ state: appState({ advancedMode: false }) })
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '编辑 nfsro' }))
    await screen.findByText('NFS 权限')
    expect(screen.queryByText('允许 root 访问')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }))
    await waitFor(() => expect(stub.adapter.update).toHaveBeenCalledTimes(1))
    const input = stub.adapter.update.mock.calls[0][1] as { allowRootAccess?: boolean }
    expect(input.allowRootAccess).toBeUndefined()
  })
})

describe('Shares · 删除确认文案', () => {
  it('单行删除确认给出连接影响 + 可撤销说明，不再出现"不可恢复"', UI_TIMEOUT, async () => {
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: '删除 lowish' }))

    expect(await screen.findByText(/4 个连接正在使用共享：lowish\(4\)/)).toBeInTheDocument()
    expect(screen.getByText('删除后可在「操作回收站」一键撤销。')).toBeInTheDocument()
    expect(screen.queryByText(/不可恢复/)).toBeNull()
  })

  it('批量删除（Del hotkey 路径）同样走连接影响与撤销说明', UI_TIMEOUT, async () => {
    renderPage()
    await screen.findByText('lowish')
    useUiStore.setState({ selectedShares: ['smb:lowish', 'smb:urgent'] })
    useUiStore.getState().requestShareDelete()

    expect(await screen.findByText(/当前 4 个连接正在使用共享：lowish\(4\)/)).toBeInTheDocument()
    expect(screen.getByText('2 个 SMB 共享删除后均可在「操作回收站」撤销。')).toBeInTheDocument()
    expect(screen.queryByText(/不可恢复/)).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: /取\s*消/ }))
  })
})

describe('Shares · 批1 集成挂载位', () => {
  it('回收站/诊断入口可由外部 props 注入（本页不实现其能力）', UI_TIMEOUT, async () => {
    renderPage({
      toolbarSlots: <button type="button">操作回收站</button>,
      rowActions: (s) => <button type="button">{`诊断 ${s.name}`}</button>,
    })
    await screen.findByText('lowish')
    expect(screen.getByRole('button', { name: '操作回收站' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '诊断 lowish' })).toBeInTheDocument()
  })
})

// 批2（docs/audit/05-renderer-debt.md §3.4）：页面级挂载不回归——抽屉的取数生命周期
// 由组件自己（渲染期调整 + effect 内 loader）保证，页面无需任何 key/props 变化。
describe('Shares · 权限抽屉（t3 迁移的页面级回归）', () => {
  const CAPS = {
    smb: {
      supportsCreate: true,
      supportsUpdate: true,
      supportsDelete: true,
      supportsToggle: true,
      supportsPermissions: true,
      supportsSessions: true,
      supportsOpenFiles: true,
      supportsServerConfig: true,
      supportsRestart: true,
      permissionModel: 'smb-acl',
    },
    nfs: null,
    ftp: null,
    webdav: null,
  }

  const permBtn = (rowKey: string) => {
    const row = document.querySelector(`tr[data-row-key="${rowKey}"]`) as HTMLElement
    return row.querySelector('.anticon-safety')!.closest('button') as HTMLElement
  }
  const drawerTableText = () =>
    Array.from(document.querySelectorAll('.ant-drawer .ant-table-tbody tr[data-row-key]'))
      .map((r) => (r.textContent || '').trim())
      .join(' | ')

  it(
    '打开→关闭→换另一共享打开：每次打开恰好一次 permissions，行以新共享为准',
    UI_TIMEOUT,
    async () => {
      stub.adapter.capabilities.mockResolvedValue(CAPS)
      stub.share.permissions.mockImplementation(async (name: string) => [
        {
          shareName: name,
          account: name === 'urgent' ? 'alice' : 'bob',
          accountType: 'User',
          access: 'Read',
          deny: false,
        },
      ])
      renderPage()
      await screen.findByText('urgent')

      fireEvent.click(permBtn('smb:urgent'))
      await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledWith('urgent'))
      expect(await screen.findByText('权限管理：urgent')).toBeInTheDocument()
      await waitFor(() => expect(drawerTableText()).toContain('alice'))
      expect(stub.share.permissions).toHaveBeenCalledTimes(1)

      // 关闭（抽屉 destroyOnClose：内容卸载，组件自身状态留给下一次打开重置）
      fireEvent.click(document.querySelector('.ant-drawer-close') as HTMLElement)
      await waitFor(() => expect(document.querySelector('.ant-drawer-open')).toBeNull())

      // 换另一共享：新目标数据最终胜出，且仍恰好一次取数
      fireEvent.click(permBtn('smb:lowish'))
      await waitFor(() => expect(stub.share.permissions).toHaveBeenCalledWith('lowish'))
      expect(await screen.findByText('权限管理：lowish')).toBeInTheDocument()
      await waitFor(() => expect(drawerTableText()).toContain('bob'))
      expect(drawerTableText()).not.toContain('alice')
      expect(stub.share.permissions).toHaveBeenCalledTimes(2)
    },
  )
})
