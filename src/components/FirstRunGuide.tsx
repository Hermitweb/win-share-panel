import { useEffect, useRef, useState } from 'react'
import { App, Alert, Button, Collapse, Input, InputNumber, Modal, Radio, Select, Steps } from 'antd'
import { FolderOpenOutlined } from '@ant-design/icons'
import { api } from '../api'
import { shareNameFromPath } from '../utils/shareDefaults'
import {
  DEFAULT_PERM_TIER,
  GUIDE_STEPS,
  PERM_TIERS,
  buildCreateInput,
  markFirstRunComplete,
  type AdvancedOpts,
  type PermTier,
} from '../utils/firstRun'

export interface FirstRunGuideProps {
  /** 可见性由挂载方按 firstRun.shouldShowGuide(shares, advancedMode, storageFlag) 决定 */
  open: boolean
  /** 关闭回调（成功后由组件主动调用；「以后再说」同样走它，但不写完成标记） */
  onClose: () => void
}

/**
 * 首启三问向导（T4 · 新手上路）：选文件夹 → 给谁用 → 完成。
 *
 * 对新手的全部承诺都落在渲染结构上：
 * - 只有三步，步骤标题来自 firstRun.GUIDE_STEPS（与测试同源）；
 * - SMB1/枚举/租约/NTFS 只出现在折叠的「高级参数」里——antd Collapse 懒渲染，
 *   不点开这些字符串根本不在 DOM 里，不存在"看不见但被术语吓到"；
 * - 默认路径来自 api.disk.suggestRoot()，改路径走原生文件夹选择；
 * - 权限三档人话，落参由 firstRun.tierToAccess 单点映射；
 * - 创建成功写一次性「已完成初始化」标记（localStorage），老用户绝不再被打扰；
 *   失败只在原地显示 Alert，不清空任何已填内容。
 *
 * 挂载方式（captain 负责）：open = 共享列表为空 && 未标记完成 && 新手模式。
 */
