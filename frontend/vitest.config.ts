// Separate from `vite.config.ts` on purpose: Vitest 2.1 bundles its own copy
// of Vite 5, which has TypeScript types that don't structurally match the
// top-level Vite 7 used by the production build. Keeping the test config
// here means `tsc -b` against `vite.config.ts` does not see the two clashing
// Vite identities. This file is intentionally NOT in any tsconfig include,
// so `tsc -b` ignores it; Vitest picks it up at runtime by filename.
import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  // Mirror the production build's alias (vite.config.ts) so tests resolve the
  // extracted shared client the same way the app does.
  resolve: {
    alias: {
      '@nubarca/api-client': resolve(__dirname, './packages/api-client/src/index.ts'),
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/vitest.setup.ts'],
    css: false,
    include: ['src/**/*.test.{ts,tsx}'],
    clearMocks: true,
  },
});
