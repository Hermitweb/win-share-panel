import type { ProtocolAdapter } from './ProtocolAdapter'
import type {
  Protocol,
  Share,
  SharePermission,
  ProtocolSession,
  ProtocolCapabilities,
  CreateShareInput,
  UpdateShareInput
} from '../../types'
import { Errors } from '../../lib/errors'
import { smbAdapter } from './adapters/smbAdapter'
import { nfsAdapter } from './adapters/nfsAdapter'
import { ftpAdapter } from './adapters/ftpAdapter'
import { webdavAdapter } from './adapters/webdavAdapter'

// adapter 注册表
const adapters: Partial<Record<Protocol, ProtocolAdapter>> = {
  smb: smbAdapter,
  nfs: nfsAdapter,
  ftp: ftpAdapter,
  webdav: webdavAdapter
}

export function getAdapter(protocol: Protocol): ProtocolAdapter {
  const adapter = adapters[protocol]
  if (!adapter) throw Errors.invalidParam(`协议 ${protocol} 暂未实现`)
  return adapter
}

// 列出所有已注册的协议
export function getRegisteredProtocols(): Protocol[] {
  return Object.keys(adapters).filter((k) => adapters[k as Protocol]) as Protocol[]
}

// 获取所有协议能力位
export function getCapabilitiesMap(): Record<Protocol, ProtocolCapabilities | null> {
  const result = {} as Record<Protocol, ProtocolCapabilities | null>
  for (const p of ['smb', 'nfs', 'ftp', 'webdav'] as Protocol[]) {
    result[p] = adapters[p]?.capabilities ?? null
  }
  return result
}

// 统一路由：列出共享（不传 protocol = 合并所有已注册协议）
// 注：协议未装/查询失败时优雅返回空数组，由 ProtocolCapabilityBanner 引导安装，不向 UI 抛错
export async function adapterList(protocol?: Protocol): Promise<Share[]> {
  console.time('[perf] adapterList')
  if (protocol) {
    try {
      const result = await getAdapter(protocol).listShares()
      console.timeEnd('[perf] adapterList')
      return result
    } catch {
      console.timeEnd('[perf] adapterList')
      return []
    }
  }
  // 合并所有协议：并行查询，避免 4 个 PowerShell 进程串行启动造成 2-4s 延迟
  const protos = getRegisteredProtocols()
  console.log(`[perf] adapterList 并行查询 ${protos.length} 个协议: ${protos.join(', ')}`)
  const results = await Promise.allSettled(protos.map((p) => getAdapter(p).listShares()))
  const all: Share[] = []
  for (const r of results) {
    if (r.status === 'fulfilled') all.push(...r.value)
  }
  console.timeEnd('[perf] adapterList')
  return all
}

export async function adapterCreate(input: CreateShareInput): Promise<Share> {
  return getAdapter(input.protocol).createShare(input)
}

export async function adapterUpdate(name: string, input: UpdateShareInput): Promise<Share> {
  const adapter = getAdapter(input.protocol)
  if (!adapter.updateShare) throw Errors.invalidParam(`${input.protocol} 协议不支持更新`)
  return adapter.updateShare(name, input)
}

export async function adapterDelete(protocol: Protocol, name: string): Promise<void> {
  return getAdapter(protocol).deleteShare(name)
}

export async function adapterToggle(
  protocol: Protocol,
  name: string,
  enabled: boolean
): Promise<void> {
  const adapter = getAdapter(protocol)
  if (!adapter.toggleShare) throw Errors.invalidParam(`${protocol} 协议不支持启停`)
  return adapter.toggleShare(name, enabled)
}

export async function adapterGetPermissions(
  protocol: Protocol,
  name: string
): Promise<SharePermission[]> {
  const adapter = getAdapter(protocol)
  if (!adapter.getPermissions) throw Errors.invalidParam(`${protocol} 协议不支持权限管理`)
  try {
    return await adapter.getPermissions(name)
  } catch {
    // 协议未装或查询失败：返回空，避免打开权限抽屉时弹错误
    return []
  }
}

export async function adapterSetPermissions(
  protocol: Protocol,
  name: string,
  perms: SharePermission[]
): Promise<void> {
  const adapter = getAdapter(protocol)
  if (!adapter.setPermissions) throw Errors.invalidParam(`${protocol} 协议不支持权限管理`)
  return adapter.setPermissions(name, perms)
}

export async function adapterSessions(protocol: Protocol): Promise<ProtocolSession[]> {
  const adapter = getAdapter(protocol)
  if (!adapter.listSessions) throw Errors.invalidParam(`${protocol} 协议不支持会话监控`)
  try {
    return await adapter.listSessions()
  } catch {
    // 协议未装或查询失败：返回空，避免切到会话 Tab 时弹错误
    return []
  }
}

export async function adapterCloseSession(
  protocol: Protocol,
  sessionId: string
): Promise<void> {
  const adapter = getAdapter(protocol)
  if (!adapter.closeSession) throw Errors.invalidParam(`${protocol} 协议不支持关闭会话`)
  return adapter.closeSession(sessionId)
}
