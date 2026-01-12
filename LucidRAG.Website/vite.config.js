import { defineConfig } from 'vite'

export default defineConfig({
  root: 'src',
  base: '/',
  publicDir: false,
  build: {
    outDir: '../dist',
    emptyOutDir: true
  },
  server: {
    port: 3000,
    open: true
  }
})
