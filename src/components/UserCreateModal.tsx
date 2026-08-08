import { useEffect, useState } from 'react'
import { Modal, Form, Input, Switch, App, Select, Progress, Tag, Tooltip, Space } from 'antd'
import { ThunderboltOutlined } from '@ant-design/icons'
import { api, call } from '../api'
import type { LocalGroup } from '../types'
import { generatePassword, evaluateStrength, STRENGTH_LABEL, STRENGTH_COLOR } from '../utils/password'

interface Props {
  open: boolean
  onClose: () => void
  onSuccess: () => void
}

export default function UserCreateModal({ open, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [form] = Form.useForm()
  const [saving, setSaving] = useState(false)
  const [groups, setGroups] = useState<LocalGroup[]>([])
  const [selectedGroups, setSelectedGroups] = useState<string[]>([])
  const [pwdStrength, setPwdStrength] = useState<'weak' | 'medium' | 'strong' | null>(null)

  useEffect(() => {
    if (open) {
      form.resetFields()
      form.setFieldsValue({
        enabled: true,
        userMayChangePassword: true,
        passwordNeverExpires: true
      })
      setSelectedGroups([])
      setPwdStrength(null)
      loadGroups()
    }
  }, [open, form])

  const loadGroups = async () => {
    try {
      const g = await call(api.user.groups)
      setGroups(g)
    } catch {
      // 加载失败不影响创建
    }
  }

  const handleGeneratePwd = () => {
    const pwd = generatePassword(12)
    form.setFieldsValue({ password: pwd, confirmPassword: pwd })
    setPwdStrength(evaluateStrength(pwd))
  }

  const handlePwdChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const v = e.target.value
    setPwdStrength(v ? evaluateStrength(v) : null)
  }

  const strengthPercent = pwdStrength === 'strong' ? 100 : pwdStrength === 'medium' ? 66 : 33

  const handleCreate = async () => {
    const v = await form.validateFields()
    if (v.password !== v.confirmPassword) {
      message.error('两次输入的密码不一致')
      return
    }
    setSaving(true)
    try {
      await call(() =>
        api.user.create({
          name: v.name,
          password: v.password,
          fullName: v.fullName || '',
          description: v.description || '',
          enabled: v.enabled,
          passwordChangeable: v.userMayChangePassword,
          passwordExpires: !v.passwordNeverExpires
        })
      )
      // 创建后分配到所选组
      if (selectedGroups.length > 0) {
        const failed: string[] = []
        for (const g of selectedGroups) {
          try {
            await call(() => api.group.addMember(g, v.name))
          } catch {
            failed.push(g)
          }
        }
        if (failed.length) {
          message.warning(`用户已创建，但 ${failed.length} 个组分配失败：${failed.join(', ')}`)
        } else {
          message.success(`用户已创建并加入 ${selectedGroups.length} 个组`)
        }
      } else {
        message.success('用户已创建')
      }
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      open={open}
      title="新建本地用户"
      onCancel={onClose}
      onOk={handleCreate}
      okText="创建"
      cancelText="取消"
      confirmLoading={saving}
      width={520}
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="name"
          label="用户名"
          rules={[
            { required: true, message: '请输入用户名' },
            { max: 20, message: '用户名不能超过 20 字符' },
            { pattern: /^[A-Za-z0-9._-]+$/, message: '仅支持字母、数字、点、下划线、连字符' }
          ]}
        >
          <Input placeholder="如 john.doe" />
        </Form.Item>
        <Form.Item
          name="password"
          label={
            <Space>
              <span>密码</span>
              <Tooltip title="生成 12 位随机强密码">
                <Tag
                  color="blue"
                  style={{ cursor: 'pointer' }}
                  onClick={handleGeneratePwd}
                >
                  <ThunderboltOutlined /> 生成密码
                </Tag>
              </Tooltip>
            </Space>
          }
          rules={[
            { required: true, message: '请输入密码' },
            { min: 1, message: '密码不能为空' }
          ]}
        >
          <Input.Password
            placeholder="输入密码或点击生成"
            autoComplete="new-password"
            onChange={handlePwdChange}
          />
        </Form.Item>
        {pwdStrength && (
          <div className="-mt-2 mb-3 flex items-center gap-2">
            <Progress
              percent={strengthPercent}
              showInfo={false}
              strokeColor={STRENGTH_COLOR[pwdStrength]}
              size="small"
              style={{ maxWidth: 120, margin: 0 }}
            />
            <Tag color={STRENGTH_COLOR[pwdStrength]}>{STRENGTH_LABEL[pwdStrength]}</Tag>
          </div>
        )}
        <Form.Item
          name="confirmPassword"
          label="确认密码"
          rules={[{ required: true, message: '请再次输入密码' }]}
        >
          <Input.Password placeholder="再次输入密码" autoComplete="new-password" />
        </Form.Item>
        <Form.Item name="fullName" label="全名">
          <Input placeholder="用户显示名（可选）" />
        </Form.Item>
        <Form.Item name="description" label="描述">
          <Input.TextArea rows={2} placeholder="账号用途描述（可选）" />
        </Form.Item>
        <Form.Item name="enabled" label="启用账号" valuePropName="checked">
          <Switch />
        </Form.Item>
        <Form.Item name="userMayChangePassword" label="允许用户修改密码" valuePropName="checked">
          <Switch />
        </Form.Item>
        <Form.Item name="passwordNeverExpires" label="密码永不过期" valuePropName="checked">
          <Switch />
        </Form.Item>
        <Form.Item label="分配到组（可选）">
          <Select
            mode="multiple"
            placeholder="选择要加入的组（可多选）"
            value={selectedGroups}
            onChange={setSelectedGroups}
            options={groups.map((g) => ({
              label: `${g.name}${g.description ? ` (${g.description})` : ''}`,
              value: g.name
            }))}
            optionFilterProp="label"
            allowClear
            maxTagCount={3}
          />
        </Form.Item>
      </Form>
    </Modal>
  )
}
