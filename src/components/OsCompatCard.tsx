import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Alert, Card, Descriptions, Space, Tag, Tooltip, Button, Collapse } from 'antd'
import {
  DesktopOutlined,
  WarningOutlined,
  CheckCircleOutlined,
  CodeOutlined,
} from '@ant-design/icons'
import { api } from '../api'
import type { PsRuntimeDiagnosis, PsRuntimeStatus } from '../types'

/** 引擎人类可读名（与 electron/services/psRuntime.ts 的 engineLabel 对齐） */
function engineLabelOf(ps: PsRuntimeStatus | undefined): string {
  switch (ps?.engine) {
    case 'windowsPowerShell':
      return 'Windows PowerShell 5.1'
    case 'powershellCore':
      return 'PowerShell 7+（pwsh）'
    default:
      return '未知引擎'
  }
}

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
  // v1.4.0：PowerShell 运行时探测
  const { data: ps, isFetching: psFetching } = useQuery({
    queryKey: ['ps-runtime'],
    queryFn: () => api.system.psRuntime(),
    staleTime: 60 * 1000,
  })
  const [diagnosed, setDiagnosed] = useState<PsRuntimeDiagnosis | null>(null)

  if (isLoading || !os) return null

  const f = os.features
  // v1.4.0 多版本兼容：用 realPathFound（是否至少有一个真实绝对路径候选）判断"缺失"，
  // 而非裸名乐观的 exists（PATH 兜底不算真的找到了 PowerShell）
  const psOk = ps ? ps.realPathFound : true
  const handleDiagnose = async () => {
    if (!ps) return
    try {
      const d = await api.system.psDiagnose(psOk ? '' : ps.resolvedPath)
      setDiagnosed(d)
    } catch (e) {
      setDiagnosed({
        ok: false,
        reason: '诊断调用失败',
        suggestions: [(e as Error).message],
        rawError: String(e),
      })
    }
  }
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
            key: 'ps',
            label: 'PowerShell',
            children: psOk ? (
              <Space size={4} wrap>
                <CheckCircleOutlined style={{ color: '#52c41a' }} />
                <Tag
                  color={ps?.engine === 'powershellCore' ? 'blue' : 'green'}
                  style={{ margin: 0 }}
                >
                  {engineLabelOf(ps)}
                </Tag>
                <span className="text-xs" style={{ wordBreak: 'break-all' }}>
                  {ps?.resolvedPath}
                </span>
              </Space>
            ) : (
              <Tooltip title="PowerShell 可执行文件缺失（5.1 与 7.x 的真实路径都不存在），所有共享/会话/用户功能不可用；点「诊断」查看修复建议">
                <Space size={4} style={{ cursor: 'pointer' }} onClick={handleDiagnose}>
                  <WarningOutlined style={{ color: '#ff4d4f' }} />
                  <Tag color="red">缺失</Tag>
                  <span className="text-xs text-fog" style={{ wordBreak: 'break-all' }}>
                    {ps?.resolvedPath}
                  </span>
                </Space>
              </Tooltip>
            ),
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
      {!psOk && (
        <Alert
          className="mt-2"
          type="error"
          showIcon
          message="PowerShell 可执行文件缺失（5.1 与 7.x 的真实路径都不存在），所有依赖 PowerShell 的功能不可用"
          description={
            <Space direction="vertical" size={4}>
              <Button
                size="small"
                type="primary"
                danger
                icon={<WarningOutlined />}
                loading={psFetching}
                onClick={handleDiagnose}
              >
                诊断并获取修复建议
              </Button>
              {diagnosed && (
                <Collapse
                  ghost
                  size="small"
                  items={[
                    {
                      key: 'diag',
                      label: <span className="text-xs">诊断：{diagnosed.reason}</span>,
                      children: (
                        <ul className="text-xs text-fog m-0" style={{ paddingLeft: 16 }}>
                          {diagnosed.suggestions.map((s, i) => (
                            <li key={i} style={{ marginBottom: 4 }}>
                              {s}
                            </li>
                          ))}
                        </ul>
                      ),
                    },
                  ]}
                />
              )}
            </Space>
          }
        />
      )}
      {/* 多引擎候选摘要（v1.4.0）：展示这台机器上实际存在的所有 PowerShell */}
      {ps?.candidates &&
        ps.candidates.length > 0 &&
        ps.candidates.some((c) => c.exists && /^[A-Za-z]:[\\/]/.test(c.path)) && (
          <div className="mt-2">
            <div className="text-xs font-medium mb-1 flex items-center gap-1">
              <CodeOutlined /> 本机 PowerShell 候选（按优先级）
            </div>
            <Space direction="vertical" size={2} style={{ width: '100%' }}>
              {ps.candidates
                .filter((c) => c.exists && /^[A-Za-z]:[\\/]/.test(c.path))
                .map((c) => (
                  <Space key={c.path} size={4} wrap>
                    <Tag
                      color={c.path === ps.resolvedPath ? 'blue' : 'default'}
                      style={{ margin: 0 }}
                    >
                      {c.path === ps.resolvedPath
                        ? '使用中'
                        : c.engine === 'windowsPowerShell'
                          ? '5.1'
                          : '7+'}
                    </Tag>
                    <span className="text-xs text-fog" style={{ wordBreak: 'break-all' }}>
                      {c.path}
                    </span>
                    <span className="text-xs text-fog">（{c.note}）</span>
                  </Space>
                ))}
            </Space>
          </div>
        )}
    </Card>
  )
}
