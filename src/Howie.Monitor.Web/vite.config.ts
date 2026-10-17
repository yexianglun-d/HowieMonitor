import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  build: { outDir: '../Howie.Monitor.Server/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://localhost:5080' } },
})
