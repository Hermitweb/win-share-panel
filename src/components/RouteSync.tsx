import { useEffect } from 'react'
import { useLocation } from 'react-router-dom'
import { useUiStore } from '../stores/uiStore'

/**
 * 把 react-router 当前路径同步到 uiStore，供 useHotkeys 等模块按路由分流。
 */
export default function RouteSync(): null {
  const pathname = useLocation().pathname
  const setRoute = useUiStore((s) => s.setRoute)
  useEffect(() => {
    setRoute(pathname)
  }, [pathname, setRoute])
  return null
}
