import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, App, Badge, Button, Modal, Popconfirm, Select, Space, Tag } from 'antd'
import {
  CopyOutlined,
  ExperimentOutlined,
  ThunderboltOutlined,
  WarningOutlined,
} from '@ant-design/icons'
import { api, call } from '../api'
import type { DiagnoseFixKind, DiagnoseItem, Share } from '../types'

/**
 * 一键诊断向导（★★ 口碑时刻）：「别人打不开我的共享」自助排查面板。
 *
 * 检查链路（服务在跑 → 端口在听 → 共享/路径有效 → NTFS 可读 → 防火墙放行 →
 * 共享权限 → 安全策略）与全部判定都在主进程 electron/services/diagnose.ts，
 * 本组件只做呈现与编排：
 * - 逐项红绿灯：pass 绿 / warn 黄 / fail 红，label 与 detail 分显；
 * - 带 fix 的异常项才给「一键修复」，Popconfirm 二次确认后调 applyFix，
 *   成功提示主进程返回的文案并自动复检（红绿灯只说"刚查过"的结果）；
 * - 无 fix 的 fail 给"下一步怎么办"的人话建议，而不是只报个错；
 * - 共享名可留空＝全局体检（主进程会把共享相关三项标为"跳过"）；
 * - 探测失败整体错误可见，绝不白屏。
 *
 * 主进程是判定与修复动作的唯一真相源：这里不猜路径、不改状态、不自作结论把行改绿。
 */

interface Props {
  open: boolean
  onClose: () => void
  /** 从共享行带入的诊断目标；不传＝留空（全局体检） */
  initialShareName?: string
}

type Status = DiagnoseItem['status']

/** 红绿灯三态映射：dot=色值，tag=antd Tag 预设色名，text=给人看的结论词 */
const STATUS_META: Record<Status, { dot: string; tag: string; text: string }> = {
  pass: { dot: 'green', tag: 'green', text: '正常' },
  warn: { dot: 'gold', tag: 'gold', text: '需关注' },
  fail: { dot: 'red', tag: 'red', text: '异常' },
}

/** 可自动修复项：把人话写进确认框，用户点下去之前就知道要动什么 */
const FIX_LABEL: Record<DiagnoseFixKind, string> = {
  'service:lanman': '启动 SMB 服务端（LanmanServer）服务',
  'firewall:smb': '添加防火墙规则：WinShare SMB (445/TCP) 入站放行',
  'acl:read-everyone': '给共享文件夹的 NTFS 权限追加 Everyone 读取',
}

/** 这类修复需按共享名反查文件夹，留空（全局体检）时无从下手 */
const FIX_NEEDS_SHARE: DiagnoseFixKind[] = ['acl:read-everyone']

/**
 * 无 fix 的 fail 项＝只能人工处理的环节，给可执行的下一步。
 * 键与 DiagnoseItem.key 对齐；未收录的新检查项走兜底文案，不会出现"只有报错没有建议"。
 */
const NEXT_STEP: Record<string, string> = {
  'port-445':
    '在服务端命令行执行 `netstat -ano | findstr :445`：若被别的程序占用，结束该进程后再诊断；' +
    '若 SMB 服务已在运行仍无监听，通常是第三方安全软件接管了共享，需在它里面放行 445 端口。',
  'share-exists':
    '共享可能已改名或被删除：回到共享列表核对名称（不区分大小写），' +
    '必要时重新创建同名共享并指向原文件夹，然后点「重新诊断」。',
  'path-exists':
    '共享指向的本地文件夹不见了（被移动/改名/换盘，或外接盘已拔掉）：' +
    '接回原磁盘恢复该文件夹，或重建共享指向现有路径。',
  'share-perms':
    '在共享列表选中它 → 打开「权限」面板，给 Everyone 或指定用户组授予「读取」；' +
    '默认只给读取最安全，确认对方要写文件再加「更改」。',
  smb1:
    '在「服务器配置 → SMB」关闭 SMB1 后重新诊断；' +
    '仍坚持要连的老设备请升级 Windows，而不是把整台机器拖回 SMB1 的风险面。',
  guest:
    '在「服务器配置 → SMB」关闭「允许来宾访问」与「不安全的来宾登录」，' +
    '再给访问者建一个带密码的本地账户并授予共享权限。',
}

const GENERIC_STEP =
  '点右下「复制诊断结果」，把清单发给管理员或客服；也可先到设置页跑一次安全体检，排除常见配置问题。'

const ADMIN_HINT =
  '提示：修复动作会改服务、防火墙或 NTFS 权限，需要管理员权限；失败时请以管理员身份重新打开本应用。'

const INTRO_DETAIL =
  '留空只查服务端与防火墙，共享相关的三项会显示「未指定共享，跳过」。' +
  '诊断只读取状态、不改动任何配置；带「一键修复」的异常项需你二次确认才会动手。'

function emptyCounts(): Record<Status, number> {
  return { pass: 0, warn: 0, fail: 0 }
}

