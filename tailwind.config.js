/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./src/**/*.{ts,tsx,html}'],
  theme: {
    extend: {
      colors: {
        primary: '#7EC8F0',
        'primary-light': '#A8DCF5',
        secondary: '#C8B6E2',
        accent: '#F5C6C6',
        mist: '#F4FAFD',
        ink: '#3A4A5C',
        fog: '#8AA0B0'
      },
      borderRadius: {
        card: '16px',
        btn: '12px'
      },
      backdropBlur: {
        glass: '16px'
      },
      boxShadow: {
        card: '0 4px 16px rgba(126, 200, 240, 0.12), 0 1px 3px rgba(58, 74, 92, 0.04)',
        hover: '0 8px 28px rgba(126, 200, 240, 0.20), 0 2px 6px rgba(58, 74, 92, 0.06)'
      }
    }
  },
  plugins: []
}