export default function FirstRunGuide({ open, onClose }: FirstRunGuideProps) {
  const { message } = App.useApp()
  const [step, setStep] = useState(0)
  const [path, setPath] = useState('')
  const [name, setName] = useState('')
  const [tier, setTier] = useState<PermTier>(DEFAULT_PERM_TIER)
  const [advanced, setAdvanced] = useState<AdvancedOpts>({})
  const [err, setErr] = useState('')
  const [creating, setCreating] = useState(false)
  // 用户手改过共享名 → 路径变化不再自动带名覆盖（与 Shares 页 nameTouchedRef 同纪律）
  const nameEdited = useRef(false)

  // 每次打开重置：向导重新出现意味着上次没有走完，别把放弃过的输入带回来
  useEffect(() => {
    if (!open) return
    setStep(0)
    setPath('')
    setName('')
    setTier(DEFAULT_PERM_TIER)
    setAdvanced({})
    setErr('')
    nameEdited.current = false
    let ignore = false
    // 智能默认：给一个可用路径（非系统盘余量最大卷）；查盘失败留空由用户自己选，
    // 路径为空会被「下一步」禁用挡住，不会建出坏共享。
    void api.disk
      .suggestRoot()
      .then((p) => {
        if (!ignore && p) setPath((cur) => cur || p)
      })
      .catch(() => {})
    return () => {
      ignore = true
    }
  }, [open])

  // 共享名默认由路径末级目录带出
  useEffect(() => {
    if (!nameEdited.current) setName(shareNameFromPath(path))
  }, [path])

  const pickFolder = async () => {
    try {
      const p = await api.system.selectFolder()
      if (p) setPath(p)
    } catch (e) {
      setErr((e as Error).message)
    }
  }

  const create = async () => {
    setCreating(true)
    setErr('')
    try {
      await api.adapter.create(buildCreateInput(name, path, tier, advanced))
      // 一次性「已完成初始化」：从此 shouldShowGuide 永远给出"不弹"
      markFirstRunComplete()
      message.success('第一个共享建好了，打开资源管理器就能用')
      onClose()
    } catch (e) {
      // 只提示、不清空：改改就能重试，重填是最差的失败体验
      setErr((e as Error).message || '创建失败，请检查文件夹路径后重试')
    } finally {
      setCreating(false)
    }
  }

  const atLast = step === GUIDE_STEPS.length - 1

  return (
    <Modal
      open={open}
      onCancel={onClose}
      footer={null}
      width={560}
      closable={false}
      mask={{ closable: false }}
      title="欢迎用 WinShare，三步建好第一个共享"
    >
      <Steps
        size="small"
        current={step}
        items={GUIDE_STEPS.map((s) => ({ title: s.title }))}
        className="!mt-5"
      />

      {step === 0 && (
        <div className="flex flex-col gap-3 mt-5">
          <div className="text-sm">挑一个要分享给别人的文件夹</div>
          <div className="flex gap-2">
            <Input
              aria-label="共享文件夹路径"
              value={path}
              placeholder="例如 D:\FamilyPhotos"
              onChange={(e) => setPath(e.target.value)}
            />
            <Button icon={<FolderOpenOutlined />} onClick={() => void pickFolder()}>
              选个文件夹
            </Button>
          </div>
          {path && (
            <div className="text-xs text-fog">
              将以「{name || '（取不了名，下一步可手动填）'}」这个名字共享
            </div>
          )}
          <Collapse
            size="small"
            items={[
              {
                key: 'advanced',
                label: '高级参数（绝大多数人不需要碰）',
                children: (
                  <div className="flex flex-col gap-3 py-1">
                    <div className="flex items-center gap-2">
                      <span className="text-xs shrink-0">同时连接人数上限</span>
                      <InputNumber
                        min={0}
                        value={advanced.concurrentUserLimit}
                        placeholder="不限"
                        onChange={(v) =>
                          setAdvanced((a) => ({
                            ...a,
                            concurrentUserLimit: typeof v === 'number' ? v : undefined,
                          }))
                        }
                      />
                    </div>
                    <div className="flex items-center gap-2">
                      <span className="text-xs shrink-0">离线缓存模式</span>
                      <Select
                        className="min-w-40"
                        value={advanced.cachingMode ?? 'None'}
                        onChange={(v) =>
                          setAdvanced((a) => ({
                            ...a,
                            cachingMode: v as AdvancedOpts['cachingMode'],
                          }))
                        }
                        options={[
                          { value: 'None', label: '不缓存（默认）' },
                          { value: 'Manual', label: '手动选择' },
                          { value: 'Documents', label: '文档优先' },
                          { value: 'Programs', label: '程序优先' },
                          { value: 'BranchCache', label: 'BranchCache（带宽优化）' },
                        ]}
                      />
                    </div>
                    <div className="text-xs text-fog">
                      SMB1 兼容、按访问枚举、租约、NTFS 权限透传等服务器级设置不在此处，
                      请在设置页（专家模式）调整。
                    </div>
                  </div>
                ),
              },
            ]}
          />
        </div>
      )}

      {step === 1 && (
        <div className="flex flex-col gap-3 mt-5">
          <div className="text-sm">给谁用？按“能让他们做到哪一步”来选</div>
          <Radio.Group
            value={tier}
            onChange={(e) => setTier(e.target.value as PermTier)}
            className="flex flex-col gap-3 items-start"
          >
            {PERM_TIERS.map((o) => (
              <Radio key={o.tier} value={o.tier} className="leading-6">
                <span className="font-medium">{o.label}</span>
                <div className="text-xs text-fog">{o.desc}</div>
              </Radio>
            ))}
          </Radio.Group>
        </div>
      )}

      {atLast && (
        <div className="flex flex-col gap-3 mt-5">
          <div className="text-sm">最后给它起个名字，完成</div>
          <div>
            <div className="text-xs text-fog mb-1">共享名（已按文件夹名自动填好，可修改）</div>
            <Input
              aria-label="共享名"
              value={name}
              onChange={(e) => {
                nameEdited.current = true
                setName(e.target.value)
              }}
            />
          </div>
          <div className="text-xs text-fog">
            文件夹：{path || '未选择'} · 权限：{PERM_TIERS.find((o) => o.tier === tier)?.label}
          </div>
        </div>
      )}

      {err && <Alert type="error" showIcon className="mt-4" message={err} />}

      <div className="flex justify-between mt-6">
        <Button type="text" onClick={onClose}>
          以后再说
        </Button>
        <div className="flex gap-2">
          {step > 0 && (
            <Button onClick={() => setStep(step - 1)} disabled={creating}>
              上一步
            </Button>
          )}
          {atLast ? (
            <Button
              type="primary"
              loading={creating}
              disabled={!name.trim()}
              onClick={() => void create()}
            >
              创建
            </Button>
          ) : (
            <Button type="primary" disabled={!path.trim()} onClick={() => setStep(step + 1)}>
              下一步
            </Button>
          )}
        </div>
      </div>
    </Modal>
  )
}
