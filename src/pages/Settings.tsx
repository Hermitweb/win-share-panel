import { useEffect, useMemo, useState } from 'react'
import {
  Form,
  Switch,
  InputNumber,
  Button,
  Tag,
  Tabs,
  Table,
  Space,
  Popconfirm,
  Descriptions,
  App,
  Select,
  Tooltip,
  Upload,
  Divider,
  Collapse,
} from 'antd'
import {
  ReloadOutlined,
  PoweroffOutlined,
  DeleteOutlined,
  HistoryOutlined,
  EditOutlined,
  CopyOutlined,
  ExportOutlined,
  ImportOutlined,
  UndoOutlined,
  CaretRightOutlined,
  PauseOutlined,
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import dayjs from 'dayjs'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, call } from '../api'
import type { PermissionPreset, SmbSnapshotMeta, SmbServerConfig } from '../types'
import ConfigPresetBar from '../components/ConfigPresetBar'
import OsCompatCard from '../components/OsCompatCard'
import { SMB_CONFIG_PRESETS } from '../utils/configPresets'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'
import NfsSettingsPanel from '../components/NfsSettingsPanel'
import FtpSettingsPanel from '../components/FtpSettingsPanel'
import WebdavSettingsPanel from '../components/WebdavSettingsPanel'
import PresetEditor from '../components/PresetEditor'

