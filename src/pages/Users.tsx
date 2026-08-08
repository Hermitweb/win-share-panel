import { useEffect, useState } from 'react'
import { Table, Tabs, Tag, App, Button, Space, Tooltip, Popconfirm, Input, Switch, Select, Modal } from 'antd'
import {
  ReloadOutlined,
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  TeamOutlined,
  UserOutlined,
  LockOutlined,
  UnlockOutlined,
  KeyOutlined,
  SettingOutlined
} from '@ant-design/icons'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { LocalUser, LocalGroup } from '../types'
import PermissionMatrix from '../components/PermissionMatrix'
import UserCreateModal from '../components/UserCreateModal'
import UserDetailDrawer from '../components/UserDetailDrawer'
import GroupCreateModal from '../components/GroupCreateModal'
import GroupManageModal from '../components/GroupManageModal'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from '../hooks/useTickEffect'

export default function Users() {
  const { message, modal } = App.useApp()
  const [users, setUsers] = useState<LocalUser[]>([])
  const [groups, setGroups] = useState<LocalGroup[]>([])
  const [loading, setLoading] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [onlyEnabled, setOnlyEnabled] = useState(false)
  const [searchField, setSearchField] = useState<'name' | 'all'>('name')

  // 模态框状态
  const [createUserOpen, setCreateUserOpen] = useState(false)
  const [editUser, setEditUser] = useState<LocalUser | null>(null)
  const [createGroupOpen, setCreateGroupOpen] = useState(false)
  const [manageGroup, setManageGroup] = useState<LocalGroup | null>(null)

  // 批量操作状态
  const [selectedUsers, setSelectedUsers] = useState<string[]>([])
  const [batchGroupOpen, setBatchGroupOpen] = useState(false)
  const [batchGroupName, setBatchGroupName] = useState<string | null>(null)

  const refreshTick = useUiStore((s) => s.refreshTick)

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

  // hotkey F5 刷新
  useTickEffect(refreshTick, () => {
    load()
  })

  const handleDeleteUser = (u: LocalUser) => {
    modal.confirm({
      title: `删除用户 ${u.name}？`,
      content: '此操作不可恢复，与该用户关联的共享权限将失效。',
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        try {
          await call(() => api.user.delete(u.name))
          message.success('用户已删除')
          load()
        } catch (e) {
          message.error((e as Error).message)
        }
      }
    })
  }

  const handleToggleUser = async (u: LocalUser) => {
    try {
      if (u.enabled) {
        await call(() => api.user.disable(u.name))
        message.success('已禁用')
      } else {
        await call(() => api.user.enable(u.name))
        message.success('已启用')
      }
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleDeleteGroup = (g: LocalGroup) => {
    modal.confirm({
      title: `删除组 ${g.name}？`,
      content: '此操作不可恢复。组成员将失去该组的所有权限。',
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        try {
          await call(() => api.group.delete(g.name))
          message.success('组已删除')
          load()
        } catch (e) {
          message.error((e as Error).message)
        }
      }
    })
  }

  // === 批量操作 ===
  const handleBatchEnable = async () => {
    const targets = [...selectedUsers]
    modal.confirm({
      title: `批量启用 ${targets.length} 个用户？`,
      okText: '启用',
      cancelText: '取消',
      onOk: async () => {
        const failed: string[] = []
        for (const name of targets) {
          try {
            await call(() => api.user.enable(name))
          } catch {
            failed.push(name)
          }
        }
        message.success(`已启用 ${targets.length - failed.length} 个用户${failed.length ? `，${failed.length} 个失败` : ''}`)
        setSelectedUsers([])
        load()
      }
    })
  }

  const handleBatchDisable = async () => {
    const targets = [...selectedUsers]
    modal.confirm({
      title: `批量禁用 ${targets.length} 个用户？`,
      okText: '禁用',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        const failed: string[] = []
        for (const name of targets) {
          try {
            await call(() => api.user.disable(name))
          } catch {
            failed.push(name)
          }
        }
        message.success(`已禁用 ${targets.length - failed.length} 个用户${failed.length ? `，${failed.length} 个失败` : ''}`)
        setSelectedUsers([])
        load()
      }
    })
  }

  const handleBatchDelete = () => {
    const targets = [...selectedUsers]
    modal.confirm({
      title: `批量删除 ${targets.length} 个用户？`,
      content: '此操作不可恢复，与该用户关联的共享权限将失效。',
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        const failed: string[] = []
        for (const name of targets) {
          try {
            await call(() => api.user.delete(name))
          } catch {
            failed.push(name)
          }
        }
        message.success(`已删除 ${targets.length - failed.length} 个用户${failed.length ? `，${failed.length} 个失败` : ''}`)
        setSelectedUsers([])
        load()
      }
    })
  }

  const handleBatchAssignConfirm = async () => {
    if (!batchGroupName || selectedUsers.length === 0) return
    const targets = [...selectedUsers]
    const groupName = batchGroupName
    const failed: string[] = []
    for (const name of targets) {
      try {
        await call(() => api.group.addMember(groupName, name))
      } catch {
        failed.push(name)
      }
    }
    message.success(`已分配 ${targets.length - failed.length} 个用户到组「${groupName}」${failed.length ? `，${failed.length} 个失败` : ''}`)
    setBatchGroupOpen(false)
    setBatchGroupName(null)
    setSelectedUsers([])
    load()
  }

  const filteredUsers = users.filter((u) => {
    if (onlyEnabled && !u.enabled) return false
    const q = keyword.trim().toLowerCase()
    if (!q) return true
    if (searchField === 'name') return u.name.toLowerCase().includes(q)
    return (
      u.name.toLowerCase().includes(q) ||
      (u.fullName || '').toLowerCase().includes(q) ||
      (u.description || '').toLowerCase().includes(q) ||
      u.groups.some((g) => g.toLowerCase().includes(q))
    )
  })

  const fmtTime = (ts: string): string => {
    if (!ts) return '-'
    const d = dayjs(ts)
    return d.isValid() ? d.format('YYYY-MM-DD HH:mm') : ts
  }

  const userColumns = [
    { title: '用户名', dataIndex: 'name', width: 140 },
    { title: '全名', dataIndex: 'fullName', width: 140, ellipsis: true },
    {
      title: '状态',
      dataIndex: 'enabled',
      width: 80,
      render: (v: boolean, r: LocalUser) => (
        <Tooltip title={v ? '点击禁用' : '点击启用'}>
          <Tag
            color={v ? 'green' : 'default'}
            style={{ cursor: 'pointer' }}
            onClick={() => handleToggleUser(r)}
          >
            {v ? '启用' : '禁用'}
          </Tag>
        </Tooltip>
      )
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true
    },
    {
      title: '所属组',
      dataIndex: 'groups',
      width: 200,
      render: (v: string[]) => (
        <Space size={0} wrap>
          {(v || []).slice(0, 3).map((g) => (
            <Tag key={g}>{g}</Tag>
          ))}
          {v && v.length > 3 && <Tag>+{v.length - 3}</Tag>}
          {(!v || v.length === 0) && <span className="text-fog">-</span>}
        </Space>
      )
    },
    {
      title: '上次登录',
      dataIndex: 'lastLogon',
      width: 130,
      render: (v: string) => <span className="text-fog text-xs">{fmtTime(v)}</span>
    },
    {
      title: '操作',
      width: 180,
      render: (_: unknown, r: LocalUser) => (
        <Space>
          <Tooltip title="编辑属性">
            <Button
              size="small"
              icon={<EditOutlined />}
              onClick={() => setEditUser(r)}
            />
          </Tooltip>
          <Tooltip title={r.enabled ? '禁用账号' : '启用账号'}>
            <Button
              size="small"
              icon={r.enabled ? <LockOutlined /> : <UnlockOutlined />}
              onClick={() => handleToggleUser(r)}
            />
          </Tooltip>
          <Popconfirm
            title={`删除用户 ${r.name}？`}
            description="此操作不可恢复"
            okText="删除"
            okType="danger"
            cancelText="取消"
            onConfirm={() => handleDeleteUser(r)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      )
    }
  ]

  const groupColumns = [
    { title: '组名', dataIndex: 'name', width: 160 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '成员数',
      dataIndex: 'members',
      width: 80,
      render: (v: { length: number } | undefined) => v?.length || 0
    },
    {
      title: '成员预览',
      dataIndex: 'members',
      render: (v: { name: string }[] | undefined) => (
        <span className="text-fog text-sm">
          {(v || []).slice(0, 5).map((m) => m.name).join(', ')}
          {v && v.length > 5 ? ` 等 ${v.length} 个` : ''}
          {(!v || v.length === 0) && '-'}
        </span>
      )
    },
    {
      title: '操作',
      width: 160,
      render: (_: unknown, r: LocalGroup) => (
        <Space>
          <Tooltip title="管理成员">
            <Button
              size="small"
              icon={<TeamOutlined />}
              onClick={() => setManageGroup(r)}
            />
          </Tooltip>
          <Popconfirm
            title={`删除组 ${r.name}？`}
            description="此操作不可恢复"
            okText="删除"
            okType="danger"
            cancelText="取消"
            onConfirm={() => handleDeleteGroup(r)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      )
    }
  ]

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold">用户与权限</h1>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
        </Space>
      </div>
      <Tabs
        items={[
          {
            key: 'users',
            label: (
              <span>
                <UserOutlined /> 本地用户
              </span>
            ),
            children: (
              <div className="glass-card p-3">
                {selectedUsers.length > 0 && (
                  <div className="mb-3 flex items-center gap-2 p-2 rounded bg-blue-50 dark:bg-blue-900/20">
                    <Tag color="blue">已选 {selectedUsers.length} 个用户</Tag>
                    <Space size="small">
                      <Button size="small" icon={<UnlockOutlined />} onClick={handleBatchEnable}>
                        批量启用
                      </Button>
                      <Button size="small" icon={<LockOutlined />} onClick={handleBatchDisable}>
                        批量禁用
                      </Button>
                      <Button
                        size="small"
                        icon={<TeamOutlined />}
                        onClick={() => setBatchGroupOpen(true)}
                      >
                        分配到组
                      </Button>
                      <Popconfirm
                        title={`批量删除 ${selectedUsers.length} 个用户？`}
                        okText="删除"
                        okType="danger"
                        cancelText="取消"
                        onConfirm={handleBatchDelete}
                      >
                        <Button size="small" danger icon={<DeleteOutlined />}>
                          批量删除
                        </Button>
                      </Popconfirm>
                      <Button size="small" type="link" onClick={() => setSelectedUsers([])}>
                        取消选择
                      </Button>
                    </Space>
                  </div>
                )}
                <div className="mb-3 flex items-center gap-2">
                  <Input
                    allowClear
                    placeholder={searchField === 'name' ? '搜索用户名' : '搜索用户名/全名/描述/所属组'}
                    value={keyword}
                    onChange={(e) => setKeyword(e.target.value)}
                    style={{ maxWidth: 280 }}
                  />
                  <select
                    className="border border-line rounded px-2 py-1 text-sm bg-card"
                    value={searchField}
                    onChange={(e) => setSearchField(e.target.value as 'name' | 'all')}
                  >
                    <option value="name">仅用户名</option>
                    <option value="all">全部字段</option>
                  </select>
                  <Space size="small" className="ml-2">
                    <span className="text-xs text-fog">仅启用</span>
                    <Switch size="small" checked={onlyEnabled} onChange={setOnlyEnabled} />
                  </Space>
                  <Button
                    type="primary"
                    icon={<PlusOutlined />}
                    className="ml-auto"
                    onClick={() => setCreateUserOpen(true)}
                  >
                    新建用户
                  </Button>
                </div>
                <Table
                  dataSource={filteredUsers}
                  rowKey="name"
                  loading={loading}
                  size="middle"
                  pagination={{ pageSize: 10, showSizeChanger: true }}
                  columns={userColumns}
                  scroll={{ x: 1000 }}
                  rowSelection={{
                    selectedRowKeys: selectedUsers,
                    onChange: (keys) => setSelectedUsers(keys as string[])
                  }}
                />
              </div>
            )
          },
          {
            key: 'groups',
            label: (
              <span>
                <TeamOutlined /> 本地组
              </span>
            ),
            children: (
              <div className="glass-card p-3">
                <div className="mb-3 flex items-center justify-between">
                  <span className="text-xs text-fog">
                    系统内置组（如 Administrators/Users）不可删除，但可管理成员。
                  </span>
                  <Button
                    type="primary"
                    icon={<PlusOutlined />}
                    onClick={() => setCreateGroupOpen(true)}
                  >
                    新建组
                  </Button>
                </div>
                <Table
                  dataSource={groups}
                  rowKey="name"
                  loading={loading}
                  size="middle"
                  pagination={{ pageSize: 10 }}
                  columns={groupColumns}
                />
              </div>
            )
          },
          {
            key: 'matrix',
            label: (
              <span>
                <KeyOutlined /> 权限矩阵
              </span>
            ),
            children: <PermissionMatrix />
          }
        ]}
      />
      <UserCreateModal
        open={createUserOpen}
        onClose={() => setCreateUserOpen(false)}
        onSuccess={load}
      />
      <UserDetailDrawer
        open={!!editUser}
        user={editUser}
        onClose={() => setEditUser(null)}
        onSuccess={load}
      />
      <GroupCreateModal
        open={createGroupOpen}
        onClose={() => setCreateGroupOpen(false)}
        onSuccess={load}
      />
      <GroupManageModal
        open={!!manageGroup}
        group={manageGroup}
        onClose={() => setManageGroup(null)}
        onSuccess={load}
      />
      <Modal
        title={`分配 ${selectedUsers.length} 个用户到组`}
        open={batchGroupOpen}
        onOk={handleBatchAssignConfirm}
        onCancel={() => {
          setBatchGroupOpen(false)
          setBatchGroupName(null)
        }}
        okText="分配"
        cancelText="取消"
        okButtonProps={{ disabled: !batchGroupName }}
      >
        <div className="py-2">
          <div className="mb-2 text-sm text-fog">
            将以下用户批量加入指定组（已在该组中的用户会跳过）：
          </div>
          <div className="mb-3">
            {selectedUsers.map((u) => (
              <Tag key={u}>{u}</Tag>
            ))}
          </div>
          <Select
            style={{ width: '100%' }}
            placeholder="选择目标组"
            value={batchGroupName}
            onChange={setBatchGroupName}
            options={groups.map((g) => ({
              label: `${g.name}${g.description ? ` (${g.description})` : ''}`,
              value: g.name
            }))}
            showSearch
            optionFilterProp="label"
          />
        </div>
      </Modal>
    </div>
  )
}
