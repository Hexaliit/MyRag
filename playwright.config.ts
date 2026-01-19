import { defineConfig, devices } from '@playwright/test';

/**
 * LucidRAG Playwright Configuration
 *
 * Running tests:
 *   npx playwright test                    # Run all tests (starts server automatically)
 *   npx playwright test --headed           # Run with visible browser
 *   npx playwright test --ui               # Run with Playwright UI
 *   BASE_URL=http://localhost:5020 npx playwright test  # Against running server
 *
 * From WSL against Windows .NET app:
 *   1. Start app on Windows: dotnet run --project src/LucidRAG/LucidRAG.csproj
 *   2. Run tests: npx playwright test
 */

const baseURL = process.env.BASE_URL || 'http://localhost:5020';

export default defineConfig({
  testDir: './tests',

  /* Run tests in files in parallel */
  fullyParallel: true,

  /* Fail the build on CI if you accidentally left test.only in the source code. */
  forbidOnly: !!process.env.CI,

  /* Retry on CI only */
  retries: process.env.CI ? 2 : 0,

  /* Opt out of parallel tests on CI. */
  workers: process.env.CI ? 1 : undefined,

  /* Reporter to use */
  reporter: [
    ['html', { open: 'never' }],
    ['list']
  ],

  /* Global timeout for each test */
  timeout: 60000,

  /* Expect timeout */
  expect: {
    timeout: 10000,
  },

  /* Shared settings for all the projects below */
  use: {
    /* Base URL for page.goto('') calls */
    baseURL,

    /* Collect trace when retrying the failed test */
    trace: 'on-first-retry',

    /* Take screenshot on failure */
    screenshot: 'only-on-failure',

    /* Video recording */
    video: 'retain-on-failure',

    /* Default viewport */
    viewport: { width: 1400, height: 900 },
  },

  /* Configure projects for major browsers */
  projects: [
    // Setup project for authentication
    {
      name: 'setup',
      testMatch: /.*\.setup\.ts/,
    },

    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // Use stored auth state
        storageState: 'playwright/.auth/user.json',
      },
      dependencies: ['setup'],
    },

    // Uncomment to test on more browsers
    // {
    //   name: 'firefox',
    //   use: {
    //     ...devices['Desktop Firefox'],
    //     storageState: 'playwright/.auth/user.json',
    //   },
    //   dependencies: ['setup'],
    // },
    // {
    //   name: 'webkit',
    //   use: {
    //     ...devices['Desktop Safari'],
    //     storageState: 'playwright/.auth/user.json',
    //   },
    //   dependencies: ['setup'],
    // },
  ],

  /* Run your local dev server before starting the tests */
  webServer: process.env.BASE_URL ? undefined : {
    command: 'dotnet run --project src/LucidRAG/LucidRAG.csproj -- --standalone',
    url: 'http://localhost:5020',
    reuseExistingServer: !process.env.CI,
    timeout: 120000, // .NET apps take longer to start
    stdout: 'pipe',
    stderr: 'pipe',
  },
});
