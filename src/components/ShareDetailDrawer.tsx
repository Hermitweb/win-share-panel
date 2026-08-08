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
  Divider
} from 'antd'
import { ReloadOutlined, CloseCircleOutlined, SaveOutlined } from '@ant-design/icons'
import type React from 'react'
import { api, call } from '../api'
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
  const [connections, setConnections] = useState<{ concurrentUsers: number; clientConnections: ClientConn[] } | null>(null)
  const [openFiles, setOpenFiles] = useState<OpenFile[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()

  // 仅 SMB 支持详细操作（连接/打开文件/高级属性）
  const isSmb = share?.protocol === 'smb'

  useEffect(() => {
    if (!open || !share) return
    setTab('info')
    if (isSmb) {
      loadConnections()
      loadOpenFiles()
      // 初始化表单
      form.setFieldsValue({
        description: share.description
      })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, share])

  const loadConnections = async () => {
    if (!share) return
    setLoading(true)
    try {
      const r = await call(() => api.share.connections(share.name))
      setConnections(r)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const loadOpenFiles = async () => {
    if (!share) return
    try {
      const r = await call(() => api.share.openFiles(share.name))
      setOpenFiles(r)
    } catch {
      setOpenFiles([])
    }
  }

  const handleCloseAllFiles = async () => {
    if (!share) return
    try {
      const r = await call(() => api.share.closeOpenFiles(share.name))
      if (r.failed > 0) {
        message.warning(`已关闭 ${r.closed} 个，失败 ${r.failed} 个`)
      } else {
        message.success(`已关闭 ${r.closed} 个打开文件`)
      }
      loadOpenFiles()
      loadConnections()
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
          encryptData: v.encryptData
        })
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
    { title: '锁', dataIndex: 'lockCount', width: 60 }
  ]

  const connColumns = [
    { title: '用户', dataIndex: 'clientUserName', ellipsis: true },
    { title: '客户端', dataIndex: 'clientComputerName', ellipsis: true },
    { title: '打开文件数', dataIndex: 'openFiles', width: 100 }
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
              { key: 'proto', label: '协议', children: <Tag color="blue">{share?.protocol?.toUpperCase()}</Tag> as unknown as string },
              { key: 't', label: '类型', children: share?.type || '-' },
              { key: 's', label: '状态', children: <Tag color={share?.status === 'Enabled' ? 'green' : 'default'}>{share?.status}</Tag> as unknown as string },
              { key: 'u', label: '当前连接', children: String(share?.concurrentUsers ?? 0) },
              ...(share?.protocol === 'smb' ? [
                { key: 'e', label: '加密', children: (share?.encrypted ? <Tag color="blue">是</Tag> : <span className="text-fog">否</span>) as unknown as string },
                { key: 'c', label: '缓存', children: (share?.cached ? <Tag>是</Tag> : <span className="text-fog">否</span>) as unknown as string }
              ] : [])
            ]}
          />
        </div>
      )
    }
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
                { label: '无限制（可见全部子项）', value: 'Unrestricted' }
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
                { label: 'BranchCache', value: 'BranchCache' }
              ]}
              style={{ width: '100%' }}
            />
          </Form.Item>
          <Form.Item name="encryptData" label="启用数据加密" valuePropName="checked">
            <Switch />
          </Form.Item>
          <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleSave}>
            保存属性
          </Button>
        </Form>
      )
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
            <Button size="small" icon={<ReloadOutlined />} onClick={loadConnections}>
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
      )
    })
    items.push({
      key: 'files',
      label: (
        <span>
          打开文件
          {openFiles.length > 0 && <Tag color="orange" className="ml-1">{openFiles.length}</Tag>}
        </span>
      ),
      children: (
        <div>
          <div className="mb-2 flex items-center justify-between">
            <span className="text-xs text-fog">{openFiles.length} 个打开文件</span>
            <Space>
              <Button size="small" icon={<ReloadOutlined />} onClick={loadOpenFiles}>
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
      )
    })
  }

  return (
    <Drawer
      open={open}
      title={`共享详情：${share?.name ?? ''}`}
      onClose={onClose}
      width={640}
    >
      <Tabs activeKey={tab} onChange={setTab} items={items} size="small" />
      {!isSmb && share && (
        <>
          <Divider />
          <div className="text-xs text-fog">
            {share.protocol.toUpperCase()} 协议的站点级配置（端口/SSL/认证/权限）请通过共享列表中的"权限"按钮编辑。
          </div>
        </>
      )}
    </Drawer>
  )
}
