/**
 * 应用内「检查更新」——只做检查与提示，**不做自动下载安装**。
 *
 * 为什么不做自动更新：electron-updater 要 electron-builder 的 publish 配置、代码签名与
 * 线上 latest.yml 三件套齐全才有意义；本仓库 electron-builder.yml 里没有 publish 段，
 * README 也把自动更新列为「v1.1 规划中」。所以本模块只回答「有没有新版本」并给出可执行动作
 * （打开下载页）：不引入任何新依赖、不静默下载、不自动改动机器上的安装。
 *
 * 网络现实（本机实测，必须写进代码而不是只写在文档里）：本机 `api.github.com` 不可达
 * （与 README「国内推 GitHub 若被重置」一致）。因此**失败路径比成功路径更需要做好**：
 * 每种失败都归到一句人话原因 + 一个可执行动作，而不是抛一句 `fetch failed`。
 *
 * 本文件不 import electron：当前版本由调用方（IPC 层用 app.getVersion()）传入，
 * 这样整个检查逻辑能在 node 环境被单测直接覆盖。
 */

/** 更新源：与 git remote / release workflow 指向的仓库一致 */
export const UPDATE_REPO = 'Hermitweb/win-share-panel'
/** 给用户点的下载页（无论检查成功与否都可打开） */
export const RELEASES_PAGE = `https://github.com/${UPDATE_REPO}/releases/latest`
const API_LATEST = `https://api.github.com/repos/${UPDATE_REPO}/releases/latest`

export interface UpdateCheckResult {
  /** 当前运行版本（来自 app.getVersion()） */
  current: string
  /** 更新源上的最新版本；不可达时为空串 */
  latest: string
  hasUpdate: boolean
  releaseUrl: string
  publishedAt: string | null
  /** 更新源不可达 / 响应异常：UI 据此走「检查失败」分支而不是「已是最新」 */
  unavailable: boolean
  /** 人话失败原因（可直接展示）；成功时为 null */
  reason: string | null
}

/** 只为可测而收窄的最小响应接口（避免把 DOM/undici 的完整类型拖进主进程） */
export interface MinimalResponse {
  ok: boolean
  status: number
  json: () => Promise<unknown>
}
export type FetchLike = (url: string, init?: { signal?: AbortSignal }) => Promise<MinimalResponse>

const DEFAULT_TIMEOUT_MS = 8000

const defaultFetch: FetchLike = async (url, init) => {
  const res = await fetch(url, {
    signal: init?.signal,
    headers: { 'User-Agent': 'WinSharePanel', Accept: 'application/vnd.github+json' },
  })
  return res as unknown as MinimalResponse
}

/** 去掉 `v` 前缀并拆成 [数值段, 预发布标识]，供语义化比较 */
function parseVersion(v: string): { nums: number[]; pre: string } {
  const s = v.trim().replace(/^v/i, '')
  const [core, ...rest] = s.split('-')
  const nums = core.split('.').map((x) => {
    const n = Number.parseInt(x, 10)
    return Number.isFinite(n) ? n : 0
  })
  return { nums, pre: rest.join('-') }
}

/**
 * 语义化版本比较：返回值 <0 表示 a 旧于 b，0 表示相等，>0 表示 a 新于 b。
 * 规则：先逐段比数值（缺段按 0 补齐）；数值相同时，**带预发布标识的更低**
 * （1.2.0-beta < 1.2.0，符合 semver）。
 */
export function compareVersions(a: string, b: string): number {
  const pa = parseVersion(a)
  const pb = parseVersion(b)
  const len = Math.max(pa.nums.length, pb.nums.length)
  for (let i = 0; i < len; i++) {
    const x = pa.nums[i] ?? 0
    const y = pb.nums[i] ?? 0
    if (x !== y) return x < y ? -1 : 1
  }
  if (pa.pre === pb.pre) return 0
  if (!pa.pre) return 1
  if (!pb.pre) return -1
  return pa.pre < pb.pre ? -1 : 1
}

function unavailable(current: string, reason: string): UpdateCheckResult {
  return {
    current,
    latest: '',
    hasUpdate: false,
    releaseUrl: RELEASES_PAGE,
    publishedAt: null,
    unavailable: true,
    reason,
  }
}

/**
 * 查询更新源。**不抛错**：一切失败都收敛成 `unavailable + reason`，
 * 因为「检查更新失败」对用户是一个正常状态（受限网络很常见），不是异常。
 */
export async function checkForUpdate(
  currentVersion: string,
  fetchImpl: FetchLike = defaultFetch,
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<UpdateCheckResult> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    let res: MinimalResponse
    try {
      res = await fetchImpl(API_LATEST, { signal: controller.signal })
    } catch (e) {
      if (controller.signal.aborted) {
        return unavailable(currentVersion, `检查更新超时（超过 ${timeoutMs} 毫秒未响应）`)
      }
      const msg = e instanceof Error ? e.message : String(e)
      return unavailable(
        currentVersion,
        `无法连接到更新源（${UPDATE_REPO}）：${msg}。若处于受限网络，可配置代理后重试，或直接打开下载页手动查看。`,
      )
    }

    if (res.status === 404) {
      return unavailable(currentVersion, '更新源上没有已发布的 Release（仓库不存在或尚未发版）')
    }
    if (!res.ok) {
      return unavailable(currentVersion, `更新源返回 HTTP ${res.status}`)
    }

    let body: unknown
    try {
      body = await res.json()
    } catch {
      return unavailable(currentVersion, '更新源响应不是合法 JSON')
    }
    const obj = (body ?? {}) as Record<string, unknown>
    const tag = typeof obj.tag_name === 'string' ? obj.tag_name.trim() : ''
    if (!tag) {
      return unavailable(currentVersion, '更新源响应里没有版本号（tag_name），无法判断是否有新版本')
    }

    const latest = tag.replace(/^v/i, '')
    return {
      current: currentVersion,
      latest,
      hasUpdate: compareVersions(latest, currentVersion) > 0,
      releaseUrl: typeof obj.html_url === 'string' && obj.html_url ? obj.html_url : RELEASES_PAGE,
      publishedAt: typeof obj.published_at === 'string' ? obj.published_at : null,
      unavailable: false,
      reason: null,
    }
  } finally {
    clearTimeout(timer)
  }
}
