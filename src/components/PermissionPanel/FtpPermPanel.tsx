import { useState } from 'react'
import type { TableColumnsType } from 'antd'
import { App, Button, Input, Select, Tag, Tooltip } from 'antd'
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import type { Share, SharePermission } from '../../types'
import { useProtocolPermissions } from '../../hooks/useProtocolPermissions'
import { ProtocolPermissionShell } from '../ProtocolPermissionShell'

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
  { label: '读写 (Read, Write)', value: 'rw' },
]

const TYPE_OPTIONS: { label: string; value: FtpType }[] = [
  { label: '允许', value: 'Allow' },
  { label: '拒绝', value: 'Deny' },
]

function toRule(p: SharePermission): FtpRule {
  return {
    account: p.account,
    accountType: p.accountType,
    perm: p.access === 'Change' || p.access === 'Full' ? 'rw' : 'ro',
    type: p.deny ? 'Deny' : 'Allow',
  }
}

function toSharePerm(r: FtpRule, shareName: string): SharePermission {
  return {
    shareName,
    account: r.account,
    accountType: r.accountType,
    access: r.perm === 'rw' ? 'Change' : 'Read',
    deny: r.type === 'Deny',
  }
}

export default function FtpPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newPerm, setNewPerm] = useState<FtpPerm>('ro')
  const [newAccessType, setNewAccessType] = useState<FtpType>('Allow')

  const { rows, loading, saving, reload, save, addRow, removeRow, updateRow, hasRow } =
    useProtocolPermissions<FtpRule>({
      protocol: 'ftp',
      shareName: share.name,
      toRow: toRule,
      toPerm: toSharePerm,
      rowKey: 'account',
    })

  const handleAdd = () => {
    const acct = newAccount.trim()
    if (!acct) {
      message.warning('请输入账号名')
      return
    }
    if (hasRow(acct)) {
      message.warning('该账号已存在')
      return
    }
    addRow({ account: acct, accountType: newType, perm: newPerm, type: newAccessType })
    setNewAccount('')
  }

  const columns: TableColumnsType<FtpRule> = [
    { title: '账号', dataIndex: 'account', ellipsis: true },
    {
      title: '类型',
      dataIndex: 'accountType',
      width: 90,
      render: (v: 'User' | 'Group') => (
        <Tag color={v === 'User' ? 'blue' : 'purple'}>{v === 'User' ? '用户' : '组'}</Tag>
      ),
    },
    {
      title: '权限',
      dataIndex: 'perm',
      width: 140,
      render: (v: FtpPerm, r) => (
        <Select
          size="small"
          value={v}
          options={PERM_OPTIONS}
          onChange={(next) => updateRow(r.account, { perm: next })}
          style={{ width: 140 }}
        />
      ),
    },
    {
      title: '授权',
      dataIndex: 'type',
      width: 110,
      render: (v: FtpType, r) => (
        <Select
          size="small"
          value={v}
          options={TYPE_OPTIONS}
          onChange={(next) => updateRow(r.account, { type: next })}
          style={{ width: 90 }}
        />
      ),
    },
    {
      title: '',
      width: 50,
      render: (_: unknown, r) => (
        <Tooltip title="移除">
          <Button
            size="small"
            danger
            icon={<DeleteOutlined />}
            onClick={() => removeRow(r.account)}
          />
        </Tooltip>
      ),
    },
  ]

  return (
    <ProtocolPermissionShell<FtpRule>
      loading={loading}
      saving={saving}
      headerText="FTP 授权规则基于用户/组授予 Read 或 Read+Write，可设允许/拒绝。保存时覆盖现有规则。"
      onReload={reload}
      onSave={() => void save('授权规则已保存')}
      saveConfirmTitle="确认覆盖当前 FTP 授权规则？"
      columns={columns}
      rows={rows}
      rowKey="account"
      emptyText="暂无授权规则"
      addTitle="添加授权规则"
      addBox={
        <>
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
              { label: '组', value: 'Group' },
            ]}
            style={{ width: 90 }}
          />
          <Select
            value={newPerm}
            onChange={setNewPerm}
            options={PERM_OPTIONS}
            style={{ width: 140 }}
          />
          <Select
            value={newAccessType}
            onChange={setNewAccessType}
            options={TYPE_OPTIONS}
            style={{ width: 90 }}
          />
          <Button icon={<PlusOutlined />} onClick={handleAdd}>
            添加
          </Button>
        </>
      }
      hint={
        <>
          <Tag color="green">FTP</Tag>
          组授权使用 roles，用户授权使用 users；拒绝规则优先于允许规则。
        </>
      }
    />
  )
}
