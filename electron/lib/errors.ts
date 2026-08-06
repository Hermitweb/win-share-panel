export type ErrorCategory = 'network' | 'permission' | 'param' | 'system' | 'unknown'

export class AppError extends Error {
  code: string
  category: ErrorCategory
  constructor(code: string, message: string, category: ErrorCategory = 'unknown') {
    super(message)
    this.code = code
    this.category = category
    this.name = 'AppError'
  }
}

export const Errors = {
  notAdmin: () => new AppError('NOT_ADMIN', '需要管理员权限才能执行此操作', 'permission'),
  invalidParam: (msg: string) => new AppError('INVALID_PARAM', msg, 'param'),
  shareNotFound: (name: string) => new AppError('SHARE_NOT_FOUND', `共享 ${name} 不存在`, 'param'),
  commandFailed: (msg: string) => new AppError('COMMAND_FAILED', msg, 'system'),
  presetNotFound: (id: string) => new AppError('PRESET_NOT_FOUND', `预设模板 ${id} 不存在`, 'param'),
  builtinProtected: () => new AppError('BUILTIN_PROTECTED', '内置模板不可删除', 'param')
}
