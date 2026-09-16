// The REVIEW FIXTURE runner, and deliberately a second config.
//
// `vitest.config.ts` is the suite: `src/**/*.test.tsx`, run in CI on every
// change. This one runs `src/**/*.fixtures.tsx`, which render real components
// against mocked responses and write their markup to a directory for
// `scripts/check-party-workspace-layout.mjs` to open in a real browser. They
// assert nothing and are not part of the suite, which is why they are excluded
// from it by filename and need their own include.
//
//   PARTY_FIXTURE_DIR=/tmp/party npx vitest run --config vitest.fixtures.config.ts
//
// Like `vitest.config.ts` this file is intentionally NOT in any tsconfig
// include, so `tsc -b` never sees Vitest's bundled Vite types.
import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      '@nubarca/api-client': resolve(__dirname, './packages/api-client/src/index.ts'),
      '@nubarca/contracts': resolve(__dirname, '../packages/contracts/src/index.ts'),
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/vitest.setup.ts'],
    css: false,
    include: ['src/**/*.fixtures.tsx'],
    clearMocks: true,
  },
});
