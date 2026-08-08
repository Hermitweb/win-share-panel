import { useEffect } from 'react'
import { Modal, Form, Input, App } from 'antd'
import { api, call } from '../api'

interface Props {
  open: boolean
  onClose: () => void
  onSuccess: () => void
}

export default function GroupCreateModal({ open, onClose, onSuccess }: Props) {
  const { message } = App.useApp()
  const [form] = Form.useForm()

  useEffect(() => {
    if (open) form.resetFields()
  }, [open, form])

  const handleCreate = async () => {
    const v = await form.validateFields()
    try {
      await call(() => api.group.create({ name: v.name, description: v.description || '' }))
      message.success('组已创建')
      onSuccess()
      onClose()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  return (
    <Modal
      open={open}
      title="新建本地组"
      onCancel={onClose}
      onOk={handleCreate}
      okText="创建"
      cancelText="取消"
    >
      <Form form={form} layout="vertical">
        <Form.Item
          name="name"
          label="组名"
          rules={[
            { required: true, message: '请输入组名' },
            { max: 256, message: '组名不能超过 256 字符' },
            { pattern: /^[A-Za-z0-9._\- \u4e00-\u9fa5]+$/, message: '名称包含非法字符' }
          ]}
        >
          <Input placeholder="如 ProjectEditors" />
        </Form.Item>
        <Form.Item name="description" label="描述">
          <Input.TextArea rows={2} placeholder="组用途描述（可选）" />
        </Form.Item>
      </Form>
    </Modal>
  )
}