/** 每项一行「[状态] label —— detail」，可直接粘进工单/聊天 */
function buildReport(items: DiagnoseItem[], shareName: string | undefined): string {
  const counts = emptyCounts()
  items.forEach((i) => {
    counts[i.status] += 1
  })
  const lines = items.map((i) => {
    const fixable = i.fix ? `（可一键修复：${FIX_LABEL[i.fix]}）` : ''
    return `[${STATUS_META[i.status].text}] ${i.label} —— ${i.detail}${fixable}`
  })
  const steps = items
    .filter((i) => i.status === 'fail' && !i.fix)
    .map((i) => `- ${i.label}：${NEXT_STEP[i.key] ?? GENERIC_STEP}`)
  return [
    `【WinShare 一键诊断】${new Date().toLocaleString()}`,
    shareName ? `范围：共享 ${shareName}` : '范围：全局体检（未指定共享）',
    `小结：${counts.pass} 项通过 / ${counts.fail} 项异常` +
      (counts.warn ? ` / ${counts.warn} 项需关注` : ''),
    '',
    '检查项：',
    ...lines,
    ...(steps.length ? ['', '下一步怎么办：', ...steps] : []),
  ].join('\n')
}

/** 剪贴板（与 ErrorBoundary 同一策略：Clipboard API 不可用时退到 textarea + execCommand） */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // 无权限/非安全上下文，落到下面的兜底路径
  }
  try {
    const ta = document.createElement('textarea')
    ta.value = text
    ta.style.position = 'fixed'
    ta.style.opacity = '0'
    document.body.appendChild(ta)
    ta.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(ta)
    return ok
  } catch {
    return false
  }
}

