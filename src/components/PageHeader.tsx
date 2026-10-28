import type { ReactNode } from 'react'

/**
 * 统一页头：标题（与左侧导航名一致）+ 可选说明 + 右侧操作区。
 *
 * 六个页面共用本组件，消除两处不一致：
 * - 导航名与页内标题对不上（侧栏「服务配置」→ 页内「服务器配置」）
 * - 操作按钮位置/样式各页不一（有的用 h1 + Space，有的用 h2 + 裸 div）
 *
 * extra 由调用方自己包 <Space wrap>，本组件只管左右分栏与垂直对齐。
 */
export default function PageHeader({
  title,
  description,
  extra,
}: {
  title: string
  description?: ReactNode
  extra?: ReactNode
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-2 mb-4">
      <div className="min-w-0">
        <h1 className="text-xl font-semibold m-0">{title}</h1>
        {description ? <p className="text-xs text-fog mt-1 mb-0">{description}</p> : null}
      </div>
      {extra}
    </div>
  )
}
