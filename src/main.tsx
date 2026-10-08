import React from 'react'
import ReactDOM from 'react-dom/client'
import { HashRouter } from 'react-router-dom'
import { ConfigProvider, App as AntdApp } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import App from './App'
import ErrorBoundary from './components/ErrorBoundary'
import { api } from './api'
import './index.css'

// E6：全局错误转发——未捕获异常与 Promise 拒绝持久化到主进程日志（打包后仍可追溯复制）
window.addEventListener('error', (e) => {
  const detail = `${e.message} @${e.filename || 'unknown'}:${e.lineno || 0}:${e.colno || 0}${
    e.error?.stack ? '\n' + e.error.stack : ''
  }`
  void api.log?.write('error', `[window.onerror] ${detail}`)
})
window.addEventListener('unhandledrejection', (e) => {
  const r = e.reason
  const detail = r instanceof Error ? (r.stack ?? r.message) : String(r)
  void api.log?.write('error', `[unhandledrejection] ${detail.slice(0, 3000)}`)
})

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider
      locale={zhCN}
      theme={{
        token: {
          colorPrimary: '#7EC8F0',
          borderRadius: 12,
          colorBgContainer: 'rgba(255,255,255,0.7)',
        },
      }}
    >
      <AntdApp>
        <HashRouter>
          <ErrorBoundary>
            <App />
          </ErrorBoundary>
        </HashRouter>
      </AntdApp>
    </ConfigProvider>
  </React.StrictMode>,
)
