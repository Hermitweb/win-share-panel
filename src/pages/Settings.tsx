import { useEffect, useMemo, useState } from 'react'
import type { ComponentType } from 'react'
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
  Card,
  Segmented,
  Alert,
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
  AimOutlined,
  BellOutlined,
  BulbOutlined,
  FireOutlined,
  PlusOutlined,
  RocketOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import dayjs from 'dayjs'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, call } from '../api'
import type {
  AlertRules,
  FirewallRule,
  PermissionPreset,
  SecurityIssue,
  SmbSnapshotMeta,
  SmbServerConfig,
} from '../types'
import ConfigPresetBar from '../components/ConfigPresetBar'
import OsCompatCard from '../components/OsCompatCard'
import { SMB_CONFIG_PRESETS } from '../utils/configPresets'
import { useUiStore } from '../stores/uiStore'
import { useAppStore } from '../stores/appStore'
import { useTickEffect } from '../hooks/useTickEffect'
import NfsSettingsPanel from '../components/NfsSettingsPanel'
import FtpSettingsPanel from '../components/FtpSettingsPanel'
import WebdavSettingsPanel from '../components/WebdavSettingsPanel'
import PresetEditor from '../components/PresetEditor'

// ===== 「一键诊断」入口的装配探测 =====
// 诊断向导由另一位成员在 src/components/DiagnoseModal.tsx 产出（本任务不实现弹窗本身）。
// 用 glob 探测"是否已合入"：已合入→命中一条、按钮可用并按需懒加载该模块；
// 尚未合入→命中集合为空，按钮禁用并写明原因——绝不留下"点了没反应"的入口，
// 也不需要等对方文件到位后再回来改这里的代码。
const diagnoseGlob = import.meta.glob('../components/DiagnoseModal.tsx') as Record<
  string,
  () => Promise<unknown>
>

/** DiagnoseModal 的最小契约：默认导出、受控 open/onClose（见 t3 交付说明） */
export interface DiagnoseModalProps {
  open: boolean
  onClose: () => void
  initialShareName?: string
}

export type DiagnoseLoader = (() => Promise<unknown>) | null

const defaultDiagnoseLoader: DiagnoseLoader = Object.values(diagnoseGlob)[0] ?? null

export interface SettingsProps {
  /**
   * 诊断向导模块加载器。默认由上方 glob 探测 DiagnoseModal 是否已合入；
   * 显式传 null 可复现"组件尚未就绪"的降级态（测试用），传自定义 loader 可注入替身。
   */
  diagnoseLoader?: DiagnoseLoader
}

