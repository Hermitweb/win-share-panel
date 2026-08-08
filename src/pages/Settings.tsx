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
  App,
  Select,
  Tooltip,
  Upload,
  Divider,
  Collapse
} from 'antd'
import {
  ReloadOutlined,
  PoweroffOutlined,
  PlusOutlined,
  DeleteOutlined,
  HistoryOutlined,
  EditOutlined,
  CopyOutlined,
  ExportOutlined,
  ImportOutlined,
  UndoOutlined,
  CaretRightOutlined,
  PauseOutlined
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { SmbServerConfig, ServiceStatus, PermissionPreset, SmbSnapshotMeta } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import NfsSettingsPanel from '../components/NfsSettingsPanel'
import FtpSettingsPanel from '../components/FtpSettingsPanel'
import WebdavSettingsPanel from '../components/WebdavSettingsPanel'
import PresetEditor from '../components/PresetEditor'

export default function Settings() {
  const { message, modal } = App.useApp()
  const [config, setConfig] = useState<Partial<SmbServerConfig>>({})
  const [svc, setSvc] = useState<ServiceStatus | null>(null)
  const [presets, setPresets] = useState<PermissionPreset[]>([])
  const [snapshots, setSnapshots] = useState<SmbSnapshotMeta[]>([])
  const [audit, setAudit] = useState('')
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const [editPreset, setEditPreset] = useState<PermissionPreset | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState<string[]>([])

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

  const startSvc = async () => {
    try {
      await call(api.smb.start)
      message.success('服务已启动')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const stopSvc = async () => {
    try {
      await call(api.smb.stop)
      message.success('服务已停止')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const restoreDefault = async () => {
    try {
      const def = await call(api.smb.restoreDefault)
      message.success('已恢复默认配置')
      form.setFieldsValue(def)
      setConfig(def)
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

  // === 权限模板操作 ===
  const delPreset = (p: PermissionPreset) => {
    modal.confirm({
      title: `删除模板「${p.name}」？`,
      content: '此操作不可恢复。',
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        try {
          await call(() => api.preset.delete(p.id))
          message.success('已删除')
          load()
        } catch (e) {
          message.error((e as Error).message)
        }
      }
    })
  }

  const duplicatePreset = async (p: PermissionPreset) => {
    try {
      await call(() => api.preset.duplicate(p.id))
      message.success('已复制为新模板')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const exportPresets = async () => {
    try {
      const json = await call(() => api.preset.export())
      const blob = new Blob([json], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `presets-${Date.now()}.json`
      a.click()
      URL.revokeObjectURL(url)
      message.success('模板已导出')
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const importPresetsProps: UploadProps = {
    beforeUpload: async (file) => {
      try {
        const text = await file.text()
        const result = await call(() => api.preset.import(text))
        if (result.skipped > 0) {
          message.warning(
            `导入完成：成功 ${result.imported} 个，跳过 ${result.skipped} 个。${result.errors.slice(0, 2).join('；')}${result.errors.length > 2 ? ' 等' : ''}`
          )
        } else {
          message.success(`导入完成：成功 ${result.imported} 个模板`)
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

  void config

  const presetColumns = [
    { title: '名称', dataIndex: 'name', width: 140 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '分类',
      dataIndex: 'category',
      width: 90,
      render: (v: string | undefined) =>
        v ? <Tag color="blue">{v}</Tag> : <span className="text-fog">-</span>
    },
    {
      title: '类型',
      dataIndex: 'builtIn',
      width: 80,
      render: (v: boolean) => (v ? <Tag>内置</Tag> : <Tag color="blue">自定义</Tag>)
    },
    {
      title: '条目',
      dataIndex: 'entries',
      width: 60,
      render: (v: { length: number } | undefined) => v?.length || 0
    },
    {
      title: '操作',
      width: 200,
      render: (_: unknown, r: PermissionPreset) => (
        <Space>
          <Tooltip title={r.builtIn ? '查看/复制' : '编辑'}>
            <Button size="small" icon={<EditOutlined />} onClick={() => setEditPreset(r)} />
          </Tooltip>
          <Tooltip title="复制为新模板">
            <Button size="small" icon={<CopyOutlined />} onClick={() => duplicatePreset(r)} />
          </Tooltip>
          {!r.builtIn && (
            <Popconfirm title="删除该模板？" onConfirm={() => delPreset(r)}>
              <Button size="small" danger icon={<DeleteOutlined />} />
            </Popconfirm>
          )}
        </Space>
      )
    }
  ]

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
                          <div className="text-sm font-medium mb-2 text-fog">基础协议</div>
                          <div className="flex flex-wrap gap-6 mb-3">
                            <Form.Item name="enableSMB1Protocol" label="SMB1" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item name="enableSMB2Protocol" label="SMB2" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item name="enableSMB3Protocol" label="SMB3" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item name="auditSmb1Access" label="审计 SMB1" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                          </div>

                          <div className="text-sm font-medium mb-2 text-fog">安全</div>
                          <div className="flex flex-wrap gap-6 mb-3">
                            <Form.Item
                              name="enableGuestUserAccess"
                              label="访客访问"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableInsecureGuestLogons"
                              label="不安全访客登录"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="requireSecuritySignature"
                              label="要求签名"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableStrictNameChecking"
                              label="严格名称检查"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item name="silentAU" label="静默 AU" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                          </div>

                          <div className="text-sm font-medium mb-2 text-fog">性能与功能</div>
                          <div className="flex flex-wrap gap-6 mb-3">
                            <Form.Item
                              name="enableMultiChannel"
                              label="多通道"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item name="enableLeasing" label="租约" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item name="enableOplocks" label="机会锁" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableOplockDirectoryCache"
                              label="目录缓存机会锁"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableSMBDirectoryCache"
                              label="目录缓存"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableChannelChange"
                              label="通道切换"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item name="enableSMBQUIC" label="SMB QUIC" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item name="announceServer" label="声明服务器" valuePropName="checked">
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="multipleSessionsPerConnection"
                              label="每连接多会话"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                          </div>

                          <Collapse
                            size="small"
                            activeKey={advancedOpen}
                            onChange={setAdvancedOpen}
                            className="mb-3"
                            items={[
                              {
                                key: 'advanced',
                                label: '高级参数（吞吐/超时/压缩）',
                                children: (
                                  <div className="flex flex-wrap gap-6">
                                    <Form.Item
                                      name="unauthenticatedUsersTimeLimit"
                                      label="未认证超时(秒)"
                                    >
                                      <InputNumber min={0} max={65535} />
                                    </Form.Item>
                                    <Form.Item name="sessionTimeoutSeconds" label="会话超时(秒)">
                                      <InputNumber min={0} max={65535} />
                                    </Form.Item>
                                    <Form.Item
                                      name="maxSessionPerConnection"
                                      label="每连接最大会话"
                                    >
                                      <InputNumber min={1} max={65535} />
                                    </Form.Item>
                                    <Form.Item name="maxMpxCount" label="最大 Mpx 数">
                                      <InputNumber min={1} max={65535} />
                                    </Form.Item>
                                    <Form.Item name="maxWorkItems" label="最大工作项">
                                      <InputNumber min={1} max={65535} />
                                    </Form.Item>
                                    <Form.Item name="maxThreadsPerQueue" label="每队列最大线程">
                                      <InputNumber min={1} max={65535} />
                                    </Form.Item>
                                    <Form.Item name="requestCompression" label="请求压缩">
                                      <Select
                                        style={{ width: 120 }}
                                        options={[
                                          { label: '关闭', value: 'Off' },
                                          { label: '允许', value: 'Allow' },
                                          { label: '要求', value: 'Require' }
                                        ]}
                                      />
                                    </Form.Item>
                                  </div>
                                )
                              }
                            ]}
                          />

                          <Space className="mt-4 flex-wrap">
                            <Button type="primary" loading={saving} onClick={save}>
                              保存配置
                            </Button>
                            <Popconfirm
                              title="确认恢复 SMB 默认配置？"
                              description="当前配置将被覆盖（自动产生快照）"
                              okText="恢复默认"
                              okType="danger"
                              cancelText="取消"
                              onConfirm={restoreDefault}
                            >
                              <Button icon={<UndoOutlined />} danger>
                                恢复默认
                              </Button>
                            </Popconfirm>
                            <Popconfirm title="重启 LanmanServer 服务？" onConfirm={restart}>
                              <Button icon={<PoweroffOutlined />}>重启服务</Button>
                            </Popconfirm>
                            {svc?.status === 'Stopped' ? (
                              <Button icon={<CaretRightOutlined />} onClick={startSvc}>
                                启动
                              </Button>
                            ) : (
                              <Popconfirm title="停止 LanmanServer 服务？" onConfirm={stopSvc}>
                                <Button icon={<PauseOutlined />}>
                                  停止
                                </Button>
                              </Popconfirm>
                            )}
                          </Space>
                          {svc && (
                            <Descriptions
                              className="mt-4"
                              size="small"
                              column={3}
                              items={[
                                {
                                  key: 'st',
                                  label: '服务状态',
                                  children: (
                                    <Tag color={svc.status === 'Running' ? 'green' : 'red'}>
                                      {svc.status}
                                    </Tag>
                                  )
                                },
                                { key: 'srt', label: '启动类型', children: svc.startType || '-' },
                                { key: 'sn', label: '服务名', children: svc.name }
                              ]}
                            />
                          )}
                          <Divider style={{ margin: '12px 0' }} />
                          <div className="text-xs text-fog">
                            提示：SMB1 出于安全考虑默认关闭；建议保持"要求签名"开启以防止中间人攻击。修改高级参数可能影响性能与兼容性，不确定时请点"恢复默认"。
                          </div>
                        </Form>
                      </div>
                    )
                  },
                  {
                    key: 'presets',
                    label: '权限模板',
                    children: (
                      <div className="glass-card p-3">
                        <div className="mb-3 flex items-center justify-between">
                          <span className="text-xs text-fog">
                            内置模板可查看/复制为自定义；自定义模板可编辑、删除。支持导入导出。
                          </span>
                          <Space>
                            <Button icon={<ExportOutlined />} onClick={exportPresets}>
                              导出
                            </Button>
                            <Upload {...importPresetsProps}>
                              <Button icon={<ImportOutlined />}>导入</Button>
                            </Upload>
                          </Space>
                        </div>
                        <Table
                          dataSource={presets}
                          rowKey="id"
                          size="middle"
                          pagination={false}
                          columns={presetColumns}
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
                            {
                              title: '时间',
                              dataIndex: 'id',
                              render: (v: string) => fmtSnapshotTs(v)
                            },
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
            children: <FtpSettingsPanel />
          },
          {
            key: 'webdav',
            label: 'WebDAV',
            children: <WebdavSettingsPanel />
          }
        ]}
      />
      <PresetEditor
        open={!!editPreset}
        preset={editPreset}
        onClose={() => setEditPreset(null)}
        onSuccess={load}
      />
    </div>
  )
}
