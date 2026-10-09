import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, fireEvent, cleanup } from '@testing-library/react'
import { App as AntdApp } from 'antd'
import ConfigPresetBar from './ConfigPresetBar'
import type { ConfigPreset } from '../utils/configPresets'

// globals:false 下 RTL auto-cleanup 不注册，手动逐用例卸载，避免 DOM 跨用例累积
afterEach(cleanup)

const PRESETS: ConfigPreset[] = [
  { id: 'p1', name: '安全加固', desc: '强制签名等', values: { a: 1 } },
  { id: 'p2', name: '兼容优先', desc: '放开限制', risk: '会降低安全性', values: { b: 2 } },
]

function renderBar(onApply: (p: ConfigPreset) => Promise<void>) {
  return render(
    <AntdApp>
      <ConfigPresetBar presets={PRESETS} onApply={onApply} />
    </AntdApp>,
  )
}

describe('ConfigPresetBar（B3 组件测试基建）', () => {
  it('渲染全部预设卡片与标题', () => {
    renderBar(vi.fn())
    expect(screen.getByText('快捷配置预设')).toBeInTheDocument()
    expect(screen.getByText('安全加固')).toBeInTheDocument()
    expect(screen.getByText('兼容优先')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: /一键应用/ })).toHaveLength(2)
  })

  it('二次确认后调用 onApply 并传入对应预设', async () => {
    const onApply = vi.fn().mockResolvedValue(undefined)
    renderBar(onApply)
    const applyButtons = screen.getAllByRole('button', { name: /一键应用/ })
    fireEvent.click(applyButtons[0])
    // Popconfirm 渲染到 body 的浮层（.ant-popover/.ant-popconfirm）；按去空格文本
    // 精确匹配"应用"确认按钮，避免与"一键应用"触发按钮冲突、不依赖内部类名
    await vi.waitFor(() =>
      expect(document.querySelector('.ant-popover, .ant-popconfirm')).not.toBeNull(),
    )
    const okBtn = Array.from(
      document.querySelectorAll('.ant-popover button, .ant-popconfirm button'),
    ).find((b) => (b.textContent || '').replace(/\s/g, '') === '应用')
    expect(okBtn).toBeTruthy()
    fireEvent.click(okBtn!)
    await vi.waitFor(() => {
      expect(onApply).toHaveBeenCalledTimes(1)
    })
    expect(onApply.mock.calls[0][0]).toMatchObject({ id: 'p1' })
  })

  it('带 risk 的预设展示注意标记', () => {
    renderBar(vi.fn())
    // 仅第二个预设带 risk
    expect(screen.getAllByText('注意')).toHaveLength(1)
  })
})
