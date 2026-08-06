import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

// Dev proxy forwards the API + share-link endpoints to the backend so the
// browser sees a single origin (avoids CORS + lets the auth cookie ride
// every request unchanged). The backend dev port is fixed by
// src/NubArca.Api/Properties/launchSettings.json.
//
// Vitest's `test` block lives in `vitest.config.ts` so the build-time tsc
// pass on this file does not pull in vitest's bundled Vite types (which
// otherwise clash with the top-level Vite 7).
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@nubarca/api-client': resolve(__dirname, './packages/api-client/src/index.ts'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5177',
        changeOrigin: false,
      },
      '/health': {
        target: 'http://localhost:5177',
        changeOrigin: false,
      },
      // Share links are `/s/{token}` (ShareLinkEndpoints). This MUST stay a
      // regexp: a plain '/s' key is a PREFIX match, so it also captured
      // `/src/main.tsx` and every other source module, and the dev server
      // answered them with the API's 404 — local `npm run dev` could not boot
      // the app at all. `^/s/` matches only real share links.
      '^/s/': {
        target: 'http://localhost:5177',
        changeOrigin: false,
      },
    },
  },
});
