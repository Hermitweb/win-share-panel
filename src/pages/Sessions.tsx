import { useEffect, useRef, useState } from 'react'
import { Table, Tabs, Tag, App, Button, Popconfirm, Select, Space, Empty } from 'antd'
import {
  ReloadOutlined,
  DisconnectOutlined,
  PauseOutlined,
  PlayCircleOutlined
} from '@ant-design/icons'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { SmbSession, SmbOpenFile, ProtocolSession } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

const INTERVAL_OPTIONS = [
  { label: '1s', value: 1 },
  { label: '5s', value: 5 },
  { label: '10s', value: 10 },
  { label: '30s', value: 30 }
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
    bytesSent: s.bytesSent
  }
}

type SessionProto = 'smb' | 'nfs' | 'ftp' | 'webdav'

export default function Sessions() {
  const { message, modal } = App.useApp()
  // 统一会话列表（SMB/NFS 共用渲染）；SMB 打开文件仍用 SmbOpenFile
  const [sessions, setSessions] = useState<ProtocolSession[]>([])
  const [files, setFiles] = useState<SmbOpenFile[]>([])
  const [loading, setLoading] = useState(false)
  const [intervalSec, setIntervalSec] = useState(5)
  const [paused, setPaused] = useState(false)
  const [countdown, setCountdown] = useState(5)
  const [activeProto, setActiveProto] = useState<SessionProto>('smb')

  const selectedSessions = useUiStore((s) => s.selectedSessions)
  const setSelectedSessions = useUiStore((s) => s.setSelectedSessions)
  const sessionCloseTick = useUiStore((s) => s.sessionCloseTick)

  const prevUsersRef = useRef<Set<string>>(new Set())
  const lastBalloonRef = useRef<Record<string, number>>({})
  // in-flight guard：防止轮询触发的 load 与手动刷新/协议切换的 load 并发堆积，
  // 避免响应乱序覆盖与重复 PowerShell 调用
  const inflightRef = useRef(false)

  const load = async (silent = false) => {
    if (inflightRef.current) return
    inflightRef.current = true
    if (!silent) setLoading(true)
    try {
      if (activeProto === 'smb') {
        const [s, f] = await Promise.all([call(api.session.list), call(api.session.files)])
        const unified = s.map(smbToSession)
        setSessions(unified)
        setFiles(f)
        detectNewSessions(unified)
      } else if (activeProto === 'nfs') {
        // NFS 会话：adapter 统一路由
        const list = await call(() => api.adapter.sessions('nfs'))
        setSessions(list)
        setFiles([])
        detectNewSessions(list)
      } else {
        // FTP/WebDAV 无原生会话 API
        setSessions([])
        setFiles([])
      }
    } catch (e) {
      if (!silent) message.error((e as Error).message)
    } finally {
      if (!silent) setLoading(false)
      inflightRef.current = false
    }
  }

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

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeProto])

  // 实时刷新：倒计时归零触发 load（silent=true 避免 loading 抖动）
  useEffect(() => {
    if (paused) return
    const id = window.setInterval(() => {
      setCountdown((c) => {
        if (c <= 1) {
          load(true)
          return intervalSec
        }
        return c - 1
      })
    }, 1000)
    return () => window.clearInterval(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paused, intervalSec, activeProto])

  // 切换间隔时重置倒计时
  useEffect(() => {
    setCountdown(intervalSec)
  }, [intervalSec])

  // 切换协议时重置已选与基线
  useEffect(() => {
    setSelectedSessions([])
    prevUsersRef.current = new Set()
  }, [activeProto, setSelectedSessions])

  // 统一断开：按协议路由
  const closeOne = (sessionId: string): Promise<void> => {
    if (activeProto === 'smb') return call(() => api.session.close(sessionId))
    return call(() => api.adapter.closeSession('nfs', sessionId))
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
      onOk: async () => {
        const results = await Promise.allSettled(selectedSessions.map((id) => closeOne(id)))
        const failed = results.filter((r) => r.status === 'rejected')
        if (failed.length) {
          message.error(`${selectedSessions.length - failed.length} 个成功，${failed.length} 个失败`)
        } else {
          message.success(`已断开 ${selectedSessions.length} 个会话`)
        }
        setSelectedSessions([])
        load()
      }
    })
  })

  const handleClose = async (sessionId: string) => {
    try {
      await closeOne(sessionId)
      message.success('已断开')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }
  const handleCloseFile = async (id: string) => {
    try {
      await call(() => api.session.closeFile(id))
      message.success('已关闭')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const batchClose = async () => {
    if (!selectedSessions.length) return
    const results = await Promise.allSettled(selectedSessions.map((id) => closeOne(id)))
    const failed = results.filter((r) => r.status === 'rejected')
    if (failed.length) {
      message.error(`${selectedSessions.length - failed.length} 个成功，${failed.length} 个失败`)
    } else {
      message.success(`已断开 ${selectedSessions.length} 个会话`)
    }
    setSelectedSessions([])
    load()
  }

  const fmtTime = (v: string) => (v ? dayjs(v).format('YYYY-MM-DD HH:mm:ss') : '-')

  const sessionColumns = [
    { title: '用户/客户端', dataIndex: 'clientUserName', width: 160 },
    { title: '计算机', dataIndex: 'clientComputerName', width: 160 },
    {
      title: '开始时间',
      dataIndex: 'sessionStartTime',
      render: (v: string) => fmtTime(v)
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
      )
    }
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
      )
    }
  ]

  // 协议 Tab：SMB / NFS 有会话；FTP/WebDAV 无原生会话，显示 Empty 引导
  const protoTabs: { key: SessionProto; label: string }[] = [
    { key: 'smb', label: 'SMB' },
    { key: 'nfs', label: 'NFS' },
    { key: 'ftp', label: 'FTP' },
    { key: 'webdav', label: 'WebDAV' }
  ]

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">会话监控</h1>
        <Space>
          <span className="text-xs text-fog">刷新间隔</span>
          <Select
            value={intervalSec}
            onChange={setIntervalSec}
            options={INTERVAL_OPTIONS}
            style={{ width: 80 }}
          />
          <Button icon={paused ? <PlayCircleOutlined /> : <PauseOutlined />} onClick={() => setPaused((p) => !p)}>
            {paused ? '恢复' : '暂停'}
          </Button>
          {!paused && (
            <Tag color="blue" style={{ marginLeft: 4 }}>
              {countdown}s
            </Tag>
          )}
          <Button icon={<ReloadOutlined />} onClick={() => load()}>
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
                      {selectedSessions.length > 0 && (
                        <div className="mb-3 flex items-center gap-2">
                          <span className="text-xs text-fog">已选 {selectedSessions.length}</span>
                          <Button onClick={() => setSelectedSessions([])}>清空</Button>
                          <Popconfirm title={`批量断开 ${selectedSessions.length} 个会话？`} onConfirm={batchClose}>
                            <Button danger>批量断开</Button>
                          </Popconfirm>
                        </div>
                      )}
                      <Table
                        dataSource={sessions}
                        rowKey="sessionId"
                        loading={loading}
                        size="middle"
                        pagination={{ pageSize: 10 }}
                        rowSelection={{
                          selectedRowKeys: selectedSessions,
                          onChange: (keys) => setSelectedSessions(keys as string[])
                        }}
                        columns={sessionColumns}
                      />
                    </div>
                  )
                },
                {
                  key: 'files',
                  label: `打开文件 (${files.length})`,
                  children: (
                    <div className="glass-card p-3">
                      <Table
                        dataSource={files}
                        rowKey="fileId"
                        loading={loading}
                        size="middle"
                        pagination={{ pageSize: 10 }}
                        columns={fileColumns}
                      />
                    </div>
                  )
                }
              ]
            : activeProto === 'nfs'
            ? [
                {
                  key: 'sessions',
                  label: `NFS 会话 (${sessions.length})`,
                  children: (
                    <div className="glass-card p-3">
                      {selectedSessions.length > 0 && (
                        <div className="mb-3 flex items-center gap-2">
                          <span className="text-xs text-fog">已选 {selectedSessions.length}</span>
                          <Button onClick={() => setSelectedSessions([])}>清空</Button>
                          <Popconfirm title={`批量断开 ${selectedSessions.length} 个会话？`} onConfirm={batchClose}>
                            <Button danger>批量断开</Button>
                          </Popconfirm>
                        </div>
                      )}
                      <Table
                        dataSource={sessions}
                        rowKey="sessionId"
                        loading={loading}
                        size="middle"
                        pagination={{ pageSize: 10 }}
                        rowSelection={{
                          selectedRowKeys: selectedSessions,
                          onChange: (keys) => setSelectedSessions(keys as string[])
                        }}
                        columns={sessionColumns}
                        locale={{ emptyText: <Empty description="暂无 NFS 客户端会话" /> }}
                      />
                    </div>
                  )
                }
              ]
            : [
                {
                  key: 'empty',
                  label: '提示',
                  children: (
                    <div className="glass-card p-3">
                      <Empty description="FTP/WebDAV 无原生会话 API。可通过 IIS 日志（%SystemDrive%\inetpub\logs\LogFiles）查看连接记录。" />
                    </div>
                  )
                }
              ]
        }
      />
    </div>
  )
}
