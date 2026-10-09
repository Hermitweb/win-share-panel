// vitest 全局 setup（B3）：jsdom 环境补 antd 依赖的浏览器 API 桩，注入 jest-dom 匹配器。
// node 环境用例（electron/*、src/*.test.ts）不触碰 DOM，守卫式判空保证零副作用。
import '@testing-library/jest-dom/vitest'
// RTL 的 waitFor 默认 asyncUtilTimeout 只有 **1s**——它和 vitest 的 testTimeout 是两个**不同的**超时：
// vitest.config.ts 已把 testTimeout 统一为 30s，但管不到 RTL 自己的异步等待窗口。47 个重型
// antd+jsdom 文件并发时，antd message 的异步 portal 公告偶发超过 1s，于是出现随负载漂移的间歇红。
// 这里统一放宽异步等待窗口以消除基建抖动——**不是**修好了某个慢测试，也**不是**性能改进；
// 用例、断言、等待时长之外的逻辑一律未动。
// 从 @testing-library/dom 引入：它与 @testing-library/react 共用同一实例（实测解析到同一路径），
// 该包在 node 环境可安全加载；configure 的调用点放在下方 DOM 守卫内，node project 侧不生效。
import { configure } from '@testing-library/dom'

if (typeof window !== 'undefined') {
  configure({ asyncUtilTimeout: 10_000 })
  if (!window.matchMedia) {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }),
    })
  }
  if (!(window as unknown as { ResizeObserver?: unknown }).ResizeObserver) {
    class RO {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    }
    Object.defineProperty(window, 'ResizeObserver', { writable: true, value: RO })
  }
  if (!window.IntersectionObserver) {
    class IO {
      constructor(_cb: unknown) {}
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
      takeRecords(): unknown[] {
        return []
      }
    }
    Object.defineProperty(window, 'IntersectionObserver', { writable: true, value: IO })
  }
  window.scrollTo = () => {}
}
