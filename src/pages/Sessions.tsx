import { useEffect, useRef, useState } from 'react'
import { Table, Tabs, Tag, App, Button, Popconfirm, Select, Space, Empty } from 'antd'
import {
  ReloadOutlined,
  DisconnectOutlined,
  PauseOutlined,
  PlayCircleOutlined,
} from '@ant-design/icons'
import dayjs from 'dayjs'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, call } from '../api'
import type { SmbSession, SmbOpenFile, ProtocolSession } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

const INTERVAL_OPTIONS = [
  { label: '1s', value: 1 },
  { label: '5s', value: 5 },
  { label: '10s', value: 10 },
  { label: '30s', value: 30 },
]

// SMB SmbSession → 统一 ProtocolSession（sessionId 复用 clientUserName，供断开调用）
function smbToSession(s: SmbSession): ProtocolSession {
  return {
    protocol: 'smb',
    sessionId: s.clientUserName,
    clientUserName: s.clientUserName,
    clientComputerName: s.clientComputerName,
    sessionStartTime: s.sessionStartTime,
    clientOpenFiles: s.clientOpenFiles,
    clientIdleTime: s.clientIdleTime,
    bytesReceived: s.bytesReceived,
    bytesSent: s.bytesSent,
  }
}

type SessionProto = 'smb' | 'nfs' | 'ftp' | 'webdav'

interface SessionsData {
  sessions: ProtocolSession[]
  files: SmbOpenFile[]
}

