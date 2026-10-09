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
      // === 规则降级留档（E2 引入；B1 迁移后状态更新 2026-10-09）===
      // react-hooks v7 编译器风格规则集。set-state-in-effect 违例已从 26 处收敛至 10 处：
      // 5 页面 + 3 协议面板完成 react-query 迁移、命令面板改"渲染期调整 state"、Shares Ctrl+N 去镜像态。
      // 剩余 10 处全部位于 modal/drawer 层（GroupManageModal/PermissionDrawer/PermissionMatrix/
      // 三 PermPanel/PresetEditor/ShareDetailDrawer/UserCreateModal/UserDetailDrawer），
      // 与 R-5 组件抽象同属一个后续工作包（需 UI 运行时回归配合）。全部清零后本规则复启为 error。
      // 其余 react-hooks 规则（rules-of-hooks / exhaustive-deps / immutability 等）保持开启。
      'react-hooks/set-state-in-effect': 'off',
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
