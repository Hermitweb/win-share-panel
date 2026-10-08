# 主进程审计报告 (审计 A1)

- 范围：electron/main.ts、preload.ts、ipc/index.ts、lib/(powershell、powershellPool、audit、errors)、services/*、services/protocol/*（registry/adapters/ProtocolAdapter/detect）
- 审计方式：逐文件通读 + 既有测试实证（powershellPool.test / powershell.test / *Adapter.test）
- 审计员：audit-main（环境配额中断，由 Captain 接管完成）

## 1. 结论

主进程架构清晰、分层合理（IPC → services → protocol 适配器 → PowerShell 执行层）。**未发现运行安全/逻辑级阻断性缺陷**。代码在并发安全、事务补偿、错误恢复、生命周期管理上做得相当到位（见"已验证通过项"）。以下为可优化项，均为中低优先级。

## 2. 发现清单

| ID | 严重级 | 标题 | 位置 | 建议 |
|---|---|---|---|---|
| M-1 | 中→已复核关闭 | `nfs.setConfig` 静默丢弃 `gatewayCharacterSet`/`protocolVersion` 字段 | electron/services/nfs.ts setConfig | 【复核结论 2026-06】UI 实况：NfsSettingsPanel 已将两字段置于"身份映射 / 网关信息（只读）"分区且 `<Input disabled>`，与 types.ts"只读"声明一致；`Set-NfsServerConfiguration` 不接受这两参数。故 setConfig 跳过为正确语义而非缺陷，已在 nfs.ts 补注释防误修。原判"需与渲染层确认"已闭环 |
| M-2 | 低 | `services/smb.ts` 写前快照串行锁通过 `setConfigChain` Promise 链实现，rollback 路径 `skipNextSnapshot=true` 的 finally 保证复位，但 `restoreDefault` 也会进入该链，语义正确但注释易误读 | electron/services/smb.ts:169-196 | 维持现状，补充一条注释说明 restoreDefault 同样会被快照 |
| M-3 | 低 | `powershellPool` 的 `onStdoutData` 在 worker 存活但无在飞命令时（预热期杂散输出）直接 return，未处理 OK/ERR 残留标记可能错配 | electron/lib/powershellPool.ts:274-278 | 极端情况下杂散输出含 `___PS_OK_...___` 串会误判；建议在 `!worker.current` 分支也清空 buffer 或仅保留有限窗口 |
| M-4 | 低 | `buildServerScript` 的 marker 使用 `Math.random` 生成 8 位 hex，非加密随机；同进程内多池实例共享随机空间但 token 碰撞概率极低 | electron/lib/powershellPool.ts:68-70 | 可由 `crypto.randomBytes` 替代以更符合"不可预测"直觉；非安全阻断 |
| M-5 | 低 | `execOnce` 回退路径未复用进程池的 UTF8/ProgressPreference 前缀逻辑，但已内联 `UTF8_PREFIX`，两路径行为一致 | electron/lib/powershell.ts:14-15,32-42 | 维持现状 |
| M-6 | 中 | `share.toggleShare` 恢复路径调用 `createShare`，若 `importConfig` 触发 `createShare` 且 path 校验失败会抛 `invalidParam`，错误信息未区分"恢复"场景 | electron/services/share.ts:202-261 | 建议包装为更友好的"恢复共享失败"提示 |
| M-7 | 低 | 所有 PowerShell 调用均经 `runPowerShell*`，但 `system.ts:123` `Get-Command Get-SmbShare` 硬编码命令，未来若重命名 cmdlet 需多处改动；属可维护性 | electron/services/system.ts | 维持现状 |

## 3. 已验证通过项（Positive Findings）

- 进程池：生命周期（spawn/dispatch/assign/prewarm/shutdown）、超时中毒 kill + 补 spawn、崩溃 reject + 重试、in-flight 去重均有对应单测覆盖（`powershellPool.test.ts`、`powershellPool.stress.test.ts`，共 3+ 压力场景通过）。
- 事务补偿：SMB/WebDAV/FTP/NFS 权限设置均有"备份→清空→逐项授予→失败回滚"逻辑（含日志取证），鲁棒性高。
- 命令注入防护：所有用户输入经 `psQuote`/`psBool`/`psNumber`/`psEnum`（详见 03-security.md，结论：无可用注入漏洞）。
- 系统保护：删除/重命名内置账号与组（`administrator/guest/...`、Administrators/Users/...）有白名单拦截；删除系统特殊共享（`admin$/c$/...`）有拦截。
- 快照回滚：`rollbackSnapshot` 对 `id` 做 `^[A-Za-z0-9_-]+$` 路径穿越防护，且不覆盖真实共享写。
- 单实例锁 + 退出时关闭进程池（before-quit / will-quit 双保险），无 powershell.exe 残留。

## 4. 探针覆盖矩阵

| 文件/模块 | 已探针 | 方式 |
|---|---|---|
| electron/lib/powershell.ts | ✅ | 静态 + 测试实证 |
| electron/lib/powershellPool.ts | ✅ | 静态 + 压力测试实证 |
| electron/lib/audit.ts | ✅ | 静态（rotation 逻辑清晰） |
| electron/lib/errors.ts | ✅ | 静态 |
| electron/ipc/index.ts | ✅ | 静态 |
| electron/main.ts / preload.ts | ✅ | 静态（加固齐备） |
| electron/services/*.ts（share/smb/user/ftp/webdav/nfs/session/system/preset） | ✅ | 逐文件静态 |
| electron/services/protocol/{registry,detect,ProtocolAdapter}.ts | ✅ | 静态 |
| electron/services/protocol/adapters/*.ts | ✅ | 逐文件静态 + adapter 测试实证 |
| 端到端 powershell 实时探针 | ❌ | 沙箱无法稳定 spawn 交互式 powershell（环境限制） |
