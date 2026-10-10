import { useEffect } from 'react'
import { App, Button, Descriptions, Space, Tag } from 'antd'
import { CopyOutlined, ExportOutlined, GithubOutlined, ReloadOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { api } from '../api'
import { useAppStore } from '../stores/appStore'
import { ISSUES_URL, PROJECT_URL, RELEASES_URL } from '../utils/appMeta'
import UpdateChecker from '../components/UpdateChecker'
import SettingsTransferCard from '../components/SettingsTransferCard'
import {
  AlertRulesCard,
  AutoStartCard,
  FirewallCard,
  SecurityCard,
  UsageModeCard,
} from '../components/AppSettingsCards'
import { AppLogPanel, AuditLogPanel } from '../components/LogPanels'

/**
 * 「应用设置」独立页（侧栏一项）。
 *
 * 为什么从「服务配置」的页签里搬出来：那一页的语义是**服务与协议配置**（SMB/NFS/FTP/WebDAV
 * 的服务参数、权限模板、快照），而里面的「应用设置」页签装的却是应用级的东西——使用模式、
 * 主题、开机自启、告警规则、防火墙组内规则、账号体检，外加检查更新与设置导入导出。
 * 两者混在一页会让人（包括我）找不到入口，所以拆成独立页；顺带把原先**错挂在 SMB 页签下**
 * 的审计日志/应用日志也挪过来（应用日志跟 SMB 无关），并在这里补上「关于」信息。
 *
 * 页面结构：关于 → 检查更新 → 备份与恢复 → 偏好与运维 → 日志。
 */
export default function AppSettings() {
  const { message } = App.useApp()
  // 深链直接落到本页（未经 App 冷启动）时也要拿到真实偏好；hydrate 幂等，只在挂载跑一次
  const hydrate = useAppStore((s) => s.hydrate)
  useEffect(() => {
    void hydrate()
  }, [hydrate])

  // 版本信息只取一次：它不会在运行期变化，staleTime 设成 Infinity 免得来回重取
  const { data: info, error: infoError } = useQuery({
    queryKey: ['app-info'],
    queryFn: () => api.system.appInfo(),
    staleTime: Infinity,
  })

  const openUrl = (url: string) => {
    // 走主进程的白名单校验（只放行 http/https），不用 window.open：本窗口 sandbox 且未设 setWindowOpenHandler
    void api.system.openExternal(url).catch((e) => message.error((e as Error).message))
  }

  const copyUrl = async (url: string, what: string) => {
    try {
      await navigator.clipboard.writeText(url)
      message.success(`${what}已复制`)
    } catch {
      const ta = document.createElement('textarea')
      ta.value = url
      document.body.appendChild(ta)
      ta.select()
      document.execCommand('copy')
      document.body.removeChild(ta)
      message.success(`${what}已复制`)
    }
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-lg font-semibold m-0">应用设置</h2>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={() => openUrl(RELEASES_URL)}>
            查看 releases
          </Button>
        </Space>
      </div>

      <div className="glass-card p-4 mb-3" data-testid="about-card">
        <div className="text-sm font-medium mb-3">关于</div>
        <Descriptions size="small" column={1} bordered>
          <Descriptions.Item label="版本号">
            {info ? (
              <Space>
                <Tag color="blue" style={{ margin: 0 }}>
                  v{info.version}
                </Tag>
                <span className="text-xs text-fog">
                  构建于 {info.platform} / {info.arch}
                </span>
              </Space>
            ) : infoError ? (
              <span className="text-red-500">读取失败：{(infoError as Error).message}</span>
            ) : (
              '读取中…'
            )}
          </Descriptions.Item>
          <Descriptions.Item label="项目地址">
            <Space wrap>
              <a onClick={() => openUrl(PROJECT_URL)} className="break-all">
                <GithubOutlined /> {PROJECT_URL}
              </a>
              <Button
                size="small"
                icon={<CopyOutlined />}
                onClick={() => copyUrl(PROJECT_URL, '项目地址')}
              >
                复制
              </Button>
              <Button size="small" icon={<ExportOutlined />} onClick={() => openUrl(PROJECT_URL)}>
                在浏览器打开
              </Button>
            </Space>
          </Descriptions.Item>
          <Descriptions.Item label="问题反馈">
            <a onClick={() => openUrl(ISSUES_URL)}>{ISSUES_URL}</a>
          </Descriptions.Item>
          <Descriptions.Item label="下载页">
            <Space wrap>
              <a onClick={() => openUrl(RELEASES_URL)} className="break-all">
                {RELEASES_URL}
              </a>
              <Button size="small" icon={<ExportOutlined />} onClick={() => openUrl(RELEASES_URL)}>
                在浏览器打开
              </Button>
            </Space>
          </Descriptions.Item>
          <Descriptions.Item label="运行时">
            <span className="text-xs text-fog">
              {info
                ? `Electron ${info.electron} · Chromium ${info.chrome} · Node ${info.node}`
                : '读取中…'}
            </span>
          </Descriptions.Item>
        </Descriptions>
        <div className="mt-2 text-xs text-fog">
          本项目为 Windows 文件夹共享控制面板，需以管理员身份运行；不会自动下载或安装更新。
        </div>
      </div>

      <UpdateChecker />
      <SettingsTransferCard />

      <div className="text-sm font-medium mt-4 mb-2">偏好与运维</div>
      <UsageModeCard />
      <AutoStartCard />
      <AlertRulesCard />
      <FirewallCard />
      <SecurityCard />

      <div className="text-sm font-medium mt-4 mb-2">日志</div>
      <AppLogPanel />
      <AuditLogPanel />
    </div>
  )
}
