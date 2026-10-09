/** @type {import('tailwindcss').Config} */
module.exports = {
  // 暗色双轨接线：由默认的 'media'（跟随操作系统）改为 'class'（跟随应用主题）。
  // App.tsx 已把 .dark class 落到 <html>，其真源是 appStore.state.theme。
  // 注意后果：本次改动前，仓库内唯一的 dark: 变体（Users.tsx 的 dark:bg-blue-900/20）
  // 跟随的是操作系统；此后它与其余 UI 一起跟随应用内主题设置——这正是期望行为。
  darkMode: 'class',
  content: ['./src/**/*.{ts,tsx,html}'],
  theme: {
    extend: {
      // R-7：仅保留被实际引用的 token（类名扫描证据见 docs/audit/00-backlog.md §3.5）
      colors: {
        primary: '#7EC8F0',
        mist: '#F4FAFD',
        ink: '#3A4A5C',
        fog: '#8AA0B0',
        // 暗色轨道配色（配合 dark: 变体使用）。语义与上面四个 token 一一对应，
        // 供无法用 CSS 变量表达 alpha 的场合（如 text-ink/70）在暗色下换值。
        'night-ink': '#C9D6E2',
        'night-fog': '#8FA3B5',
        'night-soft': '#18202A',
      },
      borderRadius: {
        card: '16px',
        btn: '12px',
      },
      boxShadow: {
        card: '0 4px 16px rgba(126, 200, 240, 0.12), 0 1px 3px rgba(58, 74, 92, 0.04)',
      },
    },
  },
  plugins: [],
}
