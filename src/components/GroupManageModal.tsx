import { useEffect, useState } from 'react'
import { Modal, Form, Input, Table, Button, Space, Tag, App, Popconfirm, Tooltip, Empty, Select } from 'antd'
import { PlusOutlined, DeleteOutlined, ReloadOutlined, EditOutlined, CheckOutlined, CloseOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { LocalGroup, GroupMember, LocalUser } from '../types'

interface Props {
  open: boolean
  group: LocalGroup | null
  onClose: () => void
  onSuccess: () => void
}

export default function GroupManageModal({ open, group, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [form] = Form.useForm()
  const [members, setMembers] = useState<GroupMember[]>([])
  const [loading, setLoading] = useState(false)
  const [savingDesc, setSavingDesc] = useState(false)
  const [newMember, setNewMember] = useState('')

  // 组重命名
  const [renaming, setRenaming] = useState(false)
  const [newGroupName, setNewGroupName] = useState('')

  // 批量添加成员
  const [allUsers, setAllUsers] = useState<LocalUser[]>([])
  const [batchMembers, setBatchMembers] = useState<string[]>([])

  useEffect(() => {
    if (open && group) {
      form.setFieldsValue({ description: group.description })
      setMembers(group.members || [])
      setNewMember('')
      setRenaming(false)
      setNewGroupName('')
      setBatchMembers([])
      // 加载用户列表供批量添加使用
      loadUsers()
    }
  }, [open, group, form])

  const loadUsers = async () => {
    try {
      const u = await call(api.user.list)
      setAllUsers(u)
    } catch {
      // 加载失败不影响主功能
    }
  }

  const reloadMembers = async () => {
    if (!group) return
    setLoading(true)
    try {
      const groups = await call(api.user.groups)
      const updated = groups.find((g) => g.name === group.name)
      if (updated) {
        setMembers(updated.members || [])
        form.setFieldsValue({ description: updated.description })
      }
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const handleSaveDesc = async () => {
    if (!group) return
    const v = await form.validateFields()
    setSavingDesc(true)
    try {
      await call(() => api.group.update(group.name, v.description || ''))
      message.success('描述已更新')
      onSuccess()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSavingDesc(false)
    }
  }

  const handleRename = async () => {
    if (!group || !newGroupName.trim()) return
    if (newGroupName.trim() === group.name) {
      setRenaming(false)
      return
    }
    try {
      await call(() => api.group.rename(group.name, newGroupName.trim()))
      message.success('组已重命名')
      setRenaming(false)
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleAddMember = async () => {
    if (!group || !newMember.trim()) return
    try {
      await call(() => api.group.addMember(group.name, newMember.trim()))
      message.success(`已添加成员：${newMember.trim()}`)
      setNewMember('')
      reloadMembers()
      onSuccess()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleBatchAddMembers = async () => {
    if (!group || batchMembers.length === 0) return
    const failed: string[] = []
    for (const m of batchMembers) {
      try {
        await call(() => api.group.addMember(group.name, m))
      } catch {
        failed.push(m)
      }
    }
    message.success(`已添加 ${batchMembers.length - failed.length} 个成员${failed.length ? `，${failed.length} 个失败` : ''}`)
    setBatchMembers([])
    reloadMembers()
    onSuccess()
  }

  const handleRemoveMember = async (memberName: string) => {
    if (!group) return
    try {
      await call(() => api.group.removeMember(group.name, memberName))
      message.success(`已移除成员：${memberName}`)
      reloadMembers()
      onSuccess()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  // 可选用户：未已在组中的本地用户
  const memberNames = new Set(members.map((m) => m.name))
  const availableUsers = allUsers.filter((u) => !memberNames.has(u.name))

  const columns = [
    { title: '成员', dataIndex: 'name', ellipsis: true },
    {
      title: '类型',
      dataIndex: 'objectClass',
      width: 80,
      render: (v: 'User' | 'Group') => (
        <Tag color={v === 'User' ? 'blue' : 'purple'}>{v === 'User' ? '用户' : '组'}</Tag>
      )
    },
    {
      title: '来源',
      dataIndex: 'principalSource',
      width: 110,
      render: (v: string) => <span className="text-fog text-sm">{v || 'Local'}</span>
    },
    {
      title: '操作',
      width: 80,
      render: (_: unknown, r: GroupMember) => (
        <Popconfirm
          title={`从组中移除 ${r.name}？`}
          onConfirm={() => handleRemoveMember(r.name)}
        >
          <Tooltip title="移除">
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Tooltip>
        </Popconfirm>
      )
    }
  ]

  return (
    <Modal
      open={open}
      title={`管理组：${group?.name ?? ''}`}
      onCancel={onClose}
      footer={null}
      width={640}
    >
      <Form form={form} layout="vertical">
        <Form.Item label="组名">
          {renaming ? (
            <Space.Compact style={{ width: '100%' }}>
              <Input
                value={newGroupName}
                onChange={(e) => setNewGroupName(e.target.value)}
                placeholder="输入新组名"
                onPressEnter={handleRename}
              />
              <Button type="primary" icon={<CheckOutlined />} onClick={handleRename} />
              <Button icon={<CloseOutlined />} onClick={() => setRenaming(false)} />
            </Space.Compact>
          ) : (
            <Space style={{ width: '100%' }}>
              <Input value={group?.name ?? ''} disabled style={{ flex: 1 }} />
              <Tooltip title="重命名组">
                <Button icon={<EditOutlined />} onClick={() => { setNewGroupName(group?.name || ''); setRenaming(true) }} />
              </Tooltip>
            </Space>
          )}
        </Form.Item>
        <Form.Item name="description" label="描述">
          <Input.TextArea rows={2} placeholder="组用途描述" />
        </Form.Item>
        <Button type="primary" loading={savingDesc} onClick={handleSaveDesc} className="mb-3">
          保存描述
        </Button>
      </Form>

      <div className="border-t border-line pt-3 mt-2">
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium">成员管理</span>
          <Button size="small" icon={<ReloadOutlined />} onClick={reloadMembers} loading={loading}>
            刷新
          </Button>
        </div>

        {/* 批量添加：从本地用户列表多选 */}
        <div className="mb-2">
          <span className="text-xs text-fog mb-1 block">从本地用户批量添加</span>
          <Space.Compact style={{ width: '100%' }}>
            <Select
              mode="multiple"
              style={{ flex: 1 }}
              placeholder="选择要添加的用户（可多选）"
              value={batchMembers}
              onChange={setBatchMembers}
              options={availableUsers.map((u) => ({
                label: `${u.name}${u.fullName ? ` (${u.fullName})` : ''}`,
                value: u.name
              }))}
              optionFilterProp="label"
              maxTagCount={3}
            />
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={handleBatchAddMembers}
              disabled={batchMembers.length === 0}
            >
              批量添加
            </Button>
          </Space.Compact>
        </div>

        {/* 手动添加：兼容域账号等非本地用户 */}
        <Space.Compact style={{ width: '100%' }} className="mb-2">
          <Input
            placeholder="手动输入用户名或组名后回车添加"
            value={newMember}
            onChange={(e) => setNewMember(e.target.value)}
            onPressEnter={handleAddMember}
          />
          <Button type="primary" icon={<PlusOutlined />} onClick={handleAddMember}>
            添加
          </Button>
        </Space.Compact>

        <Table
          dataSource={members}
          rowKey="name"
          columns={columns}
          size="small"
          pagination={false}
          loading={loading}
          locale={{ emptyText: <Empty description="暂无成员" /> }}
          scroll={{ y: 320 }}
        />
      </div>
    </Modal>
  )
}