export default function Settings({ diagnoseLoader = defaultDiagnoseLoader }: SettingsProps) {
  const { message, modal } = App.useApp()
  const [saving, setSaving] = useState(false)
  const [form] = Form.useForm()
  const [editPreset, setEditPreset] = useState<PermissionPreset | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState<string[]>([])

  const refreshTick = useUiStore((s) => s.refreshTick)
  // 应用级持久状态（新手/专家模式、主题、告警规则）——本页是它的写入入口
  const advancedMode = useAppStore((s) => s.state.advancedMode)
  const hydrate = useAppStore((s) => s.hydrate)
  useEffect(() => {
    // 直接深链落到本页（未经 App 冷启动）时也要拿到真实偏好；hydrate 幂等、只在挂载跑一次
    void hydrate()
  }, [hydrate])

  // === 一键诊断入口：只负责"能否打开 + 打开什么"，向导本体在 DiagnoseModal ===
  const [DiagnoseModal, setDiagnoseModal] = useState<ComponentType<DiagnoseModalProps> | null>(null)
  const [diagnoseOpen, setDiagnoseOpen] = useState(false)
  const [diagnoseLoading, setDiagnoseLoading] = useState(false)
  const openDiagnose = async () => {
    if (!diagnoseLoader) return
    setDiagnoseLoading(true)
    try {
      const mod = (await diagnoseLoader()) as { default?: ComponentType<DiagnoseModalProps> }
      const Comp = mod.default
      if (!Comp) throw new Error('诊断模块没有默认导出组件')
      setDiagnoseModal(() => Comp)
      setDiagnoseOpen(true)
    } catch (e) {
      message.error(`诊断面板打开失败：${(e as Error).message}`)
    } finally {
      setDiagnoseLoading(false)
    }
  }

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
        <Space wrap>
          {/* 诊断入口放页头而非 Tab 内：排障时第一眼就能看到；未合入时禁用并写明原因 */}
          <Button
            type="primary"
            icon={<AimOutlined />}
            loading={diagnoseLoading}
            disabled={!diagnoseLoader}
            onClick={() => void openDiagnose()}
          >
            一键诊断
          </Button>
          {!diagnoseLoader && (
            <span className="text-xs text-fog">
              诊断面板尚未就绪（src/components/DiagnoseModal.tsx 未合入），合入后此入口自动可用
            </span>
          )}
          <Button icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
        </Space>
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

                          {advancedMode ? (
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
                          ) : (
                            <div className="text-xs text-fog mb-3">
                              当前为新手模式：高级参数（未认证超时 / 会话超时 / 每连接最大会话 /
                              请求压缩等）已收起，
                              保存时不会改动这些项；需要调整请到「偏好与安全」切换到专家模式。
                            </div>
                          )}

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
          {
            key: 'ops',
            label: (
              <span>
                <BellOutlined /> 偏好与安全
              </span>
            ),
            // 批1 UX 运维控制：使用模式/主题、开机自启、告警规则、防火墙组内规则、账号安全体检
            children: (
              <div>
                <UsageModeCard />
                <AutoStartCard />
                <AlertRulesCard />
                <FirewallCard />
                <SecurityCard />
              </div>
            ),
          },
        ]}
      />
      <PresetEditor
        open={!!editPreset}
        preset={editPreset}
        onClose={() => setEditPreset(null)}
        onSuccess={load}
      />
      {/* 诊断向导本体在 DiagnoseModal（懒加载，未点开不加载），这里只负责开关 */}
      {DiagnoseModal && (
        <DiagnoseModal open={diagnoseOpen} onClose={() => setDiagnoseOpen(false)} />
      )}
    </div>
  )
}

// ============================================================
// 「偏好与安全」Tab —— 批1 UX 运维控制五块
// 统一约定：
//  · 读 = useQuery（不设 refetchInterval，不做后台轮询；只有 F5 / 卡片刷新按钮才重取），
//    失败在卡片内 Alert 显示原因 + 重试，不静默降级成空列表
//  · 写 = call() 包裹（保留主进程 code/category），成功与失败都有 message 反馈；
//    偏好类写入走 appStore.patch（乐观更新，主进程拒绝时镜像值自动回滚）
// ============================================================

