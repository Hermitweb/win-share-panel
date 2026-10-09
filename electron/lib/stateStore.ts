import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'fs'
import { homedir, platform } from 'os'
import { join } from 'path'
import type { AppState, AppStatePatch, HistoryPoint, JournalEntry } from '../types'

// ===== 应用级持久状态（UI 偏好/告警规则/置顶/连接历史/操作日志）=====
// 单 JSON 文件原子写；读取带进程内缓存；容量上限防止无限增长。
// 与 disabled.json（SMB 禁用共享存档）、snapshots（SMB 配置快照）并列，互不影响。
// 类型契约在 ../types（本文件不定义跨层类型，避免渲染层类型图拖入 node 依赖）。

const DEFAULTS: AppState = {
  theme: 'light',
  advancedMode: true,
  pinned: [],
  alertRules: {
    idleAlertMinutes: null,
    smb1Alert: true,
    weakPasswordAlert: true,
    diskLowGb: 20,
  },
  autoStart: false,
  dashboardHistory: [],
  journal: [],
}

const HISTORY_MAX = 288 // 5min 采样 × 24h
const JOURNAL_MAX = 200
const PINNED_MAX = 50

function stateDir(): string {
  const base =
    platform() === 'win32' ? process.env.APPDATA || homedir() : join(homedir(), '.winshare-panel')
  return join(base, 'WinSharePanel', 'state')
}

function stateFile(): string {
  return join(stateDir(), 'appstate.json')
}

let cache: AppState | null = null

/** 旧文件缺字段/多字段都要能安全升级：逐字段显式收敛回默认值（不用 spread，避免脏值穿透） */
function normalize(raw: unknown): AppState {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return structuredClone(DEFAULTS)
  const r = raw as Record<string, unknown>
  const ar = (r.alertRules && typeof r.alertRules === 'object' ? r.alertRules : {}) as Record<
    string,
    unknown
  >
  const d = DEFAULTS.alertRules
  return {
    theme: r.theme === 'dark' ? 'dark' : 'light',
    advancedMode: typeof r.advancedMode === 'boolean' ? r.advancedMode : true,
    pinned: Array.isArray(r.pinned)
      ? r.pinned.filter((x): x is string => typeof x === 'string').slice(-PINNED_MAX)
      : [],
    alertRules: {
      idleAlertMinutes:
        typeof ar.idleAlertMinutes === 'number' && ar.idleAlertMinutes > 0
          ? Math.round(ar.idleAlertMinutes)
          : d.idleAlertMinutes,
      smb1Alert: typeof ar.smb1Alert === 'boolean' ? ar.smb1Alert : d.smb1Alert,
      weakPasswordAlert:
        typeof ar.weakPasswordAlert === 'boolean' ? ar.weakPasswordAlert : d.weakPasswordAlert,
      diskLowGb:
        typeof ar.diskLowGb === 'number' && ar.diskLowGb >= 0
          ? Math.round(ar.diskLowGb)
          : d.diskLowGb,
    },
    autoStart: typeof r.autoStart === 'boolean' ? r.autoStart : false,
    dashboardHistory: Array.isArray(r.dashboardHistory)
      ? r.dashboardHistory
          .filter(
            (p): p is HistoryPoint =>
              !!p && typeof p === 'object' && typeof (p as HistoryPoint).ts === 'number',
          )
          .slice(-HISTORY_MAX)
      : [],
    journal: Array.isArray(r.journal)
      ? r.journal
          .filter(
            (j): j is JournalEntry =>
              !!j && typeof j === 'object' && typeof (j as JournalEntry).id === 'string',
          )
          .slice(-JOURNAL_MAX)
      : [],
  }
}

export function loadState(): AppState {
  if (cache) return cache
  try {
    if (existsSync(stateFile())) {
      cache = normalize(JSON.parse(readFileSync(stateFile(), 'utf8')))
      return cache
    }
  } catch {
    // 损坏则重建默认
  }
  cache = structuredClone(DEFAULTS)
  return cache
}

export function saveState(patch: AppStatePatch): AppState {
  const prev = loadState()
  // alertRules 浅合并：调用方只改一项时其余项保持原值；normalize 再逐键校验类型
  const next = normalize({
    ...prev,
    ...patch,
    alertRules: { ...prev.alertRules, ...(patch.alertRules ?? {}) },
  })
  cache = next
  try {
    const dir = stateDir()
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })
    const file = stateFile()
    const tmp = `${file}.tmp`
    writeFileSync(tmp, JSON.stringify(next), 'utf8')
    renameSync(tmp, file)
  } catch {
    // 持久化失败不影响内存态（下次写重试）
  }
  return next
}

// ===== 操作日志（撤销底座）=====

let journalSeq = 0

export function addJournal(entry: Omit<JournalEntry, 'id' | 'ts'>): JournalEntry {
  const full: JournalEntry = {
    ...entry,
    id: `j${Date.now()}-${++journalSeq}`,
    ts: Date.now(),
  }
  const s = loadState()
  saveState({ journal: [...s.journal, full] })
  return full
}

/** 撤销成功后消费该条记录（避免重复撤销） */
export function removeJournal(id: string): void {
  const s = loadState()
  saveState({ journal: s.journal.filter((j) => j.id !== id) })
}

export function pushHistory(point: HistoryPoint): void {
  const s = loadState()
  saveState({ dashboardHistory: [...s.dashboardHistory, point] })
}

/** 测试/进程内状态复位（仅测试用） */
export function __resetStateCache(): void {
  cache = null
  journalSeq = 0
}
