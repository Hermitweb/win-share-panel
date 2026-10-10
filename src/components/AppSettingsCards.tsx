/**
 * 「应用设置」页里的五张偏好/运维卡片（原先内联在 pages/Settings.tsx 里）。
 *
 * 为什么抽出来：它们本来挂在「服务配置」页的一个页签下面，而其中开机自启、告警规则、
 * 防火墙、账号安全体检都属于**应用级**而非协议配置级。现在「应用设置」独立成页（侧栏一项），
 * 这些卡片随之搬到这里；pages/Settings 只保留服务与协议相关的内容。
 *
 * 搬迁是纯位移：卡片内部的查询 key、乐观更新、F5 tick 行为逐字保留。
 */

import { useState } from 'react'
import {
  Switch,
  InputNumber,
  Button,
  Tag,
  Table,
  Space,
  Popconfirm,
  App,
  Select,
  Card,
  Segmented,
  Alert,
} from 'antd'
import {
  ReloadOutlined,
  DeleteOutlined,
  BellOutlined,
  BulbOutlined,
  FireOutlined,
  PlusOutlined,
  RocketOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons'
import dayjs from 'dayjs'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, call } from '../api'
import type { AlertRules, FirewallRule, SecurityIssue } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useAppStore } from '../stores/appStore'
import { useTickEffect } from '../hooks/useTickEffect'
import { useResetOnKeyChange } from '../hooks/useResetOnOpen'

/** InputNumber 回调值收敛为整数（antd 的 value 允许 string） */
function toInt(v: string | number | null): number | null {
  if (v === null || v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? Math.round(n) : null
}

/** ① 使用模式（新手/专家 → appState.advancedMode）与外观主题（light/dark） */
export function UsageModeCard() {
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
export function AutoStartCard() {
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
export function AlertRulesCard() {
  const { message } = App.useApp()
  const rules = useAppStore((s) => s.state.alertRules)
  const patch = useAppStore((s) => s.patch)
  const [busy, setBusy] = useState(false)
  const [diskDraft, setDiskDraft] = useState<number | null>(rules.diskLowGb)
  // 外部值变化 → 草稿跟随：属官方「props/外部值变化时调整 state」场景，渲染期调整（P-5）
  // 比 effect 镜像少一次渲染，且不改变既有 dirty / 保存失败退回语义
  useResetOnKeyChange(`disk:${String(rules.diskLowGb)}`, () => setDiskDraft(rules.diskLowGb))
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
export function FirewallCard() {
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
export function SecurityCard() {
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