export default function Settings() {
  const { message, modal } = App.useApp()
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const [editPreset, setEditPreset] = useState<PermissionPreset | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState<string[]>([])

  const refreshTick = useUiStore((s) => s.refreshTick)

  // B1：数据层 react-query 统一六项拉取；load 保留名称、语义=失效重取（12 处调用零改动）
  const queryClient = useQueryClient()
  // 版本适配：OS 功能探测（与 OsCompatCard 共享同一 query 缓存）
  const { data: osInfo } = useQuery({
    queryKey: ['os-info'],
    queryFn: () => api.system.osInfo(),
    staleTime: 5 * 60 * 1000,
  })
  const { data, error } = useQuery({
    queryKey: ['settings'],
    queryFn: async () => {
      const [c, s, p, a, snaps, al] = await Promise.all([
        api.smb.getConfig(),
        api.smb.serviceStatus(),
        api.preset.list(),
        api.system.auditLog(),
        api.smb.listSnapshots().catch(() => [] as SmbSnapshotMeta[]),
        api.log ? api.log.tail(300).catch(() => '') : Promise.resolve(''),
      ])
      return { c, s, p, a, snaps, al }
    },
  })
  const svc = data?.s ?? null
  const presets = data?.p ?? []
  const audit = data?.a ?? ''
  const appLog = data?.al ?? ''
  const snapshots = data?.snaps ?? []
  const load = () => {
    void queryClient.invalidateQueries({ queryKey: ['settings'] }).catch(() => {})
  }

  // 首轮失败提示（与原 load catch 语义一致）
  useEffect(() => {
    if (error && !data) message.error((error as Error).message)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- error/data 引用稳定于单次状态迁移
  }, [error, data])

  // 拉到配置后同步表单（setFieldsValue 为 antd 命令式 API，非组件 state）
  useEffect(() => {
    if (data) form.setFieldsValue(data.c)
  }, [data, form])

  // hotkey F5 刷新
  useTickEffect(refreshTick, load)

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

  // A4：审计日志美化为结构化行（JSONL → 时间/操作/对象/结果/原因），末 200 条
  interface AuditRow {
    ts?: string
    action?: string
    target?: string
    result?: string
    detail?: string
  }
  const auditRows = useMemo<AuditRow[]>(() => {
    const lines = audit
      .split(/\r?\n/)
      .filter((l) => l.trim())
      .slice(-200)
    return lines.map((l) => {
      try {
        return JSON.parse(l) as AuditRow
      } catch {
        return { action: l }
      }
    })
  }, [audit])

  const copyAudit = async () => {
    try {
      await navigator.clipboard.writeText(audit)
      message.success('审计日志已复制')
    } catch {
      const ta = document.createElement('textarea')
      ta.value = audit
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
      message.success('审计日志已复制')
    }
  }

  // === 应用日志（E6）：查看/复制/导出 ===
  const copyAppLog = async () => {
    try {
      await navigator.clipboard.writeText(appLog)
      message.success('应用日志已复制到剪贴板')
    } catch {
      const ta = document.createElement('textarea')
      ta.value = appLog
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
      message.success('应用日志已复制')
    }
  }

  const exportAppLog = () => {
    const blob = new Blob([appLog], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `app-${Date.now()}.log`
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
      },
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
            `导入完成：成功 ${result.imported} 个，跳过 ${result.skipped} 个。${result.errors.slice(0, 2).join('；')}${result.errors.length > 2 ? ' 等' : ''}`,
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
    accept: '.json',
  }

  const presetColumns = [
    { title: '名称', dataIndex: 'name', width: 140 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '分类',
      dataIndex: 'category',
      width: 90,
      render: (v: string | undefined) =>
        v ? <Tag color="blue">{v}</Tag> : <span className="text-fog">-</span>,
    },
    {
      title: '类型',
      dataIndex: 'builtIn',
      width: 80,
      render: (v: boolean) => (v ? <Tag>内置</Tag> : <Tag color="blue">自定义</Tag>),
    },
    {
      title: '条目',
      dataIndex: 'entries',
      width: 60,
      render: (v: { length: number } | undefined) => v?.length || 0,
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
      ),
    },
  ]

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-2 mb-4">
        <h1 className="text-xl font-semibold">服务器配置</h1>
        <Button icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>
      </div>
      {/* 版本适配环境卡：运行时功能探测（SMB/NFS/IIS/QUIC），配置页按结果降级 */}
      <OsCompatCard />
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
                        <ConfigPresetBar
                          presets={SMB_CONFIG_PRESETS}
                          onApply={async (p) => {
                            await call(() =>
                              api.smb.setConfig(p.values as Partial<SmbServerConfig>),
                            )
                            load()
                          }}
                        />
                        <Form form={form} layout="vertical">
                          <div className="text-sm font-medium mb-2 text-fog">基础协议</div>
                          <div className="flex flex-wrap gap-6 mb-3">
                            <Form.Item
                              name="enableSMB1Protocol"
                              label="SMB1"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableSMB2Protocol"
                              label="SMB2"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="enableSMB3Protocol"
                              label="SMB3"
                              valuePropName="checked"
                            >
                              <Switch />
                            </Form.Item>
                            <Form.Item
                              name="auditSmb1Access"
                              label="审计 SMB1"
                              valuePropName="checked"
                            >
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
                            <Form.Item
                              name="enableSMBQUIC"
                              label="SMB QUIC"
                              valuePropName="checked"
                              tooltip={
                                osInfo && !osInfo.features.smbQuicConfig
                                  ? '需 Server 2022 / Win11 23H2+；本机探测不支持，保存时将自动跳过'
                                  : undefined
                              }
                            >
                              <Switch disabled={osInfo ? !osInfo.features.smbQuicConfig : false} />
                            </Form.Item>
                            <Form.Item
                              name="announceServer"
                              label="声明服务器"
                              valuePropName="checked"
                            >
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
                                          { label: '优化速度', value: 'OptimizeForSpeed' },
                                          { label: '优化体积', value: 'OptimizeForSize' },
                                        ]}
                                      />
                                    </Form.Item>
                                  </div>
                                ),
                              },
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
                                <Button icon={<PauseOutlined />}>停止</Button>
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
                                  ),
                                },
                                { key: 'srt', label: '启动类型', children: svc.startType || '-' },
                                { key: 'sn', label: '服务名', children: svc.name },
                              ]}
                            />
                          )}
                          <Divider style={{ margin: '12px 0' }} />
                          <div className="text-xs text-fog">
                            提示：SMB1
                            出于安全考虑默认关闭；建议保持"要求签名"开启以防止中间人攻击。修改高级参数可能影响性能与兼容性，不确定时请点"恢复默认"。
                          </div>
                        </Form>
                      </div>
                    ),
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
                          scroll={{ x: 'max-content' }}
                          columns={presetColumns}
                        />
                      </div>
                    ),
                  },
                  {
                    key: 'audit',
                    label: '审计日志',
                    children: (
                      <div className="glass-card p-3">
                        <Space className="mb-2">
                          <Button onClick={exportAudit}>导出日志</Button>
                          <Button onClick={copyAudit}>复制</Button>
                        </Space>
                        <div className="text-xs text-fog mb-2">
                          最近 200 条操作审计（谁做了什么、成败与原因）；运行日志见"应用日志"Tab。
                        </div>
                        {auditRows.length === 0 ? (
                          <div className="text-xs text-fog p-3">暂无审计记录</div>
                        ) : (
                          <div className="max-h-96 overflow-auto text-xs bg-white/40 p-2 rounded-card">
                            {auditRows.map((r, i) => (
                              <div
                                key={i}
                                className="flex gap-2 items-center py-1 border-b border-black/5 last:border-0"
                              >
                                <span className="text-fog shrink-0">
                                  {(r.ts || '').replace('T', ' ').slice(0, 19)}
                                </span>
                                <span className="shrink-0 font-medium">{r.action ?? '-'}</span>
                                <span className="truncate flex-1">{r.target ?? ''}</span>
                                {r.detail && (
                                  <span
                                    className="text-red-500 truncate max-w-[240px]"
                                    title={r.detail}
                                  >
                                    {r.detail}
                                  </span>
                                )}
                                <Tag
                                  color={r.result === 'success' ? 'green' : 'red'}
                                  style={{ margin: 0 }}
                                >
                                  {r.result === 'success' ? '成功' : '失败'}
                                </Tag>
                              </div>
                            ))}
                          </div>
                        )}
                      </div>
                    ),
                  },
                  {
                    key: 'applog',
                    label: '应用日志',
                    children: (
                      <div className="glass-card p-3">
                        <Space className="mb-2">
                          <Button icon={<ReloadOutlined />} onClick={load}>
                            刷新
                          </Button>
                          <Button onClick={copyAppLog}>复制</Button>
                          <Button onClick={exportAppLog}>导出</Button>
                        </Space>
                        <div className="text-xs text-fog mb-2">
                          显示 app.log 最近 300
                          行（%APPDATA%\WinSharePanel\logs\，2MB×3轮转）；含主进程运行日志与渲染层错误，报错后可在此复制完整堆栈。
                        </div>
                        <pre className="text-xs bg-white/40 p-3 rounded-card max-h-96 overflow-auto whitespace-pre-wrap">
                          {appLog || '暂无日志'}
                        </pre>
                      </div>
                    ),
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
                          每次 SMB 配置写入前自动保存当前完整配置为快照（最多 20
                          份）。回滚会覆盖当前配置，且不产生新快照。
                        </p>
                        <Table
                          dataSource={snapshots}
                          rowKey="id"
                          size="middle"
                          pagination={{ pageSize: 10 }}
                          scroll={{ x: 'max-content' }}
                          locale={{ emptyText: '暂无快照' }}
                          columns={[
                            {
                              title: '时间',
                              dataIndex: 'id',
                              render: (v: string) => fmtSnapshotTs(v),
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
                              ),
                            },
                          ]}
                        />
                      </div>
                    ),
                  },
                ]}
              />
            ),
          },
          {
            key: 'nfs',
            label: 'NFS',
            children: <NfsSettingsPanel />,
          },
          {
            key: 'ftp',
            label: 'FTP',
            children: <FtpSettingsPanel />,
          },
          {
            key: 'webdav',
            label: 'WebDAV',
            children: <WebdavSettingsPanel />,
          },
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
