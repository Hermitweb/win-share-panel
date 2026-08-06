import type {
  Protocol,
  ProtocolCapabilities,
  Share,
  SharePermission,
  ServiceStatus,
  ProtocolSession,
  SmbOpenFile,
  CreateShareInput,
  UpdateShareInput
} from '../../types'

// 协议适配器接口：每个协议实现此接口，registry 路由调用
// 可选方法用 `?` 标记，不支持的能力不实现或抛 Errors.unsupported
export interface ProtocolAdapter {
  readonly protocol: Protocol
  readonly capabilities: ProtocolCapabilities

  // 必选：列表/创建/删除
  listShares(): Promise<Share[]>
  createShare(input: CreateShareInput): Promise<Share>
  deleteShare(name: string): Promise<void>

  // 可选：按能力实现，不支持的抛 unsupported
  updateShare?(name: string, input: UpdateShareInput): Promise<Share>
  toggleShare?(name: string, enabled: boolean): Promise<void>
  getPermissions?(name: string): Promise<SharePermission[]>
  setPermissions?(name: string, perms: SharePermission[]): Promise<void>
  listSessions?(): Promise<ProtocolSession[]>
  closeSession?(sessionId: string): Promise<void>
  listOpenFiles?(): Promise<SmbOpenFile[]>
  closeFile?(fileId: string): Promise<void>

  // 协议级配置与服务控制
  getServiceStatus?(): Promise<ServiceStatus>
  restartService?(): Promise<void>
  getConfig?(): Promise<unknown>
  setConfig?(config: unknown): Promise<void>
}