export default function Sessions() {
  const { message, modal } = App.useApp()
  const [intervalSec, setIntervalSec] = useState(5)
  const [paused, setPaused] = useState(false)
  const [countdown, setCountdown] = useState(5)
  const [activeProto, setActiveProto] = useState<SessionProto>('smb')

  const selectedSessions = useUiStore((s) => s.selectedSessions)
  const setSelectedSessions = useUiStore((s) => s.setSelectedSessions)
  const sessionCloseTick = useUiStore((s) => s.sessionCloseTick)

  const queryClient = useQueryClient()
  const prevUsersRef = useRef<Set<string>>(new Set())
  const lastBalloonRef = useRef<Record<string, number>>({})

  // 检测新增会话并触发托盘气泡（仅在非首次加载时，避免冷启动轰炸）
  const detectNewSessions = (next: ProtocolSession[]) => {
    const nextUsers = new Set(next.map((s) => s.clientUserName).filter(Boolean))
    if (prevUsersRef.current.size === 0) {
      prevUsersRef.current = nextUsers
      return
    }
    const added: string[] = []
    const now = Date.now()
    nextUsers.forEach((u) => {
      if (!prevUsersRef.current.has(u)) {
        // 同用户 60s 内不重复气泡
        const last = lastBalloonRef.current[u] || 0
        if (now - last > 60000) {
          added.push(u)
          lastBalloonRef.current[u] = now
        }
      }
    })
    prevUsersRef.current = nextUsers
    if (added.length > 0) {
      const preview = added.slice(0, 3).join(', ')
      const body = `新增 ${added.length} 个连接：${preview}${added.length > 3 ? ' 等' : ''}`
      const title = activeProto === 'smb' ? '新 SMB 会话' : '新 NFS 会话'
      call(() => api.window.showBalloon(title, body)).catch(() => {
        // 静默
      })
    }
  }

  // B1 试点：数据层 react-query。轮询/并发去重由 query 承担（替代手写 load+inflightRef），
  // 协议切换=切 queryKey，暂停=interval false。
  const { data, isFetching, error, refetch } = useQuery<SessionsData>({
    queryKey: ['sessions', activeProto],
    queryFn: async () => {
      if (activeProto === 'smb') {
        const [s, f] = await Promise.all([api.session.list(), api.session.files()])
        const unified = s.map(smbToSession)
        detectNewSessions(unified)
        return { sessions: unified, files: f }
      }
      if (activeProto === 'nfs') {
        const list = await api.adapter.sessions('nfs')
        detectNewSessions(list)
        return { sessions: list, files: [] }
      }
      // FTP/WebDAV 无原生会话 API
      return { sessions: [], files: [] }
    },
    refetchInterval: paused ? false : intervalSec * 1000,
    placeholderData: keepPreviousData,
    staleTime: 0,
  })
  const sessions = data?.sessions ?? []
  const files = data?.files ?? []

  // 与原语义一致：首轮加载失败提示（有数据后的轮询失败静默，列表保留旧数据）
  useEffect(() => {
    if (error && !data) message.error((error as Error).message)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- message 非组件态；error/data 引用稳定于单次状态迁移
  }, [error, data])

  // 倒计时展示（1s 归零即回卷）；真实轮询由 refetchInterval 驱动，二者解耦避免每秒重建副作用
  useEffect(() => {
    const id = window.setInterval(() => {
      setCountdown((c) => (c <= 1 ? intervalSec : c - 1))
    }, 1000)
    return () => window.clearInterval(id)
  }, [intervalSec])

  // 切换协议时重置已选与气泡基线（zustand 为外部 store，非组件 setState）
  useEffect(() => {
    setSelectedSessions([])
    prevUsersRef.current = new Set()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 仅协议切换时重置
  }, [activeProto])

  const refreshNow = async () => {
    setCountdown(intervalSec)
    const r = await refetch()
    if (r.isError) message.error(String((r.error as Error)?.message ?? '刷新失败'))
  }
  const invalidate = () => {
    // .catch 吞掉 unhandled rejection：卸载竞态（如测试 cleanup）时 refetch 会被取消并 reject
    void queryClient.invalidateQueries({ queryKey: ['sessions', activeProto] }).catch(() => {})
  }

  // 统一断开：按协议路由
  const closeOne = (sessionId: string): Promise<void> => {
    if (activeProto === 'smb') return call(() => api.session.close(sessionId))
    return call(() => api.adapter.closeSession('nfs', sessionId))
  }

  const doBatchClose = async (ids: string[]) => {
    if (!ids.length) return
    const results = await Promise.allSettled(ids.map((id) => closeOne(id)))
    const failed = results.filter((r) => r.status === 'rejected')
    if (failed.length) {
      message.error(`${ids.length - failed.length} 个成功，${failed.length} 个失败`)
    } else {
      message.success(`已断开 ${ids.length} 个会话`)
    }
    setSelectedSessions([])
    invalidate()
  }

  // hotkey: Del 批量断开（跳过首次挂载）
  useTickEffect(sessionCloseTick, () => {
    if (!selectedSessions.length) return
    modal.confirm({
      title: '批量断开会话',
      content: `将对 ${selectedSessions.length} 个会话执行强制断开。`,
      okText: '断开',
      okType: 'danger',
      cancelText: '取消',
      onOk: () => doBatchClose(selectedSessions),
    })
  })

  const handleClose = async (sessionId: string) => {
    try {
      await closeOne(sessionId)
      message.success('已断开')
      invalidate()
    } catch (e) {
      message.error((e as Error).message)
    }
  }
  const handleCloseFile = async (id: string) => {
    try {
      await call(() => api.session.closeFile(id))
      message.success('已关闭')
      invalidate()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const fmtTime = (v: string) => (v ? dayjs(v).format('YYYY-MM-DD HH:mm:ss') : '-')

  const sessionColumns = [
    { title: '用户/客户端', dataIndex: 'clientUserName', width: 160 },
    { title: '计算机', dataIndex: 'clientComputerName', width: 160 },
    {
      title: '开始时间',
      dataIndex: 'sessionStartTime',
      render: (v: string) => fmtTime(v),
    },
    { title: '打开文件', dataIndex: 'clientOpenFiles', width: 90 },
    { title: '空闲(秒)', dataIndex: 'clientIdleTime', width: 90 },
    {
      title: '操作',
      width: 100,
      render: (_: unknown, r: ProtocolSession) => (
        <Popconfirm title="断开该会话？" onConfirm={() => handleClose(r.sessionId)}>
          <Button size="small" danger icon={<DisconnectOutlined />}>
            断开
          </Button>
        </Popconfirm>
      ),
    },
  ]

  const fileColumns = [
    { title: '路径', dataIndex: 'path', ellipsis: true },
    { title: '用户', dataIndex: 'clientUserName', width: 140 },
    { title: '计算机', dataIndex: 'clientComputerName', width: 140 },
    { title: '锁', dataIndex: 'lockCount', width: 60 },
    {
      title: '操作',
      width: 100,
      render: (_: unknown, r: SmbOpenFile) => (
        <Popconfirm title="关闭该文件？" onConfirm={() => handleCloseFile(r.fileId)}>
          <Button size="small" danger>
            关闭
          </Button>
        </Popconfirm>
      ),
    },
  ]

  const selectionBar = (
    <>
      {selectedSessions.length > 0 && (
        <div className="mb-3 flex items-center gap-2">
          <span className="text-xs text-fog">已选 {selectedSessions.length}</span>
          <Button onClick={() => setSelectedSessions([])}>清空</Button>
          <Popconfirm
            title={`批量断开 ${selectedSessions.length} 个会话？`}
            onConfirm={() => doBatchClose(selectedSessions)}
          >
            <Button danger>批量断开</Button>
          </Popconfirm>
        </div>
      )}
    </>
  )

  // 协议 Tab：SMB / NFS 有会话；FTP/WebDAV 无原生会话，显示 Empty 引导
  const protoTabs: { key: SessionProto; label: string }[] = [
    { key: 'smb', label: 'SMB' },
    { key: 'nfs', label: 'NFS' },
    { key: 'ftp', label: 'FTP' },
    { key: 'webdav', label: 'WebDAV' },
  ]

  const spinning = isFetching && !data

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-2 mb-4">
        <h1 className="text-xl font-semibold">会话监控</h1>
        <Space wrap>
          <span className="text-xs text-fog">刷新间隔</span>
          <Select
            value={intervalSec}
            onChange={setIntervalSec}
            options={INTERVAL_OPTIONS}
            style={{ width: 80 }}
          />
          <Button
            icon={paused ? <PlayCircleOutlined /> : <PauseOutlined />}
            onClick={() => setPaused((p) => !p)}
          >
            {paused ? '恢复' : '暂停'}
          </Button>
          {!paused && (
            <Tag color="blue" style={{ marginLeft: 4 }}>
              {countdown}s
            </Tag>
          )}
          <Button icon={<ReloadOutlined />} onClick={refreshNow} loading={isFetching && !paused}>
            刷新
          </Button>
        </Space>
      </div>
      <Tabs
        activeKey={activeProto}
        onChange={(k) => setActiveProto(k as SessionProto)}
        items={protoTabs}
        size="small"
        className="mb-3"
      />
      <Tabs
        items={
          activeProto === 'smb'
            ? [
                {
                  key: 'sessions',
                  label: `会话 (${sessions.length})`,
                  children: (
                    <div className="glass-card p-3">
                      {selectionBar}
                      <Table
                        dataSource={sessions}
                        rowKey="sessionId"
                        loading={spinning}
                        size="middle"
                        pagination={{ pageSize: 10 }}
                        scroll={{ x: 'max-content' }}
                        rowSelection={{
                          selectedRowKeys: selectedSessions,
                          onChange: (keys) => setSelectedSessions(keys as string[]),
                        }}
                        columns={sessionColumns}
                      />
                    </div>
                  ),
                },
                {
                  key: 'files',
                  label: `打开文件 (${files.length})`,
                  children: (
                    <div className="glass-card p-3">
                      <Table
                        dataSource={files}
                        rowKey="fileId"
                        loading={spinning}
                        size="middle"
                        pagination={{ pageSize: 10 }}
                        scroll={{ x: 'max-content' }}
                        columns={fileColumns}
                      />
                    </div>
                  ),
                },
              ]
            : activeProto === 'nfs'
              ? [
                  {
                    key: 'sessions',
                    label: `NFS 会话 (${sessions.length})`,
                    children: (
                      <div className="glass-card p-3">
                        {selectionBar}
                        <Table
                          dataSource={sessions}
                          rowKey="sessionId"
                          loading={spinning}
                          size="middle"
                          pagination={{ pageSize: 10 }}
                          scroll={{ x: 'max-content' }}
                          rowSelection={{
                            selectedRowKeys: selectedSessions,
                            onChange: (keys) => setSelectedSessions(keys as string[]),
                          }}
                          columns={sessionColumns}
                          locale={{ emptyText: <Empty description="暂无 NFS 客户端会话" /> }}
                        />
                      </div>
                    ),
                  },
                ]
              : [
                  {
                    key: 'empty',
                    label: '提示',
                    children: (
                      <div className="glass-card p-3">
                        <Empty description="FTP/WebDAV 无原生会话 API。可通过 IIS 日志（%SystemDrive%\inetpub\logs\LogFiles）查看连接记录。" />
                      </div>
                    ),
                  },
                ]
        }
      />
    </div>
  )
}
