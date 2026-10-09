import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    globals: false,
    // vitest 4 projects：node 组=主进程+渲染层纯逻辑；dom 组=React 组件测试（B3）
    // setupFiles 需逐 project 声明（vitest 4 顶层不向 projects 继承）
    projects: [
      {
        test: {
          name: 'node',
          environment: 'node',
          setupFiles: ['./src/test/setup.ts'],
          include: ['electron/**/*.test.ts', 'src/**/*.test.ts'],
        },
      },
      {
        test: {
          name: 'dom',
          environment: 'jsdom',
          setupFiles: ['./src/test/setup.ts'],
          include: ['src/**/*.test.tsx'],
        },
      },
    ],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
      reportsDirectory: 'coverage',
      // 覆盖范围如实限定为"当前可单测的纯逻辑面"：
      // React 组件（依赖 DOM/jsdom）不在其中，待引入组件测试基建后扩列。
      include: ['electron/**/*.ts', 'src/utils/**/*.ts', 'src/stores/**/*.ts', 'src/api.ts'],
      exclude: [
        'electron/**/*.test.ts',
        'electron/types.ts', // 纯类型声明，无运行时语句
        'src/**/*.test.ts',
        'src/**/*.test.tsx',
        'src/test/**',
        'src/types.ts', // 纯 re-export
        'src/vite-env.d.ts',
      ],
      // 阈值=2026-06 实测水位取整（32.83/19.85/30.79/25.66），作为只升不降的棘轮基线；
      // services/share|smb|user|ftp|webdav|nfs|preset、ipc/index、preload 为零覆盖（逻辑已由
      // adapters/pool/powershell 测试覆盖，其余为薄封装），补测后应上调本值并更新此注释。
      thresholds: {
        lines: 32,
        statements: 30,
        functions: 19,
        branches: 25,
      },
    },
  },
})
