import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5298', changeOrigin: true },
      '/card-art': { target: 'http://localhost:5298', changeOrigin: true },
      '/card-fonts': { target: 'http://localhost:5298', changeOrigin: true },
    },
  },
  build: { outDir: 'dist', emptyOutDir: true },
})
