import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The service allows exactly this origin for CORS, so the port must not drift.
export default defineConfig({
  plugins: [react()],
  resolve: {
    // @pluperfect/jwt is a file: dependency, a symlink, so React reached through it could be a second copy.
    dedupe: ['react', 'react-dom'],
  },
  optimizeDeps: {
    // Vite caches pre-bundled dependencies, so an edit in the linked sibling would otherwise not show.
    exclude: ['@pluperfect/jwt'],
  },
  server: {
    port: 6165,
    strictPort: true,
  },
})
