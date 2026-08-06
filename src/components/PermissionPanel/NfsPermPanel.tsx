import { useEffect, useState } from 'react'
import { Table, Button, Space, Select, Input, Popconfirm, App, Tag, Empty, Spin, Tooltip } from 'antd'
import { DeleteOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { api, call } from '../../api'
import type { Share, SharePermission } from '../../types'

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
  { label: '读写 (rw)', value: 'rw' }
]

const TYPE_OPTIONS: { label: string; value: NfsType }[] = [
  { label: '允许', value: 'Allow' },
  { label: '拒绝', value: 'Deny' }
]

function toClientPerm(p: SharePermission): NfsClientPerm {
  return {
    clientName: p.account,
    permission: p.access === 'Full' || p.access === 'Change' ? 'rw' : 'ro',
    type: p.deny ? 'Deny' : 'Allow'
  }
}

function toSharePerm(share: Share, c: NfsClientPerm): SharePermission {
  return {
    shareName: share.name,
    account: c.clientName,
    accountType: 'Group',
    access: c.permission === 'rw' ? 'Change' : 'Read',
    deny: c.type === 'Deny'
  }
}

export default function NfsPermPanel({ share }: Props) {
  const { message } = App.useApp()
  const [rows, setRows] = useState<NfsClientPerm[]>([])
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [newClient, setNewClient] = useState('')
  const [newPerm, setNewPerm] = useState<NfsPermission>('rw')
  const [newType, setNewType] = useState<NfsType>('Allow')

  const load = async () => {
    setLoading(true)
    try {
      const list = await call(() => api.adapter.permissions('nfs', share.name))
      setRows(list.map(toClientPerm))
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
    const client = newClient.trim()
    if (!client) {
      message.warning('请输入客户端名称')
      return
    }
    if (rows.some((r) => r.clientName === client)) {
      message.warning('该客户端已存在')
      return
    }
    setRows([...rows, { clientName: client, permission: newPerm, type: newType }])
    setNewClient('')
  }

  const handleRemove = (clientName: string) => {
    setRows(rows.filter((r) => r.clientName !== clientName))
  }

  const handlePermChange = (clientName: string, permission: NfsPermission) => {
    setRows(rows.map((r) => (r.clientName === clientName ? { ...r, permission } : r)))
  }

  const handleTypeChange = (clientName: string, type: NfsType) => {
    setRows(rows.map((r) => (r.clientName === clientName ? { ...r, type } : r)))
  }

  const handleSave = async () => {
    setSaving(true)
    try {
      const perms = rows.map((r) => toSharePerm(share, r))
      await call(() => api.adapter.setPermissions('nfs', share.name, perms))
      message.success('权限已保存')
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const columns = [
    { title: '客户端', dataIndex: 'clientName', ellipsis: true },
    {
      title: '权限',
      dataIndex: 'permission',
      width: 140,
      render: (v: NfsPermission, r: NfsClientPerm) => (
        <Select
          size="small"
          value={v}
          options={PERMISSION_OPTIONS}
          onChange={(next) => handlePermChange(r.clientName, next)}
          style={{ width: 120 }}
        />
      )
    },
    {
      title: '类型',
      dataIndex: 'type',
      width: 110,
      render: (v: NfsType, r: NfsClientPerm) => (
        <Select
          size="small"
          value={v}
          options={TYPE_OPTIONS}
          onChange={(next) => handleTypeChange(r.clientName, next)}
          style={{ width: 90 }}
        />
      )
    },
    {
      title: '',
      width: 50,
      render: (_: unknown, r: NfsClientPerm) => (
        <Tooltip title="移除">
          <Button size="small" danger icon={<DeleteOutlined />} onClick={() => handleRemove(r.clientName)} />
        </Tooltip>
      )
    }
  ]

  return (
    <Spin spinning={loading}>
      <div className="mb-3 flex items-center justify-between">
        <span className="text-sm text-fog">
          NFS 基于客户端授权。客户端可为主机名、IP 或通配符（如 <code>*</code>、<code>192.168.1.0/24</code>）。保存时将覆盖现有规则。
        </span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={load}>
            重新加载
          </Button>
          <Popconfirm title="确认覆盖当前 NFS 客户端权限？" onConfirm={handleSave}>
            <Button size="small" type="primary" loading={saving}>
              保存
            </Button>
          </Popconfirm>
        </Space>
      </div>
      <Table
        dataSource={rows}
        rowKey="clientName"
        columns={columns}
        pagination={false}
        size="small"
        locale={{ emptyText: <Empty description="暂无客户端规则" /> }}
      />
      <div className="mt-4 p-3 rounded-card bg-white/60">
        <div className="text-xs text-fog mb-2">添加客户端规则</div>
        <Space wrap>
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
          <Select value={newType} onChange={setNewType} options={TYPE_OPTIONS} style={{ width: 90 }} />
          <Button icon={<PlusOutlined />} onClick={handleAdd}>
            添加
          </Button>
        </Space>
        <div className="mt-2 text-xs text-fog">
          <Tag color="purple">NFS</Tag>
          拒绝规则优先于允许规则；未匹配的客户端遵循共享默认权限。
        </div>
      </div>
    </Spin>
  )
}
