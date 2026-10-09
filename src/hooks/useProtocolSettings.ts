import { useEffect, useState } from 'react'
import type { FormInstance } from 'antd'
import { App, Form } from 'antd'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { call } from '../api'
import type { Protocol, ServiceStatus } from '../types'
import { useUiStore } from '../stores/uiStore'
import { useTickEffect } from './useTickEffect'
import { useEnsureProtocolCaps } from './useEnsureProtocolCaps'

/**
 * R-5：三协议设置面板（Nfs / Ftp / Webdav SettingsPanel）共用的
 * 「协议能力门控 → 配置读取 → 保存 → 服务启停 → 恢复默认 → 刷新 tick」逻辑。
 *
 * 数据层沿用三面板既有的 react-query 用法（原样收进来，不改数据层）：
 * - `queryKey` 逐字沿用 `'nfs-settings' | 'ftp-settings' | 'webdav-settings'`；
 * - `load = invalidateQueries`（setState-free），因此可安全被 useTickEffect 的 effect 调用。
 *   若把它改成 setState 型重载，会立刻产生新的 set-state-in-effect 违例。
 * - 首轮读取失败提示与「拉到配置后同步表单」两条 effect 保留原样（含既有 exhaustive-deps 理由）。
 */

export interface ProtocolServiceApi<TConfig> {
  getConfig: () => Promise<TConfig>
  serviceStatus: () => Promise<ServiceStatus>
  setConfig: (patch: Partial<TConfig>) => Promise<void>
  restoreDefault: () => Promise<TConfig>
  restart: () => Promise<void>
  start: () => Promise<void>
  stop: () => Promise<void>
}

export interface ProtocolSettingsTexts {
  /** '已保存' */
  saved: string
  /** 'NFS 服务已重启' / 'FTP 服务已重启' / 'WebDAV 服务已重启' */
  restarted: string
  /** 'NFS 服务已启动' … */
  started: string
  /** 'NFS 服务已停止' … */
  stopped: string
  /** '已恢复默认配置' */
  restored: string
}

export interface UseProtocolSettingsOptions<TConfig> {
  protocol: Extract<Protocol, 'nfs' | 'ftp' | 'webdav'>
  api: ProtocolServiceApi<TConfig>
  texts: ProtocolSettingsTexts
}

export interface UseProtocolSettingsResult<TConfig> {
  /** 面板用 <Form form={settings.form}> 渲染协议特有字段 */
  form: FormInstance<TConfig>
  /** true=已安装 / false=未安装（走降级分支）/ null=探测中 */
  installed: boolean | null
  /** 最近一次成功读取的配置（未取到时 undefined） */
  config: Partial<TConfig> | undefined
  /** 服务状态（未取到时 null） */
  service: ServiceStatus | null
  /** = isFetching */
  loading: boolean
  saving: boolean
  /** = invalidateQueries（setState-free，因此可安全被 useTickEffect 的 effect 调用） */
  load: () => void
  save: () => Promise<void>
  restoreDefault: () => Promise<void>
  restart: () => Promise<void>
  start: () => Promise<void>
  stop: () => Promise<void>
}

export function useProtocolSettings<TConfig>(
  opts: UseProtocolSettingsOptions<TConfig>,
): UseProtocolSettingsResult<TConfig> {
  const { message } = App.useApp()
  const { protocol, api: svcApi, texts } = opts
  const [saving, setSaving] = useState(false)
  const [detectFailed, setDetectFailed] = useState(false)
  const [form] = Form.useForm<TConfig>()

  const refreshTick = useUiStore((s) => s.refreshTick)
  const protocolCaps = useUiStore((s) => s.protocolCaps)

  // 数据层 react-query；安装状态纯推导；load=invalidate
  const queryClient = useQueryClient()
  // 协议探测（统一入口）：store 无缓存时挂载探测，失败经 detectFailed 降级
  useEnsureProtocolCaps({ onDetectFailure: () => setDetectFailed(true) })
  const installed = detectFailed ? false : protocolCaps ? !!protocolCaps[protocol]?.installed : null

  const { data, isFetching, error } = useQuery({
    queryKey: [`${protocol}-settings`],
    queryFn: async () => {
      const [c, s] = await Promise.all([svcApi.getConfig(), svcApi.serviceStatus()])
      return { c, s }
    },
    // 仅在确认已安装时拉取，避免未装时触发 getConfig 错误
    enabled: installed === true,
  })
  const service = data?.s ?? null
  const config: Partial<TConfig> | undefined = data?.c
  const loading = isFetching
  const load = () => {
    void queryClient.invalidateQueries({ queryKey: [`${protocol}-settings`] }).catch(() => {})
  }

  // 首轮失败提示（与原 load catch 语义一致；整段原样收进来）
  useEffect(() => {
    if (error && !data && installed === true) message.error((error as Error).message)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- error/data 引用稳定于单次状态迁移
  }, [error, data, installed])

  // 拉到配置后同步表单（setFieldsValue 为 antd 命令式 API）
  useEffect(() => {
    if (data) form.setFieldsValue(data.c)
  }, [data, form])

  useTickEffect(refreshTick, () => {
    if (installed === true) load()
  })

  const save = async (): Promise<void> => {
    // 校验失败不弹 message：validateFields 有意放在 try 之外（与抽象前逐字一致）
    const v = await form.validateFields()
    setSaving(true)
    try {
      await call(() => svcApi.setConfig(v))
      message.success(texts.saved)
      load()
    } catch (e) {
      message.error((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const restart = async (): Promise<void> => {
    try {
      await call(svcApi.restart)
      message.success(texts.restarted)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const start = async (): Promise<void> => {
    try {
      await call(svcApi.start)
      message.success(texts.started)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const stop = async (): Promise<void> => {
    try {
      await call(svcApi.stop)
      message.success(texts.stopped)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  const restoreDefault = async (): Promise<void> => {
    try {
      const def = await call(svcApi.restoreDefault)
      message.success(texts.restored)
      form.setFieldsValue(def)
      load()
    } catch (e) {
      message.error((e as Error).message)
    }
  }

  return {
    form,
    installed,
    config,
    service,
    loading,
    saving,
    load,
    save,
    restoreDefault,
    restart,
    start,
    stop,
  }
}
