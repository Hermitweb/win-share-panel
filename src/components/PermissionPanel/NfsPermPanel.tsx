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

// NFS 权限模型：基于客户端（ClientName，可为主机名/IP/通配符）授予 ro/rw，并区分 Allow/Deny
// 底层 adapter 以 SharePermission 透传：account=ClientName，access=Change→rw/Read→ro，deny→Deny
type NfsPermission = 'ro' | 'rw'
type NfsType = 'Allow' | 'Deny'

interface NfsClientPerm {
  clientName: string
  permission: NfsPermission
  type: NfsType
}

const PERMISSION_OPTIONS: { label: string; value: NfsPermission }[] = [
  { label: '只读 (ro)', value: 'ro' },
  { label: '读写 (rw)', value: 'rw' },
]

const TYPE_OPTIONS: { label: string; value: NfsType }[] = [
  { label: '允许', value: 'Allow' },
  { label: '拒绝', value: 'Deny' },
]

function toClientPerm(p: SharePermission): NfsClientPerm {
  return {
    clientName: p.account,
    permission: p.access === 'Full' || p.access === 'Change' ? 'rw' : 'ro',
    type: p.deny ? 'Deny' : 'Allow',
  }
}

function toSharePerm(c: NfsClientPerm, shareName: string): SharePermission {
  return {
    shareName,
    account: c.clientName,
    accountType: 'Group',
    access: c.permission === 'rw' ? 'Change' : 'Read',
    deny: c.type === 'Deny',
  }
}

export default function NfsPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [newClient, setNewClient] = useState('')
  const [newPerm, setNewPerm] = useState<NfsPermission>('rw')
  const [newType, setNewType] = useState<NfsType>('Allow')

  const { rows, loading, saving, reload, save, addRow, removeRow, updateRow, hasRow } =
    useProtocolPermissions<NfsClientPerm>({
      protocol: 'nfs',
      shareName: share.name,
      toRow: toClientPerm,
      toPerm: toSharePerm,
      rowKey: 'clientName',
    })

  const handleAdd = () => {
    const client = newClient.trim()
    if (!client) {
      message.warning('请输入客户端名称')
      return
    }
    if (hasRow(client)) {
      message.warning('该客户端已存在')
      return
    }
    addRow({ clientName: client, permission: newPerm, type: newType })
    setNewClient('')
  }

  const columns: TableColumnsType<NfsClientPerm> = [
    { title: '客户端', dataIndex: 'clientName', ellipsis: true },
    {
      title: '权限',
      dataIndex: 'permission',
      width: 140,
      render: (v: NfsPermission, r) => (
        <Select
          size="small"
          value={v}
          options={PERMISSION_OPTIONS}
          onChange={(next) => updateRow(r.clientName, { permission: next })}
          style={{ width: 120 }}
        />
      ),
    },
    {
      title: '类型',
      dataIndex: 'type',
      width: 110,
      render: (v: NfsType, r) => (
        <Select
          size="small"
          value={v}
          options={TYPE_OPTIONS}
          onChange={(next) => updateRow(r.clientName, { type: next })}
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
            onClick={() => removeRow(r.clientName)}
          />
        </Tooltip>
      ),
    },
  ]

  return (
    <ProtocolPermissionShell<NfsClientPerm>
      loading={loading}
      saving={saving}
      headerText={
        <>
          NFS 基于客户端授权。客户端可为主机名、IP 或通配符（如 <code>*</code>、
          <code>192.168.1.0/24</code>）。保存时将覆盖现有规则。
        </>
      }
      onReload={reload}
      onSave={() => void save('权限已保存')}
      saveConfirmTitle="确认覆盖当前 NFS 客户端权限？"
      columns={columns}
      rows={rows}
      rowKey="clientName"
      emptyText="暂无客户端规则"
      addTitle="添加客户端规则"
      addBox={
        <>
          <Input
            placeholder="客户端名称（如 * 或 192.168.1.0/24）"
            value={newClient}
            onChange={(e) => setNewClient(e.target.value)}
            style={{ width: 240 }}
          />
          <Select
            value={newPerm}
            onChange={setNewPerm}
            options={PERMISSION_OPTIONS}
            style={{ width: 120 }}
          />
          <Select
            value={newType}
            onChange={setNewType}
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
          <Tag color="purple">NFS</Tag>
          拒绝规则优先于允许规则；未匹配的客户端遵循共享默认权限。
        </>
      }
    />
  )
}
