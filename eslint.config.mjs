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
      // === 规则降级留档（E2：ESLint 引入）===
      // react-hooks v7 的 set-state-in-effect 源自 React Compiler 规则集，要求把
      // 「挂载即发起请求并 setState」改写为事件驱动 / use() 渲染模式。本仓库存在 26 处
      // useEffect(() => { load() }, []) 统一惯例，逐条改写等价于组件重构，
      // 明确超出 E2 的 nonGoals（不做组件抽象/重构），故关闭该规则而非制造抑制注释噪音。
      // 后续统一数据获取模式（react-query/SWR 或 Compiler 兼容写法）已登记为修复路线。
      // 其余 react-hooks 规则（rules-of-hooks / exhaustive-deps / immutability 等）保持开启，
      // 且本次已按「行为不变」原则修复其全部命中。
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
