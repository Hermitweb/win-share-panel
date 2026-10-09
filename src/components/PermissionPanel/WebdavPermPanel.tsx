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
  { label: '完全 (Read, Write, Source)', value: 'full' },
]

function toRule(p: SharePermission): WebdavRule {
  let perm: WebdavPerm = 'ro'
  if (p.access === 'Full') perm = 'full'
  else if (p.access === 'Change') perm = 'rw'
  else perm = 'ro'
  return {
    account: p.account,
    accountType: p.accountType,
    perm,
  }
}

function toSharePerm(r: WebdavRule, shareName: string): SharePermission {
  return {
    shareName,
    account: r.account,
    accountType: r.accountType,
    access: r.perm === 'full' ? 'Full' : r.perm === 'rw' ? 'Change' : 'Read',
    deny: false,
  }
}

export default function WebdavPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newPerm, setNewPerm] = useState<WebdavPerm>('ro')

  const { rows, loading, saving, reload, save, addRow, removeRow, updateRow, hasRow } =
    useProtocolPermissions<WebdavRule>({
      protocol: 'webdav',
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
    addRow({ account: acct, accountType: newType, perm: newPerm })
    setNewAccount('')
  }

  const columns: TableColumnsType<WebdavRule> = [
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
      width: 200,
      render: (v: WebdavPerm, r) => (
        <Select
          size="small"
          value={v}
          options={PERM_OPTIONS}
          onChange={(next) => updateRow(r.account, { perm: next })}
          style={{ width: 200 }}
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
    <ProtocolPermissionShell<WebdavRule>
      loading={loading}
      saving={saving}
      headerText={
        <>
          WebDAV 作者规则基于用户/组授予 Read / Read+Write /
          Read+Write+Source，仅允许（无拒绝）。保存时覆盖现有规则。
        </>
      }
      onReload={reload}
      onSave={() => void save('作者规则已保存')}
      saveConfirmTitle="确认覆盖当前 WebDAV 作者规则？"
      columns={columns}
      rows={rows}
      rowKey="account"
      emptyText="暂无作者规则"
      addTitle="添加作者规则"
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
            style={{ width: 200 }}
          />
          <Button icon={<PlusOutlined />} onClick={handleAdd}>
            添加
          </Button>
        </>
      }
      hint={
        <>
          <Tag color="orange">WebDAV</Tag>
          Source 权限允许客户端修改文件元数据（如属性）；组授权使用 roles，用户授权使用 users。
        </>
      }
    />
  )
}