export default function DiagnoseModal({ open, onClose, initialShareName }: Props) {
  const { message } = App.useApp()
  const [shareName, setShareName] = useState<string | undefined>(initialShareName)
  const [shares, setShares] = useState<Share[]>([])
  const [items, setItems] = useState<DiagnoseItem[] | null>(null)
  const [running, setRunning] = useState(false)
  const [runError, setRunError] = useState<string | null>(null)
  const [fixingKey, setFixingKey] = useState<string | null>(null)
  // 只认最后一次请求：快速切换共享、修复复检时，迟到的旧结果不得覆盖新结果
  const runIdRef = useRef(0)

  // 打开时带入目标共享、清空上次结果，并拉真实共享列表喂下拉
  useEffect(() => {
    if (!open) return
    runIdRef.current += 1
    setShareName(initialShareName)
    setItems(null)
    setRunError(null)
    setRunning(false)
    let dead = false
    call(() => api.adapter.list('smb'))
      .then((list) => {
        if (!dead) setShares(list)
      })
      .catch(() => {
        // 拉不到共享列表不阻断全局体检，留空照样能诊断
        if (!dead) setShares([])
      })
    return () => {
      dead = true
    }
  }, [open, initialShareName])

  const runDiagnose = useCallback(async (target: string | undefined) => {
    const id = (runIdRef.current += 1)
    setRunning(true)
    setRunError(null)
    try {
      const res = await call(() => api.diagnose.run(target || undefined))
      if (runIdRef.current === id) setItems(res)
    } catch (e) {
      // 探测失败整体可见：错误条 + 重试入口，绝不留白屏
      if (runIdRef.current === id) {
        setItems(null)
        setRunError((e as Error).message || '诊断失败')
      }
    } finally {
      if (runIdRef.current === id) setRunning(false)
    }
  }, [])

  const handleFix = useCallback(
    async (item: DiagnoseItem) => {
      // fix 先收窄成局部常量：闭包里再读 item.fix 会被 TS 重新视为可选
      const fix = item.fix
      if (!fix) return
      setFixingKey(item.key)
      try {
        const done = await call(() =>
          api.diagnose.applyFix(fix, { shareName: shareName || undefined }),
        )
        message.success(done || '已执行修复')
        // 复检：红绿灯由主进程重新判定，不在这里自行把该行改绿
        await runDiagnose(shareName)
      } catch (e) {
        // 修复失败同样不吞：说明原因，该行维持异常，用户知道问题还在
        message.error((e as Error).message || '修复失败')
      } finally {
        setFixingKey(null)
      }
    },
    [message, runDiagnose, shareName],
  )

  const handleCopy = useCallback(async () => {
    if (!items?.length) return
    const ok = await copyText(buildReport(items, shareName))
    if (ok) message.success('诊断结果已复制，可直接粘贴发给管理员或客服')
    else message.error('复制失败，请手动截图')
  }, [items, message, shareName])

  const counts = useMemo(() => {
    const c = emptyCounts()
    items?.forEach((i) => {
      c[i.status] += 1
    })
    return c
  }, [items])

  const hasFixable = !!items?.some((i) => i.fix && i.status !== 'pass')
  const fixing = fixingKey !== null
  const shareOptions = shares.map((s) => ({
    value: s.name,
    label: `${s.name}${s.description ? ` — ${s.description}` : ''}`,
  }))

  return (
    <Modal
      open={open}
      title={
        <Space>
          <ExperimentOutlined />
          一键诊断：别人打不开我的共享？
        </Space>
      }
      onCancel={onClose}
      width={760}
      footer={
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-fog">
            {items?.length ? (
              <>
                {counts.pass} 项通过 / {counts.fail} 项异常
                {counts.warn > 0 && ` · ${counts.warn} 项需关注`}
                {counts.fail === 0 && counts.warn === 0 && ' · 暂未发现阻断问题'}
              </>
            ) : (
              '尚未诊断'
            )}
          </span>
          <Space>
            <Button icon={<CopyOutlined />} disabled={!items?.length} onClick={handleCopy}>
              复制诊断结果
            </Button>
            <Button type="primary" onClick={onClose}>
              关闭
            </Button>
          </Space>
        </div>
      }
    >
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Select
          allowClear
          showSearch
          style={{ minWidth: 260, flex: 1 }}
          placeholder="选择共享（留空＝全局体检）"
          value={shareName}
          onChange={(v) => {
            // 换目标＝换范围：清空旧逐项结论，避免拿上一个范围的绿灯套到新目标上
            setShareName(v)
            setItems(null)
            setRunError(null)
          }}
          disabled={running || fixing}
          options={shareOptions}
          optionFilterProp="label"
          notFoundContent="暂无共享：留空做全局体检即可"
        />
        <Button
          type="primary"
          icon={<ThunderboltOutlined />}
          loading={running}
          disabled={fixing}
          onClick={() => void runDiagnose(shareName)}
        >
          {items ? '重新诊断' : '开始诊断'}
        </Button>
      </div>

      {runError && (
        <Alert
          type="error"
          showIcon
          icon={<WarningOutlined />}
          className="mb-3"
          title="诊断没能完成"
          description={runError}
          action={
            <Button size="small" onClick={() => void runDiagnose(shareName)}>
              重试
            </Button>
          }
        />
      )}

      {!items && !running && !runError && (
        <Alert
          type="info"
          showIcon
          title="选好共享（或留空做全局体检）后点「开始诊断」"
          description={INTRO_DETAIL}
        />
      )}

      {items && items.length === 0 && !runError && (
        <Alert type="warning" showIcon title="没有返回任何检查项，请重试或反馈给开发者。" />
      )}

      {running && items === null && (
        <div className="py-6 text-center text-sm text-fog">正在逐项探测，请稍候…</div>
      )}

      {items && items.length > 0 && (
        <ul className="m-0 list-none space-y-2 p-0">
          {items.map((it) => {
            const meta = STATUS_META[it.status]
            const fix = it.fix
            // ACL 修复要按共享名反查文件夹，留空时不可用
            const needShare = fix !== undefined && FIX_NEEDS_SHARE.includes(fix) && !shareName
            return (
              <li
                key={it.key}
                data-key={it.key}
                data-status={it.status}
                data-color={meta.tag}
                className="rounded-xl border border-black/5 bg-white/40 px-3 py-2"
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge color={meta.dot} />
                      <span className="font-medium text-ink">{it.label}</span>
                      <Tag className="m-0" color={meta.tag}>
                        {meta.text}
                      </Tag>
                    </div>
                    {/* detail 与 label 分显：详情可能很长（路径/状态），另起一行不挤压标题 */}
                    <div className="mt-1 break-all text-xs text-fog">{it.detail}</div>
                    {it.status === 'fail' && fix === undefined && (
                      <div className="mt-2 rounded-lg bg-mist/70 px-2 py-1 text-xs text-ink">
                        下一步：{NEXT_STEP[it.key] ?? GENERIC_STEP}
                      </div>
                    )}
                  </div>

                  {/* 只有"主进程标了 fix 且不是 pass"的行才出按钮：无 fix 的异常只能人工处理 */}
                  {fix !== undefined && it.status !== 'pass' && (
                    <Popconfirm
                      title="确认自动修复？"
                      description={
                        <div style={{ maxWidth: 300 }}>
                          <div>将执行：{FIX_LABEL[fix]}</div>
                          <div className="mt-1">
                            {needShare
                              ? '该项需要先选择共享，才能定位要修改的文件夹。'
                              : '完成后会自动重新诊断确认变绿。'}
                          </div>
                        </div>
                      }
                      okText="确认修复"
                      cancelText="取消"
                      okButtonProps={{ danger: true, disabled: needShare }}
                      onConfirm={() => void handleFix(it)}
                    >
                      <Button
                        size="small"
                        type="primary"
                        danger
                        icon={<ThunderboltOutlined />}
                        loading={fixingKey === it.key}
                        disabled={running || fixing}
                      >
                        一键修复
                      </Button>
                    </Popconfirm>
                  )}
                </div>
              </li>
            )
          })}
        </ul>
      )}

      {hasFixable && <div className="mt-3 text-xs text-fog">{ADMIN_HINT}</div>}
    </Modal>
  )
}
