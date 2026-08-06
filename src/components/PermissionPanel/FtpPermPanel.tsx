import { useEffect, useState } from 'react'
import { Table, Button, Space, Select, Input, Popconfirm, App, Tag, Empty, Spin, Tooltip } from 'antd'
import { DeleteOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../../api'
import type { Share, SharePermission } from '../../types'

interface Props {
  share: Share
}

// FTP（IIS）授权规则：基于用户/组授予 Read / Read+Write，区分 Allow/Deny
// adapter 以 SharePermission 透传：account=users/roles，access=Read/Change，deny=Deny
type FtpPerm = 'ro' | 'rw'
type FtpType = 'Allow' | 'Deny'

interface FtpRule {
  account: string
  accountType: 'User' | 'Group'
  perm: FtpPerm
  type: FtpType
}

const PERM_OPTIONS: { label: string; value: FtpPerm }[] = [
  { label: '只读 (Read)', value: 'ro' },
  { label: '读写 (Read, Write)', value: 'rw' }
]

const TYPE_OPTIONS: { label: string; value: FtpType }[] = [
  { label: '允许', value: 'Allow' },
  { label: '拒绝', value: 'Deny' }
]

function toRule(p: SharePermission): FtpRule {
  return {
    account: p.account,
    accountType: p.accountType,
    perm: p.access === 'Change' || p.access === 'Full' ? 'rw' : 'ro',
    type: p.deny ? 'Deny' : 'Allow'
  }
}

function toSharePerm(share: Share, r: FtpRule): SharePermission {
  return {
    shareName: share.name,
    account: r.account,
    accountType: r.accountType,
    access: r.perm === 'rw' ? 'Change' : 'Read',
    deny: r.type === 'Deny'
  }
}

export default function FtpPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [rows, setRows] = useState<FtpRule[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newPerm, setNewPerm] = useState<FtpPerm>('ro')
  const [newAccessType, setNewAccessType] = useState<FtpType>('Allow')

  const load = async () => {
    setLoading(true)
    try {
      const list = await call(() => api.adapter.permissions('ftp', share.name))
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
    setRows([...rows, { account: acct, accountType: newType, perm: newPerm, type: newAccessType }])
    setNewAccount('')
  }

  const handleRemove = (account: string) => {
    setRows(rows.filter((r) => r.account !== account))
  }

  const handlePermChange = (account: string, perm: FtpPerm) => {
    setRows(rows.map((r) => (r.account === account ? { ...r, perm } : r)))
  }

  const handleTypeChange = (account: string, type: FtpType) => {
    setRows(rows.map((r) => (r.account === account ? { ...r, type } : r)))
  }

  const handleSave = async () => {
    setSaving(true)
    try {
      const perms = rows.map((r) => toSharePerm(share, r))
      await call(() => api.adapter.setPermissions('ftp', share.name, perms))
      message.success('授权规则已保存')
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
      width: 140,
      render: (v: FtpPerm, r: FtpRule) => (
        <Select
          size="small"
          value={v}
          options={PERM_OPTIONS}
          onChange={(next) => handlePermChange(r.account, next)}
          style={{ width: 140 }}
        />
      )
    },
    {
      title: '授权',
      dataIndex: 'type',
      width: 110,
      render: (v: FtpType, r: FtpRule) => (
        <Select
          size="small"
          value={v}
          options={TYPE_OPTIONS}
          onChange={(next) => handleTypeChange(r.account, next)}
          style={{ width: 90 }}
        />
      )
    },
    {
      title: '',
      width: 50,
      render: (_: unknown, r: FtpRule) => (
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
          FTP 授权规则基于用户/组授予 Read 或 Read+Write，可设允许/拒绝。保存时覆盖现有规则。
        </span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={load}>
            重新加载
          </Button>
          <Popconfirm title="确认覆盖当前 FTP 授权规则？" onConfirm={handleSave}>
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
        locale={{ emptyText: <Empty description="暂无授权规则" /> }}
      />
      <div className="mt-4 p-3 rounded-card bg-white/60">
        <div className="text-xs text-fog mb-2">添加授权规则</div>
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
          <Select value={newPerm} onChange={setNewPerm} options={PERM_OPTIONS} style={{ width: 140 }} />
          <Select value={newAccessType} onChange={setNewAccessType} options={TYPE_OPTIONS} style={{ width: 90 }} />
          <Button icon={<PlusOutlined />} onClick={handleAdd}>
            添加
          </Button>
        </Space>
        <div className="mt-2 text-xs text-fog">
          <Tag color="green">FTP</Tag>
          组授权使用 roles，用户授权使用 users；拒绝规则优先于允许规则。
        </div>
      </div>
    </Spin>
  )
}
