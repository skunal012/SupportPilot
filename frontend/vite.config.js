import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The React dev server runs on http://localhost:5173, the .NET API on :5254.
// A browser fetch across those ports is cross-origin and would need CORS.
// Instead we let Vite proxy the API paths: the browser calls /chat, /ingest,
// /search on its OWN origin, and Vite forwards them to the backend. No CORS,
// no backend changes. (In production the app is served behind one origin,
// so this dev-only concern disappears.)
// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/chat': 'http://localhost:5254',
      '/ingest': 'http://localhost:5254',
      '/search': 'http://localhost:5254',
    },
  },
})
