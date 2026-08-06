import { useEffect, useState } from 'react'
import { Table, Tabs, Tag, App, Button } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { LocalUser, LocalGroup } from '../types'

export default function Users() {
  const { message } = App.useApp()
  const [users, setUsers] = useState<LocalUser[]>([])
  const [groups, setGroups] = useState<LocalGroup[]>([])
  const [loading, setLoading] = useState(false)

  const load = async () => {
    setLoading(true)
    try {
      const [u, g] = await Promise.all([call(api.user.list), call(api.user.groups)])
      setUsers(u)
      setGroups(g)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
  }, [])

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">用户与权限</h1>
        <Button icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>
      </div>
      <Tabs
        items={[
          {
            key: 'users',
            label: '本地用户',
            children: (
              <div className="glass-card p-3">
                <Table
                  dataSource={users}
                  rowKey="name"
                  loading={loading}
                  size="middle"
                  pagination={{ pageSize: 10 }}
                  columns={[
                    { title: '用户名', dataIndex: 'name', width: 160 },
                    { title: '全名', dataIndex: 'fullName' },
                    {
                      title: '状态',
                      dataIndex: 'enabled',
                      width: 90,
                      render: (v: boolean) => (
                        <Tag color={v ? 'green' : 'default'}>{v ? '启用' : '禁用'}</Tag>
                      )
                    },
                    { title: '描述', dataIndex: 'description', ellipsis: true }
                  ]}
                />
              </div>
            )
          },
          {
            key: 'groups',
            label: '本地组',
            children: (
              <div className="glass-card p-3">
                <Table
                  dataSource={groups}
                  rowKey="name"
                  loading={loading}
                  size="middle"
                  pagination={{ pageSize: 10 }}
                  columns={[
                    { title: '组名', dataIndex: 'name', width: 180 },
                    { title: '描述', dataIndex: 'description', ellipsis: true },
                    {
                      title: '成员',
                      dataIndex: 'members',
                      render: (v: string[]) => (
                        <span className="text-fog text-sm">{v.join(', ') || '-'}</span>
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
