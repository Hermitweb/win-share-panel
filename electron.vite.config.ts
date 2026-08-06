import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'path'

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    build: {
      rollupOptions: {
        input: { index: resolve(__dirname, 'electron/main.ts') },
        external: ['electron'],
        output: { format: 'cjs', entryFileNames: '[name].js' }
      }
    }
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    build: {
      rollupOptions: {
        input: { index: resolve(__dirname, 'electron/preload.ts') },
        external: ['electron'],
        output: { format: 'cjs', entryFileNames: '[name].js' }
      }
    }
  },
  renderer: {
    root: 'src',
    publicDir: resolve(__dirname, 'resources'),
    build: {
      rollupOptions: { input: { index: resolve(__dirname, 'src/index.html') } }
    },
    resolve: {
      alias: { '@': resolve(__dirname, 'src') }
    },
    plugins: [react()]
  }
})
