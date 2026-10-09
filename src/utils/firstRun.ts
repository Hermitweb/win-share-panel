import type { CreateShareInput, Share } from '../types'
import { DEFAULT_READ_ACCESS } from './shareDefaults'

// ===== 首启三问向导的判定与参数拼装（T4 · 新手上路第一批）=====
// 判定逻辑全部做成纯函数放在这里，而不是散进组件：
// "老用户绝不打扰"是行为契约，不是渲染细节——有共享/已完成/专家模式三种情形
// 都必须收敛到"不弹"，做成表驱动单测才守得住；挂载方（App）只消费结果。

/** 一次性"已完成初始化"标记键（localStorage）：创建成功后写入，首启向导从此不再出现 */
export const FIRST_RUN_DONE_KEY = 'winshare.firstRunCompleted'
export const FIRST_RUN_DONE_VALUE = '1'

/** 向导步骤标题：组件 Steps 与测试共用同一来源，防止"三步"契约各写各的 */
export const GUIDE_STEPS = [
  { title: '选文件夹', hint: '要对外共享哪个文件夹？' },
  { title: '给谁用', hint: '别人能对它做到哪一步？' },
  { title: '完成', hint: '确认名字，马上就能用' },
] as const

/**
 * 人话权限档位。desc 就是"能干什么/不能干什么"的承诺，
 * tierToAccess 是唯一落点：文案与 CreateShareInput 权限字段永远同源。
 * 顺序即展示顺序：最安全的"只看看"排第一，也是默认档。
 */
export type PermTier = 'read' | 'change' | 'full'

export interface PermTierOption {
  tier: PermTier
  label: string
  desc: string
}

export const PERM_TIERS: readonly PermTierOption[] = [
  {
    tier: 'read',
    label: '只让他们看看',
    desc: '能打开、复制文件，但不能改动或删除——给不熟悉的人最稳妥',
  },
  {
    tier: 'change',
    label: '大家一起放文件',
    desc: '能看、能新增和修改文件，不能动权限设置——家人同事协作够用',
  },
  {
    tier: 'full',
    label: '完全交给他们',
    desc: '能看、能改、能删、能改权限——只给绝对信任的人',
  },
] as const

export const DEFAULT_PERM_TIER: PermTier = 'read'

/** 档位在 CreateShareInput 里的落点：恰好一个权限键（Spread 进 create 参数时不带 protocol/name/path） */
export type TierAccess = Pick<CreateShareInput, 'fullAccess' | 'changeAccess' | 'readAccess'>

export function tierToAccess(tier: PermTier): Partial<TierAccess> {
  switch (tier) {
    case 'full':
      return { fullAccess: [...DEFAULT_READ_ACCESS] }
    case 'change':
      return { changeAccess: [...DEFAULT_READ_ACCESS] }
    default:
      return { readAccess: [...DEFAULT_READ_ACCESS] }
  }
}

/** 向导步骤三"高级参数"折叠区能改的项；未展开=保持默认=完全不提交（与 Shares 页同纪律） */
export interface AdvancedOpts {
  /** SMB 高级：并发连接上限（0/undefined 不写） */
  concurrentUserLimit?: number
  /** SMB 高级：分支缓存/文档缓存模式 */
  cachingMode?: CreateShareInput['cachingMode']
  /** 提示级开关（不落 create 参数）：如 SMB1 兼容引导 */
  hideForOldClients?: boolean
}

/** 建共享入参唯一出口：路径/名 trim，档位展开，高级参数仅在展开过时并入 */
export function buildCreateInput(
  name: string,
  path: string,
  tier: PermTier,
  advanced?: AdvancedOpts,
): CreateShareInput {
  const input: CreateShareInput = {
    protocol: 'smb',
    name: name.trim(),
    path: path.trim(),
    ...tierToAccess(tier),
  }
  if (advanced?.concurrentUserLimit && advanced.concurrentUserLimit > 0) {
    input.concurrentUserLimit = advanced.concurrentUserLimit
  }
  if (advanced?.cachingMode && advanced.cachingMode !== 'None') {
    input.cachingMode = advanced.cachingMode
  }
  return input
}

/** 存储原始值 → 已完成判定：'1'/true/'true' 算已标记；其余（'0'、空、缺失）未标记 */
export function isMarkedComplete(flag: unknown): boolean {
  return flag === true || flag === FIRST_RUN_DONE_VALUE || flag === 'true'
}

/**
 * 首启判定纯函数（只进不 IO）：三个条件任一成立都不弹——
 * ①已有共享（不管标记与否，建过就不是首启）②已写完成标记 ③专家模式。
 * 只有「空共享 + 未标记 + 新手模式」才弹。shares 传 null/undefined 视为
 * "尚未加载"不弹：加载中闪向导，恰恰会骚扰到数据稍后才有显示的老用户。
 */
export function shouldShowGuide(
  shares: readonly Share[] | null | undefined,
  advancedMode: boolean | null | undefined,
  storageFlag: unknown,
): boolean {
  if (advancedMode === true) return false
  if (isMarkedComplete(storageFlag)) return false
  if (!Array.isArray(shares)) return false
  return shares.length === 0
}

/** 最小键值存储接口（jsdom/浏览器 localStorage 天然满足；node 测试注入 fake） */
export interface FlagStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

function browserStorage(): FlagStorage | null {
  if (typeof window === 'undefined') return null
  try {
    return window.localStorage ?? null
  } catch {
    // 隐私模式等场景访问 localStorage 直接抛错：视为不可用
    return null
  }
}

/** 读存储里的完成标记原始值（storage 缺省取浏览器环境；不可用时 null → 判未标记） */
export function readFirstRunFlag(storage: FlagStorage | null = browserStorage()): unknown {
  if (!storage) return null
  try {
    return storage.getItem(FIRST_RUN_DONE_KEY)
  } catch {
    return null
  }
}

/**
 * 一次性写入"已完成初始化"标记：创建成功后调用，老用户从此绝不打扰。
 * 返回是否写入成功（无 storage 环境 false）；不抛错，写失败不阻塞向导收尾。
 */
export function markFirstRunComplete(storage: FlagStorage | null = browserStorage()): boolean {
  if (!storage) return false
  try {
    storage.setItem(FIRST_RUN_DONE_KEY, FIRST_RUN_DONE_VALUE)
    return true
  } catch {
    return false
  }
}
