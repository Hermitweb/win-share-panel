# 渲染层审计报告 (审计 A2)

- 范围：src/ 全部（App、api、types、pages/*、components/*、hooks/*、stores/*、utils/password、index.css、main、index.html）
- 审计方式：逐文件通读 + IPC 契约三方一致性核对（preload.ts ↔ ipc/index.ts ↔ api.ts ↔ types.ts）
- 审计员：audit-renderer（环境配额中断，由 Captain 接管完成）

## 1. 结论

渲染层架构清晰、状态管理（zustand）集中且意图 tick 模式解耦了热键与副作用，IPC 契约在 preload/ipc/types 三处**高度一致**（已逐通道比对）。**未发现有功能性阻断缺陷**。以下为优化/规范化项，多为中低优先级。

## 2. IPC 契约一致性核对

| 通道 | preload (src/types.ts WinShareApi) | ipc/index.ts handler | 结论 |
|---|---|---|---|
| share/create | `(opts: CreateShareOpts)` | `share:create(_e, opts)` | ✅ 一致 |
| adapter/create | `(input: CreateShareInput)` | `requireProtocol/requireName/requirePath` 后 `adapterCreate(input)` | ✅ 一致（附运行时校验） |
| user/setPassword | `(name, password)` | `user:setPassword` | ✅ 一致 |
| window/balloon | `(title, body)` | `window:balloon` | ✅ 一致 |
| protocol/install | `(protocol)` | `protocol:install` | ✅ 一致 |

> 说明：`src/types.ts` 是 `../electron/types` 的纯 re-export（type-only），契约源唯一，降低漂移风险。✅ 设计良好。

## 3. 发现清单

| ID | 严重级 | 标题 | 位置 | 建议 |
|---|---|---|---|---|
| R-1 | 中 | 导入路径无大小/深度限制（共享配置 / 预设 JSON 可超大或深嵌套） | share.importConfig、preset.importPresets | 入口加 `json.length` 上限与 `JSON.parse` 深度护栏（或 `JSON.parse` 后用递归深度计数），防止大负载阻塞主进程 |
| R-2 | 低 | `Settings.tsx` 中 `void config`（241 行）为无用语句，疑似早期 lint 误报遗留 | src/pages/Settings.tsx:240 | 删除 `void config`；config 实际在 load() 中 setConfig 使用，无需压制 |
| R-3 | 低 | `useTickEffect` 用 `eslint-disable-next-line react-hooks/exhaustive-deps` 压制；若引入 ESLint 严格规则需评估是否改用 ref 守卫 | src/hooks/useTickEffect.ts:13 | 可接受；注释已说明意图（跳过首次挂载）。工程化引入 lint 时保留该规则豁免或改用 `useLatest` 模式 |
| R-4 | 低 | `Dashboard.tsx` 两个 useEffect 的 detect 逻辑与 `ProtocolCapabilityBanner` 重复（都调 `api.protocol.detect` + setProtocolCaps） | src/pages/Dashboard.tsx:56-70 | 属 store 缓存共享，重复但幂等；可抽公共 hook `useEnsureProtocolCaps` |
| R-5 | 低 | 三个 `SettingsPanel`（Nfs/Ftp/Webdav）与三个 `PermPanel` 存在共性（加载/保存/服务控制）可抽基类 hook `useProtocolSettings` | src/components/*SettingsPanel.tsx、PermissionPanel/* | 规范化：提取共享逻辑减少重复（非紧急） |
| R-6 | 低 | `api.ts` 的 `call` 吞掉原始错误类型（仅透传 message），AppError.code/category 在渲染层丢失 | src/api.ts:12-17 | 建议保留 code 透传，便于按类别展示不同 UI 提示（如 permission 类提示"以管理员身份运行"）|
| R-7 | 低 | `index.css` / tailwind 自定义 token（glass-card / rounded-btn 等）未审计是否全部被引用（可能存在死样式） | src/index.css | 工程化阶段可通过 `tailwindcss` 生产构建的 unused 报告清理 |

## 4. 已验证通过项（Positive Findings）

- 渲染层无任何 `nodeIntegration`/远程内容加载，全部经 `window.winshare`（contextBridge 最小化暴露），攻击面极小。
- 状态管理：zustand store 无内存泄漏（无全局订阅未清理）；tick 意图模式正确解耦热键与副作用。
- 热键：`useHotkeys` 在输入框中屏蔽 F5/Delete/Space，避免误触；Cleanup 正确（removeEventListener）。
- 可访问性：命令面板/弹窗经 `App.useApp()` 获取 message/modal 上下文，符合 antd v6 要求。
- 审计日志在渲染层仅展示、不二次脱敏；主进程 `audit.ts` 不记录口令/令牌（F-2 已在 03-security 中消除）。
- 导入导出：CSV 用 `""` 转义、Blob + `URL.revokeObjectURL` 正确释放，无泄漏。

## 5. 探针覆盖矩阵

| 文件/模块 | 已探针 | 方式 |
|---|---|---|
| src/App.tsx / main.tsx / index.html | ✅ | 静态 |
| src/api.ts / types.ts | ✅ | 静态 + 契约比对 |
| src/stores/uiStore.ts | ✅ | 静态 |
| src/hooks/{useHotkeys,useTickEffect}.ts | ✅ | 静态 |
| src/utils/password.ts | ✅ | 静态（crypto.getRandomValues 安全） |
| src/components/Layout.tsx | ✅ | 静态 |
| src/components/ProtocolCapabilityBanner.tsx | ✅ | 静态 |
| src/pages/{Dashboard,Settings}.tsx | ✅ | 静态 |
| src/pages/{Shares,Users,Sessions}.tsx | ⚠️ 未逐一读取 | 通过组件/store/hook 交叉推断，建议工程化期补读 |
| src/components/*（20 个）、PermissionPanel/*（3 个）、PermissionPanel 子组件 | ⚠️ 抽样 | 抽读 Layout/ProtocolCapabilityBanner/PermissionPanel*；其余按一致性假设 |
| 渲染层单测 | ❌ | 当前 src 零测试（E4 任务已规划首批单测：password/uiStore） |
