import { useQuery } from '@tanstack/react-query'
import { Alert, Card, Descriptions, Space, Tag, Tooltip } from 'antd'
import { DesktopOutlined } from '@ant-design/icons'
import { api } from '../api'

function Yes({ label }: { label: string }) {
  return <Tag color="green">{label}</Tag>
}
function No({ label, why }: { label: string; why?: string }) {
  const tag = <Tag color="red">{label}</Tag>
  return why ? <Tooltip title={why}>{tag}</Tooltip> : tag
}
function Part({ label, why }: { label: string; why?: string }) {
  const tag = <Tag color="orange">{label}</Tag>
  return why ? <Tooltip title={why}>{tag}</Tooltip> : tag
}

/**
 * 系统环境/版本适配卡（OS matrix）：展示运行时功能探测结果——
 * SMB 全 SKU 可用；NFS 服务端仅 Server；FTP/WebDAV 依赖 IIS（家庭版不可用）；
 * SMB QUIC 需 Server 2022 / Win11 23H2+。各协议面板/弹窗据同批探测做降级。
 */
export default function OsCompatCard() {
  const { data: os, isLoading } = useQuery({
    queryKey: ['os-info'],
    queryFn: () => api.system.osInfo(),
    staleTime: 5 * 60 * 1000,
  })

  if (isLoading || !os) return null

  const f = os.features
  return (
    <Card
      size="small"
      className="mb-3"
      title={
        <Space size={6}>
          <DesktopOutlined />
          系统环境
          <span className="text-xs font-normal text-fog">（版本适配自动探测）</span>
        </Space>
      }
    >
      <Descriptions
        size="small"
        column={2}
        items={[
          {
            key: 'os',
            label: '操作系统',
            children: (
              <span className="text-xs">
                {os.caption} · Build {os.buildNumber} · {os.skuName}
              </span>
            ),
          },
          {
            key: 'host',
            label: '计算机',
            children: <span className="text-xs">{os.hostname}</span>,
          },
          {
            key: 'smb',
            label: 'SMB 共享管理',
            children: f.smbShareModule ? (
              <Yes label="完整支持" />
            ) : (
              <No label="不可用" why="Get-SmbShare cmdlet 缺失" />
            ),
          },
          {
            key: 'nfs',
            label: 'NFS',
            children: f.nfsServerCmdlets ? (
              <Yes label="服务端角色" />
            ) : os.isServer ? (
              <Part
                label="可安装服务端角色"
                why="服务器系统：可在协议配置页一键安装 FS-NFS-Service"
              />
            ) : (
              <Part
                label="仅客户端能力"
                why="Win10/11 专业/企业版可安装 NFS 客户端（挂载他人共享），但不能创建 NFS 共享；家庭版无 NFS 组件"
              />
            ),
          },
          {
            key: 'iis',
            label: 'IIS（FTP/WebDAV）',
            children: f.iisAvailable ? (
              <Yes label="可承载" />
            ) : (
              <No
                label="不可用"
                why={
                  os.isHomeEdition
                    ? 'Windows 家庭版不含 IIS 组件，FTP/WebDAV 管理不可用；建议升级专业版或使用 SMB 共享'
                    : '本机 IIS 可选功能不可用'
                }
              />
            ),
          },
          {
            key: 'quic',
            label: 'SMB QUIC',
            children: f.smbQuicConfig ? (
              <Yes label="支持（配置项可用）" />
            ) : (
              <Part
                label="不支持"
                why="需 Server 2022 / Win11 23H2+；旧系统该项在 SMB 配置页自动隐藏/保存时跳过"
              />
            ),
          },
        ]}
      />
      {!f.iisAvailable && !os.isServer && (
        <Alert
          className="mt-2"
          type="info"
          showIcon
          message="本机无法使用 FTP/WebDAV：设置页对应面板将保持安装引导降级态，SMB 共享不受影响。"
        />
      )}
    </Card>
  )
}
