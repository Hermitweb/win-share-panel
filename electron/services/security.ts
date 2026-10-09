import { runPowerShell } from '../lib/powershell'
import type { SecurityIssue, SecurityReport } from '../types'

// ===== 账号安全体检（弱口令策略层面的可探测项 + 常见合规点）=====
interface RawUser {
  Name: string
  Enabled: boolean
  PasswordRequired: boolean
  PasswordExpires: boolean
  PasswordLastSet: string | null
}

const DAY = 24 * 3600 * 1000

export async function securityReport(): Promise<SecurityReport> {
  const raw = await runPowerShell<RawUser | RawUser[]>(
    'Get-LocalUser | Select-Object Name,Enabled,PasswordRequired,PasswordExpires,PasswordLastSet',
    { retries: 0 },
  )
  const arr = (Array.isArray(raw) ? raw : [raw]).filter((u) => u && u.Name)
  const issues: SecurityIssue[] = []
  const now = Date.now()
  for (const u of arr) {
    if (!u.Enabled) continue
    if (u.PasswordRequired === false) {
      issues.push({ user: u.Name, level: 'fail', issue: '账号设置了"密码永不需要"，可空口令登录' })
      continue
    }
    if (u.PasswordExpires === false) {
      issues.push({
        user: u.Name,
        level: 'warn',
        issue: '密码设置为永不过期（共享账号建议定期轮换）',
      })
    }
    if (u.PasswordLastSet) {
      const set = Date.parse(u.PasswordLastSet)
      if (!Number.isNaN(set) && now - set > 180 * DAY) {
        issues.push({
          user: u.Name,
          level: 'warn',
          issue: `密码超过 180 天未修改（${new Date(set).toISOString().slice(0, 10)}）`,
        })
      }
    }
  }
  return { checked: arr.filter((u) => u.Enabled).length, issues, at: now }
}
