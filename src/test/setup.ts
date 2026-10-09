// vitest 全局 setup（B3）：jsdom 环境补 antd 依赖的浏览器 API 桩，注入 jest-dom 匹配器。
// node 环境用例（electron/*、src/*.test.ts）不触碰 DOM，守卫式判空保证零副作用。
import '@testing-library/jest-dom/vitest'

if (typeof window !== 'undefined') {
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
