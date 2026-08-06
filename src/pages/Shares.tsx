import { useEffect, useState } from 'react'
import {
  Table,
  Button,
  Modal,
  Form,
  Input,
  Select,
  Switch,
  Tag,
  Space,
  Popconfirm,
  App,
  Tooltip,
  Upload
} from 'antd'
import {
  PlusOutlined,
  ReloadOutlined,
  DeleteOutlined,
  ExportOutlined,
  ImportOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined
} from '@ant-design/icons'
import type { UploadProps } from 'antd'
import { api, call } from '../api'
import type { Share, PermissionPreset } from '../types'

export default function Shares() {
  const { message } = App.useApp()
  const [shares, setShares] = useState<Share[]>([])
  const [presets, setPresets] = useState<PermissionPreset[]>([])
  const [loading, setLoading] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [form] = Form.useForm()

  const load = async () => {
    setLoading(true)
    try {
      const [s, p] = await Promise.all([call(api.share.list), call(api.preset.list)])
      setShares(s)
      setPresets(p)
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setLoading(false)
    }
  }
  useEffect(() => {
    load()
  }, [])

  const handleCreate = async () => {
    const v = await form.validateFields()
    try {
      const created = await call(() =>
        api.share.create({
          name: v.name,
          path: v.path,
          description: v.description,
          encrypted: !!v.encrypted
        })
      )
      if (v.presetId) {
        await call(() => api.preset.apply(created.name, v.presetId, 'overwrite'))
      }
      message.success('创建成功')
      setModalOpen(false)
      form.resetFields()
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleDelete = async (name: string) => {
    try {
      await call(() => api.share.delete(name))
      message.success('已删除')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleToggle = async (name: string, enabled: boolean) => {
    try {
      await call(() => api.share.toggle(name, !enabled))
      message.success(!enabled ? '已启用' : '已禁用')
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const handleExport = async () => {
    try {
      const json = await call(api.share.exportConfig)
      const blob = new Blob([json], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `winshare-export-${Date.now()}.json`
      a.click()
      URL.revokeObjectURL(url)
      message.success('已导出')
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const importProps: UploadProps = {
    beforeUpload: async (file) => {
      try {
        const text = await file.text()
        await call(() => api.share.importConfig(text))
        message.success('导入完成')
        load()
      } catch (e) {
        message.error((e as Error).message)
      }
      return false
    },
    showUploadList: false,
    accept: '.json'
  }

  // 拖拽文件夹进窗口快速创建
  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    const f = e.dataTransfer.files[0]
    if (f) {
      const path = (f as File & { path?: string }).path
      if (path) {
        setModalOpen(true)
        form.setFieldsValue({ path })
      }
    }
  }

  const columns = [
    { title: '名称', dataIndex: 'name', width: 160 },
    { title: '路径', dataIndex: 'path', ellipsis: true },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    { title: '连接数', dataIndex: 'concurrentUsers', width: 80 },
    {
      title: '加密',
      dataIndex: 'encrypted',
      width: 70,
      render: (v: boolean) => (v ? <Tag color="blue">是</Tag> : <span className="text-fog">-</span>)
    },
    {
      title: '操作',
      width: 140,
      render: (_: unknown, r: Share) => (
        <Space>
          <Tooltip title={r.status === 'Enabled' ? '禁用' : '启用'}>
            <Button
              size="small"
              icon={r.status === 'Enabled' ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
              onClick={() => handleToggle(r.name, r.status === 'Enabled')}
            />
          </Tooltip>
          <Popconfirm title="确认删除该共享？" onConfirm={() => handleDelete(r.name)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      )
    }
  ]

  return (
    <div onDrop={handleDrop} onDragOver={(e) => e.preventDefault()}>
      <div className="flex items-center justify-between mb-4">
        <div>
          <h1 className="text-xl font-semibold">共享管理</h1>
          <p className="text-xs text-fog mt-1">提示：拖拽文件夹到本页可快速创建共享</p>
        </div>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={load}>
            刷新
          </Button>
          <Button icon={<ExportOutlined />} onClick={handleExport}>
            导出
          </Button>
          <Upload {...importProps}>
            <Button icon={<ImportOutlined />}>导入</Button>
          </Upload>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => setModalOpen(true)}>
            新建共享
          </Button>
        </Space>
      </div>
      <div className="glass-card p-3">
        <Table
          dataSource={shares}
          columns={columns}
          rowKey="name"
          loading={loading}
          pagination={{ pageSize: 10 }}
          size="middle"
        />
      </div>
      <Modal
        open={modalOpen}
        title="新建共享"
        onCancel={() => setModalOpen(false)}
        onOk={handleCreate}
        okText="创建"
        cancelText="取消"
      >
        <Form form={form} layout="vertical">
          <Form.Item name="name" label="共享名" rules={[{ required: true, message: '请输入共享名' }]}>
            <Input placeholder="如 SharedDocs" />
          </Form.Item>
          <Form.Item name="path" label="本地路径" rules={[{ required: true, message: '请输入或拖入路径' }]}>
            <Input placeholder="如 D:\Share" />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input />
          </Form.Item>
          <Form.Item name="presetId" label="权限模板（可选）">
            <Select allowClear placeholder="选择模板一键应用权限">
              {presets.map((p) => (
                <Select.Option key={p.id} value={p.id}>
                  {p.name} - {p.description}
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item name="encrypted" label="启用加密" valuePropName="checked" initialValue={false}>
            <Switch />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
