import { useState } from 'react'

/**
 * 渲染期调整 state（React 官方 "adjusting state during render"，见
 * https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes）。
 *
 * 批 2 渲染层债清零：`react-hooks/set-state-in-effect` 会报 effect 体内的同步 setState
 * （docs/audit/05-renderer-debt.md §0.4 P-1/P-5），而"打开/切换目标就把纯本地态归零"正是
 * 官方推荐用渲染期调整而不是 effect 的场景。
 *
 * 为什么不用 `key` 重挂载：11 个 modal/drawer 的挂载点分散在 App/Settings/Shares/SharesWithOps，
 * 铺 key 会改变挂载时机并丢掉 antd 的关闭过渡（规格 §1.4 A 类理由 2）。
 *
 * `useResetOnOpen`：open 由 false 变 true（含首次即以 open=true 挂载）时调用一次 reset；
 * open 由 true 变 false 时不调用——关闭过程中不重置，避免关闭动画里内容闪回初始态。
 */
export function useResetOnOpen(open: boolean, reset: () => void): void {
  const [prevOpen, setPrevOpen] = useState(false)
  if (open !== prevOpen) {
    setPrevOpen(open)
    if (open) reset()
  }
}

/**
 * `useResetOnKeyChange`：key 变化时调用一次 reset，key 由调用方拼装
 * （惯例：`${open}|${目标标识}` 或 `${open}|${目标}|${重读序号 nonce}`）。
 *
 * 首次渲染也会执行一次（prevKey 初值为 null），与迁移前 `useEffect(..., [deps])` 在挂载时
 * 执行一次的语义对齐；需要"仅打开时"语义时由调用方在 reset 内按 open 守卫。
 */
export function useResetOnKeyChange(key: string, reset: () => void): void {
  const [prevKey, setPrevKey] = useState<string | null>(null)
  if (prevKey !== key) {
    setPrevKey(key)
    reset()
  }
}
