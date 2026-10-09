import { useState } from 'react'
import { App, Button, Card, Popconfirm, Tag, Tooltip } from 'antd'
import { ThunderboltOutlined } from '@ant-design/icons'
import type { ConfigPreset } from '../utils/configPresets'

interface Props {
  title?: string
  presets: ConfigPreset[]
  onApply: (p: ConfigPreset) => Promise<void>
}

/**
 * 配置预设一键应用条：卡片式列出各场景预设，点击 Popconfirm 二次确认（含风险提示
 * 与将写入字段预览）后调用 onApply（由父组件走对应协议 setConfig 并刷新表单）。
 */
export default function ConfigPresetBar({ title = '快捷配置预设', presets, onApply }: Props) {
  const { message } = App.useApp()
  const [applying, setApplying] = useState<string | null>(null)

  const preview = (p: ConfigPreset) =>
    Object.entries(p.values)
      .slice(0, 6)
      .map(([k, v]) => `${k}=${String(v)}`)
      .join('；') +
    (Object.keys(p.values).length > 6 ? ` …（共 ${Object.keys(p.values).length} 项）` : '')

  return (
    <div className="mb-3">
      <div className="text-sm font-medium mb-2 text-fog">
        <ThunderboltOutlined /> {title}
      </div>
      <div className="flex flex-wrap gap-2">
        {presets.map((p) => (
          <Card key={p.id} size="small" className="w-[300px]" styles={{ body: { padding: 10 } }}>
            <div className="flex items-center gap-2 mb-1">
              <span className="font-medium">{p.name}</span>
              {p.risk && (
                <Tooltip title={p.risk}>
                  <Tag color="orange" style={{ margin: 0 }}>
                    注意
                  </Tag>
                </Tooltip>
              )}
            </div>
            <div className="text-xs text-fog mb-2" style={{ minHeight: 32 }}>
              {p.desc}
            </div>
            <Popconfirm
              title={`应用「${p.name}」？`}
              description={
                <div style={{ maxWidth: 320 }}>
                  {p.risk && <div className="text-orange-600 mb-1">⚠ {p.risk}</div>}
                  <div className="text-xs break-all">将写入：{preview(p)}</div>
                </div>
              }
              okText="应用"
              cancelText="取消"
              onConfirm={async () => {
                setApplying(p.id)
                try {
                  await onApply(p)
                  message.success(`已应用「${p.name}」`)
                } catch (e) {
                  message.error((e as Error).message)
                } finally {
                  setApplying(null)
                }
              }}
            >
              <Button size="small" type="primary" ghost loading={applying === p.id}>
                一键应用
              </Button>
            </Popconfirm>
          </Card>
        ))}
      </div>
      <div className="text-xs text-fog mt-1">
        仅写入列出的字段，其余保持当前值；SMB 服务器配置应用前会自动快照，可在"快照历史"回滚。
      </div>
    </div>
  )
}
