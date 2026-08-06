import { useEffect, useState } from 'react'
import { Table, Tabs, Tag, App, Button, Popconfirm } from 'antd'
import { ReloadOutlined, DisconnectOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { SmbSession, SmbOpenFile } from '../types'

export default function Sessions() {
  const { message } = App.useApp()
  const [sessions, setSessions] = useState<SmbSession[]>([])
  const [files, setFiles] = useState<SmbOpenFile[]>([])
  const [loading, setLoading] = useState(false)

  const load = async () => {
    setLoading(true)
    try {
      const [s, f] = await Promise.all([call(api.session.list), call(api.session.files)])
      setSessions(s)
      setFiles(f)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
  }, [])

  const handleClose = async (userName: string) => {
    try {
      await call(() => api.session.close(userName))
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

  const fmtTime = (v: string) => (v ? dayjs(v).format('YYYY-MM-DD HH:mm:ss') : '-')

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">会话监控</h1>
        <Button icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>
      </div>
      <Tabs
        items={[
          {
            key: 'sessions',
            label: `会话 (${sessions.length})`,
            children: (
              <div className="glass-card p-3">
                <Table
                  dataSource={sessions}
                  rowKey="clientId"
                  loading={loading}
                  size="middle"
                  pagination={{ pageSize: 10 }}
                  columns={[
                    { title: '用户', dataIndex: 'clientUserName', width: 140 },
                    { title: '计算机', dataIndex: 'clientComputerName', width: 140 },
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
                      render: (_: unknown, r: SmbSession) => (
                        <Popconfirm title="断开该会话？" onConfirm={() => handleClose(r.clientUserName)}>
                          <Button size="small" danger icon={<DisconnectOutlined />}>
                            断开
                          </Button>
                        </Popconfirm>
                      )
                    }
                  ]}
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
                  columns={[
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
                  ]}
                />
              </div>
            )
          }
        ]}
      />
    </div>
  )
}
