import { useMemo } from 'react'
import { App, Button, Space, Table, Tag } from 'antd'
import { CopyOutlined, ExportOutlined, FolderOpenOutlined, ReloadOutlined } from '@ant-design/icons'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

/**
 * 日志面板（审计日志 / 应用日志）。
 *
 * 为什么从「服务配置」搬到这里：这两个页签原先挂在**一级页签「SMB」**下面，
 * 但应用运行日志跟 SMB 毫无关系、审计日志也是全应用级（不只 SMB 操作）——
 * 挂在协议配置里属于错位。搬到应用设置页后，与「关于 / 版本 / 项目地址」同处一处。
 *
 * 每个面板自带 useQuery：搬出来之后不再搭 Settings 那条「一次并发拉 6 项」的大查询，
 * 免得只为看日志也把 SMB 配置/快照/预设全拉一遍。
 */

interface AuditRow {
  /** 渲染 key：原始行在末尾 200 行窗口里的序号（antd Table 的 rowKey 用函数取下标已废弃） */
  key: string
  ts?: string
  action?: string
  target?: string
  result?: string
  detail?: string
}

/** 审计日志：JSONL → 结构化行（时间/操作/对象/结果/原因），末 200 条 */
export function AuditLogPanel() {
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const refreshTick = useUiStore((s) => s.refreshTick)
  const { data: audit = '', error } = useQuery({
    queryKey: ['audit-log'],
    queryFn: () => api.system.auditLog(),
  })

  const rows = useMemo<AuditRow[]>(() => {
    const lines = audit
      .split(/\r?\n/)
      .filter((l) => l.trim())
      .slice(-200)
    return lines.map((l, i) => {
      try {
        return { ...(JSON.parse(l) as AuditRow), key: String(i) }
      } catch {
        // 非 JSONL 行（历史格式/被截断的行）也要显示，不丢内容
        return { key: String(i), action: l }
      }
    })
  }, [audit])

  const load = () => {
    void queryClient.invalidateQueries({ queryKey: ['audit-log'] }).catch(() => undefined)
  }
  useTickEffect(refreshTick, load)

  const exportAudit = () => {
    const blob = new Blob([audit], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `audit-${Date.now()}.log`
    a.click()
    URL.revokeObjectURL(url)
  }

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

  const columns = [
    {
      title: '时间',
      dataIndex: 'ts',
      width: 170,
      render: (v?: string) => (
        <span className="text-fog">{(v || '').replace('T', ' ').slice(0, 19)}</span>
      ),
    },
    { title: '操作', dataIndex: 'action', width: 120, render: (v?: string) => v ?? '-' },
    { title: '对象', dataIndex: 'target', ellipsis: true },
    {
      title: '原因',
      dataIndex: 'detail',
      ellipsis: true,
      render: (v?: string) => (v ? <span className="text-red-500">{v}</span> : ''),
    },
    {
      title: '结果',
      dataIndex: 'result',
      width: 80,
      render: (v?: string) => (
        <Tag color={v === 'success' ? 'green' : 'red'} style={{ margin: 0 }}>
          {v === 'success' ? '成功' : '失败'}
        </Tag>
      ),
    },
  ]

  return (
    <div className="glass-card p-4 mb-3" data-testid="audit-log-panel">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-medium">审计日志</span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
          <Button size="small" icon={<CopyOutlined />} onClick={copyAudit}>
            复制
          </Button>
          <Button size="small" icon={<ExportOutlined />} onClick={exportAudit}>
            导出
          </Button>
        </Space>
      </div>
      <div className="text-xs text-fog mb-2">
        最近 200 条操作审计（谁做了什么、成败与原因）。运行日志见下方「应用日志」。
      </div>
      {error && !audit ? (
        <div className="text-xs text-red-500">读取审计日志失败：{(error as Error).message}</div>
      ) : (
        <Table
          dataSource={rows}
          rowKey="key"
          size="small"
          pagination={false}
          scroll={{ y: 320 }}
          locale={{ emptyText: '暂无审计记录' }}
          columns={columns}
        />
      )}
    </div>
  )
}

/** 应用日志：app.log 最近 300 行（主进程运行日志 + 渲染层错误转发） */
export function AppLogPanel() {
  const { message } = App.useApp()
  const queryClient = useQueryClient()
  const refreshTick = useUiStore((s) => s.refreshTick)
  const { data: logs = '', error } = useQuery({
    queryKey: ['app-log'],
    queryFn: async () => (api.log ? api.log.tail(300).catch(() => '') : ''),
  })

  const load = () => {
    void queryClient.invalidateQueries({ queryKey: ['app-log'] }).catch(() => undefined)
  }
  useTickEffect(refreshTick, load)

  const copyAppLog = async () => {
    try {
      await navigator.clipboard.writeText(logs)
      message.success('应用日志已复制到剪贴板')
    } catch {
      const ta = document.createElement('textarea')
      ta.value = logs
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
      message.success('应用日志已复制')
    }
  }

  const exportAppLog = () => {
    const blob = new Blob([logs], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `app-${Date.now()}.log`
    a.click()
    URL.revokeObjectURL(url)
  }

  const openLogFolder = () => {
    void api.system
      .openLogFolder()
      .then((p) => message.success(p ? `已打开日志文件夹：${p}` : '已请求打开日志文件夹'))
      .catch((e) => message.error((e as Error).message))
  }

  return (
    <div className="glass-card p-4 mb-3" data-testid="app-log-panel">
      <div className="flex items-center justify-between mb-2">
        <span className="text-sm font-medium">应用日志</span>
        <Space>
          <Button size="small" icon={<FolderOpenOutlined />} onClick={openLogFolder}>
            打开日志文件夹
          </Button>
          <Button size="small" icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
          <Button size="small" icon={<CopyOutlined />} onClick={copyAppLog}>
            复制
          </Button>
          <Button size="small" icon={<ExportOutlined />} onClick={exportAppLog}>
            导出
          </Button>
        </Space>
      </div>
      <div className="text-xs text-fog mb-2">
        显示 app.log 最近 300 行（%APPDATA%\WinSharePanel\logs\，2MB×3 轮转）；
        含主进程运行日志与渲染层错误，报错后可在此复制完整堆栈。
      </div>
      {error ? (
        <div className="text-xs text-red-500">读取应用日志失败：{(error as Error).message}</div>
      ) : (
        <pre className="text-xs bg-white/40 dark:bg-white/5 p-3 rounded-card max-h-96 overflow-auto whitespace-pre-wrap">
          {logs || '暂无日志'}
        </pre>
      )}
    </div>
  )
}