/** InputNumber 回调值收敛为整数（antd 的 value 允许 string） */
function toInt(v: string | number | null): number | null {
  if (v === null || v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? Math.round(n) : null
}

/** ① 使用模式（新手/专家 → appState.advancedMode）与外观主题（light/dark） */
function UsageModeCard() {
  const { message } = App.useApp()
  const advancedMode = useAppStore((s) => s.state.advancedMode)
  const theme = useAppStore((s) => s.state.theme)
  const patch = useAppStore((s) => s.patch)
  const [busy, setBusy] = useState(false)

  const apply = async (p: { advancedMode?: boolean; theme?: 'light' | 'dark' }, done: string) => {
    if (busy) return
    setBusy(true)
    try {
      await patch(p)
      message.success(done)
    } catch (e) {
      // patch 失败时 appStore 已回滚镜像值，这里只把原因显示出来
      message.error((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card
      size="small"
      className="glass-card mb-3"
      title={
        <Space size={6}>
          <BulbOutlined />
          使用模式与外观
        </Space>
      }
    >
      <Space vertical size={10} className="w-full">
        <div>
          <div className="text-xs text-fog mb-1">使用模式</div>
          <Segmented
            disabled={busy}
            value={advancedMode ? 'expert' : 'novice'}
            onChange={(v) =>
              void apply(
                { advancedMode: v !== 'novice' },
                v !== 'novice'
                  ? '已切换到专家模式：高级参数已展开'
                  : '已切换到新手模式：高级参数已收起',
              )
            }
            options={[
              { label: '新手模式', value: 'novice' },
              { label: '专家模式', value: 'expert' },
            ]}
          />
          <div className="text-xs text-fog mt-1">
            专家模式会展开高级参数（未认证超时 / 会话超时 / 每连接最大会话 /
            请求压缩等），适合排障与调优；新手模式只显示常用项，避免误改底层参数。
            选择结果写入应用状态，重启后保持。
          </div>
        </div>
        <div>
          <div className="text-xs text-fog mb-1">外观主题</div>
          <Segmented
            disabled={busy}
            value={theme}
            onChange={(v) => void apply({ theme: v === 'dark' ? 'dark' : 'light' }, '主题已更新')}
            options={[
              { label: '浅色', value: 'light' },
              { label: '深色', value: 'dark' },
            ]}
          />
        </div>
      </Space>
    </Card>
  )
}

/** ② 开机自启：读取系统登录项真实值 → 写系统 + 持久化意愿；返回 null（平台不支持）整卡隐藏 */
function AutoStartCard() {
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const patch = useAppStore((s) => s.patch)
  const refreshTick = useUiStore((s) => s.refreshTick)
  const [pending, setPending] = useState<boolean | null>(null)
  const { data, error, refetch } = useQuery({
    queryKey: ['system-autoStart'],
    queryFn: () => api.system.autoStart(),
    staleTime: 60_000,
  })
  const reload = () => {
    void refetch().catch(() => undefined)
  }
  useTickEffect(refreshTick, reload)

  const write = async (next: boolean) => {
    if (pending !== null) return
    setPending(next) // 乐观显示目标态
    try {
      // 主进程返回"写入后读回的真实值"，以其为准（可能被组策略拒绝而未生效）
      const actual = await call(() => api.system.setAutoStart(next))
      queryClient.setQueryData(['system-autoStart'], actual)
      setPending(null)
      if (actual !== next) {
        message.warning(`系统未接受该设置，当前为${actual ? '已开启' : '已关闭'}`)
      } else {
        message.success(
          actual ? '已开启开机自启（下次登录 Windows 时自动启动本面板）' : '已关闭开机自启',
        )
      }
      try {
        // 记录用户意愿：主进程启动时据此同步登录项
        await patch({ autoStart: actual })
      } catch (e) {
        message.warning(
          `开机自启已生效，但偏好保存失败（下次启动可能回到系统状态）：${(e as Error).message}`,
        )
      }
    } catch (e) {
      // 写入失败：目标态作废并重读真实值 → 开关回到进入页面时的状态，原因可见
      setPending(null)
      reload()
      message.error(`开机自启设置失败：${(e as Error).message}`)
    }
  }

  // 平台不支持（主进程读登录项抛错 → 返回 null）：不给用户一个改不动的开关
  if (data === null) return null

  const shown = pending ?? data ?? false
  return (
    <Card
      size="small"
      className="glass-card mb-3"
      title={
        <Space size={6}>
          <RocketOutlined />
          开机自启
        </Space>
      }
    >
      {error && data === undefined ? (
        <Alert
          type="error"
          showIcon
          message={`读取开机自启状态失败：${(error as Error).message}`}
          action={
            <Button size="small" onClick={reload}>
              重试
            </Button>
          }
        />
      ) : (
        <>
          <div className="flex items-center gap-3 flex-wrap">
            <Switch
              aria-label="开机自启开关"
              checked={shown}
              disabled={data === undefined || pending !== null}
              onChange={(v) => void write(v)}
            />
            <span className="text-sm">
              {data === undefined ? '读取中…' : shown ? '已开启' : '已关闭'}
            </span>
          </div>
          <div className="text-xs text-fog mt-2">
            面向常驻服务器 / NAS 托管：开启后当前 Windows
            用户登录时自动启动本面板（仅写用户登录项，不改动共享服务与账号凭据）。
          </div>
        </>
      )}
    </Card>
  )
}

const IDLE_ALERT_OPTIONS = [
  { label: '关闭', value: 0 },
  { label: '15 分钟', value: 15 },
  { label: '30 分钟', value: 30 },
  { label: '60 分钟', value: 60 },
]

/** ③ 告警规则四项：每次只提交被改的那一项，alertRules 由主进程浅合并保留其余项 */
function AlertRulesCard() {
  const { message } = App.useApp()
  const rules = useAppStore((s) => s.state.alertRules)
  const patch = useAppStore((s) => s.patch)
  const [busy, setBusy] = useState(false)
  const [diskDraft, setDiskDraft] = useState<number | null>(rules.diskLowGb)
  useEffect(() => {
    setDiskDraft(rules.diskLowGb)
  }, [rules.diskLowGb])
  const diskDirty = diskDraft !== null && diskDraft !== rules.diskLowGb

  const save = async (p: Partial<AlertRules>, label: string) => {
    if (busy) return
    setBusy(true)
    try {
      await patch({ alertRules: p })
      message.success(`${label}已保存`)
    } catch (e) {
      if (p.diskLowGb !== undefined) setDiskDraft(rules.diskLowGb) // 草稿退回当前值
      message.error(`保存失败：${(e as Error).message}`)
    } finally {
      setBusy(false)
    }
  }
  const saveDisk = () => {
    if (!diskDirty || diskDraft === null) return
    void save({ diskLowGb: diskDraft }, '磁盘阈值')
  }

  return (
    <Card
      size="small"
      className="glass-card mb-3"
      title={
        <Space size={6}>
          <BellOutlined />
          告警规则
        </Space>
      }
    >
      <div className="flex flex-wrap gap-x-8 gap-y-3">
        <div>
          <div className="text-xs text-fog mb-1">空闲会话提醒</div>
          <Select
            aria-label="空闲会话提醒"
            style={{ width: 120 }}
            disabled={busy}
            value={rules.idleAlertMinutes ?? 0}
            options={IDLE_ALERT_OPTIONS}
            onChange={(v: number) =>
              void save({ idleAlertMinutes: v === 0 ? null : v }, '空闲提醒')
            }
          />
          <div className="text-xs text-fog mt-1">
            会话空闲超过该时长时在监控页提示（关闭=不提示）
          </div>
        </div>
        <div>
          <div className="text-xs text-fog mb-1">SMB1 接入告警</div>
          <Switch
            aria-label="SMB1 接入告警开关"
            disabled={busy}
            checked={rules.smb1Alert}
            onChange={(v) => void save({ smb1Alert: v }, 'SMB1 告警')}
          />
          <div className="text-xs text-fog mt-1">检测到旧版 SMB1 客户端接入时提醒</div>
        </div>
        <div>
          <div className="text-xs text-fog mb-1">弱口令 / 空口令告警</div>
          <Switch
            aria-label="弱口令空口令告警开关"
            disabled={busy}
            checked={rules.weakPasswordAlert}
            onChange={(v) => void save({ weakPasswordAlert: v }, '弱口令告警')}
          />
          <div className="text-xs text-fog mt-1">账号体检发现空口令 / 永不过期账号时提醒</div>
        </div>
        <div>
          <div className="text-xs text-fog mb-1">磁盘低水位阈值（GB）</div>
          <Space>
            <InputNumber
              aria-label="磁盘低水位阈值GB"
              min={1}
              max={100000}
              style={{ width: 110 }}
              disabled={busy}
              value={diskDraft}
              onChange={(v) => setDiskDraft(toInt(v))}
              onBlur={saveDisk}
              onPressEnter={saveDisk}
            />
            {diskDirty && (
              <Button size="small" disabled={busy} onClick={saveDisk}>
                保存
              </Button>
            )}
          </Space>
          <div className="text-xs text-fog mt-1">
            共享所在盘剩余空间低于该值时标黄提醒（失焦或回车即保存）
          </div>
        </div>
      </div>
      <div className="text-xs text-fog mt-2">
        改动即时写入应用状态：只覆盖所改的那一项，其余规则保持不变。
      </div>
    </Card>
  )
}

type FirewallPresetKind = 'smb' | 'ftp' | 'ftpPassive' | 'webdav' | 'quic'

const FW_PRESETS: { kind: FirewallPresetKind; label: string; note: string }[] = [
  { kind: 'smb', label: 'SMB 文件共享（445/TCP）', note: '客户端访问 \\\\计算机名\\共享 必需' },
  {
    kind: 'ftp',
    label: 'FTP 控制 + 默认被动段（21、50000-51000）',
    note: 'IIS FTP 站点常用组合',
  },
  {
    kind: 'ftpPassive',
    label: 'FTP 被动端口范围（自定义）',
    note: '应与 FTP 配置里的防火墙范围一致',
  },
  { kind: 'webdav', label: 'WebDAV（80/TCP）', note: 'HTTP；若启用 HTTPS 需另行放行 443' },
  { kind: 'quic', label: 'SMB QUIC（445/UDP）', note: '需 Server 2022 / Win11 23H2+' },
]

/** ④ 防火墙：列表/新增/删除都只作用于「WinShare Panel」组，系统规则一概不碰 */
function FirewallCard() {
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<FirewallPresetKind>('smb')
  const [passiveFrom, setPassiveFrom] = useState<number | null>(50000)
  const [passiveTo, setPassiveTo] = useState<number | null>(51000)
  const [adding, setAdding] = useState(false)
  const [removing, setRemoving] = useState<string | null>(null)
  const { data, error, isFetching, refetch } = useQuery({
    queryKey: ['firewall-rules'],
    queryFn: () => api.firewall.list(),
    staleTime: 30_000,
  })
  const rules = data ?? []
  const reload = () => {
    void refetch().catch(() => undefined)
  }
  const refreshTick = useUiStore((s) => s.refreshTick)
  useTickEffect(refreshTick, reload)

  const needRange = kind === 'ftpPassive'
  const rangeOk =
    passiveFrom !== null &&
    passiveTo !== null &&
    passiveFrom >= 1 &&
    passiveTo <= 65535 &&
    passiveFrom <= passiveTo
  const selected = FW_PRESETS.find((p) => p.kind === kind)

  const add = async () => {
    if (adding || (needRange && !rangeOk)) return
    setAdding(true)
    try {
      const opts = needRange
        ? { passiveFrom: passiveFrom ?? 0, passiveTo: passiveTo ?? 0 }
        : undefined
      // 先向主进程取该预设的期望规则（名称/端口/协议由服务端定义），再幂等 ensure——前端不自造规则名
      const desired = await call(() => api.firewall.preset(kind, opts))
      const created = await call(() => api.firewall.ensure(desired))
      await queryClient.invalidateQueries({ queryKey: ['firewall-rules'] })
      if (created.length > 0) {
        message.success(`已添加 ${created.length} 条组内规则：${created.join('、')}`)
      } else {
        message.info('该规则已在「WinShare Panel」组内，无需重复添加')
      }
    } catch (e) {
      message.error(`添加防火墙规则失败：${(e as Error).message}`)
      reload() // 可能已部分写入，以重新读到的组内规则为准
    } finally {
      setAdding(false)
    }
  }

  const remove = async (name: string) => {
    if (removing !== null) return
    setRemoving(name)
    try {
      await call(() => api.firewall.remove(name))
      message.success(`已删除组内规则「${name}」`)
      await queryClient.invalidateQueries({ queryKey: ['firewall-rules'] })
    } catch (e) {
      message.error(`删除防火墙规则失败：${(e as Error).message}`)
      reload()
    } finally {
      setRemoving(null)
    }
  }

  return (
    <Card
      size="small"
      className="glass-card mb-3"
      title={
        <Space size={6}>
          <FireOutlined />
          防火墙规则（WinShare Panel 组）
        </Space>
      }
      extra={
        <Button size="small" icon={<ReloadOutlined />} loading={isFetching} onClick={reload}>
          刷新
        </Button>
      }
    >
      {error ? (
        <Alert
          type="error"
          showIcon
          message={`读取组内规则失败：${(error as Error).message}`}
          action={
            <Button size="small" onClick={reload}>
              重试
            </Button>
          }
        />
      ) : (
        <>
          <div className="text-xs text-fog mb-2">
            只管理本应用「WinShare
            Panel」显示组内创建的规则：列表只列该组，新增与删除也只作用于该组，
            不会读取或改动系统自带、以及其他软件创建的防火墙规则。
          </div>
          <Space wrap className="mb-2">
            <Select
              aria-label="防火墙预设"
              style={{ width: 300 }}
              value={kind}
              onChange={(v: FirewallPresetKind) => setKind(v)}
              options={FW_PRESETS.map((p) => ({ label: p.label, value: p.kind }))}
            />
            {needRange && (
              <>
                <span className="text-xs text-fog">起始</span>
                <InputNumber
                  aria-label="被动起始端口"
                  min={1}
                  max={65535}
                  style={{ width: 110 }}
                  value={passiveFrom}
                  onChange={(v) => setPassiveFrom(toInt(v))}
                />
                <span className="text-xs text-fog">结束</span>
                <InputNumber
                  aria-label="被动结束端口"
                  min={1}
                  max={65535}
                  style={{ width: 110 }}
                  value={passiveTo}
                  onChange={(v) => setPassiveTo(toInt(v))}
                />
              </>
            )}
            <Button
              type="primary"
              icon={<PlusOutlined />}
              loading={adding}
              disabled={needRange && !rangeOk}
              onClick={() => void add()}
            >
              添加预设规则
            </Button>
          </Space>
          <div className="text-xs text-fog mb-2">
            {selected?.note}
            {needRange && !rangeOk && (
              <span className="text-red-500 ml-2">
                被动端口范围需落在 1-65535 且起始 ≤ 结束，填对后才能提交。
              </span>
            )}
          </div>
          <Table
            size="small"
            rowKey="name"
            pagination={false}
            dataSource={rules}
            scroll={{ x: 'max-content' }}
            locale={{ emptyText: '「WinShare Panel」组内暂无规则' }}
            columns={[
              { title: '规则名', dataIndex: 'name' },
              { title: '端口', dataIndex: 'ports', width: 140 },
              {
                title: '状态',
                dataIndex: 'enabled',
                width: 90,
                render: (v: boolean) => (v ? <Tag color="green">启用</Tag> : <Tag>已禁用</Tag>),
              },
              {
                title: '操作',
                width: 80,
                render: (_: unknown, r: FirewallRule) => (
                  <Popconfirm
                    title={`删除组内规则「${r.name}」？`}
                    description="只删除 WinShare Panel 组内这一条规则，不影响系统与其他软件的规则。"
                    okText="删除"
                    okType="danger"
                    cancelText="取消"
                    onConfirm={() => void remove(r.name)}
                  >
                    <Button
                      size="small"
                      danger
                      icon={<DeleteOutlined />}
                      loading={removing === r.name}
                    />
                  </Popconfirm>
                ),
              },
            ]}
          />
        </>
      )}
    </Card>
  )
}

const ISSUE_LEVEL: Record<SecurityIssue['level'], { color: string; label: string }> = {
  fail: { color: 'red', label: '严重' },
  warn: { color: 'orange', label: '提醒' },
}

/** ⑤ 账号安全体检：只报主进程真的探测过的项（空口令 / 永不过期 / 超 180 天未改） */
function SecurityCard() {
  const { data, error, isFetching, refetch } = useQuery({
    queryKey: ['security-report'],
    queryFn: () => api.security.report(),
    staleTime: 60_000,
  })
  const reload = () => {
    void refetch().catch(() => undefined)
  }
  const refreshTick = useUiStore((s) => s.refreshTick)
  useTickEffect(refreshTick, reload)

  return (
    <Card
      size="small"
      className="glass-card mb-3"
      title={
        <Space size={6}>
          <SafetyCertificateOutlined />
          账号安全体检
        </Space>
      }
      extra={
        <Button size="small" icon={<ReloadOutlined />} loading={isFetching} onClick={reload}>
          重新体检
        </Button>
      }
    >
      {error && !data ? (
        <Alert
          type="error"
          showIcon
          message={`体检失败：${(error as Error).message}`}
          action={
            <Button size="small" onClick={reload}>
              重试
            </Button>
          }
        />
      ) : !data ? (
        <div className="text-xs text-fog">正在枚举本地账号（Get-LocalUser）…</div>
      ) : (
        <>
          <div className="text-xs text-fog mb-2">
            已检查 {data.checked} 个启用账号
            {data.at ? ` · ${dayjs(data.at).format('YYYY-MM-DD HH:mm')} 体检` : ''}
          </div>
          {data.issues.length === 0 ? (
            <Alert
              type="success"
              showIcon
              message={`未发现空口令 / 密码永不过期 / 超 180 天未修改的账号（共检查 ${data.checked} 个启用账号）`}
            />
          ) : (
            <>
              <div className="flex flex-col gap-1">
                {data.issues.map((i) => (
                  <div
                    key={`${i.user}::${i.issue}`}
                    className={`flex items-start gap-2 sec-issue-${i.level}`}
                  >
                    <Tag color={ISSUE_LEVEL[i.level].color} style={{ margin: 0 }}>
                      {ISSUE_LEVEL[i.level].label}
                    </Tag>
                    <span className="text-xs">
                      <span className="font-medium">{i.user}</span>：{i.issue}
                    </span>
                  </div>
                ))}
              </div>
              <div className="text-xs text-fog mt-2">
                建议：为共享账号设置口令并开启密码过期策略（本地用户页可改），不再使用的账号请禁用。
              </div>
            </>
          )}
        </>
      )}
    </Card>
  )
}
