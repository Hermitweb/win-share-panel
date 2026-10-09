import { describe, it, expect } from 'vitest'
import {
  shareNameFromPath,
  diskLevel,
  isUncPath,
  driveOf,
  indexDiskUsages,
  deleteImpactText,
  undoHintText,
  DEFAULT_READ_ACCESS,
} from './shareDefaults'
import type { DiskUsage, Share } from '../types'

const share = (over: Partial<Share>): Share =>
  ({
    name: 'docs',
    path: 'D:\\Share\\docs',
    description: '',
    protocol: 'smb',
    type: 'Disk',
    hidden: false,
    encrypted: false,
    concurrentUsers: 0,
    status: 'Enabled',
    cached: false,
    ...over,
  }) as Share

const disk = (over: Partial<DiskUsage>): DiskUsage =>
  ({ drive: 'D:', freeGB: 100, totalGB: 500, freePct: 20, ...over }) as DiskUsage

describe('shareNameFromPath（新建共享名带出）', () => {
  it('取末级目录名', () => {
    expect(shareNameFromPath('D:\\Share\\项目资料')).toBe('项目资料')
    expect(shareNameFromPath('D:/Share/docs/')).toBe('docs')
  })
  it('非法字符转下划线，前导点/$ 去掉（避免生成隐藏共享名）', () => {
    expect(shareNameFromPath('D:\\a\\b<c>d:')).toBe('b_c_d_')
    expect(shareNameFromPath('C:\\Users\\x\\$hared')).toBe('hared')
  })
  it('空/根路径返回空串（调用方据此不覆盖用户已输入的名字）', () => {
    expect(shareNameFromPath('')).toBe('')
    expect(shareNameFromPath('D:\\')).toBe('')
  })
  it('超长截断到 60 字符', () => {
    expect(shareNameFromPath('D\\' + 'a'.repeat(80))).toHaveLength(60)
  })
})

describe('DEFAULT_READ_ACCESS', () => {
  it('默认给 Users 读取', () => {
    expect(DEFAULT_READ_ACCESS).toEqual(['Users'])
  })
})

describe('diskLevel（磁盘水位分级）', () => {
  it('未知盘返回 unknown', () => {
    expect(diskLevel(undefined)).toBe('unknown')
  })
  it('比例或绝对值任一命中即 warn，命中一半阈值即 danger', () => {
    expect(diskLevel(disk({ freePct: 50, freeGB: 100 }))).toBe('ok')
    expect(diskLevel(disk({ freePct: 10, freeGB: 900 }))).toBe('warn') // 大盘只看比例
    expect(diskLevel(disk({ freePct: 90, freeGB: 15 }))).toBe('warn') // 小剩余量看绝对值
    expect(diskLevel(disk({ freePct: 4, freeGB: 900 }))).toBe('danger')
    expect(diskLevel(disk({ freePct: 90, freeGB: 4 }))).toBe('danger')
  })
  it('阈值可配（跟随告警规则 diskLowGb）', () => {
    expect(diskLevel(disk({ freePct: 80, freeGB: 30 }), { lowGb: 50 })).toBe('warn')
    expect(diskLevel(disk({ freePct: 80, freeGB: 30 }), { lowGb: 10 })).toBe('ok')
  })
})

describe('isUncPath / driveOf / indexDiskUsages', () => {
  it('UNC 无盘符，本地路径取大写盘符', () => {
    expect(isUncPath('\\\\nas\\share')).toBe(true)
    expect(isUncPath('D:\\Share')).toBe(false)
    expect(driveOf('e:\\a\\b')).toBe('E:')
    expect(driveOf('\\\\nas\\share')).toBeNull()
  })
  it('按盘符建索引（大小写归一）', () => {
    const map = indexDiskUsages([disk({ drive: 'D:' }), disk({ drive: 'E:' })])
    expect(map['D:']?.freeGB).toBe(100)
    expect(map['X:']).toBeUndefined()
  })
})

describe('deleteImpactText（删除前连接影响提示）', () => {
  it('无人连接时明确说可安全删除', () => {
    expect(deleteImpactText([share({})])).toEqual({
      text: '当前无人连接，可安全删除。',
      danger: false,
    })
    expect(deleteImpactText([share({}), share({ name: 'b' })]).danger).toBe(false)
  })
  it('有人连接时给出连接数与共享名，并标记 danger', () => {
    const r = deleteImpactText([
      share({ name: 'docs', concurrentUsers: 3 }),
      share({ name: 'img' }),
    ])
    expect(r.danger).toBe(true)
    expect(r.text).toContain('3 个连接')
    expect(r.text).toContain('docs(3)')
    expect(r.text).toContain('强制断开')
  })
  it('超过 3 个连接中的共享只列前 3 个并加"等"', () => {
    const many = [1, 2, 3, 4, 5].map((n) => share({ name: `s${n}`, concurrentUsers: 1 }))
    const r = deleteImpactText(many)
    expect(r.text).toContain('s1(1)、s2(1)、s3(1)')
    expect(r.text).toContain(' 等')
    expect(r.text).not.toContain('s5(1)')
    expect(r.text).toContain('5 个连接')
  })
})

describe('undoHintText（撤销能力诚实表述）', () => {
  it('单个 SMB 删除可撤销', () => {
    expect(undoHintText([share({ protocol: 'smb' })])).toContain('操作回收站')
  })
  it('单个非 SMB 删除只留档，不给假承诺', () => {
    expect(undoHintText([share({ protocol: 'ftp' })])).toContain('不支持一键撤销')
  })
  it('混合场景分别报数', () => {
    const r = undoHintText([share({ protocol: 'smb' }), share({ protocol: 'ftp' })])
    expect(r).toContain('1 个 SMB 共享可撤销')
    expect(r).toContain('1 个非 SMB')
  })
  it('全 SMB 与全非 SMB 各自成句', () => {
    expect(undoHintText([share({ protocol: 'smb' }), share({ protocol: 'smb', name: 'b' })])).toBe(
      '2 个 SMB 共享删除后均可在「操作回收站」撤销。',
    )
    expect(undoHintText([share({ protocol: 'nfs' })]).length).toBeGreaterThan(0)
    expect(undoHintText([share({ protocol: 'nfs' }), share({ protocol: 'ftp', name: 'b' })])).toBe(
      '2 个非 SMB 共享仅留档，不支持一键撤销。',
    )
  })
})
