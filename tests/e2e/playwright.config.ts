import { defineConfig, devices } from '@playwright/test';
import { WEB_URL } from './src/env';

// NubArca browser verification.
//
// The matrix is three engines across two form factors, plus explicit 200%-zoom
// projects. Zoom is modelled the way a browser actually applies it: at 200% the
// CSS-pixel viewport halves while the device pixel ratio doubles. Emulating it
// that way exercises the real failure mode — a layout that only fits when the
// viewport is wide in CSS pixels — which a screenshot at 1x would never catch.
//
// Zoom projects are restricted to the layout-sensitive surfaces named in the
// verification requirements (media library and browser TV) rather than the whole
// suite: running every spec six more times buys nothing and triples the runtime.

const DESKTOP = { width: 1280, height: 800 };
const ZOOMED_DESKTOP = { width: DESKTOP.width / 2, height: DESKTOP.height / 2 };

const LAYOUT_SENSITIVE = /(media-library|tv-browser)\.spec\.ts/;

export default defineConfig({
  testDir: './specs',
  outputDir: './test-results',
  // Deterministic ordering: the seeded dataset is shared and read-only, so
  // parallel files are safe, but a single worker per project keeps failures
  // reproducible rather than dependent on interleaving.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [
    ['list'],
    ['html', { outputFolder: './playwright-report', open: 'never' }],
    // Outside outputDir on purpose: Playwright cleans outputDir at the start of a
    // run, which deleted the machine-readable report before it could be read.
    ['json', { outputFile: './.artifacts/results.json' }],
  ],
  use: {
    baseURL: WEB_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    // The suite drives a local stack over plain http.
    ignoreHTTPSErrors: true,
  },

  projects: [
    // ---------------------------------------------------------------- desktop
    {
      name: 'chromium-desktop',
      use: { ...devices['Desktop Chrome'], viewport: DESKTOP },
    },
    {
      name: 'firefox-desktop',
      use: { ...devices['Desktop Firefox'], viewport: DESKTOP },
    },
    {
      name: 'webkit-desktop',
      use: { ...devices['Desktop Safari'], viewport: DESKTOP },
    },

    // ----------------------------------------------------------------- mobile
    // Real device descriptors: they carry the touch, scale-factor and user-agent
    // differences that the mobile command-bar layout actually branches on.
    {
      name: 'chromium-mobile',
      use: { ...devices['Pixel 7'] },
    },
    {
      name: 'webkit-mobile',
      use: { ...devices['iPhone 14'] },
    },
    {
      // Firefox has no touch-enabled device descriptor in Playwright, so the
      // mobile Firefox project is a narrow viewport without touch emulation.
      // Stated explicitly rather than pretended: this covers responsive layout,
      // not touch behaviour.
      name: 'firefox-mobile',
      use: {
        ...devices['Desktop Firefox'],
        viewport: { width: 412, height: 915 },
        isMobile: false,
        hasTouch: false,
      },
    },

    // ------------------------------------------------------------- 200% zoom
    {
      name: 'chromium-desktop-zoom200',
      testMatch: LAYOUT_SENSITIVE,
      use: {
        ...devices['Desktop Chrome'],
        viewport: ZOOMED_DESKTOP,
        deviceScaleFactor: 2,
      },
    },
    {
      name: 'firefox-desktop-zoom200',
      testMatch: LAYOUT_SENSITIVE,
      use: { ...devices['Desktop Firefox'], viewport: ZOOMED_DESKTOP },
    },
    {
      name: 'webkit-desktop-zoom200',
      testMatch: LAYOUT_SENSITIVE,
      use: {
        ...devices['Desktop Safari'],
        viewport: ZOOMED_DESKTOP,
        deviceScaleFactor: 2,
      },
    },
  ],

  // No webServer: the ephemeral stack serves the built app and the API from one
  // origin (see docker-compose.e2e.yml and nginx.e2e.conf), which is how
  // production serves them. Driving a dev proxy instead made WebKit and Firefox
  // reject every /api call as cross-origin.
});
