/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./src/**/*.{ts,tsx,html}'],
  theme: {
    extend: {
      // R-7：仅保留被实际引用的 token（类名扫描证据见 docs/audit/00-backlog.md §3.5）
      colors: {
        primary: '#7EC8F0',
        mist: '#F4FAFD',
        ink: '#3A4A5C',
        fog: '#8AA0B0',
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
