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
  Radio
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
  SafetyOutlined
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import type { Share, PermissionPreset, Protocol, ProtocolCapabilities } from '../types'
import { api, call } from '../api'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import PermissionDrawer from '../components/PermissionDrawer'
import ProtocolCapabilityBanner from '../components/ProtocolCapabilityBanner'

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

  const load = async () => {
    setLoading(true)
    try {
      const proto = activeProtocol === 'all' ? undefined : activeProtocol
      const [s, p, c] = await Promise.all([
        call(() => api.adapter.list(proto)),
        call(api.preset.list),
        call(api.adapter.capabilities)
      ])
      setShares(s)
      setPresets(p)
      setCaps(c)
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

  // hotkey: Ctrl+N
  useEffect(() => {
    if (shareCreateOpen) {
      setModalOpen(true)
      setShareCreateOpen(false)
    }
  }, [shareCreateOpen, setShareCreateOpen])

  useTickEffect(refreshTick, () => load())
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    load()
  }, [activeProtocol])

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
      const created = await call(() =>
        api.adapter.create({
          protocol,
          name: v.name,
          path: v.path,
          description: v.description,
          // SMB
          encrypted: v.encrypted,
          // NFS
          authentication: v.authentication,
          nfsPermission: v.nfsPermission,
          allowRootAccess: v.allowRootAccess,
          enableUnmappedAccess: v.enableUnmappedAccess,
          // FTP
          port: v.port,
          sslPolicy: v.sslPolicy,
          authMode: v.authMode,
          // WebDAV
          anonymousEnabled: v.anonymousEnabled
        })
      )
      if (v.presetId && protocol === 'smb') {
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
            title: '协议',
            dataIndex: 'protocol',
            width: 80,
            render: (p: Protocol) => <Tag color={PROTOCOL_COLOR[p]}>{p.toUpperCase()}</Tag>
          }
        ]
      : []),
    { title: '名称', dataIndex: 'name', width: 160 },
    { title: '路径', dataIndex: 'path', ellipsis: true },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    ...(activeProtocol === 'all' || activeProtocol === 'nfs'
      ? [
          {
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
            title: 'NFS权限',
            dataIndex: 'nfsPermission',
            width: 90,
            render: (v: string | undefined) =>
              v ? <Tag color={v === 'rw' ? 'green' : 'default'}>{v}</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    { title: '连接数', dataIndex: 'concurrentUsers', width: 80 },
    // FTP 专有列
    ...(activeProtocol === 'ftp'
      ? [
          {
            title: '端口',
            dataIndex: 'port',
            width: 70,
            render: (v: number | undefined) => v ?? <span className="text-fog">-</span>
          },
          {
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
            title: '认证',
            dataIndex: 'authMode',
            width: 90,
            render: (v: string | undefined) =>
              v ? <Tag color={v === 'anonymous' ? 'orange' : 'blue'}>{v}</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    // WebDAV 专有列
    ...(activeProtocol === 'webdav'
      ? [
          {
            title: '端口',
            dataIndex: 'port',
            width: 70,
            render: (v: number | undefined) => v ?? <span className="text-fog">-</span>
          },
          {
            title: '匿名',
            dataIndex: 'anonymousEnabled',
            width: 70,
            render: (v: boolean | undefined) =>
              v ? <Tag color="orange">是</Tag> : <span className="text-fog">-</span>
          },
          {
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
            title: '加密',
            dataIndex: 'encrypted',
            width: 70,
            render: (v: boolean | undefined, r: Share) =>
              r.protocol === 'smb' && v ? <Tag color="blue">是</Tag> : <span className="text-fog">-</span>
          }
        ]
      : []),
    {
      title: '操作',
      width: 240,
      render: (_: unknown, r: Share) => (
        <Space>
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
    { key: 'nfs', label: 'NFS' },
    { key: 'ftp', label: 'FTP' },
    { key: 'webdav', label: 'WebDAV' }
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
                  <Form.Item name="encrypted" label="启用加密" valuePropName="checked" initialValue={false}>
                    <Switch />
                  </Form.Item>
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
    </div>
  )
}
