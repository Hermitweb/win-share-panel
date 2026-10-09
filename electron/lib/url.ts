/**
 * 外部链接白名单。
 *
 * 为什么需要它：`system:openExternal` 会把 URL 交给系统默认浏览器/shell。
 * 渲染层是被沙箱化的、内容不可信的一侧，若不加校验，`file://`、`ms-msdt:`、
 * `javascript:` 之类协议就能被诱导传进来交给 shell 打开——这是一条真实的提权/钓鱼面。
 * 因此只放行 http/https，并限制长度，其余一律拒绝。
 */
const MAX_URL_LENGTH = 2000

export function isSafeExternalUrl(url: unknown): url is string {
  if (typeof url !== 'string') return false
  if (url.length === 0 || url.length > MAX_URL_LENGTH) return false
  // 只认显式的 http/https 起始；不 trim（前导空白会改变 shell 解析语义）
  return /^https?:\/\//i.test(url)
}
