import { Errors } from '../lib/errors'
import { loadState, saveState } from '../lib/stateStore'
import type { AlertRules, AppState, AppStatePatch } from '../types'

/**
 * 「设置整体导入导出」—— 搬的是**应用偏好与运维规则**，不是业务数据。
 *
 * 与既有两处的分工（避免功能重叠造成困惑）：
 *  · `share:export/import`  → 共享配置（共享名/路径/权限）
 *  · `preset:export/import` → 权限模板
 *  · `state:exportAll/importAll`（本模块）→ 主题 / 新手模式 / 置顶 / 告警规则 / 自启意图
 *
 * 刻意**不导出** `dashboardHistory` 与 `journal`：
 *  · dashboardHistory 是「这台机器这段历史」的连接数采样曲线，搬到新机器会显示别人的趋势；
 *  · journal 是撤销台账（含改前快照），跨机器回放会指向不存在的共享。
 * 把运行时数据当偏好导出，等于给它一个它没有的含义——宁可不带，也不误导。
 *
 * 导入**不信任文件**：形状校验 → 逐字段类型收敛 → 交给 stateStore.saveState（它内部再走
 * 一次 normalize 收敛）。语义是「合法字段应用、非法字段忽略」，而不是整体拒绝或整体写入。
 */

export const TRANSFER_FORMAT = 'winshare-panel/settings'
export const TRANSFER_VERSION = 1
/** 设置体量远小于共享/模板；护栏与 R-1 同思路：先拒超大再 parse */
const MAX_JSON_BYTES = 512 * 1024

export interface SettingsPayload {
  theme: AppState['theme']
  advancedMode: boolean
  pinned: string[]
  alertRules: AlertRules
  autoStart: boolean
}

export interface SettingsBundle {
  format: string
  version: number
  exportedAt: string
  settings: SettingsPayload
}

export interface ImportResult {
  /** 实际被应用的字段名（供 UI 如实反馈"应用了哪几项"） */
  applied: string[]
}

/** 导出：字段显式列出，不 spread 整份 AppState —— 免得将来加了运行时字段被顺手带出去 */
export function exportSettings(): string {
  const s = loadState()
  const bundle: SettingsBundle = {
    format: TRANSFER_FORMAT,
    version: TRANSFER_VERSION,
    exportedAt: new Date().toISOString(),
    settings: {
      theme: s.theme,
      advancedMode: s.advancedMode,
      pinned: [...s.pinned],
      alertRules: { ...s.alertRules },
      autoStart: s.autoStart,
    },
  }
  return JSON.stringify(bundle, null, 2)
}

export function importSettings(json: unknown): ImportResult {
  if (typeof json !== 'string') throw Errors.invalidParam('导入内容必须是文本')
  if (json.length > MAX_JSON_BYTES) {
    throw Errors.invalidParam(`导入内容过大（上限 ${Math.round(MAX_JSON_BYTES / 1024)} KB）`)
  }

  let data: unknown
  try {
    data = JSON.parse(json)
  } catch {
    throw Errors.invalidParam('不是合法的 JSON 文件')
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) {
    throw Errors.invalidParam('文件结构不是本应用的设置导出')
  }

  const obj = data as Record<string, unknown>
  if (obj.format !== TRANSFER_FORMAT) {
    throw Errors.invalidParam('文件格式不符：不是本应用的设置导出文件')
  }
  if (typeof obj.version !== 'number' || !Number.isFinite(obj.version)) {
    throw Errors.invalidParam('文件缺少版本号，无法判断兼容性')
  }
  if (obj.version > TRANSFER_VERSION) {
    throw Errors.invalidParam(
      `文件版本（${obj.version}）高于本程序支持的版本（${TRANSFER_VERSION}），请先升级程序`,
    )
  }

  const raw = obj.settings
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
    throw Errors.invalidParam('文件里没有 settings 段')
  }
  const s = raw as Record<string, unknown>

  const patch: AppStatePatch = {}
  const applied: string[] = []

  if (s.theme === 'light' || s.theme === 'dark') {
    patch.theme = s.theme
    applied.push('theme')
  }
  if (typeof s.advancedMode === 'boolean') {
    patch.advancedMode = s.advancedMode
    applied.push('advancedMode')
  }
  if (Array.isArray(s.pinned)) {
    const pinned = s.pinned.filter((x): x is string => typeof x === 'string')
    if (pinned.length) {
      patch.pinned = pinned
      applied.push('pinned')
    }
  }
  if (s.alertRules && typeof s.alertRules === 'object' && !Array.isArray(s.alertRules)) {
    // 逐键收敛交给 stateStore.normalize：这里只拒绝"根本不是对象"的形态
    patch.alertRules = s.alertRules as Partial<AlertRules>
    applied.push('alertRules')
  }
  if (typeof s.autoStart === 'boolean') {
    patch.autoStart = s.autoStart
    applied.push('autoStart')
  }

  if (applied.length === 0) throw Errors.invalidParam('文件里没有任何可识别的设置项')

  saveState(patch)
  return { applied }
}
