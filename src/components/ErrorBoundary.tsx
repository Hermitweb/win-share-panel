import { Component, type ErrorInfo, type ReactNode } from 'react'
import { api } from '../api'

interface Props {
  children: ReactNode
}

interface State {
  error: Error | null
  info: string
}

// E6：渲染层崩溃边界——白屏变可见报错，错误详情可复制并持久化到主进程日志
export default class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, info: '' }

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    const detail = `${error.stack || error.message}\n组件栈:${info.componentStack || '(无)'}`
    this.setState({ info: detail })
    try {
      void api.log.write('error', `[ErrorBoundary] ${detail}`)
    } catch {
      // log.write 本身失败不再级联
    }
  }

  private copy = (): void => {
    const text = `${this.state.error?.stack || this.state.error?.message || ''}\n${this.state.info}`
    if (navigator.clipboard?.writeText) {
      void navigator.clipboard.writeText(text)
    } else {
      // 剪贴板 API 不可用时回退临时文本域
      const ta = document.createElement('textarea')
      ta.value = text
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
    }
  }

  render(): ReactNode {
    if (!this.state.error) return this.props.children
    return (
      <div style={{ padding: 24, maxWidth: 720, margin: '0 auto' }}>
        <h2 style={{ color: '#c00' }}>界面渲染出错</h2>
        <p style={{ fontSize: 13 }}>
          错误已记录到应用日志（设置页"应用日志"可查看/复制），可点击下方按钮重试本页。
        </p>
        <pre
          style={{
            background: 'var(--code-bg)',
            padding: 12,
            borderRadius: 8,
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-all',
            maxHeight: 260,
            overflow: 'auto',
            fontSize: 12,
          }}
        >
          {this.state.error.stack || this.state.error.message}
        </pre>
        <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
          <button
            className="rounded-btn px-3 py-1 bg-white/70 dark:bg-white/10"
            onClick={this.copy}
          >
            复制错误详情
          </button>
          <button
            className="rounded-btn px-3 py-1 bg-primary text-white"
            onClick={() => this.setState({ error: null, info: '' })}
          >
            重试
          </button>
        </div>
      </div>
    )
  }
}
