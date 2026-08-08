import { useEffect, useMemo, useState } from 'react'
import {
  Table,
  Button,
  Modal,
  Form,
  Input,
  InputNumber,
  Select,
  Switch,
  Tag,
  Space,
  Popconfirm,
  App,
  Tooltip,
  Upload,
  Tabs,
  Checkbox,
  Radio,
  Empty,
  Badge,
  Alert,
  Collapse
} from 'antd'
import {
  PlusOutlined,
  ReloadOutlined,
  DeleteOutlined,
  ExportOutlined,
  ImportOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  EditOutlined,
  SearchOutlined,
  SafetyOutlined,
  InfoCircleOutlined
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import type { Share, PermissionPreset, Protocol, ProtocolCapabilities, LocalUser, LocalGroup } from '../types'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import PermissionDrawer from '../components/PermissionDrawer'
import ProtocolCapabilityBanner from '../components/ProtocolCapabilityBanner'
import ShareDetailDrawer from '../components/ShareDetailDrawer'

// 协议标签颜色
const PROTOCOL_COLOR: Record<string, string> = {
  smb: 'blue',
  nfs: 'purple',
  ftp: 'green',
  webdav: 'orange'
}

// 复合 key 工具：${protocol}:${name}
function toKey(s: Share): string {
  return `${s.protocol}:${s.name}`
}
function parseKey(key: string): { protocol: Protocol; name: string } {
  const idx = key.indexOf(':')
  if (idx < 0) return { protocol: 'smb', name: key }
  return { protocol: key.slice(0, idx) as Protocol, name: key.slice(idx + 1) }
}

export default function Shares() {
  const { message, modal } = App.useApp()
  const [shares, setShares] = useState<Share[]>([])
  const [presets, setPresets] = useState<PermissionPreset[]>([])
  const [users, setUsers] = useState<LocalUser[]>([])
  const [groups, setGroups] = useState<LocalGroup[]>([])
  const [caps, setCaps] = useState<Record<Protocol, ProtocolCapabilities | null>>({
    smb: null,
    nfs: null,
    ftp: null,
    webdav: null
  })
  const [loading, setLoading] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [editOpen, setEditOpen] = useState(false)
  const [editShare, setEditShare] = useState<Share | null>(null)
  const [permOpen, setPermOpen] = useState(false)
  const [permShare, setPermShare] = useState<Share | null>(null)
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailShare, setDetailShare] = useState<Share | null>(null)
  const [keyword, setKeyword] = useState('')
  const [form] = Form.useForm()
  const [editForm] = Form.useForm()

  // store 订阅
  const selectedShares = useUiStore((s) => s.selectedShares)
  const setSelectedShares = useUiStore((s) => s.setSelectedShares)
  const shareCreateOpen = useUiStore((s) => s.shareCreateOpen)
  const setShareCreateOpen = useUiStore((s) => s.setShareCreateOpen)
  const refreshTick = useUiStore((s) => s.refreshTick)
  const shareDeleteTick = useUiStore((s) => s.shareDeleteTick)
  const shareToggleTick = useUiStore((s) => s.shareToggleTick)
  const activeProtocol = useUiStore((s) => s.activeProtocol)
  const setActiveProtocol = useUiStore((s) => s.setActiveProtocol)
  const protocolCaps = useUiStore((s) => s.protocolCaps)
  const setProtocolCaps = useUiStore((s) => s.setProtocolCaps)
  const [installingProto, setInstallingProto] = useState<Protocol | null>(null)

  const load = async () => {
    setLoading(true)
    try {
      const proto = activeProtocol === 'all' ? undefined : activeProtocol
      const [s, p, c, u, g] = await Promise.all([
        call(() => api.adapter.list(proto)),
        call(api.preset.list),
        call(api.adapter.capabilities),
        call(api.user.list).catch(() => [] as LocalUser[]),
        call(api.user.groups).catch(() => [] as LocalGroup[])
      ])
      setShares(s)
      setPresets(p)
      setCaps(c)
      setUsers(u)
      setGroups(g)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeProtocol])

  // 安装协议
  const handleInstall = async (proto: Protocol) => {
    setInstallingProto(proto)
    try {
      await call(() => api.protocol.install(proto))
      message.success('安装成功，可能需要重启系统')
      const result = await call(api.protocol.detect)
      setProtocolCaps(result)
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setInstallingProto(null)
    }
  }

  // 判断当前选中协议是否已安装
  const isCurrentProtoInstalled = (): boolean => {
    if (activeProtocol === 'all') return true
    const cap = protocolCaps?.[activeProtocol as Protocol]
    // cap 未加载时默认视为已安装（避免误显安装引导）
    return !cap || cap.installed
  }

  // hotkey: Ctrl+N
  useEffect(() => {
    if (shareCreateOpen) {
      setModalOpen(true)
      setShareCreateOpen(false)
    }
  }, [shareCreateOpen, setShareCreateOpen])

  useTickEffect(refreshTick, () => load())

  // hotkey: Del 批量删除
  useTickEffect(shareDeleteTick, () => {
    if (!selectedShares.length) return
    modal.confirm({
      title: '批量删除共享',
      content: `将对 ${selectedShares.length} 个共享执行删除，不可恢复。`,
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        const results = await Promise.allSettled(
          selectedShares.map((key) => {
            const { protocol, name } = parseKey(key)
            return api.adapter.delete(protocol, name)
          })
        )
        const failed = results.filter((r) => r.status === 'rejected')
        if (failed.length) {
          message.error(`${selectedShares.length - failed.length} 个成功，${failed.length} 个失败`)
        } else {
          message.success(`已删除 ${selectedShares.length} 个共享`)
        }
        setSelectedShares([])
        load()
      }
    })
  })

  // hotkey: Space 批量启停
  useTickEffect(shareToggleTick, () => {
    if (!selectedShares.length) return
    modal.confirm({
      title: '批量启停切换',
      content: `将对 ${selectedShares.length} 个共享切换启用/禁用状态。`,
      okText: '切换',
      cancelText: '取消',
      onOk: async () => {
        const map = new Map(shares.map((s) => [toKey(s), s]))
        const results = await Promise.allSettled(
          selectedShares.map((key) => {
            const { protocol, name } = parseKey(key)
            const cur = map.get(key)
            if (!cur) return Promise.reject(new Error('not found'))
            return api.adapter.toggle(protocol, name, cur.status !== 'Enabled')
          })
        )
        const failed = results.filter((r) => r.status === 'rejected')
        if (failed.length) {
          message.error(`${selectedShares.length - failed.length} 个成功，${failed.length} 个失败`)
        } else {
          message.success(`已切换 ${selectedShares.length} 个共享`)
        }
        setSelectedShares([])
        load()
      }
    })
  })

  const handleCreate = async () => {
    const v = await form.validateFields()
    try {
      const protocol = (v.protocol || 'smb') as Protocol
      await call(() =>
        api.adapter.create({
          protocol,
          name: v.name,
          path: v.path,
          description: v.description,
          // SMB
          encrypted: v.encrypted,
          fullAccess: v.fullAccess,
          changeAccess: v.changeAccess,
          readAccess: v.readAccess,
          noAccess: v.noAccess,
          encryptData: v.encryptData,
          concurrentUserLimit: v.concurrentUserLimit,
          cachingMode: v.cachingMode,
          folderEnumerationMode: v.folderEnumerationMode,
          shareShadowCopy: v.shareShadowCopy,
          // NFS
          authentication: v.authentication,
          nfsPermission: v.nfsPermission,
          allowRootAccess: v.allowRootAccess,
          enableUnmappedAccess: v.enableUnmappedAccess,
          anonymousUid: v.anonymousUid,
          anonymousGid: v.anonymousGid,
          // FTP
          port: v.port,
          sslPolicy: v.sslPolicy,
          authMode: v.authMode,
          // WebDAV
          anonymousEnabled: v.anonymousEnabled
        })
      )
      if (v.presetId && protocol === 'smb') {
        const created = await call(() => api.share.get(v.name))
        await call(() => api.preset.apply(created.name, v.presetId, 'overwrite'))
      }
      message.success('创建成功')
      setModalOpen(false)
      form.resetFields()
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleEdit = async () => {
    if (!editShare) return
    const v = await editForm.validateFields()
    try {
      await call(() =>
        api.adapter.update(editShare.name, {
          protocol: editShare.protocol,
          description: v.description,
          // NFS
          nfsPermission: v.nfsPermission,
          allowRootAccess: v.allowRootAccess,
          // FTP
          sslPolicy: v.sslPolicy,
          authMode: v.authMode,
          // WebDAV
          anonymousEnabled: v.anonymousEnabled
        })
      )
      message.success('已保存')
      setEditOpen(false)
      setEditShare(null)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleDelete = async (r: Share) => {
    try {
      await call(() => api.adapter.delete(r.protocol, r.name))
      message.success('已删除')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleToggle = async (r: Share) => {
    try {
      await call(() => api.adapter.toggle(r.protocol, r.name, r.status !== 'Enabled'))
      message.success(r.status === 'Enabled' ? '已禁用' : '已启用')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleExport = async () => {
    try {
      const json = await call(api.share.exportConfig)
      const blob = new Blob([json], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `winshare-export-${Date.now()}.json`
      a.click()
      URL.revokeObjectURL(url)
      message.success('已导出（仅 SMB）')
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const importProps: UploadProps = {
    beforeUpload: async (file) => {
      try {
        const text = await file.text()
        const result = await call(() => api.share.importConfig(text))
        // 显示导入统计：成功数 + 跳过数（含非法条目与失败原因）
        if (result.skipped > 0) {
          message.warning(
            `导入完成：成功 ${result.imported} 个，跳过 ${result.skipped} 个。${result.errors.slice(0, 2).join('；')}${result.errors.length > 2 ? ' 等' : ''}`
          )
        } else {
          message.success(`导入完成：成功 ${result.imported} 个共享（仅 SMB）`)
        }
        load()
      } catch (e) {
        message.error((e as Error).message)
      }
      return false
    },
    showUploadList: false,
    accept: '.json'
  }

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    const f = e.dataTransfer.files[0]
    if (f) {
      const path = (f as File & { path?: string }).path
      if (path) {
        form.setFieldsValue({ path })
        setShareCreateOpen(true)
      }
    }
  }

  // SMB 访问控制选项：本地用户 + 本地组
  const accountOptions = useMemo(() => {
    const userOpts = users.map((u) => ({ label: u.name, value: u.name, isGroup: false }))
    const groupOpts = groups.map((g) => ({ label: g.name, value: g.name, isGroup: true }))
    return [...userOpts, ...groupOpts]
  }, [users, groups])

  const filtered = useMemo(() => {
    const q = keyword.trim().toLowerCase()
    if (!q) return shares
    return shares.filter(
      (s) =>
        s.name.toLowerCase().includes(q) ||
        s.path.toLowerCase().includes(q) ||
        (s.description || '').toLowerCase().includes(q)
    )
  }, [shares, keyword])

  // 协议能力位
  const capOf = (p: Protocol): ProtocolCapabilities | null => caps[p]
  const canToggle = (p: Protocol) => capOf(p)?.supportsToggle ?? false

  const columns = [
    ...(activeProtocol === 'all'
      ? [
          {
            key: 'protocol',
            title: '协议',
            dataIndex: 'protocol',
            width: 80,
            render: (p: Protocol) => <Tag color={PROTOCOL_COLOR[p]}>{p.toUpperCase()}</Tag>
          }
        ]
      : []),
    { key: 'name', title: '名称', dataIndex: 'name', width: 160 },
    { key: 'path', title: '路径', dataIndex: 'path', width: 160, ellipsis: true },
    { key: 'description', title: '描述', dataIndex: 'description', width: 120, ellipsis: true },
    // FTP 专有列（前置到路径/描述之后、连接数之前，便于 FTP 管理）
    ...(activeProtocol === 'ftp'
      ? [
          {
            key: 'ftp-port',
            title: '端口',
            dataIndex: 'port',
            width: 70,
            render: (v: number | undefined) => v ?? <span className="text-fog">-</span>
          },
          {
            key: 'ftp-ssl',
            title: 'SSL',
            dataIndex: 'sslPolicy',
            width: 130,
            render: (v: string | undefined) =>
              v ? (
                <Tag color={v === 'SslRequire' || v === 'SslRequireCredentials' ? 'green' : 'default'}>{v}</Tag>
              ) : (
                <span className="text-fog">-</span>
              )
          },
          {
            key: 'ftp-auth',
            title: '认证',
            dataIndex: 'authMode',
            width: 90,
            render: (v: string | undefined) =>
              v ? <Tag color={v === 'anonymous' ? 'orange' : 'blue'}>{v}</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    ...(activeProtocol === 'all' || activeProtocol === 'nfs'
      ? [
          {
            key: 'nfs-auth',
            title: '认证',
            dataIndex: 'authentication',
            width: 100,
            render: (v: string[] | undefined) =>
              v && v.length ? (
                <Space size={0} wrap>
                  {v.map((a) => (
                    <Tag key={a}>{a}</Tag>
                  ))}
                </Space>
              ) : (
                <span className="text-fog">-</span>
              )
          },
          {
            key: 'nfs-perm',
            title: 'NFS权限',
            dataIndex: 'nfsPermission',
            width: 90,
            render: (v: string | undefined) =>
              v ? <Tag color={v === 'rw' ? 'green' : 'default'}>{v}</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    { key: 'concurrentUsers', title: '连接数', dataIndex: 'concurrentUsers', width: 80 },
    // WebDAV 专有列
    ...(activeProtocol === 'webdav'
      ? [
          {
            key: 'webdav-port',
            title: '端口',
            dataIndex: 'port',
            width: 70,
            render: (v: number | undefined) => v ?? <span className="text-fog">-</span>
          },
          {
            key: 'webdav-anon',
            title: '匿名',
            dataIndex: 'anonymousEnabled',
            width: 70,
            render: (v: boolean | undefined) =>
              v ? <Tag color="orange">是</Tag> : <span className="text-fog">-</span>
          },
          {
            key: 'webdav-authoring',
            title: '作者',
            dataIndex: 'authoringEnabled',
            width: 70,
            render: (v: boolean | undefined) =>
              v ? <Tag color="green">启用</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    ...(activeProtocol === 'all' || activeProtocol === 'smb'
      ? [
          {
            key: 'encrypted',
            title: '加密',
            dataIndex: 'encrypted',
            width: 70,
            render: (v: boolean | undefined, r: Share) =>
              r.protocol === 'smb' && v ? <Tag color="blue">是</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    {
      key: 'actions',
      title: '操作',
      width: 220,
      render: (_: unknown, r: Share) => (
        <Space>
          <Tooltip title="详情">
            <Button
              size="small"
              icon={<InfoCircleOutlined />}
              onClick={() => {
                setDetailShare(r)
                setDetailOpen(true)
              }}
            />
          </Tooltip>
          {canToggle(r.protocol) && (
            <Tooltip title={r.status === 'Enabled' ? '禁用' : '启用'}>
              <Button
                size="small"
                icon={r.status === 'Enabled' ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
                onClick={() => handleToggle(r)}
              />
            </Tooltip>
          )}
          <Tooltip title="编辑">
            <Button
              size="small"
              icon={<EditOutlined />}
              onClick={() => {
                setEditShare(r)
                editForm.setFieldsValue({
                  description: r.description,
                  nfsPermission: r.nfsPermission,
                  allowRootAccess: r.allowRootAccess,
                  sslPolicy: r.sslPolicy,
                  authMode: r.authMode,
                  anonymousEnabled: r.anonymousEnabled
                })
                setEditOpen(true)
              }}
            />
          </Tooltip>
          {capOf(r.protocol)?.supportsPermissions && (
            <Tooltip title="权限">
              <Button
                size="small"
                icon={<SafetyOutlined />}
                onClick={() => {
                  setPermShare(r)
                  setPermOpen(true)
                }}
              />
            </Tooltip>
          )}
          <Popconfirm title="确认删除该共享？" onConfirm={() => handleDelete(r)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      )
    }
  ]

  // 协议 Tabs
  const protocolTabs = [
    { key: 'all', label: '全部' },
    { key: 'smb', label: 'SMB' },
    {
      key: 'nfs',
      label: protocolCaps && !protocolCaps.nfs?.installed
        ? <Badge dot status="warning" offset={[2, 0]}>NFS</Badge>
        : 'NFS'
    },
    {
      key: 'ftp',
      label: protocolCaps && !protocolCaps.ftp?.installed
        ? <Badge dot status="warning" offset={[2, 0]}>FTP</Badge>
        : 'FTP'
    },
    {
      key: 'webdav',
      label: protocolCaps && !protocolCaps.webdav?.installed
        ? <Badge dot status="warning" offset={[2, 0]}>WebDAV</Badge>
        : 'WebDAV'
    }
  ]

  return (
    <div onDrop={handleDrop} onDragOver={(e) => e.preventDefault()}>
      <div className="flex items-center justify-between mb-4">
        <div>
          <h1 className="text-xl font-semibold">共享管理</h1>
          <p className="text-xs text-fog mt-1">
            提示：拖拽文件夹到本页可快速创建共享 · Ctrl+N 新建 · Del 批量删除 · Space 批量启停 · F5 刷新
          </p>
        </div>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
          <Button icon={<ExportOutlined />} onClick={handleExport}>
            导出
          </Button>
          <Upload {...importProps}>
            <Button icon={<ImportOutlined />}>导入</Button>
          </Upload>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => setShareCreateOpen(true)}>
            新建共享
          </Button>
        </Space>
      </div>
      <ProtocolCapabilityBanner />
      <div className="glass-card p-3">
        <Tabs
          activeKey={activeProtocol}
          onChange={(k) => setActiveProtocol(k as Protocol | 'all')}
          items={protocolTabs}
          size="small"
          className="mb-3"
        />
        <div className="mb-3 flex items-center gap-2">
          <Input
            allowClear
            prefix={<SearchOutlined className="text-fog" />}
            placeholder="搜索名称 / 路径 / 描述"
            value={keyword}
            onChange={(e) => setKeyword(e.target.value)}
            style={{ maxWidth: 320 }}
          />
          {selectedShares.length > 0 && (
            <Space className="ml-auto">
              <span className="text-xs text-fog">已选 {selectedShares.length}</span>
              <Button onClick={() => setSelectedShares([])}>清空</Button>
              <Popconfirm
                title={`批量切换 ${selectedShares.length} 个共享的启用状态？`}
                onConfirm={() => useUiStore.getState().requestShareToggle()}
              >
                <Button>批量启停</Button>
              </Popconfirm>
              <Button
                danger
                onClick={() =>
                  modal.confirm({
                    title: '批量删除共享',
                    content: `将对 ${selectedShares.length} 个共享执行删除，不可恢复。`,
                    okText: '删除',
                    okType: 'danger',
                    cancelText: '取消',
                    onOk: async () => {
                      const results = await Promise.allSettled(
                        selectedShares.map((key) => {
                          const { protocol, name } = parseKey(key)
                          return api.adapter.delete(protocol, name)
                        })
                      )
                      const failed = results.filter((r) => r.status === 'rejected')
                      if (failed.length) {
                        message.error(
                          `${selectedShares.length - failed.length} 个成功，${failed.length} 个失败`
                        )
                      } else {
                        message.success(`已删除 ${selectedShares.length} 个共享`)
                      }
                      setSelectedShares([])
                      load()
                    }
                  })
                }
              >
                批量删除
              </Button>
            </Space>
          )}
        </div>
        {isCurrentProtoInstalled() ? (
          <Table
            dataSource={filtered}
            columns={columns}
            rowKey={toKey}
            loading={loading}
            pagination={{ pageSize: 10 }}
            size="middle"
            rowSelection={{
              selectedRowKeys: selectedShares,
              onChange: (keys) => setSelectedShares(keys as string[])
            }}
          />
        ) : (
          <Empty
            className="py-16"
            description={
              <div className="text-center">
                <p className="text-base font-medium mb-2">
                  {(activeProtocol as string).toUpperCase()} 协议未安装
                </p>
                <p className="text-sm text-fog mb-2">
                  {protocolCaps?.[activeProtocol as Protocol]?.installHint}
                </p>
                {(() => {
                  const cap = protocolCaps?.[activeProtocol as Protocol]
                  if (!cap || cap.installed || cap.installType !== 'iis-role') return null
                  const hint = cap.installHint || ''
                  const tags: { label: string; color: string }[] = []
                  if (/IIS.*已安装|IIS 基础.*已安装/.test(hint)) {
                    tags.push({ label: 'IIS 已装', color: 'green' })
                    tags.push({ label: `${(activeProtocol as string).toUpperCase()} 角色未装`, color: 'orange' })
                  } else if (/IIS.*未安装|IIS.*均未安装/.test(hint)) {
                    tags.push({ label: 'IIS 未装', color: 'red' })
                  }
                  if (/服务未运行|ftpsvc.*未运行|W3SVC.*未运行/.test(hint)) {
                    tags.push({ label: '服务已停', color: 'volcano' })
                  }
                  if (tags.length === 0) return null
                  return (
                    <Space size={4} wrap className="mb-4 justify-center">
                      {tags.map((t, i) => (
                        <Tag key={i} color={t.color}>{t.label}</Tag>
                      ))}
                    </Space>
                  )
                })()}
                <Button
                  type="primary"
                  className="mt-2"
                  loading={installingProto === activeProtocol}
                  onClick={() => handleInstall(activeProtocol as Protocol)}
                >
                  一键安装
                </Button>
              </div>
            }
          />
        )}
      </div>
      <Modal
        open={modalOpen}
        title="新建共享"
        onCancel={() => setModalOpen(false)}
        onOk={handleCreate}
        okText="创建"
        cancelText="取消"
        width={520}
      >
        <Form form={form} layout="vertical" initialValues={{ protocol: 'smb', nfsPermission: 'rw' }}>
          <Form.Item name="protocol" label="协议" rules={[{ required: true }]}>
            <Select
              onChange={(v) => {
                if (v !== 'smb') form.setFieldValue('presetId', undefined)
              }}
            >
              <Select.Option value="smb">SMB（Windows 文件共享）</Select.Option>
              <Select.Option value="nfs">NFS（网络文件系统）</Select.Option>
              <Select.Option value="ftp">FTP（IIS FTP 站点）</Select.Option>
              <Select.Option value="webdav">WebDAV（IIS WebDAV 站点）</Select.Option>
            </Select>
          </Form.Item>
          <Form.Item name="name" label="共享名" rules={[{ required: true, message: '请输入共享名' }]}>
            <Input placeholder="如 SharedDocs" />
          </Form.Item>
          <Form.Item name="path" label="本地路径" rules={[{ required: true, message: '请输入或拖入路径' }]}>
            <Input placeholder="如 D:\Share" />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input />
          </Form.Item>
          {/* SMB 专有字段 */}
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.protocol !== cur.protocol}>
            {({ getFieldValue }) =>
              getFieldValue('protocol') === 'smb' ? (
                <>
                  <Form.Item name="presetId" label="权限模板（可选）">
                    <Select allowClear placeholder="选择模板一键应用权限">
                      {presets.map((p) => (
                        <Select.Option key={p.id} value={p.id}>
                          {p.name} - {p.description}
                        </Select.Option>
                      ))}
                    </Select>
                  </Form.Item>
                  <Form.Item name="encrypted" label="启用 SMB 加密" valuePropName="checked" initialValue={false}>
                    <Switch />
                  </Form.Item>
                  <Collapse
                    size="small"
                    className="mb-3"
                    items={[
                      {
                        key: 'access',
                        label: '访问控制（可选，不选则使用默认权限）',
                        children: (
                          <>
                            <Form.Item name="fullAccess" label="完全控制">
                              <Select
                                mode="multiple"
                                allowClear
                                placeholder="选择用户/组授予完全控制"
                                optionFilterProp="label"
                                options={accountOptions.map((o) => ({
                                  label: o.isGroup ? `${o.label}（组）` : o.label,
                                  value: o.value
                                }))}
                              />
                            </Form.Item>
                            <Form.Item name="changeAccess" label="更改">
                              <Select
                                mode="multiple"
                                allowClear
                                placeholder="选择用户/组授予更改权限"
                                optionFilterProp="label"
                                options={accountOptions.map((o) => ({
                                  label: o.isGroup ? `${o.label}（组）` : o.label,
                                  value: o.value
                                }))}
                              />
                            </Form.Item>
                            <Form.Item name="readAccess" label="读取">
                              <Select
                                mode="multiple"
                                allowClear
                                placeholder="选择用户/组授予读取权限"
                                optionFilterProp="label"
                                options={accountOptions.map((o) => ({
                                  label: o.isGroup ? `${o.label}（组）` : o.label,
                                  value: o.value
                                }))}
                              />
                            </Form.Item>
                          </>
                        )
                      }
                    ]}
                  />
                  <Collapse
                    size="small"
                    className="mb-3"
                    items={[
                      {
                        key: 'advanced',
                        label: '高级选项',
                        children: (
                          <>
                            <Form.Item
                              name="encryptData"
                              label="共享级数据加密"
                              valuePropName="checked"
                              initialValue={false}
                              tooltip="对通过此共享传输的数据进行加密，与 SMB 加密独立"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="shareShadowCopy"
                              label="卷影副本"
                              valuePropName="checked"
                              initialValue={false}
                              tooltip="启用 VSS 卷影副本支持，允许客户端访问历史版本"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item name="folderEnumerationMode" label="文件夹枚举模式" initialValue="Unrestricted">
                              <Select
                                options={[
                                  { label: '无限制（可见全部子项）', value: 'Unrestricted' },
                                  { label: '基于访问（仅可见有权限的子项）', value: 'AccessBased' }
                                ]}
                              />
                            </Form.Item>
                            <Form.Item name="cachingMode" label="脱机缓存模式" initialValue="Manual">
                              <Select
                                options={[
                                  { label: '无', value: 'None' },
                                  { label: '手动', value: 'Manual' },
                                  { label: '文档', value: 'Documents' },
                                  { label: '程序', value: 'Programs' },
                                  { label: 'BranchCache', value: 'BranchCache' }
                                ]}
                              />
                            </Form.Item>
                            <Form.Item name="concurrentUserLimit" label="并发用户上限（0=无限制）" initialValue={0}>
                              <InputNumber min={0} max={65535} style={{ width: '100%' }} />
                            </Form.Item>
                          </>
                        )
                      }
                    ]}
                  />
                </>
              ) : null
            }
          </Form.Item>
          {/* NFS 专有字段 */}
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.protocol !== cur.protocol}>
            {({ getFieldValue }) =>
              getFieldValue('protocol') === 'nfs' ? (
                <>
                  <Form.Item name="authentication" label="认证方式">
                    <Checkbox.Group
                      options={[
                        { label: 'Krb5', value: 'krb5' },
                        { label: 'Krb5i', value: 'krb5i' },
                        { label: 'Krb5p', value: 'krb5p' },
                        { label: 'AUTH_SYS', value: 'sys' }
                      ]}
                    />
                  </Form.Item>
                  <Form.Item name="nfsPermission" label="默认访问权限">
                    <Radio.Group>
                      <Radio value="rw">读写</Radio>
                      <Radio value="ro">只读</Radio>
                    </Radio.Group>
                  </Form.Item>
                  <Form.Item
                    name="enableUnmappedAccess"
                    label="启用未映射用户访问"
                    valuePropName="checked"
                    initialValue={false}
                  >
                    <Switch />
                  </Form.Item>
                  <Form.Item
                    name="allowRootAccess"
                    label="允许 root 访问"
                    valuePropName="checked"
                    initialValue={false}
                  >
                    <Switch />
                  </Form.Item>
                  <Form.Item name="anonymousUid" label="匿名 UID（0=默认）" initialValue={0}>
                    <InputNumber min={-1} max={65535} style={{ width: '100%' }} />
                  </Form.Item>
                  <Form.Item name="anonymousGid" label="匿名 GID（0=默认）" initialValue={0}>
                    <InputNumber min={-1} max={65535} style={{ width: '100%' }} />
                  </Form.Item>
                </>
              ) : null
            }
          </Form.Item>
          {/* FTP 专有字段 */}
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.protocol !== cur.protocol}>
            {({ getFieldValue }) =>
              getFieldValue('protocol') === 'ftp' ? (
                <>
                  <Form.Item name="port" label="端口" initialValue={21}>
                    <InputNumber min={1} max={65535} style={{ width: 120 }} />
                  </Form.Item>
                  <Form.Item name="sslPolicy" label="SSL 策略" initialValue="SslAllow">
                    <Radio.Group>
                      <Radio value="SslAllow">允许</Radio>
                      <Radio value="SslRequire">要求</Radio>
                      <Radio value="SslRequireCredentials">要求凭据</Radio>
                    </Radio.Group>
                  </Form.Item>
                  <Form.Item name="authMode" label="认证模式" initialValue="basic">
                    <Radio.Group>
                      <Radio value="anonymous">匿名</Radio>
                      <Radio value="basic">基本</Radio>
                      <Radio value="windows">Windows</Radio>
                    </Radio.Group>
                  </Form.Item>
                </>
              ) : null
            }
          </Form.Item>
          {/* WebDAV 专有字段 */}
          <Form.Item noStyle shouldUpdate={(prev, cur) => prev.protocol !== cur.protocol}>
            {({ getFieldValue }) =>
              getFieldValue('protocol') === 'webdav' ? (
                <>
                  <Form.Item name="port" label="端口" initialValue={80}>
                    <InputNumber min={1} max={65535} style={{ width: 120 }} />
                  </Form.Item>
                  <Form.Item
                    name="anonymousEnabled"
                    label="启用匿名访问"
                    valuePropName="checked"
                    initialValue={false}
                  >
                    <Switch />
                  </Form.Item>
                </>
              ) : null
            }
          </Form.Item>
        </Form>
      </Modal>
      <Modal
        open={editOpen}
        title={`编辑共享：${editShare?.name ?? ''}`}
        onCancel={() => {
          setEditOpen(false)
          setEditShare(null)
        }}
        onOk={handleEdit}
        okText="保存"
        cancelText="取消"
      >
        <Form form={editForm} layout="vertical">
          <Form.Item label="共享名">
            <Input value={editShare?.name ?? ''} disabled />
          </Form.Item>
          <Form.Item label="路径">
            <Input value={editShare?.path ?? ''} disabled />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input />
          </Form.Item>
          {editShare?.protocol === 'nfs' && (
            <>
              <Form.Item name="nfsPermission" label="NFS 权限">
                <Radio.Group>
                  <Radio value="rw">读写</Radio>
                  <Radio value="ro">只读</Radio>
                </Radio.Group>
              </Form.Item>
              <Form.Item name="allowRootAccess" label="允许 root 访问" valuePropName="checked">
                <Switch />
              </Form.Item>
            </>
          )}
          {editShare?.protocol === 'ftp' && (
            <>
              <Form.Item name="sslPolicy" label="SSL 策略">
                <Radio.Group>
                  <Radio value="SslAllow">允许</Radio>
                  <Radio value="SslRequire">要求</Radio>
                  <Radio value="SslRequireCredentials">要求凭据</Radio>
                </Radio.Group>
              </Form.Item>
              <Form.Item name="authMode" label="认证模式">
                <Radio.Group>
                  <Radio value="anonymous">匿名</Radio>
                  <Radio value="basic">基本</Radio>
                  <Radio value="windows">Windows</Radio>
                </Radio.Group>
              </Form.Item>
            </>
          )}
          {editShare?.protocol === 'webdav' && (
            <Form.Item name="anonymousEnabled" label="启用匿名访问" valuePropName="checked">
              <Switch />
            </Form.Item>
          )}
        </Form>
      </Modal>
      <PermissionDrawer
        open={permOpen}
        share={permShare}
        onClose={() => {
          setPermOpen(false)
          setPermShare(null)
        }}
      />
      <ShareDetailDrawer
        open={detailOpen}
        share={detailShare}
        onClose={() => {
          setDetailOpen(false)
          setDetailShare(null)
        }}
        onSuccess={load}
      />
    </div>
  )
}
