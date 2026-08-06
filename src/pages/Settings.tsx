import { useEffect, useState } from 'react'
import {
  Form,
  Switch,
  InputNumber,
  Button,
  Tag,
  Tabs,
  Table,
  Modal,
  Input,
  Space,
  Popconfirm,
  Descriptions,
  App
} from 'antd'
import { ReloadOutlined, PoweroffOutlined, PlusOutlined, DeleteOutlined, HistoryOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { SmbServerConfig, ServiceStatus, PermissionPreset, SmbSnapshotMeta } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import NfsSettingsPanel from '../components/NfsSettingsPanel'
import IisServicePanel from '../components/IisServicePanel'

export default function Settings() {
  const { message } = App.useApp()
  const [config, setConfig] = useState<Partial<SmbServerConfig>>({})
  const [svc, setSvc] = useState<ServiceStatus | null>(null)
  const [presets, setPresets] = useState<PermissionPreset[]>([])
  const [snapshots, setSnapshots] = useState<SmbSnapshotMeta[]>([])
  const [audit, setAudit] = useState('')
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const [presetModal, setPresetModal] = useState(false)
  const [presetForm] = Form.useForm()

  const refreshTick = useUiStore((s) => s.refreshTick)

  const load = async () => {
    try {
      const [c, s, p, a, snaps] = await Promise.all([
        call(api.smb.getConfig),
        call(api.smb.serviceStatus),
        call(api.preset.list),
        call(api.system.auditLog),
        call(api.smb.listSnapshots).catch(() => [] as SmbSnapshotMeta[])
      ])
      setConfig(c)
      setSvc(s)
      setPresets(p)
      setAudit(a)
      setSnapshots(snaps)
      form.setFieldsValue(c)
    } catch (e) {
      message.error((e as Error).message)
    }
  }
  useEffect(() => {
    load()
  }, [])

  // hotkey F5 刷新
  useTickEffect(refreshTick, () => {
    load()
  })

  const save = async () => {
    const v = await form.validateFields()
    setSaving(true)
    try {
      await call(() => api.smb.setConfig(v))
      message.success('已保存')
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const restart = async () => {
    try {
      await call(api.smb.restart)
      message.success('服务已重启')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const rollback = async (id: string) => {
    try {
      await call(() => api.smb.rollback(id))
      message.success('已回滚到所选快照')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  // 快照 id 形如 2026-08-06T12-34-56-789Z，解析为可读时间
  const fmtSnapshotTs = (id: string): string => {
    const m = id.match(/^(\d{4}-\d{2}-\d{2})-(\d{2})-(\d{2})-(\d{2})/)
    if (!m) return id
    return dayjs(`${m[1]} ${m[2]}:${m[3]}:${m[4]}`).format('YYYY-MM-DD HH:mm:ss')
  }

  const exportAudit = () => {
    const blob = new Blob([audit], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `audit-${Date.now()}.log`
    a.click()
    URL.revokeObjectURL(url)
  }

  const savePreset = async () => {
    const v = await presetForm.validateFields()
    try {
      await call(() =>
        api.preset.save({
          id: v.id || `custom-${Date.now()}`,
          name: v.name,
          description: v.description || '',
          builtIn: false,
          entries: []
        })
      )
      message.success('模板已保存')
      setPresetModal(false)
      presetForm.resetFields()
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }
  const delPreset = async (id: string) => {
    try {
      await call(() => api.preset.delete(id))
      message.success('已删除')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  void config

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">服务器配置</h1>
        <Button icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>
      </div>
      <Tabs
        items={[
          {
            key: 'smb',
            label: 'SMB',
            children: (
              <Tabs
                items={[
          {
            key: 'config',
            label: '服务器配置',
            children: (
              <div className="glass-card p-4">
                <Form form={form} layout="vertical">
                  <Space wrap size="large">
                    <Form.Item name="enableSMB1Protocol" label="SMB1" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="enableSMB2Protocol" label="SMB2" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="enableSMB3Protocol" label="SMB3" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="enableGuestUserAccess" label="访客访问" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="enableInsecureGuestLogons" label="不安全访客登录" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="requireSecuritySignature" label="要求签名" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="enableMultiChannel" label="多通道" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="announceServer" label="声明服务器" valuePropName="checked">
                      <Switch />
                    </Form.Item>
                    <Form.Item name="unauthenticatedUsersTimeLimit" label="未认证超时(秒)">
                      <InputNumber />
                    </Form.Item>
                  </Space>
                </Form>
                <Space className="mt-4">
                  <Button type="primary" loading={saving} onClick={save}>
                    保存配置
                  </Button>
                  <Popconfirm title="重启 LanmanServer 服务？" onConfirm={restart}>
                    <Button icon={<PoweroffOutlined />}>重启服务</Button>
                  </Popconfirm>
                </Space>
                {svc && (
                  <Descriptions
                    className="mt-4"
                    size="small"
                    column={2}
                    items={[
                      {
                        key: 'st',
                        label: '服务状态',
                        children: <Tag color={svc.status === 'Running' ? 'green' : 'red'}>{svc.status}</Tag>
                      },
                      { key: 'srt', label: '启动类型', children: svc.startType || '-' }
                    ]}
                  />
                )}
              </div>
            )
          },
          {
            key: 'presets',
            label: '权限模板',
            children: (
              <div className="glass-card p-3">
                <div className="mb-3">
                  <Button type="primary" icon={<PlusOutlined />} onClick={() => setPresetModal(true)}>
                    新建模板
                  </Button>
                </div>
                <Table
                  dataSource={presets}
                  rowKey="id"
                  size="middle"
                  pagination={false}
                  columns={[
                    { title: '名称', dataIndex: 'name', width: 160 },
                    { title: '描述', dataIndex: 'description' },
                    {
                      title: '类型',
                      dataIndex: 'builtIn',
                      width: 90,
                      render: (v: boolean) => (v ? <Tag>内置</Tag> : <Tag color="blue">自定义</Tag>)
                    },
                    {
                      title: '条目数',
                      dataIndex: 'entries',
                      width: 80,
                      render: (v: { length: number } | undefined) => v?.length || 0
                    },
                    {
                      title: '操作',
                      width: 80,
                      render: (_: unknown, r: PermissionPreset) =>
                        r.builtIn ? null : (
                          <Popconfirm title="删除该模板？" onConfirm={() => delPreset(r.id)}>
                            <Button size="small" danger icon={<DeleteOutlined />} />
                          </Popconfirm>
                        )
                    }
                  ]}
                />
              </div>
            )
          },
          {
            key: 'audit',
            label: '审计日志',
            children: (
              <div className="glass-card p-3">
                <Button className="mb-2" onClick={exportAudit}>
                  导出日志
                </Button>
                <pre className="text-xs bg-white/40 p-3 rounded-card max-h-96 overflow-auto whitespace-pre-wrap">
                  {audit || '暂无日志'}
                </pre>
              </div>
            )
          },
          {
            key: 'snapshots',
            label: (
              <span>
                <HistoryOutlined /> 快照历史
              </span>
            ),
            children: (
              <div className="glass-card p-3">
                <p className="text-xs text-fog mb-3">
                  每次 SMB 配置写入前自动保存当前完整配置为快照（最多 20 份）。回滚会覆盖当前配置，且不产生新快照。
                </p>
                <Table
                  dataSource={snapshots}
                  rowKey="id"
                  size="middle"
                  pagination={{ pageSize: 10 }}
                  locale={{ emptyText: '暂无快照' }}
                  columns={[
                    { title: '时间', dataIndex: 'id', render: (v: string) => fmtSnapshotTs(v) },
                    {
                      title: '操作',
                      width: 100,
                      render: (_: unknown, r: SmbSnapshotMeta) => (
                        <Popconfirm
                          title="回滚到该快照？"
                          description="当前配置将被覆盖"
                          okText="回滚"
                          okType="danger"
                          cancelText="取消"
                          onConfirm={() => rollback(r.id)}
                        >
                          <Button size="small">回滚</Button>
                        </Popconfirm>
                      )
                    }
                  ]}
                />
              </div>
            )
          }
                ]}
              />
            )
          },
          {
            key: 'nfs',
            label: 'NFS',
            children: <NfsSettingsPanel />
          },
          {
            key: 'ftp',
            label: 'FTP',
            children: <IisServicePanel protocol="ftp" />
          },
          {
            key: 'webdav',
            label: 'WebDAV',
            children: <IisServicePanel protocol="webdav" />
          }
        ]}
      />
      <Modal
        open={presetModal}
        title="新建权限模板"
        onCancel={() => setPresetModal(false)}
        onOk={savePreset}
        okText="保存"
        cancelText="取消"
      >
        <Form form={presetForm} layout="vertical">
          <Form.Item name="name" label="模板名" rules={[{ required: true, message: '请输入模板名' }]}>
            <Input />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input />
          </Form.Item>
          <div className="text-xs text-fog">权限条目可在共享管理新建时通过模板选择应用</div>
        </Form>
      </Modal>
    </div>
  )
}
