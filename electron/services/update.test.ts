import { describe, it, expect, vi } from 'vitest'
import {
  compareVersions,
  checkForUpdate,
  RELEASES_PAGE,
  type FetchLike,
  type MinimalResponse,
} from './update'

describe('compareVersions（语义化，含预发布）', () => {
  it('相同版本返回 0（含 v 前缀与段数不等）', () => {
    expect(compareVersions('1.1.0', '1.1.0')).toBe(0)
    expect(compareVersions('v1.1.0', '1.1.0')).toBe(0)
    expect(compareVersions('1.1', '1.1.0')).toBe(0)
  })

  it('落后/超前', () => {
    expect(compareVersions('1.0.0', '1.1.0')).toBeLessThan(0)
    expect(compareVersions('1.1.0', '1.0.0')).toBeGreaterThan(0)
    expect(compareVersions('1.0.0', '1.0.1')).toBeLessThan(0)
    expect(compareVersions('2.0.0', '1.9.9')).toBeGreaterThan(0)
    expect(compareVersions('1.10.0', '1.9.0')).toBeGreaterThan(0)
  })

  it('预发布低于同号正式版（semver 规则）', () => {
    expect(compareVersions('1.2.0-beta.1', '1.2.0')).toBeLessThan(0)
    expect(compareVersions('1.2.0', '1.2.0-beta.1')).toBeGreaterThan(0)
    expect(compareVersions('1.2.0-beta.1', '1.2.0-beta.2')).toBeLessThan(0)
  })
})

/** 造一个最小响应 */
function res(status: number, body: unknown): MinimalResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  }
}

describe('checkForUpdate（不抛错：失败收敛为 unavailable + reason）', () => {
  it('有新版本：latest > current → hasUpdate 为真', async () => {
    const f: FetchLike = async () =>
      res(200, {
        tag_name: 'v1.2.0',
        html_url: 'https://github.com/Hermitweb/win-share-panel/releases/tag/v1.2.0',
        published_at: '2026-10-01T00:00:00Z',
      })
    const r = await checkForUpdate('1.1.0', f)
    expect(r).toMatchObject({
      current: '1.1.0',
      latest: '1.2.0',
      hasUpdate: true,
      unavailable: false,
      reason: null,
      publishedAt: '2026-10-01T00:00:00Z',
    })
    expect(r.releaseUrl).toContain('/releases/tag/v1.2.0')
  })

  it('已是最新：相同版本 → hasUpdate 为假且非 unavailable', async () => {
    const f: FetchLike = async () => res(200, { tag_name: 'v1.1.0' })
    const r = await checkForUpdate('1.1.0', f)
    expect(r.hasUpdate).toBe(false)
    expect(r.unavailable).toBe(false)
    expect(r.latest).toBe('1.1.0')
    expect(r.releaseUrl).toBe(RELEASES_PAGE)
  })

  it('比更新源还新（本地预发布）：不算有更新', async () => {
    const f: FetchLike = async () => res(200, { tag_name: 'v1.0.0' })
    const r = await checkForUpdate('1.1.0', f)
    expect(r.hasUpdate).toBe(false)
  })

  it('网络不可达：给出人话原因 + 仍提供下载页（本机实测的真实场景）', async () => {
    const f: FetchLike = async () => {
      throw new Error('fetch failed')
    }
    const r = await checkForUpdate('1.1.0', f)
    expect(r.unavailable).toBe(true)
    expect(r.hasUpdate).toBe(false)
    expect(r.reason).toContain('无法连接到更新源')
    expect(r.reason).toContain('Hermitweb/win-share-panel')
    expect(r.reason).toContain('下载页')
    expect(r.releaseUrl).toBe(RELEASES_PAGE)
  })

  it('404：提示尚未发版，而不是 generic 失败', async () => {
    const f: FetchLike = async () => res(404, {})
    const r = await checkForUpdate('1.1.0', f)
    expect(r.unavailable).toBe(true)
    expect(r.reason).toContain('没有已发布的 Release')
  })

  it('5xx：带状态码', async () => {
    const f: FetchLike = async () => res(503, {})
    const r = await checkForUpdate('1.1.0', f)
    expect(r.reason).toContain('503')
  })

  it('响应不是合法 JSON / 缺 tag_name：分别给不同原因', async () => {
    const bad: FetchLike = async () => ({
      ok: true,
      status: 200,
      json: async () => {
        throw new Error('Unexpected token')
      },
    })
    expect((await checkForUpdate('1.1.0', bad)).reason).toContain('不是合法 JSON')

    const noTag: FetchLike = async () => res(200, { name: 'no tag here' })
    expect((await checkForUpdate('1.1.0', noTag)).reason).toContain('没有版本号')
  })

  it('超时：abort 后报超时而不是网络错误', async () => {
    vi.useFakeTimers()
    const f: FetchLike = (_url, init) =>
      new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new Error('aborted')))
      })
    const p = checkForUpdate('1.1.0', f, 50)
    await vi.advanceTimersByTimeAsync(60)
    const r = await p
    vi.useRealTimers()
    expect(r.unavailable).toBe(true)
    expect(r.reason).toContain('超时')
  })
})
