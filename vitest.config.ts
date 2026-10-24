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
          // 不用 retry 兜底：retry 会把"首次失败、重试通过"的用例记为通过，
          // 反而可能掩盖新引入的间歇性回归。原先这里挂着 retry:1 用来吸收压测用例的
          // 负载敏感抖动，而该抖动已在其自身文件里根治（见 powershellPool.stress.test.ts
          // 按命令是否卡死分别给超时值），故此处的宽容已无必要。
          // 统一超时基线：默认 5s 在覆盖率插桩 + 全量并发下会被重型 antd+jsdom 用例偶发击穿，
          // 且超时文件随负载漂移（FirstRunGuide / JournalDrawer / ProtocolSettingsPanels 均出现过），
          // 逐文件补齐无法覆盖未来新增的重型测试。此处**只放宽超时阈值以消除基建抖动**，
          // 不代表修好了某个慢测试，也不是性能改进；用例与断言一律未动。
          testTimeout: 30_000,
          // 与 dom 组的 maxWorkers 不同，故必须有各自的 sequence.groupOrder（vitest 4 要求）。
          // node 组排在前面：它是纯逻辑用例，出结果快，先跑完能更快暴露逻辑层回归。
          sequence: { groupOrder: 1 },
        },
      },
      {
        test: {
          name: 'dom',
          environment: 'jsdom',
          setupFiles: ['./src/test/setup.ts'],
          include: ['src/**/*.test.tsx'],
          // 同上：组件测试是重型 antd+jsdom 渲染用例，统一 30s 基线消除负载敏感假红
          // （放宽阈值，不改任何用例与断言）
          testTimeout: 30_000,
          // 并发上限：antd + jsdom 的重型渲染按 CPU 数全并发会**超订**——
          // 16 逻辑核 / 8 物理核的机器上 27 个 DOM 文件并行，每个 worker 只分到约半个物理核，
          // 异步等待预算（RTL asyncUtilTimeout 10s / testTimeout 30s）因此偶发击穿，
          // 表现为「随负载漂移的间歇红」（历史上已登记为负载敏感断言抖动）。
          // 这里只限制 DOM 组的并发度：node 组（纯逻辑、轻量）保持全并行。
          // **不改任何用例、断言与等待时长**，只是不让并发度超过机器真正能同时服务的渲染负载。
          // CI（windows-latest 4 核）上默认并发本就是 3~4，本设置对其无影响。
          maxWorkers: 4,
          // 与 node 组的 maxWorkers 不同，故必须有各自的 sequence.groupOrder（vitest 4 要求）。
          sequence: { groupOrder: 2 },
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
      // 阈值=实测水位取整，作为只升不降的棘轮基线。
      // 历史：2026-06 取 32.83/19.85/30.79/25.66 → 32/19/30/25；
      // 2026-10 随渲染层组件测试（dom 组）补齐，实测升到 46.93/32.14/44.70/43.58
      // → 按同一"取整"规则上调为 46/32/44/43（各项留约 0.1~0.9 个百分点余量）。
      // services/share|smb|user|ftp|webdav|nfs|preset、ipc/index、preload 为零覆盖（逻辑已由
      // adapters/pool/powershell 测试覆盖，其余为薄封装），补测后应再上调本值并更新此注释。
      thresholds: {
        lines: 46,
        statements: 44,
        functions: 32,
        branches: 43,
      },
    },
  },
})
