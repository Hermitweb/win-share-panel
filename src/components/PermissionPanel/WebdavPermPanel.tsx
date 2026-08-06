import { useEffect, useState } from 'react'
import { Table, Button, Space, Select, Input, Popconfirm, App, Tag, Empty, Spin, Tooltip } from 'antd'
import { DeleteOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../../api'
import type { Share, SharePermission } from '../../types'

interface Props {
  share: Share
}

// WebDAV authoringRules：基于用户/组授予 Read / Read+Write / Read+Write+Source
// adapter 以 SharePermission 透传：account=users/roles，access=Read/Change/Full（无 Deny）
type WebdavPerm = 'ro' | 'rw' | 'full'

interface WebdavRule {
  account: string
  accountType: 'User' | 'Group'
  perm: WebdavPerm
}

const PERM_OPTIONS: { label: string; value: WebdavPerm }[] = [
  { label: '只读 (Read)', value: 'ro' },
  { label: '读写 (Read, Write)', value: 'rw' },
  { label: '完全 (Read, Write, Source)', value: 'full' }
]

function toRule(p: SharePermission): WebdavRule {
  let perm: WebdavPerm = 'ro'
  if (p.access === 'Full') perm = 'full'
  else if (p.access === 'Change') perm = 'rw'
  else perm = 'ro'
  return {
    account: p.account,
    accountType: p.accountType,
    perm
  }
}

function toSharePerm(share: Share, r: WebdavRule): SharePermission {
  return {
    shareName: share.name,
    account: r.account,
    accountType: r.accountType,
    access: r.perm === 'full' ? 'Full' : r.perm === 'rw' ? 'Change' : 'Read',
    deny: false
  }
}

export default function WebdavPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [rows, setRows] = useState<WebdavRule[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newPerm, setNewPerm] = useState<WebdavPerm>('ro')

  const load = async () => {
    setLoading(true)
    try {
      const list = await call(() => api.adapter.permissions('webdav', share.name))
      setRows(list.map(toRule))
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [share.name])

  const handleAdd = () => {
    const acct = newAccount.trim()
    if (!acct) {
      message.warning('请输入账号名')
      return
    }
    if (rows.some((r) => r.account === acct)) {
      message.warning('该账号已存在')
      return
    }
    setRows([...rows, { account: acct, accountType: newType, perm: newPerm }])
    setNewAccount('')
  }

  const handleRemove = (account: string) => {
    setRows(rows.filter((r) => r.account !== account))
  }

  const handlePermChange = (account: string, perm: WebdavPerm) => {
    setRows(rows.map((r) => (r.account === account ? { ...r, perm } : r)))
  }

  const handleSave = async () => {
    setSaving(true)
    try {
      const perms = rows.map((r) => toSharePerm(share, r))
      await call(() => api.adapter.setPermissions('webdav', share.name, perms))
      message.success('作者规则已保存')
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const columns = [
    { title: '账号', dataIndex: 'account', ellipsis: true },
    {
      title: '类型',
      dataIndex: 'accountType',
      width: 90,
      render: (v: 'User' | 'Group') => (
        <Tag color={v === 'User' ? 'blue' : 'purple'}>{v === 'User' ? '用户' : '组'}</Tag>
      )
    },
    {
      title: '权限',
      dataIndex: 'perm',
      width: 200,
      render: (v: WebdavPerm, r: WebdavRule) => (
        <Select
          size="small"
          value={v}
          options={PERM_OPTIONS}
          onChange={(next) => handlePermChange(r.account, next)}
          style={{ width: 200 }}
        />
      )
    },
    {
      title: '',
      width: 50,
      render: (_: unknown, r: WebdavRule) => (
        <Tooltip title="移除">
          <Button size="small" danger icon={<DeleteOutlined />} onClick={() => handleRemove(r.account)} />
        </Tooltip>
      )
    }
  ]

  return (
    <Spin spinning={loading}>
      <div className="mb-3 flex items-center justify-between">
        <span className="text-sm text-fog">
          WebDAV 作者规则基于用户/组授予 Read / Read+Write / Read+Write+Source，仅允许（无拒绝）。保存时覆盖现有规则。
        </span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={load}>
            重新加载
          </Button>
          <Popconfirm title="确认覆盖当前 WebDAV 作者规则？" onConfirm={handleSave}>
            <Button size="small" type="primary" loading={saving}>
              保存
            </Button>
          </Popconfirm>
        </Space>
      </div>
      <Table
        dataSource={rows}
        rowKey="account"
        columns={columns}
        pagination={false}
        size="small"
        locale={{ emptyText: <Empty description="暂无作者规则" /> }}
      />
      <div className="mt-4 p-3 rounded-card bg-white/60">
        <div className="text-xs text-fog mb-2">添加作者规则</div>
        <Space wrap>
          <Input
            placeholder="账号名（如 * 或 Administrators）"
            value={newAccount}
            onChange={(e) => setNewAccount(e.target.value)}
            style={{ width: 220 }}
          />
          <Select
            value={newType}
            onChange={setNewType}
            options={[
              { label: '用户', value: 'User' },
              { label: '组', value: 'Group' }
            ]}
            style={{ width: 90 }}
          />
          <Select value={newPerm} onChange={setNewPerm} options={PERM_OPTIONS} style={{ width: 200 }} />
          <Button icon={<PlusOutlined />} onClick={handleAdd}>
            添加
          </Button>
        </Space>
        <div className="mt-2 text-xs text-fog">
          <Tag color="orange">WebDAV</Tag>
          Source 权限允许客户端修改文件元数据（如属性）；组授权使用 roles，用户授权使用 users。
        </div>
      </div>
    </Spin>
  )
}
