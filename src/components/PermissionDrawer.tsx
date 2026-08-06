import { useEffect, useState } from 'react'
import {
  Drawer,
  Tabs,
  Table,
  Button,
  Space,
  Select,
  Input,
  Popconfirm,
  App,
  Tag,
  Empty,
  Spin
} from 'antd'
import { DeleteOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { Share, SharePermission, LocalUser, LocalGroup, NtfsAcl, NtfsAclEntry } from '../types'
import NfsPermPanel from './PermissionPanel/NfsPermPanel'
import FtpPermPanel from './PermissionPanel/FtpPermPanel'
import WebdavPermPanel from './PermissionPanel/WebdavPermPanel'

interface Props {
  open: boolean
  share: Share | null
  onClose: () => void
}

type AccessChoice = 'Full' | 'Change' | 'Read' | 'Deny'

interface PermRow {
  account: string
  accountType: 'User' | 'Group'
  access: AccessChoice
}

const ACCESS_OPTIONS: { label: string; value: AccessChoice }[] = [
  { label: '完全控制', value: 'Full' },
  { label: '更改', value: 'Change' },
  { label: '只读', value: 'Read' },
  { label: '拒绝', value: 'Deny' }
]

const ACCESS_COLOR: Record<AccessChoice, string> = {
  Full: 'blue',
  Change: 'purple',
  Read: 'default',
  Deny: 'red'
}

function toRow(p: SharePermission): PermRow {
  return {
    account: p.account,
    accountType: p.accountType,
    access: p.deny || p.access === 'NoAccess' ? 'Deny' : p.access
  }
}

function toPerm(share: Share, r: PermRow): SharePermission {
  return {
    shareName: share.name,
    account: r.account,
    accountType: r.accountType,
    access: r.access === 'Deny' ? 'NoAccess' : r.access,
    deny: r.access === 'Deny'
  }
}

export default function PermissionDrawer({ open, share, onClose }: Props) {
  const { message } = App.useApp()
  const [rows, setRows] = useState<PermRow[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [candidates, setCandidates] = useState<string[]>([])
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newAccess, setNewAccess] = useState<AccessChoice>('Read')

  // NTFS Tab
  const [ntfs, setNtfs] = useState<NtfsAcl | null>(null)
  const [ntfsLoading, setNtfsLoading] = useState(false)

  const loadPerms = async () => {
    if (!share) return
    setLoading(true)
    try {
      const list = await call(() => api.share.permissions(share.name))
      setRows(list.map(toRow))
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const loadCandidates = async () => {
    try {
      const [users, groups] = await Promise.all([
        call(api.user.list).catch(() => [] as LocalUser[]),
        call(api.user.groups).catch(() => [] as LocalGroup[])
      ])
      setCandidates([
        ...users.map((u) => u.name),
        ...groups.map((g) => g.name)
      ])
    } catch {
      // 静默
    }
  }

  const loadNtfs = async () => {
    if (!share?.path) return
    setNtfsLoading(true)
    try {
      const acl = await call(() => api.user.ntfsPermissions(share.path))
      setNtfs(acl)
    } catch (e) {
      message.error((e as Error).message)
      setNtfs(null)
    } finally {
      setNtfsLoading(false)
    }
  }

  useEffect(() => {
    // 仅 SMB 共享走遗留权限通道；NFS/其他协议由对应 PermPanel 自行加载
    if (open && share && share.protocol === 'smb') {
      loadPerms()
      loadCandidates()
      setNtfs(null)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, share])

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
    setRows([...rows, { account: acct, accountType: newType, access: newAccess }])
    setNewAccount('')
  }

  const handleRemove = (account: string) => {
    setRows(rows.filter((r) => r.account !== account))
  }

  const handleAccessChange = (account: string, access: AccessChoice) => {
    setRows(rows.map((r) => (r.account === account ? { ...r, access } : r)))
  }

  const handleSave = async () => {
    if (!share) return
    setSaving(true)
    try {
      const perms = rows.map((r) => toPerm(share, r))
      await call(() => api.user.setSharePermissions(share.name, perms))
      message.success('权限已保存')
      loadPerms()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const sharePermColumns = [
    { title: '账号', dataIndex: 'account', width: 180 },
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
      dataIndex: 'access',
      width: 140,
      render: (v: AccessChoice, r: PermRow) => (
        <Select
          size="small"
          value={v}
          options={ACCESS_OPTIONS}
          onChange={(next) => handleAccessChange(r.account, next)}
          style={{ width: 110 }}
        />
      )
    },
    {
      title: '',
      width: 50,
      render: (_: unknown, r: PermRow) => (
        <Button size="small" danger icon={<DeleteOutlined />} onClick={() => handleRemove(r.account)} />
      )
    }
  ]

  const ntfsColumns = [
    { title: '账号', dataIndex: 'account', ellipsis: true },
    { title: '权限', dataIndex: 'rights', ellipsis: true },
    {
      title: '类型',
      dataIndex: 'type',
      width: 80,
      render: (v: 'Allow' | 'Deny') => <Tag color={v === 'Allow' ? 'blue' : 'red'}>{v === 'Allow' ? '允许' : '拒绝'}</Tag>
    },
    {
      title: '继承',
      dataIndex: 'inherited',
      width: 70,
      render: (v: boolean) => (v ? <Tag>继承</Tag> : <Tag color="default">显式</Tag>)
    }
  ]

  return (
    <Drawer
      title={share ? `权限管理：${share.name}` : '权限管理'}
      open={open}
      onClose={onClose}
      width={640}
      destroyOnClose
      styles={{
        body: {
          background: 'rgba(255,255,255,0.75)',
          backdropFilter: 'blur(16px)'
        }
      }}
    >
      {share?.protocol === 'nfs' && share ? (
        <NfsPermPanel share={share} />
      ) : share?.protocol === 'ftp' && share ? (
        <FtpPermPanel share={share} />
      ) : share?.protocol === 'webdav' && share ? (
        <WebdavPermPanel share={share} />
      ) : (
      <Tabs
        items={[
          {
            key: 'share',
            label: '共享权限',
            children: (
              <Spin spinning={loading}>
                <div className="mb-3 flex items-center justify-between">
                  <span className="text-sm text-fog">
                    修改后点击「保存」生效。Deny 优先于其他权限。
                  </span>
                  <Space>
                    <Button size="small" icon={<ReloadOutlined />} onClick={loadPerms}>
                      重新加载
                    </Button>
                    <Popconfirm title="确认覆盖当前权限？" onConfirm={handleSave}>
                      <Button size="small" type="primary" loading={saving}>
                        保存
                      </Button>
                    </Popconfirm>
                  </Space>
                </div>
                <Table
                  dataSource={rows}
                  rowKey="account"
                  columns={sharePermColumns}
                  pagination={false}
                  size="small"
                  locale={{ emptyText: <Empty description="暂无权限条目" /> }}
                />
                <div className="mt-4 p-3 rounded-card bg-white/60">
                  <div className="text-xs text-fog mb-2">添加账号</div>
                  <Space wrap>
                    <Input
                      placeholder="账号名"
                      value={newAccount}
                      onChange={(e) => setNewAccount(e.target.value)}
                      style={{ width: 180 }}
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
                    <Select
                      value={newAccess}
                      onChange={setNewAccess}
                      options={ACCESS_OPTIONS}
                      style={{ width: 110 }}
                    />
                    <Button icon={<PlusOutlined />} onClick={handleAdd}>
                      添加
                    </Button>
                  </Space>
                  {candidates.length > 0 && (
                    <div className="mt-2 text-xs text-fog">
                      常用账号：{candidates.slice(0, 12).join('、')}
                      {candidates.length > 12 ? ' 等' : ''}
                    </div>
                  )}
                </div>
              </Spin>
            )
          },
          {
            key: 'ntfs',
            label: 'NTFS 权限',
            children: (
              <Spin spinning={ntfsLoading}>
                <div className="mb-3 flex items-center justify-between">
                  <span className="text-sm text-fog break-all">路径：{share?.path}</span>
                  <Button size="small" icon={<ReloadOutlined />} onClick={loadNtfs}>
                    加载
                  </Button>
                </div>
                {ntfs ? (
                  <Table
                    dataSource={ntfs.entries as NtfsAclEntry[]}
                    rowKey={(r) => `${r.account}-${r.rights}-${r.type}`}
                    columns={ntfsColumns}
                    pagination={false}
                    size="small"
                    locale={{ emptyText: <Empty description="无 ACL 条目" /> }}
                  />
                ) : (
                  <Empty description="点击「加载」查看 NTFS ACL（只读）" />
                )}
              </Spin>
            )
          }
        ]}
      />
      )}
    </Drawer>
  )
}
