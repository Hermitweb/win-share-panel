import { useEffect, useState } from 'react'
import {
  Drawer,
  Tabs,
  Descriptions,
  Tag,
  Button,
  Space,
  Table,
  App,
  Popconfirm,
  Form,
  Input,
  InputNumber,
  Select,
  Switch,
  Empty,
  Spin,
  Divider,
  Typography,
} from 'antd'
import {
  ReloadOutlined,
  CloseCircleOutlined,
  SaveOutlined,
  FolderOpenOutlined,
} from '@ant-design/icons'
import type React from 'react'
import { api, call } from '../api'
import { useResetOnKeyChange } from '../hooks/useResetOnOpen'
import type { Share } from '../types'

interface Props {
  open: boolean
  share: Share | null
  onClose: () => void
  onSuccess: () => void
}

interface OpenFile {
  fileId: number
  path: string
  clientUserName: string
  clientComputerName: string
  lockCount: number
}

interface ClientConn {
  clientUserName: string
  clientComputerName: string
  openFiles: number
}

export default function ShareDetailDrawer({ open, share, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [tab, setTab] = useState('info')
  const [connections, setConnections] = useState<{
    concurrentUsers: number
    clientConnections: ClientConn[]
  } | null>(null)
  const [openFiles, setOpenFiles] = useState<OpenFile[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()

  // 仅 SMB 支持详细操作（连接/打开文件/高级属性）
  const isSmb = share?.protocol === 'smb'

  // A2：主机名用于拼网络路径（SMB→UNC；FTP/WebDAV→访问 URL）
  const [host, setHost] = useState('')
  useEffect(() => {
    let dead = false
    api.system
      .currentUser()
      .then((u) => !dead && setHost(u.computerName))
      .catch(() => {})
    return () => {
      dead = true
    }
  }, [])
  const netPath = (s: Share | null): string => {
    if (!s) return ''
    if (s.protocol === 'smb') return `\\\\${host}\\${s.name}`
    if (s.protocol === 'ftp') return `ftp://${host}${s.port ? ':' + s.port : ''}`
    if (s.protocol === 'webdav') return `http://${host}${s.port ? ':' + s.port : ''}`
    return s.path
  }

  // 渲染期重读序号：连接与打开文件各一个，「刷新」只 bump 自己那一个（不连带多发一次 IPC）
  const [connNonce, setConnNonce] = useState(0)
  const [filesNonce, setFilesNonce] = useState(0)

  // 打开/换共享：tab 回到「基本信息」（渲染期调整，P-5）
  useResetOnKeyChange(`${open}|${share?.name ?? ''}`, () => {
    if (!open || !share) return
    setTab('info')
  })

  // 打开/换共享/重读连接：SMB 的加载态与打开同帧生效
  useResetOnKeyChange(`${open}|${share?.name ?? ''}|${connNonce}`, () => {
    if (!open || !share || share.protocol !== 'smb') return
    setLoading(true)
  })

  // 表单按协议预填（antd 命令式 API，非 React state）。
  // 只在打开/换共享时同步：刷新连接不打断「高级属性」里未保存的编辑。
  useEffect(() => {
    if (!open || !share) return
    if (share.protocol === 'smb') {
      form.setFieldsValue({
        description: share.description,
        hidden: share.hidden,
      })
    } else if (share.protocol === 'nfs') {
      form.setFieldsValue({
        nfsPermission: share.nfsPermission ?? 'rw',
        allowRootAccess: !!share.allowRootAccess,
        enableUnmappedAccess: !!share.enableUnmappedAccess,
      })
    } else if (share.protocol === 'ftp') {
      form.setFieldsValue({
        sslPolicy: share.sslPolicy ?? 'SslAllow',
        authMode: share.authMode ?? 'basic',
        physicalPath: share.path,
      })
    } else if (share.protocol === 'webdav') {
      form.setFieldsValue({
        anonymousEnabled: !!share.anonymousEnabled,
        authoringEnabled: !!share.authoringEnabled,
      })
    }
  }, [open, share, form])

  // 连接：打开/换共享/「刷新」各恰好一次 IPC（loader 定义在 effect 内部，见 B-1/P-3）
  useEffect(() => {
    if (!open || !share || share.protocol !== 'smb') return
    let dead = false
    const loadConnections = async () => {
      try {
        const r = await call(() => api.share.connections(share.name))
        if (!dead) setConnections(r)
      } catch (e) {
        if (!dead) message.error((e as Error).message)
      } finally {
        if (!dead) setLoading(false)
      }
    }
    void loadConnections()
    return () => {
      dead = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 有意仅依赖 open/share/connNonce：message 为上下文稳定引用，纳入依赖会重复触发
  }, [open, share, connNonce])

  // 打开文件：失败静默置空（既有语义）；独立序号使「刷新连接」不连带重读打开文件
  useEffect(() => {
    if (!open || !share || share.protocol !== 'smb') return
    let dead = false
    const loadOpenFiles = async () => {
      try {
        const r = await call(() => api.share.openFiles(share.name))
        if (!dead) setOpenFiles(r)
      } catch {
        if (!dead) setOpenFiles([])
      }
    }
    void loadOpenFiles()
    return () => {
      dead = true
    }
  }, [open, share, filesNonce])

  const handleCloseAllFiles = async () => {
    if (!share) return
    try {
      const r = await call(() => api.share.closeOpenFiles(share.name))
      if (r.failed > 0) {
        message.warning(`已关闭 ${r.closed} 个，失败 ${r.failed} 个`)
      } else {
        message.success(`已关闭 ${r.closed} 个打开文件`)
      }
      setFilesNonce((n) => n + 1)
      setConnNonce((n) => n + 1)
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleSave = async () => {
    if (!share) return
    const v = await form.validateFields()
    setSaving(true)
    try {
      await call(() =>
        api.share.update(share.name, {
          description: v.description,
          concurrentUserLimit: v.concurrentUserLimit,
          cachingMode: v.cachingMode,
          folderEnumerationMode: v.folderEnumerationMode,
          encryptData: v.encryptData,
          hidden: v.hidden,
        }),
      )
      message.success('已保存')
      onSuccess()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const fileInfoColumns = [
    { title: '路径', dataIndex: 'path', ellipsis: true },
    { title: '用户', dataIndex: 'clientUserName', width: 140, ellipsis: true },
    { title: '客户端', dataIndex: 'clientComputerName', width: 140, ellipsis: true },
    { title: '锁', dataIndex: 'lockCount', width: 60 },
  ]

  // 协议专门适配：NFS/FTP/WebDAV 站点配置保存（经 adapter.update 路由；SMB 走上方"高级属性"）
  const handleProtoSave = async () => {
    if (!share || isSmb) return
    const v = await form.validateFields()
    setSaving(true)
    try {
      await call(() => api.adapter.update(share.name, { ...v, protocol: share.protocol }))
      message.success('站点配置已保存')
      onSuccess()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const connColumns = [
    { title: '用户', dataIndex: 'clientUserName', ellipsis: true },
    { title: '客户端', dataIndex: 'clientComputerName', ellipsis: true },
    { title: '打开文件数', dataIndex: 'openFiles', width: 100 },
  ]

  const items: Array<{ key: string; label: React.ReactNode; children: React.ReactNode }> = [
    {
      key: 'info',
      label: '基本信息',
      children: (
        <div>
          <Descriptions
            size="small"
            column={1}
            bordered
            items={[
              { key: 'n', label: '共享名', children: share?.name || '-' },
              { key: 'p', label: '本地路径', children: share?.path || '-' },
              { key: 'd', label: '描述', children: share?.description || '-' },
              {
                key: 'proto',
                label: '协议',
                children: (
                  <Tag color="blue">{share?.protocol?.toUpperCase()}</Tag>
                ) as unknown as string,
              },
              { key: 't', label: '类型', children: share?.type || '-' },
              {
                key: 'net',
                label: '网络路径',
                children:
                  share && host ? (
                    <Typography.Text copyable style={{ fontSize: 12 }}>
                      {netPath(share)}
                    </Typography.Text>
                  ) : (
                    '—'
                  ),
              },
              {
                key: 's',
                label: '状态',
                children: (
                  <Tag color={share?.status === 'Enabled' ? 'green' : 'default'}>
                    {share?.status}
                  </Tag>
                ) as unknown as string,
              },
              { key: 'u', label: '当前连接', children: String(share?.concurrentUsers ?? 0) },
              ...(share?.protocol === 'smb'
                ? [
                    {
                      key: 'e',
                      label: '加密',
                      children: (share?.encrypted ? (
                        <Tag color="blue">是</Tag>
                      ) : (
                        <span className="text-fog">否</span>
                      )) as unknown as string,
                    },
                    {
                      key: 'c',
                      label: '缓存',
                      children: (share?.cached ? (
                        <Tag>是</Tag>
                      ) : (
                        <span className="text-fog">否</span>
                      )) as unknown as string,
                    },
                  ]
                : []),
            ]}
          />
        </div>
      ),
    },
  ]

  // SMB 专属：属性编辑 + 连接 + 打开文件
  if (isSmb) {
    items.push({
      key: 'props',
      label: '高级属性',
      children: (
        <Form form={form} layout="vertical">
          <Form.Item name="description" label="描述">
            <Input />
          </Form.Item>
          <Form.Item name="concurrentUserLimit" label="并发用户上限（0=无限制）">
            <InputNumber min={0} max={65535} style={{ width: 200 }} />
          </Form.Item>
          <Form.Item name="folderEnumerationMode" label="文件夹枚举模式">
            <Select
              options={[
                { label: '基于访问（仅可见有权限的子项）', value: 'AccessBased' },
                { label: '无限制（可见全部子项）', value: 'Unrestricted' },
              ]}
              style={{ width: '100%' }}
            />
          </Form.Item>
          <Form.Item name="cachingMode" label="脱机缓存模式">
            <Select
              options={[
                { label: '无', value: 'None' },
                { label: '手动', value: 'Manual' },
                { label: '文档', value: 'Documents' },
                { label: '程序', value: 'Programs' },
                { label: 'BranchCache', value: 'BranchCache' },
              ]}
              style={{ width: '100%' }}
            />
          </Form.Item>
          <Form.Item name="encryptData" label="启用数据加密" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Form.Item name="hidden" label="隐藏共享（不在网络浏览列表显示）" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleSave}>
            保存属性
          </Button>
        </Form>
      ),
    })
    items.push({
      key: 'conns',
      label: (
        <span>
          连接
          {connections && connections.concurrentUsers > 0 && (
            <Tag color="blue" className="ml-1">
              {connections.concurrentUsers}
            </Tag>
          )}
        </span>
      ),
      children: (
        <Spin spinning={loading}>
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs text-fog">
              当前 {connections?.concurrentUsers ?? 0} 个连接
            </span>
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={() => setConnNonce((n) => n + 1)}
            >
              刷新
            </Button>
          </div>
          <Table
            dataSource={connections?.clientConnections || []}
            rowKey={(r) => `${r.clientUserName}-${r.clientComputerName}`}
            columns={connColumns}
            size="small"
            pagination={false}
            locale={{ emptyText: <Empty description="暂无活动连接" /> }}
            scroll={{ y: 320 }}
          />
        </Spin>
      ),
    })
    items.push({
      key: 'files',
      label: (
        <span>
          打开文件
          {openFiles.length > 0 && (
            <Tag color="orange" className="ml-1">
              {openFiles.length}
            </Tag>
          )}
        </span>
      ),
      children: (
        <div>
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs text-fog">{openFiles.length} 个打开文件</span>
            <Space>
              <Button
                size="small"
                icon={<ReloadOutlined />}
                onClick={() => setFilesNonce((n) => n + 1)}
              >
                刷新
              </Button>
              {openFiles.length > 0 && (
                <Popconfirm
                  title={`关闭共享 ${share?.name} 上的全部 ${openFiles.length} 个打开文件？`}
                  description="可能导致客户端数据丢失"
                  okText="全部关闭"
                  okType="danger"
                  cancelText="取消"
                  onConfirm={handleCloseAllFiles}
                >
                  <Button size="small" danger icon={<CloseCircleOutlined />}>
                    全部关闭
                  </Button>
                </Popconfirm>
              )}
            </Space>
          </div>
          <Table
            dataSource={openFiles}
            rowKey="fileId"
            columns={fileInfoColumns}
            size="small"
            pagination={{ pageSize: 10 }}
            locale={{ emptyText: <Empty description="暂无打开文件" /> }}
            scroll={{ y: 320 }}
          />
        </div>
      ),
    })
  }

  // 协议专门适配：非 SMB 协议的"站点配置"编辑 Tab（能力对齐 create 面板的协议字段）
  if (share?.protocol === 'nfs') {
    items.push({
      key: 'proto-cfg',
      label: '站点配置',
      children: (
        <div>
          <Form form={form} layout="vertical">
            <Form.Item name="nfsPermission" label="共享权限">
              <Select
                style={{ width: 200 }}
                options={[
                  { label: '只读 (ro)', value: 'ro' },
                  { label: '读写 (rw)', value: 'rw' },
                ]}
              />
            </Form.Item>
            <Form.Item name="allowRootAccess" label="允许 root 完全访问" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item
              name="enableUnmappedAccess"
              label="允许未映射访问（无身份映射）"
              valuePropName="checked"
            >
              <Switch />
            </Form.Item>
          </Form>
          <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleProtoSave}>
            保存配置
          </Button>
        </div>
      ),
    })
  }
  if (share?.protocol === 'ftp') {
    items.push({
      key: 'proto-cfg',
      label: '站点配置',
      children: (
        <div>
          <Form form={form} layout="vertical">
            <Form.Item name="sslPolicy" label="SSL 策略">
              <Select
                options={[
                  { label: '允许 TLS（不强制）', value: 'SslAllow' },
                  { label: '要求 TLS', value: 'SslRequire' },
                  { label: '要求 TLS + 客户端证书', value: 'SslRequireCredentials' },
                ]}
              />
            </Form.Item>
            <Form.Item name="authMode" label="认证模式">
              <Select
                options={[
                  { label: '匿名', value: 'anonymous' },
                  { label: '基本（本地用户）', value: 'basic' },
                  { label: 'Windows', value: 'windows' },
                ]}
              />
            </Form.Item>
            <Form.Item
              label="本地物理路径"
              tooltip="修改后需与新路径的 NTFS 权限及授权规则匹配"
              required
            >
              <Space.Compact style={{ width: '100%' }}>
                <Form.Item
                  name="physicalPath"
                  noStyle
                  rules={[{ required: true, message: '请输入路径（如 E:\\ftp\\site）' }]}
                >
                  <Input />
                </Form.Item>
                <Button
                  icon={<FolderOpenOutlined />}
                  onClick={async () => {
                    try {
                      const p = await api.system.selectFolder()
                      if (p) form.setFieldsValue({ physicalPath: p })
                    } catch (e) {
                      message.error((e as Error).message)
                    }
                  }}
                >
                  浏览
                </Button>
              </Space.Compact>
            </Form.Item>
          </Form>
          <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleProtoSave}>
            保存配置
          </Button>
        </div>
      ),
    })
  }
  if (share?.protocol === 'webdav') {
    items.push({
      key: 'proto-cfg',
      label: '站点配置',
      children: (
        <div>
          <Form form={form} layout="vertical">
            <Form.Item
              name="anonymousEnabled"
              label="匿名访问（站点级）"
              valuePropName="checked"
              tooltip="需服务器级匿名认证同时开启才生效（服务配置页）"
            >
              <Switch />
            </Form.Item>
            <Form.Item
              name="authoringEnabled"
              label="允许写入（WebDAV authoring）"
              valuePropName="checked"
            >
              <Switch />
            </Form.Item>
          </Form>
          <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleProtoSave}>
            保存配置
          </Button>
        </div>
      ),
    })
  }

  return (
    <Drawer open={open} title={`共享详情：${share?.name ?? ''}`} onClose={onClose} width={640}>
      <Tabs activeKey={tab} onChange={setTab} items={items} size="small" />
      {!isSmb && share && (
        <>
          <Divider />
          <div className="text-xs text-fog">
            {share.protocol.toUpperCase()} 授权规则经共享列表的"权限"按钮编辑；站点级配置见本页
            "站点配置"Tab；服务器级（端口段/SSL 策略/认证开关）在"服务配置"页。
          </div>
        </>
      )}
    </Drawer>
  )
}
