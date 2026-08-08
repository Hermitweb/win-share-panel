import { useEffect, useState } from 'react'
import {
  Drawer,
  Tabs,
  Form,
  Input,
  Switch,
  Button,
  Space,
  Tag,
  Descriptions,
  Table,
  App,
  Spin,
  Popconfirm,
  Select,
  Empty,
  Progress,
  Tooltip
} from 'antd'
import {
  SaveOutlined,
  ReloadOutlined,
  KeyOutlined,
  ThunderboltOutlined,
  TeamOutlined,
  PlusOutlined,
  DeleteOutlined
} from '@ant-design/icons'
import dayjs from 'dayjs'
import { api, call } from '../api'
import type { LocalUser, LocalGroup, SharePermission } from '../types'
import { generatePassword, evaluateStrength, STRENGTH_LABEL, STRENGTH_COLOR } from '../utils/password'

interface Props {
  open: boolean
  user: LocalUser | null
  onClose: () => void
  onSuccess: () => void
}

type Access = 'Full' | 'Change' | 'Read' | 'NoAccess'

const ACCESS_TAG_COLOR: Record<string, string> = {
  Full: 'blue',
  Change: 'purple',
  Read: 'default',
  NoAccess: 'red'
}

const ACCESS_LABEL: Record<string, string> = {
  Full: '完全控制',
  Change: '更改',
  Read: '只读',
  NoAccess: '拒绝'
}

