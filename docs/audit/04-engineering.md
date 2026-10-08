# 工程化与质量门禁审计报告 (审计 A4)

- 范围：package.json、pnpm-workspace.yaml、tsconfig*.json、electron.vite.config.ts、electron-builder.yml、.npmrc、.github/workflows/*、vitest.config.ts、scripts/、docs/*、.gitignore
- 审计方式：逐项实际执行取证（node_modules/.bin 直接调用 tsc/vitest/electron-vite）+ 文件静态核对
- 审计员：audit-eng（环境配额中断，由 Captain 接管完成）

## 1. 基线实测

| 检查项 | 命令 | 结果 |
|---|---|---|
| 类型检查（node 配置） | `node_modules/.bin/tsc -p tsconfig.node.json` | ✅ exit 0 |
| 类型检查（web 配置） | `node_modules/.bin/tsc -p tsconfig.json` | ✅ exit 0 |
| 单元测试 | `node_modules/.bin/vitest run` | ✅ 10 文件 / 191 用例全部通过 |
| pnpm 脚本 | `pnpm run typecheck` / `test` / `build` | ❪ 修复前全部报 `packages field missing or empty`（已由 E1 修复，pnpm 脚本恢复可用） |

## 2. 发现清单（含确切修复步骤）

| ID | 严重级 | 标题 | 现状 | 修复步骤（命令级） |
|---|---|---|---|---|
| E-1 | 阻断(已修) | `pnpm-workspace.yaml` 与 pnpm 9.12 不兼容 | 文件缺 `packages` 字段、含非法键 `allowBuilds`，导致全部 pnpm 脚本不可用 | ✅ 已由 E1 修复：删除该文件，`onlyBuiltDependencies` 迁至 package.json `pnpm` 字段，补 `packageManager`/`engines` |
| E-2 | 高 | 本地与 CI 工具链版本漂移 | 本地 pnpm 9.12 / Node 24；CI `release.yml` 用 `pnpm/action-setup@v4 version:11` + `node-version:22`；lockfile v9.0 由 pnpm 9 生成 | 二选一：(a) 本地升级到 pnpm 11 + Node 22 与 CI 对齐（推荐，CI 用 `--no-frozen-lockfile` 会重算 lockfile，但保持单一真相源）；(b) 将 CI 降为 `pnpm/action-setup@v4 version:9` + `node-version:22` 并改用 `--frozen-lockfile`。建议选 (a) 并在 lockfile 提交时即用 pnpm 11 重生成 v9 lockfile |
| E-3 | 高 | 无 ESLint/Prettier，无 lint 脚本 | src/electron 无静态检查门禁 | 引入 flat config eslint（typescript-eslint + react-hooks）+ prettier；加 `lint`/`format`/`format:check` 脚本；全库 lint 归零（见 E2 任务） |
| E-4 | 高 | CI 无 test/lint 门禁 | 仅 `release.yml`（push tag 触发），PR/推送不跑 typecheck/lint/test | 新增 `ci.yml`：push/PR 触发，步骤 `pnpm install --frozen-lockfile → typecheck → lint → test`（见 E3 任务） |
| E-5 | 中 | 测试仅覆盖 electron，src 零测试、无覆盖率 | vitest include 仅 `electron/**/*.test.ts`，无 coverage 配置 | 引入 `@vitest/coverage-v8`；新增 `test:coverage` 脚本 + 阈值；补 src 首批单测（password/uiStore）（见 E4 任务） |
| E-6 | 中 | `tsconfig.node.json` 用 `exclude: ["electron/**/*.test.ts"]` 使测试代码脱离 typecheck | 191 个测试文件不被 `pnpm typecheck` 覆盖，类型漂移风险 | 新增独立 `tsconfig.test.json`（含 electron 测试 + vitest 类型），或调整现有 exclude 策略；明确"测试也纳入类型检查" |
| E-7 | 低 | tsconfig 未开 `noUnusedLocals`/`noUnusedParameters`/`noUncheckedIndexedAccess`/`exactOptionalPropertyTypes` | strict 已开但可进一步收紧 | 评估引入成本：先开 `noUnusedLocals`/`noUnusedParameters`（收益高、误报低）；`exactOptionalPropertyTypes` 改动面大，暂缓 |
| E-8 | 低 | `.gitignore` 未忽略审计产物与团队目录 | `.agent-teams/`、`docs/audit/` 会误入版本库 | 在 `.gitignore` 增加 `.agent-teams/`、`docs/audit/`（或仅 `.agent-teams/`） |
| E-9 | 低 | `scripts/` 目录为空 | 无用途 | 删除空目录，或用于放置 `check-workflows`/本地工具脚本 |
| E-10 | 低 | `dev` 脚本 `chcp 65001 >nul 2>&1 & electron-vite dev` 用单 `&`（cmd 下为"先后执行"而非后台） | 在 PowerShell/部分终端下行为不一致 | 改为 `chcp 65001 >nul 2>&1 && electron-vite dev`（逻辑与"先设编码再启动"一致）；实测验证 |
| E-11 | 低 | `release.yml` 安装步骤用 `--no-frozen-lockfile` | 发布时 lockfile 可能漂移，非可重现构建 | 同 E-2，统一工具链后改回 `--frozen-lockfile`（CI 与本地一致时） |

## 3. 文档一致性抽查

| 文档 | 抽查结论 |
|---|---|
| README.md | 命令/脚本名需与 package.json 对齐（audit-eng 建议补一条 README 校验：脚本名与 package.json scripts 一致） |
| DEVELOPMENT.md / UI_DESIGN.md / CHANGELOG.md | 存在且内容详实；CHANGELOG 记录 v1.0.0 多协议适配器 + 安全加固，与提交历史一致 ✅ |

## 4. 已验证通过项（Positive Findings）

- 类型检查双配置均通过（node/web 分离合理）。
- 191 个既有测试全部通过，覆盖 PowerShell 执行层、进程池、协议适配器、注入防护分支。
- `.npmrc` 镜像配置合理（npmmirror electron/electron-builder 镜像），本地安装无外网依赖。
- `electron-builder.yml` 配置完整（appId、nsis、portable、requestedExecutionLevel=requireAdministrator、icon）。
- electron-vite 三入口（main/preload/renderer）配置正确，external electron。
- 单实例锁定与托盘逻辑（main.ts）完整。

## 5. 探针覆盖矩阵

| 项 | 已探针 | 方式 |
|---|---|---|
| package.json / pnpm-workspace.yaml | ✅ | 静态 + 实测（E1 已验证修复） |
| tsconfig.node.json / tsconfig.json | ✅ | 实测 tsc 通过 |
| vitest.config.ts | ✅ | 实测 vitest 通过 |
| electron.vite.config.ts / electron-builder.yml | ✅ | 静态 |
| .npmrc / .gitignore | ✅ | 静态 |
| .github/workflows/release.yml | ✅ | 静态（发现 E-2 版本漂移） |
| scripts/ | ✅ | 目录为空（已清理审计残留） |
| docs/README/DEVELOPMENT/UI_DESIGN/CHANGELOG | ✅ | 静态抽查 |
