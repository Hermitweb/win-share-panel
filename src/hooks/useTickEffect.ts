import { useEffect, useRef } from 'react'

/**
 * 订阅一个单调递增的 tick 值，跳过首次挂载时的初值触发。
 * 用于 hotkey 写入 store 的意图 tick：组件挂载时 tick=0 不应触发副作用。
 */
export function useTickEffect(tick: number, fn: () => void): void {
  const prev = useRef(tick)
  useEffect(() => {
    if (prev.current === tick) return
    prev.current = tick
    fn()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- 有意仅依赖 tick：tick 变化是唯一触发条件；fn 每轮渲染皆新建，纳入依赖会破坏"跳过初值"语义并重复触发
  }, [tick])
}
