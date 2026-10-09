import js from '@eslint/js'
import tseslint from 'typescript-eslint'
import react from 'eslint-plugin-react'
import reactHooks from 'eslint-plugin-react-hooks'
import globals from 'globals'

export default tseslint.config(
  {
    ignores: [
      'out/**',
      'release/**',
      'dist/**',
      'node_modules/**',
      '.agent-teams/**',
      'docs/**',
      'coverage/**',
      'scripts/**',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      parserOptions: {
        ecmaFeatures: { jsx: true },
      },
    },
    plugins: {
      react,
      'react-hooks': reactHooks,
    },
    settings: {
      react: { version: 'detect' },
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react/react-in-jsx-scope': 'off',
      'react/prop-types': 'off',
      '@typescript-eslint/no-unused-vars': [
        'warn',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' },
      ],
      '@typescript-eslint/no-explicit-any': 'off',
      '@typescript-eslint/no-empty-object-type': 'off',
      '@typescript-eslint/no-unused-expressions': 'off',
      // === 规则复启留档（E2 引入降级；B1 迁移；批 2 renderer-debt 清零后复启为 error）===
      // react-hooks v7 编译器风格规则集。set-state-in-effect 违例**已全量清零**（14 处 → 0）：
      // - 11 处 modal/drawer 层：DiagnoseModal / FirstRunGuide / GroupManageModal / JournalDrawer /
      //   PermissionDrawer / PermissionMatrix / PresetEditor / ShareDetailDrawer / UserCreateModal /
      //   UserDetailDrawer / pages/Settings（派生态）。统一改为「渲染期调整 state」
      //   （useResetOnOpen / useResetOnKeyChange）+「effect 内定义 loader」的 async 安全形态；
      // - 3 处 PermPanel：NfsPermPanel / FtpPermPanel / WebdavPermPanel，随 R-5 抽象
      //   收敛进 src/hooks/useProtocolPermissions.ts。
      // 数字勘误：旧留档写的「剩余 10 处」只统计 B1 的 26→10 收敛，未含批 1 新增代码引入的 4 处
      // （DiagnoseModal:158 / FirstRunGuide:52 / JournalDrawer:127 / pages/Settings:1132）；起点实测为 14 处。
      // 复启为 error 后，任何回退（在 effect 内重新同步 setState、用 setTimeout/queueMicrotask 藏 setState、
      // 把 state 搬进 ref 静音、加本规则的行内 disable、或把规则改回 'off'）都会被 `pnpm lint`
      // （eslint . --max-warnings=0）与 CI 门禁直接挡住；该规则 suppressions 当前为 0。
      // 其余 react-hooks 规则（rules-of-hooks / exhaustive-deps / immutability 等）保持开启。
      'react-hooks/set-state-in-effect': 'error',
      // no-control-regex 保持开启。本仓库两处「显式排除控制字符」的校验正则（validatePath 与其
      // 测试镜像）为合法用法：配置级 options allowEscape 在 eslint 9.39.4 flat rules 校验下报
      // 'Key "no-control-regex":' 错误（疑似上游缺陷），故按文档改用行内 disable 并附理由。
      'no-control-regex': 'error',
    },
  },
  {
    // 根目录 CommonJS 配置文件（postcss.config.js / tailwind.config.js）：module/require 是 CJS 运行时内置标识
    files: ['**/*.js'],
    languageOptions: {
      sourceType: 'commonjs',
      globals: {
        ...globals.node,
      },
    },
  },
  {
    // 进程池测试将 this 别名到 self（worker 句柄）为刻意设计，关闭 no-this-alias
    files: ['**/*.test.ts'],
    rules: {
      '@typescript-eslint/no-this-alias': 'off',
    },
  },
)