// 用户详情抽屉：属性编辑 + 组成员管理 + 共享权限查看
export default function UserDetailDrawer({ open, user, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [tab, setTab] = useState('props')
  const [form] = Form.useForm()
  const [saving, setSaving] = useState(false)
  const [loading, setLoading] = useState(false)

  // 密码区
  const [pwdOpen, setPwdOpen] = useState(false)
  const [newPwd, setNewPwd] = useState('')

  // 组管理
  const [groups, setGroups] = useState<LocalGroup[]>([])
  const [addGroup, setAddGroup] = useState<string | null>(null)

  // 共享权限
  const [perms, setPerms] = useState<SharePermission[]>([])

  const originalName = user?.name || ''

  useEffect(() => {
    if (!open || !user) return
    setTab('props')
    form.setFieldsValue({
      name: user.name,
      fullName: user.fullName,
      description: user.description,
      enabled: user.enabled,
      userMayChangePassword: user.userMayChangePassword,
      passwordNeverExpires: !user.passwordExpires
    })
    setPwdOpen(false)
    setNewPwd('')
  }, [open, user, form])

  // 加载组列表（Tab 2 激活时）
  const loadGroups = async () => {
    if (!user) return
    setLoading(true)
    try {
      const g = await call(api.user.groups)
      setGroups(g)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  // 加载共享权限（Tab 3 激活时）
  const loadPerms = async () => {
    if (!user) return
    setLoading(true)
    try {
      const p = await call(() => api.user.sharePermissionsForUser(user.name))
      setPerms(p)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const handleTabChange = (key: string) => {
    setTab(key)
    if (key === 'groups' && groups.length === 0) loadGroups()
    if (key === 'perms') loadPerms()
  }

  const handleSave = async () => {
    if (!user) return
    const v = await form.validateFields()
    setSaving(true)
    try {
      // 用户名变更 → 先重命名
      if (v.name && v.name !== originalName) {
        await call(() => api.user.rename(originalName, v.name))
      }
      // 更新其他属性
      await call(() =>
        api.user.update(originalName, {
          fullName: v.fullName || '',
          description: v.description || '',
          enabled: v.enabled,
          passwordChangeable: v.userMayChangePassword,
          passwordExpires: !v.passwordNeverExpires
        })
      )
      // 重设密码
      if (pwdOpen && newPwd) {
        await call(() => api.user.setPassword(v.name || originalName, newPwd))
      }
      message.success('已保存')
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const handleGeneratePwd = () => {
    const pwd = generatePassword(12)
    setNewPwd(pwd)
  }

  // 组成员管理
  const userGroups = user?.groups || []
  const availableGroups = groups.filter((g) => !userGroups.includes(g.name))

  const handleAddToGroup = async () => {
    if (!user || !addGroup) return
    try {
      await call(() => api.group.addMember(addGroup, user.name))
      message.success(`已加入组：${addGroup}`)
      setAddGroup(null)
      onSuccess()
      // 刷新用户数据以更新 groups 列表
      loadGroups()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleRemoveFromGroup = async (groupName: string) => {
    if (!user) return
    try {
      await call(() => api.group.removeMember(groupName, user.name))
      message.success(`已从组移除：${groupName}`)
      onSuccess()
      loadGroups()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const strength = newPwd ? evaluateStrength(newPwd) : null
  const strengthPercent = strength === 'strong' ? 100 : strength === 'medium' ? 66 : 33

  const fmtTime = (ts: string): string => {
    if (!ts) return '-'
    const d = dayjs(ts)
    return d.isValid() ? d.format('YYYY-MM-DD HH:mm:ss') : ts
  }

  const permColumns = [
    { title: '共享名', dataIndex: 'shareName', width: 180, ellipsis: true },
    {
      title: '访问级别',
      dataIndex: 'access',
      width: 100,
      render: (v: string, r: SharePermission) => {
        const access = r.deny ? 'NoAccess' : v
        return <Tag color={ACCESS_TAG_COLOR[access]}>{ACCESS_LABEL[access]}</Tag>
      }
    },
    {
      title: '类型',
      dataIndex: 'accountType',
      width: 80,
      render: (v: string) => <Tag>{v === 'User' ? '用户' : '组'}</Tag>
    }
  ]

  return (
    <Drawer
      open={open}
      title={`用户详情：${originalName}`}
      onClose={onClose}
      width={560}
      footer={
        tab === 'props' ? (
          <div className="flex justify-end gap-2">
            <Button onClick={onClose}>取消</Button>
            <Button type="primary" icon={<SaveOutlined />} loading={saving} onClick={handleSave}>
              保存
            </Button>
          </div>
        ) : null
      }
    >
      <Tabs
        activeKey={tab}
        onChange={handleTabChange}
        items={[
          {
            key: 'props',
            label: (
              <span>
                <KeyOutlined /> 属性
              </span>
            ),
            children: (
              <Form form={form} layout="vertical">
                <Form.Item
                  name="name"
                  label="用户名"
                  rules={[
                    { required: true, message: '请输入用户名' },
                    { max: 20, message: '用户名不能超过 20 字符' },
                    { pattern: /^[A-Za-z0-9._-]+$/, message: '仅支持字母、数字、点、下划线、连字符' }
                  ]}
                  extra={originalName !== form.getFieldValue('name') ? '用户名已修改，保存时将执行重命名' : undefined}
                >
                  <Input />
                </Form.Item>
                <Form.Item name="fullName" label="全名">
                  <Input placeholder="用户显示名" />
                </Form.Item>
                <Form.Item name="description" label="描述">
                  <Input.TextArea rows={2} placeholder="账号用途描述" />
                </Form.Item>
                <Space size="large">
                  <Form.Item name="enabled" label="启用账号" valuePropName="checked">
                    <Switch />
                  </Form.Item>
                  <Form.Item name="userMayChangePassword" label="允许修改密码" valuePropName="checked">
                    <Switch />
                  </Form.Item>
                  <Form.Item name="passwordNeverExpires" label="密码永不过期" valuePropName="checked">
                    <Switch />
                  </Form.Item>
                </Space>

                <div className="border-t border-line pt-3 mt-3">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium">重设密码</span>
                    <Space size="small">
                      <Tooltip title="生成随机强密码">
                        <Button size="small" icon={<ThunderboltOutlined />} onClick={handleGeneratePwd}>
                          生成
                        </Button>
                      </Tooltip>
                      <Switch size="small" checked={pwdOpen} onChange={setPwdOpen} />
                    </Space>
                  </div>
                  {pwdOpen && (
                    <>
                      <Input.Password
                        placeholder="输入新密码或点击生成"
                        value={newPwd}
                        onChange={(e) => setNewPwd(e.target.value)}
                        autoComplete="new-password"
                      />
                      {strength && (
                        <div className="mt-1 flex items-center gap-2">
                          <Progress
                            percent={strengthPercent}
                            showInfo={false}
                            strokeColor={STRENGTH_COLOR[strength]}
                            size="small"
                            style={{ maxWidth: 120, margin: 0 }}
                          />
                          <Tag color={STRENGTH_COLOR[strength]}>{STRENGTH_LABEL[strength]}</Tag>
                        </div>
                      )}
                    </>
                  )}
                </div>

                {user && (
                  <Descriptions
                    className="mt-4"
                    size="small"
                    column={1}
                    colon={false}
                    items={[
                      { key: 'sid', label: 'SID', children: <span className="text-fog text-xs">{user.sid || '-'}</span> },
                      { key: 'src', label: '来源', children: user.principalSource || 'Local' },
                      { key: 'pls', label: '上次设置密码', children: <span className="text-fog text-xs">{fmtTime(user.passwordLastSet)}</span> },
                      { key: 'll', label: '上次登录', children: <span className="text-fog text-xs">{fmtTime(user.lastLogon)}</span> }
                    ]}
                  />
                )}
              </Form>
            )
          },
          {
            key: 'groups',
            label: (
              <span>
                <TeamOutlined /> 所属组
              </span>
            ),
            children: (
              <Spin spinning={loading}>
                <div className="mb-4">
                  <div className="text-sm font-medium mb-2">当前所属组</div>
                  {userGroups.length === 0 ? (
                    <Empty description="未加入任何组" image={Empty.PRESENTED_IMAGE_SIMPLE} />
                  ) : (
                    <Space wrap>
                      {userGroups.map((g) => (
                        <Tag
                          key={g}
                          closable
                          onClose={(e) => {
                            e.preventDefault()
                            handleRemoveFromGroup(g)
                          }}
                        >
                          {g}
                        </Tag>
                      ))}
                    </Space>
                  )}
                </div>
                <div className="border-t border-line pt-3">
                  <div className="text-sm font-medium mb-2">添加到组</div>
                  <Space.Compact style={{ width: '100%' }}>
                    <Select
                      style={{ flex: 1 }}
                      placeholder="选择要加入的组"
                      value={addGroup}
                      onChange={setAddGroup}
                      options={availableGroups.map((g) => ({ label: `${g.name}${g.description ? ` (${g.description})` : ''}`, value: g.name }))}
                      showSearch
                      optionFilterProp="label"
                    />
                    <Button type="primary" icon={<PlusOutlined />} onClick={handleAddToGroup} disabled={!addGroup}>
                      添加
                    </Button>
                  </Space.Compact>
                  {availableGroups.length === 0 && groups.length > 0 && (
                    <div className="mt-2 text-xs text-fog">用户已加入所有可用组</div>
                  )}
                </div>
              </Spin>
            )
          },
          {
            key: 'perms',
            label: (
              <span>
                <KeyOutlined /> 共享权限
              </span>
            ),
            children: (
              <Spin spinning={loading}>
                <div className="mb-3 flex items-center justify-between">
                  <span className="text-sm text-fog">
                    该用户在各共享上的访问权限（{perms.length} 条）
                  </span>
                  <Button size="small" icon={<ReloadOutlined />} onClick={loadPerms}>
                    刷新
                  </Button>
                </div>
                {perms.length === 0 && !loading ? (
                  <Empty description="无共享权限" image={Empty.PRESENTED_IMAGE_SIMPLE} />
                ) : (
                  <Table
                    dataSource={perms}
                    rowKey={(r) => `${r.shareName}_${r.account}`}
                    columns={permColumns}
                    size="small"
                    pagination={false}
                    scroll={{ y: 360 }}
                  />
                )}
                <div className="mt-3 text-xs text-fog">
                  权限来源为 SMB 共享级别授权。NTFS 级别权限请在「共享管理」页对单个共享编辑。
                </div>
              </Spin>
            )
          }
        ]}
      />
    </Drawer>
  )
}
