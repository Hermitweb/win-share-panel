import { useEffect, useState } from 'react'
import { Modal, Form, Input, Button, Table, Select, Switch, Space, Tag, App, Popconfirm, Tooltip, Empty } from 'antd'
import {
  PlusOutlined,
  DeleteOutlined,
  CopyOutlined,
  SaveOutlined
} from '@ant-design/icons'
import { api, call } from '../api'
import type { PermissionPreset, PresetEntry, LocalUser, LocalGroup } from '../types'

interface Props {
  open: boolean
  preset: PermissionPreset | null
  onClose: () => void
  onSuccess: () => void
}

const ACCESS_OPTIONS = [
  { label: '完全控制', value: 'Full' },
  { label: '更改', value: 'Change' },
  { label: '只读', value: 'Read' }
]

// 内置账号占位符
const PLACEHOLDER_ACCOUNTS = [
  { name: '{Everyone}', label: '所有人 (Everyone)', type: 'Group' as const },
  { name: '{Administrators}', label: '管理员组 (Administrators)', type: 'Group' as const },
  { name: '{CurrentUser}', label: '当前用户 (CurrentUser)', type: 'User' as const }
]

export default function PresetEditor({ open, preset, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [form] = Form.useForm()
  const [entries, setEntries] = useState<PresetEntry[]>([])
  const [saving, setSaving] = useState(false)
  const [candidates, setCandidates] = useState<{ name: string; label: string; type: 'User' | 'Group' }[]>([])
  const [newAccount, setNewAccount] = useState('')
  const [newType, setNewType] = useState<'User' | 'Group'>('User')
  const [newAccess, setNewAccess] = useState<PresetEntry['access']>('Read')
  const [newDeny, setNewDeny] = useState(false)

  useEffect(() => {
    if (open && preset) {
      form.setFieldsValue({
        name: preset.name,
        description: preset.description,
        category: preset.category || '自定义'
      })
      setEntries(preset.entries ? preset.entries.map((e) => ({ ...e })) : [])
      setNewAccount('')
      setNewType('User')
      setNewAccess('Read')
      setNewDeny(false)
    }
  }, [open, preset, form])

  // 加载候选账号（用户/组/占位符）
  useEffect(() => {
    if (!open) return
    const loadCandidates = async () => {
      try {
        const [users, groups] = await Promise.all([
          call(api.user.list).catch(() => [] as LocalUser[]),
          call(api.user.groups).catch(() => [] as LocalGroup[])
        ])
        const list: { name: string; label: string; type: 'User' | 'Group' }[] = [
          ...PLACEHOLDER_ACCOUNTS.map((p) => ({ name: p.name, label: p.label, type: p.type })),
          ...users.map((u) => ({ name: u.name, label: `${u.name}${u.fullName ? ` (${u.fullName})` : ''}`, type: 'User' as const })),
          ...groups.map((g) => ({ name: g.name, label: `${g.name} (组)`, type: 'Group' as const }))
        ]
        setCandidates(list)
      } catch {
        setCandidates(PLACEHOLDER_ACCOUNTS.map((p) => ({ name: p.name, label: p.label, type: p.type })))
      }
    }
    loadCandidates()
  }, [open])

  const handleAddEntry = () => {
    if (!newAccount.trim()) {
      message.warning('请选择或输入账号')
      return
    }
    // 同账号覆盖
    const idx = entries.findIndex((e) => e.account === newAccount.trim())
    const entry: PresetEntry = {
      account: newAccount.trim(),
      accountType: newType,
      access: newAccess,
      deny: newDeny
    }
    if (idx >= 0) {
      const next = [...entries]
      next[idx] = entry
      setEntries(next)
      message.info('已覆盖同账号条目')
    } else {
      setEntries([...entries, entry])
    }
    setNewAccount('')
    setNewDeny(false)
  }

  const handleRemoveEntry = (account: string) => {
    setEntries(entries.filter((e) => e.account !== account))
  }

  const handleEntryChange = (account: string, field: keyof PresetEntry, value: unknown) => {
    setEntries(entries.map((e) => (e.account === account ? { ...e, [field]: value } : e)))
  }

  const handleSave = async () => {
    if (!preset) return
    const v = await form.validateFields()
    if (entries.length === 0) {
      message.warning('至少添加一个权限条目')
      return
    }
    setSaving(true)
    try {
      if (preset.builtIn) {
        // 内置模板不允许直接保存：复制为自定义
        const newPreset: PermissionPreset = {
          id: `custom-${Date.now()}`,
          name: v.name,
          description: v.description || '',
          builtIn: false,
          category: v.category || '自定义',
          entries,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString()
        }
        await call(() => api.preset.save(newPreset))
        message.success('已基于内置模板创建自定义模板')
      } else {
        await call(() =>
          api.preset.update(preset.id, {
            name: v.name,
            description: v.description || '',
            category: v.category || '自定义',
            entries
          })
        )
        message.success('模板已更新')
      }
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const handleDuplicate = async () => {
    if (!preset) return
    try {
      const v = await form.validateFields()
      const dup = await call(() => api.preset.duplicate(preset.id, `${v.name} 副本`))
      // 立即在新副本上应用当前编辑内容
      await call(() =>
        api.preset.update(dup.id, {
          description: v.description || '',
          category: v.category || '自定义',
          entries
        })
      )
      message.success('已另存为新模板')
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const columns = [
    {
      title: '账号',
      dataIndex: 'account',
      width: 180,
      render: (v: string) => {
        const placeholder = PLACEHOLDER_ACCOUNTS.find((p) => p.name === v)
        return placeholder ? (
          <Tag color="blue">{placeholder.label}</Tag>
        ) : (
          <span>{v}</span>
        )
      }
    },
    {
      title: '类型',
      dataIndex: 'accountType',
      width: 90,
      render: (v: 'User' | 'Group', r: PresetEntry) => (
        <Select
          size="small"
          value={v}
          style={{ width: 90 }}
          options={[
            { label: '用户', value: 'User' },
            { label: '组', value: 'Group' }
          ]}
          onChange={(val) => handleEntryChange(r.account, 'accountType', val)}
        />
      )
    },
    {
      title: '权限',
      dataIndex: 'access',
      width: 120,
      render: (v: PresetEntry['access'], r: PresetEntry) => (
        <Select
          size="small"
          value={v}
          style={{ width: 120 }}
          options={ACCESS_OPTIONS}
          onChange={(val) => handleEntryChange(r.account, 'access', val)}
          disabled={r.deny}
        />
      )
    },
    {
      title: '拒绝',
      dataIndex: 'deny',
      width: 80,
      render: (v: boolean | undefined, r: PresetEntry) => (
        <Switch
          size="small"
          checked={!!v}
          onChange={(val) => handleEntryChange(r.account, 'deny', val)}
        />
      )
    },
    {
      title: '',
      width: 60,
      render: (_: unknown, r: PresetEntry) => (
        <Tooltip title="移除">
          <Button size="small" danger icon={<DeleteOutlined />} onClick={() => handleRemoveEntry(r.account)} />
        </Tooltip>
      )
    }
  ]

  return (
    <Modal
      open={open}
      title={preset?.builtIn ? `查看/复制内置模板：${preset?.name ?? ''}` : `编辑模板：${preset?.name ?? ''}`}
      onCancel={onClose}
      onOk={handleSave}
      okText={preset?.builtIn ? '另存为自定义' : '保存'}
      okButtonProps={{ loading: saving }}
      cancelText="取消"
      width={720}
      footer={(_, { OkBtn, CancelBtn }) => (
        <Space>
          <CancelBtn />
          {preset?.builtIn && (
            <Button icon={<CopyOutlined />} onClick={handleDuplicate} loading={saving}>
              复制为新模板
            </Button>
          )}
          <OkBtn />
        </Space>
      )}
    >
      <Form form={form} layout="vertical">
        <div className="grid grid-cols-2 gap-3">
          <Form.Item
            name="name"
            label="模板名"
            rules={[
              { required: true, message: '请输入模板名' },
              { max: 40, message: '模板名不能超过 40 字符' }
            ]}
          >
            <Input disabled={preset?.builtIn} placeholder="如 部门只读" />
          </Form.Item>
          <Form.Item name="category" label="分类">
            <Input placeholder="如 基础/安全/协作/自定义" />
          </Form.Item>
        </div>
        <Form.Item name="description" label="描述">
          <Input.TextArea rows={2} placeholder="模板用途说明" />
        </Form.Item>
      </Form>

      <div className="border-t border-line pt-3 mt-2">
        <div className="mb-2 text-sm font-medium">权限条目</div>
        <div className="mb-3 p-3 bg-card rounded-card border border-line">
          <div className="flex items-center gap-2 flex-wrap">
            <Select
              showSearch
              placeholder="选择或输入账号"
              value={newAccount || undefined}
              onChange={(v) => {
                setNewAccount(v)
                const c = candidates.find((c) => c.name === v)
                if (c) setNewType(c.type)
              }}
              style={{ width: 240 }}
              options={candidates.map((c) => ({ label: c.label, value: c.name }))}
              filterOption={(input, option) =>
                (option?.label ?? '').toLowerCase().includes(input.toLowerCase())
              }
            />
            <Select
              value={newType}
              onChange={setNewType}
              style={{ width: 90 }}
              options={[
                { label: '用户', value: 'User' },
                { label: '组', value: 'Group' }
              ]}
            />
            <Select
              value={newAccess}
              onChange={(v) => setNewAccess(v)}
              style={{ width: 120 }}
              options={ACCESS_OPTIONS}
              disabled={newDeny}
            />
            <Space size="small">
              <span className="text-xs text-fog">拒绝</span>
              <Switch size="small" checked={newDeny} onChange={setNewDeny} />
            </Space>
            <Button type="primary" icon={<PlusOutlined />} onClick={handleAddEntry}>
              添加
            </Button>
          </div>
        </div>
        <Table
          dataSource={entries}
          rowKey="account"
          columns={columns}
          size="small"
          pagination={false}
          locale={{ emptyText: <Empty description="暂无条目，请在上方添加" /> }}
          scroll={{ y: 320 }}
        />
        {preset?.builtIn && (
          <div className="mt-3 text-xs text-fog">
            内置模板不可直接修改，编辑后将另存为自定义模板。点击"复制为新模板"可保留原内置模板并创建副本。
          </div>
        )}
      </div>
    </Modal>
  )
}
