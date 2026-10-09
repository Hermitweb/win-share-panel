import type { ReactElement, ReactNode } from 'react'
import type { TableColumnsType } from 'antd'
import { Button, Empty, Popconfirm, Space, Spin, Table } from 'antd'
import { ReloadOutlined } from '@ant-design/icons'

/**
 * R-5：三协议权限面板（Nfs/Ftp/Webdav）共用的展示外壳。
 * 逐字复现抽象前的 DOM：Spin 包裹 → 头部说明 + 重新加载/保存 → Table → 添加区（Space wrap + 提示）。
 * 协议特有的行类型、映射、列定义、空态与文案、添加区控件由面板通过 props 注入。
 */
export interface ProtocolPermissionShellProps<TRow> {
  loading: boolean
  saving: boolean
  /** 头部说明（协议特有文案，可含 <code>） */
  headerText: ReactNode
  /** 「重新加载」 */
  onReload: () => void
  /** 「保存」 */
  onSave: () => void
  /** 保存二次确认标题（协议特有：确认覆盖当前 NFS 客户端权限？ / FTP 授权规则？ / WebDAV 作者规则？） */
  saveConfirmTitle: string
  columns: TableColumnsType<TRow>
  rows: TRow[]
  rowKey: string
  /** 空态文案（协议特有：暂无客户端规则 / 暂无授权规则 / 暂无作者规则） */
  emptyText: string
  /** 添加区标题（协议特有：添加客户端规则 / 添加授权规则 / 添加作者规则） */
  addTitle: string
  /** 添加区控件（协议特有：Input/Select/Button 组合） */
  addBox: ReactNode
  /** 添加区底部提示（协议特有：Tag + 说明句） */
  hint: ReactNode
}

export function ProtocolPermissionShell<TRow>(p: ProtocolPermissionShellProps<TRow>): ReactElement {
  return (
    <Spin spinning={p.loading}>
      <div className="mb-3 flex items-center justify-between">
        <span className="text-sm text-fog">{p.headerText}</span>
        <Space>
          <Button size="small" icon={<ReloadOutlined />} onClick={p.onReload}>
            重新加载
          </Button>
          <Popconfirm title={p.saveConfirmTitle} onConfirm={p.onSave}>
            <Button size="small" type="primary" loading={p.saving}>
              保存
            </Button>
          </Popconfirm>
        </Space>
      </div>
      <Table
        dataSource={p.rows}
        rowKey={p.rowKey}
        columns={p.columns}
        pagination={false}
        size="small"
        locale={{ emptyText: <Empty description={p.emptyText} /> }}
      />
      <div className="mt-4 p-3 rounded-card bg-white/60 dark:bg-white/5">
        <div className="text-xs text-fog mb-2">{p.addTitle}</div>
        <Space wrap>{p.addBox}</Space>
        <div className="mt-2 text-xs text-fog">{p.hint}</div>
      </div>
    </Spin>
  )
}
